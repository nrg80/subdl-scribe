// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
// SubDL Scribe is free software: you can redistribute it and/or modify it under
// the GNU General Public License as published by the Free Software Foundation.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY. See the GNU GPL for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.ScheduledTasks;

/// <summary>
/// THE scheduler (F-M48 08.09.2026, 12.09.2026, 14.09.2026):
/// one owner of all due times — refetch anchors (diced once per installation,
/// persisted in plugin config), recovery fires (F-M65, persisted to
/// recovery-fires.json since F-M116) and the weekly database refresh job. A 30-s tick
/// fires due jobs THROUGH the global run lock: busy lock → the job defers by
/// JobSpacingMinutes (config, default 15) and retries — nothing is dropped,
/// nothing overlaps. A missed anchor slot IS caught up (F-M156): the slot fires on the
/// first tick where it is past-due and unconsumed, and the persisted marker keeps it from
/// stacking or replaying across a restart. Past-due recovery fires fire on the next tick. Jobs are
/// queued via ITaskManager so they stay visible/manually triggerable in the
/// dashboard; the event dispatcher executes them (see its header).
/// </summary>
public sealed class SubdlSchedulerCoordinator : IDisposable
{
    /// <summary>
    /// F-M65: singleton reference — pipelines (built outside DI by
    /// PluginServiceRegistrator) reach the coordinator to schedule recovery fires
    /// without a constructor-injection detour. Set once by the DI singleton, cleared on dispose.
    /// </summary>
    public static SubdlSchedulerCoordinator? Instance { get; private set; }

    /// <summary>
    // F-M25: requirement marker added for traceability.
    /// F-M152 (rev.10): crypto-RNG uint32 for jitter dicing — every recovery
    /// fire rolls its own offset, direction-independent.
    /// </summary>
    private static uint GetCryptoUInt32()
    {
        var buf = RandomNumberGenerator.GetBytes(4);
        return BitConverter.ToUInt32(buf, 0);
    }

    private const int TickSeconds = 30;

    private readonly ITaskManager _taskManager;
    private readonly ILogger<SubdlSchedulerCoordinator> _logger;
    private readonly string _configDirectory;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    // F-M116 (user report 14.09.2026): pending recovery fires used to live in RAM
    // only — a JF restart (deploy/manual) silently discarded a scheduled quota-reset
    // fire (verified: 02:30 fire lost to the 23:05 restart; nothing fired at all).
    // The fire is now mirrored to recovery-fires.json and re-loaded on startup,
    // including past-due fires (they fire on the next tick immediately).
    private const string RecoveryFile = "recovery-fires.json";

    // (user decision 29.09.2026): the weekly anchor markers lived in RAM only. The dedup
    // test is `_lastXMarker != marker` with NO window bound, so on the anchor DAY, from the
    // anchor time until midnight, every Jellyfin restart re-claimed the slot and re-fired
    // the job. Measured on prod 28.09.2026 (OSHash anchor Sunday 07:46 local): 23 restarts,
    // 22 OSHash fires, of which exactly ONE was the real anchor fire (07:46:22, 5.2 h after
    // the previous restart) — the other 21 landed 0-1 s after a "Loaded assembly" line,
    // which is the signature of a restart, not of the scheduler. The database refresh
    // carries the same shape on its own anchor day. The markers are now mirrored to
    // anchor-fires.json and restored on startup, so a restart stops re-claiming a slot
    // that already fired today. The marker counts the CLAIM, not the outcome — a run that
    // fails after being queued stays consumed for the day, exactly as it did before.
    // (user decision 29.09.2026): ALL scheduler state is persisted, not just the two
    // weekly anchor markers. Every dedup test in this class was answered against a RAM
    // field, so a Jellyfin restart re-claimed slots that had already fired: the anchor
    // markers (22 OSHash fires against 1 real anchor fire on prod 28.09.), the refetch
    // slot (3 replayed catch-ups on the test instance) and the postprocessing slot
    // (22 fires, 20 of them 0-1 s after a restart). The whole state — four markers, the
    // catch-up flag and the three lock-busy deferrals — now lives in one file and is
    // restored on startup. Written at most once per tick, only when something changed.
    private const string SchedulerStateFile = "scheduler-state.json";
    private bool _stateDirty;

    // F-M65 (user decision 09.09.2026): one-shot recovery fires per direction. When a
    // pipeline hits the daily limit with ContinueAfterLimit OFF, it stops clean AND asks
    // the coordinator to re-fire JUST that direction after the quota reset
    // (next reset + 30..300 min jitter, F-M152 rev.11; 's 5..30 window replaced).
    // One entry per direction; consumed on fire (one-shot, no persistence — a
    // restart before the reset simply falls back to the regular anchors, acceptable).
    private DateTime? _recoveryFireDownloadUtc;
    private DateTime? _recoveryFireUploadUtc;

    // F-M156 (20.09.2026): one-time global catch-up. If any anchor window is
    // missed, queue exactly one catch-up run. It does NOT stack per missed slot.
    // Reset when a regular anchor fire runs again.
    private bool _catchUpFired;

    private string? _lastFiredMarker;
    private string? _lastPruneMarker; // Dedup for the weekly database refresh job
    private string? _lastOshashMarker; // F-M119: dedup for the weekly oshash-refresh job
    private string? _lastPostprocessMarker; // F-M176: dedup for postprocessing job
    private DateTime _postprocessDeferredUntil = DateTime.MinValue; // F-M176: lock-busy deferral
    // (user decision 14.09.2026): when the anchor fire defers on a busy
    // lock, back off JobSpacingMinutes instead of probing every 30 s — and the
    // reschedule is NOT bound to the 30-min anchor window anymore.
    private DateTime _pruneDeferredUntil = DateTime.MinValue;
    private DateTime _oshashDeferredUntil = DateTime.MinValue;

    /// <summary>
    /// (user decision 25.09.2026: stop rescheduling at the limit): every
    /// reschedule of a slot (lock-busy defer, rate-limit spacing, hourly-cap
    /// roll-over) is COUNTED. After <see cref="MaxRescheduleAttempts"/> (16) the
    /// rescheduling stops instead of silently pushing the same work forward
    /// forever. The slot's next REGULAR anchor picks the work up again — which is
    /// why giving up is a warning, not an error. Reset on a real fire, and at the
    /// daily roll-over (see <see cref="ResetDailyRescheduleBudget"/>).
    /// </summary>
    // F-M205: every reschedule is counted; after 16 the slot is refused. Log lines render {Limit},
    // so raising the constant touches no message text.
    private const int MaxRescheduleAttempts = 16;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _rescheduleCounts =
        new(StringComparer.Ordinal);

