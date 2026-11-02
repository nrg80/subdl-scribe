// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR
// PURPOSE. See the GNU General Public License for more details.
//
// T162 / F-M387 (operator order 02.11.2026): a QA gate judges a FILE, not a candidate.
//
// THE CASE: a season pack can carry several files for the same episode — a plain one and an SDH
// one, or two release variants. The walk used to take the first name match, and a gate verdict on
// it threw the whole candidate away, although a second file for that very episode was sitting in
// the same, already paid-for archive. Two rules are asserted here:
//
//   1. every matching file of the archive is offered to the gates, in listing order;
//   2. SDH is matched with SDH only — a plain request never falls back to an SDH file, and an SDH
//      request never to a plain one. Swapping them silently changes what the viewer gets.
//
// Usage: dotnet build Jellyfin.Plugin.SubdlSync -c Release
//        dotnet run --project scripts/tests/packretry/packretry.csproj -- [repo-root]

using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Jellyfin.Plugin.SubdlScribe.Registry;

namespace PackRetryRun;

internal static class Program
{
    private static int _checks;
    private static readonly System.Collections.Generic.List<string> Failures = new();

    private static void Check(bool ok, string what, string actual = "")
    {
        _checks++;
        if (!ok)
        {
            Failures.Add(what);
            Console.WriteLine("  FAIL  " + what + (actual.Length > 0 ? ": " + actual : string.Empty));
        }
    }

    private static SubtitleUnpackFile Member(string name, int season, int episode, bool hi)
        => new()
        {
            Name = name,
            Url = "/subtitle/Xhfe8mD1w5/" + name.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Season = season,
            Episode = episode,
            HearingImpaired = hi
        };

    private static SubtitleCandidate Pack()
    {
        var cand = new SubtitleCandidate
        {
            ReleaseName = "The.Middle.S05.HDTV.XviD-FUM",
            Url = "https://dl.subdl.com/subtitle/3752887-8674923.zip",
            Language = "EN",
            Season = 5,
            FullSeason = true
        };

        // Two plain files for E05 (two release variants) and one SDH file — the exact shape the
        // retry exists for.
        cand.UnpackFiles.Add(Member("The.Middle.S05E05.HDTV.XviD-FUM.srt", 5, 5, hi: false));
        cand.UnpackFiles.Add(Member("The.Middle.S05E05.720p.HDTV.x264.srt", 5, 5, hi: false));
        cand.UnpackFiles.Add(Member("The.Middle.S05E05.HDTV.XviD-FUM.sdh.srt", 5, 5, hi: true));
        cand.UnpackFiles.Add(Member("The.Middle.S05E06.HDTV.XviD-FUM.srt", 5, 6, hi: false));
        return cand;
    }

    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        var cand = Pack();

        // ── 1. Every matching file is offered, in listing order ─────────────────
        Console.WriteLine("1. The retry list");
        var plain = cand.FindUnpackFiles(5, 5, hearingImpaired: false);
        Check(plain.Count == 2, "two plain files for E05 are offered", plain.Count.ToString());
        Check(plain[0].Name == "The.Middle.S05E05.HDTV.XviD-FUM.srt",
            "the first listed file comes first", plain.FirstOrDefault()?.Name ?? "(none)");
        Check(plain.All(f => !f.HearingImpaired), "and none of them is an SDH file");
        Check(cand.FindUnpackFile(5, 5) == plain[0],
            "FindUnpackFile still answers with the first of them (no behaviour change for callers)");

        // ── 2. SDH only with SDH ─────────────────────────────────────────────────
        Console.WriteLine("2. SDH is matched with SDH only");
        var sdh = cand.FindUnpackFiles(5, 5, hearingImpaired: true);
        Check(sdh.Count == 1, "exactly one SDH file for E05", sdh.Count.ToString());
        Check(sdh[0].Name.EndsWith(".sdh.srt", StringComparison.Ordinal),
            "and it is the SDH one", sdh[0].Name);
        Check(sdh.All(f => f.HearingImpaired), "a plain request never sees it, an SDH request never sees a plain file");
        Check(cand.FindUnpackFiles(5, 5).Count == 3, "without the filter all three remain visible",
            cand.FindUnpackFiles(5, 5).Count.ToString());

