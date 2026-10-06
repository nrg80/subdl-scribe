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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Filters;
using Jellyfin.Plugin.SubdlScribe.Language;
using Jellyfin.Plugin.SubdlScribe.Registry;
using Jellyfin.Plugin.SubdlScribe.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;
using Jellyfin.Plugin.SubdlScribe.Data;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// Download pipeline run summary ([D] F-M41).
/// </summary>
public class DownloadRunSummary
{
    /// <summary>F-M247: true when this run was a dry run — it searched and ranked but saved
    /// nothing. The statistics counters ignore everything a dry run produced; the run-end log
    /// line labels its numbers as hypothetical instead of claiming saves that never happened.</summary>
    public bool IsDryRun { get; set; }

    /// <summary>Gets or sets the number of downloaded subtitles.</summary>
    public int Downloaded { get; set; }

    /// <summary>Gets or sets the number of failed downloads.</summary>
    public int Failed { get; set; }

    /// <summary>
    /// F-M283 (user decision 02.10.2026): there is no download mark any more, so there is nothing to
    /// invalidate or to withhold. `MarksInvalidated` and `MarksWithheld` are removed with their
    /// subject — a counter that reports on a construct which no longer exists reads as information
    /// while telling nobody anything. What the run reports instead is which items are open and which
    /// searches were asked for; see the run line below.
    /// </summary>

    /// <summary>F-M282: open required files seen in this run (regular + hearing-impaired data).</summary>
    public int OpenFilesSeen { get; set; }

    /// <summary>Gets or sets the number of languages reported "not available" for an item.</summary>
    public int NotAvailable { get; set; }

    /// <summary>F-M81z-2 (user decision 10.09.2026): run ended by hitting the daily
    /// quota (429) — a clean rescheduled stop, NOT a download failure. Surfaced as
    /// its own "quota" counter in the Task finished line.</summary>
    public int QuotaStopped { get; set; }

    /// <summary>F-M94h: run was not started because another run holds the global lock.</summary>
    public bool SkippedByLock { get; set; }

    /// <summary>Gets or sets the number of items skipped (no id, filters, no missing languages).</summary>
    public int SkippedItems { get; set; }

    // F-M24d: skip-reason counters for the aggregate log lines (one line per reason
    // class at run end, Normal mode too — not 284 per-item lines).

    /// <summary>Gets or sets the count of items skipped because SubDL had no candidates at all.</summary>
    public int SkippedNoCandidates { get; set; }

    /// <summary>Gets or sets the count of items skipped for id-less (hard IMDB match).</summary>
    public int SkippedNoId { get; set; }

    /// <summary>Gets or sets the count of items skipped as id-unresolvable after exhausting the F-M66 retry budget.</summary>
    public int SkippedIdGaveUp { get; set; }

    /// <summary>Gets or sets the count of items skipped by the skip filters (dir/file patterns).</summary>
    public int SkippedByFilter { get; set; }

    /// <summary>Gets or sets the number of items skipped as file-missing after exhausting retries (F-M60).</summary>
    public int SkippedFileMissing { get; set; }

    /// <summary>Gets or sets the count of items not due for refetch (F-M47 gap).</summary>
    public int SkippedNotDue { get; set; }

    /// <summary>Gets or sets the count of items fully skipped because every missing language hit its QA retry limit.</summary>
    public int SkippedQaGiveUp { get; set; }

    /// <summary>Gets or sets the total count of (item, language) pairs skipped for the QA retry limit this run.</summary>
    public int QaGiveUpLanguages { get; set; }

    /// <summary>
    /// F-M286: candidates this run FETCHED and then threw away — every reject path, not the QA
    /// gates alone. The four gates (language/structure/min-cues/runtime) are a subset; a candidate
    /// whose bytes arrived and were discarded for any other reason (no usable data, content already
    /// known, a hearing-impaired gate) belongs here too. Reconciles with the day's quota:
    /// requests = saved + rejected. A candidate that was never fetched (empty HI pool, candidates
    /// left untried by keep-best) is NOT counted — nothing was spent on it.
    /// </summary>
    public int RejectedCandidates { get; set; }

    /// <summary>F-M218: items whose type/season/episode came from the file name, not Jellyfin.</summary>
    public int TypeCorrectedByFileName { get; set; }

    /// <summary>F-M218: TMDb title searches that only matched once the year filter was dropped.</summary>
    public int TmdbYearFilterMisses { get; set; }

    /// <summary>
    /// User pressed the stop button during this run.
    /// </summary>
    public bool StopRequested { get; set; }

    // Id sanity-check / backfill counters (one aggregate line at run end).
    /// <summary>Searches where results[0] ids were compared against the searched ids.</summary>
    public int IdChecks { get; set; }

    /// <summary>Items dropped because SubDL's results[0] film disagrees with the searched id.</summary>
    public int IdMismatches { get; set; }

    /// <summary>Missing imdb/tmdb ids backfilled from SubDL's results[0] film header.</summary>
    public int IdBackfilledFromApi { get; set; }

    /// <summary>Items whose resolved id set contains an imdb id.</summary>
    public int IdImdbOk { get; set; }

    /// <summary>Items whose resolved id set contains a tmdb id.</summary>
    public int IdTmdbOk { get; set; }

    /// <summary>Items where the filename-based TMDB fallback resolved an id.</summary>
    public int IdResolvedFromFilename { get; set; }

    /// <summary>Gets or sets the count of items with no missing target languages.</summary>
    public int SkippedNothingMissing { get; set; }

    /// <summary>Gets or sets the number of media files processed.</summary>
    public int FilesSeen { get; set; }
}

/// <summary>
/// Download pipeline ([D] F-M41–M47): for every item in the selected libraries,
/// find missing target-language subtitles on SubDL, pick the best candidate
/// (release-score, F-M44), download it and place it next to the media file.
/// Uses the same API client, registry and language mapper as the upload pipeline (F-M37).
/// </summary>
public sealed class DownloadPipeline : IDisposable
{
    private static readonly Regex FpsRegex = new(@"(\d{1,3}(?:\.\d+)?)\s*fps", RegexOptions.IgnoreCase);
    private readonly ILogger<DownloadPipeline> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly SubdlApiClient _api;
    /// <summary>Always use the current Plugin.Instance registry so a DB reset is respected.</summary>
    private ContentHashRegistry Registry => Plugin.Instance?.Registry ?? throw new InvalidOperationException("Plugin not initialized");
    private readonly TmdbImdbResolver _tmdb;

    /// <summary>F-M218: the summary of the run in flight, for event handlers.</summary>
    private DownloadRunSummary? _runSummaryForEvents;
    private readonly PluginConfiguration _config;
    private readonly GlobalRateLimiter _limiter;
    private readonly DownloadSearchTracker _searchTracker;

    /// <summary>
    /// Directories proven read-only THIS run (probe cached per run — a share can
    /// be remounted writable between runs, so the verdict must never outlive the run).
    /// </summary>
    private readonly HashSet<string> _readonlyDirs = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Directories already probed (writable) this run — probe once per run per dir.
    /// </summary>
    private readonly HashSet<string> _probedDirs = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Probe write — exactly mirrors AtomicWriteAsync's.part path. Returns false
    /// and caches the directory on any failure (read-only share, permission, quota).
    /// </summary>
    private async Task<bool> ProbeWritableAsync(string dir, CancellationToken ct)
    {
        string probePath = Path.Combine(dir, ".subdl-write-probe");
        try
        {
            await File.WriteAllBytesAsync(probePath, new byte[] { 0x70 }, ct).ConfigureAwait(false);
            File.Delete(probePath);
            return true;
        }
        catch (Exception)
        {
            _readonlyDirs.Add(dir);
            LogUtil.Normal(_logger, "[SubDL-D] Directory not writable — skipped this run: {Dir}", dir);
            return false;
        }
    }
    private readonly Registry.FileRetryTracker _fileRetries;
    private readonly Registry.IdNotFoundTracker _idNotFound;
    private readonly Registry.QaFailTracker _qaFails;

    /// <summary>In-run watchdog (NF-4); non-null only while a run is active.</summary>
    private RunWatchdog? _watchdog;

    /// <summary>
    /// F-M48 fix (08.09.2026): set when the hourly rate cap or a server 429 is hit —
    /// the item loop aborts so remaining items keep their refetch-due state instead
    /// of being marked "searched" with fail-open empty results.
    /// </summary>
    private bool _stopRun;

    // Per-item filename-title fallback (soft mode last rung)

    /// <summary>
    /// True only for arrival-watcher-triggered runs — the 15-min metadata
    /// wait applies to them (the item just landed). Scheduled refetch runs skip
    /// the wait and go straight to the TMDB ladder.
    /// </summary>
    public bool IsArrivalRun { get; set; }

    // ── F-M111: queue-driven operation (user decision 12.09.2026) ───────────
    // When set, the item loop processes ONLY these item ids (the seeder filled
    // the queue). Null = classic full-scan behaviour (legacy manual runs).
    public HashSet<string>? QueueFilter { get; set; }

    // (16.09.2026): v1 soft-mode film_name search restored — the parsed
    // Filename title rides into the v1 search as the last rung for
    // Id-less items (HEAD 8a17aed behavior, removed by, user wants it back).
    private string? _searchFnTitle;
    private int? _searchFnYear;

    /// <summary>Per-item outcome report for the queue bookkeeping (fired by ProcessItemAsync).</summary>
    public event Action<string, ItemOutcome>? ItemResult;

    /// <summary>Per-item result class reported back to the queue bookkeeping.</summary>
    public enum ItemOutcome { Done, RealFailure, NotAvailable }

    /// <summary>Fires ItemResult — no-op when nobody listens.</summary>
    private void ReportOutcome(BaseItem item, ItemOutcome outcome)
    {
        try
        {
            ItemResult?.Invoke(item.Id.ToString(), outcome);
        }
        catch
        {
            // Queue bookkeeping must never break the pipeline.
        }
    }