    /// <summary>
    /// UTC day the reschedule budget was last reset for. The counters are per-day, so a slot that
    /// exhausted its budget today gets a fresh one tomorrow instead of staying blocked until a
    /// restart — the counter lives in RAM, so without this the limit was effectively
    /// "until the next process start" (measured 25.09.2026: a slot sat at 11 attempts with no path
    /// back, because the refusal drops its recovery fire and a Manual install has no regular anchor
    /// that could clear it).
    /// </summary>
    private DateTime _rescheduleBudgetDayUtc = DateTime.UtcNow.Date;

    /// <summary>
    /// Counts one reschedule for a slot. Returns false when the attempt limit is
    /// exhausted — the caller must then NOT reschedule (the regular anchor owns
    /// the slot again).
    /// </summary>
    /// <param name="slot">Slot label: download, upload, postprocess, dbrefresh, oshash, refetch.</param>
    /// <param name="reason">Why the reschedule happened (lock-busy, rate-limit, …) — log only.</param>
    /// <returns>True when another reschedule is still allowed.</returns>
    /// F-M205: one counter per slot, cleared on a real fire and by the per-day budget reset (F-M208).
    public bool TryCountReschedule(string slot, string reason)
    {
        ResetDailyRescheduleBudget();
        int attempt = _rescheduleCounts.AddOrUpdate(slot, 1, (_, old) => old + 1);
        if (attempt > MaxRescheduleAttempts)
        {
            // Warn exactly once when the limit is crossed; further attempts stay on
            // debug so a repeatedly-triggered slot cannot flood the log.
            if (attempt == MaxRescheduleAttempts + 1)
            {
                _logger.LogWarning(
                    "[SubDL] {Slot} reschedule limit reached ({Limit}x, {Reason}) — NOT rescheduling again today; "
                    + "the budget resets at the next UTC day roll-over (00:00 UTC).",
                    slot, MaxRescheduleAttempts, reason);
            }
            else
            {
                LogUtil.Detail(_logger, 
                    "[SubDL] {Slot} reschedule refused again ({Attempt}x, limit {Limit}).",
                    slot, attempt, MaxRescheduleAttempts);
            }

            return false;
        }

        LogUtil.Detail(_logger, 
            "[SubDL] {Slot} reschedule {Attempt}/{Limit} ({Reason}).",
            slot, attempt, MaxRescheduleAttempts, reason);
        return true;
    }

    /// <summary>
    /// Clears the reschedule counter for a slot — called when the slot actually
    /// fired, i.e. the blockage cleared (user decision 25.09.2026).
    /// </summary>
    /// <param name="slot">Slot label, see <see cref="TryCountReschedule"/>.</param>
    public void ResetReschedule(string slot)
    {
        if (_rescheduleCounts.TryRemove(slot, out int had) && had > 0)
        {
            LogUtil.Detail(_logger, "[SubDL] {Slot} reschedule counter reset (was {Count}).", slot, had);
        }
    }

    /// <summary>
    /// Clears the reschedule budget for every slot once the UTC day rolls over — the same boundary
    /// the SubDL quota reset uses. Called from the scheduler tick (so it also happens while nothing
    /// else is running) and from <see cref="TryCountReschedule"/> (so a trigger arriving right after
    /// midnight is never counted against yesterday's budget).
    /// <para>
    /// The counters are per-day on purpose: the limit exists to stop one slot from spinning inside a
    /// single day, not to block it across days. Without this the limit was unbounded in time,
    /// because a refused slot loses its recovery fire and cannot reach the on-fire reset.
    /// </para>
    /// </summary>
    public void ResetDailyRescheduleBudget()
    {
        var today = DateTime.UtcNow.Date;
        if (today <= _rescheduleBudgetDayUtc)
        {
            return;
        }

        _rescheduleBudgetDayUtc = today;
        int slots = _rescheduleCounts.Count;
        _rescheduleCounts.Clear();
        if (slots > 0)
        {
            LogUtil.Detail(_logger, 
                "[SubDL] reschedule budget reset for the new UTC day ({Slots} slot(s) cleared) (F-M208).",
                slots);
        }
    }

    /// <summary>
    /// F-M65: schedules a ONE-SHOT recovery fire for one direction at resetUtc + jitter.
    /// Called by a pipeline that stopped on the daily limit with ContinueAfterLimit OFF.
    /// </summary>
    public void ScheduleRecoveryFire(bool upload, DateTime resetUtc)
    {
        // F-M67 (user decision 09.09.2026, "bei sowas evtl den nach 00 uhr retry
        // triggern"): overload variant — a transient service_busy stop (NOT a quota
        // problem) gets a SHORT recovery window so the retry happens soon, not
        // tomorrow night. Same dedup (one pending fire per direction, first-wins);
        // a pending quota fire is NOT overwritten by an overload fire and vice versa.
        ScheduleRecoveryFireInternal(upload, resetUtc, overload: false, alreadyJittered: false);
    }

    /// <summary>
    /// F-M176: one-shot re-fire for postprocessing (rate-limit or lock-busy). The next
    /// attempt is spaced by JobSpacingMinutes and still respects the diced anchor.
    /// </summary>
    public void SchedulePostprocessFire(DateTime fireAtUtc)
    {
        if (!TryCountReschedule("postprocess", "lock-busy/rate-limit"))
        {
            return;
        }

        if (_postprocessDeferredUntil == DateTime.MinValue || fireAtUtc < _postprocessDeferredUntil)
        {
            _postprocessDeferredUntil = fireAtUtc;
            _stateDirty = true;
            LogUtil.Normal(_logger, "[SubDL] Postprocessing re-fire scheduled at {FireAt:yyyy-MM-dd HH:mm} UTC.", fireAtUtc);
        }
    }

    public void ScheduleRefreshFire(DateTime fireAtUtc)
    {
        if (!TryCountReschedule("dbrefresh", "lock-busy"))
        {
            return;
        }

        if (_pruneDeferredUntil == DateTime.MinValue || fireAtUtc < _pruneDeferredUntil)
        {
            _pruneDeferredUntil = fireAtUtc;
            _stateDirty = true;
            LogUtil.Normal(_logger, "[SubDL] Database refresh re-fire scheduled at {FireAt:yyyy-MM-dd HH:mm} UTC.", fireAtUtc);
        }
    }

    public void ScheduleOshashFire(DateTime fireAtUtc)
    {
        if (!TryCountReschedule("oshash", "lock-busy"))
        {
            return;
        }

        if (_oshashDeferredUntil == DateTime.MinValue || fireAtUtc < _oshashDeferredUntil)
        {
            _oshashDeferredUntil = fireAtUtc;
            _stateDirty = true;
            LogUtil.Normal(_logger, "[SubDL] OSHash refresh re-fire scheduled at {FireAt:yyyy-MM-dd HH:mm} UTC.", fireAtUtc);
        }
    }

