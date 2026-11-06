// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even
// the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// T175 / F-M449: machine-translated uploads are kept out of the candidate pool
// unless the download switch allows them.
//
// WHY THE WIRING IS ASSERTED AND NOT JUST THE PREDICATE. A matcher that answers
// correctly but is never called protects nothing: the release would ship with the
// account's subtitles still being downloaded, and every unit test of the predicate
// would stay green. The checks therefore also read the pipeline, the configuration
// property and the page:
//
//   1. THE KEY. "Sub AI", "sub_ai", "Sub.AI" and "SubAI" are the same uploader.
//   2. NO FALSE POSITIVE. A name that merely starts the same way, and an empty name,
//      keep their candidates — the filter may not swallow other uploaders.
//   3. THE POOL. Remove() drops every candidate of that account, keeps the rest and
//      reports how many it dropped.
//   4. THE SWITCH. The property exists, defaults to FALSE, and the pipeline reads it.
//   5. THE ORDER. The filter sits on the search result, before the pool is ranked and
//      before anything is fetched.
//   6. THE COUNTER. Dropped candidates are counted and named in the run summary.
//   7. THE PAGE. The switch is a checkbox in the download tab and says "Default: off".
//
// Usage: dotnet run --project scripts/tests/aifilter/aifilter.csproj [repo-root]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace AiFilterRun;

internal static class AiFilterRun
{
    private static int _checks;
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        _checks++;
        Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what);
        if (!ok)
        {
            Failures.Add(what);
        }
    }

    private static string Read(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        string plugin = Path.Combine(root, "Jellyfin.Plugin.SubdlSync");

        // --- 1. the key -------------------------------------------------------
        Check(AiTranslatedSubs.IsAiUploader("SUBDL AI"), "the spaced name is the account");
        Check(AiTranslatedSubs.IsAiUploader("subdl_ai"), "the underscored name is the account");
        Check(AiTranslatedSubs.IsAiUploader("SubDL.AI"), "the dotted name is the account");
        Check(AiTranslatedSubs.IsAiUploader("SUBDLAI"), "the joined name is the account");
        Check(AiTranslatedSubs.IsAiUploader("subdl-ai "), "case, hyphen and padding do not matter");

        // --- 2. no false positive --------------------------------------------
        Check(!AiTranslatedSubs.IsAiUploader("subdlmaker"), "a neighbouring account is NOT the AI account");
        Check(!AiTranslatedSubs.IsAiUploader("DrMohamed"), "an ordinary uploader keeps its candidates");
        Check(!AiTranslatedSubs.IsAiUploader(null) && !AiTranslatedSubs.IsAiUploader("   "), "an absent name is not the account");

        // --- 3. the pool ------------------------------------------------------
        var pool = new List<SubtitleCandidate>
        {
            new() { Author = "DrMohamed", Language = "EN" },
            new() { Author = "SUBDL AI", Language = "DE" },
            new() { Author = "saxec", Language = "EN" },
            new() { Author = "subdl_ai", Language = "FR" },
        };
        int dropped = AiTranslatedSubs.Remove(pool);
        Check(dropped == 2, $"both machine-translated candidates are removed (dropped {dropped})");
        Check(pool.Count == 2 && pool.All(c => !AiTranslatedSubs.IsAiUploader(c.Author)),
            "the other uploaders survive the filter");
        Check(AiTranslatedSubs.Remove(null) == 0, "an absent pool drops nothing");

        // --- 4. the switch ----------------------------------------------------
        string config = Read(plugin, "Configuration/PluginConfiguration.cs");
        Check(Regex.IsMatch(config, @"public\s+bool\s+AllowAiTranslatedSubs\s*\{\s*get;\s*set;\s*\}\s*=\s*false\s*;"),
            "the switch exists and defaults to off");
        string pipeline = Read(plugin, "Pipeline/DownloadPipeline.cs");
        Check(pipeline.Contains("AllowAiTranslatedSubs", StringComparison.Ordinal),
            "the download pipeline reads the switch");
        Check(Regex.IsMatch(pipeline, @"if\s*\(\s*!_config\.AllowAiTranslatedSubs\s*\)"),
            "the filter runs only while the switch is OFF");

        // --- 5. the order -----------------------------------------------------
        int filterAt = pipeline.IndexOf("AiTranslatedSubs.Remove", StringComparison.Ordinal);
        int rankAt = pipeline.IndexOf("weighted release score, best candidate first", StringComparison.Ordinal);
        int fetchAt = pipeline.IndexOf("DownloadCandidateAsync(cand", StringComparison.Ordinal);
        Check(filterAt > 0 && rankAt > 0 && filterAt < rankAt,
            "the filter sits before the ranking picks a best candidate");
        Check(fetchAt > 0 && filterAt < fetchAt,
            "the filter sits before anything is fetched for this item");

        // --- 6. the counter ---------------------------------------------------
        Check(Regex.IsMatch(pipeline, @"summary\.AiUploaderFiltered\s*\+="),
            "dropped candidates are counted");
        Check(pipeline.Contains("{Ai} AI uploader filtered", StringComparison.Ordinal),
            "the run summary names the count");

        // --- 7. the page ------------------------------------------------------
        string page = Read(plugin, "Configuration/configPage.html");
        int gateAt = page.IndexOf("Quality gates (before download save)", StringComparison.Ordinal);
        int switchAt = page.IndexOf("id=\"AllowAiTranslatedSubs\"", StringComparison.Ordinal);
        Check(switchAt > 0, "the page carries the switch");
        Check(gateAt > 0 && switchAt > gateAt,
            "the switch sits inside the download quality-gate block");
        Check(page.Contains("Allow AI translated subs (SUBDL AI)", StringComparison.Ordinal),
            "the label names the account");
        string tail = switchAt > 0 ? page.Substring(switchAt, Math.Min(600, page.Length - switchAt)) : string.Empty;
        Check(tail.Contains("Default: off", StringComparison.Ordinal),
            "the description states the default");
        Check(page.Contains("config.AllowAiTranslatedSubs = document.querySelector('#AllowAiTranslatedSubs').checked", StringComparison.Ordinal)
              && page.Contains("document.querySelector('#AllowAiTranslatedSubs').checked = config.AllowAiTranslatedSubs === true", StringComparison.Ordinal),
            "the switch is loaded and saved with the page");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"aifilter: {_checks} checks, all green."
            : $"aifilter: {Failures.Count} of {_checks} checks FAILED:");
        foreach (string f in Failures)
        {
            Console.WriteLine("  - " + f);
        }

        return Failures.Count == 0 ? 0 : 1;
    }
}
