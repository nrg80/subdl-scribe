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
using Jellyfin.Plugin.SubdlScribe.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// Records the LAST run of each worker (scheduled task) in the data file, so the configuration page
/// can answer "when did the download worker last run, and how did it end?" without reading logs.
/// <para>
/// One row per worker, keyed by the worker's own task key: a new run OVERWRITES that row, so the area
/// never grows and needs no retention policy. This is deliberately not a run history — a history would
/// need a prune path and a cap, and the question does not ask for one.
/// </para>
/// <para>
/// Why the database and not a static field: the postprocessing status used to live in
/// <c>UploadPipeline</c> statics, so every Jellyfin restart reset it to "never run" and the status
/// endpoint reported a worker as never-executed minutes after it had run (measured 30.09.2026).
/// A database row survives a restart, and a database reset wipes it — which is exactly the honest
/// scope for a progress record.
/// </para>
/// <para>
/// A worker has exactly ONE row, so the write path is an upsert and must never be a history insert.
/// Every worker is expected to appear in the GUI even before its first run, so <see cref="List"/>
/// merges the stored rows with the known workers instead of returning only what was written.
/// </para>
/// </summary>
public sealed class WorkerRunRegistry
{
    private readonly SubdlDbContext _db;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerRunRegistry"/> class.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="logger">Optional logger.</param>
    public WorkerRunRegistry(SubdlDbContext db, ILogger? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Outcome values stored in <see cref="WorkerRunEntity.Outcome"/>.</summary>
    /// <remarks>
    /// One word each, and the GUI shows that word verbatim: "ein worker, eine ampel, ein datum, ein
    /// end status" (user, 30.09.2026). Do not turn these into sentences.
    /// </remarks>
    public static class Outcome
    {
        /// <summary>The run is in progress right now.</summary>
        public const string Running = "run";

        /// <summary>The run finished its work.</summary>
        public const string Ok = "ok";

        /// <summary>The run threw and did not finish its work.</summary>
        public const string Failed = "failed";

        /// <summary>The run was cancelled (user stop or task cancellation).</summary>
        public const string Cancelled = "cancelled";

        /// <summary>The worker did not do its work: disabled, or deferred by the global run lock.</summary>
        public const string Skipped = "skipped";

        /// <summary>
        /// The worker did not get to its work: the direction was deferred, i.e. it hit the daily
        /// quota/rate limit or was pushed forward because the global run lock was busy.
        /// <para>
        /// Its own word because the GUI paints it YELLOW (user, 30.09.2026): the cycle DID finish,
        /// so "run" would be wrong, but the work did not happen, so "ok" would be a lie. Live
        /// 30.09.2026 18:51: "Daily download limit (429) — run stopped" after 2 of 1144 queued
        /// items, while the GUI showed the download worker as ok.
        /// </para>
        /// </summary>
        public const string Deferred = "deferred";

        /// <summary>The worker has not run since this record was created.</summary>
        public const string Never = "never";
    }

    /// <summary>
    /// Marks a worker as started, clearing the previous run's end time and outcome.
    /// <para>
    /// Called BEFORE the work so a worker that dies mid-run still shows the attempt; its row then
    /// reads "running" with no end, which is more truthful than the previous run's number.
    /// </para>
    /// </summary>
    /// <param name="key">Worker task key.</param>
    /// <param name="name">Worker display name.</param>
    /// <param name="detail">Optional start detail.</param>
    public void Start(string key, string name, string? detail = null)
    {
        try
        {
            var row = _db.WorkerRuns.FindById(key) ?? new WorkerRunEntity { Id = key };
            row.Name = name;
            row.Started = DateTime.UtcNow;
            row.Ended = null;
            row.Outcome = Outcome.Running;
            row.Detail = detail ?? "running";
            row.DryRun = false;
            _db.WorkerRuns.Upsert(row);
        }
        catch (Exception ex)
        {
            // Recording a run must never break the run itself.
            _logger?.LogWarning("[SubDL] recording the start of worker {Key} failed: {Msg}", key, ex.Message);
        }
    }

    /// <summary>
    /// Marks a worker's run as finished.
    /// </summary>
    /// <param name="key">Worker task key.</param>
    /// <param name="outcome">One of the <see cref="Outcome"/> values.</param>
    /// <param name="detail">Short human-readable summary.</param>
    /// <param name="dryRun">True when the run was a dry run (nothing written).</param>
    public void Finish(string key, string outcome, string detail, bool dryRun = false)
        => Finish(key, name: null, outcome, detail, dryRun);

    /// <summary>
    /// Marks a worker's run as finished, refreshing the display name when one is given.
    /// </summary>
    /// <param name="key">Worker task key.</param>
    /// <param name="name">Worker display name; null keeps the stored one.</param>
    /// <param name="outcome">One of the <see cref="Outcome"/> values.</param>
    /// <param name="detail">Short human-readable summary.</param>
    /// <param name="dryRun">True when the run was a dry run (nothing written).</param>
    public void Finish(string key, string? name, string outcome, string detail, bool dryRun = false)
    {
        try
        {
            var row = _db.WorkerRuns.FindById(key) ?? new WorkerRunEntity { Id = key };
            if (row.Started == null)
            {
                // No Start() was recorded for this run (a task that failed before it could, or a
                // worker that does not call Start) — the finish time is the honest fallback. When
                // Start DID run, its timestamp stays: it is the real beginning of the run.
                row.Started = DateTime.UtcNow;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                row.Name = name;
            }

            row.Ended = DateTime.UtcNow;
            row.Outcome = outcome;
            row.Detail = detail;
            row.DryRun = dryRun;
            _db.WorkerRuns.Upsert(row);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[SubDL] recording the result of worker {Key} failed: {Msg}", key, ex.Message);
        }
    }

    /// <summary>
    /// The seeder's worker key.
    /// <para>
    /// The seeder is NOT a Jellyfin scheduled task — it is the dispatcher's own scan step — so it
    /// cannot be enumerated from the task manager. It gets a row anyway because it is the longest
    /// and most substantive part of a cycle: a full scan of 748 items ran 56 minutes on 30.09.2026
    /// while the GUI showed the wait-only upload task as finished.
    /// </para>
    /// </summary>
    public const string SeederKey = "SubDLSeeder";

    /// <summary>
    /// The download task's worker key. A named constant rather than a literal because the dispatcher
    /// writes this row too when an ARRIVAL cycle produced it — on that path the waiting task never runs.
    /// </summary>
    public const string DownloadWorkerKey = "SubdlSyncDownloadTask";

    /// <summary>
    /// The upload task's worker key; see <see cref="DownloadWorkerKey"/> for why it is a constant.
    /// </summary>
    public const string UploadWorkerKey = "SubdlSyncUploadTask";

    /// <summary>
    /// F-M322 (operator order 08.10.2026): the auto-sync's own worker key. It gets its own row rather
    /// than being folded into the Download row, because the alignment is the part of a download run
    /// the operator watches: a run can fetch 40 files and align none, and the Download row's single
    /// word cannot tell those two appart.
    /// </summary>
    public const string AutoSyncWorkerKey = "SubdlAutoSyncTask";

    /// <summary>
    /// The status a worker reports for the cycle it took part in.
    /// <para>
    /// Colour criteria (user-approved 30.09.2026), one principle for every worker:
    /// GREEN = the work really ran and ended without an exception; YELLOW = the work did not happen
    /// but nothing is broken (quota, run-lock deferral, user stop); RED = something is broken
    /// (exception, failed cycle, unreachable core); GREY = deliberately not run (disabled), never
    /// run, or a scan that found nothing to do.
    /// </para>
    /// <para>
    /// Why the direction's and the seeder's fate are both inputs: the download/upload tasks do no
    /// work of their own, they start the cycle and wait. Reporting their own "ok" was wrong twice
    /// over — it claimed success when the 30-minute wait cap expired while the seeder was STILL
    /// scanning (measured 30.09.2026: cycle start 16:16, seeder scanned until 17:12, GUI said "ok"),
    /// and it hid a quota stop (same day 18:51: "Daily download limit (429)", 2 of 1144 items, GUI
    /// said "ok").
    /// </para>
    /// </summary>
    /// <param name="cycleFinished">False when the wait hit its cap and the cycle is still working.</param>
    /// <param name="seederOutcome">The seeder's fate in THIS cycle, or null when it reported none.</param>
    /// <param name="seederDetail">The matching seeder detail.</param>
    /// <param name="directionOutcome">
    /// This direction's fate (download/upload) in the cycle, or empty/null when it ran without a
    /// special fate.
    /// </param>
    /// <param name="directionDetail">The matching direction detail.</param>
    /// <returns>Outcome word and detail for the waiting worker's row.</returns>
    public static (string Outcome, string Detail) DescribeCycle(
        bool cycleFinished,
        string? seederOutcome = null,
        string? seederDetail = null,
        string? directionOutcome = null,
        string? directionDetail = null)
    {
        if (!cycleFinished)
        {
            // The cap expired, so this is what is TRUE right now — not a failure and not a success.
            return (Outcome.Running, "cycle still running at the wait cap");
        }

        // RED first: a broken cycle is red no matter who reports it. Ranked above everything because
        // a cycle that threw did not do its work either, and a green or yellow light would bury it.
        if (directionOutcome == Outcome.Failed)
        {
            return (Outcome.Failed, Fallback(directionDetail));
        }

        if (seederOutcome == Outcome.Failed)
        {
            return (Outcome.Failed, Fallback(seederDetail));
        }

        // YELLOW: the work did not happen, nothing is broken. The direction's fate beats the
        // seeder's, because the seeder can have done its job (scanned fine) while the direction it
        // fed was stopped by the quota — ranking it below would repaint exactly that case green.
        if (directionOutcome == Outcome.Deferred || directionOutcome == Outcome.Cancelled)
        {
            return (directionOutcome, Fallback(directionDetail));
        }

        if (seederOutcome == Outcome.Deferred || seederOutcome == Outcome.Cancelled)
        {
            return (seederOutcome, Fallback(seederDetail));
        }

        // GREY: deliberately not run, or a scan that found nothing to do.
        if (directionOutcome == Outcome.Skipped)
        {
            return (Outcome.Skipped, Fallback(directionDetail));
        }

        if (seederOutcome == Outcome.Skipped)
        {
            return (Outcome.Skipped, Fallback(seederDetail));
        }

        // GREEN: the work ran. The seeder's numbers are the most informative thing to show.
        return (Outcome.Ok, string.IsNullOrWhiteSpace(seederDetail) ? "cycle finished" : seederDetail!);
    }

    /// <summary>Detail text that is never empty, so a row cannot show a bare outcome word.</summary>
    /// <param name="detail">The candidate detail.</param>
    /// <returns>The detail, or a neutral placeholder.</returns>
    private static string Fallback(string? detail)
        => string.IsNullOrWhiteSpace(detail) ? "not recorded" : detail!;

    /// <summary>
    /// The workers this plugin runs, in display order, with the SHORT name the GUI shows.
    /// <para>
    /// A canonical list rather than a live query against Jellyfin's task manager: the GUI must show
    /// every worker even before its first run, and a worker that has never run has no row to enumerate.
    /// </para>
    /// <para>
    /// This list is the display source of truth, so a rename here takes effect for EXISTING rows too
    /// (<see cref="List"/> overwrites the stored name). Names are deliberately bare — "Download",
    /// not "SubDL/TMDB — Subtitle Download": the section header already says Workers, and the long
    /// form was noise the user asked to be rid of (30.09.2026).
    /// </para>
    /// </summary>
    public static readonly (string Key, string Name)[] KnownWorkers =
    [
        (SeederKey, "Seeder"),
        (DownloadWorkerKey, "Download"),
        (AutoSyncWorkerKey, "Autosync"),
        (UploadWorkerKey, "Upload"),
        ("SubDLPostprocessTask", "Upl. Postproc."),
        ("SubdlSyncDatabaseRefreshTask", "Database"),
        ("SubdlSyncOshashRefreshTask", "OSHash"),
    ];

    /// <summary>
    /// Every known worker with its last run, in <see cref="KnownWorkers"/> order.
    /// <para>
    /// Merged rather than read straight from the area: a worker that has never run must still appear,
    /// and it has no stored row to return. A stored row whose key is no longer a known worker is
    /// dropped — that is a worker this build does not run any more, and showing it would be a ghost.
    /// </para>
    /// </summary>
    /// <returns>One entry per known worker, never null.</returns>
    public System.Collections.Generic.List<WorkerRunEntity> List()
    {
        var result = new System.Collections.Generic.List<WorkerRunEntity>();
        foreach (var (key, name) in KnownWorkers)
        {
            WorkerRunEntity row;
            try
            {
                // A worker with no row has NEVER run. Its start must stay EMPTY: defaulting it to
                // "now" made the GUI print a fresh date next to "never run", which reads as if it had
                // just executed. Measured on the first live run of this section (30.09.2026).
                row = _db.WorkerRuns.FindById(key)
                    ?? new WorkerRunEntity { Id = key, Name = name, Started = null, Outcome = Outcome.Never };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("[SubDL] reading worker rows failed for {Key}: {Msg}", key, ex.Message);
                row = new WorkerRunEntity { Id = key, Name = name, Started = null, Outcome = Outcome.Never };
            }

            // The canonical name WINS over the stored one: it is the display name, so a rename here
            // must show up for rows written by an older build instead of waiting for each worker's
            // next run. That makes a rename a pure code change with no data migration.
            row.Name = name;

            result.Add(row);
        }

        return result;
    }

    /// <summary>
    /// Reads one worker's row, or null when it has no row yet.
    /// </summary>
    /// <param name="key">Worker task key.</param>
    /// <returns>The stored row or null.</returns>
    public WorkerRunEntity? Get(string key)
    {
        try
        {
            return _db.WorkerRuns.FindById(key);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[SubDL] reading worker {Key} failed: {Msg}", key, ex.Message);
            return null;
        }
    }
}
