// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// T152 / F-M344: the upload reports the SOURCE quality of the subtitle.
//
// THREE THINGS ARE ASSERTED, and the first two are why this harness is not a unit test of a pure
// function alone:
//
//   1. THE VALUE LIST IS SUBDL's. The API document lists exactly web, bluray, dvd, hdtv and cam for
//      the upload field `quality`. Asserted against SourceQuality.Allowed, so a later edit cannot
//      invent a value — the server accepted an unknown `client` value silently, and this field
//      cannot be assumed to be any more forgiving just because the failure would be invisible.
//   2. THE RECOGNITION RULE ON A NAME TABLE, including the cases that would be FALSE POSITIVES if
//      the tables were built from substrings: a film called "Cambridge" or "Camera Obscura" must
//      NOT become a CAM release, while a bare `DVD` token (the Dinosaurs "480p DVD x265 Panda"
//      episodes) must. The fallback is asserted too: unrecognized stays `web`, which is exactly
//      what shipped before the change, so a name this code cannot place cannot regress.
//   3. THE WIRING IS ON THE SOURCE. The value has to reach the wire: the pipeline computes it from
//      the subtitle's own name (sidecar) or the container's, and the client sends the PARAMETER
//      instead of the old literal. A pure unit test of Detect() would be green while the upload
//      still sent "web" — the exact shape of the bug this replaced.
//
// Usage: dotnet run --project quality.csproj [repo-root]
//        (repo-root defaults to /opt/data/subdl-scribe, where the sources are read from)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

int failures = 0;

void Check(bool ok, string what, string detail = "")
{
    Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what + (detail.Length > 0 ? "  [" + detail + "]" : ""));
    if (!ok)
    {
        failures++;
    }
}

string repo = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
string clientPath = Path.Combine(repo, "Jellyfin.Plugin.SubdlSync", "Api", "SubdlApiClient.cs");
string pipelinePath = Path.Combine(repo, "Jellyfin.Plugin.SubdlSync", "Pipeline", "UploadPipeline.cs");

// Whitespace is collapsed before any source assertion: a re-indent must not turn a check red,
// and a comment mentioning the old literal must not turn it green either (the check runs on the
// code text, and the literal is asserted absent as an ASSIGNMENT, not as a word).
static string Flatten(string s)
{
    return Regex.Replace(s, "\\s+", " ");
}

Console.WriteLine("T152 / F-M344 — upload source quality");
Console.WriteLine();

// --- 1. the documented value list ---------------------------------------------------------------
string[] documented = { "web", "bluray", "dvd", "hdtv", "cam" };
Check(
    SourceQuality.Allowed.OrderBy(x => x).SequenceEqual(documented.OrderBy(x => x)),
    "the value list is the documented one",
    string.Join(",", SourceQuality.Allowed));
Check(
    SourceQuality.Allowed.Distinct().Count() == SourceQuality.Allowed.Count,
    "no value appears twice");

