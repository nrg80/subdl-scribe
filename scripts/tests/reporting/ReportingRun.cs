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
//   3. FILING IN THE RUN THAT SAW IT. A defect is filed in the same run, without a second fetch;
//      ReportedUtc keeps a filing from repeating and a reported candidate is spent. A rate-limited
//      report stays open for the next run.
//   4. THE SWITCH. On by default, in the download quality gates, second to last — the runtime
//      tolerance stays the last field.
//
// Usage: dotnet run --project scripts/tests/reporting/reporting.csproj [repo-root]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Registry;
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

        // --- 3. filing in the run that saw it ----------------------------------
        string pipeline = Read(plugin, "Pipeline/DownloadPipeline.cs");
        Check(pipeline.Contains("RememberAndReportAsync", StringComparison.Ordinal),
            "the empty-payload reject remembers the defect");
        Check(!pipeline.Contains("previous == null", StringComparison.Ordinal),
            "no defect waits for a second sighting any more");
        Check(pipeline.Contains("if (row != null && row.ReportedUtc == null)", StringComparison.Ordinal),
            "a row is filed once — ReportedUtc, not a second failure, keeps it from repeating");
        Check(pipeline.Contains("qaKnownRejected.Contains(row.SubdlId, StringComparer.Ordinal)", StringComparison.Ordinal),
            "the skip path still files a report — the safety net for rows remembered with the switch off");
        Check(pipeline.Contains("summary.ReportsDeferred++", StringComparison.Ordinal),
            "a report SubDL does not take stays open for the next run");
        Check(pipeline.Contains("qaRejectedReleases.Contains(row.SubdlId, StringComparer.Ordinal)", StringComparison.Ordinal),
            "a measured gate verdict is reported in the same run — no second sighting is awaited");
        Check(pipeline.Contains("gateFinding += \" ('\" + packFile.Name + \"')\"", StringComparison.Ordinal),
            "the pipeline puts the resolved pack member into the finding");
        // F-M455 replaced the "no members" test with "at most one member": with unpack=1 an ordinary
        // single release carries exactly one member — the file itself — so the old guard silenced
        // every honest candidate. A pack is MORE than one member, and a pack stays excluded.
        Check(pipeline.Contains("genuineEmpty && !cand.FullSeason && cand.UnpackFiles.Count <= 1", StringComparison.Ordinal),
            "a season/range pack that carries no file for the episode is NOT reported (our selection, not the uploader's defect), and a single file is");
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
        Check(db.Contains("CurrentSchemaVersion = 8", StringComparison.Ordinal),
            "the schema version is bumped with the columns (8 carries the status row\'s BrokenReported)");

        string api = Read(plugin, "Api/SubdlApiClient.cs");
        Check(api.Contains("\"subtitlePage\"", StringComparison.Ordinal) && api.Contains("c.NId = page[(slash + 1)..]", StringComparison.Ordinal),
            "the report id is taken from the search answer");
        Check(api.Contains("$\"{ApiBase}/report\"", StringComparison.Ordinal),
            "the report goes to the site's report endpoint");
        Check(api.Contains("== 429", StringComparison.Ordinal) && api.Contains("SubdlReportOutcome.Deferred", StringComparison.Ordinal),
            "a rate-limited report is reported back as deferred");
        Check(api.Contains("ud.TryGetProperty(\"name\"", StringComparison.Ordinal),
            "the attribution uses the account name, not the login mail");

        // --- 3c. a refusal is not a defect (measured 09.11.2026) ---------------
        string outcomeSource = Read(plugin, "Api/DownloadOutcome.cs");
        Check(outcomeSource.Contains("record DownloadOutcome", StringComparison.Ordinal)
              && outcomeSource.Contains("public required int Status", StringComparison.Ordinal)
              && outcomeSource.Contains("public string Label =>", StringComparison.Ordinal),
            "F-M464: every fetch returns its own outcome — status, bytes, error and payload together");
        Check(!api.Contains("LastFileDownloadStatus", StringComparison.Ordinal)
              && !api.Contains("ResetDownloadStatus", StringComparison.Ordinal),
            "F-M464: the client carries no download verdict any more, so no call can overwrite another's");
        Check(api.Contains("download?format=zip", StringComparison.Ordinal)
              && api.Contains("ResolveMemberAsync(archive, zipSeason, zipEpisode, ct, memberName)", StringComparison.Ordinal),
            "the archive is the shape asked for first — one request, resolved here");
        Check(ArchiveKey.FromUrl("https://api.subdl.com/api/v2/subtitles/AbCdEf123/download?format=zip") == "v2-AbCdEf123",
            "a v2 archive has a stable cache key, so a pack is fetched once");
        // --- 3b. a spent candidate stays spent ---------------------------------
        string dir = Path.Combine(Path.GetTempPath(), "subdl-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SubdlDbContext(dir);
            var spent = new QaFailTracker(store);
            spent.RecordSkippedCandidates("item", "EN", new[] { "9100001-111111", "9100002-222222" },
                kind: "empty");
            spent.RecordSkippedCandidates("item", "EN", new[] { "9100003-333333" }, kind: "qa");
            var rowToReport = store.RejectedCandidates.Find(x => x.SubdlId == "9100001-111111").First();
            spent.MarkReported(rowToReport);

            List<string> blocked = spent.GetSkippedCandidates("item", "EN");
            Check(blocked.Contains("9100001-111111"),
                "a reported package is spent — it is not fetched a second time");
            Check(blocked.Contains("9100003-333333"), "a reported gate verdict is spent");
            Check(!blocked.Contains("9100002-222222"),
                "an empty row without a report blocks nothing — one hiccup does not burn a candidate");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Check(api.Contains("not_a_single_file", StringComparison.Ordinal)
              && api.Contains("\"zip\"", StringComparison.Ordinal)
              && api.Contains("ResolveMemberAsync(archive, zipSeason, zipEpisode, ct, memberName)", StringComparison.Ordinal),
            "a subtitle that exists only as an archive is fetched as zip and resolved");
        Check(pipeline.Contains("dl.Label", StringComparison.Ordinal),
            "the reject line names the HTTP status and the API error of THIS attempt");
        Check(!pipeline.Contains("_api.LastFileDownload", StringComparison.Ordinal)
              && !pipeline.Contains("_api.ResetDownloadStatus", StringComparison.Ordinal),
            "F-M464: the walk reads the outcome of its own call, never state on the client");
        Check(pipeline.Contains("dl.GenuineEmpty", StringComparison.Ordinal)
              && pipeline.Contains("bool genuineEmpty", StringComparison.Ordinal),
            "only a 200 that carried nothing may be reported as a broken upload");

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

        // -------------------------------------------------------------------------------------------
        // F-M455 (operator order 10.11.2026): "für leere subs den einmaligen refetch nach 10 Sekunden
        // zurück. Melden dann erst nach dem zweiten Fehlschlag." One empty answer proves nothing — it
        // was measured that night: a release answered 200 with nothing and delivered 83,119 B a few
        // hours later. So the candidate is asked once more, and only the second empty answer is a
        // finding. Driven through the source of the real walk plus the REAL delta builder.
        // -------------------------------------------------------------------------------------------
        Check(pipeline.Contains("asking once more in 10 s (F-M455)", StringComparison.Ordinal)
              && pipeline.Contains("await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);", StringComparison.Ordinal),
            "F-M455: an empty answer is asked once more after ten seconds");
        Check(pipeline.Contains("_emptyRetried.Add(lang + \"|\" + cand.Url)", StringComparison.Ordinal),
            "F-M455: the refetch happens ONCE per candidate — a second empty answer is the second failure");
        Check(pipeline.Contains("cand.UnpackFiles.Count <= 1", StringComparison.Ordinal),
            "F-M455: one member is a single file, so an ordinary candidate can be reported (the old test demanded none)");

        // F-M456 (measured 10.11.2026): a candidate that is thrown away BEFORE any request was
        // explained with the previous candidate's answer — "no bytes (HTTP 200)" on a candidate
        // nobody had asked about. The verdict is cleared per attempt, and only a real 200 counts as
        // an empty answer. Driven through the REAL client.
        DownloadOutcome neverAsked = DownloadOutcome.None("candidate");
        Check(neverAsked.NoRequest && neverAsked.Bytes == 0 && string.IsNullOrEmpty(neverAsked.Error),
            "F-M456: an attempt that never happened carries no status, no bytes and no error");
        Check(neverAsked.Label == "no request made",
            "F-M456: the reject line says 'no request made' instead of naming a status that came from somewhere else");
        Check(!neverAsked.GenuineEmpty,
            "F-M456: status 0 is not an empty answer — only a real 200 that carried nothing is");
        Check(pipeline.Contains("DownloadOutcome.None(\"candidate\")", StringComparison.Ordinal),
            "F-M456: a candidate dropped before any request starts from a verdict of its own, not the last one");

        // F-M460 (operator order 10.11.2026: "Soll der SDH-Pfad melden" — Ja): the SDH walk measures
        // the same verdicts and filed nothing, because only the main path reported. The wordings come
        // verbatim from the run of that morning, and the mapping is the REAL one.
        string? hiStructureFinding = BrokenSubtitleReports.FindingFromGateFailure(
            "structure: monotonic=True min=200ms max=3579000ms");
        Check(!string.IsNullOrEmpty(hiStructureFinding),
            "F-M460: the SDH structure verdict (a cue over the whole film) becomes a finding");

        string? hiRuntimeFinding = BrokenSubtitleReports.FindingFromGateFailure(
            "runtime: span 5870s vs runtime 6633s (tolerance \u00b1600s)");
        Check(!string.IsNullOrEmpty(hiRuntimeFinding) && hiRuntimeFinding!.Contains("5870", StringComparison.Ordinal),
            "F-M460: the SDH runtime verdict becomes a finding and names the measured span");

        Check(BrokenSubtitleReports.FindingFromGateFailure("no bytes (no request made)") == null,
            "F-M460/F-M456: nothing delivered is not a finding — the SDH path stays silent there");
        Check(pipeline.Contains("hiFinding = hiBytes == null", StringComparison.Ordinal),
            "F-M460: the SDH report is built only from delivered bytes");
        Check(pipeline.Contains("_emptyRetried.Add(\"hi|\"", StringComparison.Ordinal),
            "F-M455/F-M460: the SDH fetch also asks once more after ten seconds before anything is said");
        Check(pipeline.Contains("QueueReport(row);", StringComparison.Ordinal)
              && pipeline.Contains("string.Equals(row.SubdlId, hi.SubdlId, StringComparison.Ordinal)", StringComparison.Ordinal),
            "F-M460: the SDH finding is queued right there, not at a run end that only runs when nothing was saved");

        // F-M462 (operator order 10.11.2026): a gate verdict died as soon as a LATER candidate saved
        // the language, because the only flush sat behind "nothing was saved". It is filed where it is
        // measured now — before the walk moves on.
        int gateFact = pipeline.IndexOf("qaRejectedFacts[cand.SubdlId] = new ReportFacts", StringComparison.Ordinal);
        int gateFile = pipeline.IndexOf("F-M462", StringComparison.Ordinal);
        int runEndFlush = pipeline.IndexOf("if (!savedAny && attempts > 0)", StringComparison.Ordinal);
        Check(gateFact > 0 && gateFile > gateFact && runEndFlush > gateFile
              && pipeline[gateFile..runEndFlush].Contains("QueueReport(row);", StringComparison.Ordinal),
            "F-M462: the main-path verdict is queued where it is measured, not behind 'nothing was saved'");

        string apiSource = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "SubdlApiClient.cs"));
        // F-M461 (measured 10.11.2026): the v1 download route recorded no verdict at all, so its
        // rejects quoted the previous v2 call — the source of both misleading labels we chased
        // ("HTTP 200" on a healthy upload, "no request made" on a request that happened).
        int v1 = apiSource.IndexOf("private async Task<DownloadOutcome> FetchArchiveAsync", StringComparison.Ordinal);
        int v1End = v1 < 0 ? -1 : apiSource.IndexOf("Downloads a subtitle file from dl.subdl.com", v1, StringComparison.Ordinal);
        bool v1Records = v1 >= 0 && v1End > v1
                         && apiSource[v1..v1End].Contains("Status = (int)resp.StatusCode,", StringComparison.Ordinal)
                         && apiSource[v1..v1End].Contains("Bytes = payload.Length,", StringComparison.Ordinal)
                         && apiSource[v1..v1End].Contains("Route = \"v1\",", StringComparison.Ordinal);
        Check(v1Records,
            "F-M461: the v1 download route returns status and body length of its OWN request");

        var liveRun = new DownloadRunSummary { Downloaded = 3, Reported = 2 };
        Check(StatusCounterDelta.From(null, liveRun).BrokenReported == 2,
            "F-M455: accepted reports reach the statistics row");

        var dryRun = new DownloadRunSummary { Downloaded = 3, Reported = 2, IsDryRun = true };
        Check(StatusCounterDelta.From(null, dryRun).BrokenReported == 0,
            "F-M455: a dry run reports nothing, so it counts nothing");
        Check(StatusCounterDelta.From(null, liveRun).IsEmpty == false,
            "F-M455: a run that only filed reports is not an empty run — the row gets written");

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
