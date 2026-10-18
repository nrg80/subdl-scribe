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
//   3. THE FALLBACK IS EXECUTED TOO. The operator's library shows neither field as trustworthy: 27 files
//      carry a broken average (1000) with a correct real rate, 18 a good average with a real rate 0.05+
//      off, 11 have both mapping to different codes. CodeFor(average, real) must take the average when it
//      maps, fall back to the real rate when it does not, and prefer the average on the 11 conflicts
//      (consistency with the download gate, F-M43). Both fields are handed over once per ITEM, not per
//      uploadable track.
//   4. THE WIRING IS ON THE SOURCE. A green table with the upload still sending "0" is exactly the
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

// --- 1b. the fallback, executed (measured cases from the operator's library) ---
var pairCases = new (double? Avg, double? Real, string Want, string Why)[]
{
    (1000.0, 23.976025, "2", "broken average (one frame in 1 ms), real rate is good — 27 files in the library"),
    (1000.0, 25.0, "3", "same, PAL"),
    (29.966667, 29.916666, "4", "average carries it, real is 0.053 off — 18 files in the library"),
    (23.976025, 24.0, "2", "both map, different codes: the average wins by design — 11 files in the library"),
    (23.976025, 0.0, "2", "no real rate at all"),
    (0.0, 25.0, "3", "no average rate, real is good"),
    (null, null, "0", "nothing known"),
    (1000.0, 50.0, "0", "neither maps — stays unknown, as before"),
    (23.81, 23.81, "0", "23.81 is not in the table; decoding it as 23.976 would be a guess"),
};

foreach ((double? avg, double? real, string want, string why) in pairCases)
{
    string got = SubdlFramerate.CodeFor(avg, real);
    Check(got == want, $"CodeFor(avg {avg?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}, real {real?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}) = {want}", $"{why} — got {got}");
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
Check(pipeline.Contains("SubdlFramerate.CodeFor(video.AverageFrameRate, video.RealFrameRate)"),
      "the pipeline hands BOTH rates to SubdlFramerate (average first, real as fallback)");
Check(pipeline.Contains("episode, ct, hearingImpaired, framerate)"),
      "the computed code reaches the API call");
Check(pipeline.Contains("framerate: itemFramerate"),
      "the phase-3 upload hands over the item's code");

// The lookup is a property of the VIDEO and must run once per item, before the candidate loop —
// an item with four uploadable tracks must not look it up four times.
const string HoistDecl = "string itemFramerate = GetFramerateCode(item);";
int hoistCount = Regex.Matches(pipeline, Regex.Escape(HoistDecl)).Count;
Check(hoistCount == 1, "the frame rate is looked up exactly once per item", $"{hoistCount} occurrence(s)");
int hoistAt = pipeline.IndexOf(HoistDecl, StringComparison.Ordinal);
int phase3At = pipeline.LastIndexOf("foreach (var cand in qaSurvivors)", StringComparison.Ordinal);
Check(hoistAt >= 0 && phase3At > hoistAt,
      "the lookup sits BEFORE the upload loop, not inside it",
      hoistAt < 0 ? "no hoisted lookup" : $"lookup at {hoistAt}, loop at {phase3At}");

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