// --- 2. the name table -------------------------------------------------------------------------
// Real names from the operator's library first: the two that motivated the change.
(string Name, string Expect)[] table =
{
    ("Dark.Matter.2024.S02E07.The.Pyramid.1080p.WEBRip.10Bit.DDP5.1.x265-NeoNoir.mkv", "web"),
    ("Dinosaurs (1991) - S01E01 - The Mighty Megalosaurus (480p DVD x265 Panda).mkv", "dvd"),

    ("Movie.2019.1080p.BluRay.x264-GROUP.mkv", "bluray"),
    ("Movie.2019.1080p.Blu-Ray.x264-GROUP.mkv", "bluray"),
    ("Movie.2019.1080p.BDRip.x264-GROUP.mkv", "bluray"),
    ("Movie.2019.1080p.BRRip.x264-GROUP.mkv", "bluray"),
    ("Movie.2019.2160p.UHD.BDREMUX.HDR.HEVC-GROUP.mkv", "bluray"),

    ("Show.S01E01.720p.HDTV.x264-GROUP.mkv", "hdtv"),
    ("Show.S01E01.1080i.PDTV.x264-GROUP.mkv", "hdtv"),

    ("Movie.2019.CAM.XviD-GROUP.avi", "cam"),
    ("Movie.2019.1080p.HDCAM.x264-GROUP.mkv", "cam"),
    ("Movie.2019.HD.TELESYNC.XviD-GROUP.avi", "cam"),

    ("Movie.2019.1080p.DVDRip.XviD-GROUP.avi", "dvd"),
    ("Movie.2019.PAL.DVD5.x264-GROUP.iso", "dvd"),

    ("Movie.2024.2160p.AMZN.WEB-DL.DDP5.1.HDR.x265-GROUP.mkv", "web"),
    ("Movie.2024.1080p.DSNP.WEB-DL.DDP5.1.H.264-GROUP.mkv", "web"),
    ("Movie.2024.1080p.NF.WEBRip.DDP5.1.x264-GROUP.mkv", "web"),

    // A name with nothing to read: the fallback IS the old behaviour, so it must stay web.
    ("Some.Film.Without.Any.Source.Token.mkv", "web"),
    ("holiday-video-2024.mp4", "web"),

    // The false positives the tables must not produce: a title containing "cam" as letters, and a
    // disc word that belongs to the TITLE (the last source word in the name decides).
    ("Cambridge.2019.1080p.WEB-DL.x264-GROUP.mkv", "web"),
    ("Camera.Obscura.2019.1080p.BluRay.x264-GROUP.mkv", "bluray"),
    ("The.DVD.Club.2019.1080p.BluRay.x264-GROUP.mkv", "bluray"),
};

foreach ((string name, string expect) in table)
{
    string got = SourceQuality.Detect(name);
    Check(got == expect, $"Detect: {name}", $"expected {expect}, got {got}");
    Check(SourceQuality.Allowed.Contains(got), $"… and the answer is a documented value: {got}");
}

// Paths must be accepted like plain names, and a sidecar's own name must win over the container's —
// but only when it says something.
Check(SourceQuality.Detect("/data/movies/Dinosaurs.1991/S01/e01 (480p DVD x265 Panda).mkv") == "dvd", "a full path is reduced to its name");
Check(SourceQuality.Detect("Film.2019.1080p.BluRay.srt", "Film.2019.1080p.WEB-DL.mkv") == "bluray", "the sidecar's own name wins");
Check(SourceQuality.Detect("Film.mkv.srt", "Film.2019.1080p.BluRay.mkv") == "bluray", "a silent sidecar name falls through to the media file");
Check(SourceQuality.Detect(null, "Film.2019.1080p.BluRay.mkv") == "bluray", "an embedded track has no own name — the container is read");
Check(SourceQuality.Detect("", "") == "web", "empty input is web, not a crash");

// --- 3. the wiring, on the source ---------------------------------------------------------------
Check(File.Exists(clientPath), "client source is readable", clientPath);
Check(File.Exists(pipelinePath), "pipeline source is readable", pipelinePath);

string client = Flatten(File.ReadAllText(clientPath));
string pipeline = Flatten(File.ReadAllText(pipelinePath));

Check(
    client.Contains("[\"quality\"] = quality,", StringComparison.Ordinal),
    "the metadata step sends the PARAMETER, not a literal");
Check(
    !client.Contains("[\"quality\"] = \"web\"", StringComparison.Ordinal),
    "the old hardcoded literal is gone");
Check(
    client.Contains("string releaseName, string quality,", StringComparison.Ordinal),
    "the parameter is required in the signature (no silent default)");

Check(
    pipeline.Contains("string quality = SourceQuality.Detect(loosePath, mediaPath);", StringComparison.Ordinal),
    "the pipeline reads the value from the subtitle's own name, the container as fallback");
Check(
    pipeline.Contains("UploadSubtitleAsync(srtContent, neutralName, lang, releaseName, quality,", StringComparison.Ordinal),
    "… and passes it into the upload call");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL GREEN" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