    /// <summary>
    /// Fires at the exact given time — the caller already applied its own
    /// offset (lock retry: now + JobSpacingMinutes; hourly cap: roll-over + JobSpacingMinutes).
    /// No second jitter on top. Do NOT use this overload for daily-limit quota anchors:
    /// those need the coordinator's 30..300 min dice — use ScheduleRecoveryFire instead
    /// (F-M182, 23.09.2026: alreadyJittered:true silently disabled the anti-herd jitter).
    /// </summary>
    /// <returns>True when the fire was (re)scheduled; false when the slot's
    /// reschedule limit (16) is exhausted (user decision 25.09.2026).</returns>
    public bool ScheduleRecoveryFireAt(bool upload, DateTime fireAtUtc)
    {
        // The respacing path (lock-busy / hourly cap / rate limit): every REQUEST is
        // counted, before the duplicate check, so repeated triggers for the same slot
        // cannot push the work forward forever (user decision 25.09.2026:
        // "stop rescheduling at the limit"). The duplicate check below still keeps the
        // fire from stacking.
        if (!TryCountReschedule(upload ? "upload" : "download", "lock-busy/rate-limit respacing"))
        {
            return false;
        }

        ScheduleRecoveryFireInternal(upload, fireAtUtc, overload: false, alreadyJittered: true);
        return true;
    }

    /// <summary>
    /// F-M81z-2: the pending one-shot fire time for this direction (UTC), or null
    /// when no recovery fire is scheduled — used for logging the restart date.
    /// </summary>
    public DateTime? GetPendingFireUtc(bool upload)
    {
        return upload ? _recoveryFireUploadUtc : _recoveryFireDownloadUtc;
    }

    /// <summary>
    /// F-M67: overload variant — one-shot recovery fire 10–30 min after a
    /// transient service_busy stop. The quota reset is not involved; the window
    /// is short because the SubDL server is usually back within minutes.
    /// </summary>
    /// <param name="upload">Direction: upload vs download.</param>
    /// <param name="nowUtc">Stop moment; the fire lands JobSpacingMinutes after this (F-M152 rev.10).</param>
    public void ScheduleOverloadFire(bool upload, DateTime nowUtc)
    {
        // F-M152 (rev.10): the overload/hourly-cap fire uses the configurable
        // JobSpacingMinutes gap (same spacing as run-lock re-fires) instead of
        // the old fixed 10..30 min window.
        // F-M238: clamped like every other reader of this setting (5..120, the range
        // the settings page enforces). It used Math.Max(1, …), so a hand-edited value
        // above the GUI range produced a far longer spacing here than at any sibling
        // deferral site — one setting with two meanings.
        var spacing = 15; // default; Plugin.Instance config read below when available
        try
        {
            spacing = Math.Clamp(
                Jellyfin.Plugin.SubdlScribe.Plugin.Instance?.Configuration.JobSpacingMinutes ?? 15,
                5,
                120);
        }
        catch
        {
            spacing = 15; // plugin not ready (unit tests) - default gap
        }

        // service_busy overload is a rate-limit respacing → counted like the others.
        if (!TryCountReschedule(upload ? "upload" : "download", "service_busy overload"))
        {
            return;
        }

        ScheduleRecoveryFire(upload, nowUtc.AddMinutes(spacing), overload: true);
    }

    private void ScheduleRecoveryFire(bool upload, DateTime fireAtUtc, bool overload)
    {
        ScheduleRecoveryFireInternal(upload, fireAtUtc, overload, alreadyJittered: false);
    }

    private void ScheduleRecoveryFireInternal(bool upload, DateTime fireAtUtc, bool overload, bool alreadyJittered)
    {
        // F-M65 (user decision 09.09.2026): one recovery fire per direction, not one
        // per run — if a direction already has a pending recovery fire, keep the
        // existing one (first-wins) and skip the duplicate.
        var existing = upload ? _recoveryFireUploadUtc : _recoveryFireDownloadUtc;
        if (existing.HasValue)
        {
            LogUtil.Normal(_logger, 
                "[SubDL] Recovery fire already scheduled ({Direction}): {Existing:yyyy-MM-dd HH:mm} local — skipping duplicate (F-M65).",
                upload ? "upload" : "download",
                TimeZoneInfo.ConvertTimeFromUtc(existing.Value, TimeZoneInfo.Local));
            return;
        }

        var fireAt = fireAtUtc;
        // NOTE: the overload path (service_busy) and the respacing path
        // (alreadyJittered) are COUNTED by their public entry points
        // (ScheduleOverloadFire / ScheduleRecoveryFireAt) — never here, or one
        // request would consume two reschedule slots.
        if (!overload && !alreadyJittered)
        {
            // F-M65 user correction (09.09.2026): quota fires get 120..360 min of
            // jitter ON TOP of the reset — NOT 0..30 min. Landing 02:00–06:59 UTC
            // keeps this installation away from the 00:00 collective-restart herd
            // (verified live: the 02:05 post-reset run died on a service_busy 429
            // five minutes after reset while the panel showed only 24/2000 used —
            // every API consumer restarts at exactly 00:00 UTC). Same anti-sync
            // Rationale as (which dices 120..240 for the in-run wait).
            // F-M152 (rev.11, user decision 17.09.2026): quota fires get 30..300 MINUTES
            // of jitter on top of the reset — diced fresh per fire, crypto RNG,
            // direction-independent (upload and download fires roll separately).
            // Replaces the 5..30 window. Keeps away from the 00:00 UTC
            // Collective-restart herd (rationale).
            fireAt = fireAt.AddMinutes(30 + (int)(GetCryptoUInt32() % 271)); // +30..300 min
        }

        if (upload)
        {
            _recoveryFireUploadUtc = fireAt;
        }
        else
        {
            _recoveryFireDownloadUtc = fireAt;
        }

        PersistRecoveryFires();

        if (overload)
        {
            LogUtil.Normal(_logger, 
                "[SubDL] Overload recovery fire scheduled ({Direction}): {FireAt:yyyy-MM-dd HH:mm} local — SubDL was transiently overloaded (service_busy); retrying this direction once in 10–30 min (F-M67).",
                upload ? "upload" : "download",
                TimeZoneInfo.ConvertTimeFromUtc(fireAt, TimeZoneInfo.Local));
        }
        else if (alreadyJittered)
        {
            // Lock-busy / rate-limit / hourly-cap reschedule: the caller already
            // computed the exact time (now or roll-over + JobSpacingMinutes) and no
            // jitter applies to it. The old text claimed a quota reset with a
            // 30..300 min jitter that never happened here — misleading in the log.
            LogUtil.Normal(_logger, 
                // F-M206: a respacing fire carries no jitter and must not claim a quota reset.
                "[SubDL] Recovery fire scheduled ({Direction}): {FireAt:yyyy-MM-dd HH:mm} local — respaced by JobSpacingMinutes after lock-busy or rate-limit (no jitter).",
                upload ? "upload" : "download",
                TimeZoneInfo.ConvertTimeFromUtc(fireAt, TimeZoneInfo.Local));
        }
        else
        {
            LogUtil.Normal(_logger, 
                "[SubDL] Recovery fire scheduled ({Direction}): quota reset {Reset:yyyy-MM-dd HH:mm} local + 30..300 min jitter → fire ~{FireAt:yyyy-MM-dd HH:mm} local — re-firing this direction once after the reset (F-M65, F-M152).",
                upload ? "upload" : "download",
                TimeZoneInfo.ConvertTimeFromUtc(fireAtUtc, TimeZoneInfo.Local),
                TimeZoneInfo.ConvertTimeFromUtc(fireAt, TimeZoneInfo.Local));
        }
    }