        // ── 3. Nothing else leaks in ────────────────────────────────────────────
        Console.WriteLine("3. Episode boundaries");
        Check(cand.FindUnpackFiles(5, 6, hearingImpaired: false).Count == 1,
        "E06 has its own single file");
        Check(cand.FindUnpackFiles(5, 7).Count == 0, "an episode the pack does not carry yields nothing");

        // ── 4. Wiring: the retry exists and is fed from the cache ───────────────
        Console.WriteLine("4. Source contract");
        var api = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/SubdlApiClient.cs"));
        Check(api.Contains("public List<SubtitleUnpackFile> FindUnpackFiles(int season, int episode, bool? hearingImpaired = null)", StringComparison.Ordinal),
            "the candidate publishes the retry list with the HI filter");
        Check(api.Contains("f.HearingImpaired == hearingImpaired.Value", StringComparison.Ordinal),
            "the filter compares the file's OWN flag, not the pack header");
        Check(api.Contains("public async Task<byte[]?> ExtractArchiveMemberAsync(string? archiveUrl, string? memberName, CancellationToken ct)", StringComparison.Ordinal),
            "the client can take a named member out of an archive");
        Check(api.Contains("GetArchiveBodyAsync(fullUrl, ct)", StringComparison.Ordinal)
              && api.Split("GetArchiveBodyAsync(").Length - 1 >= 3,
            "both the entry download and the member retry read through the ONE cached path");
        Check(api.Contains("string? memberName = null)", StringComparison.Ordinal)
              && api.Contains("string.Equals(e.Name, memberName, StringComparison.OrdinalIgnoreCase)", StringComparison.Ordinal),
            "the unpacker extracts the named entry, never a guessed one");

        var pipe = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        Check(pipe.Contains("string? QaGateFailure(byte[] candidateBytes)", StringComparison.Ordinal),
            "the four content gates live in one function");
        Check(pipe.Contains("cand.FindUnpackFiles(season, episode, effectiveHi)", StringComparison.Ordinal),
            "the retry walks the archive's files with the wanted HI flag");
        Check(pipe.Contains(".ExtractArchiveMemberAsync(cand.Url, sibling.Name, ct)", StringComparison.Ordinal),
            "and takes them out of the archive it already holds");
        Check(pipe.Contains("next file of the same archive accepted", StringComparison.Ordinal),
            "an accepted sibling is named in the log, not swapped in silently");
        Check(pipe.Split("summary.RejectedCandidates++;").Length - 1 >= 2,
            "a candidate is still counted as rejected after its files were tried");
        Check(pipe.Contains("no usable file left in this archive", StringComparison.Ordinal),
            "and the reject line says the archive was exhausted, not that one file failed");

        // ── 5. F-M388: the download drops invisible cues too ────────────────────
        // MobLand's real defect: a duplicate line timed 00:00:03,898 → 00:00:03,899, a cue of 1 ms.
        // On the upload side it is dropped since F-M382; on the download side it used to convict the
        // file. Both sides now use one rule, and — the operator's condition — the STATE HASH is taken
        // over the corrected text, so it describes the bytes that end up on disk.
        Console.WriteLine("5. Invisible cues on the download side (F-M388)");
        const string dirty =
            "1\r\n00:00:01,000 --> 00:00:03,000\r\nHello there.\r\n\r\n" +
            "2\r\n00:00:03,898 --> 00:00:03,899\r\n♪ MTV ♪\r\n\r\n" +
            "3\r\n00:00:04,000 --> 00:00:06,000\r\nSecond line.\r\n\r\n" +
            "4\r\n00:00:07,000 --> 00:00:07,098\r\n♪\r\n";
        byte[] dirtyPayload = System.Text.Encoding.UTF8.GetBytes(dirty); // ♪ is three bytes, one char
        byte[] dirtyBytes = new byte[dirtyPayload.Length + 3];
        dirtyBytes[0] = 0xEF; dirtyBytes[1] = 0xBB; dirtyBytes[2] = 0xBF; // BOM, CRLF — the real payload style
        dirtyPayload.CopyTo(dirtyBytes, 3);

