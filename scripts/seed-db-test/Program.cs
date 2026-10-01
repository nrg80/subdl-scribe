// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
// SubDL Scribe — database-built test.
//
// Runs WITHOUT a Jellyfin host. The parts of the seeder that write the registry
// take plain inputs (a directory, a file path, MediaStream DTOs), so they can be
// exercised directly:
//
//   A  SubdlDbContext + ContentHashRegistry construct against a temp directory
//   B  SidecarNaming.List reads loose .srt files from a path alone
//   C  SidecarNaming.EmbeddedTracks reads MediaStream DTOs
//   D  the three together = one database built, both areas in one answer
//   E  the writer/reader name contract (F-M260)
//   E2 the rename target for an unlabelled sidecar (F-M278)
//   E3 a renamed sidecar keeps its row and its verdict (F-M278)
//   F  observations never overwrite a verdict (F-M257/F-M259)
//   G  edge cases: bitmap, forced, unlabeled, unreadable
//
// What this does NOT cover: a full SubdlSeeder.Scan() pass, and therefore the rename itself —
// RenameSidecar reads Plugin.Instance for the switch and moves a file, so it needs the host. This
// suite covers the pure rule it calls (SidecarNaming.PlanTarget) and the row move that follows it
// (MoveSidecarLocation). The scan loop is covered by the live test instance, not here.
//
// Exit code 0 = all checks passed, 1 = at least one failed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Language;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Jellyfin.Plugin.SubdlScribe.Registry;
using MediaBrowser.Model.Entities;

namespace SeedDbTest;

internal static class Program
{
    private static int _failed;
    private static int _passed;

    private static void Check(string label, bool ok, string detail = "")
    {
        if (ok)
        {
            _passed++;
        }
        else
        {
            _failed++;
        }

        Console.WriteLine("  " + (ok ? "OK  " : "FAIL") + " " + label.PadRight(58) + detail);
    }

    private static void Section(string name) => Console.WriteLine("\n=== " + name + " ===");