    public SubdlSchedulerCoordinator(ITaskManager taskManager, IApplicationPaths applicationPaths, ILogger<SubdlSchedulerCoordinator> logger)
    {
        Instance = this;
        _taskManager = taskManager;
        _logger = logger;
        _configDirectory = applicationPaths.ConfigurationDirectoryPath;
        LoadRecoveryFires();
        LoadSchedulerState();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        LogUtil.Normal(_logger, 
            "[SubDL] Refetch scheduler started.");
    }

    /// <summary>
    /// F-M48: fixed-time triggers are forbidden — all scheduled fires come from the
    /// coordinator's random anchors. JF persists dashboard trigger edits as
    /// {ConfigurationDirectory}/scheduledtasks/{TaskId}.js; installs that ran an
    /// older version (or where the user edited triggers) still carry the old
    /// DailyTrigger there. Remove the file and reload — LoadTriggers then falls
    /// back to our empty GetDefaultTriggers().
    /// </summary>
    private void ClearStaleTriggers()
    {
        try
        {
            foreach (var worker in _taskManager.ScheduledTasks)
            {
                if (worker.ScheduledTask is not (SubdlDownloadTask or SubdlUploadTask or SubdlPostprocessTask))
                {
                    continue;
                }

                var file = Path.Combine(_configDirectory, "scheduledtasks", worker.Id + ".js");
                bool hadFile = File.Exists(file);
                if (hadFile)
                {
                    File.Delete(file);
                }

                if (hadFile || worker.Triggers.Count > 0)
                {
                    LogUtil.Normal(_logger, 
                        "[SubDL] Scheduler: clearing {Count} stale trigger(s) from '{Task}' (fixed-time fires replaced by random anchors).",
                        worker.Triggers.Count, worker.Name);
                    worker.ReloadTriggerEvents(); // reloads from file → now empty defaults
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL] Scheduler: stale-trigger cleanup failed: {Msg}", ex.Message);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ResetDailyRescheduleBudget(); // midnight may have passed while idle
                    CheckAndFire();
                    if (_stateDirty)
                    {
                        PersistSchedulerState();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("[SubDL] Scheduler tick failed: {Msg}", ex.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(TickSeconds), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// True when the given plugin task type is registered with the TaskManager.
    /// During early startup Jellyfin has not registered the plugin's tasks yet,
    /// and QueueScheduledTask at that moment logs "Unable to find scheduled task
    /// of type ... in QueueScheduledTask" and DROPS the fire silently. Every fire
    /// path must therefore test this BEFORE it consumes its marker (or resets its
    /// reschedule counter), so an unregistered task leaves the slot claimable and
    /// the next 30-s tick retries it. Same guard class as the F-M65 recovery
    /// fires, which had it for download/upload only (F-M212).
    /// </summary>
    private bool IsTaskRegistered<T>()
        where T : IScheduledTask =>
        _taskManager.ScheduledTasks.Any(t => t.ScheduledTask is T);

    private void CheckAndFire()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        // Self-healing trigger cleanup (F-M48): runs every tick, idempotent, cheap.
        // Fixed-time triggers are forbidden — scheduling belongs to this coordinator.
        ClearStaleTriggers();

        // F-M65: one-shot recovery fires (daily-limit stop with ContinueAfterLimit OFF).
        // Each direction fires INDEPENDENTLY — only the direction that hit the limit
        // gets re-fired; the other one is untouched. Consumed on fire.
        var nowUtc = DateTime.UtcNow;
        if (_recoveryFireDownloadUtc.HasValue && nowUtc >= _recoveryFireDownloadUtc.Value)
        {
            // (14.09.2026): during early startup the TaskManager may not
            // have registered the plugin tasks yet — QueueScheduledTask then just
            // logs 'Unable to find scheduled task' and drops the fire. Guard:
            // only fire when the task is registered; otherwise keep the fire
            // pending and retry on the next tick (30 s).
            bool registered = IsTaskRegistered<SubdlDownloadTask>();
            if (!registered)
            {
                return; // fire stays pending, file stays as backup
            }

            // Lock busy → defer by JobSpacingMinutes instead of dropping.
            if (!Pipeline.PipelineRunLock.IsFree(_logger))
            {
                DeferFire(upload: false);
                return;
            }

            _recoveryFireDownloadUtc = null;
            ResetReschedule("download"); // really fired — clear the 16x counter
            PersistRecoveryFires();
            LogUtil.Normal(_logger, "[SubDL] Recovery fire: download — quota window has reset; queueing download run (F-M65).");
            _taskManager.QueueScheduledTask<SubdlDownloadTask>(new TaskOptions());
            return;
        }

        if (_recoveryFireUploadUtc.HasValue && nowUtc >= _recoveryFireUploadUtc.Value)
        {
            // Same guard as download.
            bool registeredUp = IsTaskRegistered<SubdlUploadTask>();
            if (!registeredUp)
            {
                return; // fire stays pending
            }

            // Lock busy → defer.
            if (!Pipeline.PipelineRunLock.IsFree(_logger))
            {
                DeferFire(upload: true);
                return;
            }

            _recoveryFireUploadUtc = null;
            ResetReschedule("upload"); // really fired — clear the 16x counter
            PersistRecoveryFires();
            LogUtil.Normal(_logger, "[SubDL] Recovery fire: upload — quota window has reset; queueing upload run (F-M65).");
            _taskManager.QueueScheduledTask<SubdlUploadTask>(new TaskOptions());
            return;
        }

        var config = plugin.Configuration;

        // Weekly database refresh job — fired by the scheduler on the diced weekly
        // anchor (same anchor the refetch uses), NOT by a JF trigger anymore.
        // Runs even when both pipelines are disabled; respects the global lock
        // (defer like every job) and dedups via marker (one run per anchor day).
        // F-M111 (user decision 12.09.2026): ONE unified refetch interval for both
        // directions (GUI General). No per-direction scheduling anymore — one
        // rhythm drives the cycle tick (the dispatcher sequences seed→down→up).
        //
        // The "no scheduled refetch fires" decision for OnArrival/Manual is applied
        // AFTER the three independent anchor jobs below, NOT here. Those jobs — refresh,
        // OSHash refresh and upload postprocessing — each have their OWN setting and
        // their own diced anchor, and are documented as independent of the pipeline.
        // Gating them on RefetchInterval silenced all three: with RefetchInterval=Manual
        // the tick returned before reaching them, so not one anchor-driven run ever
        // happened (measured 26.09.2026 over every log: 0 "Database refresh fire at", 0
        // "OSHash-refresh fire at", 0 "Postprocessing fire at" — the only runs were
        // manual API triggers and one lock-recovery start).
        var now = DateTime.Now;

        // The database refresh runs regardless of pipeline switches (rationale).
        // (14.09.2026): the refresh block no longer aborts the tick —
        // both the deferred case (lock busy) and the fired case fall through
        // to the run fires below. QueueScheduledTask only QUEUES; running the
        // refresh beside a queued run fire is fine (the run lock serializes them;
        // the refresh task itself re-checks and waits/bails via F-M94h).
        {
            // Own diced anchor — no longer the refetch weekly anchor.
            int refreshWeekMod = config.PruneMode switch
            {
                Configuration.PruneMode.Weekly => 1,
                Configuration.PruneMode.Monthly => 4,
                Configuration.PruneMode.Yearly => 52,
                _ => 0 // Never
            };
            var refreshAnchor = IsWeeklyDaySpec(config.RandomPruneTime, now)
                ? ParseWeeklySpec(config.RandomPruneTime, now)
                : (DateTime?)null;
            var refreshMarker = refreshAnchor?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var refreshCadenceDue = refreshWeekMod > 0 && IsoWeek(now) % refreshWeekMod == 0;
            var refreshDue = refreshAnchor.HasValue && now >= refreshAnchor.Value && refreshCadenceDue
                && (_lastPruneMarker != refreshMarker || now < _pruneDeferredUntil);
            if (refreshDue && now >= _pruneDeferredUntil)
            {
                if (!IsTaskRegistered<SubdlDatabaseRefreshTask>())
                {
                    return; // F-M212: task not registered yet — marker not consumed, retried next tick
                }

                if (Pipeline.PipelineRunLock.IsFree(_logger))
                {
                    _lastPruneMarker = refreshMarker;
                    _stateDirty = true;
                    _pruneDeferredUntil = DateTime.MinValue;
                    _stateDirty = true;
                    ResetReschedule("refresh"); // fired — clear the 16x counter
                    LogUtil.Normal(_logger, "[SubDL] Database refresh fire at {Anchor:yyyy-MM-dd HH:mm} local ({Mode}).", (DateTime)refreshAnchor, config.PruneMode);
                    _taskManager.QueueScheduledTask<SubdlDatabaseRefreshTask>(new TaskOptions());
                }
                else
                {
                    // Reschedule instead of window-bound retry — next
                    // attempt JobSpacingMinutes out, regardless of the anchor window.
                    if (TryCountReschedule("refresh", "lock-busy anchor"))
                    {
                        _pruneDeferredUntil = now.AddMinutes(Math.Max(1, config.JobSpacingMinutes));
                        _stateDirty = true;
                        LogUtil.Normal(_logger, "[SubDL] Database refresh deferred to {Until:HH:mm} local (lock busy).", _pruneDeferredUntil);
                    }
                }
            }
        }

        // (user decision 14.09.2026): OSHash cache refresh on the diced
        // WEEKLY anchor (same anchor as the refresh — the cadence menu already
        // offers Weekly; a monthly reshash made no sense against it). Same
        // reschedule mechanic as the refresh: busy lock → back off
        // JobSpacingMinutes, NOT bound to the 30-min window.
        {
            // Own diced anchor.
            var osAnchor = IsWeeklyDaySpec(config.RandomOshashTime, now)
                ? ParseWeeklySpec(config.RandomOshashTime, now)
                : (DateTime?)null;
            var osMarker = osAnchor?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var osDue = osAnchor.HasValue && now >= osAnchor.Value
                && (_lastOshashMarker != osMarker || now < _oshashDeferredUntil);
            if (osDue && now >= _oshashDeferredUntil)
            {
                if (!IsTaskRegistered<SubdlOshashRefreshTask>())
                {
                    return; // F-M212: task not registered yet — marker not consumed, retried next tick
                }

                if (Pipeline.PipelineRunLock.IsFree(_logger))
                {
                    _lastOshashMarker = osMarker;
                    _stateDirty = true;
                    _oshashDeferredUntil = DateTime.MinValue;
                    _stateDirty = true;
                    ResetReschedule("oshash"); // fired — clear the 16x counter
                    LogUtil.Normal(_logger, "[SubDL] OSHash-refresh fire at {Anchor:yyyy-MM-dd HH:mm} local.", (DateTime)osAnchor);
                    _taskManager.QueueScheduledTask<SubdlOshashRefreshTask>(new TaskOptions());
                }
                else
                {
                    if (TryCountReschedule("oshash", "lock-busy anchor"))
                    {
                        _oshashDeferredUntil = now.AddMinutes(Math.Max(1, config.JobSpacingMinutes));
                        _stateDirty = true;
                        LogUtil.Normal(_logger, "[SubDL] OSHash-refresh deferred to {Until:HH:mm} local (lock busy).", _oshashDeferredUntil);
                    }
                }
            }
        }

        // F-M176: upload postprocessing schedule — independent from upload pipeline.
        // Respects the global run lock (defers by JobSpacingMinutes). Off/Never disables it;
        // Daily/Weekly/Monthly resolve their own anchor so each interval actually works.
        {
            var ppInterval = config.UploadPostprocessInterval;
            if (ppInterval is not (UpdateInterval.Never or UpdateInterval.Manual or UpdateInterval.OnArrival))
            {
                var ppAnchor = ppInterval switch
                {
                    UpdateInterval.Daily => ParseHhMmFromWeeklySpec(config.RandomPostprocessTime, now),
                    UpdateInterval.Weekly => IsWeeklyDaySpec(config.RandomPostprocessTime, now)
                        ? ParseWeeklySpec(config.RandomPostprocessTime, now)
                        : null,
                    UpdateInterval.Monthly => now.Day == config.RandomMonthlyDay
                        ? ParseHhMm(config.RandomMonthlyTime, now)
                        : null,
                    _ => null
                };

                if (ppAnchor.HasValue)
                {
                    var ppMarker = ppAnchor?.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
                    bool ppDue = now >= ppAnchor.Value
                        && (_lastPostprocessMarker != ppMarker || now < _postprocessDeferredUntil);

                    if (ppDue && now >= _postprocessDeferredUntil)
                    {
                        if (!IsTaskRegistered<SubdlPostprocessTask>())
                        {
                            return; // F-M212: task not registered yet — marker not consumed, retried next tick
                        }

                        _lastPostprocessMarker = ppMarker;
                        _stateDirty = true;
                        _postprocessDeferredUntil = DateTime.MinValue;
                    _stateDirty = true;
                        ResetReschedule("postprocess"); // fired — clear the 16x counter
                        LogUtil.Normal(_logger, "[SubDL] Postprocessing fire at {Anchor:yyyy-MM-dd HH:mm} local ({Interval}).", (DateTime)ppAnchor, ppInterval);
                        _taskManager.QueueScheduledTask<SubdlPostprocessTask>(new TaskOptions());
                    }
                }
            }
        }

        // ── Refetch rhythm (the unified pipeline cycle) ──────────────────────
        // Only here does RefetchInterval decide anything: OnArrival and Manual mean
        // "no automatic pipeline cycles". This gate deliberately sits AFTER the refresh,
        // OSHash and postprocessing, which run on their own settings/anchors.
        var effective = config.RefetchInterval;
        if (effective is UpdateInterval.OnArrival || effective == UpdateInterval.Manual)
        {
            return; // no scheduled refetch fires (arrival-only or manual-only)
        }

        foreach (var fire in EffectiveFiresToday(effective, config, now))
        {
            if (now < fire.Effective)
            {
                continue;
            }

            if (fire.Marker == _lastFiredMarker)
            {
                continue;
            }

            // (user decision 29.09.2026): NO fire window. The 60-s window
            // (2 * Tick) was a rollback against F-M156: it made a missed anchor wait for
            // the NEXT regular window instead of being caught up, so the one-shot catch-up
            // below had to exist as a workaround. A due slot now fires on the first tick
            // where it is past-due and unconsumed — the same logic the database refresh,
            // the OSHash refresh and postprocessing already have (they carry no window
            // either). The marker is what prevents stacking: it is persisted, so a restart
            // does not replay the slot, and a missed window is picked up on the next tick.

            // A due anchor fire respects the global lock — if a run is
            // active, defer (retry at now + JobSpacing) instead of dropping the
            // window. The marker is NOT set on defer, so the slot stays claimable.
            if (!Pipeline.PipelineRunLock.IsFree(_logger))
            {
                if (TryCountReschedule("refetch", "lock-busy anchor"))
                {
                    var spacedRef = now.AddMinutes(JobSpacing(config));
                    LogUtil.Normal(_logger, 
                        "[SubDL] Refetch fire ({Anchor}) deferred — a run holds the lock; retrying at {Retry:yyyy-MM-dd HH:mm} local (F-M117).",
                        fire.AnchorDesc, spacedRef);
                }

                return; // marker NOT set → slot retries after the spacing window
            }

            // F-M212: during early startup the tasks may not be registered yet, and
            // QueueScheduledTask then drops the fire silently. Test this BEFORE the
            // marker is consumed so the slot stays claimable for the next tick.
            // Checked for both directions, since either may be missing.
            if ((config.DownloadEnabled && !IsTaskRegistered<SubdlDownloadTask>())
                || (config.UploadEnabled && !IsTaskRegistered<SubdlUploadTask>()))
            {
                return; // marker NOT set → fires on a later tick
            }

            _lastFiredMarker = fire.Marker;
            _catchUpFired = false; // regular fire resets global catch-up state
            _stateDirty = true;
            ResetReschedule("refetch"); // fired — clear the 16x counter
            ResetReschedule("download"); // regular anchor owns both directions again
            ResetReschedule("upload");
            LogUtil.Normal(_logger, 
                "[SubDL] Refetch fire: rhythm={Rhythm} anchor {Anchor} — queueing cycle (unified refetch interval, F-M111).",
                effective, fire.AnchorDesc);

            // F-M111 (user decision 12.09.2026): the unified refetch rhythm fires
            // the cycle via the dispatcher (seed → down → up). The per-direction
            // task queueing stays as the delivery mechanism — both tasks forward
            // to TriggerCycle, which is idempotent (one cycle at a time).
            bool downloadJoins = config.DownloadEnabled;
            bool uploadJoins = config.UploadEnabled;

            if (downloadJoins)
            {
                _taskManager.QueueScheduledTask<SubdlDownloadTask>(new TaskOptions());
            }

            if (uploadJoins)
            {
                _taskManager.QueueScheduledTask<SubdlUploadTask>(new TaskOptions());
            }

            return; // one fire per tick
        }

        // F-M156: one-shot global catch-up — REDUNDANT since the fire window was removed
        // (F-M248, 29.09.2026). The regular fire above already claims every past-due,
        // unconsumed slot, and its test (`now >= anchor`) is strictly weaker than this
        // block's (`now > anchor + 60 s`), so the regular fire always wins and this code
        // cannot be reached. It is kept rather than deleted because F-M156 is still a
        // standing requirement and the block is the exact shape to restore if a window is
        // ever reintroduced — but no path leads here today. Do not read a run without a
        // "Refetch catch-up" line as a defect.
        // If any of today's fire slots has
        // already passed and no regular fire has run for the current slot, queue
        // exactly one catch-up run — regardless of how many slots were missed.
        var anyMissed = EffectiveFiresToday(effective, config, now)
            .Any(f => now > f.Effective.AddSeconds(2 * TickSeconds) && _lastFiredMarker != f.Marker);
        if (anyMissed && !_catchUpFired)
        {
            if (!Pipeline.PipelineRunLock.IsFree(_logger))
            {
                return; // still within catch-up window; retry on next tick
            }

            // F-M212: catch-up queueing needs the same registration guard as the
            // regular fire — an early-startup miss would otherwise set _catchUpFired
            // and drop the cycle for the whole day.
            if ((config.DownloadEnabled && !IsTaskRegistered<SubdlDownloadTask>())
                || (config.UploadEnabled && !IsTaskRegistered<SubdlUploadTask>()))
            {
                return; // retry on a later tick
            }

            _catchUpFired = true;
            _stateDirty = true;
            LogUtil.Normal(_logger, 
                "[SubDL] Refetch catch-up: missed anchor window — queueing one cycle (F-M156).");

            bool downloadJoins = config.DownloadEnabled;
            bool uploadJoins = config.UploadEnabled;

            if (downloadJoins)
            {
                _taskManager.QueueScheduledTask<SubdlDownloadTask>(new TaskOptions());
            }

            if (uploadJoins)
            {
                _taskManager.QueueScheduledTask<SubdlUploadTask>(new TaskOptions());
            }
        }
    }

    /// <summary>Spacing window for deferred jobs (config, clamped 5..120).</summary>
    private static int JobSpacing(PluginConfiguration config) =>
        Math.Clamp(config.JobSpacingMinutes is int m && m > 0 ? m : 15, 5, 120);

    /// <summary>Pushes a pending recovery fire back by JobSpacing minutes
    /// (lock was busy when it came due) and mirrors that to the fire file.
    /// Counted against the reschedule limit (user decision 25.09.2026).</summary>
    private void DeferFire(bool upload)
    {
        if (!TryCountReschedule(upload ? "upload" : "download", "lock-busy at due time"))
        {
            // Limit reached: stop pushing this fire forward. Drop it so the
            // direction falls back to its regular anchor instead of spinning.
            if (upload)
            {
                _recoveryFireUploadUtc = null;
            }
            else
            {
                _recoveryFireDownloadUtc = null;
            }

            PersistRecoveryFires();
            return;
        }

        var spaced = DateTime.UtcNow.AddMinutes(JobSpacing(Plugin.Instance!.Configuration));
        if (upload)
        {
            _recoveryFireUploadUtc = spaced;
        }
        else
        {
            _recoveryFireDownloadUtc = spaced;
        }

        PersistRecoveryFires();
        _logger.LogWarning(
            "[SubDL] Recovery fire ({Direction}) deferred — a run holds the lock; retrying at {Retry:yyyy-MM-dd HH:mm} UTC (F-M117).",
            upload ? "upload" : "download",
            spaced);
    }

    /// <summary>One fire slot: effective moment (the diced anchor), dedup marker, description.</summary>
    private sealed record FireSlot(DateTime Effective, string Marker, string AnchorDesc);

    /// <summary>All effective fire moments for today under the current interval.</summary>
    private List<FireSlot> EffectiveFiresToday(UpdateInterval effective, PluginConfiguration config, DateTime now)
    {
        var result = new List<FireSlot>();
        switch (effective)
        {
            case UpdateInterval.Daily:
                AddSlot(result, ParseHhMm(config.RandomDailyTime, now), "daily " + config.RandomDailyTime);
                break;

            case UpdateInterval.TwiceDaily:
                AddSlot(result, ParseHhMm(config.RandomDailyTime, now), "twice-daily " + config.RandomDailyTime);
                AddSlot(result, ParseHhMm(config.RandomDailyTime, now)?.AddHours(12), "twice-daily+12h " + config.RandomDailyTime);
                break;

            case UpdateInterval.Weekly:
                if (IsWeeklyDay(config, now))
                {
                    AddSlot(result, ParseWeekly(config, now), "weekly " + config.RandomWeeklyTime);
                }

                break;

            case UpdateInterval.TwiceWeekly:
                // anchor day + anchor+3d12h ≈ every 3.5 days (F-M47 mapping)
                if (IsWeeklyDay(config, now))
                {
                    AddSlot(result, ParseWeekly(config, now), "twice-weekly " + config.RandomWeeklyTime);
                }

                if (IsWeeklyDay(config, now.AddDays(-3)) && ParseWeekly(config, now.AddDays(-3)).HasValue)
                {
                    var second = ParseWeekly(config, now.AddDays(-3))!.Value.AddHours(12);
                    AddSlot(result, second, "twice-weekly+3.5d " + config.RandomWeeklyTime);
                }

                break;

            case UpdateInterval.Monthly:
                if (now.Day == config.RandomMonthlyDay)
                {
                    AddSlot(result, ParseHhMm(config.RandomMonthlyTime, now), $"monthly day {config.RandomMonthlyDay} {config.RandomMonthlyTime}");
                }

                break;
        }

        return result;
    }

    // (user decision 12.09.2026): no per-fire jitter anymore — runs are
    // processed sequentially (one queue), so simultaneous anchors across
    // installations are harmless.
    private void AddSlot(List<FireSlot> list, DateTime? anchor, string desc)
    {
        if (anchor == null)
        {
            return;
        }

        var effective = anchor.Value;
        if (effective.Date != anchor.Value.Date && anchor.Value.Date == DateTime.Today.AddDays(1))
        {
            return; // rolled past midnight into tomorrow — not today's slot
        }

        list.Add(new FireSlot(effective, effective.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture), desc));
    }

    /// <summary>Parses "HH:mm" into today's DateTime (local). Null when malformed.</summary>
    private static DateTime? ParseHhMm(string? value, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m))
        {
            return null;
        }

        if (h < 0 || h > 23 || m < 0 || m > 59)
        {
            return null;
        }

        return now.Date.AddHours(h).AddMinutes(m);
    }

    /// <summary>Parses "D HH:mm" (D=1..7 Mon..Sun) into today's DateTime. Null when malformed.</summary>
    private static DateTime? ParseWeekly(PluginConfiguration config, DateTime now)
        => ParseWeeklySpec(config.RandomWeeklyTime, now);

    /// <summary>Same "D HH:mm" parsing for the dedicated refresh/oshash anchors.</summary>
    private static DateTime? ParseWeeklySpec(string? spec, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var parts = spec.Split(' ');
        return parts.Length == 2 ? ParseHhMm(parts[1], now) : null;
    }

    /// <summary>Parses the "HH:mm" portion of a "D HH:mm" weekly anchor for daily cadences.</summary>
    private static DateTime? ParseHhMmFromWeeklySpec(string? spec, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var parts = spec.Split(' ');
        if (parts.Length != 2)
        {
            return null;
        }

        return ParseHhMm(parts[1], now);
    }


    /// <summary>True when today's weekday matches the diced weekly anchor "D".</summary>
    private static bool IsWeeklyDay(PluginConfiguration config, DateTime now)
        => IsWeeklyDaySpec(config.RandomWeeklyTime, now);

    /// <summary>Day-match for the dedicated refresh/oshash anchors.</summary>
    private static bool IsWeeklyDaySpec(string? spec, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var parts = spec.Split(' ');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int d) || d < 1 || d > 7)
        {
            return false;
        }

