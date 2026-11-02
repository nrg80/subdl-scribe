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

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"packretry: {_checks} checks, all green."
            : $"packretry: {_checks} checks, {Failures.Count} FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
