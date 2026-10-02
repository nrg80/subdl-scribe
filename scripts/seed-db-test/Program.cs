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
//   J  the OSHash VALUE, pinned against fixed vectors (F-M61b)
//
// What this does NOT cover: a full SubdlSeeder.Scan() pass, and therefore the rename itself —
// RenameSidecar reads Plugin.Instance for the switch and moves a file, so it needs the host. This
// suite covers the pure rule it calls (SidecarNaming.PlanTarget) and the row move that follows it
// (MoveSidecarLocation). The scan loop is covered by the live test instance, not here.
//
// Section J pins the OSHash against literal vectors computed independently (Python, from the
// published algorithm). Before it existed, the only assertion was `hash.Length > 0` — which passes
// on any wrong implementation whose output merely LOOKS like a hash. Every downstream identity
// (media rows, mark invalidation, seeder dedup, the tag-rewrite move) is keyed by this value, so a
// wrong value breaks them all silently. Change the algorithm deliberately, never to make J green.
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
        // F-M284: a forced track IS observed now — it used to be filtered out here, which left the
        // registry with no row for a track the file demonstrably carries, and every caller then
        // re-derived the exclusion from Jellyfin's flag in five separate places. The rule lives in
        // ONE reader (SubtitleCoverage) and the row states what the file has.
        Check("three embedded tracks survive the filters (forced is OBSERVED now)",
              tracks.Count == 3,
              "-> " + string.Join(",", tracks.Select(t => "pos" + t.SubPos + ":" + t.Lang
                    + (t.HearingImpaired ? "/hi" : "") + (t.Forced ? "/forced" : ""))));
        Check("bitmap codec skipped — no text, so no datum", !tracks.Any(t => t.Lang == "FR"));
        Check("external stream skipped", !tracks.Any(t => t.Lang == "IT"));
        Check("HI flag carried through", tracks.Any(t => t.Lang == "DE" && t.HearingImpaired));
        Check("forced flag carried through", tracks.Any(t => t.Lang == "ES" && t.Forced),
              "-> " + string.Join(",", tracks.Where(t => t.Lang == "ES").Select(t => t.Forced.ToString())));
        Check("a forced track is not hearing-impaired", !tracks.Any(t => t.Lang == "ES" && t.HearingImpaired));

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

        // ── B2) the datum is the pair (language, HI) — F-M282 ───────────────
        // A hearing-impaired subtitle is its own datum with its own identity: a regular subtitle
        // and its variant are two files on disk, two sidecar rows keyed by content, and two pairs.
        // The failure this guards is the one the string tokens could not prevent — both recorded
        // under the plain language, so the regular save closed the variant's slot and the mark
        // claimed a file that was not there, while the search re-fetched the regular file.
        Section("B2) The datum is the pair (language, HI) — F-M282");
        var deRegular = new SubtitleRef("de", false);
        var deVariant = new SubtitleRef("DE", true);
        Check("the language is normalized, so two spellings are one datum",
              deRegular == new SubtitleRef("DE", false),
              "-> " + deRegular.Language);
        Check("the variant is a DIFFERENT datum from the regular file",
              !deRegular.Equals(deVariant));
        Check("a variant prints as readable text, not as a parseable token",
              deVariant.ToString() == "DE (HI)",
              "-> " + deVariant.ToString());
        Check("an empty language is not a datum", !new SubtitleRef("", false).IsUsable);

        // Required(): the switch ADDS the variant pairs, so turning it on makes an item due for the
        // variant and turning it off removes them with nothing to clean up.
        var withoutHi = SubtitleRef.Required(new[] { "DE" }, includeHearingImpaired: false);
        var withHi = SubtitleRef.Required(new[] { "DE" }, includeHearingImpaired: true);
        Check("switch off requires exactly the regular file", withoutHi.Count == 1,
              "-> " + withoutHi.Count);
        Check("switch on requires the regular file AND its variant", withHi.Count == 2,
              "-> " + string.Join(",", withHi.Select(p => p.ToString())));
        Check("the two required pairs are distinct", withHi[0] != withHi[1]);

        // PresentPairs reads the DISK: the .de.srt in this fixture carries no marker, so it proves
        // the regular datum and must NOT prove the variant. Under the string tokens this assertion
        // had to be written against a spelling ("DE" vs "DE:hi"); now it is a comparison of data.
        var presentPairs = SidecarNaming.PresentPairs(media);
        Check("the .de.srt proves the regular DE datum", presentPairs.Contains(new SubtitleRef("DE", false)));
        Check("the .de.srt does NOT prove the DE variant", !presentPairs.Contains(new SubtitleRef("DE", true)),
              "-> [" + string.Join(",", presentPairs.Select(p => p.ToString()).OrderBy(x => x)) + "]");
        Check("the .en.sdh.srt proves the EN variant", presentPairs.Contains(new SubtitleRef("EN", true)));
        Check("the .en.sdh.srt also proves EN itself (a variant is still the language)",
              presentPairs.Contains(new SubtitleRef("EN", false)));

        // The variant, once on disk, must prove its own datum.
        File.WriteAllText(Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.de.sdh.srt"),
            "1\n00:00:01,000 --> 00:00:02,000\n[ TÜR ]\n");
        var pairsAfter = SidecarNaming.PresentPairs(media);
        Check("a .de.sdh.srt on disk proves the DE variant",
              pairsAfter.Contains(new SubtitleRef("DE", true)),
              "-> [" + string.Join(",", pairsAfter.Select(p => p.ToString()).OrderBy(x => x)) + "]");
        File.Delete(Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.de.sdh.srt"));

        // ── B2b) the marker reader the backfill shares with Parse — F-M284 ──
        // A row written before `Forced` existed reads back as false, so a forced sidecar would
        // COUNT as coverage and its language would stay settled forever. The refresh re-reads the
        // flag off the row's own name, which is where it always came from — through this reader, so
        // the backfill and the normal parse cannot disagree about what a marker is.
        Section("B2b) The marker reader: forced and hi as a set (F-M284)");
        Check("a plain name states neither flag",
              SidecarNaming.ReadFlags("Film.2026.1080p.WEB-DL.de") == (false, false),
              "-> " + SidecarNaming.ReadFlags("Film.2026.1080p.WEB-DL.de"));
        Check("the .forced marker is read",
              SidecarNaming.ReadFlags("Film.2026.1080p.WEB-DL.de.forced").Forced);
        Check("the .sdh marker is read",
              SidecarNaming.ReadFlags("Film.2026.1080p.WEB-DL.de.sdh").HearingImpaired);
        var bothFlags = SidecarNaming.ReadFlags("Film.2026.1080p.WEB-DL.de.sdh.forced");
        Check("both markers in one name resolve together (sdh then forced)",
              bothFlags.HearingImpaired && bothFlags.Forced,
              "-> hi=" + bothFlags.HearingImpaired + " forced=" + bothFlags.Forced);
        var bothReversed = SidecarNaming.ReadFlags("Film.2026.1080p.WEB-DL.de.forced.sdh");
        Check("order does not matter (forced then sdh)",
              bothReversed.HearingImpaired && bothReversed.Forced,
              "-> hi=" + bothReversed.HearingImpaired + " forced=" + bothReversed.Forced);
        Check("the numbered slot is stepped over before the markers are read",
              SidecarNaming.ReadFlags("Film.2026.1080p.WEB-DL.de.sdh.2").HearingImpaired,
              "-> " + SidecarNaming.ReadFlags("Film.2026.1080p.WEB-DL.de.sdh.2"));
        Check("a language token that merely LOOKS like a marker is not one",
              SidecarNaming.ReadFlags("Film.2026.1080p.WEB-DL.hin") == (false, false),
              "-> hin is Hindi, not a marker");

        // ── B3) coverage: the ONE reader, and the two search pools it feeds ──
        // This is the regression that mattered on the live library: a missing variant was reported
        // as a missing LANGUAGE, the regular search was fed that language, and the .de.srt already
        // on disk was fetched again — 41 downloads in one run against a 50/day limit, the same
        // content hash on three consecutive days.
        Section("B3) Coverage: the datum decides, and the pools are separate — F-M282/F-M283");
        var covRegularOnly = SubtitleCoverage.FromPairs(new[] { new SubtitleRef("DE", false) });
        Check("a regular file covers its language datum",
              covRegularOnly.Covers(new SubtitleRef("DE", false)));
        Check("a regular file does NOT cover the variant datum",
              !covRegularOnly.Covers(new SubtitleRef("DE", true)));

        var requiredBoth = SubtitleRef.Required(new[] { "DE" }, includeHearingImpaired: true);
        var openBoth = covRegularOnly.Open(requiredBoth);
        Check("exactly the variant is open when only the regular file is present",
              openBoth.Count == 1 && openBoth[0] == new SubtitleRef("DE", true),
              "-> " + string.Join(",", openBoth.Select(p => p.ToString())));
        Check("the open regular pool is EMPTY — no re-fetch of the file on disk",
              SubtitleCoverage.RegularLanguages(openBoth).Count == 0,
              "-> [" + string.Join(",", SubtitleCoverage.RegularLanguages(openBoth)) + "]");
        Check("the open variant pool names DE — the pool that must be searched",
              SubtitleCoverage.VariantLanguages(openBoth).SequenceEqual(new[] { "DE" }),
              "-> [" + string.Join(",", SubtitleCoverage.VariantLanguages(openBoth)) + "]");

        var covVariantOnDisk = SubtitleCoverage.FromPairs(new[] { new SubtitleRef("DE", true) });
        Check("a variant file satisfies the regular datum too (SDH is the same dialogue)",
              covVariantOnDisk.Covers(new SubtitleRef("DE", false)));
        Check("a variant file satisfies the variant datum", covVariantOnDisk.Covers(new SubtitleRef("DE", true)));
        Check("nothing is open for a file carrying its German", covVariantOnDisk.Open(requiredBoth).Count == 0);

        // Deleting the variant puts its datum back — no invalidation step, no stored mark.
        Check("a datum with no evidence is open again (self-correcting, F-M283)",
              covRegularOnly.Open(requiredBoth).Any(p => p.HearingImpaired));

        // ── D ───────────────────────────────────────────────────────────────
        Section("D) The three together = one database built");
        int embWritten = 0;
        foreach (var (pos, lang, hi, forced) in tracks)
        {
            if (reg.ObserveEmbed(mediaHash, pos, lang, hi, forced))
            {
                embWritten++;
            }
        }

        int sideWritten = 0;
        foreach (var (path, lang, hi, _) in loose)
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
        Check("embed rows written (forced row included — it is a fact about the file)",
              embWritten == 3, "-> " + embWritten);
        Check("the forced row states its flag in the database",
              reg.GetEmbeds(mediaHash).Any(x => x.Language == "ES" && x.Forced),
              "-> " + string.Join(",", reg.GetEmbeds(mediaHash).Select(x => x.Language + (x.Forced ? "/forced" : ""))));
        Check("a forced track never counts as coverage (F-M246/F-M284)",
              !SubtitleCoverage.FromPairs(new[] { new SubtitleRef("ES", false, true) })
                  .Covers(new SubtitleRef("ES", false)),
              "-> forced ES alone must NOT cover ES");
        Check("sidecar rows written (2 labelled, the unlabelled one cannot state a language)",
              sideWritten == 2, "-> " + sideWritten);

        // Both areas answer the SAME question, so the answer must merge them — and it now answers
        // with PAIRS, which is what lets a caller tell the regular German from its variant:
        // DE (HI) comes from the embed side, EN (HI) from the sidecar side.
        var recorded = reg.RecordedPairs(media);
        Check("the recorded pairs merge both areas (DE variant embed + EN variant sidecar)",
              recorded.Contains(new SubtitleRef("DE", true)) && recorded.Contains(new SubtitleRef("EN", true)),
              "-> [" + string.Join(",", recorded.Select(p => p.ToString()).OrderBy(x => x)) + "]");
        Check("the regular datum of a variant language is recorded too (SDH is still the language)",
              recorded.Contains(new SubtitleRef("DE", false)) && recorded.Contains(new SubtitleRef("EN", false)),
              "-> [" + string.Join(",", recorded.Select(p => p.ToString()).OrderBy(x => x)) + "]");

        // Idempotence: the seeder calls this on every pass.
        int embAgain = 0, sideAgain = 0;
        foreach (var (pos, lang, hi, forced) in tracks)
        {
            if (reg.ObserveEmbed(mediaHash, pos, lang, hi, forced))
            {
                embAgain++;
            }
        }

        foreach (var (path, lang, hi, _) in loose)
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
            Check("the subtitle DATA travelled — the rows that describe them (F-M283)",
                  ireg.GetEmbeds(hNew).Count == 2 && idb.Sidecars.FindAll().Any(),
                  "-> " + ireg.GetEmbeds(hNew).Count + " embed(s), " + idb.Sidecars.FindAll().Count() + " sidecar(s)");
            Check("the pair record for the file travelled with its flag",
                  ireg.RecordedPairsByHash(hNew).Contains(new SubtitleRef("DE", true)),
                  "-> [" + string.Join(",", ireg.RecordedPairsByHash(hNew).Select(p => p.ToString())) + "]");

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

        // ── J ───────────────────────────────────────────────────────────────
        Section("J) The OSHash VALUE, pinned against fixed vectors (F-M61b)");
        Console.WriteLine("  Independent oracle: scripts/oshash-oracle/oshash_oracle.py (Python, written");
        Console.WriteLine("  from the published algorithm, NOT from ComputeMediaHash). These literals are");
        Console.WriteLine("  its output; the assertion below is that the shipped code agrees.");

        string jDir = Path.Combine(Path.GetTempPath(), "subdl-oshash-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(jDir);
        try
        {
            // (size, byte, expected) — the pattern is uniform, so the expected value is a pure
            // function of size and byte and can be recomputed by hand from the algorithm.
            (int Size, byte Byte, string Expected)[] vectors =
            [
                (8, 0x42, "848484848484848c"),
                (65536, 0x42, "9090909090918000"),
                (131072, 0x42, "9090909090928000"),
                (200000, 0x42, "9090909090938d40"),
            ];

            foreach (var (size, fill, expected) in vectors)
            {
                string vp = Path.Combine(jDir, "v_" + size + "_" + fill.ToString("x2", System.Globalization.CultureInfo.InvariantCulture) + ".bin");
                File.WriteAllBytes(vp, Enumerable.Repeat(fill, size).ToArray());
                string got = ContentHashRegistry.ComputeMediaHash(vp) ?? "<null>";
                Check(size + " bytes pinned to the independent oracle", got == expected, "-> " + got);
            }

            // The sizes either side of the 64 KiB window: these are where a wrong chunk size hides.
            (int Size, byte Byte, string Expected)[] edges =
            [
                (65535, 0x42, "0c0c0c0c0c0cfb7b"),
                (65537, 0x42, "9090909090918001"),
                (131071, 0x42, "9090909090927fff"),
                (131073, 0x42, "9090909090928001"),
            ];

            foreach (var (size, fill, expected) in edges)
            {
                string vp = Path.Combine(jDir, "e_" + size + ".bin");
                File.WriteAllBytes(vp, Enumerable.Repeat(fill, size).ToArray());
                string got = ContentHashRegistry.ComputeMediaHash(vp) ?? "<null>";
                Check("window edge " + size + " bytes", got == expected, "-> " + got);
            }

            // A file that differs ONLY in the tail must hash differently: that is what makes the
            // value usable as an identity at all.
            string a = Path.Combine(jDir, "tail_a.bin");
            string b = Path.Combine(jDir, "tail_b.bin");
            var body = Enumerable.Repeat((byte)0x42, 200_000).ToArray();
            File.WriteAllBytes(a, body);
            var bodyB = (byte[])body.Clone();
            bodyB[^1] = 0x43;
            File.WriteAllBytes(b, bodyB);
            string ha = ContentHashRegistry.ComputeMediaHash(a) ?? "<null>";
            string hb = ContentHashRegistry.ComputeMediaHash(b) ?? "<null>";
            Check("a one-byte tail change changes the hash", ha != hb, "-> " + ha + " vs " + hb);

            // Same size, same first 64 KiB, different last window.
            string c1 = Path.Combine(jDir, "head_same_1.bin");
            string c2 = Path.Combine(jDir, "head_same_2.bin");
            var first = Enumerable.Repeat((byte)0x42, 200_000).ToArray();
            var second = (byte[])first.Clone();
            second[^8] = 0x43;
            File.WriteAllBytes(c1, first);
            File.WriteAllBytes(c2, second);
            Check("a last-window change changes the hash",
                  ContentHashRegistry.ComputeMediaHash(c1) != ContentHashRegistry.ComputeMediaHash(c2));

            // Empty file: the code refuses (chunk <= 0) rather than inventing a value.
            string empty = Path.Combine(jDir, "empty.bin");
            File.WriteAllBytes(empty, Array.Empty<byte>());
            Check("an empty file yields null, not a made-up hash",
                  ContentHashRegistry.ComputeMediaHash(empty) == null);

            // A missing path must return null, not throw.
            Check("a missing path yields null, not an exception",
                  ContentHashRegistry.ComputeMediaHash(Path.Combine(jDir, "nope.bin")) == null);

            // The cache must return the SAME value as the direct computation.
            using var jdb = new SubdlDbContext(Path.Combine(jDir, "db"), null);
            var jreg = new ContentHashRegistry(jdb, null, null);
            string direct = ContentHashRegistry.ComputeMediaHash(a) ?? "<null>";
            string viaCache = jreg.GetMediaHash(a) ?? "<null>";
            string secondRead = jreg.GetMediaHash(a) ?? "<null>";
            Check("the cache returns the same value as the direct call", viaCache == direct, "-> " + viaCache);
            Check("a second read is stable (cache hit)", secondRead == direct, "-> " + secondRead);
        }
        finally
        {
            try { Directory.Delete(jDir, true); } catch { /* best effort */ }
        }

        // ── summary ─────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Fehler: " + _failed + "  (bestanden: " + _passed + ")");
        return _failed == 0 ? 0 : 1;
    }
}
