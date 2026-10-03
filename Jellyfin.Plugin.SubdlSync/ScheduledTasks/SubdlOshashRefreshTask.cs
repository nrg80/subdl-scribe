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
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.ScheduledTasks;

/// <summary>
/// F-M119 (user decision 14.09.2026): OSHash refresh is a SCHEDULER job, not a
/// lazy uploader concern. The coordinator fires this task on its own diced
/// WEEKLY anchor (`RandomOshashTime`, "D HH:mm", drawn once at install, never
/// re-rolled) — every week, with no nth-week gating: the prune's `IsoWeek` gate
/// does not apply here. It walks the central oshash cache and recomputes every
/// entry whose trust window (OshashRefresh cadence) has expired or whose
/// size+mtime fingerprint changed. `OshashRefresh = Never` does NOT disable the
/// job — it narrows it to fingerprint mismatches only (zero media reads in the
/// steady state); the trust window is not a cadence. Missing files are left
/// alone (the state prune owns deletions); the uploader keeps a lazy
/// self-healing path for cache misses (a file it has never seen) but no longer
/// revalidates aged entries itself.
/// Holds the global pipeline run lock for the whole pass (registry mutation —
/// same rationale as the prune, F-M94h).
/// </summary>
public class SubdlOshashRefreshTask : IScheduledTask
{
    private readonly ILogger<SubdlOshashRefreshTask> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlOshashRefreshTask"/> class.
    /// </summary>
    public SubdlOshashRefreshTask(ILibraryManager libraryManager, ILogger<SubdlOshashRefreshTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "SubDL Scribe — OSHash Cache Refresh";

    /// <inheritdoc />
    public string Key => "SubdlSyncOshashRefreshTask";

    /// <inheritdoc />
    public string Description => "Recomputes expired or fingerprint-changed OSHash cache entries (scheduler-driven, F-M119).";

    /// <inheritdoc />
    // F-M227: every task this plugin schedules reports ONE category, so the dashboard shows one
    // group instead of splitting the plugin's work.
    public string Category => "SubDL Scribe";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    // The coordinator fires this task on its own diced WEEKLY anchor
    // (RandomOshashTime, F-M119); a fixed JF timer here would double-fire (same
    // rationale as the prune).

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            _logger.LogError("[SubDL-OshashRefresh] Plugin instance missing — cannot run.");
            return;
        }

        plugin.WorkerRuns.Start("SubdlSyncOshashRefreshTask", Name);

        var cache = plugin.SharedOshashCache;
        if (cache == null)
        {
            _logger.LogError("[SubDL-OshashRefresh] Cache unavailable — cannot run.");
            return;
        }

        var refresh = _config()?.OshashRefresh ?? OshashRefreshMode.Monthly;
        TimeSpan revalidateEvery = refresh switch
        {
            OshashRefreshMode.Never => TimeSpan.Zero,
            OshashRefreshMode.Weekly => TimeSpan.FromDays(7),
            OshashRefreshMode.Monthly => TimeSpan.FromDays(30),
            OshashRefreshMode.Yearly => TimeSpan.FromDays(365),
            _ => TimeSpan.FromDays(30)
        };

        // F-M94h rationale: this job mutates oshash-cache state that pipeline runs
        // mutate — hold the single global run lock. On overlap it reschedules by
        // JobSpacingMinutes instead of waiting (rework 25.09.2026).
        if (!await Pipeline.PipelineRunLock.AcquireAsync(_logger, "oshash-refresh", cancellationToken).ConfigureAwait(false))
        {
            var spacing = JobSpacingMinutes();
            SubdlSchedulerCoordinator.Instance?.ScheduleOshashFire(DateTime.UtcNow.AddMinutes(spacing));
            _logger.LogWarning("[SubDL-OshashRefresh] Deferred — global run lock busy; re-fire scheduled in {Spacing} min.", spacing);
            // Lock busy is a DEFERRAL, not a switch-off: the work is pending and will retry, so the
            // light is yellow. Grey stays for "disabled" and "never run".
            plugin.WorkerRuns.Finish("SubdlSyncOshashRefreshTask", Name, Registry.WorkerRunRegistry.Outcome.Deferred, "lock busy — rescheduled");
            return;
        }

        try
        {
            var snapshot = cache.Snapshot();
            int recomputed = 0, fingerprintChanged = 0, missing = 0, skipped = 0;
            long nowUnix = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

            foreach (var entry in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = entry.Path;

                long size, mtime;
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists)
                    {
                        // F-M119: unknown ≠ deleted — keep the entry untouched; the
                        // state prune (F-M94) owns deletions, not this refresh.
                        missing++;
                        continue;
                    }
                    size = fi.Length;
                    mtime = (long)(fi.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[SubDL-OshashRefresh] Stat failed for {Path} ({Message}) — entry untouched.", path, ex.Message);
                    missing++;
                    continue;
                }

                if (size != entry.Size || mtime != entry.MtimeUnix)
                {
                    fingerprintChanged++;
                }
                else if (revalidateEvery == TimeSpan.Zero
                         || entry.ValidatedUnix + (long)revalidateEvery.TotalSeconds > nowUnix)
                {
                    skipped++; // fingerprint unchanged AND trust window not expired
                    continue;
                }

                var hash = Registry.ContentHashRegistry.ComputeMediaHash(path);
                if (hash != null)
                {
                    cache.Store(path, hash, size, mtime);
                    recomputed++;
                }
                else
                {
                    skipped++; // unreadable — keep old entry, uploader will retry
                }
            }

            cache.Flush();
            LogUtil.Normal(_logger, "[SubDL-OshashRefresh] done: {Total} entries — {Changed} changed, {Missing} missing, {Skipped} skipped.", snapshot.Count, fingerprintChanged, missing, skipped);
            plugin.WorkerRuns.Finish("SubdlSyncOshashRefreshTask", Name, Registry.WorkerRunRegistry.Outcome.Ok,
                $"{snapshot.Count} entries — {fingerprintChanged} changed, {missing} missing, {skipped} skipped");
            progress?.Report(100);
        }
        catch (OperationCanceledException)
        {
            // F-M293 (user decision 03.10.2026): no stop button exists for this worker, so this is a
            // restart or a task cancel, not a user stop. See the postprocessing twin; the row reports
            // the work that landed rather than an action nobody took.
            LogUtil.Normal(_logger, "[SubDL-OshashRefresh] Cancelled — restart, not a user stop.");
            plugin.WorkerRuns.Finish("SubdlSyncOshashRefreshTask", Name, Registry.WorkerRunRegistry.Outcome.Ok, "restart");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubDL-OshashRefresh] Aborted mid-run — flushed entries stay, remainder untouched.");
            plugin.WorkerRuns.Finish("SubdlSyncOshashRefreshTask", Name, Registry.WorkerRunRegistry.Outcome.Failed, ex.Message);
        }
        finally
        {
            Pipeline.PipelineRunLock.Release(_logger, "oshash-refresh");
        }
    }

    private Jellyfin.Plugin.SubdlScribe.Configuration.PluginConfiguration? _config() => Plugin.Instance?.Configuration;

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
}