    private static MediaStream Sub(string lang, string codec = "subrip", bool hi = false,
                                   bool forced = false, bool external = false)
        => new()
        {
            Type = MediaStreamType.Subtitle,
            Language = lang,
            Codec = codec,
            IsHearingImpaired = hi,
            IsForced = forced,
            IsExternal = external,
            Index = 1
        };

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "seed-db-test");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        Directory.CreateDirectory(root);

        string mediaDir = Path.Combine(root, "media");
        Directory.CreateDirectory(mediaDir);
        string media = Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.mkv");
        File.WriteAllBytes(media, Enumerable.Repeat((byte)0x42, 200_000).ToArray());

        Console.WriteLine("SubDL Scribe — database built test");
        Console.WriteLine("media: " + media);

        // ── A ───────────────────────────────────────────────────────────────
        Section("A) Registry constructs without a Jellyfin host");
        using var db = new SubdlDbContext(Path.Combine(root, "db"), null);
        var reg = new ContentHashRegistry(db, null, null);
        Check("SubdlDbContext + ContentHashRegistry", reg != null);

        string mediaHash = reg.GetMediaHash(media) ?? string.Empty;
        Check("media hash computed from the real file", mediaHash.Length > 0,
              mediaHash.Length > 12 ? "-> " + mediaHash[..12] + "..." : "");

        // ── C ── (before B: the tracks are needed for D) ─────────────────────
        Section("C) EmbeddedTracks reads plain MediaStream DTOs");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc" },
            Sub("eng"),                                  // plain EN track
            Sub("ger", hi: true),                        // DE with the HI flag
            Sub("fra", codec: "pgssub"),                 // bitmap — must be skipped
            Sub("spa", forced: true),                    // forced — must be skipped
            Sub("ita", external: true),                  // external — not embedded
        };

        var tracks = SidecarNaming.EmbeddedTracks(streams);
        Check("two embedded tracks survive the filters", tracks.Count == 2,
              "-> " + string.Join(",", tracks.Select(t => "pos" + t.SubPos + ":" + t.Lang + (t.HearingImpaired ? "/hi" : ""))));
        Check("bitmap codec skipped", !tracks.Any(t => t.Lang == "FR"));
        Check("forced track skipped", !tracks.Any(t => t.Lang == "ES"));
        Check("external stream skipped", !tracks.Any(t => t.Lang == "IT"));
        Check("HI flag carried through", tracks.Any(t => t.Lang == "DE" && t.HearingImpaired));

        // ── B ───────────────────────────────────────────────────────────────
        Section("B) SidecarNaming.List reads loose files from a path alone");
        File.WriteAllText(Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.en.sdh.srt"),
            "1\n00:00:01,000 --> 00:00:02,000\n[ KNOCKING ]\n");
        File.WriteAllText(Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.de.srt"),
            "1\n00:00:01,000 --> 00:00:02,000\nHallo\n");
        File.WriteAllText(Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.srt"),
            "1\n00:00:01,000 --> 00:00:02,000\nUnlabeled\n");

        var loose = SidecarNaming.List(media);
        Check("three sidecars recognized", loose.Count == 3,
              "-> " + string.Join(",", loose.Select(x => x.Lang + (x.HearingImpaired ? "/hi" : ""))));
        Check("the .sdh.srt reads as EN/HI", loose.Any(x => x.Lang == "EN" && x.HearingImpaired));
        // F-M278: the unlabelled file carries NO language in its name — that is exactly why the
        // seeder now runs detection on it and renames it. Reading it as EN was the old guess
        // (Jellyfin's convention), removed by user decision on 30.09.2026.
        Check("the unlabeled .srt carries NO language",
              loose.Any(x => x.Lang == null && !x.HearingImpaired),
              "-> " + string.Join(",", loose.Select(x => x.Lang ?? "<null>")));

        // ── B2) mark tokens: one entry per required FILE (F-M240) ────────────
        // A regular subtitle and its variant are two files on disk, so they must be two
        // entries in the mark. The failure this guards: both recorded as "DE", the regular
        // save closed the variant's slot, and the mark claimed a file that was not there.
        Section("B2) The mark names files: DE vs DE:hi (F-M240)");
        Check("Token(DE, false) is the plain language", SidecarNaming.Token("DE", false) == "DE",
              "-> " + SidecarNaming.Token("DE", false));
        Check("Token(DE, true) carries the variant marker", SidecarNaming.Token("DE", true) == "DE:hi",
              "-> " + SidecarNaming.Token("DE", true));
        Check("a plain token is not read as a variant", !SidecarNaming.IsHiToken("DE"));
        Check("a variant token is recognized", SidecarNaming.IsHiToken("DE:hi"));
        Check("the marker is case-insensitive", SidecarNaming.IsHiToken("de:HI"));
        Check("TokenLanguage(DE:hi) is DE", SidecarNaming.TokenLanguage("DE:hi") == "DE",
              "-> " + SidecarNaming.TokenLanguage("DE:hi"));
        Check("TokenLanguage(DE) is DE", SidecarNaming.TokenLanguage("DE") == "DE");

        // PresentTokens reads the DISK: the .de.srt in this fixture carries no marker, so it
        // proves the plain token and must NOT prove the variant. This is the assertion the old
        // language-keyed code could not express at all.
        var tokens = SidecarNaming.PresentTokens(media);
        Check("the .de.srt proves the DE token", tokens.Contains("DE"));
        Check("the .de.srt does NOT prove DE:hi", !tokens.Contains("DE:hi"),
              "-> [" + string.Join(",", tokens.OrderBy(x => x)) + "]");
        Check("the .en.sdh.srt proves EN:hi", tokens.Contains("EN:hi"));
        Check("the .en.sdh.srt also proves EN (a variant is still the language)", tokens.Contains("EN"));

        // The variant, once on disk, must prove its own token.
        File.WriteAllText(Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.de.sdh.srt"),
            "1\n00:00:01,000 --> 00:00:02,000\n[ TÜR ]\n");
        var tokensAfter = SidecarNaming.PresentTokens(media);
        Check("a .de.sdh.srt on disk proves DE:hi", tokensAfter.Contains("DE:hi"),
              "-> [" + string.Join(",", tokensAfter.OrderBy(x => x)) + "]");
        File.Delete(Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.de.sdh.srt"));

        // The regression that started this: the subset rule must NOT let a plain set cover a
        // variant requirement. "DE,EN" (regular only) must fail to satisfy "DE,DE:hi".
        Check("a regular-only set does not cover the variant requirement",
              !ContentHashRegistry.CoversLanguages(new[] { "DE", "EN" }, new[] { "DE", "DE:hi" }));
        Check("a set with both covers the variant requirement",
              ContentHashRegistry.CoversLanguages(new[] { "DE", "DE:hi", "EN" }, new[] { "DE", "DE:hi" }));
        Check("a superset still covers a smaller requirement",
              ContentHashRegistry.CoversLanguages(new[] { "DE", "DE:hi", "EN", "FR" }, new[] { "DE", "DE:hi" }));

        // ── D ───────────────────────────────────────────────────────────────
        Section("D) The three together = one database built");
        int embWritten = 0;
        foreach (var (pos, lang, hi) in tracks)
        {
            if (reg.ObserveEmbed(mediaHash, pos, lang, hi))
            {
                embWritten++;
            }
        }

        int sideWritten = 0;
        foreach (var (path, lang, hi) in loose)
        {
            string text = File.ReadAllText(path);
            if (reg.ObserveSidecar(ContentHashRegistry.ComputeHash(text), mediaHash, lang, hi,
                                   Path.GetFileName(path), path))
            {
                sideWritten++;
            }
        }

        // F-M278: the fixture's sidecars are TWO labelled (.en.sdh.srt, .de.srt) and ONE unlabelled
        // (.srt). The unlabelled one resolves to no language, and a row must state a language — it is
        // what the coverage check reads — so it is refused and gets no row. That is the rule the
        // seeder's own path now follows by detecting the language first (F-M278); this fixture has no
        // text-based resolution, so the file simply contributes nothing here.
        Check("embed rows written", embWritten == 2, "-> " + embWritten);
        Check("sidecar rows written (2 labelled, the unlabelled one cannot state a language)",
              sideWritten == 2, "-> " + sideWritten);

        // Both areas answer the SAME question, so the answer must merge them:
        // DE comes from the embed side, EN from the sidecar side.
        var hiLangs = reg.HearingImpairedLanguages(media);
        Check("HI answer merges both areas (DE embed + EN sidecar)",
              hiLangs.Contains("DE") && hiLangs.Contains("EN"),
              "-> [" + string.Join(",", hiLangs.OrderBy(x => x)) + "]");

        // Idempotence: the seeder calls this on every pass.
        int embAgain = 0, sideAgain = 0;
        foreach (var (pos, lang, hi) in tracks)
        {
            if (reg.ObserveEmbed(mediaHash, pos, lang, hi))
            {
                embAgain++;
            }
        }

        foreach (var (path, lang, hi) in loose)
        {
            string text = File.ReadAllText(path);
            if (reg.ObserveSidecar(ContentHashRegistry.ComputeHash(text), mediaHash, lang, hi,
                                   Path.GetFileName(path), path))
            {
                sideAgain++;
            }
        }

        Check("second pass writes nothing (idempotent)", embAgain == 0 && sideAgain == 0,
              "-> emb=" + embAgain + " side=" + sideAgain);

        // ── E ───────────────────────────────────────────────────────────────
        Section("E) Writer/reader name contract (F-M260)");
        string baseName = Path.GetFileNameWithoutExtension(media);
        var shapes = new (string Path, string Lang, bool Hi, string Label)[]
        {
            (SidecarNaming.Build(media, "EN", false), "EN", false, "plain"),
            (SidecarNaming.Build(media, "EN", true), "EN", true, "sdh"),
            (SidecarNaming.Build(media, "EN", false, 3), "EN", false, "slot 3"),
            (SidecarNaming.Build(media, "EN", true, 2), "EN", true, "sdh + slot 2"),
        };

        foreach (var (path, wantLang, wantHi, label) in shapes)
        {
            string want = label == "sdh + slot 2" ? ".en.sdh.2.srt"
                        : label == "slot 3" ? ".en.3.srt"
                        : label == "sdh" ? ".en.sdh.srt"
                        : ".en.srt";
            Check("Build: " + label + " -> " + want, path.EndsWith(want, StringComparison.Ordinal),
                  "-> " + Path.GetFileName(path));

            var parsed = SidecarNaming.Parse(Path.GetFileNameWithoutExtension(path), baseName);
            bool ok = parsed.HasValue
                      && parsed.Value.HearingImpaired == wantHi
                      && string.Equals(parsed.Value.Lang, wantLang, StringComparison.OrdinalIgnoreCase);
            Check("Parse reads back " + label, ok,
                  parsed.HasValue ? $"-> lang={parsed.Value.Lang} hi={parsed.Value.HearingImpaired}" : "-> null");
        }

        // ── E2 ──────────────────────────────────────────────────────────────
        // F-M278: the rename target. A file whose NAME says nothing gets the shape this plugin
        // writes — and a combination already on disk takes the next slot instead of an overwrite,
        // because two unlabelled files detected as EN are two subtitles.
        Section("E2) Rename target for an unlabelled sidecar (F-M278)");

        var noneTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string freeTarget = SidecarNaming.PlanTarget(media, "EN", false, noneTaken);
        Check("free slot 1 -> <base>.en.srt",
              Path.GetFileName(freeTarget) == baseName + ".en.srt",
              "-> " + Path.GetFileName(freeTarget));

        var plainTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseName + ".en.srt" };
        string slot2 = SidecarNaming.PlanTarget(media, "EN", false, plainTaken);
        Check("taken slot 1 -> slot 2",
              Path.GetFileName(slot2) == baseName + ".en.2.srt",
              "-> " + Path.GetFileName(slot2));

        var twoTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            baseName + ".en.srt", baseName + ".en.2.srt",
        };
        string slot3 = SidecarNaming.PlanTarget(media, "EN", false, twoTaken);
        Check("taken slots 1+2 -> slot 3",
              Path.GetFileName(slot3) == baseName + ".en.3.srt",
              "-> " + Path.GetFileName(slot3));

        // The slot is per COMBINATION: a DE file present does not push the EN file to a slot.
        var otherLangTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseName + ".de.srt" };
        Check("another language's file does not occupy the slot",
              Path.GetFileName(SidecarNaming.PlanTarget(media, "EN", false, otherLangTaken)) == baseName + ".en.srt");

        // And the target must be a name the plugin's own reader reads back — a rename that writes a
        // name Parse cannot resolve would make the file invisible while "tidying" it.
        foreach (var (path, label) in new (string, string)[]
                 {
                     (freeTarget, "slot 1"), (slot2, "slot 2"), (slot3, "slot 3"),
                 })
        {
            var parsedTarget = SidecarNaming.Parse(Path.GetFileNameWithoutExtension(path), baseName);
            Check("Parse reads the rename target back (" + label + ")",
                  parsedTarget.HasValue
                  && string.Equals(parsedTarget.Value.Lang, "EN", StringComparison.OrdinalIgnoreCase)
                  && !parsedTarget.Value.HearingImpaired,
                  parsedTarget.HasValue ? "-> lang=" + parsedTarget.Value.Lang : "-> null");
        }

        // ── E3 ──────────────────────────────────────────────────────────────
        // F-M278: a rename moves the file, so a stored row must move with it — otherwise the refresh
        // task forgets the row of a file that is right there under a new name.
        Section("E3) A renamed sidecar keeps its row and its verdict (F-M278)");

        string movedText = "1\n00:00:01,000 --> 00:00:02,000\nmoved\n";
        string movedHash = ContentHashRegistry.ComputeHash(movedText);
        string oldPath = Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.srt");
        string newPath = Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.en.srt");
        reg.MarkSidecar(movedHash, mediaHash, "EN", false, SubtitleStatus.Uploaded,
                        fileName: Path.GetFileName(oldPath), path: oldPath);

        bool locMoved = reg.MoveSidecarLocation(movedHash, Path.GetFileName(newPath), newPath);
        var renamedRow = reg.GetSidecar(movedHash);
        Check("the location move reports a change", locMoved);
        Check("the row points at the new name",
              renamedRow != null && renamedRow.Path == newPath,
              "-> " + (renamedRow?.Path ?? "null"));
        Check("the VERDICT survived the move",
              renamedRow != null && renamedRow.Status == SubtitleStatus.Uploaded,
              "-> " + (renamedRow?.Status ?? "null"));

        Check("a second identical move is a no-op",
              !reg.MoveSidecarLocation(movedHash, Path.GetFileName(newPath), newPath));
        Check("a move for an unknown row does nothing",
              !reg.MoveSidecarLocation("00000000000000000000000000000000", "x.srt", Path.Combine(mediaDir, "x.srt")));

        // ── F ───────────────────────────────────────────────────────────────
        Section("F) An observation never overwrites a verdict");
        string verdictText = "1\n00:00:01,000 --> 00:00:02,000\nverdict\n";
        string verdictHash = ContentHashRegistry.ComputeHash(verdictText);
        reg.MarkSidecar(verdictHash, mediaHash, "EN", false, SubtitleStatus.Downloaded,
                        subdlId: "12345", fileName: "Film.2026.1080p.WEB-DL.en.srt",
                        path: Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.en.srt"));

        bool changed = reg.ObserveSidecar(verdictHash, mediaHash, "EN", true);
        var row = reg.GetSidecar(verdictHash);
        Check("observation reports 'nothing done'", !changed);
        Check("the verdict still stands", row != null && row.Status == SubtitleStatus.Downloaded,
              "-> " + (row?.Status ?? "null"));
        Check("observation did NOT flip the HI flag", row != null && !row.HearingImpaired,
              "-> hi=" + (row?.HearingImpaired.ToString() ?? "?"));
        Check("a decided row counts as known",
              reg.IsContentKnown(verdictHash));
        Console.WriteLine("       (the trap on the other side: an OBSERVATION must not count as");
        Console.WriteLine("        knowledge, or the downloader skips a write it never made —");
        Console.WriteLine("        covered by the observation rows above, asserted below)");

        // An observation row must NOT answer IsContentKnown.
        string obsText = "1\n00:00:01,000 --> 00:00:02,000\nobserved only\n";
        string obsHash = ContentHashRegistry.ComputeHash(obsText);
        reg.ObserveSidecar(obsHash, mediaHash, "FR", false);
        Check("an observation is NOT knowledge", !reg.IsContentKnown(obsHash));

        // ── G ───────────────────────────────────────────────────────────────
        Section("G) Edge cases");
        Check("no streams -> no tracks", SidecarNaming.EmbeddedTracks(null).Count == 0);
        Check("empty stream list -> no tracks", SidecarNaming.EmbeddedTracks(new List<MediaStream>()).Count == 0);

        var onlyVideo = new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc" } };
        Check("video-only -> no tracks", SidecarNaming.EmbeddedTracks(onlyVideo).Count == 0);

        string unmappable = Path.Combine(root, "empty-dir");
        Directory.CreateDirectory(unmappable);
        string orphan = Path.Combine(unmappable, "Nothing.mkv");
        File.WriteAllBytes(orphan, new byte[] { 1, 2, 3 });
        Check("no sidecars -> empty list, no throw", SidecarNaming.List(orphan).Count == 0);

        // ── H ───────────────────────────────────────────────────────────────
        Section("H) Untagged / 'und' language gate (F-M261)");
        Console.WriteLine("  The gap: MapToSubdl has no entry for 'und', so its two-letter fallback");
        Console.WriteLine("  invents 'UN' — and a NULL tag maps to nothing and gets no row at all.");
        Console.WriteLine("  Either way the embedded area answers 'language absent' for a track that");
        Console.WriteLine("  is sitting right there. Measured on Slow.Horses.S06E03: 44 tracks, all");
        Console.WriteLine("  lang=None, holding real EN/AR/PT/BG text.");

        // The two shapes that must be recognized as undecided — and the ones that must not.
        Check("NULL tag is undecided", LanguageTagGate.IsUntagged(null));
        Check("empty tag is undecided", LanguageTagGate.IsUntagged("   "));
        Check("literal 'und' is undecided", LanguageTagGate.IsUntagged("und"));
        Check("'UND' (any case) is undecided", LanguageTagGate.IsUntagged("UND"));
        Check("'undefined' is undecided", LanguageTagGate.IsUntagged("undefined"));
        Check("a real tag is NOT undecided", !LanguageTagGate.IsUntagged("ger"));
        Check("'eng' is NOT undecided", !LanguageTagGate.IsUntagged("eng"));

        // The defect this gate exists for: what the mapper does with the undecided shapes.
        Check("MapToSubdl(NULL) yields no language (no row)",
              LanguageMapper.MapToSubdl(null) == null,
              "-> " + (LanguageMapper.MapToSubdl(null) ?? "null"));
        Check("MapToSubdl('und') invents 'UN' — the reason 'und' must be resolved first",
              LanguageMapper.MapToSubdl("und") == "UN",
              "-> " + (LanguageMapper.MapToSubdl("und") ?? "null"));

        // The ISO 639-2 write-back table (F-M261, second half).
        Check("ToIso6392('EN') -> 'eng'", LanguageMapper.ToIso6392("EN") == "eng",
              "-> " + LanguageMapper.ToIso6392("EN"));
        Check("ToIso6392 is case-insensitive", LanguageMapper.ToIso6392("de") == "ger",
              "-> " + LanguageMapper.ToIso6392("de"));
        Check("ToIso6392 uses the B spelling these containers carry",
              LanguageMapper.ToIso6392("DE") == "ger",
              "-> " + LanguageMapper.ToIso6392("DE"));
        Check("ToIso6392 round-trips through MapToSubdl",
              LanguageMapper.MapToSubdl(LanguageMapper.ToIso6392("FR")) == "FR",
              "-> " + LanguageMapper.MapToSubdl(LanguageMapper.ToIso6392("FR")));
        Check("ToIso6392 falls back to a legal tag (never a missing one) for an unmapped code",
              LanguageMapper.ToIso6392("XX") == "xx",
              "-> " + (LanguageMapper.ToIso6392("XX") ?? "null"));
        Check("ToIso6392(NULL) -> null", LanguageMapper.ToIso6392(null) == null);

        // The verification rule that guards the container write. It must accept what ffmpeg
        // writes back, and must NOT accept a wrong tag — a false OK replaces a good file.
        Check("tag verification: exact match", FfmpegTools.LanguageTagMatches("eng", "eng"));
        Check("tag verification: 'en' matches 'eng'", FfmpegTools.LanguageTagMatches("en", "eng"));
        Check("tag verification: case-insensitive", FfmpegTools.LanguageTagMatches("ENG", "eng"));
        Check("tag verification rejects a different language",
              !FfmpegTools.LanguageTagMatches("fre", "eng"));
        Check("tag verification rejects an empty tag",
              !FfmpegTools.LanguageTagMatches("", "eng"));
        Check("tag verification rejects a long name (not the same comparison)",
              !FfmpegTools.LanguageTagMatches("English", "eng"));

        // ── I ───────────────────────────────────────────────────────────────
        Section("I) Media identity move after a tag write (F-M261)");
        Console.WriteLine("  Writing a tag changes the file's OSHash (size + first/last 64 KB), and every");
        Console.WriteLine("  row of a file is keyed by that hash. Without the move the file keeps TWO");
        Console.WriteLine("  identities — measured against a real registry: two media rows, same item");
        Console.WriteLine("  id, same path, with the marks on the dead one. Nothing prunes them either:");
        Console.WriteLine("  PruneDeadMediaAndSubtitles only removes a row whose JELLYFIN ITEM is gone.");

        string idRoot = Path.Combine(Path.GetTempPath(), "subdl-ident-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(idRoot);
        try
        {
            using var idb = new SubdlDbContext(Path.Combine(idRoot, "db"), null);
            var ireg = new ContentHashRegistry(idb, null, null);
            const string itemId = "aabbccdd11223344aabbccdd11223344";
            const string hOld = "aaaaaaaaaaaaaaaa";
            const string hNew = "bbbbbbbbbbbbbbbb";
            const string mediaPath = "/media/Film.mkv";

            // State earned on the file BEFORE it was rewritten.
            ireg.EnsureMedia(hOld, itemId, mediaPath, m =>
            {
                m.ImdbId = "tt1234567";
                m.SubtitlesDownloadedAt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
                m.SubtitlesDownloadedLanguages = "de,en";
            });
            ireg.ObserveEmbed(hOld, 0, "EN", false);
            ireg.ObserveEmbed(hOld, 2, "DE", true);
            ireg.MarkSidecar("content-de", hOld, "DE", false, SubtitleStatus.Downloaded);

            Check("before: exactly one media row", idb.Media.Count() == 1, "-> " + idb.Media.Count());
            Check("before: two embed rows", ireg.GetEmbeds(hOld).Count == 2, "-> " + ireg.GetEmbeds(hOld).Count);

            int moved = ireg.ReplaceMediaIdentity(hOld, hNew);

            Check("the move reports the rows it touched", moved == 4, "-> " + moved);
            Check("exactly ONE media row after the move", idb.Media.Count() == 1, "-> " + idb.Media.Count());
            Check("the old key is gone", ireg.GetMedia(hOld) == null);
            Check("the new key exists", ireg.GetMedia(hNew) != null);

            var movedRow = ireg.GetMedia(hNew);
            Check("item id travelled", movedRow?.JellyfinItemId == itemId, "-> " + (movedRow?.JellyfinItemId ?? "null"));
            Check("path travelled", movedRow?.Path == mediaPath, "-> " + (movedRow?.Path ?? "null"));
            Check("imdb id travelled", movedRow?.ImdbId == "tt1234567", "-> " + (movedRow?.ImdbId ?? "null"));
            Check("the download mark travelled (this is a rename, not a re-decision)",
                  movedRow?.SubtitlesDownloadedAt != null,
                  "-> " + (movedRow?.SubtitlesDownloadedAt?.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "null"));
            Check("the mark's language list travelled",
                  movedRow?.SubtitlesDownloadedLanguages == "de,en",
                  "-> " + (movedRow?.SubtitlesDownloadedLanguages ?? "null"));

            Check("embeds travelled", ireg.GetEmbeds(hNew).Count == 2, "-> " + ireg.GetEmbeds(hNew).Count);
            Check("no embeds left under the old key", ireg.GetEmbeds(hOld).Count == 0, "-> " + ireg.GetEmbeds(hOld).Count);

            var hiRow = ireg.GetEmbeds(hNew).FirstOrDefault(x => x.SubPos == 2);
            Check("the HI flag survived the move", hiRow?.HearingImpaired == true, "-> " + (hiRow?.HearingImpaired.ToString() ?? "?"));
            Check("the language survived the move", hiRow?.Language == "DE", "-> " + (hiRow?.Language ?? "null"));

            Check("the sidecar's parent pointer travelled",
                  idb.Sidecars.FindAll().FirstOrDefault()?.MediaHash == hNew,
                  "-> " + (idb.Sidecars.FindAll().FirstOrDefault()?.MediaHash ?? "none"));

            // The consequence that matters. `FindByItemId` is a FindOne: with two rows carrying the
            // same item id the winner was undefined, so the search stamp could land on the dead row.
            Check("FindOne(itemId) is unambiguous again — and it is the LIVE row",
                  idb.Media.FindOne(x => x.JellyfinItemId == itemId)?.Id == hNew,
                  "-> " + (idb.Media.FindOne(x => x.JellyfinItemId == itemId)?.Id ?? "nothing"));

            // Guards: an equal pair must not move anything, an empty hash must not act as one.
            int samePair = ireg.ReplaceMediaIdentity(hNew, hNew);
            Check("an equal pair is a no-op", samePair == 0, "-> " + samePair);
            Check("the row is still there after the no-op", idb.Media.Count() == 1, "-> " + idb.Media.Count());
            Check("an empty old hash is refused", ireg.ReplaceMediaIdentity(null, "cccccccccccccccc") == 0);
            Check("an empty new hash is refused", ireg.ReplaceMediaIdentity("cccccccccccccccc", null) == 0);
            Check("no row was created by the refusals", idb.Media.Count() == 1, "-> " + idb.Media.Count());

            // Collision: both keys already carry a row. The OLD one wins — it is the row that
            // accumulated the work on this same file (marks, ids, verdicts), while a row under the
            // new hash can only come from a run that already saw the rewritten file and knows no
            // more than it. What must NOT happen either way: two rows for one file.
            ireg.EnsureMedia(hOld, itemId, mediaPath, m => m.SdId = "sd-old");
            ireg.EnsureMedia(hNew, itemId, mediaPath, m => m.SdId = "sd-new");
            ireg.ReplaceMediaIdentity(hOld, hNew);
            Check("a collision does not duplicate the row", idb.Media.Count() == 1, "-> " + idb.Media.Count());
            Check("the accumulated row wins the collision", ireg.GetMedia(hNew)?.SdId == "sd-old",
                  "-> " + (ireg.GetMedia(hNew)?.SdId ?? "null"));
            Check("the collision kept one identity, not two", ireg.GetMedia(hOld) == null);
        }
        finally
        {
            try { Directory.Delete(idRoot, true); } catch { /* best effort */ }
        }

        // ── summary ─────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Fehler: " + _failed + "  (bestanden: " + _passed + ")");
        return _failed == 0 ? 0 : 1;
    }
}