        int todayD = now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek;
        return todayD == d;
    }

    /// <summary>ISO-8601 week number (1..53) for cadence gating.</summary>
    private static int IsoWeek(DateTime date)
    {
        var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
        return cal.GetWeekOfYear(date.Date, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
    }

    /// <summary>F-M57: rhythm → approximate day distance (for "shorter rhythm wins").</summary>
    private static int RhythmDays(UpdateInterval interval) => interval switch
    {
        UpdateInterval.Daily => 1,
        UpdateInterval.TwiceDaily => 0, // twice a day — tightest
        UpdateInterval.TwiceWeekly => 3,
        UpdateInterval.Weekly => 7,
        UpdateInterval.Monthly => 30,
        _ => int.MaxValue // Manual/OnArrival/unknown
    };

    /// <summary>F-M116: mirror pending recovery fires to recovery-fires.json
    /// (UTC ISO-8601, round-trip 'O'); consumed keys are written as null.</summary>
    private void PersistRecoveryFires()
    {
        try
        {
            var payload = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"download\":\"{0}\",\"upload\":\"{1}\"}}",
                _recoveryFireDownloadUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                _recoveryFireUploadUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            File.WriteAllText(Path.Combine(_configDirectory, RecoveryFile), payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL] Recovery-fire persist failed: {Msg}", ex.Message);
        }
    }

    /// <summary>F-M116: reload pending recovery fires on startup. Past-due fires
    /// stay as-is — CheckAndFire consumes them on the first tick (immediate fire).</summary>
    private void LoadRecoveryFires()
    {
        try
        {
            var file = Path.Combine(_configDirectory, RecoveryFile);
            if (!File.Exists(file))
            {
                return;
            }

            var payload = File.ReadAllText(file);
            bool any = false;
            foreach (var part in payload.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':', 2);
                if (kv.Length != 2)
                {
                    continue;
                }

                var raw = kv[1].Trim().Trim('"', '}');
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
                {
                    if (kv[0].Contains("download", StringComparison.Ordinal))
                    {
                        _recoveryFireDownloadUtc = when.ToUniversalTime();
                    }
                    else if (kv[0].Contains("upload", StringComparison.Ordinal))
                    {
                        _recoveryFireUploadUtc = when.ToUniversalTime();
                    }
                }
            }

            if (_recoveryFireDownloadUtc.HasValue || _recoveryFireUploadUtc.HasValue)
            {
                LogUtil.Normal(_logger, 
                    "[SubDL] Recovery fires restored from disk: download {D:yyyy-MM-dd HH:mm} UTC, upload {U:yyyy-MM-dd HH:mm} UTC (F-M116).",
                    _recoveryFireDownloadUtc ?? DateTime.MinValue,
                    _recoveryFireUploadUtc ?? DateTime.MinValue);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL] Recovery-fire reload failed: {Msg}", ex.Message);
        }
    }

    /// <summary>(user decision 29.09.2026): mirror the WHOLE scheduler state to
    /// scheduler-state.json. Every dedup test in this class is answered against a RAM
    /// field, so without this a Jellyfin restart re-claims slots that already fired
    /// today and re-runs work that was done: measured on prod 28.09.2026 (OSHash anchor
    /// Sunday 07:46) 23 restarts produced 22 OSHash fires against ONE real anchor fire,
    /// and postprocessing fired 22 times with 20 of them 0-1 s after a restart. The
    /// marker counts the CLAIM, not the outcome — a run that fails after being queued
    /// stays consumed for the day, exactly as it did before.
    /// Written at most once per tick and only when something actually changed.</summary>
    private void PersistSchedulerState()
    {
        try
        {
            var payload = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"refresh\":\"{0}\",\"oshash\":\"{1}\",\"postprocess\":\"{2}\",\"fired\":\"{3}\","
                + "\"catchup\":{4},\"refreshDefer\":\"{5}\",\"oshashDefer\":\"{6}\",\"postprocessDefer\":\"{7}\"}}",
                _lastPruneMarker ?? string.Empty,
                _lastOshashMarker ?? string.Empty,
                _lastPostprocessMarker ?? string.Empty,
                _lastFiredMarker ?? string.Empty,
                _catchUpFired ? "true" : "false",
                _pruneDeferredUntil.ToString("O", CultureInfo.InvariantCulture),
                _oshashDeferredUntil.ToString("O", CultureInfo.InvariantCulture),
                _postprocessDeferredUntil.ToString("O", CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(_configDirectory, SchedulerStateFile), payload);
            _stateDirty = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL] Scheduler-state persist failed: {Msg}", ex.Message);
        }
    }

    /// <summary>(user decision 29.09.2026): reload the scheduler state on startup. A
    /// marker restored from disk is already consumed for its day, so the due-test skips
    /// the slot until the next anchor day — which is what stops a restart from replaying
    /// it. A missing or unreadable file simply leaves the fields at their defaults (the
    /// pre-persist behaviour), so a fresh install or a deleted file is never worse.</summary>
    private void LoadSchedulerState()
    {
        try
        {
            var file = Path.Combine(_configDirectory, SchedulerStateFile);
            if (!File.Exists(file))
            {
                return;
            }

            var payload = File.ReadAllText(file);
            // Split on ',' then ':' — the values carry no comma (markers are digits,
            // instants use the round-trip 'O' format, which has no separator conflict).
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in payload.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':', 2);
                if (kv.Length != 2)
                {
                    continue;
                }

                fields[kv[0].Trim().TrimStart('{').Trim('"')] =
                    kv[1].Trim().Trim('"', '}', ' ');
            }

            if (fields.TryGetValue("refresh", out var r) && !string.IsNullOrWhiteSpace(r))
            {
                _lastPruneMarker = r;
            }

            if (fields.TryGetValue("oshash", out var o) && !string.IsNullOrWhiteSpace(o))
            {
                _lastOshashMarker = o;
            }

            if (fields.TryGetValue("postprocess", out var p) && !string.IsNullOrWhiteSpace(p))
            {
                _lastPostprocessMarker = p;
            }

            if (fields.TryGetValue("fired", out var f) && !string.IsNullOrWhiteSpace(f))
            {
                _lastFiredMarker = f;
            }

            if (fields.TryGetValue("catchup", out var c)
                && bool.TryParse(c, out var catchUp))
            {
                _catchUpFired = catchUp;
            }

            _pruneDeferredUntil = ParseInstant(fields, "refreshDefer");
            _oshashDeferredUntil = ParseInstant(fields, "oshashDefer");
            _postprocessDeferredUntil = ParseInstant(fields, "postprocessDefer");

            LogUtil.Normal(_logger,
                "[SubDL] Scheduler state restored from disk: refresh {Refresh}, oshash {Oshash}, postprocessing {Pp}, refetch slot {Fired}, catch-up {CatchUp}.",
                _lastPruneMarker ?? "-",
                _lastOshashMarker ?? "-",
                _lastPostprocessMarker ?? "-",
                _lastFiredMarker ?? "-",
                _catchUpFired);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL] Scheduler-state reload failed: {Msg}", ex.Message);
        }
    }

    /// <summary>Reads one UTC instant written in round-trip format; MinValue when absent
    /// or unparseable, which is the "no deferral pending" value the due-tests expect.</summary>
    private static DateTime ParseInstant(Dictionary<string, string> fields, string key)
    {
        if (fields.TryGetValue(key, out var raw)
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
        {
            return when;
        }

        return DateTime.MinValue;
    }

    public void Dispose()
    {
        Instance = null;
        _cts.Cancel();
        _cts.Dispose();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }
    }
}