    /// <summary>
    /// F-M49: daily-limit waits already performed in this run — at most ONE.
    /// A second 429 after a successful resume stops the run; the next
    /// scheduled run continues with the fresh quota.
    /// </summary>
    private int _dailyLimitWaits;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadPipeline"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="api">SubDL API client.</param>
    /// <param name="registry">Content hash registry.</param>
    /// <param name="tmdb">TMDB→IMDB resolver (required — gates SERIES items, F-M203).</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="limiter">Global rate limiter shared with the upload pipeline (F-M20/F-M26).</param>
    /// <param name="searchTracker">Per-item search timestamps (F-M47).</param>
    public DownloadPipeline(
        ILogger<DownloadPipeline> logger,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        SubdlApiClient api,
        TmdbImdbResolver tmdb,
        PluginConfiguration config,
        GlobalRateLimiter limiter,
        DownloadSearchTracker searchTracker,
        Registry.FileRetryTracker fileRetries,
        Registry.IdNotFoundTracker idNotFound,
        Registry.QaFailTracker qaFails)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _api = api;
        _tmdb = tmdb;
        _config = config;
        _limiter = limiter;
        _searchTracker = searchTracker;
        _fileRetries = fileRetries;
        _idNotFound = idNotFound;
        _qaFails = qaFails;
    }

    /// <summary>Gets the effective target languages (F-M42), parsed from config, ordered, deduped.</summary>
    public List<string> TargetLanguages =>
        _config.DownloadLanguages
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToUpperInvariant())
            .Distinct()
            .ToList();

    /// <summary>
    /// Runs a full download pass over the selected libraries.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Run summary.</returns>
    public async Task<DownloadRunSummary> RunAsync(IProgress<double> progress, CancellationToken ct)
    {
        var summary = new DownloadRunSummary();

        // F-M247: record that this run was a dry run. It searches, ranks and runs the QA gates,
        // so its summary fills up with numbers, but it saves nothing to disk — the statistics
        // must not receive them. Set here, at the source, so a later counter cannot forget it.
        summary.IsDryRun = _config.DownloadDryRun;
        _dailyLimitWaits = 0; // F-M49: fresh per run

        // F-M94h (rework 25.09.2026): ONE global run lock for all six components.
        // No long wait: a brief hand-off grace only, then reschedule by JobSpacingMinutes.
        if (!await PipelineRunLock.AcquireAsync(_logger, "download", ct).ConfigureAwait(false))
        {
            summary.SkippedByLock = true;
            var spacing = Math.Clamp(_config.JobSpacingMinutes, 5, 120);
            ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFireAt(upload: false, DateTime.UtcNow.AddMinutes(spacing));
            LogUtil.Normal(_logger, "[SubDL-D] Download deferred by {Spacing} min — global run lock busy.", spacing);
            return summary;
        }
        try
        {
        // F-M19/F-M203: no partial operation — same gate as the upload side, from the same
        // source. Without all four credentials the search cannot run (the API key) and the
        // ids cannot be resolved (the TMDb key), so the run is refused up front.
        var missingDown = _config.MissingCredentials();
        if (missingDown.Count > 0)
        {
            LogUtil.Normal(_logger, "[SubDL-D] download: NOT started — missing credential(s): {Missing}. Set them in the plugin configuration.", string.Join(", ", missingDown));
            return summary;
        }

        var targets = TargetLanguages;
        if (targets.Count == 0)
        {
            LogUtil.Normal(_logger, "[SubDL-D] No target languages configured — nothing to do.");
            return summary;
        }

        // F-M213 (26.09.2026, user decision): an empty library selection is a
        // hard stop for BOTH directions. UploadPipeline always had this check;
        // DownloadPipeline did not, so a run with nothing selected still went
        // through the full setup (watchdog, API/TMDB handler wiring, the
        // "download: started" line) and only filtered everything away later in
        // CollectItems — which reads like "it scanned the whole server".
        if (_config.SelectedLibraries.Count == 0)
        {
            LogUtil.Normal(_logger, "[SubDL-D] No libraries selected — nothing to do.");
            return summary;
        }

        // F-M64 (user decision 09.09.2026 "download at the next run is ok"):
        // when the configured language list CHANGES, the recorded refetch timestamps
        // belong to the OLD list — items missing a newly added language must not wait
        // out the refetch gap. Reset all timestamps once; the next run treats every
        // item as never-searched. Cost control: MissingLanguages runs BEFORE any API
        // call, so items that already have all (new) target languages skip for free —
        // search calls only happen where a new language is genuinely absent.
        // The former explicit "language list changed → reset every timestamp" pass is gone: each
        // file records the language list it was searched under, and IsDue compares that against the
        // current configuration. A file whose stored list differs is simply due again — no global
        // reset, no second bookkeeping row to keep in step.
        LogUtil.Detail(_config.LogMode, _logger, "[SubDL-D] target languages: {Languages}", string.Join(",", targets));

        // NF-4: in-run watchdog — fixed 6-min grace, independent of the pacing knob.
        // ApiActivity fires after EVERY completed API round-trip (search pages,
        // downloads, uploads, retries) — the heartbeat that keeps the watchdog
        // quiet while the run is making legal slow progress.
        _watchdog = new RunWatchdog(
            "SubDL-D", ct,
            RunWatchdog.Grace,
            msg => _logger.LogError("{Msg}", msg));
        _api.ApiActivity += _watchdog.Heartbeat;
        var runCt = _watchdog.Token;
        _watchdog.Heartbeat();
        _watchdog.Start();

        // F-M24a (fixed 09.09.2026, user decision): run START is Normal-level —
        // critical errors, rate limits, run started/finished.
        LogUtil.Normal(_logger, "[SubDL] download: started (dry-run={Dry}, refetch={Refetch})", _config.DownloadDryRun, _config.RefetchInterval);

        PipelineStopSignal.ClearStaleMarkers(Plugin.Instance?.DataFolderPath ?? string.Empty);

        // F-M24a: forward per-call API traces to LogLevel.Debug
        _api.DebugTrace += OnApiTrace;

        // F-M232: the client's LogInfo channel (429 raw bodies, rate headers, login
        // diagnostics) was subscribed only by the postprocessing path, so a download
        // run's rate-limit diagnosis never reached the log. Subscribe it per run.
        _api.Log += OnApiLog;

        // F-M24a (user decision 09.09.2026): TMDB calls trace at VERBOSE (SubDL API calls are Debug).
        _tmdb.Trace += OnTmdbTrace;
        _runSummaryForEvents = summary; // F-M218: event handlers need the running summary
        _tmdb.YearFilterMiss += OnTmdbYearFilterMiss;

        // F-M54: wrong TMDB key reported ONCE at Normal/Error level.
        _tmdb.AuthError += msg => _logger.LogError("[SubDL-D] {Msg}", msg);

        // NO run-level metadata grace here — the arrival watcher grants ONE
        // 1-min grace per event before this (first) pipeline starts. Scheduled
        // refetch runs get none. No-id items still have the per-item 15-min ladder.

        // F-M47: per-item refetch gap (F-M111: unified interval, user decision 12.09.2026).
        var refetchGap = SubdlDownloadTask.IntervalToGap(_config.RefetchInterval);

        var items = CollectItems(_config.SelectedLibraries);
        summary.FilesSeen = items.Count;

        // (user decision 08.09.2026): id-less items go to the BACK — their
        // 15-min metadata ladder must not block items that already have ids.
        var withIds = new List<BaseItem>();
        var withoutIds = new List<BaseItem>();
        foreach (var it in items)
        {
            var (im, tm, _, _, _) = ResolveIds(it);
            (string.IsNullOrWhiteSpace(im) && string.IsNullOrWhiteSpace(tm) ? withoutIds : withIds).Add(it);
        }

        if (withoutIds.Count > 0)
        {
            items = withIds.Concat(withoutIds).ToList();
        }

        // F-M59 (user decision 09.09.2026): LIFO within BOTH partitions — newest items
        // (DateCreated descending) get the daily quota first. Same rationale as upload;
        // id-partition keeps priority over recency.
        items = items
            .Select((it, i) => (Item: it, Pos: i, Created: it.DateCreated))
            .OrderByDescending(x => x.Created)
            .ThenBy(x => x.Pos)
            .Select(x => x.Item)
            .ToList();

        var skipFilter = new SkipFilter(_config.EffectiveSkipDirPatterns, _config.EffectiveSkipFilePatterns);

        // F-M183 (user report 23.09.2026): the run counters used items.Count (= every
        // item in the selected libraries, e.g. 1125) as their denominator, but the
        // queue filter below skips non-queued items — so a run stopped after 400 queue
        // items reported "400/1125" (25%) while it had actually consumed 400 of only
        // 727 queued items (55%). Count the queue as
        // the denominator: the number the run can really work, and the one that
        // makes the hourly-cap decision legible.
        int queuedTotal = QueueFilter == null
            ? items.Count
            : items.Count(it => QueueFilter.Contains(it.Id.ToString()));

        int idx = 0;
        foreach (var item in items)
        {
            // User stop marker — check BEFORE doing any work.
            if (PipelineStopSignal.IsStopped(Plugin.Instance?.DataFolderPath ?? string.Empty, upload: false, runCt))
            {
                LogUtil.Normal(_logger, "[SubDL-D] download: user stop requested — finishing current run.");
                summary.StopRequested = true;
                break;
            }

            runCt.ThrowIfCancellationRequested();
            _watchdog.Heartbeat();

            // F-M111: queue-driven — only queued items are worked; others stay
            // invisible to this run (the seeder owns admission).
            if (QueueFilter != null && !QueueFilter.Contains(item.Id.ToString()))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested(); // F-M131: user stop aborts the run
            if (_stopRun)
            {
                // F-M48 fix (08.09.2026): quota/rate-limit exhausted mid-run — stop
                // instead of marking the remaining items "searched" with garbage results.
                _logger.LogWarning(
                    "[SubDL-D] Run aborted after {Done}/{Total} queued items — quota/rate limit hit; remaining items stay due for the next run.",
                    idx, queuedTotal);
                break;
            }

            idx++;
            // Fine-grained progress: item start + item done (streams inside report via 0.5 steps)
            progress?.Report((idx - 1 + 0.05) / Math.Max(items.Count, 1) * 100);
            await ProcessItemAsync(item, targets, summary, skipFilter, refetchGap, runCt).ConfigureAwait(false);
            progress?.Report((double)idx / Math.Max(items.Count, 1) * 100);
        }

        _api.DebugTrace -= OnApiTrace;
        _api.Log -= OnApiLog; // F-M232: detach the client's LogInfo channel per run
        _tmdb.Trace -= OnTmdbTrace;
        _tmdb.YearFilterMiss -= OnTmdbYearFilterMiss; // F-M218
        _runSummaryForEvents = null;
        Registry.Flush();
        _searchTracker.Flush(); // F-M47: persist search timestamps after the run
        _idNotFound.Flush(); // Persist id-resolution failure counters (F-M66) — same kill-safe flush as upload pipeline
        // F-M183 (user report 23.09.2026, supersedes the "saved only" rule for
        // the run-end line): a run that searched 400 items and saved nothing looked
        // identical to a run that did nothing at all — the single "{N} saved" line hid
        // whether items had no candidates, were already complete, or were skipped as
        // not-due. Print one compact aggregate so a 0-saved run is diagnosable in
        // Normal mode without raising LogMode to Verbose (per-item lines stay Verbose).
        // F-M247: in a dry run nothing was saved. Keep the whole diagnostic aggregate — a dry
        // run is exactly when those counts matter — but label the first number as what it is:
        // the saves the run would have made.
        string savedLabel = summary.IsDryRun ? "would have saved" : "saved";
        // F-M286: {Rejected} names what was FETCHED AND THROWN AWAY. Without it a run that spent
        // its whole daily quota and kept 41 of 50 files read exactly like one that kept everything:
        // "skipped" counts items NOT PROCESSED, so the nine discards sat in no field at all.
        LogUtil.Normal(_logger, 
            "[SubDL-D] download: finished — lock released. {Saved} " + savedLabel + " | {Rejected} rejected after fetch | {NoCand} no candidates | {NotAvail} lang not available | {Skipped} skipped ({NotDue} not due, {Filtered} filtered, {NoId} no id, {NothingMissing} nothing open) | {Open} open file(s) seen | {Failed} failed | processed {Done}/{Queued} queued",
            summary.Downloaded,
            summary.RejectedCandidates,
            summary.SkippedNoCandidates,
            summary.NotAvailable,
            summary.SkippedItems,
            summary.SkippedNotDue,
            summary.SkippedByFilter,
            summary.SkippedNoId + summary.SkippedIdGaveUp,
            summary.SkippedNothingMissing,
            summary.OpenFilesSeen,
            summary.Failed,
            idx,
            queuedTotal);
        return summary;
        }
        catch (OperationCanceledException)
        {
            // F-M23: same as the upload pipeline — a run cancelled from outside (user stop,
            // restart, task cancel) must not discard the downloads it already completed.
            // Return the partial summary; the caller still sees the cancel via StopRequested.
            LogUtil.Normal(_logger, "[SubDL-D] download: cancelled — keeping the partial result. {N} saved", summary.Downloaded);
            summary.StopRequested = true;
            return summary;
        }
        finally
        {
            // The watchdog belongs to THIS run and must go down with it even when the run
            // dies on an exception. Before this, disposal sat on the success path only
            // (it was inside the try), so any exception left the monitor alive: it kept
            // polling, and 2.5 min later reported "no progress" for a run that had been
            // dead since the throw. Measured 25.09.2026 — six such phantom aborts, each
            // exactly ~2.5 min after a failed cycle, all with no run in flight. Nothing
            // was actually aborted (the token is per-run and the monitor returns right
            // after firing), but the ERROR line made six dead runs look like six real
            // watchdog aborts. UploadPipeline already did this correctly via `using`.
            if (_watchdog != null)
            {
                _api.ApiActivity -= _watchdog.Heartbeat;
            }

            _watchdog?.Dispose();
            _watchdog = null;

            // F-M23: keep the partial summary even when the run is cancelled — the caller
            // reads StopRequested and the counters include the work that did land.
            PipelineRunLock.Release(_logger, "download");
        }
    }

    /// <summary>
    /// F-M49: optional resume after the SubDL daily download limit. With
    /// DownloadContinueAfterLimit enabled and a usable server-reported reset
    /// time, waits — watchdog-kept, cancellable — until the quota window resets
    /// and returns true. Returns false when disabled, when no usable reset time
    /// was reported, or on a second limit hit in the same run; the caller then
    /// stops the run.
    /// </summary>
    /// <param name="ct">Cancellation token (user cancel + watchdog).</param>
    /// <returns>True when the wait completed and the download can be retried.</returns>
    /// <summary>
    /// F-M24a (user decision 09.09.2026): the TMDb call trace goes through ONE gate,
    /// Verbose. F-M245 (28.09.2026): it also went through Detail, so at Debug mode every
    /// TMDb line appeared twice — Verbose is a SUBSET of Debug, not a separate tier, and
    /// "additionally at Debug (superset)" was the misconception behind the second call.
    /// Measured on the 28.09. download run: 198 duplicated messages, all TMDb.
    /// </summary>
    private void OnTmdbTrace(string msg)
    {
        LogUtil.Trace(_config.LogMode, _logger, "{Tag} {Msg}", "[SubDL-D]", msg);
    }

    /// <summary>
    /// F-M218: counts a TMDb title search that only matched after the year filter was
    /// dropped. Runs on the resolver's YearFilterMiss event during this run.
    /// </summary>
    /// <param name="title">The title that was searched.</param>
    private void OnTmdbYearFilterMiss(string title)
    {
        var s = _runSummaryForEvents;
        if (s != null)
        {
            s.TmdbYearFilterMisses++;
        }
    }

    /// <summary>F-M24a: per-API-call trace → LogLevel.Debug (never Normal/Verbose).</summary>
    private void OnApiTrace(string msg) => LogUtil.Detail(_config.LogMode, _logger, "[SubDL-D] {Msg}", msg);

    /// <summary>
    /// F-M232: routes the API client's LogInfo channel into the run log (429 raw bodies,
    /// rate headers, login diagnostics). Detail keeps them at the plugin's own Debug mode.
    /// </summary>
    /// <param name="msg">Message from the API client.</param>
    private void OnApiLog(string msg) => LogUtil.Detail(_config.LogMode, _logger, "[SubDL-D] {Msg}", msg);

    private async Task<bool> WaitOutDailyLimitAsync(CancellationToken ct)
    {
        if (!_config.DownloadContinueAfterLimit)
        {
            // F-M152 (rev.10, user decision 17.09.2026): tick OFF = clean stop,
            // NO recovery fire. Items stay due; the next trigger is an arrival
            // event or the weekly anchor. (Old F-M65 fire-on-tick-off removed.)
            LogUtil.Normal(_logger, "[SubDL-D] Daily limit reached - continue-after-limit is OFF: clean stop, no recovery fire (F-M152).");
            return false;
        }

        // (user decision 09.09.2026, option B): arrival runs NEVER wait out the
        // Daily limit — they stop clean; the scheduled runs own the quota wait.
        // The sequential arrival cycle (download THEN upload) would otherwise block
        // the upload direction for hours behind the download wait (verified live).
        if (IsArrivalRun)
        {
            _logger.LogWarning("[SubDL-D] Daily limit (429), arrival run — not waiting, next scheduled run.");
            return false;
        }

        // F-M238 (user decision 28.09.2026): the 429 alone does not prove the
        // allowance is spent — ask the counters first (same source the settings page
        // shows). A trip that left quota free is a rate-limit respacing, not a
        // day-long stop: it gets one fire after JobSpacingMinutes. A spent OR
        // unreadable allowance keeps the day-long path below.
        var read = await _api.ReadQuotaAsync(forDownload: true, ct).ConfigureAwait(false);
        if (read == QuotaRead.Available)
        {
            _api.ResetRateLimitFlag();
            ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleOverloadFire(upload: false, DateTime.UtcNow);
            _logger.LogWarning(
                "[SubDL-D] 429 is a short-term rate limit — download allowance still has {Remaining}/{Limit} left; "
                + "run respaced by JobSpacingMinutes, item stays due (F-M238).",
                _api.Quota?.DownloadsRemaining, _api.Quota?.DownloadsLimit);
            return false;
        }

        var reset = _api.RateLimitResetUtc;

        // F-M49/F-M54 (user decision 08.09.2026 evening): the daily-limit 429 carries
        // NO reset header in practice (verified live) — fall back to the next
        // UTC midnight (the quota resets per UTC day, panel-verified 09.09.2026).
        if (!reset.HasValue)
        {
            var nowUtc = DateTime.UtcNow;
            reset = nowUtc.Date.AddDays(1); // next 00:00 UTC
            LogUtil.Normal(_logger, 
                "[SubDL-D] Daily limit reached without a server-reported reset time — waiting until {ResetUtc} UTC (next UTC midnight) instead.",
                reset.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_dailyLimitWaits >= 1)
        {
            _logger.LogWarning("[SubDL-D] Daily limit again — run stopped, next scheduled run.");
            return false;
        }

        _dailyLimitWaits++;

        // (user decision 09.09.2026 "Neustart nach daily limit von random 2..4h nach
        // Offiziellem Reset"): resume 5-30 min AFTER the official reset (,
        // 11.09.2026: quota resets roll, big offset no longer needed) — not exactly at it —
        // every API consumer hammers SubDL at 00:00 UTC (verified: the 02:05 post-reset
        // run died on a service_busy 429 five minutes after reset, panel showed 24/2000).
        // Same anti-synchronization rationale as F-M51's per-installation anchors.
        var effectiveReset = _api.Edge429NoHeaders || !_api.RateLimitResetUtc.HasValue
            ? NextMidnightUtc() // F-M152 rev.10: Edge 429 (no headers) - fail-safe anchor
            : _api.RateLimitResetUtc.Value; // exact server reset (v2 headers)
        // F-M152 rev.10: no local dicing - the coordinator adds 30..300 min
        // per fire (direction-independent, crypto RNG).

        string resetLocal = TimeZoneInfo.ConvertTimeFromUtc(effectiveReset, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var fireInfo = ScheduledTasks.SubdlSchedulerCoordinator.Instance?.GetPendingFireUtc(upload: false);
        _logger.LogWarning(
            "[SubDL-D] Daily download limit (429) — run stopped. Reset ~{ResetLocal} local; recovery fire ~{Next} local (F-M152, F-M182).",
            resetLocal,
            fireInfo.HasValue
                ? TimeZoneInfo.ConvertTimeFromUtc(fireInfo.Value, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : "next scheduled run");

        // F-M152: NO in-run wait — the task stops clean and the SchedulerCoordinator
        // re-fires this direction once at anchor + jitter (30..300 min, diced per fire —
        // the coordinator's dice; a plain deferral uses JobSpacingMinutes instead).
        // F-M182 (user report 23.09.2026): this used ScheduleRecoveryFireAt, which sets
        // alreadyJittered:true — the fire landed EXACTLY on the reset (verified live:
        // 21.09. download, reset 02:00 local → fire 02:00 local, +0 min) while the log
        // claimed "+30..300 min jitter". SubDL reports reset_at = 00:00 UTC for every
        // consumer, so a jitter-free fire walks straight into the collective-restart
        // herd the jitter exists to avoid. ScheduleRecoveryFire leaves the dicing to the
        // coordinator (30..300 min, crypto RNG, per fire, direction-independent).
        ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFire(upload: false, effectiveReset.ToUniversalTime());
        return false;
    }

    private async Task ProcessItemAsync(BaseItem item, List<string> targets, DownloadRunSummary summary, SkipFilter skipFilter, TimeSpan refetchGap, CancellationToken ct)
    {
        string? mediaPath = item.Path;
        _searchFnTitle = null;
        _searchFnYear = null;
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            summary.SkippedItems++;
            summary.SkippedByFilter++;
            return;
        }

        // F-M60 (user decision 09.09.2026): file gone from disk but item still in the
        // catalog — after FileRetryLimit consecutive failures skip as file-missing
        // (aggregate, no per-run errors). Success resets the shared counter.
        if (_fileRetries.IsExhausted(item.Id.ToString(), _config.FileRetryLimit))
        {
            summary.SkippedFileMissing++;
            // Remove the media and subtitle state for the missing file.
            //
            // F-M22 (defect fixed 02.10.2026): NOT in a dry run. This method returns from the
            // file-missing branch above every dry-run exit in the file (the exits sit per language,
            // far below), so a dry run could DELETE rows for an item it merely reported on. F-M22
            // names both directions of the rule: "No stored verdict is written **or cleared** by a
            // dry run." Clearing is the destructive half — the item's whole registry state went,
            // and unlike a wrong stamp there is nothing to re-derive it from but a full re-scan.
            var missingHash = Registry.GetMediaHash(mediaPath);
            if (!string.IsNullOrEmpty(missingHash) && !_config.DownloadDryRun)
            {
                Registry.MarkAndFlush(() => Registry.DeleteMediaAndSubtitles(missingHash));
            }

            return;
        }

        // (10.09.2026, user decision): one probe write per directory PER RUN,
        // lazily on first contact — runs BEFORE any API search/download work for this
        // item. A read-only directory short-circuits the whole item (and every later
        // item in the same directory) with a single clear log line instead of a
        // per-file write failure + retry loop. The verdict never outlives the run.
        string itemDir = Path.GetDirectoryName(mediaPath) ?? ".";
        if (_readonlyDirs.Contains(itemDir))
        {
            summary.SkippedItems++;
            summary.SkippedByFilter++;
            return;
        }

        if (!_probedDirs.Add(itemDir))
        {
            // already probed this run and found writable — fall through
        }
        else if (!await ProbeWritableAsync(itemDir, ct).ConfigureAwait(false))
        {
            summary.SkippedItems++;
            summary.SkippedByFilter++;
            return;
        }

        if (!File.Exists(mediaPath))
        {
            // F-M22 (defect fixed 02.10.2026): a dry run does not burn a retry. The counter is
            // stored state that decides when this item is given up on — and the give-up branch
            // DELETES its registry rows. Letting a report-only run advance that count means a dry
            // run can be the reason an item loses its state. F-M22 states the rule for the counter
            // by name ("a dry run neither burns a retry nor leaves the retry state stale"); the
            // normal path honours it by recording a success, this exit path recorded a failure.
            if (!_config.DownloadDryRun)
            {
                _fileRetries.RecordFailure(item.Id.ToString());
            }
            else
            {
                LogUtil.PerItem(_config.LogMode, _logger, "[SubDL-D] DRY-RUN {File} is missing — retry counter left untouched (F-M22)", Path.GetFileName(mediaPath));
            }

            summary.Failed++;
            ReportOutcome(item, ItemOutcome.RealFailure);
            LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] FILE MISSING {File} — skip after {Limit} consecutive attempts", Path.GetFileName(mediaPath), _config.FileRetryLimit);

            return;
        }

        // F-M60: file readable — reset the consecutive-failure counter.
        _fileRetries.RecordSuccess(item.Id.ToString());

        // F-M263 (user decision 30.09.2026): the language gate is NOT called here any more.
        // It was redundant: the seeder resolves untagged tracks before the queue decision, and there
        // is no way into this pipeline that skips that scan — `SeedAndMaybeRunAsync` seeds first and
        // only then calls `RunDirectionAsync`, and every trigger goes through it (scheduled task,
        // arrival, follow-up, manual). So by the time an item is worked here, the seeder has already
        // corrected its container AND moved its registry rows. Calling the gate again found nothing
        // to do; what it could still do was harmful, because this call site passed NO registry
        // (`new LanguageTagGate(_logger, _config)`) and therefore got no hash pair back — a write
        // here would have left the file with two identities and the download mark on the dead one,
        // the exact defect F-M261's `ReplaceMediaIdentity` exists to prevent. And with a dry run
        // armed it rewrote media files before the first dry-run check in this method was reached.
        // The coverage check below reads the seeder's result from the REGISTRY instead.

        // F-M88c + F-M234 (B): media content hash for file-complete short-circuit.
        string? mediaHash = Registry.GetMediaHash(mediaPath);

        // F-M22 (defect fixed 02.10.2026): BOTH registry writes below are STORED STATE and run
        // ahead of the dry-run exit (which sits per language, far below this method's pro-item
        // section). A dry run must write no stored verdict, so the pair moves behind one guard.
        // The hash itself is still read — the coverage check needs it either way; only the
        // WRITING is held back. In a real run the behaviour is unchanged.
        if (!_config.DownloadDryRun)
        {
            if (!string.IsNullOrEmpty(mediaHash))
            {
                Registry.MarkAndFlush(() => Registry.EnsureMedia(mediaHash, item.Id.ToString("D"), mediaPath));
            }

            // F-M257: record what the container carries, before any work decision is taken. The
            // download side asks what subtitle data are covered (F-M282); on an install where the
            // uploader never runs that area was empty, so the question was answered "absent" forever
            // for a track sitting right there. This runs ahead of the open-pair check on purpose: a
            // settled item still has facts worth having — but only a real run records them.
            ObserveEmbeddedFacts(item, mediaHash);
        }

        // F-M283 (user decision 02.10.2026): the download mark is GONE, and with it the short-circuit
        // this block used to be. It read a stored verdict, then re-derived a list of "open tokens"
        // from a DIFFERENT set of evidence than the check that had written it — so a variant embedded
        // in the container had a registry verdict and no sidecar, the withhold check refused the mark,
        // the mark was never written, and the item stayed due forever while the regular subtitle was
        // re-fetched on every pass.
        //
        // The question is now asked once, at the grain of the subtitle datum: which of the required
        // PAIRS have no evidence? Both answers — "is this file done" and "may it be marked done" —
        // were always the same question, and with the mark deleted there is only one of them left.
        var required = Jellyfin.Plugin.SubdlScribe.Registry.SubtitleRef
            .Required(TargetLanguages, _config.DownloadHearingImpaired);
        var openPairs = Registry.OpenPairs(mediaPath, required, EmbeddedPresentLanguagesOf(item));

        var openStale = openPairs
            .Where(r => !_qaFails.IsExhausted(item.Id.ToString(), r.Language, _config.DownloadQaRetryLimit))
            .ToList();

        if (openStale.Count == 0)
        {
            // Everything required has evidence, or SubDL settled it as unavailable. Nothing to do —
            // and because the answer is derived, it cannot be stale: deleting a subtitle puts its
            // pair back in this list on the next ask, with no invalidation step and no stored stamp
            // to be overtaken. That is why `MarksInvalidated` is gone: there was never a mark to
            // invalidate, only a deletion whose evidence disappeared.
            summary.SkippedItems++;
            LogUtil.PerItem(_config.LogMode, _logger, "[SubDL-D] SKIP {File} — nothing open", Path.GetFileName(mediaPath));
            return;
        }

        if (openPairs.Count > openStale.Count)
        {
            // Something is open but settled-as-unavailable. Only informative: the QA budget is not a
            // give-up, it says "do not ask SubDL for this again this cycle".
            LogUtil.PerItem(
                _config.LogMode,
                _logger,
                "[SubDL-D] open but QA-exhausted {File}: {Pairs}",
                Path.GetFileName(mediaPath),
                string.Join(",", openPairs.Select(r => r.ToString())));
        }

        // F-M47: per-item refetch gate — never-searched items (new arrivals) pass
        // immediately; already-searched items only when the interval has elapsed
        if (!_searchTracker.IsDue(item.Id.ToString(), refetchGap, targets))
        {
            summary.SkippedItems++;
            summary.SkippedNotDue++;
            return;
        }

        // REMOVED (user decision 11.09.2026): move detection was dropped —
        // a moved file is simply treated as new content; MissingLanguages (file
        // existence, zero API cost for present SRTs) drives what is searched.

        // F-M34/M35: global skip filters (same as upload)
        string? skip = skipFilter.GetSkipReason(mediaPath);
        if (skip != null)
        {
            summary.SkippedItems++;
            summary.SkippedByFilter++;
            LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] SKIP {File} — {Reason}", Path.GetFileName(mediaPath), skip);

            return;
        }

        // F-M45: hard mode → no id, no download; soft mode → title-based search.
        // F-M66 (user decision 09.09.2026, replaces the 15-min wait): id resolution
        // in ONE pass whenever the IMDb ID is missing — 60 s metadata wait (arrival
        // only, 30 s heartbeat loop), then the TMDB ladder (id→IMDB, title search),
        // then requeue-with-counter.
        // F-M151b: the download ID quality gate — first validate/correct JF ids against
        // TMDb so JF and TMDb point at the same title. No switch: it is the standard path,
        // and the run does not start without the TMDb key it depends on (F-M203).
        var (imdbId, tmdbId, season, episode, isSeries) = ResolveIds(item);

        // F-M217 (27.09.2026, user decision "immer typneutral suchen und uns nicht auf
        // die JF-Klassifizierung einlassen"): the FILE NAME is the authority for the
        // content TYPE and for the episode numbers. F-M190 already established that
        // for the upload side ("an episode in a 'movies' library is imported as a
        // Movie") and MediaNameParser was written for it — but the download side
        // never applied it. Consequences measured 27.09.2026 on the test library:
        // an episode sitting in a library typed movies arrives as a Movie, so
        // ResolveIds reports season=0/episode=0 and the item NAME is the raw file
        // name ("Reacher.S03E01"), which the type-neutral TMDb search/multi cannot
        // match (0 hits for "Reacher.S03E01", immediate hits for "Reacher"). Both
        // broke the run before it started; the pack fix (F-M215) could never fire
        // because its guard needs season/episode > 0.
        var parsedName = MediaNameParser.Parse(mediaPath);
        if (parsedName.IsSeries)
        {
            if (!isSeries)
            {
                isSeries = true;
                summary.TypeCorrectedByFileName++; // F-M218
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[SubDL-D] type from file name (not Jellyfin) {File} — series, name states S{Season}E{Episode}",
                    Path.GetFileName(mediaPath), parsedName.Season ?? 0, parsedName.Episode ?? 0);
            }

            if (parsedName.Season is int ps && ps > 0)
            {
                season = ps;
            }

            if (parsedName.Episode is int pe && pe > 0)
            {
                episode = pe;
            }
        }

        // Title that every lookup below asks about: the series name for episodes,
        // the parsed file-name title when the name itself stated the series (the
        // JF NAME is the raw file name in exactly the mis-typed case this fixes).
        string title = isSeries ? (item as Episode)?.Series?.Name ?? item.Name : item.Name;
        if (parsedName.IsSeries && parsedName.Title is { Length: >= 2 } parsedTitle)
        {
            title = parsedTitle;
        }

        // F-M203 (25.09.2026, user decision "TMDb-Key wird verpflichtend, nur Serien
        // verweigern"): a SERIES cannot be matched without TMDB — the show id that both
        // the SubDL search and the series/episode pairing need comes from the key. Films
        // keep working. Same fail-closed rule as the upload side.
        // F-M231 (27.09.2026, user decision "Wir testen jfs id immer gegen tmdb"): the FILE
        // NAME's year wins for the id test — Jellyfin's year comes from the same metadata
        // that may be pinned to the wrong title, so asking with it can only confirm the
        // mistake. Mayday: nfo year 2003 (documentary) vs. name year 2026 (the film); the
        // test needs 2026 to see anything but the wrong series.
        int? idTestYear = parsedName.Year ?? ((item as Movie)?.ProductionYear is int ipy ? ipy
            : (item as Episode)?.Series?.ProductionYear is int isy ? isy
            : null);

        if (!string.IsNullOrWhiteSpace(imdbId) || !string.IsNullOrWhiteSpace(tmdbId))
        {
            // F-M231: ALWAYS test Jellyfin's ids against TMDb by TITLE and YEAR before using
            // them. ResolveAndValidateIdsAsync below only asks whether the two ids describe
            // the same entity — a wrongly pinned pair passes that, so it cannot catch an
            // item whose metadata points at the wrong title.
            var verified = await _tmdb.VerifyIdsAgainstTmdbAsync(imdbId, tmdbId, title, idTestYear, isSeries, ct).ConfigureAwait(false);
            if (verified.Corrected)
            {
                imdbId = verified.Imdb;
                tmdbId = verified.Tmdb;
                isSeries = verified.IsSeries;
                summary.TypeCorrectedByFileName++;
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[SubDL-D] TMDb id test corrected the item — tmdb={Tmdb} imdb={Imdb} {File}.",
                    tmdbId ?? "-", imdbId ?? "-", Path.GetFileName(mediaPath));
            }

            // title comes from the F-M217 block above (file name wins).
            int? year = (item as Movie)?.ProductionYear is int py ? py
                : (item as Episode)?.Series?.ProductionYear is int sy ? sy
                : null;
            var (correctedImdb, correctedTmdb) = await _tmdb.ResolveAndValidateIdsAsync(imdbId, tmdbId, title, year, isSeries, ct).ConfigureAwait(false);
            if (correctedImdb != null || correctedTmdb != null)
            {
                imdbId = correctedImdb ?? imdbId;
                tmdbId = correctedTmdb ?? tmdbId;
                _idNotFound.RecordSuccess(item.Id.ToString());
            }
            else
            {
                imdbId = null;
                tmdbId = null;
            }
        }

        if (string.IsNullOrWhiteSpace(imdbId))
        {
            // F-M66: the "requeue with not-found +1" budget applies only to items
            // with NO id at all — exhausted ones skip the whole ladder (incl. the
            // 60 s wait and the TMDB calls). An item whose metadata improved (now
            // carries a TMDB id) always gets the ladder again.
            if (string.IsNullOrWhiteSpace(tmdbId) && _idNotFound.IsExhausted(item.Id.ToString(), _config.IdRetryLimit))
            {
                summary.SkippedItems++;
                summary.SkippedNoId++;
                summary.SkippedIdGaveUp++;
                return;
            }

            // (user decision 12.09.2026 "Ne. Direkt starten."): the 60 s
            // metadata wait is GONE — arrival runs go straight to the TMDB ladder
            // when the JF ids are empty. The ladder itself (tmdb->imdb, title
            // search, filename parse) is fast and idempotent; JF metadata that
            // arrives late is picked up by the next run's ladder re-run.

            // F-M66: TMDB ladder now runs in BOTH directions without RequireImdb gating —
            // "if tmdb available, fetch imdb" (user decision 09.09.2026): TMDB→IMDB via key,
            // then a title search.
            if (string.IsNullOrWhiteSpace(imdbId) && !string.IsNullOrWhiteSpace(tmdbId) && _tmdb.IsConfigured)
            {
                imdbId = await _tmdb.ResolveImdbAsync(tmdbId, isSeries, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(imdbId) && _tmdb.IsConfigured)
            {
                // title comes from the F-M217 block above (file name wins).
                int? year = (item as Movie)?.ProductionYear is int py ? py
                    : (item as Episode)?.Series?.ProductionYear is int sy ? sy
                    : null;

                // F-M219 (27.09.2026): the TYPE-NEUTRAL search runs FIRST, like the upload
                // side (F-M190). The typed search below searches one endpoint only, so it
                // inherits whatever type Jellyfin guessed — the very assumption F-M217
                // stopped trusting. A name-detected series that Jellyfin typed as a Movie
                // therefore asked search/movie and could not match. The multi-search lets
                // TMDB decide the type, and its year check keeps that from costing
                // precision on ambiguous titles. The typed search stays as the second
                // rung, where a known type plus the year is the sharper query.
                var multi = await _tmdb.ResolveByMultiSearchAsync(title, year, isSeries, ct).ConfigureAwait(false);
                if (multi.Found)
                {
                    imdbId = multi.Imdb;
                    tmdbId = multi.Tmdb;
                    if (multi.IsSeries != isSeries)
                    {
                        isSeries = multi.IsSeries; // TMDB decides, not Jellyfin
                        summary.TypeCorrectedByFileName++; // F-M218
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL-D] TMDb resolved this as a {Kind} — type corrected {File}.",
                            multi.IsSeries ? "series" : "movie", Path.GetFileName(mediaPath));
                    }
                }
                else
                {
                    (imdbId, tmdbId) = await _tmdb.ResolveImdbByTitleAsync(title, year, isSeries, ct).ConfigureAwait(false);
                }

                // (user decision 11.09.2026): filename fallback — when the
                // JF title search failed, parse the RELEASE FILENAME (the most
                // honest data: user-renamed files, JF wrong content-type) and
                // retry with the parsed title/year/type. Fixes e.g. "New Zealand
                // Spy 2026 S01E01 …" searched as movie (JF typed it as movie) →
                // 0 results, while search/tv finds it instantly.
                if (string.IsNullOrWhiteSpace(imdbId) && string.IsNullOrWhiteSpace(tmdbId))
                {
                    var (fnTitle, fnYear, fnIsSeries) = ParseFileNameForIds(Path.GetFileName(mediaPath));
                    if (fnTitle != null && (fnIsSeries != isSeries || fnTitle != title))
                    {
                        (imdbId, tmdbId) = await _tmdb.ResolveImdbByTitleAsync(fnTitle, fnYear, fnIsSeries, ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(imdbId))
                        {
                            isSeries = fnIsSeries; // the filename knows the content type
                            summary.IdResolvedFromFilename++;
                            LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] filename fallback {File} — title \"{Title}\" ({Kind} {Year}) → imdb={Imdb}",
                                Path.GetFileName(mediaPath), fnTitle, fnIsSeries ? "tv" : "movie", fnYear, imdbId);
                        }
                    }
                }
            }

            // (user decision 12.09.2026): the filename TITLE search is the LAST
            // rung of the ladder — but ONLY when "IMDb/TMDB required" is OFF. When the
            // checkbox is on (hard mode), no id = no search (fail-closed, unchanged).
            // Soft mode: one film_name search with the parsed filename before the
            // no-id skip — if it finds candidates, process them like any other item;
            // only a miss lands the item in the no-id requeue.
            if (string.IsNullOrWhiteSpace(imdbId) && string.IsNullOrWhiteSpace(tmdbId))
            {
                if (_config.DownloadRequireImdb)
                {
                    // F-M22 (defect fixed 02.10.2026): a dry run does not spend the item's
                    // id-resolution budget. This counter's limit (IdRetryLimit) is what gives an
                    // item up, and the give-up is a STORED verdict a later run reads — so three dry
                    // runs could retire an item that a real run never touched. The success side is
                    // deliberately NOT guarded: recording a success clears stale state instead of
                    // creating it, which is the direction F-M22 sanctions ("neither burns a retry
                    // nor leaves the retry state stale").
                    if (!_config.DownloadDryRun)
                    {
                        _idNotFound.RecordFailure(item.Id.ToString());
                    }

                    summary.SkippedItems++;
                    summary.SkippedNoId++;
                    if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger, 
                            "[SubDL-D] SKIP {File} — no id resolvable (wait {Wait}; TMDB ladder failed) — requeued, not-found #{Count}",
                            Path.GetFileName(mediaPath), "none", _idNotFound.IsExhausted(item.Id.ToString(), _config.IdRetryLimit) ? "limit" : "+1");
                    }

                    return;
                }

                // (16.09.2026, user decision): v1 soft mode restored — parse
                // the filename title and let the v1 search run with film_name
                // (HEAD 8a17aed behavior). had disabled this.
                var (fnTitle, fnYear, fnIsSeries2) = ParseFileNameForIds(Path.GetFileName(mediaPath));
                if (fnTitle != null)
                {
                    isSeries = fnIsSeries2;
                    imdbId = string.Empty;
                    tmdbId = string.Empty;
                    if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        if (_config.LogMode >= LogLevelMode.Verbose)
                        {
                            LogUtil.PerItem(_config.LogMode, _logger, "[SubDL-D] filename title search (F-M143, soft mode) {File} — \"{Title}\" ({Kind} {Year})",
                            Path.GetFileName(mediaPath), fnTitle, isSeries ? "series" : "movie", fnYear);
                        }
                    }
                    _searchFnTitle = fnTitle;
                    _searchFnYear = fnYear;
                }
                else
                {
                    LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] id-less item (soft mode) {File} — no parsable title, search skipped",
                        Path.GetFileName(mediaPath));
                }
            }

            _idNotFound.RecordSuccess(item.Id.ToString()); // resolved via wait or TMDB
        }

        // F-M66: the ladder above already resolved TMDB→IMDB and ran the title
        // search when the item arrived id-less. Items that entered this method
        // WITH a tmdb id (JF metadata) still need the IMDB fetch here.
        if (string.IsNullOrWhiteSpace(imdbId) && _tmdb.IsConfigured && !string.IsNullOrWhiteSpace(tmdbId))
        {
            imdbId = await _tmdb.ResolveImdbAsync(tmdbId, isSeries, ct).ConfigureAwait(false);
        }

        // F-M45: hard mode → an id-less item (no IMDB, no TMDB at all) does not
        // search SubDL. Soft mode (DownloadRequireImdb=false) continues with the
        // TMDB id alone — SubDL search accepts TMDB ids as fallback (F-M45).
        if (string.IsNullOrWhiteSpace(imdbId) && string.IsNullOrWhiteSpace(tmdbId) && _config.DownloadRequireImdb)
        {
            summary.SkippedItems++;
            summary.SkippedNoId++;
            LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] SKIP {File} — no id resolvable (after wait + TMDB)", Path.GetFileName(mediaPath));

            return;
        }

        // F-M282 (user decision 02.10.2026): the two search pools are fed from the PAIRS, separately.
        //
        // This is the spot that produced the wasted downloads. The old code asked which LANGUAGES had
        // no variant and appended them to the same `missing` list that feeds the `hi=0` search — so a
        // missing German variant made the regular German subtitle "missing", the search ran with
        // `hi=0` on a language whose .de.srt had been on disk for days, and the file was re-fetched.
        // SubDL serves the two sides from separate, non-overlapping pools (F-M241), so they must be
        // fed separately here: `regularMissing` drives `&hi=0`, `variantMissing` drives `&hi=1`.
        var requiredPairs = RequiredPairs(targets);
        var openPairsNow = Registry.OpenPairs(mediaPath, requiredPairs, EmbeddedPresentLanguagesOf(item));

        var regularMissing = Jellyfin.Plugin.SubdlScribe.Registry.SubtitleCoverage
            .RegularLanguages(openPairsNow);
        var variantMissing = Jellyfin.Plugin.SubdlScribe.Registry.SubtitleCoverage
            .VariantLanguages(openPairsNow);
        var missing = new List<string>(regularMissing);

        if (missing.Count == 0 && variantMissing.Count == 0)
        {
            summary.SkippedItems++;
            summary.SkippedNothingMissing++;
            // F-M283: nothing open means nothing to fetch. No mark is written any more — the question
            // at the top of this method IS the answer, asked fresh every run, so there is no stored
            // verdict left to settle or to contradict.
            return;
        }

        // The variant pool is asked even when nothing regular is missing: that is the whole point of
        // judging the datum instead of the language. A file with its German on disk but no German
        // variant has work to do, and it must not be reached by re-requesting German.
        if (missing.Count == 0)
        {
            LogUtil.PerItem(
                _config.LogMode,
                _logger,
                "[SubDL-D] {File} — only the variant is open: {Pairs} (regular files present, no re-fetch)",
                Path.GetFileName(mediaPath),
                string.Join(",", openPairsNow.Select(p => p.ToString())));
        }

        // F-M88c: track which target languages reached a terminal state this run
        // (downloaded, not-available, or QA-exhausted). Used at the end to decide
        // whether the whole file can be marked complete.
        var closedLangs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // QA-exhausted (item, language) pairs are removed BEFORE the search —
        // their candidates passed the id/release gates but fell through the QA gates
        // too often (e.g. stub items with broken JF runtimes). No search call, no
        // fetches, counter persists until a save succeeds or the limit changes.
        int qaSkippedLangs = missing.RemoveAll(lang => _qaFails.IsExhausted(item.Id.ToString(), lang, _config.DownloadQaRetryLimit));
        if (qaSkippedLangs > 0)
        {
            summary.QaGiveUpLanguages += qaSkippedLangs;
            if (_config.LogMode >= LogLevelMode.Verbose)
            {
                LogUtil.PerItem(_config.LogMode, _logger, 
                    "[SubDL-D] {File} — {N} language(s) skipped (QA retry limit {Limit} reached): {Langs} — no search, no fetch until a save succeeds",
                    Path.GetFileName(mediaPath), qaSkippedLangs, _config.DownloadQaRetryLimit, string.Join(",", missing));
            }
            if (missing.Count == 0)
            {
                summary.SkippedItems++;
                summary.SkippedQaGiveUp++;
                return;
            }
        }

        // F-M20/F-M26: the configured rate paces this search; the plugin holds no budget of
        // its own. A per-run hourly bucket sat here until 03.10.2026 and could stop the whole run
        // via `_stopRun`. It was our own invention — SubDL publishes DAILY counters only — and it
        // stopped runs the server would have allowed (735 of 2000 searches still free when it
        // fired). Enforcement belongs to SubDL: a real 429 is answered below by the
        // QuotaStopDecision path, which reads the live counters before it decides.
        // The pause stays (F-M48 fix 08.09.2026): without it the search path fired up to 100
        // requests in ~9 s, SubDL answered 429 and every result was silently discarded as
        // "no candidates".
        await _limiter.PauseAsync(ct).ConfigureAwait(false);
        // (16.09.2026, user decision): this pause is the only one before a search. An
        // extra delay sat here once and doubled it — every search waited 2× MinCallPauseSec.

        // One search per item with the server-side filters (season, episode, target
        // languages) — strict, no fallbacks (user decision 08.09.2026). The client-side
        // episode filter stays as a second safety net.
        // F-M58 (09.09.2026): a transient service_busy 429 (retryAfterSeconds) is
        // NOT the daily limit — wait the short window and retry the SAME search up
        // to 3 times before giving up. Only a real daily-limit 429 aborts the run.
        _watchdog?.Heartbeat();        // (16.09.2026, user decision): v2 filename batch (BACKUP)
        // REMOVED — the v1 MAIN search already carries the title anchor as its
        // Last rung (soft mode: parsed filename title → plain JF name),
        // so a filename-similarity second pass adds nothing but extra requests.
        // Main path is the episode-exact v1 id search (GET /api/v1/subtitles by
        // imdb/tmdb/film_name + season/episode, unpack=1); No v2 search remains
        // in the download path.
        var (candidates, usedBackup, thresholdPassCount, hiCandidates) = await RunV2SearchAsync(
            item, mediaPath, imdbId, tmdbId, season, isSeries ? episode : 0, missing, variantMissing, summary, ct);

        const int maxTransientRetries = 3;
        int transientAttempt = 0;
        while (candidates == null && _api.TransientOverload && transientAttempt < maxTransientRetries)
        {
            // F-M152 (rev.10): NO /me diagnostic call - a service_busy 429 is
            // server overload (or Edge throttling); the run retries briefly and
            // stops with the overload fire if it persists.
            transientAttempt++;
            int waitSec = Math.Max(5, _api.TransientRetrySeconds);
            _logger.LogWarning(
                "[SubDL-D] SubDL transiently overloaded (429 service_busy) at {File} — waiting {Sec}s, retry {Attempt}/{Max}. Not the daily limit; run continues.",
                Path.GetFileName(mediaPath), waitSec, transientAttempt, maxTransientRetries);
            await Task.Delay(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false);
            _watchdog?.Heartbeat();
            _api.ResetRateLimitFlag(); // clear the transient marker before the retry
            (candidates, usedBackup, thresholdPassCount, hiCandidates) = await RunV2SearchAsync(
                item, mediaPath, imdbId, tmdbId, season, isSeries ? episode : 0, missing, variantMissing, summary, ct);
        }

        // F-M52: requirement marker added for traceability.
        // F-M48 fix (08.09.2026): null = search did not complete (429/5xx/403) — do NOT
        // mark the item "searched"; abort the run so this and all remaining items
        // stay due for the next run instead of being poisoned with garbage results.
        if (candidates == null)
        {
            _stopRun = true;
            if (_api.AuthBroken)
            {
                _logger.LogError("[SubDL-D] API key rejected (403) — run stopped at {File}, check credentials.",
                    Path.GetFileName(mediaPath));
            }
            else if (_api.TransientOverload)
            {
                // F-M152 (rev.10): NO /me diagnostic call — service_busy is real
                // server overload; the recovery fire is the short overload fire.
                ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleOverloadFire(upload: false, DateTime.UtcNow);
                _logger.LogWarning(
                    "[SubDL-D] SubDL still overloaded (429 service_busy) after {Max} retries — stopping run at {File}; item stays due for the overload fire.",
                    maxTransientRetries, Path.GetFileName(mediaPath));
            }
            else
            {
                // F-M238: a 429 on the search endpoint is not automatically the daily
                // allowance — SubDL answers the same code for a short-term trip. Ask the
                // live counters which one it is, then let the decision name the retry
                // shape. A spent OR unreadable allowance earns the day-long fire; only a
                // free allowance is respaced. Both day-long cases MUST schedule, or the
                // item waits for the next regular anchor instead of the reset.
                var read = await _api.ReadQuotaAsync(forDownload: false, ct).ConfigureAwait(false);
                bool searchExhausted = read == QuotaRead.Spent;
                bool searchUnreadable = read == QuotaRead.Unreadable;
                var stop = QuotaStopDecision.Decide(
                    _config.DownloadContinueAfterLimit,
                    IsArrivalRun,
                    searchExhausted,
                    searchUnreadable);

                if (stop == QuotaStop.Respaced)
                {
                    _api.ResetRateLimitFlag();
                    _stopRun = false;
                    ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleOverloadFire(upload: false, DateTime.UtcNow);
                    _logger.LogWarning(
                        "[SubDL-D] 429 at {File} is a short-term rate limit — search allowance still has {Remaining}/{Limit} left; "
                        + "run respaced by JobSpacingMinutes, item stays due (F-M238).",
                        Path.GetFileName(mediaPath), _api.Quota?.SearchRemaining, _api.Quota?.SearchLimit);
                }
                else if (stop == QuotaStop.NextDayFire)
                {
                    // Count the stop, or the dispatcher cannot tell it from an "ok" cycle: it would
                    // paint the download light GREEN and mark every item this run never worked as
                    // Done, removing it from the queue. `QuotaStopped` is the only flag the
                    // dispatcher reads (SubdlEventDispatcher.CleanupDirectionQueue).
                    summary.QuotaStopped++;
                    // Day-long: anchor on the server's reset (or the fail-safe midnight),
                    // exactly like the download-429 path. Without this the search path
                    // stopped without scheduling anything and nothing retried until the
                    // next regular anchor.
                    var anchor = _api.RateLimitResetUtc ?? NextMidnightUtc();
                    ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFire(upload: false, anchor.ToUniversalTime());
                    _logger.LogWarning(
                        "[SubDL-D] Search allowance spent ({Reason}) — run stopped at {File}; one recovery fire after the reset at {Reset} local (F-M238).",
                        searchExhausted ? "counter says spent" : "allowance unreadable, treated as spent",
                        Path.GetFileName(mediaPath),
                        TimeZoneInfo.ConvertTimeFromUtc(anchor, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    // A spent allowance with a clean stop and no fire — still a quota stop (the work
                    // did not happen), so the counter and the queue guard apply.
                    summary.QuotaStopped++;
                    _logger.LogWarning(
                        "[SubDL-D] Search allowance spent — run stopped at {File}; no recovery fire (continue-after-limit is off or arrival run) (F-M238).",
                        Path.GetFileName(mediaPath));
                }
            }

            return;
        }
        // F-M47: search performed (success OR no-candidates) — start the per-item
        // refetch clock. Items with missing languages get re-searched after the
        // configured interval; new arrivals pass immediately. 5xx errors don't count
        // as a search (fail-open empty result above, but the refetch clock stays off).
        //
        // F-M22 (defect fixed 02.10.2026): NOT in a dry run. The stamp is a stored verdict
        // about this item, and a dry run may write none — "what a dry run leaves behind must
        // not change what a later run finds". Without this guard the clock ran anyway, so a
        // dry run made every item it reported read as `not due` for the whole refetch
        // interval: the very run meant to answer "what WOULD happen" silently suppressed the
        // next real one. Unlike the old download mark there is no invalidation pass to undo
        // a wrong stamp — the pipeline trusts it — so this one did not self-heal.
        if (!_config.DownloadDryRun)
        {
            _searchTracker.MarkSearched(item.Id.ToString(), targets);
        }

        // ID sanity check + fallback — compare the searched id with the
        // film header SubDL returned (results[0]) and backfill the missing one.
        // The per-candidate ids are stamped by the client (StampFilmIds).
        // F-M93d-fix (live evidence Moon.2009): only the ID actually SENT to the
        // API decides a mismatch — SubDL search filters server-side on that one
        // id, so its own secondary-id mapping (e.g. a different tmdb_id for the
        // same imdb) is irrelevant and must NOT drop correct candidates.
        string? resolvedImdb = imdbId;
        string? resolvedTmdb = tmdbId;
        if (candidates.Count > 0)
        {
            string? returnedImdb = candidates[0].ImdbId;
            string? returnedTmdb = candidates[0].TmdbId;
            summary.IdChecks++;

            bool searchedByImdb = !string.IsNullOrWhiteSpace(imdbId);
            bool searchedByTmdb = !searchedByImdb && !string.IsNullOrWhiteSpace(tmdbId);

            bool mismatch =
                (searchedByImdb && !string.IsNullOrWhiteSpace(returnedImdb) &&
                 !string.Equals(imdbId!.Trim(), returnedImdb.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                (searchedByTmdb && !string.IsNullOrWhiteSpace(returnedTmdb) &&
                 !string.Equals(tmdbId!.Trim(), returnedTmdb.Trim(), StringComparison.OrdinalIgnoreCase));

            if (mismatch)
            {
                summary.IdMismatches++;
                _logger.LogWarning(
                    "[SubDL-D] ID mismatch {File} — searched imdb={SImdb}/tmdb={STmdb}, SubDL returned imdb={RImdb}/tmdb={RTmdb}; dropping all {N} candidates for this item",
                    Path.GetFileName(mediaPath), imdbId ?? "—", tmdbId ?? "—", returnedImdb ?? "—", returnedTmdb ?? "—", candidates.Count);
                summary.NotAvailable += missing.Count;
                return;
            }

            // Fallback: backfill ids we didn't have from the film header.
            if (string.IsNullOrWhiteSpace(resolvedImdb) && !string.IsNullOrWhiteSpace(returnedImdb))
            {
                resolvedImdb = returnedImdb;
                summary.IdBackfilledFromApi++;
                LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] ID backfill {File} — imdb={Imdb} from SubDL results[0] (searched by tmdb={Tmdb})",
                    Path.GetFileName(mediaPath), returnedImdb, tmdbId);
            }

            if (string.IsNullOrWhiteSpace(resolvedTmdb) && !string.IsNullOrWhiteSpace(returnedTmdb))
            {
                resolvedTmdb = returnedTmdb;
                summary.IdBackfilledFromApi++;
                LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] ID backfill {File} — tmdb={Tmdb} from SubDL results[0] (searched by imdb={Imdb})",
                    Path.GetFileName(mediaPath), returnedTmdb, imdbId);
            }

            if (!string.IsNullOrWhiteSpace(resolvedImdb))
            {
                summary.IdImdbOk++;
            }

            if (!string.IsNullOrWhiteSpace(resolvedTmdb))
            {
                summary.IdTmdbOk++;
            }
        }

        if (candidates.Count == 0)
        {
            summary.NotAvailable += missing.Count;
            summary.SkippedNoCandidates++;
            // F-M183: LogUtil.PerItem gates on Verbose internally, so the run-end
            // Aggregate below is what makes this visible at Normal level (keeps
            // per-item lines out of Normal — 400 items must not mean 400 lines).
            LogUtil.PerItem(_config.LogMode, _logger, "[SubDL-D] {File} — no candidates on SubDL for [{Langs}]", Path.GetFileName(mediaPath), string.Join(",", missing));
            return;
        }

        string videoBase = Path.GetFileNameWithoutExtension(mediaPath);
        double? videoFps = GetVideoFps(item);
        foreach (string lang in missing)
        {
            ct.ThrowIfCancellationRequested();
            var langCands = candidates.Where(c => c.Language == lang).ToList();
            if (langCands.Count == 0)
            {
                summary.NotAvailable++;
                ReportOutcome(item, ItemOutcome.NotAvailable);
                LogUtil.PerItem(_config.LogMode, _logger, "[SubDL-D] {File} [{Lang}] — not available", Path.GetFileName(mediaPath), lang);

                closedLangs.Add(lang); // F-M88c: definitively not available = terminal
                continue;
            }

            // F-M44: weighted release score, best candidate first.
            // F-M241: the pool is ALREADY the regular side (the search ran with hi=0), so no
            // HI filtering happens here any more. The old filter had to strip HI out of a mixed
            // response, which meant the regular ranking silently depended on how many HI releases
            // SubDL happened to return.
            var ranked = langCands
                .Select(c => (Cand: c, Score: Score(c, videoBase)))
                .OrderByDescending(x => x.Score)
                .ToList();

            // Skip candidates already QA-rejected in earlier runs —
            // the next run walks ONLY fresh candidates (user decision 11.09.2026:
            // "remember the 3 unsuitable ones and try 3 different ones next run").
            var qaKnownRejected = _qaFails.GetSkippedCandidates(item.Id.ToString(), lang);
            if (qaKnownRejected.Count > 0)
            {
                // Match by stable per-upload id (exact upload, not release family);
                // candidates without a parsable id stay eligible.
                int beforeMemoryFilter = ranked.Count;
                ranked = ranked.Where(x => string.IsNullOrEmpty(x.Cand.SubdlId)
                                           || !qaKnownRejected.Contains(x.Cand.SubdlId, StringComparer.Ordinal)).ToList();

                // F-M255: the memory filter dropped candidates without a word. Name what it took
                // out, so a shrinking candidate list is never read as a thin search result.
                if (beforeMemoryFilter > ranked.Count)
                {
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[SubDL-D] {File} [{Lang}] — {N} candidate(s) skipped before the walk: QA-rejected in an earlier run ({Ids})",
                        Path.GetFileName(mediaPath), lang, beforeMemoryFilter - ranked.Count,
                        string.Join(",", qaKnownRejected));
                }
            }

            // F-M46 + F-M43 Stufe 1: try candidates in score order until one passes all checks
            bool savedAny = false;
            int attempts = 0;

            // Releases tried and QA-rejected in this run — recorded at run end,
            // so the next run walks fresh candidates only.
            var qaRejectedReleases = new List<string>();
            int savedCount = 0;
            int keepBest = Math.Max(1, _config.DownloadKeepBestPerLanguage);
            int downloadCap = DownloadBudget.EffectiveDownloadCap(
                _config.DownloadMaxCandidatesPerLanguage, keepBest);
            foreach (var (cand, _) in ranked)
            {
                // F-M50: download budget per (item, language) — each candidate download
                // costs daily quota even when rejected afterwards (runtime check), so
                // stop walking after the configured number of failed attempts.
                // F-M242: the cap is raised to the keep-best count when that is higher — a budget
                // below it makes "keep X saves X files" unreachable, and the break below would fire
                // before the Xth slot could ever be filled.
                if (downloadCap > 0 && attempts >= downloadCap)
                {
                    if (savedCount == 0)
                    {
                        summary.NotAvailable++;
                        ReportOutcome(item, ItemOutcome.NotAvailable);
                    }

                    if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger, 
                            "[SubDL-D] {File} [{Lang}] — download budget exhausted ({Max} candidates tried), counting as not available; {N} rejected download(s) memorized (F-M93g/F-M93m), next run takes fresh candidates; retried at the next refetch interval",
                            Path.GetFileName(mediaPath), lang, downloadCap, qaRejectedReleases.Count);
                    }
                    break;
                }

                attempts++;
                // F-M43 Stufe 1 — FPS pre-check (skip criterion when candidate fps unknown)
                if (videoFps.HasValue && cand.Fps > 0 && _config.DownloadFpsTolerancePercent > 0)
                {
                    double diffPct = Math.Abs(cand.Fps - videoFps.Value) / videoFps.Value * 100.0;
                    if (diffPct > _config.DownloadFpsTolerancePercent)
                    {
                        if (_config.LogMode >= LogLevelMode.Verbose)
                        {
                            LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] {File} [{Lang}] — FPS reject {Release}: {CandFps} vs {VidFps} (>±{Tol}%)",
                                Path.GetFileName(mediaPath), lang, cand.ReleaseName, cand.Fps, videoFps.Value, _config.DownloadFpsTolerancePercent);
                        }

                        continue; // next candidate
                    }
                }

                // (15.09.2026): dry-run makes NO download fetch — search +
                // threshold + ranking are logged, QA-Gates/Saves are skipped
                // (real download quota is untouched).
                if (_config.DownloadDryRun)
                {
                    if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        // Compact — no nId (Debug detail), file first. The HI flag of the
                        // candidate and of the alternative is part of the line: which file
                        // would be saved, and whether the HI variant would be fetched, is the
                        // one thing a dry run must be able to answer.
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL-D] DRY-RUN {File} [{Lang}] → {Release} (score {Score}, hi={Hi})",
                            Path.GetFileName(mediaPath), lang, cand.ReleaseName, Score(cand, videoBase),
                            cand.HearingImpaired ? "yes" : "no");
                    }

                    // F-M242: show the slots the run WOULD fill, not just the first candidate.
                    // KeepBestPerLanguage decides how many numbered files a language gets
                    // ("name.en.srt", "name.en.2.srt", …); while the loop was cut short by an
                    // unconditional break the setting had no effect at all, and a dry run that
                    // reported one candidate per language could not reveal that.
                    if (_config.LogMode >= LogLevelMode.Verbose && keepBest > 1)
                    {
                        var slots = ranked.Take(keepBest).ToList();
                        for (int s = 0; s < slots.Count; s++)
                        {
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL-D] DRY-RUN slot {Slot}/{KeepBest} {File} [{Lang}] → {Name}",
                                s + 1, keepBest, Path.GetFileName(mediaPath), lang,
                                // F-M260: same builder as the real save, so the preview cannot drift.
                                Path.GetFileName(SidecarNaming.Build(mediaPath, lang, hearingImpaired: false, slot: s + 1)));
                        }
                    }

                    // F-M241: the HI answer comes from the hi=1 search's own pool. A dry run must
                    // report it, because the real selection happens after the file download, which
                    // a dry run never reaches — without this the run logged nothing about HI.
                    if (_config.DownloadHearingImpaired)
                    {
                        var hiForLang = hiCandidates.Where(c => c.Language == lang).ToList();
                        var hiPick = hiForLang
                            .OrderByDescending(c => Score(c, videoBase))
                            .ThenByDescending(c => c.DownloadCount)
                            .FirstOrDefault();
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL-D] DRY-RUN HI {File} [{Lang}] → {Result} ({HiCount} in the HI pool)",
                            Path.GetFileName(mediaPath), lang,
                            hiPick == null ? "none on SubDL" : hiPick.ReleaseName,
                            hiForLang.Count);
                    }

                    summary.Downloaded++;
                    ReportOutcome(item, ItemOutcome.Done);
                    // F-M22: `savedAny` stays set on purpose although nothing was saved. It is not
                    // the mark's only reader: it also decides whether this language counts as a
                    // FAILED run (the QA retry counter below). Clearing it would make every dry run
                    // burn a retry on every language and give up after the limit. The completion
                    // mark is guarded at its own site instead.
                    savedAny = true;
                    break;
                }

                // F-M26: the pause before a REAL download (download→download), ±30 %.
                // The local call budget that stood here until 03.10.2026 is removed (F-M20): it could
                // only ever stop the run, and the plugin no longer counts its own calls.
                await Task.Delay(_limiter.TransferPauseMs(), ct).ConfigureAwait(false);

                _watchdog?.Heartbeat();
                var (bytes, packFile) = await DownloadCandidateAsync(cand, season, episode, ct).ConfigureAwait(false);

                // F-M260: the file the bytes actually came from. Absent for a whole-candidate
                // download; present for a pack, where it carries that ONE file's own HI flag —
                // the pack header's flag says nothing about its contents (often the opposite).
                // The candidate's own flag is the fallback, because for a single-file release the
                // candidate IS the file.
                bool effectiveHi = packFile?.HearingImpaired ?? cand.HearingImpaired;

                // F-M58 (09.09.2026): transient service_busy 429 during the file download —
                // wait the short window and retry the same candidate up to 3 times.
                // DownloadSubtitleFileAsync already does 3 internal backoff attempts,
                // so this loop only fires when the overload persisted through those.
                const int maxDlTransientRetries = 3;
                int dlTransientAttempt = 0;
                while (bytes == null && _api.TransientOverload && dlTransientAttempt < maxDlTransientRetries)
                {
                    dlTransientAttempt++;
                    int waitSec = Math.Max(5, _api.TransientRetrySeconds);
                    _logger.LogWarning(
                        "[SubDL-D] SubDL transiently overloaded during download of {File} — waiting {Sec}s, retry {Attempt}/{Max}. Not the daily limit; run continues.",
                        Path.GetFileName(mediaPath), waitSec, dlTransientAttempt, maxDlTransientRetries);
                    await Task.Delay(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false);
                    _watchdog?.Heartbeat();
                    _api.ResetRateLimitFlag();
                    (bytes, packFile) = await DownloadCandidateAsync(cand, season, episode, ct).ConfigureAwait(false);
                    effectiveHi = packFile?.HearingImpaired ?? cand.HearingImpaired;
                }

                // F-M49: optional daily-limit resume — wait out the quota window
                // once, then retry the same candidate with the fresh quota.
                if (_api.ServerRateLimited && !_api.TransientOverload && await WaitOutDailyLimitAsync(ct).ConfigureAwait(false))
                {
                    _api.ResetRateLimitFlag();
                    _watchdog?.Heartbeat();
                    (bytes, packFile) = await DownloadCandidateAsync(cand, season, episode, ct).ConfigureAwait(false);
                    effectiveHi = packFile?.HearingImpaired ?? cand.HearingImpaired;
                }

                if (_api.ServerRateLimited && !_api.TransientOverload)
                {
                    // Quota reset info is logged once by WaitForDailyLimitResetAsync; no second line here.
                    summary.QuotaStopped++;
                    _stopRun = true; // F-M65 fix (09.09.2026): a download-429 must END the run, not just this item — otherwise the loop keeps hammering the dead quota item after item.
                    return;
                }

                // F-M58: transient overload persisted through all retries — count the
                // candidate as exhausted (same as a failed download) and move on to
                // the next candidate; the run itself continues.
                if (_api.TransientOverload)
                {
                    _api.ResetRateLimitFlag();
                }

                if (bytes == null || bytes.Length < 100)
                {
                    // F-M286: fetched and thrown away — the bytes came back (or the call answered
                    // without a body) and were discarded, so the request was spent. Counted here,
                    // like every other reject path; F-M255 added the line, F-M286 the number.
                    summary.RejectedCandidates++;
                    // F-M255: no candidate is discarded without a line. This exit had none, so a
                    // fetched-and-thrown-away candidate left no trace at all.
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[SubDL-D] {File} [{Lang}] — download reject {Release}: {Reason}",
                        Path.GetFileName(mediaPath), lang, cand.ReleaseName,
                        bytes == null ? "no bytes returned" : "only " + bytes.Length + " bytes");

                    continue; // download failed → next candidate
                }

                // (user decision 10.09.2026): no pause between a search and its
                // own download — they are ONE transaction. The steered-rate pause runs
                // only BETWEEN transactions (search→search, download→next search,
                // upload→anything). Slot accounting unchanged.
                _watchdog?.Heartbeat();

                // Gate 2: language verification (F-M15) — detected content
                // language vs requested tag; fail-open when CLD2 has no verdict.
                if (_config.QaDownloadVerifyLanguage)
                {
                    string? detected = Qa.QaGates.DetectLanguage(DecodeSrt(bytes));
                    if (detected != null
                        && !string.Equals(detected, lang, StringComparison.OrdinalIgnoreCase)
                        && !Qa.QaGates.IsFamilyMatch(lang, detected))
                    {
                        summary.RejectedCandidates++; // F-M286 (QA gate)
                        qaRejectedReleases.Add(cand.SubdlId); // burned fetch → memorized

                        if (_config.LogMode >= LogLevelMode.Verbose)
                        {
                            LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] {File} [{Lang}] — language reject {Release}: content is {Detected}",
                                Path.GetFileName(mediaPath), lang, cand.ReleaseName, detected);
                        }

                        continue; // next candidate
                    }
                }

                // F-M43 Stufe 2 (structure, fixed part): corruption check on the
                // already-downloaded bytes — free (no extra quota), rejects broken
                // SRTs (no timings, non-monotonic timestamps, implausible cue
                // durations) BEFORE they are saved next to the media file.
                {
                    var srtStats = Qa.QaGates.ParseSrt(DecodeSrt(bytes));
                    bool structureOk = srtStats != null
                        && srtStats.Monotonic
                        && srtStats.MinCueMs > 100
                        && srtStats.MaxCueMs < 600_000;
                    if (!structureOk)
                    {
                        // Burned fetch on a structurally broken file —
                        // remember the exact download, the run counts as failed.
                        summary.RejectedCandidates++; // F-M286 (QA gate)
                        qaRejectedReleases.Add(cand.SubdlId);

                        if (_config.LogMode >= LogLevelMode.Verbose)
                        {
                            LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] {File} [{Lang}] — structure reject {Release}: no valid cues/non-monotonic/implausible duration",
                                Path.GetFileName(mediaPath), lang, cand.ReleaseName);
                        }

                        continue; // next candidate
                    }
                }

                // Gate 1: minimum cue count (≥30, F-M16 threshold) — catches
                // valid-but-stub SRTs the structure gate lets through.
                var gateStats = Qa.QaGates.ParseSrt(DecodeSrt(bytes)); // Fresh parse (srtStats is scoped to the structure block)
                if (_config.QaDownloadMinCues && (gateStats == null || gateStats.CueCount < 30))
                {
                    summary.RejectedCandidates++; // F-M286 (QA gate)
                    qaRejectedReleases.Add(cand.SubdlId); // Burned fetch → memorized

                    if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] {File} [{Lang}] — min-cues reject {Release}: {Count} cues",
                            Path.GetFileName(mediaPath), lang, cand.ReleaseName, gateStats?.CueCount ?? 0);
                    }

                    continue; // next candidate
                }

                // F-M43 Stufe 2 (runtime): cue span vs item runtime
                if (!RuntimeMatches(item, bytes, out string? runtimeReason) && runtimeReason != null)
                {
                    // Burned fetch (stub/broken runtime) —
                    // remember the exact download, the run counts as failed at its end.
                    summary.RejectedCandidates++; // F-M286 (QA gate)
                    qaRejectedReleases.Add(cand.SubdlId);

                    if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] {File} [{Lang}] — runtime reject {Release}: {Reason}",
                            Path.GetFileName(mediaPath), lang, cand.ReleaseName, runtimeReason);
                    }

                    continue; // next candidate
                }

                // F-M296 (development): the auto-sync. Two settings act here.
                //
                // (1) WHICH TRACK the audio gates read. Both audio-reading gates used
                // to decode 0:a:0 with a comment defending the hard index; measured on
                // this library that picks the wrong track in ~9 % of (file, language)
                // cases — typically an Italian release whose first track is the dub.
                // The choice is by language now, unless the switch is off.
                //
                // (2) WHETHER the fetched subtitle is SHIFTED by the constant offset the
                // audio reports, and the original kept beside it. The shift happens
                // BEFORE the hash is computed, because the registered hash must describe
                // the file that lies on disk (user specification). A file whose offset
                // MOVES is never shifted.
                string audioMap = "0:a:0";
                var audioChoice = Qa.AudioTrackChoice.Choose(
                    _mediaSourceManager.GetMediaStreams(item.Id), lang);
                if (_config.QaDownloadAudioTrackByLanguage && audioChoice.TrackCount > 0)
                {
                    audioMap = "0:a:" + audioChoice.Position.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (audioChoice.Priority != 3 || audioChoice.Position != 0)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL-D] {File} [{Lang}] — audio track {Pos} of {N} ({Reason})",
                            Path.GetFileName(mediaPath), lang, audioChoice.Position,
                            audioChoice.TrackCount, audioChoice.Reason);
                    }
                }

                // F-M295 (development): cue-vs-speech drift gate. Runs after the
                // structure/runtime gates (a broken file never reaches the audio
                // decode) and before the write. It answers whether the offset to
                // the spoken audio is CONSTANT — a moving offset has no valid
                // single correction, so saving it as-is plants a subtitle that is
                // right in one part and wrong in another. Off by default; see
                // PluginConfiguration.QaDownloadDriftCheck for the measured limits.
                Qa.DriftVerdict? drift = null;
                string? ffmpegForDrift = null;
                if (_config.QaDownloadDriftCheck || _config.QaDownloadAutoSync)
                {
                    ffmpegForDrift = FfmpegTools.ResolvePath(_config, _logger);
                }

                if (_config.QaDownloadDriftCheck)
                {
                    drift = await Qa.DriftGate.RunAsync(
                        mediaPath, DecodeSrt(bytes), ffmpegForDrift, _logger, ct, audioMap).ConfigureAwait(false);

                    if (drift.Ran && drift.Drifts)
                    {
                        // Reported always — a drifting file is a finding, whether or
                        // not it is rejected, and the line states the SPAN, never a
                        // correction value (the gate cannot supply one).
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL-D] {File} [{Lang}] — drift {Release}: {Verdict}",
                            Path.GetFileName(mediaPath), lang, cand.ReleaseName, drift.Describe());

                        if (_config.QaDownloadDriftReject)
                        {
                            summary.RejectedCandidates++; // F-M286 (QA gate)
                            qaRejectedReleases.Add(cand.SubdlId);
                            continue; // next candidate
                        }
                    }
                    else if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL-D] {File} [{Lang}] — drift gate: {Verdict}",
                            Path.GetFileName(mediaPath), lang, drift.Describe());
                    }
                }

                // F-M296/F-M300: the auto-sync itself. Reuses the verdict the drift gate just
                // produced when that switch is on (one audio decode, not two) and runs the
                // same gate itself when it is not. A CONSTANT offset is applied as one shift;
                // a MOVING one as a staircase, one offset per segment. Only a missing verdict,
                // a step beyond MaxShiftSec or an unusable segment list leaves the file alone.
                byte[] writeBytes = bytes;
                string content = DecodeSrt(bytes);
                string? unsyncPayload = null;
                if (_config.QaDownloadAutoSync)
                {
                    Qa.DriftVerdict syncVerdict = drift
                        ?? await Qa.DriftGate.RunAsync(mediaPath, content, ffmpegForDrift, _logger, ct, audioMap)
                            .ConfigureAwait(false);

                    double shift = syncVerdict.Ran && !syncVerdict.Drifts ? syncVerdict.MedianOffsetSec : 0;
                    if (!syncVerdict.Ran)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL-D] {File} [{Lang}] — auto-sync not applied: {Reason}",
                            Path.GetFileName(mediaPath), lang,
                            "not measured: " + (syncVerdict.SkipReason ?? "not run"));
                    }
                    else if (syncVerdict.Drifts)
                    {
                        // F-M300: a drifting file IS repaired — by a staircase, one offset per
                        // segment. The former refusal was right that no SINGLE offset exists and
                        // wrong that nothing can be done: measured, this takes the worst-cue
                        // residual over 36 drifting episodes from a 10.74 s median to 4.51 s.
                        // Two of the 36 come out worse and no reference-free signal separates
                        // them; the anchor path (F-M297, below) is preferred when a reference
                        // exists, and the untouched original is kept either way.
                        if (syncVerdict.SegmentOffsetsSec.Count == 0)
                        {
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL-D] {File} [{Lang}] — auto-sync not applied: {Verdict} (no segments reported)",
                                Path.GetFileName(mediaPath), lang, syncVerdict.Describe());
                        }
                        else
                        {
                            double largest = syncVerdict.SegmentOffsetsSec.Max(Math.Abs);
                            if (largest > Qa.SubtitleSync.MaxShiftSec)
                            {
                                LogUtil.PerItem(_config.LogMode, _logger,
                                    "[SubDL-D] {File} [{Lang}] — auto-sync not applied: staircase step "
                                    + "{Step:+0.0;-0.0}s beyond the {Limit:0}s limit",
                                    Path.GetFileName(mediaPath), lang, largest, Qa.SubtitleSync.MaxShiftSec);
                            }
                            else
                            {
                                (bool sOk, string sShifted, string sWhy, int sGuarded) = Qa.SubtitleSync.ShiftByStaircase(
                                    content, syncVerdict.SegmentStartTimesSec, syncVerdict.SegmentOffsetsSec);
                                if (sOk)
                                {
                                    (bool sbom, bool scrlf) = Qa.SubtitleSync.StyleOfBytes(bytes);
                                    unsyncPayload = content;
                                    writeBytes = Qa.SubtitleSync.Encode(sShifted, sbom, scrlf);
                                    content = sShifted;
                                    LogUtil.PerItem(_config.LogMode, _logger,
                                        "[SubDL-D] {File} [{Lang}] — auto-sync STAIRCASE over {Segments} segments "
                                        + "across a {Span:0.0}s drift ({Why})",
                                        Path.GetFileName(mediaPath), lang,
                                        syncVerdict.SegmentOffsetsSec.Count, syncVerdict.SpanSec, sWhy);
                                }
                                else
                                {
                                    LogUtil.PerItem(_config.LogMode, _logger,
                                        "[SubDL-D] {File} [{Lang}] — auto-sync not applied: {Why}",
                                        Path.GetFileName(mediaPath), lang, sWhy);
                                }
                            }
                        }
                    }
                    else if (Math.Abs(shift) < Qa.SubtitleSync.MinShiftSec)
                    {
                        if (_config.LogMode >= LogLevelMode.Verbose)
                        {
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL-D] {File} [{Lang}] — auto-sync: already in sync ({Shift:+0.00;-0.00}s)",
                                Path.GetFileName(mediaPath), lang, shift);
                        }
                    }
                    else if (Math.Abs(shift) > Qa.SubtitleSync.MaxShiftSec)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL-D] {File} [{Lang}] — auto-sync not applied: shift {Shift:+0.0;-0.0}s "
                            + "beyond the {Limit:0}s limit",
                            Path.GetFileName(mediaPath), lang, shift, Qa.SubtitleSync.MaxShiftSec);
                    }
                    else
                    {
                        // SIGN: the correction is MINUS the detector's value — measured, see
                        // SubtitleSync's header. Adding it doubled the error on every file.
                        double apply = -shift;
                        (bool ok, string shifted, string why) = Qa.SubtitleSync.ShiftBy(content, apply);
                        if (ok)
                        {
                            // The ORIGINAL is kept, uncompressed and untouched, exactly as
                            // fetched — that is the whole point of keeping it. The suffix
                            // sits after ".srt" so the sidecar listing (baseName + "*.srt")
                            // does not pick it up.
                            (bool bom, bool crlf) = Qa.SubtitleSync.StyleOfBytes(bytes);
                            unsyncPayload = content;
                            writeBytes = Qa.SubtitleSync.Encode(shifted, bom, crlf);
                            content = shifted;
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL-D] {File} [{Lang}] — auto-sync {Shift:+0.00;-0.00}s applied ({Why})",
                                Path.GetFileName(mediaPath), lang, apply, why);
                        }
                        else
                        {
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL-D] {File} [{Lang}] — auto-sync not applied: {Why}",
                                Path.GetFileName(mediaPath), lang, why);
                        }
                    }
                }

                // F-M297 (development): the anchor-sync. Repairs a DRIFTING file — the case the
                // audio sync above cannot touch — by anchoring it to a plain subtitle in the SAME
                // language. Runs after the audio path so a file the audio path already fixed is not
                // measured twice: `content` carries that result.
                //
                // The reference is chosen from what the item already has: a same-language plain
                // sidecar first (free), then a same-language plain embedded track (one extraction),
                // else nothing. The HI file never serves as a reference — it is the variant that
                // drifts, so anchoring to it would anchor a drifting file to another.
                if (_config.QaDownloadAutoSync)
                {
                    var cands = new List<Qa.ReferenceChoice.Candidate>();
                    foreach (var sc in SidecarNaming.List(mediaPath))
                    {
                        cands.Add(new Qa.ReferenceChoice.Candidate(sc.Lang, sc.HearingImpaired, sc.Path, null));
                    }

                    foreach (var tr in SidecarNaming.EmbeddedTracks(_mediaSourceManager.GetMediaStreams(item.Id)))
                    {
                        cands.Add(new Qa.ReferenceChoice.Candidate(tr.Lang, tr.HearingImpaired, null, tr.SubPos));
                    }

                    Qa.ReferenceChoice.Decision pick = Qa.ReferenceChoice.Choose(cands, lang);

                    string? refText = null;
                    if (pick.Origin == Qa.ReferenceChoice.Origin.Sidecar && pick.Path != null)
                    {
                        try
                        {
                            refText = await File.ReadAllTextAsync(pick.Path, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[SubDL] anchor-sync: reference unreadable {Ref}", pick.Path);
                        }
                    }
                    else if (pick.Origin == Qa.ReferenceChoice.Origin.Embedded && pick.SubPos is int subPos)
                    {
                        var (texts, _) = await FfmpegTools
                            .ExtractAllAsync(ffmpegForDrift!, mediaPath, [subPos], _logger, _config, ct)
                            .ConfigureAwait(false);
                        refText = texts.TryGetValue(subPos, out string? t) ? t : null;
                    }

                    if (string.IsNullOrWhiteSpace(refText))
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL-D] {File} [{Lang}] — anchor-sync not applied: {Reason}",
                            Path.GetFileName(mediaPath), lang, pick.Reason);
                    }
                    else
                    {
                        Qa.AnchorSync.Result ar = Qa.AnchorSync.Correct(
                            Qa.AnchorSync.Parse(refText), Qa.AnchorSync.Parse(content));
                        if (ar.Applied)
                        {
                            // Same protocol as the audio path: the untouched original is kept, and
                            // the hash is registered over what now lies on disk.
                            (bool abom, bool acrlf) = Qa.SubtitleSync.StyleOfBytes(bytes);
                            string rebuilt = RenderCues(ar.Cues);
                            unsyncPayload ??= content;
                            writeBytes = Qa.SubtitleSync.Encode(rebuilt, abom, acrlf);
                            content = rebuilt;
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL-D] {File} [{Lang}] — anchor-sync applied via {Origin}: {Reason}",
                                Path.GetFileName(mediaPath), lang, pick.Origin, ar.Reason);
                        }
                        else
                        {
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL-D] {File} [{Lang}] — anchor-sync not applied ({Origin}): {Reason}",
                                Path.GetFileName(mediaPath), lang, pick.Origin, ar.Reason);
                        }
                    }
                }

                string contentHash = ContentHashRegistry.ComputeHash(content);

                // 13.09.2026 (user decision): F-M39 re-download skip REMOVED — the
                // seeder owns all pre-queue checks. The download action itself
                // still records the content hash below so the seeder's checks
                // keep working.

                try
                {
                    // Slot 1 = "<base>.<lang>.srt" (as before), further slots
                    // "<base>.<lang>.2.srt", "<base>.<lang>.3.srt", ... — Jellyfin
                    // listet jede externe Datei als eigene wählbare Spur.
                    // F-M260: the name comes from the shared builder, so a name composed here is
                    // one SidecarNaming.Parse recognizes by construction. A hearing-impaired file
                    // gets the ".sdh" marker the reader demands — without it the file never counted
                    // as HI again and the F-M254 reader reported the variant as absent forever.
                    string targetPath = SidecarNaming.Build(mediaPath, lang, effectiveHi, savedCount + 1);

                    // (10.09.2026, user decision): per-run read-only guard — one cheap
                    // probe write per DIRECTORY (cached for the run) before the first real
                    // save. On a read-only share the probe fails once with a clear log line
                    // and every later save into that directory short-circuits BEFORE the
                    // API download/archive work, instead of failing write-by-write with a
                    // retry loop. Uploads are unaffected (they never write next to media);
                    // the .part probe mirrors the AtomicWriteAsync path it protects.
                    string targetDir = Path.GetDirectoryName(targetPath) ?? ".";
                    if (_readonlyDirs.Contains(targetDir))
                    {
                        summary.Failed++;
                        ReportOutcome(item, ItemOutcome.RealFailure);
                        LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] SKIP {File} [{Lang}] — directory not writable: {Dir} (F-M89 read-only guard)", Path.GetFileName(mediaPath), lang, targetDir);
                        continue;
                    }

                    await AtomicWriteAsync(targetPath, writeBytes, ct).ConfigureAwait(false);

                    // F-M296: the untouched original, kept beside the corrected file. This
                    // is what makes the correction reversible without re-downloading (which
                    // would cost quota and might return the same drifting file). Guarded by
                    // the dry-run flag like every other write (F-M287) — the flag is checked
                    // again here rather than relying on the enclosing branch.
                    if (unsyncPayload != null && !_config.DownloadDryRun)
                    {
                        try
                        {
                            string unsyncPath = Qa.SubtitleSync.UnsyncPathFor(targetPath);
                            (bool ub, bool uc) = Qa.SubtitleSync.StyleOfBytes(bytes);
                            await AtomicWriteAsync(unsyncPath, Qa.SubtitleSync.Encode(unsyncPayload, ub, uc), ct)
                                .ConfigureAwait(false);
                            if (_config.LogMode >= LogLevelMode.Verbose)
                            {
                                LogUtil.PerItem(_config.LogMode, _logger,
                                    "[SubDL-D] original kept as {Path}", Path.GetFileName(unsyncPath));
                            }
                        }
                        catch (Exception ex)
                        {
                            // The corrected file is already on disk and correct — a failure to
                            // keep the original must not undo it, but it must be visible.
                            _logger.LogWarning(ex, "[SubDL-D] could not keep the unsynchronized original for {File}",
                                Path.GetFileName(targetPath));
                        }
                    }

                    Registry.MarkAndFlush(() => Registry.PatchMediaMetadata(mediaHash, resolvedImdb, season, isSeries ? episode : null, null, resolvedTmdb));
                    // F-M260: the real flag, not the hard-coded false it used to be. A
                    // hearing-impaired file recorded as hi=false made the HI reader (F-M254) ask
                    // for a variant that was already on disk, run after run.
                    Registry.MarkAndFlush(() => Registry.MarkDownloaded(
                        contentHash, mediaHash, lang, effectiveHi, cand.SubdlId,
                        Path.GetFileName(targetPath), targetPath));
                    _qaFails.RecordSuccess(item.Id.ToString(), lang); // A real save resets the QA counter
                    summary.Downloaded++;
                    ReportOutcome(item, ItemOutcome.Done);
                    savedCount++;
                    savedAny = true;
                    if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] SAVED {Path} (lang={Lang}, score {Score}, release \"{Release}\", subdl=\"{SubdlName}\", author={Author}, imdb={Imdb}, tmdb={Tmdb}) for {File} — best-slot {N}/{Keep}",
                            Path.GetFileName(targetPath), lang, Score(cand, videoBase), cand.ReleaseName,
                            string.IsNullOrEmpty(cand.OriginalName) ? "—" : cand.OriginalName,
                            string.IsNullOrEmpty(cand.Author) ? "—" : cand.Author,
                            resolvedImdb ?? "—", resolvedTmdb ?? "—", Path.GetFileName(mediaPath), savedCount, keepBest);
                    }

                    // F-M42b + F-M241: the hearing-impaired variant, from the SECOND search's own
                    // pool. This block sits BEFORE the keepBest break on purpose — it used to sit
                    // after it, and with the default KeepBestPerLanguage=1 the break always fired
                    // first, so the block was unreachable and no HI variant was ever fetched
                    // (measured: zero "SAVED HI variant" lines in every log). The HI candidate
                    // comes from the hi=1 search, not from the regular pool: the two pools share
                    // no releases (measured on tt0448694), so picking from the regular list could
                    // only ever find what the unfiltered response happened to mix in.
                    // F-M260: `effectiveHi` also guards this — when the file we just saved WAS
                    // the pack's hearing-impaired entry, the HI variant is already on disk. The old
                    // condition (`!cand.HearingImpaired`) missed exactly that case, because a pack's
                    // header flag can be false while every file inside it is true.
                    if (_config.DownloadHearingImpaired && !effectiveHi)
                    {
                        var hi = hiCandidates.Where(c => c.Language == lang)
                            .OrderByDescending(c => Score(c, videoBase))
                            .ThenByDescending(c => c.DownloadCount)
                            .FirstOrDefault();
                        if (hi == null || string.IsNullOrEmpty(hi.Url))
                        {
                            // F-M255: the HI pool had no usable entry for this language — previously
                            // indistinguishable from "the pool was empty".
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL-D] HI reject {File} [{Lang}]: no usable candidate in the HI pool ({N} in pool)",
                                Path.GetFileName(mediaPath), lang, hiCandidates.Count(c => c.Language == lang));
                        }
                        else
                        {
                            // F-M26: the HI variant is a SECOND real download for this item, so it
                            // carries the transfer pause (main subtitle → HI variant). The local cap that
                            // used to skip the fetch (F-M20) is gone — nothing here can refuse it any more,
                            // because the plugin holds no budget of its own. Only SubDL can say no.
                            {
                            // (16.09.2026): pause between two real downloads
                            // (main subtitle → HI variant) = the steered rate-limit
                            // interval (3600/cap). Bare API calls stay on
                            // MinCallPauseSec; failed/skipped candidates get no
                            // Transfer pause (transaction semantics intact).
                            await Task.Delay(_limiter.TransferPauseMs(), ct).ConfigureAwait(false);
                            var hiBytes = await _api.DownloadSubtitleFileAsync(hi.Url, ct, season, episode).ConfigureAwait(false);
                            var hiStats = hiBytes != null && hiBytes.Length >= 100 ? Qa.QaGates.ParseSrt(DecodeSrt(hiBytes)) : null;
                            bool hiStructureOk = hiStats != null
                                && hiStats.Monotonic
                                && hiStats.MinCueMs > 100
                                && hiStats.MaxCueMs < 600_000;
                            bool hiBytesUsable = hiBytes != null && hiBytes.Length >= 100;
                            string? hiRuntimeReason = null;
                            bool hiRuntimeOk = false;
                            if (hiBytesUsable)
                            {
                                hiRuntimeOk = RuntimeMatches(item, hiBytes!, out hiRuntimeReason);
                            }
                            if (hiBytesUsable && hiStructureOk && hiRuntimeOk)
                            {
                                // F-M296: the HI variant goes through the SAME auto-sync as the
                                // regular one. Leaving it out would have produced the absurd case
                                // of a corrected main subtitle beside an uncorrected HI file of
                                // the same episode. The HI pool is exactly where the drift gate
                                // measured its findings (all 38 measured HI files drifted), so
                                // this is the case the sync more often CANNOT fix — and then it
                                // writes nothing and says so.
                                byte[] hiWriteBytes = hiBytes!;
                                string hiContent = DecodeSrt(hiBytes!);
                                string? hiUnsync = null;
                                if (_config.QaDownloadAutoSync)
                                {
                                    var hiVerdict = await Qa.DriftGate.RunAsync(
                                        mediaPath, hiContent, ffmpegForDrift, _logger, ct, audioMap).ConfigureAwait(false);
                                    double hiShift = hiVerdict.Ran && !hiVerdict.Drifts ? hiVerdict.MedianOffsetSec : 0;

                                    // F-M300: the HI variant, like the main one, is repaired by
                                    // a STAIRCASE when the offset moves — the HI pool is exactly
                                    // where the drift lives (all 38 measured HI files drifted),
                                    // so refusing here would leave every HI file uncorrected.
                                    if (hiVerdict.Ran && hiVerdict.Drifts && hiVerdict.SegmentOffsetsSec.Count > 0
                                        && hiVerdict.SegmentOffsetsSec.Max(Math.Abs) <= Qa.SubtitleSync.MaxShiftSec)
                                    {
                                        (bool hsOk, string hsShifted, string hsWhy, int hsGuarded) = Qa.SubtitleSync.ShiftByStaircase(
                                            hiContent, hiVerdict.SegmentStartTimesSec, hiVerdict.SegmentOffsetsSec);
                                        if (hsOk)
                                        {
                                            (bool hsb, bool hsc) = Qa.SubtitleSync.StyleOfBytes(hiBytes!);
                                            hiUnsync = hiContent;
                                            hiWriteBytes = Qa.SubtitleSync.Encode(hsShifted, hsb, hsc);
                                            hiContent = hsShifted;
                                            LogUtil.PerItem(_config.LogMode, _logger,
                                                "[SubDL-D] HI auto-sync STAIRCASE over {Segments} segments across a "
                                                + "{Span:0.0}s drift for {File} [{Lang}] ({Why}) ({Release})",
                                                hiVerdict.SegmentOffsetsSec.Count, hiVerdict.SpanSec,
                                                Path.GetFileName(mediaPath), lang, hsWhy, hi.ReleaseName);
                                        }
                                        else
                                        {
                                            LogUtil.PerItem(_config.LogMode, _logger,
                                                "[SubDL-D] HI auto-sync not applied for {File} [{Lang}]: {Why}",
                                                Path.GetFileName(mediaPath), lang, hsWhy);
                                        }
                                    }
                                    else if (!hiVerdict.Ran || hiVerdict.Drifts
                                        || Math.Abs(hiShift) < Qa.SubtitleSync.MinShiftSec
                                        || Math.Abs(hiShift) > Qa.SubtitleSync.MaxShiftSec)
                                    {
                                        LogUtil.PerItem(_config.LogMode, _logger,
                                            "[SubDL-D] HI auto-sync not applied for {File} [{Lang}]: {Reason} ({Release})",
                                            Path.GetFileName(mediaPath), lang,
                                            !hiVerdict.Ran
                                                ? "not measured: " + (hiVerdict.SkipReason ?? "not run")
                                                : hiVerdict.Drifts
                                                    ? hiVerdict.Describe()
                                                    : Math.Abs(hiShift) > Qa.SubtitleSync.MaxShiftSec
                                                        ? $"shift {hiShift:+0.0;-0.0}s beyond the limit"
                                                        : $"already in sync ({hiShift:+0.00;-0.00}s)",
                                            hi.ReleaseName);
                                    }
                                    else
                                    {
                                        // SIGN: MINUS the detector's value, as in the main path.
                                        double hiApply = -hiShift;
                                        (bool hOk, string hShifted, string hWhy) = Qa.SubtitleSync.ShiftBy(hiContent, hiApply);
                                        if (hOk)
                                        {
                                            (bool hb, bool hc) = Qa.SubtitleSync.StyleOfBytes(hiBytes!);
                                            hiUnsync = hiContent;
                                            hiWriteBytes = Qa.SubtitleSync.Encode(hShifted, hb, hc);
                                            hiContent = hShifted;
                                            LogUtil.PerItem(_config.LogMode, _logger,
                                                "[SubDL-D] HI auto-sync {Shift:+0.00;-0.00}s applied for {File} [{Lang}] ({Why})",
                                                hiApply, Path.GetFileName(mediaPath), lang, hWhy);
                                        }
                                        else
                                        {
                                            LogUtil.PerItem(_config.LogMode, _logger,
                                                "[SubDL-D] HI auto-sync not applied for {File} [{Lang}]: {Why}",
                                                Path.GetFileName(mediaPath), lang, hWhy);
                                        }
                                    }
                                }

                                string hiHash = ContentHashRegistry.ComputeHash(hiContent);
                                if (!Registry.IsContentKnown(hiHash))
                                {
                                    if (!_config.DownloadDryRun)
                                    {
                                        // F-M260: the shared builder, not a hand-written
                                        // concatenation — the same rule the reader parses.
                                        string hiPath = SidecarNaming.Build(mediaPath, lang, hearingImpaired: true);
                                        await AtomicWriteAsync(hiPath, hiWriteBytes, ct).ConfigureAwait(false);
                                        if (hiUnsync != null)
                                        {
                                            try
                                            {
                                                string hiUnsyncPath = Qa.SubtitleSync.UnsyncPathFor(hiPath);
                                                (bool hub, bool huc) = Qa.SubtitleSync.StyleOfBytes(hiBytes!);
                                                await AtomicWriteAsync(
                                                    hiUnsyncPath, Qa.SubtitleSync.Encode(hiUnsync, hub, huc), ct)
                                                    .ConfigureAwait(false);
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger.LogWarning(ex,
                                                    "[SubDL-D] could not keep the unsynchronized HI original for {File}",
                                                    Path.GetFileName(hiPath));
                                            }
                                        }

                                        Registry.MarkAndFlush(() => Registry.MarkDownloaded(
                                            hiHash, mediaHash, lang, true, hi.SubdlId,
                                            Path.GetFileName(hiPath), hiPath));
                                        _qaFails.RecordSuccess(item.Id.ToString(), lang); // HI save resets the pair counter too
                                        summary.Downloaded++;
                                        ReportOutcome(item, ItemOutcome.Done);
                                        if (_config.LogMode >= LogLevelMode.Verbose)
                                        {
                                            LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] SAVED HI variant {Path} (lang={Lang}, release \"{Release}\", subdl=\"{SubdlName}\", author={Author}, imdb={Imdb}, tmdb={Tmdb})",
                                                Path.GetFileName(hiPath), lang, hi.ReleaseName,
                                                string.IsNullOrEmpty(hi.OriginalName) ? "—" : hi.OriginalName,
                                                string.IsNullOrEmpty(hi.Author) ? "—" : hi.Author,
                                                resolvedImdb ?? "—", resolvedTmdb ?? "—");
                                        }
                                    }
                                    else
                                    {
                                        // Safety, not a promise: the dry run never reaches the fetch
                                        // (it breaks before it, F-M22). If that ever changes, this
                                        // guard still stops the HI file from being written.
                                        LogUtil.PerItem(_config.LogMode, _logger, "[SubDL-D] DRY-RUN would save HI variant for {File} [{Lang}]", Path.GetFileName(mediaPath), lang);
                                    }
                                }
                                else
                                {
                                    // F-M286: fetched and discarded — the bytes were spent on the
                                    // request, so this belongs in the same number as the main-path
                                    // rejects. It was the largest single hole: seven of nine discards
                                    // in the audited run exited here and counted nothing.
                                    summary.RejectedCandidates++;
                                    // F-M255: fetched, but the content is already known — a distinct
                                    // outcome that previously looked exactly like "not saved".
                                    LogUtil.PerItem(_config.LogMode, _logger,
                                        "[SubDL-D] HI reject {File} [{Lang}]: content already known ({Release})",
                                        Path.GetFileName(mediaPath), lang, hi.ReleaseName);
                                }
                            }
                            else
                            {
                                // F-M255: the HI fetch was thrown away by a gate, with the measured
                                // value — four silent exits produced the "14 searches, 1 file" picture
                                // that could not be explained from the log at all.
                                // F-M286: fetched and threw the bytes away in a gate — same spend,
                                // same counter. One increment for every reason in this branch.
                                summary.RejectedCandidates++;
                                string hiRejectReason = !hiBytesUsable
                                    ? (hiBytes == null ? "no bytes returned" : "only " + hiBytes.Length + " bytes")
                                    : !hiStructureOk
                                        ? (hiStats == null
                                            ? "structure: no parsable cues"
                                            : $"structure: monotonic={hiStats.Monotonic} min={hiStats.MinCueMs}ms max={hiStats.MaxCueMs}ms")
                                        : "runtime: " + (hiRuntimeReason ?? "no match");
                                LogUtil.PerItem(_config.LogMode, _logger,
                                    "[SubDL-D] HI reject {File} [{Lang}]: {Reason} ({Release})",
                                    Path.GetFileName(mediaPath), lang, hiRejectReason, hi.ReleaseName);
                            }
                        }
                    }
                    }

                    // X best per language — keep walking when more slots are open.
                    if (savedCount >= keepBest)
                    {
                        // F-M255: name the candidates this stop leaves untried — otherwise they are
                        // absent from the record although the next run will walk them.
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL-D] {File} [{Lang}] — keep-best reached ({Saved}/{Keep}); {N} lower-ranked candidate(s) not tried this run",
                            Path.GetFileName(mediaPath), lang, savedCount, keepBest,
                            Math.Max(0, ranked.Count - attempts));

                        break;
                    }

                    // No break here. There used to be an unconditional one at this point, which
                    // made the whole setting dead: the loop ended after the FIRST save no matter
                    // what KeepBestPerLanguage said, so the numbered slots ("name.en.2.srt") were
                    // never written and the GUI option had no effect (F-M242). The break above is
                    // the only exit, and it is what implements the setting.
                }
                catch (Exception ex)
                {
                    summary.Failed++;
                    ReportOutcome(item, ItemOutcome.RealFailure);
                    _logger.LogError("[SubDL-D] WRITE FAILED {File} [{Lang}]: {Msg}", Path.GetFileName(mediaPath), lang, ex.Message);
                    break; // IO error — retrying another candidate won't help
                }
            }

            // Run end (before the next language starts): record the
            // burned releases and count the run as failed (give-up after
            // DownloadQaRetryLimit consecutive failed runs).
            if (!savedAny && attempts > 0)
            {
                if (qaRejectedReleases.Count > 0)
                {
                    _qaFails.RecordSkippedCandidates(item.Id.ToString(), lang, qaRejectedReleases);
                }

                _qaFails.RecordFailure(item.Id.ToString(), lang);

                if (_config.LogMode >= LogLevelMode.Verbose)
                {
                    LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] {File} [{Lang}] — run failed without save; {N} download(s) memorized, failed-run counter now {C} (give-up at {Limit})",
                        Path.GetFileName(mediaPath), lang, qaRejectedReleases.Count,
                        _qaFails.IsExhausted(item.Id.ToString(), lang, _config.DownloadQaRetryLimit) ? _config.DownloadQaRetryLimit : -1,
                        _config.DownloadQaRetryLimit);
                }
            }

            if (savedAny)
            {
                closedLangs.Add(lang); // F-M88c: language saved = terminal
            }
            else if (ranked.Count > 0)
            {
                summary.NotAvailable++;
                ReportOutcome(item, ItemOutcome.NotAvailable);
                if (_config.LogMode >= LogLevelMode.Verbose)
                {
                    LogUtil.PerItem(_config.LogMode, _logger,"[SubDL-D] {File} [{Lang}] — no candidate passed checks (F-M46), language marked not-available",
                        Path.GetFileName(mediaPath), lang);
                }
                // F-M88c: QA-exhausted or all candidates rejected = terminal as well
                if (_qaFails.IsExhausted(item.Id.ToString(), lang, _config.DownloadQaRetryLimit))
                {
                    closedLangs.Add(lang);
                }
            }
        }

        // F-M283 (user decision 02.10.2026): the completion mark is GONE. This block used to decide
        // whether to WRITE one, and that decision was a third reading of the same question — it asked
        // `MissingTokens` (disk only) while the reader above asked disk plus the stored variant
        // verdict, so the two disagreed on exactly the case that mattered: a variant embedded in the
        // container. The withhold refused the mark, the item stayed due, and the regular subtitle was
        // re-fetched on every pass.
        //
        // Nothing is settled here any more, so there is nothing to report as withheld or complete:
        // the open-pair question at the top of the run is the only answer, and it is asked fresh
        // every time. What this spot still reports is the outcome of the search itself — a language
        // whose candidates were all rejected and whose QA budget is spent is closed for this cycle,
        // which is informative, not a give-up (F-M42b: no expiry, no retry budget as a brake).
        if (!_stopRun && missing.Count > 0 && missing.All(l => closedLangs.Contains(l)))
        {
            LogUtil.PerItem(
                _config.LogMode,
                _logger,
                "[SubDL-D] {File} — {Count} language(s) closed by the QA budget this run; nothing left to search",
                Path.GetFileName(mediaPath),
                missing.Count);
        }
    }

    /// <summary>
    /// F-M257: writes this file's embedded tracks into the registry as observations.
    /// <para>
    /// The download pipeline already holds the streams for its presence check (F-M246) and asks the
    /// registry for the HI verdict (F-M254) — but on an install with <c>UploadEnabled=false</c>
    /// nothing had ever written that area, so the verdict was always "absent". This closes the gap on
    /// the read side's own doorstep rather than relying on the uploader having run.
    /// </para>
    /// <para>
    /// An existing verdict is never overwritten (<see cref="ContentHashRegistry.ObserveEmbed"/>),
    /// and a failure changes nothing about the download: an observation is an improvement, not a
    /// precondition.
    /// </para>
    /// </summary>
    /// <param name="item">Jellyfin item.</param>
    /// <param name="mediaHash">Media content hash, or null when unknown.</param>
    /// <returns>Number of rows created or changed.</returns>
    private int ObserveEmbeddedFacts(BaseItem item, string? mediaHash)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return 0;
        }

        try
        {
            var streams = _mediaSourceManager.GetMediaStreams(item.Id);
            var tracks = Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.EmbeddedTracks(streams);

            // F-M263: the gate's own verdict no longer needs to be merged in here. The seeder runs the
            // gate before the queue decision and records its result in the REGISTRY; this pass reads
            // the stream list, which is Jellyfin's CACHED snapshot and can therefore still report the
            // old tag for a container corrected in the same cycle. That is why this pass only ADDS
            // what it can read (ObserveEmbed never overwrites an existing row), while the corrected
            // language is already stored — and the coverage check reads it from there.
            int written = 0;
            foreach (var (subPos, lang, hi, forced) in tracks)
            {
                if (Registry.ObserveEmbed(mediaHash, subPos, lang, hi, forced))
                {
                    written++;
                }
            }

            return written;
        }
        catch (Exception ex)
        {
            LogUtil.Detail(_config.LogMode, _logger, "[SubDL-D] embedded facts not recorded: {Msg}", ex.Message);
            return 0;
        }
    }

    private List<string> MissingLanguages(BaseItem item, string mediaPath, List<string> targets)
    {
        // F-M239: the shared reader (Registry.SidecarNaming) — one directory listing, one set of
        // name rules. See SidecarNaming for why three copies of this parse had to go: the copies
        // disagreed about the "sdh" marker and the disagreement silently invalidated download marks.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, lang, _, forced) in Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.List(mediaPath))
        {
            if (lang == null)
            {
                // A sidecar whose NAME carries no language and whose text has not been
                // detected yet proves nothing about coverage. It is not added, so the
                // language still counts as missing and is fetched if it is configured.
                continue;
            }

            // F-M284: a forced subtitle is not the film's dialogue (F-M246), so it never proves
            // its language. This is the ONE place the rule lives now; it used to be re-derived in
            // five call sites from Jellyfin's stream flag.
            if (forced)
            {
                continue;
            }

            present.Add(lang);
        }

        // Embedded streams (when DownloadOnlyMissing is on, embedded languages count as present).
        // F-M246: a FORCED track does not make its language present — it carries only the lines of
        // foreign-language scenes, so counting it left the language without a real subtitle. The
        // rule lives in SidecarNaming.EmbeddedPresentLanguages and is shared with the seeder.
        if (_config.DownloadOnlyMissing)
        {
            // F-M263: what the SEEDER resolved counts as present too, and it is read from the
            // REGISTRY. It used to come from this pipeline's own gate call, which held the result in
            // memory — correct in effect, but it made the pipeline rewrite media files with no hash
            // pair and outside any dry run. The seeder has written the same facts under this file's
            // hash, so the record is the authority now, and it is the CURRENT one: Jellyfin caches
            // its stream list, so a container corrected earlier in this cycle still reports its OLD
            // tag through the call below.
            foreach (var lang in Registry.EmbeddedLanguages(mediaPath))
            {
                present.Add(lang);
            }

            foreach (var lang in Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming
                         .EmbeddedPresentLanguages(_mediaSourceManager.GetMediaStreams(item.Id)))
            {
                present.Add(lang);
            }
        }

        return targets.Where(t => !present.Contains(t)).ToList();
    }

    /// <summary>
    /// F-M282: the language-level embedded evidence this run may count as coverage.
    /// <para>
    /// Deliberately language-level and deliberately optional: it is Jellyfin's stream list, which
    /// says a LANGUAGE has a track but says nothing about which variant, so it can prove the regular
    /// pair and never the variant pair. The registry's own embedded ROWS are consulted separately by
    /// the coverage reader and they DO carry the flag — that is the difference between the cached
    /// stream snapshot and a stored fact (F-M263).
    /// </para>
    /// <para>
    /// Empty while <c>DownloadOnlyMissing</c> is off, which is the configuration saying embedded
    /// tracks are not coverage for this install. F-M246: a FORCED track does not make its language
    /// present — it carries only the lines of foreign-language scenes.
    /// </para>
    /// </summary>
    /// <param name="item">Jellyfin item.</param>
    /// <returns>Languages with a usable embedded track, or empty.</returns>
    private IEnumerable<string> EmbeddedPresentLanguagesOf(BaseItem item)
    {
        if (!_config.DownloadOnlyMissing)
        {
            return Enumerable.Empty<string>();
        }

        return Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming
            .EmbeddedPresentLanguages(_mediaSourceManager.GetMediaStreams(item.Id));
    }

    /// <summary>
    /// F-M282 (user decision 02.10.2026): the subtitle DATA this run must deliver for this file —
    /// the regular subtitle of each configured language, plus its variant when the HI switch is on.
    /// <para>
    /// This replaces <c>RequiredTokens</c>. It is a pure projection of the configuration, and pairs
    /// carry the hearing-impaired property structurally: turning the switch on ADDS the variant
    /// pairs, so an item that already has its subtitles simply becomes due for the variant, and
    /// turning it off removes them so nothing has to be cleaned up.
    /// </para>
    /// </summary>
    /// <param name="targets">Configured target languages.</param>
    /// <returns>One pair per required file.</returns>
    private List<Jellyfin.Plugin.SubdlScribe.Registry.SubtitleRef> RequiredPairs(IReadOnlyList<string> targets)
        => Jellyfin.Plugin.SubdlScribe.Registry.SubtitleRef
            .Required(targets, _config.DownloadHearingImpaired);

    /// <summary>
    /// F-M282: which required PAIRS have no evidence for this file.
    /// <para>
    /// Read through the one shared reader (<see cref="Jellyfin.Plugin.SubdlScribe.Registry.SubtitleCoverage"/>),
    /// so this answer and the coverage question at the top of the run can never be fed different
    /// evidence — which is precisely how a complete item used to keep looking due.
    /// </para>
    /// </summary>
    /// <param name="targets">Configured target languages.</param>
    /// <param name="mediaPath">Media file path.</param>
    /// <returns>Pairs without evidence.</returns>
    private List<Jellyfin.Plugin.SubdlScribe.Registry.SubtitleRef> OpenPairs(
        IReadOnlyList<string> targets, string mediaPath)
        => Registry.OpenPairs(mediaPath, RequiredPairs(targets));

    /// <summary>F-M44 weighted release score: group match × WeightGroup + token overlap × WeightToken + min(dl,500) × WeightDownload.</summary>
    /// <summary>
    /// (15.09.2026): MAIN = v1 id search (GET /api/v1/subtitles by
    /// imdb/tmdb + season/episode — episode-exact server-side, live-verified).
    /// BACKUP = v2 filename batch (POST /api/v2/files/search), only when the
    /// id search yields nothing (or the item carries no id). v2 id search is
    /// retired as a search path (its season/episode params are ignored
    /// server-side); the similarity ranking is token-based and episode-blind.
    /// Returns (candidates, usedFilenameFallback, thresholdPassCount); null only
    /// when the search did not complete (429/5xx/403 — run must stop, F-M48).
    /// </summary>
    /// <param name="missing">Regular subtitle pairs without evidence — drives the <c>hi=0</c> search.</param>
    /// <param name="variantMissing">Variant pairs without evidence — drives the <c>hi=1</c> search.</param>
    /// <param name="summary">Run summary.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(List<SubtitleCandidate>? Candidates, bool UsedFilenameFallback, int ThresholdPassCount, List<SubtitleCandidate> HiCandidates)> RunV2SearchAsync(
        BaseItem item, string mediaPath, string? imdbId, string? tmdbId, int season, int episode, List<string> missing, List<string> variantMissing, DownloadRunSummary summary, CancellationToken ct)
    {
        string filename = Path.GetFileName(mediaPath);
        string langs = string.Join(",", missing);
        var noHi = new List<SubtitleCandidate>();

        // ---- Main: v1 id search (episode-exact, server-side verified) ----
        // (15.09.2026): v2 id-search ignores season/episode server-side
        // (live: BCS S02E01 query returned 30 candidates incl. S06 packs and
        // The Simpsons); v1 filters episode-exact. Early-stop is fed by
        // DownloadBudget.SearchEarlyStopThreshold (F-M95): it used to be a hard-coded 3, so
        // DownloadMaxCandidatesPerLanguage widened the download budget but never the search —
        // the page walk still stopped at three candidates per language.
        // (16.09.2026): id-less soft mode runs v1 with film_name
        // (_searchFnTitle ?? item.Name) — HEAD 8a17aed behavior restored.
        if (string.IsNullOrWhiteSpace(imdbId) && string.IsNullOrWhiteSpace(tmdbId) && _searchFnTitle == null && !_config.DownloadRequireImdb)
        {
            _searchFnTitle = item.Name; // last rung: plain JF title
        }
        if (string.IsNullOrWhiteSpace(imdbId) && string.IsNullOrWhiteSpace(tmdbId) && _searchFnTitle == null)
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[SubDL-D] MAIN (v1 id) skipped {File} — no TMDB/IMDb ID resolvable → filename fallback",
                filename);
        }
        else
        {
            // F-M241 (user decision 28.09.2026): TWO searches, one per side of the HI split.
            // The API filters this server-side (&hi=0 / &hi=1) and the two pools do not overlap
            // (measured on tt0448694: 13 regular, 30 HI, zero shared releases), so a single
            // unfiltered search returned a MIXTURE in which the regular slot and the HI slot had
            // to be sorted out of one list — and the regular slot's ranking depended on how many
            // HI releases the response happened to contain. The HI search only runs when the HI
            // switch asks for it, so a user who does not want HI variants pays exactly one search,
            // as before.
            // F-M95: the threshold is derived from the two candidate settings, never a literal.
            int earlyStop = DownloadBudget.SearchEarlyStopThreshold(
                _config.DownloadMaxCandidatesPerLanguage, _config.DownloadKeepBestPerLanguage);
            // F-M282: the regular search runs only when a REGULAR file is open. When the variant is
            // the only thing missing, `langs` is empty and this call would be a search for nothing —
            // worse, it used to be the call that re-fetched a subtitle already on disk.
            var main = langs.Length == 0
                ? new List<SubtitleCandidate>()
                : await _api.SearchSubtitlesAsync(
                    string.IsNullOrWhiteSpace(imdbId) ? null : imdbId,
                    string.IsNullOrWhiteSpace(tmdbId) ? null : tmdbId,
                    _searchFnTitle, season, episode, langs, 20, ct, earlyStop, hearingImpaired: false).ConfigureAwait(false);
            if (main == null)
            {
                return (null, false, 0, noHi); // 429/5xx/403 → stop the run
            }

            if (variantMissing.Count > 0 && _config.DownloadHearingImpaired)
            {
                // Second search, fed from the VARIANT pairs only. A failure here must NOT lose the
                // regular candidates: the HI variant is a bonus (F-M42b), the regular subtitle is the
                // actual target, so an error only means "no HI pool this run".
                //
                // The language list is `variantMissing`, never `missing`: sending the regular list
                // asked SubDL for the regular side of a language whose variant was missing, and the
                // result was a regular subtitle fetched over the file already on disk.
                var hiLangs = string.Join(",", variantMissing);
                var hi = await _api.SearchSubtitlesAsync(
                    string.IsNullOrWhiteSpace(imdbId) ? null : imdbId,
                    string.IsNullOrWhiteSpace(tmdbId) ? null : tmdbId,
                    _searchFnTitle, season, episode, hiLangs, 20, ct, earlyStop, hearingImpaired: true).ConfigureAwait(false);
                if (hi != null && hi.Count > 0)
                {
                    noHi = hi.Where(c => c.Language != null && variantMissing.Contains(c.Language, StringComparer.OrdinalIgnoreCase)).ToList();
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[SubDL-D] HI search {File} [{Langs}] — {N} hearing-impaired candidates (separate pool, F-M241)",
                        filename, hiLangs, noHi.Count);
                }
                else if (hi == null)
                {
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[SubDL-D] HI search {File} — failed this run; regular candidates kept (F-M241)", filename);
                }
            }

            if (main.Count > 0)
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[SubDL-D] MAIN (v1 id) {File} [{Langs}] — {N} candidates (episode-exact)",
                    filename, langs, main.Count);
                return (main, false, 0, noHi);
            }

            LogUtil.Detail(_config.LogMode, _logger, "[SubDL-D] MAIN (v1 id) {File} — no candidates → filename fallback", filename);
        }

        // V2 filename batch backup REMOVED — the MAIN v1 search already
        // Covers the title anchor (soft mode). No-candidates here means
        // the item is genuinely without results on SubDL.
        return (new List<SubtitleCandidate>(), false, 0, noHi);
    }

    /// <summary>
    /// Candidate download dispatcher. v2 candidates carry an nId →
    /// GET /api/v2/subtitles/{nId}/download?format=file (Bearer). v1-style zip
    /// urls (backup unpack_files already carry ready single-file urls on
    /// dl.subdl.com) → legacy DownloadSubtitleFileAsync with the url prefix logic.
    /// </summary>
    /// <summary>
    /// F-M215: downloads the single file of a candidate that belongs to <paramref name="season"/>/
    /// <paramref name="episode"/>.
    /// <para>
    /// F-M260: also reports WHERE the bytes came from, because a season or range pack carries one
    /// file per episode and EACH of those files states its own hearing-impaired flag — independently
    /// of the pack's own header, which is often the opposite of its contents (measured on
    /// <c>Mo.S01.WEBRip.x265-ION265</c>: header <c>hi=false</c>, all 8 files <c>hi=true</c>). That
    /// per-file flag was parsed and then never read, so a hearing-impaired file was saved as a plain
    /// <c>.srt</c> and recorded as <c>hi=false</c>. Since <c>SidecarNaming.IsHearingImpairedToken</c>
    /// accepts only <c>sdh</c>, such a file never counted as hearing impaired again — the F-M254 HI
    /// reader kept answering "absent" for a variant that was sitting on disk.
    /// </para>
    /// </summary>
    /// <param name="cand">Candidate to fetch.</param>
    /// <param name="season">Wanted season (0 = none).</param>
    /// <param name="episode">Wanted episode (0 = none).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The bytes, and the source they came from (null source = the candidate as a whole).</returns>
    private async Task<(byte[]? Bytes, SubtitleUnpackFile? PackFile)> DownloadCandidateAsync(
        SubtitleCandidate cand, int season, int episode, CancellationToken ct)
    {
        // F-M215: a season or range pack must be resolved to the episode we are
        // actually working on. The search runs with unpack=1, so the candidate
        // carries the pack's file list; pick the entry whose season/episode matches
        // the media item and download THAT single file. Without this the pack was
        // downloaded whole and the first .srt inside it was used for every episode
        // of the season (measured 26.09.2026: one E01 subtitle on E05..E10).
        if (cand.UnpackFiles.Count > 0 && (episode > 0 || season > 0))
        {
            var want = cand.FindUnpackFile(season, episode);
            if (want != null && !string.IsNullOrWhiteSpace(want.Url))
            {
                if (_config.LogMode >= LogLevelMode.Debug)
                {
                    LogUtil.Detail(_config.LogMode, _logger, 
                        "[SubDL-D] unpack pick S{Season}E{Episode} from pack {Release} → {File} (HI={Hi})",
                        season, episode, cand.ReleaseName, want.Name, want.HearingImpaired);
                }

                var packBytes = await _api.DownloadSubtitleFileAsync(want.Url, ct).ConfigureAwait(false);
                return (packBytes, want);
            }

            // The pack carries a file list but nothing matches our episode. Taking the
            // first entry would write the wrong episode's text next to this file — worse
            // than not saving anything, so treat it as no candidate (F-M215).
            _logger.LogWarning(
                "[SubDL-D] pack {Release} has {N} files but none for S{Season}E{Episode} — candidate skipped instead of saving the wrong episode.",
                cand.ReleaseName, cand.UnpackFiles.Count, season, episode);
            return (null, null);
        }

        // unpack-file candidates carry a ready single-file url (dl.subdl.com) —
        // download it directly; batch candidates carry a zip url + nId → v2
        // single-file endpoint (format=file).
        bool zipUrl = cand.Url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || cand.Url.Contains(".zip?", StringComparison.OrdinalIgnoreCase);
        if (!zipUrl && !string.IsNullOrEmpty(cand.Url))
        {
            var bytes = await _api.DownloadSubtitleFileAsync(cand.Url, ct, season, episode).ConfigureAwait(false);
            return (bytes, null);
        }

        if (!string.IsNullOrEmpty(cand.NId))
        {
            var bytes = await _api.DownloadV2FileAsync(cand.NId, ct).ConfigureAwait(false);
            return (bytes, null);
        }

        var fallback = await _api.DownloadSubtitleFileAsync(cand.Url, ct, season, episode).ConfigureAwait(false);
        return (fallback, null);
    }

    private long Score(SubtitleCandidate c, string videoBase)
    {
        long score = 0;
        string? vfGroup = videoBase.Contains('-', StringComparison.Ordinal) ? videoBase.Split('-')[^1].TrimEnd(']').Trim() : null;
        if (vfGroup != null && c.ReleaseName.Contains('-', StringComparison.Ordinal))
        {
            string subGroup = c.ReleaseName.Split('-')[^1].TrimEnd(']').Trim();
            if (string.Equals(subGroup, vfGroup, StringComparison.OrdinalIgnoreCase))
            {
                score += _config.DownloadScoreWeightGroup;
            }
        }

        var vfTokens = Tokenize(videoBase);
        var subTokens = Tokenize(c.ReleaseName);
        score += (long)vfTokens.Intersect(subTokens, StringComparer.OrdinalIgnoreCase).Count() * _config.DownloadScoreWeightToken;
        // (19.09.2026): tie-breaker restored — the v1 client parses
        // download_count (SubdlApiClient line ~1526), so the criterion works
        // Again; 's removal reason ("v2 carries no count") no longer applies.
        score += Math.Min(c.DownloadCount, 500) * _config.DownloadScoreWeightDownload; // capped tie-breaker
        return score;
    }

    /// <summary>Video FPS from the item's primary video stream (F-M43 Stufe 1). Null when unknown.</summary>
    private double? GetVideoFps(BaseItem item)
    {
        try
        {
            var streams = _mediaSourceManager.GetMediaStreams(item.Id);
            var video = streams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (video != null && video.AverageFrameRate != 0)
            {
                return video.AverageFrameRate;
            }

            if (video != null && video.RealFrameRate != 0)
            {
                return video.RealFrameRate;
            }
        }
        catch
        {
        }

        return null;
    }

    private static HashSet<string> Tokenize(string s) =>
        new(s.Split(new[] { '.', '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// Decode SRT bytes with BOM detection (UTF-8/UTF-16LE/UTF-16BE) before
    /// any QA parse. The old hard-coded UTF-8 read mangled valid UTF-16 files
    /// (live case Coherence.zh: valid file appeared cue-less → false-positive
    /// structure reject). No BOM → strict UTF-8 (same as before).
    /// </summary>
    /// <remarks>
    /// F-M187: this is the ONLY path from downloaded bytes to text. Every
    /// consumer (QA parse, runtime check, content hash) must go through it —
    /// a caller using <c>Encoding.UTF8.GetString</c> directly turns a UTF-16
    /// payload into mojibake, and then the hash computed at download time no
    /// longer equals the hash the upload side computes later over the same
    /// file, so <c>IsContentKnown</c> misses and the file is uploaded again.
    /// The runtime check degrades silently as well: NUL bytes break the
    /// timecode regex, so it reports "matches" without actually checking.
    /// </remarks>
    private static string DecodeSrt(byte[] bytes)
    {
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE) return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes[0] == 0xFE && bytes[1] == 0xFF) return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// F-M297: renders corrected cues back to SRT text.
    /// <para>
    /// Only the timestamps are written; the text of each cue is carried through byte-for-byte,
    /// because a correction may move a cue in time and must never alter what it says. The
    /// timestamp format matches the plugin's other writers (comma decimal, three places).
    /// </para>
    /// </summary>
    /// <param name="cues">Cues in order.</param>
    /// <returns>SRT content with LF line endings; the caller applies the byte style.</returns>
    private static string RenderCues(IReadOnlyList<Qa.AnchorSync.Cue> cues)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < cues.Count; i++)
        {
            sb.Append(i + 1).Append('\n');
            sb.Append(FmtTs(cues[i].StartSec)).Append(" --> ").Append(FmtTs(cues[i].EndSec)).Append('\n');
            sb.Append(cues[i].Text.Replace("\r\n", "\n", StringComparison.Ordinal)).Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>F-M297: one SRT timestamp, comma decimal, clamped at zero.</summary>
    /// <param name="t">Time in seconds.</param>
    /// <returns>Timestamp text.</returns>
    private static string FmtTs(double t)
    {
        t = Math.Max(0.0, t);
        int h = (int)(t / 3600.0);
        int mi = (int)((t - (h * 3600.0)) / 60.0);
        double s = t - (h * 3600.0) - (mi * 60.0);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{h:00}:{mi:00}:{s:00.000}").Replace('.', ',');
    }

    /// <summary>F-M43 Stufe 2: SRT cue span vs item runtime, tolerance from config. Returns (ok, reason).</summary>
    private bool RuntimeMatches(BaseItem item, byte[] srtBytes, out string? reason)
    {
        reason = null;
        int toleranceSec = _config.DownloadRuntimeToleranceSec;
        if (toleranceSec <= 0 || (item.RunTimeTicks ?? 0) <= 0)
        {
            return true; // check disabled or no runtime metadata
        }

        string content = DecodeSrt(srtBytes);
        long firstStart = -1, lastEnd = -1;
        var timeRegex = new Regex(@"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})");
        foreach (Match m in timeRegex.Matches(content))
        {
            long start = ToMs(m, 1);
            long end = ToMs(m, 5);
            if (firstStart < 0)
            {
                firstStart = start;
            }

            lastEnd = Math.Max(lastEnd, end);
        }

        if (firstStart < 0 || lastEnd <= firstStart)
        {
            return true; // unparseable SRT → skip check (fail-open)
        }

        long spanSec = (lastEnd - firstStart) / 1000;
        long runtimeSec = (item.RunTimeTicks ?? 0) / TimeSpan.TicksPerSecond;
        if (Math.Abs(spanSec - runtimeSec) > toleranceSec)
        {
            reason = $"span {spanSec}s vs runtime {runtimeSec}s (tolerance ±{toleranceSec}s)";
            return false;
        }

        return true;
    }

    private static long ToMs(Match m, int groupStart)
    {
        int h = int.Parse(m.Groups[groupStart].Value, System.Globalization.CultureInfo.InvariantCulture);
        int min = int.Parse(m.Groups[groupStart + 1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int s = int.Parse(m.Groups[groupStart + 2].Value, System.Globalization.CultureInfo.InvariantCulture);
        int ms = int.Parse(m.Groups[groupStart + 3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return ((h * 60L + min) * 60L + s) * 1000L + ms;
    }

    /// <summary>
    /// Kill-safe atomic write (NF-4): writes to a temp file next to the target,
    /// then renames. A crash mid-write leaves the temp file (cleaned on the next
    /// run) — the target file never exists in a half-written state, so a subtitle
    /// is either fully there or counts as missing again.
    /// </summary>
    private static async Task AtomicWriteAsync(string targetPath, byte[] bytes, CancellationToken ct)
    {
        string tmpPath = targetPath + ".part";
        await File.WriteAllBytesAsync(tmpPath, bytes, ct).ConfigureAwait(false);
        File.Move(tmpPath, targetPath, overwrite: true);
    }

    /// <summary>
    /// <summary>
    /// Parses a release file name into (title, year, isSeries) for the TMDb fallback when
    /// Jellyfin's own metadata is unusable.
    /// </summary>
    /// <remarks>
    /// F-M251 (29.09.2026): this used to be a SECOND name parser with its own SxxExx regex,
    /// its own year regex and its own quality-tag list. It therefore kept every gap the main
    /// parser had already closed — measured on the test instance, the download side still
    /// resolved the Cunk name as the strand "BBC Documentaries" while the upload side had
    /// been fixed (F-M249b) — and any future name shape would have had to be fixed twice.
    /// It now delegates to <see cref="MediaNameParser"/>, which is the single authority for
    /// what a file name means. The return shape is unchanged.
    /// </remarks>
    private static (string? Title, int? Year, bool IsSeries) ParseFileNameForIds(string fileName)
    {
        var parsed = MediaNameParser.Parse(fileName);
        return (parsed.Title, parsed.Year, parsed.IsSeries);
    }

    private (string? Imdb, string? Tmdb, int Season, int Episode, bool IsSeries) ResolveIds(BaseItem item)
    {
        switch (item)
        {
            case Episode ep:
                string? seriesImdb = null;
                string? seriesTmdb = null;
                ep.Series?.ProviderIds?.TryGetValue("Imdb", out seriesImdb);
                ep.Series?.ProviderIds?.TryGetValue("Tmdb", out seriesTmdb);
                return (NormalizeId(seriesImdb), NormalizeId(seriesTmdb), ep.ParentIndexNumber ?? 0, ep.IndexNumber ?? 0, true);
            case Movie movie:
                string? imdb = null;
                string? tmdb = null;
                movie.ProviderIds?.TryGetValue("Imdb", out imdb);
                movie.ProviderIds?.TryGetValue("Tmdb", out tmdb);
                return (NormalizeId(imdb), NormalizeId(tmdb), 0, 0, false);
            default:
                return (null, null, 0, 0, false);
        }
    }

    /// <summary>
    /// Treats empty/whitespace/placeholder IDs (e.g. "0") as missing so the TMDB
    /// ladder and the skip logic handle them consistently.
    /// </summary>
    private static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var trimmed = id.Trim();
        if (trimmed == "0")
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Metadata wait (on arrival, user decision 08.09.2026): max 15 min.
    /// Metadata is polled every 5 min (3 checks total); between polls a 30 s
    /// heartbeat loop keeps the in-run watchdog alive. Returns the fresh item
    /// or null on timeout.
    /// </summary>

    private List<BaseItem> CollectItems(List<string> libraryNames)
    {
        var result = new List<BaseItem>();
        // F-M189 (24.09.2026): collect via the PATH scope, not by library name —
        // a library nested inside another one owns no items of its own (JF logs
        // "Found duplicate path"), so a name-only match returns an empty list.
        var scope = LibraryScope.Create(_libraryManager, libraryNames, _logger);
        if (scope.IsEmpty)
        {
            return result;
        }

        var root = _libraryManager.GetUserRootFolder();
        foreach (var child in root.Children)
        {
            if (child is not CollectionFolder folder)
            {
                continue;
            }

            // Walk a library when the selection touches it — the outer library of
            // a nested selection holds the items, so it must be enumerated too.
            if (!scope.IsLibraryRelevant(folder.Name) && !scope.IsSelectedName(folder.Name))
            {
                continue;
            }

            result.AddRange(folder.GetRecursiveChildren()
                .Where(i => (i is Movie || i is Episode) && !string.IsNullOrWhiteSpace(i.Path))
                .Where(scope.Contains));
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_watchdog != null && _api != null)
        {
            _api.ApiActivity -= _watchdog.Heartbeat;
        }

        _watchdog?.Dispose();
        _watchdog = null;
    }

    /// <summary>F-M152 (rev.10): next 00:00 UTC after now (fail-safe anchor for
    /// Edge 429s without rate headers).</summary>
    private DateTime NextMidnightUtc()
    {
        var now = DateTime.UtcNow;
        return now.Date.AddDays(now.TimeOfDay == TimeSpan.Zero ? 0 : 1);
    }
}
