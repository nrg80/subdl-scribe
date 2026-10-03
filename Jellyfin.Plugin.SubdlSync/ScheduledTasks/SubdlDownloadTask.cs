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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.ScheduledTasks;

/// <summary>
/// Scheduled task for the SubDL download pipeline ([D] F-M41/F-M47).
/// Refetch interval comes from RefetchInterval (unified since F-M111; default: monthly).
/// </summary>
public class SubdlDownloadTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<SubdlDownloadTask> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlDownloadTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public SubdlDownloadTask(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ILogger<SubdlDownloadTask> logger, ILoggerFactory loggerFactory, SubdlEventDispatcher? dispatcher = null, ScheduledTasks.SubdlSchedulerCoordinator? coordinator = null)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public string Name => "SubDL/TMDB — Subtitle Download";

    /// <inheritdoc />
    public string Key => "SubdlSyncDownloadTask";

    /// <inheritdoc />
    public string Description => "Searches missing subtitles for the selected libraries on SubDL and downloads them as external .srt files.";

    /// <inheritdoc />
    // F-M227: every task this plugin schedules reports ONE category, so the dashboard shows one
    // group instead of splitting the plugin's work.
    public string Category => "SubDL Scribe";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // F-M48 (user decision 08.09.2026): no default triggers — the SubdlSchedulerCoordinator
        // owns all scheduled fires (diced per-installation anchors + jitter). OnArrival =
        // watcher only; Manual = dashboard only. JF persists these defaults once at first
        // registration; existing installs must have their stored DailyTrigger cleared once
        // (the coordinator also clears stale triggers at startup).
        return []; // empty — scheduled fires come from SubdlSchedulerCoordinator only
    }

    /// <summary>
    /// F-M47: interval mapping to a minimum gap between refetch runs. Applied
    /// PER ITEM by the pipeline (never-searched items always pass immediately);
    /// Manual is handled here as a hard skip.
    /// </summary>
    public static TimeSpan IntervalToGap(UpdateInterval interval) => interval switch
    {
        UpdateInterval.TwiceDaily => TimeSpan.FromHours(12),
        UpdateInterval.Weekly => TimeSpan.FromDays(7),
        UpdateInterval.TwiceWeekly => TimeSpan.FromDays(3.5),
        UpdateInterval.Monthly => TimeSpan.FromDays(30),
        UpdateInterval.Daily => TimeSpan.FromDays(1), // (user decision 11.09.2026): Daily was gap=0 (every daily run re-searched everything); now a 1-day refetch gap — a no-candidates item is re-searched tomorrow, not on every arrival follow-up.
        _ => TimeSpan.Zero
    };

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            _logger.LogError("[SubDL-D] Plugin instance missing — cannot run.");
            return;
        }

        RecordWorkerStart(plugin);

        var config = plugin.Configuration;
        if (!config.DownloadEnabled)
        {
            LogUtil.Normal(_logger, "[SubDL-D] Download pipeline disabled — run skipped.");
            RecordWorker(plugin, Registry.WorkerRunRegistry.Outcome.Skipped, "disabled");
            return;
        }

        // F-M111 (user decision 12.09.2026): the task hands over to the event
        // dispatcher (seed → download → upload → reseed). Idempotent: a cycle
        // already running makes this a no-op.
        // Wait for the cycle to end so the task-completion line is real.
        var dispatcher = SubdlEventDispatcher.Instance;
        if (dispatcher == null)
        {
            RecordWorker(plugin, Registry.WorkerRunRegistry.Outcome.Failed, "dispatcher unavailable");
            return;
        }
        var started = dispatcher.TriggerCycle("scheduled-download");
        if (!started)
        {
            // A cycle is already running and this direction is part of it: a DEFERRAL (yellow),
            // not a switch-off. Grey stays for "disabled" and "never run".
            RecordWorker(plugin, Registry.WorkerRunRegistry.Outcome.Deferred, "cycle already active");
            return;
        }
        try
        {
            // The cycle's real end. A false return means the wait cap expired while the cycle was
            // still working — the seeder, not this task, is the honest status source then. This task
            // does no work of its own: it starts the cycle and waits.
            bool cycleFinished = await dispatcher.WaitForCycleAsync(TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
            // The seeder's and this direction's fate IN THIS CYCLE, both read from memory: the
            // stored seeder row is left untouched by a pre-check skip and can carry an older fate.
            // A quota stop or a failed cycle outranks a green "ok" (prod 30.09.2026: daily limit
            // 429 after 2 of 1144 items, and a failed upload cycle, both shown as ok before).
            var (seedOutcome, seedDetail) = dispatcher.GetSeederOutcome();
            var (dirOutcome, dirDetail) = dispatcher.GetDirectionOutcome(upload: false);
            var (outcome, detail) = Registry.WorkerRunRegistry.DescribeCycle(
                cycleFinished, seedOutcome, seedDetail, dirOutcome, dirDetail);
            RecordWorker(plugin, outcome, detail);
        }
        catch (OperationCanceledException)
        {
            // F-M131 (15.09.2026, user decision): the stop button aborts the CURRENT run — the
            // dispatcher unwinds the cycle gracefully.
            // F-M293 (user decision 03.10.2026): a JELLYFIN RESTART is not a user stop. This catch
            // used to write a stop marker and call RequestUserStop unconditionally, and that call
            // sets Cancelled for BOTH directions — so a restart at 14:18 painted the download as
            // "user stop" although its own run had finished clean at 14:08, and the upload side as
            // stopped by a button nobody pressed. The stop marker is the discriminator: the Stop
            // endpoint writes it BEFORE it signals, a shutdown never does. Without it this is a
            // restart, and the work that did land is reported as such.
            if (Pipeline.PipelineStopSignal.HasMarker(plugin.DataFolderPath, upload: false))
            {
                dispatcher.RequestUserStop("download task cancelled");
                RecordWorker(plugin, Registry.WorkerRunRegistry.Outcome.Cancelled, "user stop");
            }
            else
            {
                RecordWorker(plugin, Registry.WorkerRunRegistry.Outcome.Ok, "restart");
            }
        }
    }

    /// <summary>
    /// Stores this worker's last run in the data file. The task-completion line above is Jellyfin's own
    /// record and evaporates on restart; this row is what the configuration page reads.
    /// <para>
    /// The dry-run note is appended ONLY while a dry run is active: in that mode the numbers in the log
    /// are hypothetical, and a reader who cannot see the flag would take the run for a real one. When
    /// the flag is off the line stays clean.
    /// </para>
    /// </summary>
    private void RecordWorker(Plugin plugin, string outcome, string detail)
    {
        // The dry-run note is a FLAG, not text: the line shows light, date and outcome only.
        plugin.WorkerRuns.Finish(Registry.WorkerRunRegistry.DownloadWorkerKey, Name, outcome, detail, plugin.Configuration.DownloadDryRun);
    }

    /// <summary>Marks the start of this worker's run, so the recorded time is the run's beginning.</summary>
    private void RecordWorkerStart(Plugin plugin)
        => plugin.WorkerRuns.Start(Registry.WorkerRunRegistry.DownloadWorkerKey, Name);
}