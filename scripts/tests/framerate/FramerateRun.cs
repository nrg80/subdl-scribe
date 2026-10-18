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
//
// T156 / F-M350: the upload reports the FRAME RATE CODE of the item's video.
//
// THREE THINGS ARE ASSERTED, and the first two are executed rather than read:
//
//   1. THE TABLE IS SUBDL'S. Code 0 = default/unknown, 2 = 23.976, 3 = 25, 4 = 29.97, 5 = 24,
//      7 = 30 — the mapping the download side already parses, confirmed against the live search API
//      (120 entries: code 2 always with fps 23.976, code 0 always with fps null). CodeFor() is
//      called for every one of those rates plus float noise, and for the rates the table does NOT
//      know (48, 50, 59.94, 60): those must stay 0, because that is what shipped before and a rate
//      outside the table cannot be claimed.
//   2. NO INVENTED CODE ESCAPES. Every result must be in SubdlFramerate.Allowed, and Allowed must
//      be exactly those six values — the server accepted an unknown `client` value silently for the
//      quality field, so an invented code here would fail invisibly.
//   3. THE WIRING IS ON THE SOURCE. A green table with the upload still sending "0" is exactly the
//      bug this replaced (the hardcoded quality of F-M344). So the client must send the PARAMETER,
//      the pipeline must compute it from the item's video stream, and the phase-3 call must hand
//      that value over. `production_type` is asserted to remain the literal "0": SubDL documents no
//      value list for it, and guessing an enum is worse than an explicit default.
//
// Usage: dotnet build Jellyfin.Plugin.SubdlSync -c Release
//        dotnet run --project scripts/tests/framerate/framerate.csproj [repo-root]

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

// Whitespace is collapsed before any source assertion: a re-indent must not turn a check red, and a
// comment that merely mentions a literal must not turn it green.
static string Flatten(string s) => Regex.Replace(s, "\\s+", " ");

// --- 1. the table, executed ---------------------------------------------------------------
var cases = new (double? Fps, string Want, string Why)[]
{
    (23.976, "2", "23.976 — the common film rate"),
    (23.976023976, "2", "23.976 with container float noise"),
    (24.0, "5", "24 must NOT be folded into 23.976"),
    (25.0, "3", "25 — PAL"),
    (29.97, "4", "29.97 — NTSC"),
    (29.97002997, "4", "29.97 with container float noise"),
    (30.0, "7", "30 must NOT be folded into 29.97"),
    (23.99, "5", "nearest wins: 23.99 is closer to 24 than to 23.976"),
    (null, "0", "no rate known"),
    (0.0, "0", "Jellyfin reports 0 for unknown"),
    (-1.0, "0", "nonsense"),
    (double.NaN, "0", "NaN"),
    (double.PositiveInfinity, "0", "infinity"),
    (47.952, "0", "48-ish: not in the table"),
    (50.0, "0", "50 — not in the table, stays unknown"),
    (59.94, "0", "59.94 — not in the table"),
    (60.0, "0", "60 — not in the table"),
};

foreach ((double? fps, string want, string why) in cases)
{
    string got = SubdlFramerate.CodeFor(fps);
    Check(got == want, $"CodeFor({(fps.HasValue ? fps.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}) = {want}", $"{why} — got {got}");
}

// --- 2. no invented code -----------------------------------------------------------------
string[] documented = { "0", "2", "3", "4", "5", "7" };
Check(SubdlFramerate.Allowed.OrderBy(x => x).SequenceEqual(documented.OrderBy(x => x)),
      "Allowed is exactly SubDL's six codes",
      string.Join(",", SubdlFramerate.Allowed));
Check(cases.All(c => SubdlFramerate.Allowed.Contains(SubdlFramerate.CodeFor(c.Fps))),
      "every produced code is in Allowed");

// --- 3. the wiring, on the source ---------------------------------------------------------
string client = Flatten(File.ReadAllText(clientPath));
string pipeline = Flatten(File.ReadAllText(pipelinePath));

Check(client.Contains("[\"framerate\"] = framerate"), "the client sends the framerate PARAMETER");
Check(!client.Contains("[\"framerate\"] = \"0\""), "the old hardcoded framerate literal is gone");
Check(client.Contains("string framerate = Pipeline.SubdlFramerate.Unknown"),
      "the parameter defaults to SubDL's unknown code, not to a guessed rate");
Check(pipeline.Contains("SubdlFramerate.CodeFor(videoFps)"),
      "the pipeline maps the video rate through SubdlFramerate");
Check(pipeline.Contains("episode, ct, hearingImpaired, framerate)"),
      "the computed code reaches the API call");
Check(pipeline.Contains("videoFps: GetVideoFps(item)"),
      "the phase-3 upload hands over the item's video rate");
Check(pipeline.Contains("AverageFrameRate != 0") && pipeline.Contains("RealFrameRate != 0"),
      "GetVideoFps reads the average rate first, the real rate second");

// production_type is a deliberate non-goal: no documented value list, so it stays "0".
Check(client.Contains("[\"production_type\"] = \"0\""),
      "production_type stays an explicit default (no documented value list)");

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine(">>> FRAMERATE OK: alle Pruefungen bestanden");
    return 0;
}

Console.WriteLine($">>> FRAMERATE FAIL: {failures} rot");
return 1;
