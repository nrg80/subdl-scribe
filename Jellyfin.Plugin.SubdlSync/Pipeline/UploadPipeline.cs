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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Api;
using Microsoft.Extensions.Logging;
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
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;
using Jellyfin.Plugin.SubdlScribe.Data;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// Summary of one pipeline run (F-M23 basis).
/// </summary>
public class RunSummary
{
    /// <summary>F-M247: true when this run was a dry run — it did the work but wrote nothing.
    /// The statistics counters ignore everything a dry run produced; the run-end log line
    /// labels its numbers as hypothetical instead of claiming uploads that never happened.</summary>
    public bool IsDryRun { get; set; }

    /// <summary>Gets or sets the number of uploaded subtitles.</summary>
    public int Uploaded { get; set; }

    /// <summary>Gets or sets the number of failed uploads.</summary>
    public int Failed { get; set; }

    /// <summary>F-M81z-2 (user decision 10.09.2026): run ended by hitting the daily
    /// quota (429) — a clean rescheduled stop, NOT an upload failure. Own counter
    /// in the Task finished line, symmetric to the download direction.</summary>
    public int QuotaStopped { get; set; }

    /// <summary>F-M94h: run was not started because another run holds the global lock.</summary>
    public bool SkippedByLock { get; set; }

    /// <summary>Gets or sets the number of skipped subtitle streams.</summary>
    public int SkippedStreams { get; set; }

    /// <summary>Gets or sets the count of items skipped by dir/file patterns (F-M24d aggregate).</summary>
    public int SkippedByFilter { get; set; }

    /// <summary>Gets or sets the number of skipped items (filters, no-imdb).</summary>
    public int SkippedItems { get; set; }

    /// <summary>Gets or sets the count of items skipped for missing IMDB/season/episode.</summary>
    public int SkippedNoImdb { get; set; }

    /// <summary>
    /// F-M286: candidates DISCARDED from the upload this run — every reject path, not the QA gates
    /// alone. F-M218 said "counted AT the gate, not at the skip counter"; the code implemented that at
    /// exactly one gate while 16 other paths recorded a Rejected verdict and counted nothing, so the
    /// GUI's upload column reported a fraction of the truth. This counter is the whole set:
    /// QA gates, und-off, unmapped language, self-echo, duplicate-remote, forced (F-M284).
    /// </summary>
    public int RejectedCandidates { get; set; }

    /// <summary>Gets or sets the count of items skipped as id-unresolvable after exhausting the F-M66 retry budget.</summary>
    public int SkippedIdGaveUp { get; set; }

    /// <summary>Gets or sets the number of rate-limit hits this run.</summary>
    public int RateLimitHits { get; set; }

    /// <summary>Gets or sets the number of media files processed.</summary>
    public int FilesSeen { get; set; }

    /// <summary>
    /// Distinct media files with at least one uploaded subtitle this run.
    /// Upload counters are per subtitle STREAM (one file can upload 19 languages),
    /// so the status card reports "X subtitles in Y files" — Y comes from here.
    /// </summary>
    public int FilesUploaded { get; set; }

    /// <summary>
    /// Distinct media files with candidate streams but zero uploads this run
    /// (all streams skipped/failed). Reported as "N files skipped".
    /// </summary>
    public int FilesSkipped { get; set; }

    /// <summary>
    /// User pressed the stop button during this run.
    /// </summary>
    public bool StopRequested { get; set; }

    /// <summary>Gets or sets the number of items skipped as file-missing after exhausting retries (F-M60).</summary>
    public int SkippedFileMissing { get; set; }

    /// <summary>
    /// F-M231: items whose ids the TMDb title+year test corrected (the counterpart of the
    /// download side's counter of the same name).
    /// </summary>
    public int TypeCorrectedByFileName { get; set; }
}

/// <summary>
/// Core upload pipeline (B1): discovery → skip filters → text-sub extraction →
/// dedup → IMDB resolution → upload. QA gates are B2 (F-M13–M17), not in this version.
/// </summary>
public sealed class UploadPipeline
{
        /// <summary>
        /// Current postprocessing state for monitoring /Plugins/SubdlSync/PostprocessStatus.
        /// </summary>
        private static bool _postprocessingRunning;
    private static DateTime _postprocessingStartUtc = DateTime.MinValue;
        private static DateTime _postprocessingStartedUtc;
        private static int _postprocessingPendingTotal;
        private static int _postprocessingPendingRemaining;
        private static int _postprocessingResolved;
        private static int _postprocessingAccepted;
        private static int _postprocessingRejected;
        private static int _postprocessingDeleted;
        private static string _postprocessingLastResult = "never run";

        /// <summary>True while an upload postprocessing run is active.</summary>
        public static bool IsPostprocessingRunning => _postprocessingRunning;

        /// <summary>UTC start time of the current/last postprocessing run.</summary>
        public static DateTime PostprocessingStartedUtc => _postprocessingStartedUtc;

        /// <summary>Pending entries at the start of the current/last run.</summary>
        public static int PostprocessingPendingTotal => _postprocessingPendingTotal;

        /// <summary>Pending entries still unresolved after the current/last run.</summary>
        public static int PostprocessingPendingRemaining => _postprocessingPendingRemaining;

        /// <summary>Pending entries resolved in the current/last run.</summary>
        public static int PostprocessingResolved => _postprocessingResolved;

        /// <summary>Accepted uploads found in the current/last run.</summary>
        public static int PostprocessingAccepted => _postprocessingAccepted;

        /// <summary>Rejected/duplicate uploads found in the current/last run.</summary>
        public static int PostprocessingRejected => _postprocessingRejected;

        /// <summary>Dashboard uploads deleted in the current/last run.</summary>
        public static int PostprocessingDeleted => _postprocessingDeleted;

        /// <summary>Human-readable result of the current/last run.</summary>
        public static string PostprocessingLastResult => _postprocessingLastResult;

    private readonly ILogger<UploadPipeline> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly SubdlApiClient _api;
    /// <summary>Always use the current Plugin.Instance registry so a DB reset is respected.</summary>
    private ContentHashRegistry Registry => Plugin.Instance?.Registry ?? throw new InvalidOperationException("Plugin not initialized");
    private readonly TmdbImdbResolver _tmdb;
    private readonly PluginConfiguration _config;
    private readonly GlobalRateLimiter _limiter;
    private readonly Registry.FileRetryTracker _fileRetries;
    private readonly Registry.IdNotFoundTracker _idNotFound;

    /// <summary>F-M88c/d: central OSHash cache (oshash-cache.json in the plugin data dir).</summary>
    /// Always use the current Plugin.Instance cache so a DB reset is respected.<
    private Registry.OshashCache OshashCache => Plugin.Instance?.SharedOshashCache ?? throw new InvalidOperationException("Plugin not initialized");

    /// <summary>Resolved ffmpeg path used to extract embedded text subtitles.</summary>
    private readonly string _ffmpegPath;

    /// <summary>Process-static cache so ffmpeg is resolved once per plugin lifetime.</summary>
    private static string? _cachedFfmpegPath;

    /// <summary>Cache key — the FfmpegPath override that produced the cached path.</summary>
    private static string _cachedFfmpegConfig = "\x11NITIAL\x11"; // sentinel unlikely to match any config value