        byte[]? corrected = DownloadPipeline.CorrectInvisibleCues(dirtyBytes);
        string correctedText = corrected == null ? string.Empty : System.Text.Encoding.UTF8.GetString(corrected);
        Check(corrected != null && corrected[0] != 0xEF, "the BOM is gone", corrected == null ? "(null)" : corrected[0].ToString());
        Check(!correctedText.Contains('\r', StringComparison.Ordinal), "line endings are LF");
        Check(!correctedText.Contains("MTV", StringComparison.Ordinal), "the 1 ms cue is dropped");
        Check(!correctedText.Contains("00:00:07,098", StringComparison.Ordinal), "and the 98 ms one as well");
        Check(correctedText.Contains("Hello there.", StringComparison.Ordinal)
              && correctedText.Contains("Second line.", StringComparison.Ordinal),
            "the readable cues survive");
        Check(correctedText.StartsWith("1\n00:00:01,000", StringComparison.Ordinal)
              && correctedText.Contains("\n2\n00:00:04,000", StringComparison.Ordinal),
            "and what is left is renumbered 1, 2 …", correctedText.Split('\n').FirstOrDefault() ?? "");

        // The operator's condition, as an assertion: md5 over the CORRECTED bytes is the md5 the
        // registry stores for the state.
        string hashOfCorrected = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(corrected!)).ToLowerInvariant();
        Check(hashOfCorrected == ContentHashRegistry.ComputeHash(dirty),
            "the state hash describes the corrected file, not the fetched one",
            hashOfCorrected + " vs " + ContentHashRegistry.ComputeHash(dirty));
        Check(hashOfCorrected != Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(dirtyBytes)).ToLowerInvariant(),
            "and it is not the hash of the untouched payload");

        // A clean file keeps its cues and its text — only the canonical byte style is applied, the
        // very form the state hash describes (no trailing blank line, the upload side has always
        // trimmed it). Nothing is dropped, so nothing shifts.
        const string clean = "1\n00:00:01,000 --> 00:00:03,000\nHello there.\n";
        byte[] cleanBytes = System.Text.Encoding.UTF8.GetBytes(clean);
        string cleanOut = System.Text.Encoding.UTF8.GetString(DownloadPipeline.CorrectInvisibleCues(cleanBytes)!);
        Check(cleanOut == "1\n00:00:01,000 --> 00:00:03,000\nHello there.",
            "a file that needs nothing keeps every cue, only the canonical ending is applied", cleanOut);
        Check(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                  System.Text.Encoding.UTF8.GetBytes(cleanOut))).ToLowerInvariant()
              == ContentHashRegistry.ComputeHash(clean),
            "and its state hash is unchanged by the correction");
        Check(DownloadPipeline.CorrectInvisibleCues(null) == null, "and nothing stays nothing");

        // ── 6. And the correction happens BEFORE the gates and before the write ─
        Console.WriteLine("6. Order of operations");
        var wrapper = pipe.IndexOf("DownloadCandidateRawAsync(", StringComparison.Ordinal);
        var gate = pipe.IndexOf("string? gateFail = QaGateFailure(bytes);", StringComparison.Ordinal);
        Check(wrapper > 0 && pipe.Contains("byte[]? corrected = CorrectInvisibleCues(raw);", StringComparison.Ordinal),
            "the fetched payload is corrected in the download itself");
        Check(gate > 0, "the gates run on the corrected bytes");
        Check(pipe.Contains("byte[] writeBytes = bytes;", StringComparison.Ordinal)
              && pipe.Contains("ComputeHash(content)", StringComparison.Ordinal),
            "the write and the state hash take the same, already corrected bytes");
        Check(pipe.Contains("siblingBytes = CorrectInvisibleCues(siblingBytes)", StringComparison.Ordinal),
            "a retried sibling of the archive is corrected as well");
        Check(pipe.Contains("var hiBytes = CorrectInvisibleCues(", StringComparison.Ordinal),
            "so is the hearing-impaired file");

        // ── 7. F-M390: the pack becomes a fact in the store ─────────────────────
        Console.WriteLine("7. The packs table");
        var reg = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        var coord = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
        Check(reg.Contains("public void RecordPack(", StringComparison.Ordinal), "the registry can record a pack");
        Check(reg.Contains("public bool HasPack(", StringComparison.Ordinal), "and answer whether one exists");
        Check(coord.Contains("_registry.RecordPack(", StringComparison.Ordinal), "a successful pack writes its row");
        Check(coord.Contains("ordered.Select(x => x.Episode)", StringComparison.Ordinal),
            "the row carries the member episodes");
        // The planner lives in the UPLOAD pipeline — reading download source here would assert about
        // a file that never contained the line.
        var upload = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check(upload.Contains("Registry.HasPack(entry.Key.Item1, entry.Key.Item2, pair.Key.Lang, pair.Key.Hi)", StringComparison.Ordinal),
            "and the fill-only plan skips a season that is already packed");
        Check(upload.IndexOf("Registry.HasPack(", StringComparison.Ordinal) < upload.IndexOf("already inside a pack, nothing is re-sent", StringComparison.Ordinal),
            "the check comes before the decision it guards");

        // ── 8. F-M391: the duplicate goes first, the pack second ────────────────
        Console.WriteLine("8. Delete-first absorption");
        Check(api.Contains("public async Task<bool> DeletePublishedSubtitleAsync(string subtitleId, CancellationToken ct)", StringComparison.Ordinal),
            "the client can delete one published entry by id");
        Check(api.Contains("/user/deletePublishedSubtitle?id=", StringComparison.Ordinal),
            "over the published route — deleteSubtitle answers true and deletes nothing");
        Check(coord.Contains("DeletePublishedSubtitleAsync(single.Id, ct)", StringComparison.Ordinal),
            "the coordinator deletes the absorbed single");
        Check(coord.IndexOf("DeletePublishedSubtitleAsync(single.Id, ct)", StringComparison.Ordinal)
              < coord.IndexOf("UploadSeasonPackAsync(", StringComparison.Ordinal),
            "BEFORE it uploads the pack, not after");
        Check(coord.Contains("deletedIds.Count > 0 ? deletedIds : null", StringComparison.Ordinal),
            "the deleted ids are recorded on the pack row");
        Check(upload.Contains("absorbedSingleId: absorbed", StringComparison.Ordinal),
            "and the filler hands its single's id to the candidate");

        // ── 9. F-M392: the brake comes before the deletions ─────────────────────
        Console.WriteLine("9. Pack budget per run");
        Check(coord.Contains("_config.MaxPacksPerRun > 0 && _packsThisRun >= _config.MaxPacksPerRun", StringComparison.Ordinal),
            "the run counts packs against a budget");
        Check(coord.IndexOf("_config.MaxPacksPerRun > 0", StringComparison.Ordinal)
              < coord.IndexOf("DeletePublishedSubtitleAsync(single.Id, ct)", StringComparison.Ordinal),
            "and the budget is checked BEFORE the first deletion");
        Check(coord.Contains("_packsThisRun++;", StringComparison.Ordinal), "a landed pack is counted");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"packretry: {_checks} checks, all green."
            : $"packretry: {_checks} checks, {Failures.Count} FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
