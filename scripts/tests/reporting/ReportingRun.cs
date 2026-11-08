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
// T176 / F-M450: reporting a broken subtitle to SubDL.
//
// WHY THE WIRING IS READ AND NOT ONLY THE TEXT. A correct report text that is never built protects
// nothing, and a report filed on a single observation is a complaint, not a diagnosis. The checks
// therefore cover the four things that can silently go wrong:
//
//   1. THE TEXT. Bare finding, no request, no measurement history — exactly the wording that was
//      agreed. The attribution names plugin version and account, never the login mail.
//   2. THE CATEGORY. Incomplete subtitle, wrong metadata, or "other" — decided by the finding, and
//      an unclassifiable finding is not reported at all.
//   3. THE SECOND SIGHTING. First failure only remembers; the report is filed when the same
//      candidate is seen failing again. A rate-limited report stays open for the next run.
//   4. THE SWITCH. On by default, in the download quality gates, second to last — the runtime
//      tolerance stays the last field.
//
// Usage: dotnet run --project scripts/tests/reporting/reporting.csproj [repo-root]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace ReportingRun;

internal static class ReportingRun
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
        string path = Path.Combine(root, relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string? Comment(string? finding) =>
        BrokenSubtitleReports.Build("3767750-8689923", "Breaking.Bad.S04E01.Box.Cutter.WEB-DL", "en", finding,
            "12.1.12.343", "midevildynasty")?.Comment;

    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        string plugin = Path.Combine(root, "Jellyfin.Plugin.SubdlSync");

        // --- 1. the text ------------------------------------------------------
        string empty = Comment(BrokenSubtitleReports.EmptyPayload(0)) ?? string.Empty;
        Check(empty.StartsWith("Package 3767750-8689923 ('Breaking.Bad.S04E01.Box.Cutter.WEB-DL', EN) downloads with 0 bytes.",
                StringComparison.Ordinal),
            "an empty answer is named with the byte count, not described");
        Check(empty.Contains("Automated report by SubDL Scribe (Jellyfin plugin) v12.1.12.343, account midevildynasty.", StringComparison.Ordinal),
            "the attribution carries plugin version and account");
        Check(!empty.Contains("attempt", StringComparison.OrdinalIgnoreCase)
              && !empty.Contains("please", StringComparison.OrdinalIgnoreCase)
              && !empty.Contains("verified", StringComparison.OrdinalIgnoreCase),
            "no measurement history and no request in the text");

        Check((Comment(BrokenSubtitleReports.NoCues) ?? string.Empty).Contains(": no usable cues.", StringComparison.Ordinal),
            "a defective structure says what is wrong");
        Check((Comment(BrokenSubtitleReports.NonMonotonic) ?? string.Empty).Contains(": non-monotonic timestamps.", StringComparison.Ordinal),
            "backwards timestamps are named as such");
        Check((Comment(BrokenSubtitleReports.FewCues(7)) ?? string.Empty).Contains(": 7 cues.", StringComparison.Ordinal),
            "a stub is named with its cue count");
        Check((Comment(BrokenSubtitleReports.RuntimeMismatch(2433, 1290)) ?? string.Empty).Contains(": cue span 2433s vs. runtime 1290s.", StringComparison.Ordinal),
            "a runtime mismatch is named with both numbers");
        Check((Comment(BrokenSubtitleReports.WrongLanguage("EN")) ?? string.Empty)
                .StartsWith("Package 3767750-8689923 ('Breaking.Bad.S04E01.Box.Cutter.WEB-DL'): listed as EN, contains EN.", StringComparison.Ordinal),
            "a wrong language is named without repeating the language in the package line");

        // --- 2. the category --------------------------------------------------
        Check(BrokenSubtitleReports.Build("1-2", "r", "DE", BrokenSubtitleReports.EmptyPayload(0), "v", "a")?.Category
              == BrokenSubtitleReports.CategoryIncomplete, "an empty package counts as an incomplete subtitle");
        Check(BrokenSubtitleReports.Build("1-2", "r", "DE", BrokenSubtitleReports.NoCues, "v", "a")?.Category
              == BrokenSubtitleReports.CategoryIncomplete, "a defective structure counts as an incomplete subtitle");
        Check(BrokenSubtitleReports.Build("1-2", "r", "DE", BrokenSubtitleReports.RuntimeMismatch(10, 99), "v", "a")?.Category
              == BrokenSubtitleReports.CategoryOther, "a runtime mismatch lands in \"other\"");
        Check(BrokenSubtitleReports.Build("1-2", "r", "DE", BrokenSubtitleReports.WrongLanguage("FR"), "v", "a")?.Category
              == BrokenSubtitleReports.CategoryMetadata, "a wrong language is wrong metadata");
        Check(BrokenSubtitleReports.Build("1-2", "r", "DE", "something else entirely", "v", "a") == null,
            "an unclassifiable finding is not reported");
        Check(BrokenSubtitleReports.Build(string.Empty, "r", "DE", BrokenSubtitleReports.NoCues, "v", "a") == null,
            "without a SubDL id nothing is reported");
        Check(BrokenSubtitleReports.Build("1-2", "r", "DE", BrokenSubtitleReports.WrongLanguage("FR"), "v", "a") != null
              && BrokenSubtitleReports.Build("1-2", "r", string.Empty, BrokenSubtitleReports.WrongLanguage("FR"), "v", "a") == null,
            "a language report needs the language it was requested for");

        // --- 2b. the gate wording becomes the finding -------------------------
        Check(BrokenSubtitleReports.FindingFromGateFailure("language EN") == BrokenSubtitleReports.WrongLanguage("EN"),
            "the language gate's verdict becomes the finding");
        Check(BrokenSubtitleReports.FindingFromGateFailure("structure (no valid cues / non-monotonic / implausible duration)")
              == BrokenSubtitleReports.NoCues, "the structure gate's verdict becomes the finding");
        Check(BrokenSubtitleReports.FindingFromGateFailure("min-cues (7)") == BrokenSubtitleReports.FewCues(7),
            "the minimum-cue gate's count becomes the finding");
        Check(BrokenSubtitleReports.FindingFromGateFailure("runtime (span 2433s vs runtime 1290s (tolerance ±600s))")
              == BrokenSubtitleReports.RuntimeMismatch(2433, 1290),
            "the runtime gate's two numbers become the finding");
        string memberFinding = BrokenSubtitleReports.RuntimeMismatch(2508, 1254) + " ('The.Middle.S03E01.srt')";
        var memberReport = BrokenSubtitleReports.Build("1901632-1458818", "The.Middle.S03 Complete", "EN", memberFinding, "12.1.12.344", "rosetestdev");
        Check(memberReport?.Comment.Contains(": cue span 2508s vs. runtime 1254s ('The.Middle.S03E01.srt').", StringComparison.Ordinal) == true,
            "a pack report names the member the gate measured");
        Check(memberReport?.Category == BrokenSubtitleReports.CategoryOther,
            "the member name does not change the category");
        Check(BrokenSubtitleReports.Build("1-2", "r", "DE", BrokenSubtitleReports.FewCues(7) + " ('x.srt')", "v", "a")?.Category
              == BrokenSubtitleReports.CategoryIncomplete,
            "even a finding that is classified by its tail still lands right with a member name");

        Check(BrokenSubtitleReports.FindingFromGateFailure(null) == null
              && BrokenSubtitleReports.FindingFromGateFailure("something") == null,
            "an unknown verdict produces no finding");

        // --- 3. the second sighting ------------------------------------------
        string pipeline = Read(plugin, "Pipeline/DownloadPipeline.cs");
        Check(pipeline.Contains("RememberAndReportAsync", StringComparison.Ordinal),
            "the empty-payload reject remembers the defect");
        Check(Regex.IsMatch(pipeline, @"if \(previous == null \|\| previous\.ReportedUtc != null"),
            "nothing is reported on the first failure or twice for the same row");
        Check(pipeline.Contains("qaKnownRejected.Contains(row.SubdlId, StringComparer.Ordinal)", StringComparison.Ordinal),
            "the QA path reports at the second sighting, where the candidate is refused again");
        Check(pipeline.Contains("summary.ReportsDeferred++", StringComparison.Ordinal),
            "a report SubDL does not take stays open for the next run");
        Check(pipeline.Contains("qaRejectedReleases.Contains(row.SubdlId, StringComparer.Ordinal)", StringComparison.Ordinal),
            "a measured gate verdict is reported in the same run — no second sighting is awaited");
        Check(pipeline.Contains("gateFinding += \" ('\" + packFile.Name + \"')\"", StringComparison.Ordinal),
            "the pipeline puts the resolved pack member into the finding");
        Check(pipeline.Contains("if (!cand.FullSeason && cand.UnpackFiles.Count == 0)", StringComparison.Ordinal),
            "a season/range pack that carries no file for the episode is NOT reported (our selection, not the uploader's defect)");
        Check(pipeline.Contains("QueueReport(row);", StringComparison.Ordinal)
              && !pipeline.Contains("await FileReportAsync(row, lang, summary, ct)", StringComparison.Ordinal),
            "nothing is filed inside the walk — confirmed rows are queued and filed after it");
        Check(Regex.IsMatch(pipeline, @"catch \(Exception ex\)\s*\{\s*summary\.ReportsDeferred\+\+;"),
            "a failing report is caught, counted as deferred, and never breaks the run");
        Check(pipeline.Contains("_qaFails.MarkReported(row)", StringComparison.Ordinal),
            "an accepted report is marked on the row");

        string tracker = Read(plugin, "Registry/QaFailTracker.cs");
        Check(tracker.Contains("ReportedUtc = reportedUtc", StringComparison.Ordinal),
            "re-recording a row keeps an existing report timestamp");
        Check(tracker.Contains("!string.Equals(x.Kind, \"empty\", StringComparison.Ordinal)", StringComparison.Ordinal),
            "an empty-payload memo does not turn into a skip memo");

        string db = Read(plugin, "Data/SubdlDbContext.cs");
        Check(db.Contains("ALTER TABLE rejected_candidates ADD COLUMN NId", StringComparison.Ordinal)
              && db.Contains("EnsureReportColumns();", StringComparison.Ordinal),
            "an upgraded data file gets the report columns (EnsureCreated never alters a table)");
        Check(db.Contains("CurrentSchemaVersion = 7", StringComparison.Ordinal),
            "the schema version is bumped with the columns");

        string api = Read(plugin, "Api/SubdlApiClient.cs");
        Check(api.Contains("\"subtitlePage\"", StringComparison.Ordinal) && api.Contains("c.NId = page[(slash + 1)..]", StringComparison.Ordinal),
            "the report id is taken from the search answer");
        Check(api.Contains("$\"{ApiBase}/report\"", StringComparison.Ordinal),
            "the report goes to the site's report endpoint");
        Check(api.Contains("== 429", StringComparison.Ordinal) && api.Contains("SubdlReportOutcome.Deferred", StringComparison.Ordinal),
            "a rate-limited report is reported back as deferred");
        Check(api.Contains("ud.TryGetProperty(\"name\"", StringComparison.Ordinal),
            "the attribution uses the account name, not the login mail");

        // --- 4. the switch ----------------------------------------------------
        string config = Read(plugin, "Configuration/PluginConfiguration.cs");
        Check(Regex.IsMatch(config, @"public\s+bool\s+ReportBrokenSubtitles\s*\{\s*get;\s*set;\s*\}\s*=\s*true\s*;"),
            "the switch exists and is on by default");

        string page = Read(plugin, "Configuration/configPage.html");
        int switchAt = page.IndexOf("id=\"ReportBrokenSubtitles\"", StringComparison.Ordinal);
        int aiAt = page.IndexOf("id=\"AllowAiTranslatedSubs\"", StringComparison.Ordinal);
        int runtimeAt = page.IndexOf("Runtime tolerance (seconds)", StringComparison.Ordinal);
        Check(switchAt > 0, "the page carries the switch");
        Check(switchAt > aiAt, "the switch sits below the AI filter");
        Check(runtimeAt > switchAt, "the switch sits above the runtime tolerance field, which stays last");
        string between = runtimeAt > switchAt ? page[switchAt..runtimeAt] : string.Empty;
        Check(Regex.Matches(between, "id=\"").Count == 1,
            "the switch is the second-to-last control — only the runtime tolerance field follows");
        Check(page.Contains("Report broken subtitles to SubDL", StringComparison.Ordinal),
            "the label names what it does");
        Check(page.Contains("config.ReportBrokenSubtitles = document.querySelector('#ReportBrokenSubtitles').checked", StringComparison.Ordinal)
              && page.Contains("document.querySelector('#ReportBrokenSubtitles').checked = config.ReportBrokenSubtitles !== false", StringComparison.Ordinal),
            "the switch is loaded and saved with the page");

        string summaryLine = Regex.Match(pipeline, @"{Reported} reported \(\{ReportsDeferred\} deferred\)").Value;
        Check(summaryLine.Length > 0, "the run summary names reports and deferrals");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"reporting: {_checks} checks, all green."
            : $"reporting: {Failures.Count} of {_checks} checks FAILED:");
        foreach (string f in Failures)
        {
            Console.WriteLine("  - " + f);
        }

        return Failures.Count == 0 ? 0 : 1;
    }
}