    /// <summary>F-M17y: serialized by the single global PipelineRunLock — no separate semaphore.</summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return proc.HasExited == false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception)
        {
            return true; // cannot tell — assume alive (fail-safe)
        }
    }

    /// <summary>F-M17y: throttle upload postprocessing scheduling (at most once per minute).</summary>
    private static DateTime _lastPostprocessingScheduled = DateTime.MinValue;

    /// <summary>F-M49: daily-limit waits used this run (max 1 — second hit stops the run).</summary>
    private int _dailyLimitWaits;

    /// <summary>
    /// (user decision 09.09.2026, option B): arrival runs NEVER wait out the
    /// Daily limit — they stop clean and leave the quota wait (, 2-4h random
    /// post-reset offset) to the scheduled runs. Rationale: the arrival cycle runs
    /// download THEN upload sequentially; a multi-hour download wait would block the
    /// upload direction (verified live: The Runner arrival 06:25, download waited
    /// 23.5h and the upload of 38 subtitle streams never started although the API
    /// request quota was fine). Scheduled tasks (IsArrivalRun=false) keep the wait.
    /// </summary>
    public bool IsArrivalRun { get; set; }

    // ── F-M111: queue-driven operation (user decision 12.09.2026) ───────────
    // Same contract as the download pipeline: QueueFilter restricts the item
    // loop to queued ids; ItemResult reports per-item outcomes for the queue.
    public HashSet<string>? QueueFilter { get; set; }

    /// <summary>Per-item outcome report for the queue bookkeeping (fired per item).</summary>
    public event Action<string, ItemOutcome>? ItemResult;

    /// <summary>Per-item result class reported back to the queue bookkeeping.</summary>
    public enum ItemOutcome { Done, RealFailure, NotAvailable }

    /// <summary>Fires ItemResult — no-op when nobody listens.</summary>
    private void ReportOutcome(MediaBrowser.Controller.Entities.BaseItem item, ItemOutcome outcome)
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

    /// <summary>F-M94h: run was not started because another run holds the global lock.</summary>
    public bool SkippedByLock { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadPipeline"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager (stream lookup in 10.11).</param>
    /// <param name="api">SubDL API client.</param>
    /// <param name="registry">Content hash registry.</param>
    /// <param name="tmdb">TMDB→IMDB resolver (required — gates SERIES items, F-M203).</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="limiter">Global rate limiter shared with the download pipeline (F-M20/F-M26).</param>
    public UploadPipeline(
        ILogger<UploadPipeline> logger,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        SubdlApiClient api,
        TmdbImdbResolver tmdb,
        PluginConfiguration config,
        GlobalRateLimiter limiter,
        Registry.FileRetryTracker fileRetries,
        Registry.IdNotFoundTracker idNotFound)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _api = api;
        _tmdb = tmdb;
        _config = config;
        _limiter = limiter;
        _fileRetries = fileRetries;
        _idNotFound = idNotFound;
        // F-M88c/d (user decision): OSHash cache lives in the plugin data dir via
        // the OshashCache property. Read-only shares keep their cache,
        // library folders stay clean.
        _ffmpegPath = FfmpegTools.ResolvePath(config, logger);
    }

    /// <summary>
    /// Runs a full pipeline pass over the selected libraries.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Run summary.</returns>
    public async Task<RunSummary> RunAsync(IProgress<double> progress, CancellationToken ct)
    {
        var summary = new RunSummary();

        // F-M247: record that this run was a dry run. It does the searching and the QA, so its
        // summary fills up with numbers, but it writes nothing to SubDL — the statistics must
        // not receive them. Set here, at the source, so a later counter cannot forget it.
        summary.IsDryRun = _config.DryRun;

        // F-M94h (rework 25.09.2026): ONE global run lock for all six components.
        // No long wait: a brief hand-off grace only, then reschedule by JobSpacingMinutes.
        // The _uploadWorkLock semaphore that used to sit here was removed: it guarded
        // nothing the global lock did not already cover, and its WaitAsync sat OUTSIDE
        // the try block, so a cancellation between the two acquires leaked the run lock.
        if (!await PipelineRunLock.AcquireAsync(_logger, "upload", ct).ConfigureAwait(false))
        {
            summary.SkippedByLock = true;
            var spacingUp = JobSpacingMinutes();
            ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFireAt(upload: true, DateTime.UtcNow.AddMinutes(spacingUp));
            LogUtil.Normal(_logger, "[SubDL] Upload deferred by {Spacing} min — global run lock busy.", spacingUp);
            return summary;
        }

        try
        {
        var skipFilter = new SkipFilter(_config.EffectiveSkipDirPatterns, _config.EffectiveSkipFilePatterns);

        // F-M19/F-M203: no partial operation. Without all four credentials the run cannot
        // do its job, and the failure would otherwise surface later at the first call that
        // needs the missing one (the login 404, a search without an API key, an id that
        // cannot be resolved). Refused here, with the fields named.
        var missingUp = _config.MissingCredentials();
        if (missingUp.Count > 0)
        {
            LogUtil.Normal(_logger, "[SubDL] upload: NOT started — missing credential(s): {Missing}. Set them in the plugin configuration.", string.Join(", ", missingUp));
            return summary;
        }

        if (_config.SelectedLibraries.Count == 0)
        {
            LogUtil.Normal(_logger, "[SubDL] No libraries selected — nothing to do.");
            return summary;
        }

        // NF-4: in-run watchdog — grace scales with the configured rate limit.
        // ApiActivity fires after every completed API round-trip.
        using var watchdog = new RunWatchdog(
            "SubDL", ct,
            () => RunWatchdog.GraceFromRate(_config.UploadsPerHour),
            msg => _logger.LogError("{Msg}", msg));
        _api.ApiActivity += watchdog.Heartbeat;
        var runCt = watchdog.Token;
        watchdog.Heartbeat();
        watchdog.Start();

        // F-M24a (fixed 09.09.2026): run START is Normal-level (user decision) —
        // critical errors, rate limits, run started/finished.
        // (10.09.2026): moved AFTER CollectItems — FilesSeen was always 0 here,
        // logging "0 items queued" although the scan runs 15 lines later and found 304.
        LogUtil.Normal(_logger, "[SubDL] upload: started (dry-run={Dry})", _config.DryRun);

        PipelineStopSignal.ClearStaleMarkers(Plugin.Instance?.DataFolderPath ?? string.Empty);

        // F-M24a: forward per-call API traces to LogLevel.Debug
        _api.DebugTrace += OnApiTrace;

        // F-M232: the client's LogInfo channel (login retries, 429 raw bodies, rate
        // headers) was subscribed ONLY by the postprocessing path, so every upload-run
        // login diagnostic was dropped — the 02:05 login failure left no retry line
        // behind, only the raw trace. Subscribe it per run like the trace channel.
        _api.Log += OnApiLog;

        // F-M24a (user decision 09.09.2026): TMDB calls trace at VERBOSE (SubDL API calls are Debug).
        _tmdb.Trace += OnTmdbTrace;

        // F-M54: wrong TMDB key reported ONCE at Normal/Error level.
        _tmdb.AuthError += msg => _logger.LogError("[SubDL] {Msg}", msg);

        // F-M190 (24.09.2026, user decision): a JF-vs-TMDB id disagreement is
        // reported at Normal level (the debug trace alone was invisible), and
        // TMDB wins. Detached on run end with the other TMDB handlers.
        void OnIdMismatch(string msg) => LogUtil.Normal(_logger, "[SubDL] {Msg}", msg);
        _tmdb.IdMismatch += OnIdMismatch;

        // The 1-min metadata grace lives in the ARRIVAL WATCHER (once per
        // event, before the download run which fires first) — not here. Scheduled
        // runs never get one. No-id items still have the per-item 15-min ladder.

        var items = CollectItems(_config.SelectedLibraries);
        summary.FilesSeen = items.Count; // (not logged — stats-free log)

        // (user decision 08.09.2026): id-less items go to the BACK — their
        // 15-min metadata ladder must not block items that already have ids.
        // Stable partition, no TMDB calls (raw metadata only).
        var withIds = new List<BaseItem>();
        var withoutIds = new List<BaseItem>();
        foreach (var it in items)
        {
            var (im, tm, _, _, _) = ResolveIdsRaw(it);
            (string.IsNullOrWhiteSpace(im) && string.IsNullOrWhiteSpace(tm) ? withoutIds : withIds).Add(it);
        }

        if (withoutIds.Count > 0)
        {
            // No count line — partition stays internal.
            items = withIds.Concat(withoutIds).ToList();
        }

        // F-M59 (user decision 09.09.2026): LIFO within BOTH partitions — newest items
        // (DateCreated descending) get the daily quota first, old library stock last.
        // LIFO queue order (F-M59): a brand-new arrival must not lose
        // the quota race against 284 legacy items (verified live 09.09.2026: The Runner,
        // arrived 06:14, hit the daily limit after the legacy stock had burned the
        // requests). The id-partition keeps priority (user decision: "id-less items go
        // to the back") — id-less items stay at the back even when newer.
        items = items
            .Select((it, i) => (Item: it, Pos: i, Created: it.DateCreated))
            .OrderByDescending(x => x.Created)
            .ThenBy(x => x.Pos)
            .Select(x => x.Item)
            .ToList();

        int idx = 0;

        // F-M8: login once per run — token needed for uploads (dry-run skips it)
        if (!_config.DryRun)
        {
            // F-M49 (user decision 08.09.2026 "which messages to react to"): a failed
            // login (wrong credentials NOT_FOUND / 403 / daily limit) must abort the
            // run with a clear message — not crash the task with an unhandled exception.
            try
            {
                await _api.LoginAsync(runCt).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // F-M232 (28.09.2026): a transient login failure (5xx/non-JSON after all
                // retries) is a server overload, not a config problem — F-M67 semantics
                // apply: schedule the overload fire and end the run cleanly, exactly like
                // a mid-run service_busy. Only a real verdict (wrong credentials 404,
                // 403, hourly cap) stops without a fire.
                if (_api.TransientOverload)
                {
                    _logger.LogWarning("[SubDL] {Msg}", ex.Message);
                    ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleOverloadFire(upload: true, DateTime.UtcNow);
                    return summary;
                }

                _logger.LogError("[SubDL] {Msg} — upload run aborted.", ex.Message);
                return summary;
            }
        }

        // F-M17f (own-account mySubtitles fetch) REMOVED 08.09.2026: the strict F-M17a
        // search (imdb+season+episode+language) queries the SAME SubDL database that
        // mySubtitles reads from — own uploads are included in the search results and
        // a release match skips them. No extra 8000-item fetch needed; also F-M17f's
        // blanket (imdb, episode, lang) skip would block desired second releases.

        foreach (var item in items)
        {
            // User stop marker — check BEFORE doing any work.
            if (PipelineStopSignal.IsStopped(Plugin.Instance?.DataFolderPath ?? string.Empty, upload: true, runCt))
            {
                LogUtil.Normal(_logger, "[SubDL] upload: user stop requested — finishing current run.");
                summary.StopRequested = true;
                break;
            }

            // F-M111: queue-driven — only queued items are worked (seeder owns admission).
            if (QueueFilter != null && !QueueFilter.Contains(item.Id.ToString()))
            {
                continue;
            }

            runCt.ThrowIfCancellationRequested();
            watchdog.Heartbeat();
            idx++;
            // Fine-grained progress: item start + item done (per-stream uploads report in between)
            progress?.Report((idx - 1 + 0.05) / Math.Max(items.Count, 1) * 100);

            string? mediaPath = item.Path;
            if (string.IsNullOrWhiteSpace(mediaPath))
            {
                // F-M255: a skipped item is named, not silently dropped.
                LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] SKIP {Name} — item has no file path", item.Name);
                continue;
            }

            // F-M88c: FIRST check — if this media file is already upload-complete, skip it
            // entirely without touching the OSHash cache, ffprobe, streams, or directory listings.
            // MediaHashFor is deliberately NOT called here for settled files.
            string? earlyHash = Registry.GetMediaHash(mediaPath);
            if (Registry.IsSubtitlesUploaded(earlyHash))
            {
                summary.SkippedItems++;
                summary.FilesSkipped++;
                LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] SKIP {File} — upload file-complete", Path.GetFileName(mediaPath));

                continue;
            }

            // F-M60 (user decision 09.09.2026): items whose file is gone (deleted from
            // disk but still in the JF catalog) fail extraction on EVERY run. After
            // FileRetryLimit consecutive failures they are skipped as "file-missing"
            // (aggregate counter, no per-run error spam). A success resets the counter.
            if (_fileRetries.IsExhausted(item.Id.ToString(), _config.FileRetryLimit))
            {
                summary.SkippedFileMissing++;
                // F-M88c: the file is permanently gone — drop its OSHash cache entry too.
                // Same FileRetryLimit knob governs both (user decision): after the last
                // failed attempt the dead cache entry would otherwise linger forever.
                OshashCache.Remove(mediaPath);
                OshashCache.Flush(); // kill-safe: the prune survives a cancelled run

                // Remove the media and subtitle state for the missing file.
                // F-M22 (defect fixed 02.10.2026): NOT in a dry run — this branch returns before the
                // upload dry-run exit (the per-item DRY-RUN block below), so a dry run cleared the
                // item's whole registry state. "No stored verdict is written or cleared by a dry run."
                var missingHash = Registry.GetMediaHash(mediaPath);
                if (!string.IsNullOrEmpty(missingHash) && !_config.DryRun)
                {
                    Registry.MarkAndFlush(() => Registry.DeleteMediaAndSubtitles(missingHash));
                }

                // F-M255: name the file that is gone and gave up after FileRetryLimit attempts.
                LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] SKIP {File} — file missing, retries exhausted", Path.GetFileName(mediaPath));
                continue;
            }

            // F-M60: explicit existence check BEFORE any ffmpeg work — a missing file
            // fails fast here (no stream scan, no API calls) and bumps the retry counter.
            if (!File.Exists(mediaPath))
            {
                // F-M22 (defect fixed 02.10.2026): see the download side — a dry run does not burn a
                // retry, because the give-up branch deletes the item's registry state.
                if (!_config.DryRun)
                {
                    _fileRetries.RecordFailure(item.Id.ToString());
                }
                else
                {
                    LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] DRY-RUN {File} is missing — retry counter left untouched (F-M22)", Path.GetFileName(mediaPath));
                }

                summary.Failed++;
                ReportOutcome(item, ItemOutcome.RealFailure);
                LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] FILE MISSING {File} — attempt {N} (skip after {Limit})", Path.GetFileName(mediaPath), _fileRetries.Count, _config.FileRetryLimit);

                continue;
            }

            // F-M61: path-independent dedup key — OSHash of the video content, computed ONCE
            // per item per run (~128 KiB read). Survives restarts, moves and renames, unlike
            // the previous mediaPath key that Load() could not rebuild after a restart.
            // F-M88c/d: served from the central oshash-cache.json while the fingerprint
            // (size+mtime) matches and the OshashRefresh cadence (Never/Always/Weekly/
            // Monthly/Yearly) trusts the entry.
            string? mediaHash = MediaHashFor(mediaPath);

            // F-M22 (defect fixed 02.10.2026): this upsert runs per item, ahead of the upload
            // dry-run exit below — so a dry run wrote rows for items it only described. Held back
            // like its counterpart on the download side; the hash is still computed, because the
            // candidate path needs it either way.
            if (!string.IsNullOrEmpty(mediaHash) && !_config.DryRun)
            {
                Registry.MarkAndFlush(() => Registry.EnsureMedia(mediaHash, item.Id.ToString("D"), mediaPath));
            }

            // Per-item outcome flags for the file-level counters on the status card.
            bool itemUploadedThisRun = false;
            bool itemHadCandidates = false;
            bool itemHadFailures = false; // F-M88c: do not mark file complete if any candidate failed

            // F-M34/M35: skip filters
            string? skip = skipFilter.GetSkipReason(mediaPath);
            if (skip != null)
            {
                summary.SkippedItems++;
                summary.SkippedByFilter++;
                summary.FilesSkipped++; // Fully skipped file for the status card
                LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] SKIP {File} — {Reason}", Path.GetFileName(mediaPath), skip);

                continue;
            }

            // F-M66/F-M151: id resolution in ONE pass. Raw JF ids first, then the
            // optional upload ID quality gate (default ON) validates/corrects them
            // against TMDB. Items that remain unresolvable are requeued with the
            // id-not-found budget.
            var (imdbId, tmdbIdRaw, season, episode, isSeries) = ResolveIdsRaw(item);


            // F-M190 (24.09.2026, user decision): the FILE NAME is authoritative
            // for the media type and the season/episode numbers. An episode that
            // lives in a library typed "movies" is imported by Jellyfin as a
            // Movie, so class-based detection searches TMDB for a FILM named
            // after the episode ("Norway No How" instead of series "Best
            // Medicine") and finds nothing. Same rule the movies2 watchdog used.
            var parsedName = MediaNameParser.Parse(mediaPath);
            if (parsedName.IsSeries && !isSeries)
            {
                isSeries = true;
                season = parsedName.Season ?? season;
                episode = parsedName.Episode ?? episode;
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[SubDL] {File}: file name marks a series (S{Season}E{Episode}) while Jellyfin reports no series — resolving as series.",
                    Path.GetFileName(mediaPath), parsedName.Season ?? 0, parsedName.Episode ?? 0);
            }

            // Title for the API search: for a series the SERIES name is required,
            // never the episode title. Jellyfin knows it for a real Episode; for a
            // mis-typed one the file name carries it.
            string searchTitle =
                isSeries
                    ? (item as Episode)?.Series?.Name ?? parsedName.Title ?? item.Name
                    : item.Name;
            int? searchYear =
                (item as Movie)?.ProductionYear is int py ? py
                : (item as Episode)?.Series?.ProductionYear is int sy ? sy
                : parsedName.Year;

            // F-M231 (27.09.2026, user decision "Wir testen jfs id immer gegen tmdb"): the
            // FILE NAME's year wins for the id test. Jellyfin takes its year from the same
            // metadata that may be pinned to the wrong title, so asking with Jellyfin's year
            // can only confirm the mistake (Mayday: nfo year 2003 vs. name year 2026 — the
            // test needs 2026 to see the film at all). A name without a year keeps JF's.
            int? idTestYear = parsedName.Year ?? searchYear;

            // F-M203 (25.09.2026, user decision "TMDb-Key wird verpflichtend, nur Serien
            // verweigern"): without a TMDB key the id resolution that guarantees a SHOW id
            // cannot run, and a series uploaded with Jellyfin's own ids would carry an
            // EPISODE id in the series slot (measured on the test library: every
            // mis-typed episode carries an episode id in BOTH provider fields). So a
            // SERIES is refused outright — fail-closed, reported, requeued. A FILM may
            // still upload: Jellyfin's film ids are the film's own ids, nothing is
            // mis-slotted, and the plugin stays usable for film-only libraries.
            // F-M191 (24.09.2026, user decision): a SERIES item needs SHOW-level ids —
            // TMDB and SubDL expect the tvshow id plus season/episode, never an episode
            // id. Jellyfin's provider ids for a mis-typed episode carry EPISODE ids in
            // both fields (measured: Tmdb 6676484 = S1E1, Imdb tt38949652 = S1E10 of the
            // SAME show), and /tv/{id} answers 404 for them — the old code kept them
            // silently, so the upload metadata carried an episode id where the series id
            // belongs, and the conflict report could never fire. Placed AFTER the name
            // detection (so name-detected series are covered too) and BEFORE the gate,
            // which then validates show-level ids instead of episode ids.
            //
            // F-M202 (25.09.2026): season/episode come from the parsed FILE NAME — they are
            // what lets an episode id be PROVEN against a candidate show.
            if (isSeries)
            {
                var (nlImdb, nlTmdb) = await _tmdb.NormalizeSeriesLevelIdsAsync(
                    imdbId, tmdbIdRaw, searchTitle, searchYear, season, episode, runCt).ConfigureAwait(false);
                imdbId = nlImdb;
                tmdbIdRaw = nlTmdb;
            }

            // F-M231 (27.09.2026, user decision "Wir testen jfs id immer gegen tmdb"):
            // ALWAYS test Jellyfin's ids against TMDb by TITLE and YEAR. The pair check
            // inside ResolveAndValidateIdsAsync is tautological (it asks whether the two
            // ids describe the same entity, which a wrongly pinned pair does), so on its
            // own it confirms an item whose metadata points at the wrong title.
            var verified = await _tmdb.VerifyIdsAgainstTmdbAsync(imdbId, tmdbIdRaw, searchTitle, idTestYear, isSeries, runCt).ConfigureAwait(false);
            if (verified.Corrected)
            {
                imdbId = verified.Imdb;
                tmdbIdRaw = verified.Tmdb;
                isSeries = verified.IsSeries;
                summary.TypeCorrectedByFileName++; // same counter as the other id corrections
            }

            string title = searchTitle;
            int? year = searchYear;
            var (correctedImdb, correctedTmdb) = await _tmdb.ResolveAndValidateIdsAsync(imdbId, tmdbIdRaw, title, year, isSeries, runCt).ConfigureAwait(false);
            if (correctedImdb != null || correctedTmdb != null)
            {
                imdbId = correctedImdb ?? imdbId;
                tmdbIdRaw = correctedTmdb ?? tmdbIdRaw;
                _idNotFound.RecordSuccess(item.Id.ToString());
            }
            else
            {
                imdbId = null;
                tmdbIdRaw = null;
            }

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                // The budget applies to ANY item that keeps failing the ladder.
                if (_idNotFound.IsExhausted(item.Id.ToString(), _config.IdRetryLimit))
                {
                    summary.SkippedIdGaveUp++;
                    if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL] SKIP {File} — id-resolution exhausted ({Count}/{Limit} failed ladders) — no TMDB spend until ids change",
                            Path.GetFileName(mediaPath), _idNotFound.Peek(item.Id.ToString()), _config.IdRetryLimit);
                    }
                    continue;
                }

                // No 60 s wait; go straight to the TMDB ladder when JF ids are empty.
                if (string.IsNullOrWhiteSpace(imdbId) && !string.IsNullOrWhiteSpace(tmdbIdRaw) && _tmdb.IsConfigured)
                {
                    imdbId = await _tmdb.ResolveImdbAsync(tmdbIdRaw, isSeries, runCt).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(imdbId) && _tmdb.IsConfigured)
                {
                    string ladderTitle = searchTitle;
                    int? ladderYear = searchYear;

                    // F-M190: for a name-detected series the type may still be
                    // wrong (Jellyfin said Movie) — ask TMDB WITHOUT a type
                    // assumption so its answer decides, and take the ids from
                    // that same hit. Falls back to the type-specific title
                    // search when the multi-search finds nothing.
                    var multi = await _tmdb.ResolveByMultiSearchAsync(ladderTitle, ladderYear, isSeries, runCt).ConfigureAwait(false);
                    if (multi.Found)
                    {
                        imdbId = multi.Imdb;
                        tmdbIdRaw = multi.Tmdb;
                        if (multi.IsSeries != isSeries)
                        {
                            isSeries = multi.IsSeries;
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL] {File}: TMDB resolved this as a {Kind} — type corrected.",
                                Path.GetFileName(mediaPath), multi.IsSeries ? "series" : "movie");
                        }
                    }
                    else
                    {
                        (imdbId, tmdbIdRaw) = await _tmdb.ResolveImdbByTitleAsync(ladderTitle, ladderYear, isSeries, runCt).ConfigureAwait(false);
                    }
                }

                if (!string.IsNullOrWhiteSpace(imdbId))
                {
                    _idNotFound.RecordSuccess(item.Id.ToString());
                }
                else if (string.IsNullOrWhiteSpace(tmdbIdRaw))
                {
                    // F-M22 (defect fixed 02.10.2026): same as the download side — a dry run spends
                    // no part of the id-resolution budget, because its limit retires the item.
                    if (!_config.DryRun)
                    {
                        _idNotFound.RecordFailure(item.Id.ToString());
                    }

                    summary.SkippedNoImdb++;
                    summary.FilesSkipped++;
                    if (_config.LogMode >= LogLevelMode.Verbose)
                    {
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL] SKIP {File} — no-imdb (TMDB ladder failed) — requeued, not-found +1",
                            Path.GetFileName(mediaPath));
                    }

                    continue;
                }
            }

            bool idsOk = !string.IsNullOrWhiteSpace(imdbId)
                         && (!isSeries || (season >= 0 && episode >= 0));
            if (!idsOk)
            {
                summary.SkippedNoImdb++;
                summary.FilesSkipped++; // Fully skipped file for the status card
                LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] SKIP {File} — no-imdb/no-season-ep (TMDB ladder)", Path.GetFileName(mediaPath));
                continue;
            }

            // (user decision 12.09.2026): IMDb present, TMDB missing → one
            // /find-by-imdb lookup so the upload carries tmdb_id metadata on SubDL.
            // Never blocking: null result = upload proceeds with imdb alone.
            if (string.IsNullOrWhiteSpace(tmdbIdRaw) && !string.IsNullOrWhiteSpace(imdbId) && _tmdb.IsConfigured)
            {
                string? tmdbBackfilled = await _tmdb.ResolveTmdbByImdbAsync(imdbId, runCt).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(tmdbBackfilled))
                {
                    tmdbIdRaw = tmdbBackfilled;
                }
            }

            // ============================================================
            // F-M69 (user decision 09.09.2026 "extract all languages per file
            // first, ONE comma-separated search, find all doubles from it"):
            // three-phase item processing. Phase 1 screens ALL streams + loose
            // SRTs LOCALLY (extraction, QA, dedup, F-M68 reject store) with ZERO
            // API calls. Phase 2 makes exactly ONE batched search call with the
            // comma-separated language list of the survivors. Phase 3 uploads the
            // streams that are not already on SubDL. Before F-M69 every stream
            // cost its own search request (Fauda S05E02: 30 streams = 30 searches);
            // now a whole item costs ONE search (30x fewer requests per item).
            // ============================================================

            // F-M3: only items with embedded TEXT subtitle streams.
            // IsExternal filters out loose .srt files JF mixes into the stream
            // list — ffmpeg extraction only works on container streams.
            // F-M246/F-M284: a FORCED track is not the film's subtitle — it carries only the lines
            // of foreign-language scenes, so there is nothing worth publishing. The question is
            // asked through the ONE predicate (SidecarNaming.IsDialogueStream), which the seeder's
            // prefilter and the language gate ask as well.
            // NOTE: allSubs stays UNFILTERED for the position lookup below — ffmpeg's 0:s:N
            // counts every subtitle stream including the forced and the bitmap ones, so the
            // index must be computed against the whole list.
            var allSubs = _mediaSourceManager.GetMediaStreams(item.Id)?
                .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal)
                .ToList() ?? new List<MediaStream>();
            var textStreams = allSubs
                .Where(Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.IsDialogueStream)
                .ToList();

            // ============================================================
            // F-M5: ONE ffmpeg pass for every text stream of this file, before the
            // per-stream loop. The single pass exists because the cost of the
            // per-stream shape is re-reading the whole container: "to avoid re-reading
            // large CIFS files". The C# port lost it and extracted per stream, so
            // ffmpeg reopened a ~1 GB file once per stream. Measured on prod
            // 28.09.2026 (upload run 15:24, 90 items, median item 11 s): the only
            // four items over 10 min were the stream-rich ones — 29 streams 31 min,
            // 33 streams 26 min, 83 streams 21 min, 33 streams 10 min — because
            // extraction re-read the container for each one. 81 files in the library
            // carry 80+ subtitle streams.
            // ============================================================
            var textPositions = textStreams
                .Select(s => allSubs.FindIndex(x => x == s))
                .Where(p => p >= 0)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
            var (extracted, batchExit) = await FfmpegTools.ExtractAllAsync(_ffmpegPath, mediaPath, textPositions, _logger, _config, runCt).ConfigureAwait(false);

            // When the one-pass call failed as a whole (non-zero exit, spawn error or
            // exception), a null per stream is a SUSPICION, not a verdict — ffmpeg never
            // got to write those files. Then every stream that came back empty is retried
            // individually, because a partial loss would otherwise be recorded as a real
            // extraction failure and the stream would be reported as failed forever.
            // Exit 0 means ffmpeg read every requested stream and some simply carried no
            // text: those nulls are verdicts and must NOT be retried.
            bool retryPerStream = batchExit != 0;
            if (textPositions.Count > 0)
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[SubDL] extraction: {Count} text stream(s) in ONE ffmpeg pass for {File} (exit {Exit}){Note}",
                    textPositions.Count, Path.GetFileName(mediaPath), batchExit,
                    retryPerStream ? " — per-stream retry for empty streams" : string.Empty);
            }

            // Phase 1 candidates: (kind, stream/loose-data, lang, srtContent)
            // F-M71: HearingImpaired rides along — SDH streams upload with hi=true.
            var phase1 = new List<(bool IsLoose, MediaStream? Stream, int SubPos, string LoosePath, string Lang, string Srt, bool HearingImpaired, bool Forced)>();
            int looseSeen = 0, embeddedSeen = 0;

            foreach (var stream in textStreams)
            {
                runCt.ThrowIfCancellationRequested();
                watchdog.Heartbeat();
                // ffmpeg's 0:s:N numbering counts ALL subtitle streams (text + bitmap),
                // so the position must be computed against the unfiltered list
                int subPos = allSubs.FindIndex(s => s == stream);
                if (subPos < 0)
                {
                    // F-M255: the stream could not be located in the unfiltered list — without this
                    // line the position simply vanished from the run.
                    LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] SKIP {File} — subtitle stream not in the unfiltered list", Path.GetFileName(mediaPath));
                    continue;
                }

                // F-M10: language mapping — 'und' (undefined) streams resolve via
                // F-M74 detection (tickbox UploadResolveUnd): detected language replaces
                // the tag; without the tickbox every 'und' stream is
                // removed from the upload set.
                string rawLang = stream.Language ?? string.Empty;
                // A NULL/empty tag means exactly what 'und' means (F-M261)
                // (movies2-sub-uploader.py: tags.get('language', 'und')). Before this fix
                // untagged streams (The.Hawk: 40 per file) fell through MapToSubdl → null
                // and were silently skipped without ever learning a pair → permanent walk.
                bool isUnd = string.IsNullOrWhiteSpace(rawLang)
                    || rawLang.Equals("und", StringComparison.OrdinalIgnoreCase)
                    || rawLang.Equals("undefined", StringComparison.OrdinalIgnoreCase);
                string? lang = null;

                if (isUnd)
                {
                    if (!_config.UploadResolveUnd)
                    {
                        summary.SkippedStreams++;
                        summary.RejectedCandidates++; // F-M286
                        LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] SKIP {File}: untagged stream s{Pos} — resolve-und disabled",
                            Path.GetFileName(mediaPath), subPos);
                        // One row, one verdict: the stream is rejected with und-off. There is no
                        // second "settled" row to keep in sync — terminality is derived from this one.
                        Registry.MarkAndFlush(() => Registry.MarkEmbed(
                            mediaHash, subPos, "UN", Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream),
                            SubtitleStatus.Rejected, reason: RejectReason.UndOff));
                        continue;
                    }

                    // Detection needs the extracted text — defer resolution to phase 1;
                    // mark via the placeholder and resolve after extraction below.
                    lang = "UN"; // sentinel — resolved after ExtractSingleStreamAsync
                }
                else
                {
                    lang = LanguageMapper.MapToSubdl(rawLang);
                }

                if (lang == null)
                {
                    // Unmappable tag — terminal for this position.
                    summary.SkippedStreams++;
                    summary.RejectedCandidates++; // F-M286
                    Registry.MarkAndFlush(() => Registry.MarkEmbed(
                        mediaHash, subPos, rawLang, Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream),
                        SubtitleStatus.Rejected, reason: RejectReason.UnmappedLanguage));
                    // F-M255: the registry row above is not visible in the log — name the tag.
                    LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] SKIP {File} s{Pos} — unmappable language tag {Tag}", Path.GetFileName(mediaPath), subPos, rawLang);
                    continue;
                }

                // F-M4/F-M61: dedup — (media content hash, language) pair already uploaded?
                // F-M71: HI variants have their own key space — a normal upload never blocks an SDH variant.
                bool streamHi = Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream);
                if (Registry.IsUploaded(mediaHash, lang, streamHi))
                {
                    // Pair already up. This position is NOT uploaded by itself, so it does not get
                    // an uploaded verdict of its own; it is recorded as rejected-by-self-echo, which
                    // is what it factually is: we did not send this stream because we already had
                    // this language. Marking it "uploaded" would claim an upload that never happened.
                    // F-M286: a Rejected verdict and no counter was a silent discard — the row said
                    // "rejected" while the GUI column never learned about it.
                    summary.RejectedCandidates++; // F-M286
                    Registry.MarkAndFlush(() => Registry.MarkEmbed(
                        mediaHash, subPos, lang, streamHi,
                        SubtitleStatus.Rejected, reason: RejectReason.SelfEcho));
                    // F-M255: one line per discarded stream, whatever the reason.
                    LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] SKIP {File} s{Pos} [{Lang}] — duplicate-self-echo (pair already uploaded, HI={Hi})", Path.GetFileName(mediaPath), subPos, lang, streamHi);
                    continue;
                }

                // F-M68: persistent reject store — a stream QA-rejected or dup-skipped
                // in a PREVIOUS run never burns a search request again.
                string? rejectReason = Registry.EmbedRejectedReason(mediaHash, subPos);
                if (rejectReason != null)
                {
                    summary.SkippedStreams++;
                    summary.RejectedCandidates++; // F-M286
                    // Known reject for THIS position — carry the original reason forward verbatim
                    // instead of replacing it with a generic note, so the cause stays readable.
                    Registry.MarkAndFlush(() => Registry.MarkEmbed(
                        mediaHash, subPos, lang, streamHi,
                        SubtitleStatus.Rejected, reason: rejectReason));
                    LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] SKIP {File} [{Lang}] — {Reason}", Path.GetFileName(mediaPath), lang, ExplainReject(rejectReason));

                    continue;
                }

                // F-M5: take the text from the one-pass extraction. Hit ffmpeg again only
                // when that pass failed as a whole and this stream came back empty — the
                // exit code decides, because an empty stream under a clean pass is a real
                // verdict (the stream carries no text) and must not cost another read.
                string? srt = extracted.TryGetValue(subPos, out var fromPass) ? fromPass : null;
                if (srt == null && retryPerStream)
                {
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[SubDL] per-stream retry after the failed one-pass call: {File} stream {Pos}",
                        Path.GetFileName(mediaPath), subPos);
                    srt = await ExtractSingleStreamAsync(mediaPath, subPos, runCt).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(srt))
                {
                    summary.Failed++;
                    ReportOutcome(item, ItemOutcome.RealFailure);
                    _logger.LogError("[SubDL] EXTRACTION FAILED {File} stream {Pos}", Path.GetFileName(mediaPath), subPos);
                    continue;
                }

                // F-M185: canonicalize BEFORE QA, hashing and upload. ffmpeg writes
                // CRLF for some container streams and LF for others (measured: the
                // same episode yielded 293 CR in one run, 0 in the next), so without
                // this the identical text produced two different hashes AND two
                // different payloads — the duplicate pair in the SubDL inventory.
                srt = ContentHashRegistry.NormalizeSrt(srt);

                // F-M74: resolve the 'und' sentinel with real detection on the extracted
                // text (detection runs after extraction).
                // Size order matters: the 2 KB floor (hard, F-M16) runs BEFORE
                // detection — tiny und-streams skip instead of getting a garbage verdict.
                // Success → the detected language replaces the tag; failure → skip.
                if (lang == "UN")
                {
                    if (srt.Length < 2048)
                    {
                        summary.SkippedStreams++;
                        summary.RejectedCandidates++; // F-M286
                        LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] SKIP {File}: untagged stream s{Pos} — too small to detect a language ({Bytes} bytes)",
                            Path.GetFileName(mediaPath), subPos, srt.Length);
                        // Persist the skip so the file can reach file-complete —
                        // otherwise und-skips never learn a pair and the file is re-walked forever.
                        string undContentHashFm87 = ContentHashRegistry.ComputeHash(srt);
                        Registry.MarkAndFlush(() => Registry.MarkEmbed(
                            mediaHash, subPos, "UN", Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream),
                            SubtitleStatus.Rejected, contentHash: undContentHashFm87, reason: RejectReason.UndTooSmall));
                        continue;
                    }

                    string? detected = Qa.QaGates.DetectLanguage(srt);
                    if (detected == null)
                    {
                        summary.SkippedStreams++;
                        summary.RejectedCandidates++; // F-M286
                        LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] SKIP {File}: untagged stream s{Pos} — language could not be detected ({Bytes} bytes)",
                            Path.GetFileName(mediaPath), subPos, srt.Length);
                        // Same — persist detection failures so files can complete.
                        string undContentHashFm87 = ContentHashRegistry.ComputeHash(srt);
                        Registry.MarkAndFlush(() => Registry.MarkEmbed(
                            mediaHash, subPos, "UN", Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream),
                            SubtitleStatus.Rejected, contentHash: undContentHashFm87, reason: RejectReason.UndDetectionFailed));
                        continue;
                    }

                    lang = detected;
                    LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] {File}: untagged stream s{Pos} — detected language {Lang} ({Bytes} bytes)",
                        Path.GetFileName(mediaPath), subPos, lang, srt.Length);
                }

                embeddedSeen++;
                phase1.Add((false, stream, subPos, string.Empty, lang, srt, streamHi, forced: false));
            }

            // F-M48: loose SRT files — same phase-1 treatment (read + QA local)
            foreach (var (loosePath, looseLang, looseHi, looseForced) in FindLooseSrts(mediaPath))
            {
                runCt.ThrowIfCancellationRequested();
                watchdog.Heartbeat();

                // F-M284: a forced subtitle is OBSERVED, never DELIVERED — SubDL has no forced
                // counterpart (no filter, no field, no candidate names it), so there is nothing to
                // publish and nothing to search for. `IsDeliverable` is the predicate that says so;
                // this is a caller that PLANS WORK, which is exactly what must check it. Returning
                // before the read, the hash and the QA keeps the file out of the run entirely rather
                // than spending a search request to have the server reject it.
                //
                // The embedded side needs no counterpart here: its enumeration already runs through
                // IsDialogueStream, which drops a forced track before it can become a candidate.
                if (looseForced)
                {
                    summary.SkippedStreams++;
                    summary.RejectedCandidates++; // F-M286
                    LogUtil.PerItem(_config.LogMode, _logger,
                        "[SubDL] SKIP {File} — forced subtitle, observed but never uploaded (F-M284)",
                        Path.GetFileName(loosePath));
                    continue;
                }

                if (Registry.IsUploaded(mediaHash, looseLang, looseHi))
                {
                    LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] Loose SRT already uploaded — skipping: {File} ({Lang}{Hi})", Path.GetFileName(loosePath), looseLang, looseHi ? ",HI" : "");
                    continue;
                }

                // F-M68: loose file rejected previously — skip without a search
                // Sidecars are keyed by content: hash what is on disk, then ask about that content.
                string looseHashProbe;
                try
                {
                    looseHashProbe = ContentHashRegistry.ComputeHash(await File.ReadAllTextAsync(loosePath, runCt).ConfigureAwait(false));
                }
                catch
                {
                    summary.Failed++;
                    LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] SKIP {Path} — sidecar unreadable", loosePath);
                    continue;
                }

                string? looseReject = Registry.SidecarRejectedReason(looseHashProbe);
                if (looseReject != null)
                {
                    summary.SkippedStreams++;
                    summary.RejectedCandidates++; // F-M286
                    LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] SKIP {File} [{Lang}] — {Reason}", Path.GetFileName(loosePath), looseLang, ExplainReject(looseReject));

                    continue;
                }

                try
                {
                    var looseSrt = await File.ReadAllTextAsync(loosePath, runCt).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(looseSrt))
                    {
                        continue;
                    }

                    // F-M185: same canonical form for sidecar .srt files — a loose
                    // file written with CRLF must hash and upload like its LF twin.
                    looseSrt = ContentHashRegistry.NormalizeSrt(looseSrt);

                    // F-M74 for SIDECARS: a name without a language token is the sidecar
                    // counterpart of an 'und' stream — the name says nothing, so the text
                    // has to. Same toggle as the embedded path (UploadResolveUnd), same
                    // order, same outcomes:
                    //   switch off + no token in the name → skipped, logged
                    //   text below the 2 KB floor (F-M16)  → skipped as too little text
                    //   detection finds no language         → skipped
                    //   detection finds one                 → it IS the language
                    // The unlabelled "<base>.srt" used to be read as EN by Jellyfin's
                    // convention, which uploaded German text as English; that guess is gone
                    // (user decision 30.09.2026).
                    string? looseLangResolved = looseLang;
                    if (looseLangResolved == null)
                    {
                        if (!_config.UploadResolveUnd)
                        {
                            summary.SkippedStreams++;
                            summary.RejectedCandidates++; // F-M286
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL] SKIP {File} — sidecar names no language and resolve-und is disabled",
                                Path.GetFileName(loosePath));
                            string offHash = ContentHashRegistry.ComputeHash(looseSrt);
                            Registry.MarkAndFlush(() => Registry.MarkSidecar(
                                offHash, mediaHash, "UN", looseHi, looseForced,
                                SubtitleStatus.Rejected, reason: RejectReason.UndOff,
                                fileName: Path.GetFileName(loosePath), path: loosePath));
                            continue;
                        }

                        if (looseSrt.Length < 2048)
                        {
                            summary.SkippedStreams++;
                            summary.RejectedCandidates++; // F-M286
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL] SKIP {File} — sidecar names no language and is too small to detect one ({Bytes} bytes)",
                                Path.GetFileName(loosePath), looseSrt.Length);
                            string smallHash = ContentHashRegistry.ComputeHash(looseSrt);
                            Registry.MarkAndFlush(() => Registry.MarkSidecar(
                                smallHash, mediaHash, "UN", looseHi, looseForced,
                                SubtitleStatus.Rejected, reason: RejectReason.UndTooSmall,
                                fileName: Path.GetFileName(loosePath), path: loosePath));
                            continue;
                        }

                        string? looseDetected = Qa.QaGates.DetectLanguage(looseSrt);
                        if (looseDetected == null)
                        {
                            summary.SkippedStreams++;
                            summary.RejectedCandidates++; // F-M286
                            LogUtil.PerItem(_config.LogMode, _logger,
                                "[SubDL] SKIP {File} — sidecar names no language and none could be detected ({Bytes} bytes)",
                                Path.GetFileName(loosePath), looseSrt.Length);
                            string failHash = ContentHashRegistry.ComputeHash(looseSrt);
                            Registry.MarkAndFlush(() => Registry.MarkSidecar(
                                failHash, mediaHash, "UN", looseHi, looseForced,
                                SubtitleStatus.Rejected, reason: RejectReason.UndDetectionFailed,
                                fileName: Path.GetFileName(loosePath), path: loosePath));
                            continue;
                        }

                        looseLangResolved = looseDetected;
                        LogUtil.PerItem(_config.LogMode, _logger,
                            "[SubDL] {File}: sidecar names no language — detected {Lang} ({Bytes} bytes)",
                            Path.GetFileName(loosePath), looseDetected, looseSrt.Length);
                    }

                    looseSeen++;
                    phase1.Add((true, null, -1, loosePath, looseLangResolved, looseSrt, looseHi, looseForced));
                }
                catch (Exception ex)
                {
                    summary.Failed++;
                    ReportOutcome(item, ItemOutcome.RealFailure);
                    _logger.LogError("[SubDL] READ FAILED {File}: {Msg}", Path.GetFileName(loosePath), ex.Message);
                }
            }

            if (phase1.Count == 0)
            {
                // nothing survived local screening — no API call at all (F-M69)
                // This IS a "fully skipped" file for the status card — it
                // had streams, none survived QA, and nothing will ever be uploaded
                // from it this run. Previously the early continue bypassed the
                // per-file verdict and the card showed 0 files skipped forever.
                _fileRetries.RecordSuccess(item.Id.ToString());
                summary.FilesSkipped++;
                LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] SKIP {File} — no stream survived local screening (F-M69)", Path.GetFileName(mediaPath));
                progress?.Report((double)idx / Math.Max(items.Count, 1) * 100);
                continue;
            }

            // This file had at least one candidate stream/loose SRT.
            itemHadCandidates = true;

            // Phase 1 QA: run every candidate through the QA gates + content dedup.
            // QA rejects are persisted to the F-M68 reject store so they never
            // come back. QA needs the runtime for the sync gate.
            long runtimeMs = (item.RunTimeTicks ?? 0) / 10_000;
            var qaSurvivors = new List<(bool IsLoose, MediaStream? Stream, int SubPos, string LoosePath, string Lang, string Srt, bool HearingImpaired, bool Forced)>();
            foreach (var cand in phase1)
            {
                runCt.ThrowIfCancellationRequested();
                watchdog.Heartbeat();
                string? qaReject = ScreenContentQa(cand.Srt, cand.Lang, runtimeMs, out string contentHash);
                if (qaReject != null)
                {
                    // F-M286: count the rejection itself. SkippedStreams also carries
                    // non-QA reasons (duplicates, missing ids), so it cannot serve as the
                    // rejected number either.
                    summary.RejectedCandidates++;
                    summary.SkippedStreams++;
                    // F-M68: persist the QA reject — next runs skip without re-extract/re-QA... 
                    // Flush immediately — a hard JF kill between reject and run-end
                    // otherwise loses the entry (live: the 12:03 MS-mismatch vanished on
                    // the 12:05 deploy restart). Same NF-4 kill-safety the upload path has.
                    if (cand.IsLoose)
                    {
                        // A sidecar is identified by content, not by a position — one verdict row.
                        Registry.MarkAndFlush(() => Registry.MarkSidecar(
                            contentHash, mediaHash, cand.Lang, cand.HearingImpaired, cand.Forced,
                            SubtitleStatus.Rejected, reason: qaReject,
                            fileName: Path.GetFileName(cand.LoosePath), path: cand.LoosePath));
                    }
                    else
                    {
                        Registry.MarkAndFlush(() => Registry.MarkEmbed(
                            mediaHash, cand.SubPos, cand.Lang, cand.HearingImpaired,
                            SubtitleStatus.Rejected, contentHash: contentHash, reason: qaReject));
                    }

                    LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] SKIP {File} [{Lang}] — {Reason}", Path.GetFileName(cand.IsLoose ? cand.LoosePath : mediaPath), cand.Lang, ExplainReject(qaReject));

                    continue;
                }

                qaSurvivors.Add(cand);
            }

            if (qaSurvivors.Count == 0)
            {
                _fileRetries.RecordSuccess(item.Id.ToString());
                progress?.Report((double)idx / Math.Max(items.Count, 1) * 100);
                continue;
            }

            // ---- Phase 2 (F-M69): ONE batched search for the whole item ----
            // Comma-separated languages of the QA survivors — the same strict
            // server-side filters (imdb+season+episode); candidates carry their
            // Language so doubles can be matched per stream.
            // F-M184 (23.09.2026): the dry run no longer probes SubDL for duplicates —
            // it reports what WOULD be uploaded, without spending a search call.
            if (_config.DryRun)
            {
            foreach (var cand in qaSurvivors)
            {
                string neutralName = isSeries
                    ? $"{imdbId}.S{season:00}E{episode:00}.{cand.Lang}.srt"
                    : $"{imdbId}.{cand.Lang}.srt";
                LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] DRY-RUN would upload {Name}", neutralName);
            }

            _fileRetries.RecordSuccess(item.Id.ToString());
                progress?.Report((double)idx / Math.Max(items.Count, 1) * 100);
                continue;
            }

            // F-M184 (user decision 23.09.2026): the per-item batched duplicate
            // Pre-check (ONE v1 search per item, F-M69) is REMOVED. The duplicate
            // verdict is delegated to the postprocessing job, which reads mySubtitles,
            // deletes rejected entries and marks them remote-duplicate. The pre-check cost
            // one search call per item against the 400/h bucket and the daily search quota
            // just to prevent an upload that postprocessing removes anyway. Accepted
            // trade-off: a duplicate goes up once, appears as Rejected briefly and is
            // cleaned by the next postprocessing run — 1 upload instead of 1 search/item.
            // Consequence for the 429 path: the daily-limit detection that used to come
            // from the pre-check now happens on the real upload (UploadSubtitleAsync →
            // _api.ServerRateLimited, handled in the phase-3 switch), which is the
            // authoritative signal and needs no extra call.

            // ---- Phase 3: upload the survivors that are NOT already on SubDL ----
            // 13.09.2026 (user decision): run-local content dedup REMOVED — the
            // seeder owns all pre-queue checks (registry + content hash). The
            // pipeline uploads what the queue hands it; QA gates stay as the
            // upload action's own quality step.
            foreach (var cand in qaSurvivors)
            {
                runCt.ThrowIfCancellationRequested();
                watchdog.Heartbeat();

                // Check stop marker also between streams of the same item.
                if (PipelineStopSignal.IsStopped(Plugin.Instance?.DataFolderPath ?? string.Empty, upload: true, runCt))
                {
                    LogUtil.Normal(_logger, "[SubDL] upload: user stop requested mid-item — finishing current run.");
                    summary.StopRequested = true;
                    break;
                }

                string fileName = cand.IsLoose ? cand.LoosePath : mediaPath;
                string contentHash = ContentHashRegistry.ComputeHash(cand.Srt);

                // Rev.2 (user decision 16.09.2026): persistent self-echo
                // guard instead of the run-local HashSet — after the first
                // upload of a (media, lang, hi) pair the registry KNOWS it
                // (MarkUploaded runs right after the API call), so any further
                // candidate for the same pair — later in this run OR in any
                // future run — skips without a SubDL pre-check (index lag makes
                // the remote check blind to our own fresh uploads). The
                // collector already consults the same registry entry (lines
                // 576/659); this closes the in-run gap between collect and mark.
                if (Registry.IsUploaded(mediaHash, cand.Lang, cand.HearingImpaired))
                {
                    summary.SkippedStreams++;
                    summary.RejectedCandidates++; // F-M286
                    if (cand.IsLoose)
                    {
                        Registry.MarkAndFlush(() => Registry.MarkSidecar(
                            contentHash, mediaHash, cand.Lang, cand.HearingImpaired, cand.Forced,
                            SubtitleStatus.Rejected, reason: RejectReason.SelfEcho,
                            fileName: Path.GetFileName(cand.LoosePath), path: cand.LoosePath));
                    }
                    else
                    {
                        Registry.MarkAndFlush(() => Registry.MarkEmbed(
                            mediaHash, cand.SubPos, cand.Lang, cand.HearingImpaired,
                            SubtitleStatus.Rejected, contentHash: contentHash, reason: RejectReason.SelfEcho));
                    }

                    LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] SKIP {File} [{Lang}] — pair already uploaded (registry, self-echo guard)", Path.GetFileName(fileName), cand.Lang);

                    continue;
                }

                // F-M184: the release-match duplicate check that lived here used the
                // phase-2 batched search result (existingAll) — both are removed; the
                // duplicate verdict now comes from postprocessing.
                // (14.09.2026): exhausted bucket → stop + scheduler fire at
                // roll-over (+jitter); no in-run waiting.
                if (!await _limiter.TryAcquireSlotAsync(runCt).ConfigureAwait(false))
                {
                    summary.RateLimitHits++;
                    LogUtil.Normal(_logger, "[SubDL] Hourly API cap ({Cap}/h) — run stops until roll-over.", _config.UploadsPerHour);
                    ScheduleHourlyFireIfExhausted(upload: true);
                    return summary;
                }

                var result = await UploadSrtContentAsync(
                    cand.IsLoose ? cand.LoosePath : null,
                    mediaPath,
                    cand.Lang,
                    imdbId!,
                    season,
                    episode,
                    isSeries,
                    mediaHash,
                    runCt,
                    cand.Srt,
                    runtimeMs,
                    skipPrecheck: true,
                    tmdbId: tmdbIdRaw,
                    hearingImpaired: cand.HearingImpaired,
                    subPos: cand.SubPos,
                    forced: cand.Forced).ConfigureAwait(false);
                switch (result)
                {
                    case { Ok: true }:
                        summary.Uploaded++;
                        ReportOutcome(item, ItemOutcome.Done);
                        itemUploadedThisRun = true;
                        // A completed upload is the verdict itself — one row, no companion marker.
                        if (cand.IsLoose)
                        {
                            Registry.MarkAndFlush(() => Registry.MarkSidecar(
                                ContentHashRegistry.ComputeHash(cand.Srt), mediaHash, cand.Lang, cand.HearingImpaired, cand.Forced,
                                SubtitleStatus.Uploaded,
                                fileName: Path.GetFileName(cand.LoosePath), path: cand.LoosePath));
                        }
                        else
                        {
                            Registry.MarkAndFlush(() => Registry.MarkEmbed(
                                mediaHash, cand.SubPos, cand.Lang, cand.HearingImpaired,
                                SubtitleStatus.Uploaded, contentHash: ContentHashRegistry.ComputeHash(cand.Srt)));
                        }

                        LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] UPLOADED {File} [{Lang}]", Path.GetFileName(fileName), cand.Lang);

                        break;
                    case { SkippedItem: true }:
                        summary.SkippedStreams++;
                        summary.RejectedCandidates++; // F-M286
                        // A definitive skip verdict settles the position — but
                        // transient reasons (rate caps inside UploadSrtContentAsync return
                        // as failures, not skips) keep the stream due.
                        string skipReason = RejectReason.DuplicateRemote == result.Reason
                            ? RejectReason.DuplicateRemote
                            : result.Reason ?? RejectReason.CandidateRejected;
                        if (cand.IsLoose)
                        {
                            Registry.MarkAndFlush(() => Registry.MarkSidecar(
                                ContentHashRegistry.ComputeHash(cand.Srt), mediaHash, cand.Lang, cand.HearingImpaired, cand.Forced,
                                SubtitleStatus.Rejected, reason: skipReason,
                                fileName: Path.GetFileName(cand.LoosePath), path: cand.LoosePath));
                        }
                        else
                        {
                            Registry.MarkAndFlush(() => Registry.MarkEmbed(
                                mediaHash, cand.SubPos, cand.Lang, cand.HearingImpaired,
                                SubtitleStatus.Rejected, contentHash: ContentHashRegistry.ComputeHash(cand.Srt),
                                reason: skipReason));
                        }

                        LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] SKIP {File} [{Lang}] — {Reason}", Path.GetFileName(fileName), cand.Lang, ExplainReject(result.Reason));

                        break;
                    default:
                        summary.Failed++;
                        itemHadFailures = true; // F-M88c: failure blocks file-complete marker
                        _logger.LogError("[SubDL] UPLOAD FAILED {File} [{Lang}]: {Reason}", Path.GetFileName(fileName), cand.Lang, result.Reason);
                        break;
                }

                if (_api.AuthBroken)
                {
                    _logger.LogError("[SubDL] API key rejected (403) — upload stopped, check credentials.");
                    return summary;
                }

                // F-M72: upload hourly cap (500/h) — HTTP 200 + status:false, NOT a 429.
                // The candidate stays due (skip, not fail); the run stops cleanly and
                // the overload-recovery fire retries within 10–30 min when the next
                // hourly budget is available.
                if (_api.HourlyUploadCapHit)
                {
                    _logger.LogWarning("[SubDL] Upload hourly cap (500/h) — run stopped, next hourly budget.");
                    _api.ResetHourlyCapFlag();
                    ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleOverloadFire(upload: true, DateTime.UtcNow);
                    return summary;
                }

                if (_api.ServerRateLimited)
                {
                    // Arrival runs never wait out the daily limit
                    if (IsArrivalRun)
                    {
                        _logger.LogWarning("[SubDL] Daily limit (429), arrival run — pass stopped, next scheduled run.");
                        return summary;
                    }

                    if (await WaitOutDailyLimitAsync(watchdog, runCt).ConfigureAwait(false))
                    {
                        _api.ResetRateLimitFlag();
                        watchdog.Heartbeat();
                    }
                    else
                    {
                        var fireInfoUp = ScheduledTasks.SubdlSchedulerCoordinator.Instance?.GetPendingFireUtc(upload: true);
                        _logger.LogWarning("[SubDL] SubDL account daily limit (HTTP 429) — stopping upload run early. Restart: {FireLocal} local time.{Pending}",
                            fireInfoUp?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "see scheduler log", fireInfoUp.HasValue ? string.Empty : " (no pending fire — will re-fire at next scheduled run)");
                        summary.QuotaStopped++;
                        return summary;
                    }
                }

                // (16.09.2026): pause between two real uploads = the steered
                // rate-limit interval (3600/cap). Skip-continues never reach this
                // delay (they continue above); a run with only skips/bare API
                // calls spaces those by MinCallPauseSec (0.5 s) in the throttle.
                await Task.Delay(_limiter.TransferPauseMs(), runCt).ConfigureAwait(false);
            }

            // F-M60: the file was readable this run (existence check passed) — reset
            // the consecutive-failure counter (move/rename/NAS-comeback case).
            _fileRetries.RecordSuccess(item.Id.ToString());

            // F-M88c: when every candidate of this media file has been settled and none
            // failed, mark the whole file complete so future runs skip it entirely.
            if (itemHadCandidates && !itemHadFailures && mediaHash != null)
            {
                Registry.MarkAndFlush(() => Registry.MarkSubtitlesUploaded(mediaHash));
                LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] FILE COMPLETE {File} — all streams settled", Path.GetFileName(mediaPath));
            }

            // Per-file verdict for the status card ("X subtitles in Y files").
            // Counted once per item at the single exit point of the item body.
            if (itemUploadedThisRun)
            {
                summary.FilesUploaded++;
            }
            else if (itemHadCandidates)
            {
                summary.FilesSkipped++;
            }

            // item done — fine-grained progress
            progress?.Report((double)idx / Math.Max(items.Count, 1) * 100);
        }

        _api.DebugTrace -= OnApiTrace;
        _api.Log -= OnApiLog; // F-M232: detach the client's LogInfo channel per run
        _tmdb.Trace -= OnTmdbTrace;
        _tmdb.IdMismatch -= OnIdMismatch; // F-M190: detach per-run handler
        Registry.Flush();
        _idNotFound.Flush(); // Persist id-resolution failure counters (F-M66) — without this every JF restart reset the counters and id-less items (Primer/Coherence test files) waited 60 s + spent TMDB searches in every run forever
        OshashCache.Flush(); // F-M88c: kill-safe flush of the central OSHash cache
        _fileRetries.Flush();
        // (user decision 14.09.2026, REVISED 02.10.2026 — F-M286): the rule used to be "no stats
        // line, no skip aggregates" for the upload, with per-item UPLOADED lines as the only run
        // output. That decision was made when the upload's reject number was a single gate; with the
        // counter now covering every reject path it is the number that explains a run which discarded
        // its whole inventory, and a run that rejected 40 of 40 candidates looked exactly like a run
        // that found nothing (the same blindness F-M183 fixed for the download).
        // What stays: this is ONE line with the two numbers, not a per-reason aggregate — the
        // per-item SKIP lines remain the Verbose detail.
        // F-M247: a dry run uploaded nothing, so its number is labelled hypothetical —
        // "N uploaded" would be a claim the run cannot back.
        if (summary.IsDryRun)
        {
            LogUtil.Normal(_logger, "[SubDL] upload: finished — lock released. DRY RUN, would have uploaded {N} | {Rejected} rejected, {Skipped} stream(s) skipped",
                summary.Uploaded, summary.RejectedCandidates, summary.SkippedStreams);
        }
        else
        {
            LogUtil.Normal(_logger, "[SubDL] upload: finished — lock released. {N} uploaded | {Rejected} rejected, {Skipped} stream(s) skipped",
                summary.Uploaded, summary.RejectedCandidates, summary.SkippedStreams);
        }

        return summary;
        }
        catch (OperationCanceledException)
        {
            // F-M23: a run cancelled from outside (user stop marker, Jellyfin restart, task
            // cancel) used to propagate out of RunAsync, so the caller never saw a summary
            // and every subtitle already uploaded by this run stayed invisible in the
            // statistics. Return the partial summary instead — the uploads DID reach SubDL.
            // The cancellation is not swallowed: the caller still learns about it through
            // StopRequested/the stop marker and aborts the cycle.
            LogUtil.Normal(_logger, "[SubDL] upload: cancelled — keeping the partial result. {N} uploaded", summary.Uploaded);
            summary.StopRequested = true;
            return summary;
        }
        finally
        {
            PipelineRunLock.Release(_logger, "upload");
        }
    }

    private async Task<UploadResult> ProcessAndUploadAsync(
        string mediaPath,
        string? mediaHash,
        MediaStream stream,
        int subPos,
        string lang,
        string imdbId,
        int season,
        int episode,
        bool isSeries,
        long runtimeMs,
        CancellationToken ct)
    {
        // NOTE (28.09.2026): this method is UNREFERENCED — dead code kept for reference.
        // It used to carry an "F-M5/F-M6" marker, which was wrong twice over: F-M5 asks for
        // ONE ffmpeg call per FILE, and this path calls the single-stream extractor. A
        // requirement number on the code that violates it is worse than no marker — it makes
        // the requirement look satisfied in every code review. F-M5 is now implemented in the
        // item loop (see ExtractAllStreamsAsync); this is the per-stream path only.
        // F-M6 still applies here: text streams only (bitmap subs are filtered upstream).
        // (subPos = position among ALL subtitle streams — matches ffmpeg's 0:s:N numbering)
        string? srt = await ExtractSingleStreamAsync(mediaPath, subPos, ct).ConfigureAwait(false);
        if (srt == null)
        {
            return UploadResult.Failed("extraction failed");
        }

        // F-M71: forward the SDH detection (embedded stream title) — dead-code path kept consistent.
        return await UploadSrtContentAsync(null, mediaPath, lang, imdbId, season, episode, isSeries, mediaHash, ct, srt, runtimeMs, hearingImpaired: Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.IsHearingImpairedStream(stream), subPos: subPos).ConfigureAwait(false);
    }

    /// <summary>
    /// F-M69: local QA screening WITHOUT any API call — the gates F-M13–M16,
    /// the size floor and the content-hash dedup. Returns the reject reason
    /// (or null when the content passes) plus the computed content hash.
    /// </summary>
    /// <summary>
    /// Bounded wait for a free rate-limiter slot when the local hourly cap
    /// is exhausted mid-run (default 50 min, clamp 1–120).
    /// </summary>
    /// <summary>
    /// (user decision 14.09.2026): the hourly-cap wait is delegated to
    /// the scheduler — on an exhausted bucket the run stops immediately and a
    /// recovery fire is scheduled at the bucket roll-over + JobSpacingMinutes.
    /// Replaces the in-run bounded wait (old RateLimitWaitMinutes):
    /// no lock held for up to 2 h, the scheduler owns ALL waiting now.
    /// </summary>
    /// F-M26b: the roll-over fire lands at the bucket roll-over + the job spacing (5-120, default 15).
    private void ScheduleHourlyFireIfExhausted(bool upload)
    {
        var rollover = _limiter.NextRollOverUtc;
        if (rollover == null)
        {
            return; // capacity free again — no fire needed
        }
        // (25.09.2026, user decision): the offset is JobSpacingMinutes — the same
        // GUI value that already spaces run-lock re-fires and the other background
        // jobs, so the user has ONE knob for it instead of a hidden constant.
        // Previously a random 300..900 s (5-15 min), briefly a hard-coded 30 min.
        // Clamped 5..120 like every other reader of this setting (matches the GUI range).
        var offsetMinutes = Math.Clamp(_config.JobSpacingMinutes, 5, 120);
        var fireAt = rollover.Value.AddMinutes(offsetMinutes);
        ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFireAt(upload, fireAt);
    }


    private string? ScreenContentQa(string srtContent, string lang, long runtimeMs, out string contentHash)
    {
        contentHash = ContentHashRegistry.ComputeHash(srtContent);

        // B1 simple size gate (full QA is B2/F-M13–M16)
        if (srtContent.Length < 2048)
        {
            return "too-small";
        }

        // ---- B2 QA gates (F-M13–M16), each opt-in via config checkbox ----
        var stats = Qa.QaGates.ParseSrt(srtContent);

        // F-M15: language verification — detected content language vs. stream tag
        if (_config.QaVerifyLanguage)
        {
            string? detected = Qa.QaGates.DetectLanguage(srtContent);
            if (detected != null && !string.Equals(detected, lang, StringComparison.OrdinalIgnoreCase)
                && !Qa.QaGates.IsFamilyMatch(lang, detected))
            {
                return $"qa-lang-mismatch (tag {lang}, content {detected})";
            }

            // detected == null → no confident verdict → gate stays silent (fail-open)
        }

        // F-M16: minimum cue count (≥30) — beyond the hard 2 KB size floor
        if (_config.QaMinCues && (stats == null || stats.CueCount < 30))
        {
            return $"qa-too-few-cues ({stats?.CueCount ?? 0})";
        }

        // F-M13: SRT parser validation — timings parseable, monotonic starts, plausible durations
        if (_config.QaValidateSrt)
        {
            if (stats == null)
            {
                return "qa-invalid-srt (no cue timings)";
            }

            if (!stats.Monotonic)
            {
                return "qa-invalid-srt (non-monotonic timestamps)";
            }

            if (stats.MinCueMs <= 100 || stats.MaxCueMs >= 600_000)
            {
                return $"qa-invalid-srt (cue duration {stats.MinCueMs}–{stats.MaxCueMs} ms)";
            }
        }

        // F-M14: sync plausibility — cue span vs. item runtime, concentration check
        if (_config.QaCheckSync && stats != null && runtimeMs > 0)
        {
            if (stats.LastEndMs > runtimeMs + 300_000)
            {
                return $"qa-sync (span {stats.LastEndMs / 60000} min > runtime + 5 min)";
            }

            if ((stats.LastEndMs - stats.FirstStartMs) < runtimeMs / 10)
            {
                return "qa-sync (cues concentrated in <10% of runtime)";
            }
        }

        // F-M17x: server-side duplicate verdict learned from previous runs
        if (Registry.IsRemoteDuplicate(contentHash))
        {
            return "duplicate-remote";
        }

        // F-M17c: content-hash dedup
        if (Registry.IsContentKnown(contentHash))
        {
            return "duplicate-content";
        }

        return null;
    }

    /// <summary>
    /// F-M48: finds loose SRT files next to the media file. Returns (path, language, HI, forced) tuples.
    /// <para>
    /// The name is read by <see cref="Registry.SidecarNaming"/>, the one parser — this method used to
    /// carry its own copy and the copies drifted.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <returns>One entry per loose sidecar.</returns>
    public static List<(string Path, string Lang, bool HearingImpaired, bool Forced)> FindLooseSrts(string mediaPath)
        => Jellyfin.Plugin.SubdlScribe.Registry.SidecarNaming.List(mediaPath);

    /// <summary>
    /// F-M71: resolves a loose-file language token (2-letter direct, 3-letter ISO 639-2 via mapper).
    /// </summary>
    /// <summary>
    /// Translates the persisted reject codes into a human-readable log phrase.
    /// Only used for log output — the registry keeps storing the original codes so
    /// lookups stay stable across plugin versions.
    /// </summary>
    private static string ExplainReject(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return "rejected earlier";
        }

        if (reason.StartsWith("qa-lang-mismatch", StringComparison.Ordinal))
        {
            // "qa-lang-mismatch (tag SK, content IT)" → readable form
            return string.Concat("quality gate: subtitle language does not match its tag (", reason.AsSpan("qa-lang-mismatch ".Length), ")");
        }

        return reason switch
        {
            "too-small" => "quality gate: subtitle too small",
            "duplicate-remote" => "SubDL already has this exact subtitle content (different release name)",
            "duplicate-content" => "identical subtitle already known in this file",
            "duplicate-self-echo" => "uploaded earlier in this run (self-echo lock, F-M142)",
            "duplicate-release" => "SubDL already has this release",
            "und-off (F-M74)" => "untagged stream skipped (resolve-und disabled)",
            "und-too-small (2 KB floor, F-M74)" => "untagged stream: too little text to detect a language",
            "und-detection-failed (F-M74)" => "untagged stream: language could not be detected",
            "no-imdb" => "item has no IMDb id — cannot upload",
            "no-season-ep" => "item has no season/episode numbers — cannot upload",
            _ => reason
        };
    }

    private static string? ResolveLooseLangToken(string token)
    {
        if (token.Length == 2)
        {
            return token.ToUpperInvariant();
        }

        if (token.Length == 3)
        {
            return LanguageMapper.MapToSubdl(token);
        }

        return null;
    }

    /// <summary>
    /// F-M71:
    /// detects a hearing-impaired (SDH) embedded subtitle stream. Sources, in order:
    /// the JF stream Title ("SDH"/"Hearing Impaired" — set by MediaElch/ TinyMediaManager
    /// or JF metadata) and the stream's own flag as a fallback.
    /// </summary>
    /// <summary>
    /// F-M88c/d (user decision 10.09.2026): OSHash with central cache — oshash-cache.json
    /// in the plugin data dir, never a sidecar next to the media. Fingerprint (size+mtime)
    /// mismatches always recompute (file replaced/remuxed/repaired detected immediately);
    /// the OshashRefresh cadence bounds trust time. Always = plain recompute every run.
    /// </summary>
    private string? MediaHashFor(string mediaPath)
    {
        // F-M119 (user decision 14.09.2026): the uploader is LAZY-ONLY — it trusts
        // the cache until a size+mtime fingerprint mismatch (the OshashRefresh
        // cadence is enforced by the scheduler-driven SubdlOshashRefreshTask on
        // its own diced WEEKLY anchor, NOT here). Cache miss (file never seen) →
        // compute self-healing so brand-new uploads keep working.
        long size = new System.IO.FileInfo(mediaPath).Length;
        long mtimeUnix = (long)(System.IO.File.GetLastWriteTimeUtc(mediaPath) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        string? cached = OshashCache.Lookup(mediaPath, size, mtimeUnix, TimeSpan.Zero, out _);
        if (cached != null)
        {
            return cached;
        }

        string? computed = Registry.GetMediaHash(mediaPath);
        if (computed != null)
        {
            OshashCache.Store(mediaPath, computed, size, mtimeUnix);
            // F-M88c: stores are rare (only on fingerprint mismatch or refresh expiry) —
            // flush immediately so a cancelled/killed run keeps every hash it computed.
            OshashCache.Flush();
        }

        return computed;
    }


    /// <summary>
    /// Shared upload path for embedded (extracted) and loose (file) SRT content (F-M48).
    /// srtContent: the subtitle text; loosePath: originating loose file or null for embedded.
    /// </summary>
    private async Task<UploadResult> UploadSrtContentAsync(
        string? loosePath,
        string mediaPath,
        string lang,
        string imdbId,
        int season,
        int episode,
        bool isSeries,
        string? mediaHash,
        CancellationToken ct,
        string? srtContent = null,
        long runtimeMs = 0,
        bool skipPrecheck = false,
        string? tmdbId = null,
        bool hearingImpaired = false,
        int subPos = -1,
        bool forced = false)
    {
        // F-M48: loose file — read from disk
        if (srtContent == null && loosePath != null)
        {
            try
            {
                srtContent = await File.ReadAllTextAsync(loosePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return UploadResult.Failed("read failed: " + ex.Message);
            }
        }

        if (string.IsNullOrEmpty(srtContent))
        {
            return UploadResult.Failed("empty content");
        }

        // F-M185 safety net: the canonical form is established in phase 1, but this
        // method is also reachable directly (retry path, loose-path fallback above).
        // Normalizing again is idempotent and guarantees the bytes handed to SubDL
        // are exactly the bytes ComputeHash saw — the invariant the duplicate
        // detection on both sides depends on.
        srtContent = ContentHashRegistry.NormalizeSrt(srtContent);

        // F-M69: QA + content dedup run in phase 1 (ScreenContentQa) BEFORE the
        // batched search — this path is only reached with skipPrecheck=true from
        // the phase-3 loop. A direct call without the flag (retry path) keeps the
        // full inline QA as a safety net.
        string contentHashEarly = ContentHashRegistry.ComputeHash(srtContent);
        if (!skipPrecheck)
        {
            // F-M218: deliberately NOT counted here. The QA statistics counter is
            // incremented in the phase-1 precheck (the normal path, where the run
            // summary is in scope); counting this safety net as well would report
            // one rejection twice. This branch only runs for a direct/retry call.
            string? qaReject = ScreenContentQa(srtContent, lang, runtimeMs, out _);
            if (qaReject != null)
            {
                return UploadResult.Skipped(qaReject);
            }
        }


        // F-M29/F-M30: neutral filename, no user-identifying data
        string neutralName = isSeries
            ? $"{imdbId}.S{season:00}E{episode:00}.{lang}.srt"
            : $"{imdbId}.{lang}.srt";

        string releaseName = Path.GetFileNameWithoutExtension(mediaPath);

        // F-M22: dry run — no API calls
        if (_config.DryRun)
        {
            LogUtil.PerItem(_config.LogMode, _logger,"[SubDL] DRY-RUN would upload {Name} [{Lang}]", neutralName, lang);

            return UploadResult.Skipped("dry-run");
        }

        // F-M184 (23.09.2026): the legacy inline duplicate pre-check was removed with
        // the phase-2 batched search. Duplicate handling is delegated to postprocessing.
        // NOTE: skipPrecheck stays — it still guards the QA safety net above.
        // F-M71 (user decision 09.09.2026 "liegt beides vor, beides dem Uploader mitgeben"):
        // tmdb_id is the SubDL-recommended field; imdb_id rides along too. The API-side
        // precedence rule (tmdb empty → imdb only; both → both) lives in the client —
        // here we simply forward what JF metadata provided.
        var result = await _api.UploadSubtitleAsync(srtContent, neutralName, lang, releaseName, imdbId, tmdbId, isSeries, season, episode, ct, hearingImpaired).ConfigureAwait(false);
        if (result.Ok)
        {
            // F-M17y/F-M184: SubDL returns "sent for review" for every upload. The final
            // verdict (approved / rejected / duplicate-remote) is resolved by SubDL at a
            // time we neither control nor know — review latency varies (seconds to hours,
            // depending on backlog and content). There is therefore NO fixed wait here and
            // none anywhere else: the upload is considered live immediately, and the
            // postprocessing job picks the entry up on its own schedule, whenever it runs
            // and whatever the verdict says by then. A verdict that has not arrived yet is
            // simply resolved on a later run — nothing is polled and nothing times out.
            string? sdId = null;
            int? uploadId = null;
            if (result.Response != null)
            {
                var root = result.Response.RootElement;
                if (root.TryGetProperty("subtitle", out var subEl) && subEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (subEl.TryGetProperty("sd_id", out var sdEl) && sdEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        sdId = sdEl.GetString();
                    }
                    if (subEl.TryGetProperty("subtitle_id", out var sidEl) && sidEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        if (sidEl.TryGetInt32(out var sid))
                        {
                            uploadId = sid;
                        }
                    }
                }
            }
            // Metadata describing the file, and the verdict describing this subtitle, are two
            // different facts and get two rows in two areas.
            Registry.MarkAndFlush(() => Registry.PatchMediaMetadata(mediaHash, imdbId, season, episode, sdId, tmdbId));
            Registry.MarkAndFlush(() =>
            {
                if (loosePath != null)
                {
                    Registry.MarkSidecar(contentHashEarly, mediaHash ?? contentHashEarly, lang, hearingImpaired, forced,
                        SubtitleStatus.Uploaded, subdlId: uploadId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        fileName: Path.GetFileName(loosePath), path: loosePath);
                }
                else
                {
                    Registry.MarkEmbed(mediaHash, subPos, lang, hearingImpaired,
                        SubtitleStatus.Uploaded, contentHash: contentHashEarly,
                        subdlId: uploadId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            });
        }
        else if (result.Reason == "duplicate-content")
        {
            // Legacy path: if the API ever answers an immediate duplicate verdict, handle
            // it right away. The delayed cleanup job also covers this case via mySubtitles.
            Registry.MarkAndFlush(() => Registry.PatchMediaMetadata(mediaHash, imdbId, season, episode, null, tmdbId));
            Registry.MarkAndFlush(() => Registry.MarkEmbed(
                mediaHash, subPos, lang, hearingImpaired,
                SubtitleStatus.Rejected, contentHash: contentHashEarly, reason: RejectReason.DuplicateRemote));
        }
        else if (_api.ServerRateLimited)
        {
            _logger.LogWarning("[SubDL] SubDL account daily limit (HTTP 429) — upload run should stop early.");
        }

        return result;
    }

    /// <summary>
    /// F-M49 (split 08.09.2026 evening, user decision): wait out the account-wide
    /// SubDL daily API request quota once per run and continue after the
    /// server-reported reset. Uploads draw only from the API quota —
    /// UploadContinueAfterLimit governs this direction.
    /// </summary>
    /// <param name="watchdog">In-run watchdog (heartbeats keep it quiet during the wait).</param>
    /// <param name="ct">Cancellation token (user cancel + watchdog).</param>
    /// <returns>True when the wait completed and the upload can be retried.</returns>
    /// <summary>
    /// F-M24a (user decision 09.09.2026): the TMDb call trace goes through ONE gate,
    /// Verbose. F-M245 (28.09.2026): it also went through Detail, so at Debug mode every
    /// TMDb line appeared twice — Verbose is a SUBSET of Debug, not a separate tier, and
    /// "additionally at Debug" was the misconception behind the second call. Measured on
    /// the 28.09. upload run: 167 duplicated messages, all TMDb.
    /// </summary>
    private void OnTmdbTrace(string msg)
    {
        LogUtil.Trace(_config.LogMode, _logger, "{Tag} {Msg}", "[SubDL]", msg);
    }

    /// <summary>F-M24a: per-API-call trace → LogLevel.Debug (never Normal/Verbose).</summary>
    private void OnApiTrace(string msg) => LogUtil.Detail(_config.LogMode, _logger, "[SubDL] {Msg}", msg);

    /// <summary>
    /// F-M232: routes the API client's LogInfo channel into the run log. Login retries,
    /// raw 429 bodies and rate headers arrive here; Detail keeps them at the plugin's
    /// own Debug mode so Normal stays a lifecycle-only log.
    /// </summary>
    /// <param name="msg">Message from the API client.</param>
    private void OnApiLog(string msg) => LogUtil.Detail(_config.LogMode, _logger, "[SubDL] {Msg}", msg);

    private async Task<bool> WaitOutDailyLimitAsync(RunWatchdog watchdog, CancellationToken ct)
    {
        if (!_config.UploadContinueAfterLimit)
        {
            // F-M152 (rev.10, user decision 17.09.2026): tick OFF = clean stop,
            // NO recovery fire. Items stay due; the next trigger is an arrival
            // event or the weekly anchor. (Old F-M65 fire-on-tick-off removed.)
            LogUtil.Normal(_logger, "[SubDL] Daily limit reached — continue-after-limit is OFF: clean stop, no recovery fire (F-M152).");
            return false;
        }

        // F-M238 (user decision 28.09.2026): ask the counters before treating a 429 as
        // the spent allowance. Uploads draw on the same search allowance, so a trip that
        // left requests free is a rate-limit respacing (one fire after JobSpacingMinutes),
        // not a day-long stop. A spent OR unreadable allowance keeps the day-long path.
        var read = await _api.ReadQuotaAsync(forDownload: false, ct).ConfigureAwait(false);
        if (read == QuotaRead.Available)
        {
            _api.ResetRateLimitFlag();
            ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleOverloadFire(upload: true, DateTime.UtcNow);
            _logger.LogWarning(
                "[SubDL] 429 is a short-term rate limit — search allowance still has {Remaining}/{Limit} left; "
                + "run respaced by JobSpacingMinutes, items stay due (F-M238).",
                _api.Quota?.SearchRemaining, _api.Quota?.SearchLimit);
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
                "[SubDL] Daily limit reached without a server-reported reset time — waiting until {ResetUtc} UTC (next UTC midnight) instead.",
                reset.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_dailyLimitWaits >= 1)
        {
            _logger.LogWarning("[SubDL] Daily limit again — run stopped, next scheduled run.");
            return false;
        }

        _dailyLimitWaits++;

        // (user decision 09.09.2026 "Neustart nach daily limit von random 2..4h nach
        // offiziellem Reset"): do NOT resume exactly at the official reset time — every
        // API consumer hammers SubDL at 00:00 UTC (verified: the 02:05 post-reset run
        // died on a service_busy 429 five minutes after reset, panel showed 24/2000).
        // Dice a random 5-30 min offset AFTER the reset time (, 11.09.2026:
        // F-M152 (rev.10): anchor = server reset (exact, from v2 headers) or
        // fail-safe next 00:00 UTC. No local dicing - the coordinator adds the
        // 30..300 min jitter per fire (direction-independent; the coordinator's dice,
        // SubdlSchedulerCoordinator. F-M152). Not to be confused with the hourly-cap
        // roll-over fire below, which offsets by JobSpacingMinutes.
        var effectiveReset = _api.Edge429NoHeaders || !_api.RateLimitResetUtc.HasValue
            ? NextMidnightUtc()
            : _api.RateLimitResetUtc.Value;

        // F-M182 (user report 23.09.2026): this used ScheduleRecoveryFireAt, whose
        // alreadyJittered:true disabled the coordinator's dice — the fire landed exactly
        // on the reset while the log claimed +30..300 min jitter (verified live on the
        // download twin: 21.09. reset 02:00 local → fire 02:00 local, +0 min).
        // ScheduleRecoveryFire lets the coordinator dice 30..300 min per fire (crypto
        // RNG, direction-independent), keeping this installation off the 00:00 UTC herd.
        ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFire(upload: true, effectiveReset.ToUniversalTime());

        string resetLocal = TimeZoneInfo.ConvertTimeFromUtc(effectiveReset, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var fireInfo = ScheduledTasks.SubdlSchedulerCoordinator.Instance?.GetPendingFireUtc(upload: true);
        _logger.LogWarning(
            "[SubDL] Account daily limit reached - run stopped; reset ~{ResetLocal} local, recovery fire ~{Next} local (F-M152, F-M182).",
            resetLocal,
            fireInfo.HasValue
                ? TimeZoneInfo.ConvertTimeFromUtc(fireInfo.Value, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : "next scheduled run");
        return false;
    }

    /// <summary>
    /// Resolve the ffmpeg executable path used to extract embedded text subtitles.
    /// Order of preference: explicit config override, Jellyfin's configured encoder path,
    /// "ffmpeg" in PATH, then common system locations. Logs the chosen path once at Info.
    /// </summary>
    /// <summary>
    /// Resolve ffmpeg once per process and reuse it. Re-resolves only when the
    /// explicit FfmpegPath override changes.
    /// </summary>
    private static string GetResolvedFfmpegPath(PluginConfiguration config, ILogger<UploadPipeline> logger)
    {
        string configKey = config.FfmpegPath ?? string.Empty;
        if (_cachedFfmpegPath != null && _cachedFfmpegConfig == configKey)
        {
            LogUtil.Detail(logger, "[SubDL] ffmpeg path from cache: {Path}", _cachedFfmpegPath);
            return _cachedFfmpegPath;
        }

        string resolved = ResolveFfmpegPath(config, logger);
        _cachedFfmpegPath = resolved;
        _cachedFfmpegConfig = configKey;
        return resolved;
    }

    private static string ResolveFfmpegPath(
        PluginConfiguration config,
        ILogger<UploadPipeline> logger)
    {
        // 1) explicit user override
        if (!string.IsNullOrWhiteSpace(config.FfmpegPath))
        {
            var configured = config.FfmpegPath.Trim();
            if (File.Exists(configured))
            {
                LogUtil.Detail(config.LogMode, logger, "[SubDL] ffmpeg path from config override: {Path}", configured);
                return configured;
            }

            logger.LogWarning("[SubDL] configured ffmpeg override not found: {Path}, falling back to auto-detection", configured);
        }

        // 2) PATH lookup via "which ffmpeg" / "where ffmpeg"
        var candidates = new[] { "ffmpeg" };
        foreach (var candidate in candidates)
        {
            var path = FindInPath(candidate);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                LogUtil.Detail(config.LogMode, logger, "[SubDL] ffmpeg path from PATH: {Path}", path);
                return path;
            }
        }

        // 4) common static locations (Linux/Docker first, then macOS, then Windows)
        var staticCandidates = new[]
        {
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/share/jellyfin/ffmpeg/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
            "/opt/ffmpeg/ffmpeg",
            "/opt/jellyfin-ffmpeg/ffmpeg",
            "/config/ffmpeg",
            "/Applications/Jellyfin.app/Contents/MacOS/ffmpeg",
            "C:\\ProgramData\\Jellyfin\\Server\\ffmpeg.exe",
            "C:\\Program Files\\Jellyfin\\Server\\ffmpeg.exe"
        };

        foreach (var candidate in staticCandidates)
        {
            if (File.Exists(candidate))
            {
                LogUtil.Detail(logger, "[SubDL] ffmpeg path from static fallback: {Path}", candidate);
                return candidate;
            }
        }

        // 5) last resort: keep "ffmpeg" and let the OS report the failure in the extraction log
        logger.LogWarning("[SubDL] ffmpeg not found anywhere; extraction will fail until ffmpeg is installed or FfmpegPath is configured");
        return "ffmpeg";
    }

    /// <summary>Locate an executable in PATH. Uses "where" on Windows, "which" elsewhere.</summary>
    private static string? FindInPath(string name)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "where" : "which",
                Arguments = isWindows ? $"\"{name}\"" : name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                return null;
            }

            // where/which may return multiple lines; take the first non-empty one
            foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed) && File.Exists(trimmed))
                {
                    return trimmed;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>
    /// F-M5: extract EVERY text subtitle stream of one file in a SINGLE ffmpeg call.
    /// </summary>
    /// <remarks>
    /// Why one call and not one per stream: ffmpeg must open and (over CIFS/NAS)
    /// re-read the container for every invocation. On a 1 GB episode with 83
    /// subtitle streams that is 83 reads of the same gigabyte — measured on prod
    /// 28.09.2026 as the only multi-minute stalls in an otherwise 11 s-median run.
    /// One call with repeated <c>-map 0:s:N</c> + <c>-f srt</c> output pairs reads the
    /// file once and writes one file per stream, so the container is read a single
    /// time however many streams it carries.
    /// <para>
    /// The mapping uses the SUBTITLE-relative index (<c>0:s:N</c>), never the container
    /// index: mixing the two lands on video/audio streams (exit 8 / no-stream errors).
    /// Callers pass positions computed against the unfiltered subtitle list.
    /// </para>
    /// </remarks>
    /// <param name="mediaPath">Container to read.</param>
    /// <param name="subIndices">Subtitle-relative stream positions to extract.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The extracted texts plus ffmpeg's exit code. A stream that produced no
    /// text maps to null. The exit code travels with the result because it is
    /// the difference between "this stream has no text" (0) and "the pass went
    /// wrong, retry it per stream" (non-zero) — the caller needs that to decide
    /// whether a null is a verdict or a suspicion.
    /// </returns>
    private async Task<(Dictionary<int, string?> Texts, int ExitCode)> ExtractAllStreamsAsync(
        string mediaPath,
        IReadOnlyList<int> subIndices,
        CancellationToken ct)
    {
        var result = new Dictionary<int, string?>();
        if (subIndices.Count == 0)
        {
            return (result, 0);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "subdl-scribe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(mediaPath);

            var outputs = new List<(int Index, string Path)>();
            foreach (int idx in subIndices)
            {
                string outFile = Path.Combine(tempDir, $"stream{idx}.srt");
                psi.ArgumentList.Add("-map");
                // NO trailing '?'. It looks like the safe choice — "if this index does not
                // exist, skip it" — but measured 28.09.2026 it is the opposite:
                // `-map 0:s:9?` on a file with 3 streams writes the FIRST subtitle stream
                // (the English one) into the output slot meant for index 9, exits 0 and
                // says nothing. That is a subtitle of the wrong language, silently
                // duplicated, exactly what F-M71/F-M85 exist to prevent. Without '?', an
                // unknown index fails the whole call (exit 234, no output). That is the
                // honest outcome: the caller sees a non-zero exit, retries the empty
                // streams one by one and records a real failure instead of uploading a
                // mislabelled file.
                psi.ArgumentList.Add($"0:s:{idx}");
                psi.ArgumentList.Add("-c:s");
                psi.ArgumentList.Add("srt");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("srt");
                psi.ArgumentList.Add(outFile);
                outputs.Add((idx, outFile));
                result[idx] = null;
            }

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _logger.LogError("[SubDL] ffmpeg failed to start for {File}", Path.GetFileName(mediaPath));
                return (result, -1);
            }

            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            // ffmpeg keeps going after a bad stream with -y where it can, so a non-zero
            // exit does NOT mean every output is unusable. Read whatever landed and let
            // the exit code tell the caller whether the nulls are verdicts or suspicions.
            if (proc.ExitCode != 0)
            {
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[SubDL] one-pass extraction of {File} exited {Exit}: {Err}",
                    Path.GetFileName(mediaPath), proc.ExitCode,
                    stderr.Length <= 300 ? stderr : stderr[..300]);
            }

            foreach (var (idx, outFile) in outputs)
            {
                try
                {
                    if (File.Exists(outFile))
                    {
                        string text = await File.ReadAllTextAsync(outFile, ct).ConfigureAwait(false);
                        result[idx] = string.IsNullOrWhiteSpace(text) ? null : text;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("[SubDL] reading extracted stream {Idx} failed for {File}: {Msg}",
                        idx, Path.GetFileName(mediaPath), ex.Message);
                }
            }

            return (result, proc.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("[SubDL] one-pass extraction error for {File}: {Msg}",
                Path.GetFileName(mediaPath), ex.Message);
            foreach (int idx in subIndices)
            {
                result.TryAdd(idx, null);
            }

            return (result, -1);
        }
        finally
        {
            // F-M7: always clean temp files (success AND failure)
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception)
            {
                // cleanup best effort
            }
        }
    }

    private async Task<string?> ExtractSingleStreamAsync(string mediaPath, int subIndex, CancellationToken ct)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "subdl-scribe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tempSrt = Path.Combine(tempDir, $"stream{subIndex}.srt");
        try
        {
            var psi = new ProcessStartInfo
            {
                // ffmpeg 7.x: subtitle extraction needs an explicit codec (-c:s srt);
                // -map 0:s:{subIndex} selects the Nth SUBTITLE stream (JF's per-type
                // subtitle index matches ffmpeg's s:N numbering), NOT the container
                // index — mixing them maps onto video/audio (exit 8 / no-stream errors).
                FileName = _ffmpegPath,
                Arguments = $"-y -loglevel error -i \"{mediaPath}\" -map 0:s:{subIndex} -c:s srt \"{tempSrt}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _logger.LogError("[SubDL] ffmpeg failed to start for {File}", Path.GetFileName(mediaPath));
                return null;
            }

            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0 || !File.Exists(tempSrt))
            {
                _logger.LogError("[SubDL] ffmpeg extraction failed for {File} stream {Idx}: exit={Exit}, stderr: {Err}",
                    Path.GetFileName(mediaPath), subIndex, proc.ExitCode, stderr.Length <= 300 ? stderr : stderr[..300]);
                return null;
            }

            return await File.ReadAllTextAsync(tempSrt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError("[SubDL] ffmpeg extraction error for {File}: {Msg}", Path.GetFileName(mediaPath), ex.Message);
            return null;
        }
        finally
        {
            // F-M7: always clean temp files (success AND failure)
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception)
            {
                // cleanup best effort
            }
        }
    }

    private List<MediaStream> GetTextSubtitleStreams(BaseItem item)
    {
        // 10.11: streams come from IMediaSourceManager, not from the Video entity
        var streams = _mediaSourceManager.GetMediaStreams(item.Id);
        if (streams == null)
        {
            return new List<MediaStream>();
        }

        // F-M3/F-M6: text subs only (SRT/ASS/SSA); bitmap (PGS/VobSub) excluded via IsTextSubtitleStream
        return streams
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream)
            .ToList();
    }

    /// <summary>
    /// F-M9/F-M28 raw ID resolution from JF metadata ONLY (no TMDB calls — the key
    /// is spent only after the metadata wait, user decision 08.09.2026).
    /// </summary>
    private static (string? ImdbId, string? TmdbId, int Season, int Episode, bool IsSeries) ResolveIdsRaw(BaseItem item)
    {
        switch (item)
        {
            case Episode ep:
                // F-M9/F-M28: SERIES IMDB, never episode IMDB
                string? seriesImdb = null;
                string? seriesTmdb = null;
                ep.Series?.ProviderIds?.TryGetValue("Imdb", out seriesImdb);
                ep.Series?.ProviderIds?.TryGetValue("Tmdb", out seriesTmdb);
                return (seriesImdb, seriesTmdb, ep.ParentIndexNumber ?? -1, ep.IndexNumber ?? -1, true);
            case Movie movie:
                string? imdb = null;
                string? tmdb = null;
                movie.ProviderIds?.TryGetValue("Imdb", out imdb);
                movie.ProviderIds?.TryGetValue("Tmdb", out tmdb);
                return (imdb, tmdb, 0, 0, false);
            default:
                return (null, null, -1, -1, false);
        }
    }

    /// <summary>
    /// Metadata wait (on arrival, user decision 08.09.2026): max 15 min.
    /// Metadata is polled every 5 min (3 checks total); between polls a 30 s
    /// heartbeat loop keeps the in-run watchdog alive (its grace can be as low
    /// as 3× the call period). Returns the fresh item or null on timeout.
    /// </summary>

    private static (string? ImdbId, int Season, int Episode, bool IsSeries) ResolveIds(BaseItem item)
    {
        switch (item)
        {
            case Episode ep:
                // F-M9/F-M28: SERIES IMDB, never episode IMDB
                string? seriesImdb = null;
                ep.Series?.ProviderIds?.TryGetValue("Imdb", out seriesImdb);
                return (seriesImdb, ep.ParentIndexNumber ?? -1, ep.IndexNumber ?? -1, true);
            case Movie movie:
                string? imdb = null;
                movie.ProviderIds?.TryGetValue("Imdb", out imdb);
                return (imdb, 0, 0, false);
            default:
                return (null, -1, -1, false);
        }
    }

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

        // Enumerate virtual libraries via the user root folder (10.11-safe, no query types)
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

    private bool TryAcquireRateSlot() => _limiter.TryAcquireSlot();

    /// <summary>
    /// F-M152 (rev.10): the later of the API reset (v2 headers) and the
    /// download reset (v2 sources), default 00:00 UTC each. The recovery fire
    /// anchors on the LATER of the two so both quotas are guaranteed free.
    /// </summary>
    private DateTime LaterOfResets()
    {
        var apiReset = _api.RateLimitResetUtc ?? NextMidnightUtc();
        var dlReset = NextMidnightUtc(); // download quota resets at 00:00 UTC (50/day)
        return apiReset > dlReset ? apiReset : dlReset;
    }

    /// <summary>F-M152 (rev.10): next 00:00 UTC after now (fail-safe anchor).</summary>
    private DateTime NextMidnightUtc()
    {
        var now = DateTime.UtcNow;
        return now.Date.AddDays(now.TimeOfDay == TimeSpan.Zero ? 0 : 1);
    }



    /// <summary>
    /// F-M17y: cleans up rejected entries from /user/mySubtitles. Matched rejected uploads
    /// (by content hash or metadata) are counted separately; all rejected dashboard entries
    /// are deleted. Accepted entries disappear from mySubtitles on their own.
    /// </summary>
    /// <summary>
    /// F-M176: Upload postprocessing runs on its own schedule. It acquires the global
    /// PipelineRunLock (up to 10 min) and is rescheduled by JobSpacingMinutes on lock-busy.
    /// The same reschedule happens on hourly API rate-limit. Stale-lock detection is
    /// handled by PipelineRunLock (24 h / dead PID), so no separate semaphore is needed.
    /// </summary>
    internal static async Task RunUploadPostprocessingAsync()
    {
        var logger = Plugin.Instance?.LoggerFactory?.CreateLogger("SubDL-UploadPostprocessing");

        // F-M176: single global run lock. On overlap reschedule by JobSpacingMinutes
        // instead of waiting (rework 25.09.2026 — no more 10-minute poll loop).
        if (!await Pipeline.PipelineRunLock.AcquireAsync(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "postprocessing", CancellationToken.None).ConfigureAwait(false))
        {
            var spacing = JobSpacingMinutes();
            SubdlSchedulerCoordinator.Instance?.SchedulePostprocessFire(DateTime.UtcNow.AddMinutes(spacing));
            LogUtil.Normal(logger, "[SubDL] Postprocessing deferred by {Spacing} min (global lock busy).", spacing);
            return;
        }

        try
        {
            _postprocessingRunning = true;
            _postprocessingStartUtc = DateTime.UtcNow;
            await RunUploadPostprocessingInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _postprocessingRunning = false;
            _postprocessingStartUtc = DateTime.MinValue;
            Pipeline.PipelineRunLock.Release(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "postprocessing");
        }
    }

    private static int JobSpacingMinutes()
    {
        try
        {
            return Math.Clamp(Plugin.Instance?.Configuration.JobSpacingMinutes ?? 15, 5, 120);
        }
        catch
        {
            return 15;
        }
    }

    private static async Task RunUploadPostprocessingInternalAsync()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        var cfg = plugin.Configuration;
        var loggerFactory = plugin.LoggerFactory;
        if (loggerFactory == null)
        {
            return;
        }

        var registry = plugin.Registry;
        if (registry == null)
        {
            return;
        }

        var logger = loggerFactory.CreateLogger("SubDL-UploadPostprocessing");
        LogUtil.Detail(logger, "[SubDL-Postprocessing] effective rate limiter: MinCallPauseSec={Pause}, MaxCallsPerHour={Cap}.", cfg.MinCallPauseSec, cfg.UploadsPerHour);

        try
        {
            _postprocessingStartedUtc = DateTime.UtcNow;
            _postprocessingRunning = true;
            _postprocessingPendingTotal = 0;
            _postprocessingPendingRemaining = 0;
            _postprocessingResolved = 0;
            _postprocessingAccepted = 0;
            _postprocessingRejected = 0;
            _postprocessingDeleted = 0;
            _postprocessingLastResult = "running";

            int maxRuntimeMinutes = Math.Clamp(cfg.PostprocessMaxRuntimeMinutes, 1, 30);
            int maxPages = Math.Clamp(cfg.PostprocessMaxPages, 1, 20);
            using var runCts = new CancellationTokenSource(TimeSpan.FromMinutes(maxRuntimeMinutes));
            var ct = runCts.Token;

            var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var api = new SubdlApiClient(http)
            {
                Username = cfg.Username,
                Password = cfg.Password,
                ApiKey = cfg.ApiKey
            };
            api.Log += msg => LogUtil.Detail(logger, "[SubDL-Postprocessing] {Msg}", msg);

            var limiter = new GlobalRateLimiter(cfg.UploadsPerHour, cfg.MinCallPauseSec);

            try
            {
                if (!await limiter.ThrottleAsync(ct).ConfigureAwait(false))
                {
                    _postprocessingRunning = false;
                    var spacing = JobSpacingMinutes();
                    SubdlSchedulerCoordinator.Instance?.SchedulePostprocessFire(DateTime.UtcNow.AddMinutes(spacing));
                    _postprocessingLastResult = $"rate-limited: rescheduled in {spacing} min";
                    logger.LogWarning("[SubDL] Upload postprocessing: hourly API cap exhausted before login; rescheduled in {Spacing} min.", spacing);
                    return;
                }
                await api.LoginAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning("[SubDL] Upload postprocessing login failed: {Msg}", ex.Message);
                return;
            }

            List<OwnSubtitleEntry>? allMySubtitles = null;
            try
            {
                if (!await limiter.ThrottleAsync(ct).ConfigureAwait(false))
                {
                    _postprocessingRunning = false;
                    var spacing = JobSpacingMinutes();
                    SubdlSchedulerCoordinator.Instance?.SchedulePostprocessFire(DateTime.UtcNow.AddMinutes(spacing));
                    _postprocessingLastResult = $"rate-limited: rescheduled in {spacing} min";
                    logger.LogWarning("[SubDL] Upload postprocessing: hourly API cap exhausted before fetching mySubtitles; rescheduled in {Spacing} min.", spacing);
                    return;
                }
                allMySubtitles = await api.ListMySubtitlesAsync(ct, maxPages: maxPages).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("[SubDL] Upload postprocessing: fetching mySubtitles timed out.");
            }
            catch (Exception ex)
            {
                logger.LogWarning("[SubDL] Upload postprocessing could not fetch mySubtitles: {Msg}", ex.Message);
            }

            if (allMySubtitles == null)
            {
                _postprocessingRunning = false;
                _postprocessingLastResult = "failed: could not fetch mySubtitles";
                return;
            }

            var rejectedEntries = allMySubtitles
                .Where(o => string.Equals(o.Status, "rejected", StringComparison.OrdinalIgnoreCase))
                .ToList();

            LogUtil.Normal(logger, "[SubDL] Upload postprocessing: fetched {Total} mySubtitles entries, {Rejected} rejected to clean up.", allMySubtitles.Count, rejectedEntries.Count);

            int matchedDeleted = 0, unmatchedDeleted = 0, failedToDelete = 0;
            var uploadedCandidates = registry.GetUploadedEmbeds();

            foreach (var rejected in rejectedEntries)
            {
                if (ct.IsCancellationRequested)
                {
                    logger.LogWarning("[SubDL] Upload postprocessing: run time limit reached, stopping early.");
                    break;
                }

                bool matched = false;
                foreach (var up in uploadedCandidates)
                {
                    var upMedia = registry.GetMedia(up.MediaHash);
                    bool byContentHash = rejected.ContentHash != null
                        && !string.IsNullOrEmpty(up.ContentHash)
                        && string.Equals(rejected.ContentHash, up.ContentHash, StringComparison.OrdinalIgnoreCase);
                    bool byId = false;
                    if (upMedia != null)
                    {
                        byId = string.Equals(rejected.ImdbId, upMedia.ImdbId, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrEmpty(rejected.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                && !string.IsNullOrEmpty(upMedia.TmdbId)
                                && string.Equals(rejected.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture), upMedia.TmdbId, StringComparison.OrdinalIgnoreCase));
                    }
                    bool seasonOk = rejected.Season == 0 || rejected.Season == (upMedia?.Season ?? 0);
                    bool episodeOk = rejected.Episode == 0 || rejected.Episode == (upMedia?.Episode ?? 0);
                    bool byMetadata = byId && seasonOk && episodeOk
                        && string.Equals(LanguageMapper.MapToSubdlName(rejected.Language), up.Language, StringComparison.OrdinalIgnoreCase);

                    if (byContentHash || byMetadata)
                    {
                        matched = true;
                        break;
                    }
                }

                bool deleted = false;
                try
                {
                    if (!await limiter.ThrottleAsync(ct).ConfigureAwait(false))
                    {
                        var spacing = JobSpacingMinutes();
                        SubdlSchedulerCoordinator.Instance?.SchedulePostprocessFire(DateTime.UtcNow.AddMinutes(spacing));
                        _postprocessingRunning = false;
                        _postprocessingLastResult = $"rate-limited: rescheduled in {spacing} min";
                        logger.LogWarning("[SubDL] Upload postprocessing: hourly API cap exhausted before delete of upload {UploadId}; rescheduled in {Spacing} min.", rejected.UploadId, spacing);
                        return;
                    }
                    var delStart = DateTime.UtcNow;
                    using var deleteCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    deleted = await api.DeleteMySubtitleAsync(rejected.UploadId, deleteCts.Token).ConfigureAwait(false);
                    LogUtil.Detail(logger, "[SubDL-Postprocessing] delete {UploadId} took {ElapsedMs} ms (pause+network).", rejected.UploadId, (DateTime.UtcNow - delStart).TotalMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("[SubDL] Upload postprocessing: delete rejected upload {UploadId} timed out.", rejected.UploadId);
                }
                catch (Exception delEx)
                {
                    logger.LogWarning("[SubDL] Upload postprocessing: delete rejected upload {UploadId} failed: {Msg}", rejected.UploadId, delEx.Message);
                }

                if (deleted)
                {
                    _postprocessingDeleted++;
                    if (matched)
                    {
                        matchedDeleted++;
                        foreach (var up in uploadedCandidates)
                        {
                            var upMedia = registry.GetMedia(up.MediaHash);
                            bool byContentHash = rejected.ContentHash != null
                                && !string.IsNullOrEmpty(up.ContentHash)
                                && string.Equals(rejected.ContentHash, up.ContentHash, StringComparison.OrdinalIgnoreCase);
                            bool byId = false;
                            if (upMedia != null)
                            {
                                byId = string.Equals(rejected.ImdbId, upMedia.ImdbId, StringComparison.OrdinalIgnoreCase)
                                    || (!string.IsNullOrEmpty(rejected.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                        && !string.IsNullOrEmpty(upMedia.TmdbId)
                                        && string.Equals(rejected.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture), upMedia.TmdbId, StringComparison.OrdinalIgnoreCase));
                            }
                            bool seasonOk = rejected.Season == 0 || rejected.Season == (upMedia?.Season ?? 0);
                            bool episodeOk = rejected.Episode == 0 || rejected.Episode == (upMedia?.Episode ?? 0);
                            bool byMetadata = byId && seasonOk && episodeOk
                                && string.Equals(LanguageMapper.MapToSubdlName(rejected.Language), up.Language, StringComparison.OrdinalIgnoreCase);

                            if (byContentHash || byMetadata)
                            {
                                registry.MarkAndFlush(() =>
                                {
                                    registry.PatchMediaMetadata(up.MediaHash, upMedia?.ImdbId,
                                        upMedia?.Season ?? 0, upMedia?.Episode, rejected.NId, upMedia?.TmdbId);
                                    registry.MarkEmbed(up.MediaHash, up.SubPos, up.Language, up.HearingImpaired ?? false,
                                        SubtitleStatus.Rejected, contentHash: up.ContentHash,
                                        reason: RejectReason.DuplicateRemote, subdlId: rejected.NId);
                                });
                                break;
                            }
                        }

                        LogUtil.PerItem(logger, "[SubDL] Upload postprocessing: deleted matched rejected upload {UploadId} ({Name}) and marked remote-duplicate.", rejected.UploadId, rejected.Name);
                    }
                    else
                    {
                        unmatchedDeleted++;
                        LogUtil.PerItem(logger, "[SubDL] Upload postprocessing: deleted unmatched rejected upload {UploadId} ({Name}).", rejected.UploadId, rejected.Name);
                    }
                }
                else
                {
                    failedToDelete++;
                    logger.LogWarning("[SubDL] Upload postprocessing: failed to delete rejected upload {UploadId}.", rejected.UploadId);
                }
            }

            _postprocessingRunning = false;
            var elapsed = DateTime.UtcNow - _postprocessingStartedUtc;
            _postprocessingRejected = rejectedEntries.Count;
            _postprocessingResolved = matchedDeleted;
            _postprocessingLastResult = $"done in {elapsed.TotalSeconds:F0}s: {matchedDeleted} matched rejected deleted+marked, {unmatchedDeleted} unmatched rejected deleted, {failedToDelete} failed, {rejectedEntries.Count} rejected entries";
            LogUtil.Normal(logger, 
                "[SubDL] Upload postprocessing DONE in {Seconds:F0}s: {Matched} matched rejected deleted+remote-duplicate, {Unmatched} unmatched rejected deleted, {Failed} failed, {Total} rejected in fetched pages.",
                elapsed.TotalSeconds, matchedDeleted, unmatchedDeleted, failedToDelete, rejectedEntries.Count);
        }
        catch (Exception ex)
        {
            _postprocessingRunning = false;
            var elapsed = DateTime.UtcNow - _postprocessingStartedUtc;
            _postprocessingLastResult = $"failed after {elapsed.TotalSeconds:F0}s: {ex.Message}";
            logger.LogWarning("[SubDL] Upload postprocessing failed: {Msg}", ex.Message);
        }
    }

    // F-M17y: pending upload attempt model removed; metadata lives in the registry.
    // Kept as a comment for history until the next cleanup.
}

