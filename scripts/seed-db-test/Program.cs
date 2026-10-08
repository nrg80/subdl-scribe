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
using System.Text.RegularExpressions;

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

        // ── B2c) ja / nein / nicht gesagt — F-M285 ──────────────────────────
        // The stored value is THREE-valued. An explicit false is a STATEMENT (this subtitle is not
        // the variant); null is a row that does not say. Reading the gap as "no" is how an unknown
        // becomes a false claim — a legacy forced row would pass as the film's dialogue and its
        // language would look settled forever, with nothing left to correct it.
        Section("B2c) hi/forced are ja / nein / nicht gesagt (F-M285)");
        using (var ndb = new SubdlDbContext(Path.Combine(root, "db-null"), null))
        {
            var nreg = new ContentHashRegistry(ndb, null, null);
            string nHash = "aaaaaaaabbbbbbbb";

            // A row from an older build: it carries no flag at all.
            ndb.Embeds.Upsert(new EmbedTrackEntity
            {
                Id = SubdlDbContext.EmbedKey(nHash, 0),
                MediaHash = nHash,
                SubPos = 0,
                Language = "DE",
                Status = SubtitleStatus.Observed
            });
            var legacy = ndb.Embeds.FindById(SubdlDbContext.EmbedKey(nHash, 0));
            Check("a row written before the field existed reads back as NULL, not false",
                  legacy?.HearingImpaired == null,
                  "-> hi=" + (legacy?.HearingImpaired?.ToString() ?? "null (sagt nichts)"));

            // The gap must NOT be read as coverage. An open datum is worked once more, and that
            // pass states the value explicitly — the direction that converges.
            var legacyCoverage = SubtitleCoverage.Read(nreg, null, null);
            var fromLegacyRows = SubtitleCoverage.Read(nreg, "/media/x.mkv", null);
            Check("a legacy row does not report data at all (no claim from a gap)",
                  nreg.RecordedPairsByHash(nHash).Count == 0,
                  "-> [" + string.Join(",", nreg.RecordedPairsByHash(nHash).Select(p => p.ToString())) + "]");

            // And once a writer states it, the value is explicit.
            nreg.ObserveEmbed(nHash, 1, "FR", false, false);
            Check("a fresh observation states hi EXPLICITLY (false, not null)",
                  ndb.Embeds.FindById(SubdlDbContext.EmbedKey(nHash, 1))?.HearingImpaired == false,
                  "-> hi=" + (ndb.Embeds.FindById(SubdlDbContext.EmbedKey(nHash, 1))?.HearingImpaired?.ToString() ?? "null"));
            Check("a fresh observation states forced EXPLICITLY (false, not null)",
                  ndb.Embeds.FindById(SubdlDbContext.EmbedKey(nHash, 1))?.Forced == false,
                  "-> forced=" + (ndb.Embeds.FindById(SubdlDbContext.EmbedKey(nHash, 1))?.Forced?.ToString() ?? "null"));
            Check("an explicitly stated row DOES report its datum",
                  nreg.RecordedPairsByHash(nHash).Contains(new SubtitleRef("FR", false, false)),
                  "-> [" + string.Join(",", nreg.RecordedPairsByHash(nHash).Select(p => p.ToString())) + "]");
        }

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
        foreach (var (path, lang, hi, forced) in loose)
        {
            string text = File.ReadAllText(path);
            if (reg.ObserveSidecar(ContentHashRegistry.ComputeHash(text), mediaHash, lang, hi, forced,
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
              reg.GetEmbeds(mediaHash).Any(x => x.Language == "ES" && x.Forced == true),
              "-> " + string.Join(",", reg.GetEmbeds(mediaHash).Select(x => x.Language + (x.Forced == true ? "/forced" : ""))));
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
        // F-M285: this reader reports what the ROWS STATE, raw — no expansion. A row saying
        // "DE (HI)" is recorded as DE (HI) and nothing else; whether that also covers plain DE is
        // the coverage question, answered in ONE place (SubtitleCoverage), not by a second reader
        // quietly widening the set. Two readers of one question that disagree is the defect this
        // whole change removes.
        Check("the recorded data merge both areas (DE variant embed + forced ES)",
              recorded.Contains(new SubtitleRef("DE", true)) && recorded.Contains(new SubtitleRef("ES", false, true)),
              "-> [" + string.Join(",", recorded.Select(p => p.ToString()).OrderBy(x => x)) + "]");
        // The claim is about the RAW reader not widening a variant — so it must be asserted on the
        // EMBED rows, not on the merged set. `recorded` legitimately carries plain DE as well, from
        // the fixture's real .de.srt sidecar, and that is the point: before the sidecar path stated
        // its flags, that row was `null` and INVISIBLE, so this assertion passed VACUOUSLY. It only
        // became a real test once the sidecar wrote its statement.
        var embedOnly = reg.GetEmbeds(mediaHash)
            .Where(r => !string.IsNullOrWhiteSpace(r.Language) && r.HearingImpaired.HasValue && r.Forced.HasValue)
            .Select(r => new SubtitleRef(r.Language, r.HearingImpaired.Value, r.Forced.Value))
            .ToHashSet();
        Check("the raw reader does NOT widen a variant into its plain language",
              !embedOnly.Contains(new SubtitleRef("DE", false)),
              "-> embeds: [" + string.Join(",", embedOnly.Select(p => p.ToString()).OrderBy(x => x)) + "]");
        Check("and the merged set DOES carry plain DE — from the .de.srt sidecar",
              recorded.Contains(new SubtitleRef("DE", false)),
              "-> [" + string.Join(",", recorded.Select(p => p.ToString()).OrderBy(x => x)) + "]");
        var deSidecarRow = loose.Where(x => x.Lang == "EN" || x.Lang == "DE")
            .Select(x => reg.GetSidecar(ContentHashRegistry.ComputeHash(File.ReadAllText(x.Path))))
            .FirstOrDefault(r => r != null && r.Language == "DE");
        Check("the .de.srt sidecar row STATES forced=false explicitly (not a gap)",
              deSidecarRow != null && deSidecarRow.Forced.HasValue && deSidecarRow.Forced.Value == false,
              "-> " + (deSidecarRow?.Forced?.ToString() ?? "NULL (unsichtbar fuer jeden Leser)"));
        Check("coverage is where the widening happens (a variant does cover its language)",
              SubtitleCoverage.FromPairs(recorded).Covers(new SubtitleRef("DE", false)),
              "-> DE (HI) on disk must cover plain DE");

        // Idempotence: the seeder calls this on every pass.
        int embAgain = 0, sideAgain = 0;
        foreach (var (pos, lang, hi, forced) in tracks)
        {
            if (reg.ObserveEmbed(mediaHash, pos, lang, hi, forced))
            {
                embAgain++;
            }
        }

        foreach (var (path, lang, hi, forced) in loose)
        {
            string text = File.ReadAllText(path);
            if (reg.ObserveSidecar(ContentHashRegistry.ComputeHash(text), mediaHash, lang, hi, forced,
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
            string want = label == "sdh + slot 2" ? ".en.sdh.02.srt"
                        : label == "slot 3" ? ".en.03.srt"
                        : label == "sdh" ? ".en.sdh.01.srt"
                        : ".en.01.srt";
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
        Check("free slot 1 -> <base>.en.01.srt",
              Path.GetFileName(freeTarget) == baseName + ".en.01.srt",
              "-> " + Path.GetFileName(freeTarget));

        var plainTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseName + ".en.01.srt" };
        string slot2 = SidecarNaming.PlanTarget(media, "EN", false, plainTaken);
        Check("taken slot 1 -> slot 2",
              Path.GetFileName(slot2) == baseName + ".en.02.srt",
              "-> " + Path.GetFileName(slot2));

        var twoTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            baseName + ".en.01.srt", baseName + ".en.02.srt",
        };
        string slot3 = SidecarNaming.PlanTarget(media, "EN", false, twoTaken);
        Check("taken slots 1+2 -> slot 3",
              Path.GetFileName(slot3) == baseName + ".en.03.srt",
              "-> " + Path.GetFileName(slot3));

        // The slot is per COMBINATION: a DE file present does not push the EN file to a slot.
        var otherLangTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseName + ".de.01.srt" };
        Check("another language's file does not occupy the slot",
              Path.GetFileName(SidecarNaming.PlanTarget(media, "EN", false, otherLangTaken)) == baseName + ".en.01.srt");

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
        reg.MarkSidecar(movedHash, mediaHash, "EN", false, false, SubtitleStatus.Uploaded,
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
        reg.MarkSidecar(verdictHash, mediaHash, "EN", false, false, SubtitleStatus.Downloaded,
                        subdlId: "12345", fileName: "Film.2026.1080p.WEB-DL.en.srt",
                        path: Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.en.srt"));

        bool changed = reg.ObserveSidecar(verdictHash, mediaHash, "EN", true, false);
        var row = reg.GetSidecar(verdictHash);
        Check("observation reports 'nothing done'", !changed);
        Check("the verdict still stands", row != null && row.Status == SubtitleStatus.Downloaded,
              "-> " + (row?.Status ?? "null"));
        Check("observation did NOT flip the HI flag", row != null && row.HearingImpaired == false,
              "-> hi=" + (row?.HearingImpaired?.ToString() ?? "null (sagt nichts)"));
        Check("a decided row counts as known",
              reg.IsContentKnown(verdictHash));
        Console.WriteLine("       (the trap on the other side: an OBSERVATION must not count as");
        Console.WriteLine("        knowledge, or the downloader skips a write it never made —");
        Console.WriteLine("        covered by the observation rows above, asserted below)");

        // An observation row must NOT answer IsContentKnown.
        string obsText = "1\n00:00:01,000 --> 00:00:02,000\nobserved only\n";
        string obsHash = ContentHashRegistry.ComputeHash(obsText);
        reg.ObserveSidecar(obsHash, mediaHash, "FR", false, false);
        Check("an observation is NOT knowledge", !reg.IsContentKnown(obsHash));

        // ── F2) the SIDECAR path states forced too — F-M284 ──────────────────
        Section("F2) The sidecar path states forced (F-M284/F-M285)");
        // The defect this pins: ObserveSidecar/MarkSidecar had no `forced` parameter at all, so a
        // `.de.forced.srt` row was left `null` — and a row that does not state its property is
        // invisible to every reader. The probe measured exactly that (row.Forced = NULL) while the
        // EMBEDDED path recorded `True` from the same call site shape. Both paths must state it.
        string forceText = "1\n00:00:01,000 --> 00:00:02,000\n[ FREMDSPRACHE ]\n";
        string forceHash = ContentHashRegistry.ComputeHash(forceText);
        reg.ObserveSidecar(forceHash, mediaHash, "DE", false, true,
                           "Film.2026.1080p.WEB-DL.de.forced.srt",
                           Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.de.forced.srt"));
        var forceRow = reg.GetSidecar(forceHash);
        Check("a forced sidecar row STATES forced=true",
              forceRow != null && forceRow.Forced == true,
              "-> " + (forceRow?.Forced?.ToString() ?? "NULL (sagt nichts)"));
        Check("and it states hi too (never a gap)",
              forceRow != null && forceRow.HearingImpaired.HasValue,
              "-> " + (forceRow?.HearingImpaired?.ToString() ?? "NULL"));
        Check("the forced datum is reported as a datum",
              reg.RecordedPairsByHash(mediaHash).Contains(new SubtitleRef("DE", false, true)),
              "-> [" + string.Join(",", reg.RecordedPairsByHash(mediaHash).Select(x => x.ToString())) + "]");
        // Scoped to the forced datum ALONE — the merged set also holds plain DE from the real
        // .de.srt, so asking it would have tested the fixture instead of the rule.
        Check("a forced sidecar does NOT cover its plain language",
              !SubtitleCoverage.FromPairs(new[] { new SubtitleRef("DE", false, true) })
                  .Covers(new SubtitleRef("DE", false, false)));
        Check("re-observing the same fact is a no-op",
              !reg.ObserveSidecar(forceHash, mediaHash, "DE", false, true,
                                  "Film.2026.1080p.WEB-DL.de.forced.srt",
                                  Path.Combine(mediaDir, "Film.2026.1080p.WEB-DL.de.forced.srt")));

        // The name writer must carry the marker, or a rename of an unlabelled forced file would
        // turn it into the film's dialogue. F-M316: the slot then follows the markers as a
        // two-digit number — the marker block sits before the number, the number stays last.
        Check("Build carries the forced marker",
              Path.GetFileName(SidecarNaming.Build(media, "DE", false, 1, true)) ==
              Path.GetFileNameWithoutExtension(media) + ".de.forced.01.srt",
              "-> " + Path.GetFileName(SidecarNaming.Build(media, "DE", false, 1, true)));
        Check("Build carries both markers",
              Path.GetFileName(SidecarNaming.Build(media, "DE", true, 1, true)) ==
              Path.GetFileNameWithoutExtension(media) + ".de.sdh.forced.01.srt",
              "-> " + Path.GetFileName(SidecarNaming.Build(media, "DE", true, 1, true)));
        var forcedRoundTrip = SidecarNaming.Parse(
            Path.GetFileNameWithoutExtension(SidecarNaming.Build(media, "DE", true, 1, true)),
            Path.GetFileNameWithoutExtension(media));
        Check("what Build writes, Parse reads back",
              forcedRoundTrip != null && forcedRoundTrip.Value.Lang == "DE"
              && forcedRoundTrip.Value.HearingImpaired && forcedRoundTrip.Value.Forced,
              "-> " + (forcedRoundTrip?.ToString() ?? "null"));

        // ── F3) a forced datum is never DELIVERABLE — F-M284 ─────────────────
        Section("F3) A forced subtitle is observed, never uploaded (F-M284)");
        // The upload pipeline asks this predicate before it reads, hashes or searches a loose file,
        // so a forced subtitle costs nothing. The embedded path needs no such check because its
        // enumeration already drops a forced track (IsDialogueStream).
        Check("a forced datum is NOT deliverable",
              !new SubtitleRef("DE", false, true).IsDeliverable);
        Check("its plain and variant siblings still are",
              new SubtitleRef("DE", false, false).IsDeliverable
              && new SubtitleRef("DE", true, false).IsDeliverable);
        Check("and it stays a usable datum — observed, just not delivered",
              new SubtitleRef("DE", false, true).IsUsable);
        Check("a forced track is never a dialogue stream (the embedded half)",
              !SidecarNaming.IsDialogueStream(new MediaStream
              {
                  Type = MediaStreamType.Subtitle,
                  Codec = "subrip",
                  IsForced = true,
              }));
        Check("and a plain text track still is",
              SidecarNaming.IsDialogueStream(new MediaStream
              {
                  Type = MediaStreamType.Subtitle,
                  Codec = "subrip",
                  IsForced = false,
              }));

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
            ireg.ObserveEmbed(hOld, 0, "EN", false, false);
            ireg.ObserveEmbed(hOld, 2, "DE", true, false);
            ireg.MarkSidecar("content-de", hOld, "DE", false, false, SubtitleStatus.Downloaded);

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
            Check("the HI flag survived the move", hiRow?.HearingImpaired == true, "-> " + (hiRow?.HearingImpaired?.ToString() ?? "null (sagt nichts)"));
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

        // ── K  counters: every reject path is counted, and the delta matches (F-M286) ─────
        // F-M286 widened the reject counters from "the QA gates" to "everything fetched and thrown
        // away", because the old scope made the GUI column and the run line describe a fraction of
        // the run: seven of nine discards in the audited download run exited through the
        // hearing-impaired block, which counted nothing. These checks pin the CONTRACT, not the
        // implementation: the two summaries expose one reject number per direction, a dry run
        // contributes zero, and an all-zero summary leaves the row untouched.
        {
            // A real download run: 41 saved, 9 fetched and discarded.
            var dl = new DownloadRunSummary { Downloaded = 41, RejectedCandidates = 9 };
            var dlDelta = StatusCounterDelta.From(null, dl);
            Check("K1 a download run reports its rejects beside its saves",
                dlDelta.Downloaded == 41 && dlDelta.RejectedDownload == 9,
                $"-> {dlDelta.Downloaded} saved / {dlDelta.RejectedDownload} rejected");
            Check("K2 a download run contributes no upload reject",
                dlDelta.RejectedUpload == 0);

            // A dry run saves nothing and must therefore report nothing — not even the rejects.
            var dry = new DownloadRunSummary { Downloaded = 0, RejectedCandidates = 9, IsDryRun = true };
            var dryDelta = StatusCounterDelta.From(null, dry);
            Check("K3 a dry run contributes no rejects (it is not a fact about the library)",
                dryDelta.RejectedDownload == 0, $"-> {dryDelta.RejectedDownload}");

            // An upload run: the counter covers every path, not the QA gates alone.
            var up = new RunSummary { Uploaded = 3, RejectedCandidates = 12 };
            var upDelta = StatusCounterDelta.From(up, null);
            Check("K4 an upload run reports its rejects beside its uploads",
                upDelta.Uploaded == 3 && upDelta.RejectedUpload == 12,
                $"-> {upDelta.Uploaded} uploaded / {upDelta.RejectedUpload} rejected");
            Check("K5 an upload run contributes no download reject",
                upDelta.RejectedDownload == 0);

            // A dry upload must not zero a real download's numbers, and the other way round.
            var mixed = StatusCounterDelta.From(new RunSummary { IsDryRun = true }, dl);
            Check("K6 a dry upload does not zero a real download's rejects",
                mixed.RejectedDownload == 9 && mixed.RejectedUpload == 0);

            // Nothing happened -> the row is not touched (that is what keeps Updated meaningful).
            var nothing = StatusCounterDelta.From(new RunSummary(), new DownloadRunSummary());
            Check("K7 an empty run leaves the statistics row untouched", nothing.IsEmpty);

            // The two directions are separate fields on the row, so one cannot overwrite the other.
            var ent = new StatusStatsEntity { Id = "status" };
            ent.RejectedDownload += dlDelta.RejectedDownload;
            ent.RejectedUpload += upDelta.RejectedUpload;
            Check("K8 both directions persist into their own field",
                ent.RejectedDownload == 9 && ent.RejectedUpload == 12,
                $"-> {ent.RejectedDownload} / {ent.RejectedUpload}");
        }

        // ── L  the dry run writes no stored verdict (F-M22) ────────────────────────────
        // The rule has two halves and both were violated in the same shape: a write sat BEFORE the
        // mode's own exit, so the report-only run left state behind that changed what the next real
        // run found. These checks pin the tracker the pipelines own whose reach is longest: the
        // refetch stamp, which hides work for a whole interval. The id-resolution budget that used to
        // be pinned here as well is GONE (operator order 08.10.2026, L4–L6 below assert the removal),
        // so the stamp is the remaining stored verdict of this class.
        {
            string ldir = Path.Combine(Path.GetTempPath(), "subdl-fm22-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ldir);
            try
            {
                using var ldb = new SubdlDbContext(Path.Combine(ldir, "db"), null);
                var stamp = new DownloadSearchTracker(ldb, null);
                string itemId = Guid.NewGuid().ToString();
                var gap = TimeSpan.FromHours(24);

                // MarkSearched writes onto the item's MEDIA row (FindByItemId), so the row has to
                // exist first — without it the stamp is a silent no-op. Creating it here also
                // documents that dependency: the stamp is not a free-standing record.
                ldb.EnsureMedia("fm22hash", itemId, "/tmp/fm22.mkv");

                // A fresh item is due: the stamp is what makes it otherwise.
                Check("L1 a never-searched item is due", stamp.IsDue(itemId, gap));

                stamp.MarkSearched(itemId, new List<string> { "EN" });
                stamp.Flush();
                // The language list is part of the stamp's meaning: IsDue compares it, and a
                // DIFFERENT list counts as due on purpose (the old verdict does not cover the new
                // question). Passing the same list is what isolates the timestamp itself.
                Check("L2 a searched item is not due inside the gap",
                      !stamp.IsDue(itemId, gap, new List<string> { "EN" }),
                      "-> the stamp is what hides the item");
                Check("L2b a changed language list makes it due again",
                      stamp.IsDue(itemId, gap, new List<string> { "EN", "DE" }),
                      "-> so a dry run's stamp cannot hide a language the user adds later");

                // This is the mechanism the dry-run guard protects: had MarkSearched run in a dry
                // run, the item would read as not-due here even though nothing was ever fetched.
                stamp.ClearStamp(itemId);
                stamp.Flush();
                Check("L3 clearing the stamp makes the item due again", stamp.IsDue(itemId, gap, new List<string> { "EN" }),
                      "-> no invalidation pass exists, so a wrong stamp does not self-heal");

                // L4–L6: the id-resolution give-up is GONE, asserted as an ABSENCE on both sides —
                // the tracker's type and the `id-not-found:` key. Removing a give-up is invisible in
                // a passing run, so a positive check cannot carry this; only an absence can. Negative
                // control (measured): restoring the tracker file and the counter write turns L4 RED.
                var dlSrc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DownloadPipeline.source.cs"));
                Check("L4 the pipeline reads no id-not-found budget and records no failure",
                      !dlSrc.Contains("_idNotFound", StringComparison.Ordinal)
                      && !dlSrc.Contains("IdRetryLimit", StringComparison.Ordinal),
                      "-> an id-less item is retried by the next run, never retired");
                var upSrc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UploadPipeline.source.cs"));
                Check("L5 the upload side carries the same removal",
                      !upSrc.Contains("_idNotFound", StringComparison.Ordinal)
                      && !upSrc.Contains("IdRetryLimit", StringComparison.Ordinal));
                Check("L6 no give-up counter is reported by either summary any more",
                      !dlSrc.Contains("SkippedIdGaveUp", StringComparison.Ordinal)
                      && !upSrc.Contains("SkippedIdGaveUp", StringComparison.Ordinal),
                      "-> the count that gave items up cannot be reported either");
            }
            finally
            {
                try { Directory.Delete(ldir, true); } catch { /* best effort */ }
            }
        }

        // ── M  ONE setting, TWO effects: search width and the Auto-Sync walk's limit ──
        // (F-M95/F-M50/F-M331, operator order 08.10.2026: "Die correction attempts bestimmen wieviele
        // Kandidaten pro Sprache gesucht werden und wie oft die autosync Schleife maximal Kandidaten
        // zieht bis das Ergebnis passt. Aus 2 variables mach eine.")
        //
        // What was two knobs that described the same intent is now one setting. The old pair drifted
        // silently: the walk's cap was checked BEFORE the correction budget and counted the same
        // attempts, so at the equal defaults the cap always fired first and the budget could never
        // trigger. Replayed against the loop, the cap stopped the walk in every ordering.
        {
            // The number the search receives is the ONE configured value, unchanged — so removing the
            // second knob is not a hidden quota change for an existing install (default 3).
            Check("M1 the search width IS the configured setting",
                  DownloadBudget.SearchEarlyStopThreshold(3) == 3);
            Check("M7 a deliberate value is passed through, never lowered",
                  DownloadBudget.SearchEarlyStopThreshold(10) == 10);
            Check("M5 unlimited stays unlimited (F-M50)",
                  DownloadBudget.SearchEarlyStopThreshold(0) == 0,
                  "-> 0 is the documented 'no cap', not a threshold of zero");
            Check("M6 negative values are treated as 0, never as a threshold",
                  DownloadBudget.SearchEarlyStopThreshold(-1) == 0);

            // Both effects read the SAME setting. Asserted on the pipeline SOURCE, because a correct
            // helper that nobody calls leaves the behaviour unchanged — and this is the drift that
            // caused the original bug: the search was fed a literal while the walk read the setting.
            {
                string srcPath = Path.Combine(AppContext.BaseDirectory, "DownloadPipeline.source.cs");
                string src = File.Exists(srcPath) ? File.ReadAllText(srcPath) : "";
                Check("M10 the pipeline source is available for the structural check", src.Length > 0,
                      "-> " + srcPath);

                // Effect 1: the search. No literal, and both calls take the derived value.
                Check("M11 no hard-coded search threshold remains in the pipeline",
                      !Regex.IsMatch(src, @"ct,\s*3,\s*hearingImpaired"),
                      "-> the literal that ignored the setting");
                Check("M12 both search calls take the derived threshold",
                      Regex.Matches(src, @"ct,\s*earlyStop,\s*hearingImpaired").Count == 2,
                      "-> regular search and HI search");
                Check("M12b and it is derived from the ONE correction-attempts setting",
                      src.Contains("SearchEarlyStopThreshold(\n                _config.DownloadQaRetryLimit)",
                                   StringComparison.Ordinal),
                      "-> a second setting would be the second knob again");

                // Effect 2: the walk. Its limit is that same setting, and the removed cap is gone.
                Check("M13 the walk's limit is the correction-attempts setting",
                      src.Contains("int walkLimit = Math.Max(0, _config.DownloadQaRetryLimit);",
                                   StringComparison.Ordinal),
                      "-> not a second cap");
                Check("M13b the removed second cap is gone from the loop",
                      !src.Contains("attempts >= downloadCap", StringComparison.Ordinal),
                      "-> that cap fired first at equal values and hid the limit");
                // Only FITS are counted (operator order): a gate rejection is a verdict on the
                // candidate, not a pull the Auto-Sync worked on, and must not consume the limit.
                Check("M13c the limit counts fits only, never gate rejections",
                      src.Contains("if (walkLimit > 0 && refusalsThisRun >= walkLimit)",
                                   StringComparison.Ordinal),
                      "-> gate rejections must not spend the Auto-Sync's budget");

                // The helper's signature: the removed keepBest parameter must not reappear.
                Check("M14 the budget helper takes no second parameter",
                      !Regex.IsMatch(src, @"SearchEarlyStopThreshold\(\s*[^)]*,"),
                      "-> a second argument would be the dead keepBest parameter again");
            }
        }

        // ── summary ─────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("Fehler: " + _failed + "  (bestanden: " + _passed + ")");
        return _failed == 0 ? 0 : 1;
    }
}
