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
// Event dispatcher (F-M111, user decision 12.09.2026): thin coordinator. Owns
// the event sources (library changes via debounce, refetch tick, quota recovery
// fire) and the run sequence (seed → download → upload → reseed). The workers
// (seeder, pipelines) know nothing about each other.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.ScheduledTasks;

/// <summary>
/// (user decision 14.09.2026): the EXECUTION engine ("EventTrigger").
/// Three event sources feed one cycle machine:
///   1. Library change (ItemAdded) → on-arrival gates (F-M55) → 5-min debounce
///      (NOT reset by further events, user decision 12.09.2026) → cycle scoped
///      to the directions that want on-arrival.
///   2. Refetch tick (from the scheduler coordinator / scheduled tasks) → cycle.
///   3. Quota recovery fire → cycle (seed finds nothing → cheap no-op).
/// Cycle = seed → downloader → uploader (sequenced per direction, user decision
/// 13.09.2026) → new material during cycle? → repeat, else done.
/// The WHEN belongs to the scheduler (SubdlSchedulerCoordinator): it
/// owns due times, the lock probe and the spacing deferral; this class only
/// decides HOW a run executes once triggered.
/// </summary>
public sealed class SubdlEventDispatcher : IDisposable
{
    /// <summary>The process-wide instance (static access for task entry points).</summary>
    public static SubdlEventDispatcher? Instance { get; private set; }

    private const string QueueFileName = "cycle-queues.json";
    private const int MaxRetries = 3;

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SubdlSeeder _seeder;

    // Debounce timer (event path). First event arms it — further events do NOT
    // reset the countdown (user decision 12.09.2026).
    private Timer? _debounceTimer;
    private bool _timerArmed;
    private readonly object _lock = new();

    // F-M111 (user decision 12.09.2026): event-scoped seeding — the dispatcher
    // remembers the library of the changed item; the event seed scans ONLY that
    // library. Null = seed everything (refetch/recovery path).
    private System.Collections.Generic.HashSet<string>? _eventLibraries; // targeted set: grows per event, capped

    // Fix 13.09.2026: items from events, resolved to libraries at TIMER FIRE
    // (ItemAdded fires before JF links the item to its virtual folder).
    private System.Collections.Generic.List<MediaBrowser.Controller.Entities.BaseItem>? _pendingEventItems;

    // F-M233 (28.09.2026, user decision): the ids of everything that arrived inside
    // the current debounce window. The event cycle is scoped to exactly these items
    // ("nur auf die arrivals beschränken"); a full-library pass stays with the
    // schedule and the manual button.
    private System.Collections.Generic.HashSet<string>? _pendingEventIds;

    // (19.09.2026): arrivals DURING an active cycle. They must not be
    // Dropped — the running cycle's later rounds rely only on the
    // DateCreated/DateModified pre-check, and a fresh import whose media probe
    // completes shortly after its DB write carries a DateCreated OLDER than the
    // last scan stamp → the pre-check skips and the item is never seeded.
    private System.Collections.Generic.List<string>? _pendingDuringCycleIds;

    // One cycle at a time. Workers report per-item outcomes back into the queues.
    private bool _cycleActive;

    /// <summary>Direction filter for a cycle/seed pass (user decision 13.09.2026:
    /// scheduled/manual upload fires seed+run ONLY upload, download fires ONLY
    /// download — the unified event path keeps Both).</summary>
    public enum CycleDirection { Both, UploadOnly, DownloadOnly }

    /// <summary>Direction requested by a trigger that arrived while a cycle was
    /// already running — merged into the running cycle (latch, never shrinks).</summary>
    private volatile CycleDirection _pendingDirection = CycleDirection.Both;
    private CycleDirection _eventDirection = CycleDirection.Both; // Direction requested by the armed event debounce

    // ── Queues (owned here, persisted) ──────────────────────────────────────
    // F-M244: the seeder decides WHAT is to be done and hands it over in the item. The pipelines
    // carry that order out rather than re-deriving it.
    private List<QueueItem> _uploadQueue = new();
    // F-M244: the download side reads its languages from the item and does not answer again.
    private List<QueueItem> _downloadQueue = new();

    // Fix 13.09.2026 (user decision "kein Re-Extrakt-Loop"): item IDs whose
    // uploads/downloads permanently failed (purged after MaxRetries) — the
    // seeder skips these forever. Tombstones in the queue files are not enough
    // since Done/Purged leave the queue immediately (12.09.2026 decision).
    private HashSet<string> _purgedIds = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _purgedDirty;
    private volatile bool _runActive;
    private volatile bool _upQuotaStopped;   // Direction hit quota/rate limit in THIS cycle
    private volatile bool _downQuotaStopped; // → drain loop must not re-seed/re-run that direction

    // F-M22 (02.10.2026): the dry-run state of the direction currently running. The per-item
    // outcome handler (OnItemResult / ApplyOutcomeUpload) fires DURING the pipeline and used to
    // mark items `Done` for a run that only described them. The run's own summary is not available
    // yet at that moment, so the flag is set before the pipeline starts and cleared in `finally`.
    private volatile bool _upInFlightDryRun;
    private volatile bool _downInFlightDryRun;

    // Fate of each direction in the cycle that just ended, so the waiting download/upload task can
    // report what happened to ITS direction instead of a blanket "ok". Live 30.09.2026 18:51: the
    // download direction was stopped by the daily quota (429) after 2 of 1144 items and the GUI
    // still read "ok". Null = nothing to report, the seeder's row decides.
    private volatile string? _dirOutcomeUp;
    private volatile string? _dirOutcomeDown;
    private volatile string? _dirDetailUp;
    private volatile string? _dirDetailDown;

    // The seeder's fate in THIS cycle, in memory. The database row alone is not enough: a cycle
    // that only ran a pre-check skip leaves the previous cycle's row untouched, so reading it would
    // report a stale fate (a leftover "deferred" would paint a clean cycle yellow).
    private volatile string? _seederOutcome;
    private volatile string? _seederDetail;
    private volatile int _lastSeedNewItems;  // Items the last seed ADDED (new material)

    // F-M311: media files whose language codes a scan WROTE and that no run has reported yet.
    // Accumulated across scans and consumed ONCE by the statistics writer. Both halves matter: the
    // allocation pass is direction-independent (it sits before the direction checks and edits each
    // file once, and the second scan of a cycle finds the tags already on disk), so a cycle that
    // seeds both directions produces the count only on its first scan — but a scan whose direction
    // then ends WITHOUT a run (no arrivals, empty queue) would lose its number if this were
    // assigned rather than added.
    private int _pendingLanguageCodesAllocated;

    // F-M313: loose subtitle files a scan RENAMED so their name carries the language (F-M278).
    // Same channel and same rule as the counter above: accumulated across scans, consumed once by
    // the statistics writer. Kept separate because it is a different act on a different file kind.
    private int _pendingLooseSubtitlesRenamed;
    // Rev.3: the ITEM IDS the last seed added (per direction) — the
    // final sweep run processes ONLY these, never the retry-queued leftovers
    // of the earlier run in the same cycle.
    private readonly System.Collections.Generic.HashSet<string> _lastSeedNewIdsUp = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Generic.HashSet<string> _lastSeedNewIdsDown = new(System.StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource _cts = new();
    // F-M131 (15.09.2026, user decision): the Jellyfin stop button must abort the
    // CURRENT run — not just detach from it. _stopCts is cancelled by
    // RequestUserStop(); the pipelines receive a token linked against it and
    // their loops abort like a quota stop (remaining items stay due).
    private CancellationTokenSource _stopCts = new();
    private volatile bool _userStopActive;

    // (15.09.2026): start time of the last REAL seed scan. Seed rounds
    // 2..n compare the library's newest item change (JF DB) against this; no
    // change since → skip the expensive scan. Persisted so a restart keeps the
    // fast path (round 1 / first post-restart seed always scans anyway).
    // Rev.2 (15.09.2026, mid-scan-arrival test): TWO stamps, one per
    // direction. A direction-scoped scan (UploadOnly/DownloadOnly in loop
    // rounds) only moves ITS stamp — otherwise a download-side scan would
    // burn the upload side's pre-check while the upload seed never saw the
    // new item (missed arrivals until the next anchor).
    private DateTime _lastScanStampUpUtc = DateTime.MinValue;
    private DateTime _lastScanStampDownUtc = DateTime.MinValue;
    private const string SeedStampFileName = "seed-scan-stamp.txt";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private string QueuePath => Path.Combine(Plugin.Instance!.DataFolderPath, QueueFileName);
    private string PurgedPath => Path.Combine(Plugin.Instance!.DataFolderPath, "purged-items.json");

    /// <summary>Initializes the dispatcher (DI singleton — eager resolution via task ctors).</summary>
    public SubdlEventDispatcher(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ILoggerFactory loggerFactory, TmdbImdbResolver tmdb)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("Jellyfin.Plugin.SubdlScribe.ScheduledTasks.SubdlEventDispatcher");
        _seeder = new SubdlSeeder(libraryManager, mediaSourceManager, loggerFactory, tmdb);
        _libraryManager.ItemAdded += OnItemAdded;
        LoadQueues();
        Instance = this;
        LogUtil.Normal(_logger, "[SubDL-Dispatch] Active."); // No queue counts
    }

    /// <summary>Library (collection) name of an item, preferring the SELECTED library.</summary>
    private string? ResolveLibraryName(MediaBrowser.Controller.Entities.BaseItem item)
    {
        // Fix 13.09.2026 (user report "library unknown — full scan"): the old
        // parent-walk failed on JF 10.11 — libraries are plain Folders there,
        // freshly-added items have no parent chain yet, and GetItemById(root)
        // can return null. GetCollectionFolders is the canonical API: it maps
        // the item to its virtual (library) folder(s) directly.
        //
        // F-M189 (24.09.2026): with NESTED libraries GetCollectionFolders
        // returns the OUTER name (the inner library owns no items — JF logs
        // "Found duplicate path"), so resolving through the selected scope
        // first keeps the event path targeted on the selected subtree instead
        // of widening it to the outer library.
        try
        {
            var selected = LibraryScope.Create(
                _libraryManager,
                Plugin.Instance?.Configuration.SelectedLibraries,
                _logger);
            var viaScope = selected.ResolveSelectedName(item);
            if (viaScope != null)
            {
                return viaScope;
            }

            var folders = _libraryManager.GetCollectionFolders(item);
            var first = folders.FirstOrDefault();
            return first?.Name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL-Dispatch] ResolveLibraryName failed for {Name}: {Msg}", item?.Name ?? "?", ex.Message);
            return null;
        }
    }

    /// <summary>Debounce window (minutes, config-driven, default 1).</summary>
    private static TimeSpan DebounceWindow => TimeSpan.FromMinutes(
        Plugin.Instance?.Configuration.ArrivalDebounceMinutes is int m && m > 0 ? Math.Clamp(m, 1, 120) : 1);

    // ────────────────────────────────────────────────────────────────────────
    //  EVENT SOURCE 1: library changes
    // ────────────────────────────────────────────────────────────────────────

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        if (config == null)
        {
            return;
        }

        if (e.Item is not (Movie or Episode))
        {
            return;
        }

        if (config.SelectedLibraries.Count == 0)
        {
            return;
        }

        // F-M55 arrival gates (restored 14.09.2026): events only start a
        // cycle when the direction actually wants on-arrival. If only ONE
        // direction listens, the cycle is scoped to that direction; the other
        // direction still gets its regular anchor runs.
        bool upArr = config.UploadEnabled && config.UploadOnArrival;
        bool downArr = config.DownloadEnabled && config.DownloadOnArrival;
        if (!upArr && !downArr)
        {
            LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger,"[SubDL-Dispatch] Event ignored — on-arrival disabled for both directions: {Name}", e.Item.Name);
            return;
        }

        lock (_lock)
        {
            if (_cycleActive)
            {
                // (19.09.2026): remember the item — the final seed sweep
                // ("catch it") never existed as a collector; the follow-up rounds'
                // Pre-check misses imports whose probe finishes after the
                // last scan stamp (DateCreated < stamp). Force one extra scan.
                lock (_lock)
                {
                    _pendingDuringCycleIds ??= new System.Collections.Generic.List<string>();
                    _pendingDuringCycleIds.Add(e.Item.Id.ToString());
                }
                LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger,"[SubDL-Dispatch] Event during cycle — next seed round will include it: {Name}", e.Item.Name);
                return;
            }

            if (_timerArmed)
            {
                // F-M111 + F-M233: further events do NOT reset the timer — but they
                // DO join the id collector, so the window's single cycle covers every
                // arrival in it. Dropping them would lose arrivals now that the run
                // is scoped to the collected ids.
                _pendingEventIds ??= new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                _pendingEventIds.Add(e.Item.Id.ToString());
                LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger,"[SubDL-Dispatch] Event during debounce (timer not reset, joined): {Name}", e.Item.Name);
                return;
            }

            _eventDirection = upArr && downArr ? CycleDirection.Both : upArr ? CycleDirection.UploadOnly : CycleDirection.DownloadOnly;
            _timerArmed = true;
        }

        // F-M111: remember the CHANGED ITEMS (not libraries — Fix 13.09.2026:
        // at ItemAdded time JF has not linked the item to its virtual folder
        // yet, so ResolveLibraryName here always returned null → "unknown —
        // full scan". The library names are resolved at timer fire, 5 min
        // later, when the import is complete).
        lock (_lock)
        {
            _pendingEventItems ??= new System.Collections.Generic.List<MediaBrowser.Controller.Entities.BaseItem>();
            _pendingEventItems.Add(e.Item);
            _pendingEventIds ??= new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            _pendingEventIds.Add(e.Item.Id.ToString());
        }

        LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger,"[SubDL-Dispatch] Event: {Name} — cycle in {Min} min.", e.Item.Name, DebounceWindow.TotalMinutes);
        _debounceTimer ??= new Timer(_ => _ = TimerFiredAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _debounceTimer.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
    }

    private async Task TimerFiredAsync()
    {
        List<MediaBrowser.Controller.Entities.BaseItem>? pending;
        lock (_lock)
        {
            _timerArmed = false;
            if (_cycleActive)
            {
                // F-M233: a cycle is running, so this window cannot start its own.
                // Hand its items to the during-cycle collector — the running cycle's
                // follow-up picks them up. Dropping them here would lose arrivals
                // now that the follow-up is scoped to exactly that collector.
                if (_pendingEventIds is { Count: > 0 })
                {
                    _pendingDuringCycleIds ??= new System.Collections.Generic.List<string>();
                    foreach (var id in _pendingEventIds)
                    {
                        if (!_pendingDuringCycleIds.Contains(id))
                        {
                            _pendingDuringCycleIds.Add(id);
                        }
                    }

                    LogUtil.Normal(_logger, "[SubDL-Dispatch] debounce fired during a running cycle — {N} arrival(s) handed to the follow-up.", _pendingEventIds.Count);
                }

                _pendingEventIds = null;
                _pendingEventItems = null;
                _eventLibraries = null;
                return;
            }

            _cycleActive = true;
            // Fix 13.09.2026: resolve library names NOW (import complete) — at
            // ItemAdded time the virtual-folder link does not exist yet.
            pending = _pendingEventItems;
            _pendingEventItems = null;
            _eventLibraries = null;
        }

        var eventDirection = _eventDirection; // Direction latched when the debounce was armed 

        if (pending != null)
        {
            foreach (var item in pending)
            {
                var libName = ResolveLibraryName(item);
                if (libName == null)
                {
                    _logger.LogWarning("[SubDL-Dispatch] Library unresolvable for {Name} — full scan.", item.Name);
                    lock (_lock)
                    {
                        _eventLibraries = null;
                        break;
                    }
                }

                lock (_lock)
                {
                    _eventLibraries ??= new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                    _eventLibraries.Add(libName);
                }
            }

            LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger,"[SubDL-Dispatch] Event libraries: {Libs}", _eventLibraries == null ? "unknown — full scan" : string.Join(", ", _eventLibraries));
        }

        try
        {
            await RunCycleAsync("event", eventDirection).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MarkCycleFailure(ex.Message);
            _logger.LogError("[SubDL-Dispatch] Cycle failed: {Msg}", ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _cycleActive = false;
            }
            Fm152cFollowUpIfPending();
        }
    }

    /// <summary>(19.09.2026): if during-cycle arrivals were collected but
    /// the cycle ended before any forced seed consumed them (late arrival after
    /// the last follow-up round), kick a follow-up cycle so the forced scan
    /// actually runs. 90 s delay: the media probe usually needs a moment.</summary>
    private void Fm152cFollowUpIfPending()
    {
        bool hasPending;
        lock (_lock)
        {
            hasPending = (_pendingDuringCycleIds?.Count ?? 0) > 0;
        }
        if (!hasPending)
        {
            return;
        }
        // (19.09.2026, user "falls tickbox gesetzt"): the follow-up run
        // is a 2..n round — only kick it when at least one direction still has
        // follow-up rounds enabled. Both off → next scheduled cycle catches up.
        var cfgFm152c = Plugin.Instance?.Configuration;
        if (cfgFm152c?.FollowUpRoundsDownload == false && cfgFm152c?.FollowUpRoundsUpload == false)
        {
            return;
        }
        LogUtil.Normal(_logger, "[SubDL-Dispatch] during-cycle arrivals still pending at cycle end — follow-up cycle in 90 s.");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(90), _cts.Token).ConfigureAwait(false);
                TriggerCycle("arrival-followup"); // DirectionOf default → Both
            }
            catch (OperationCanceledException)
            {
                // Dispatcher shut down before the 90 s delay finished — expected, no action needed.
            }
            catch (Exception ex)
            {
                _logger.LogError("[SubDL-Dispatch] during-cycle follow-up failed: {Msg}", ex.Message);
            }
        }, _cts.Token);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  EVENT SOURCE 2+3: refetch tick / quota recovery (fire-and-forget)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Direction implied by a trigger reason (user decision 13.09.2026):
    /// the upload task seeds+runs upload only, the download task download only,
    /// everything else (event, recovery) both.</summary>
    private static CycleDirection DirectionOf(string reason) => reason switch
    {
        "scheduled-upload" => CycleDirection.UploadOnly,
        "scheduled-download" => CycleDirection.DownloadOnly,
        "arrival-upload" => CycleDirection.UploadOnly, // Arrival watcher hands over
        "arrival-download" => CycleDirection.DownloadOnly,
        _ => CycleDirection.Both
    };

    /// <summary>
    /// F-M233 (28.09.2026, user decision "Gesamtlauf nur im schedule oder der manuelle
    /// knopf"): true when the trigger must work ONLY the collected arrivals instead of
    /// scanning the whole selection — the event debounce and its follow-up. Scheduled,
    /// manual and recovery triggers return false and keep full coverage.
    /// </summary>
    /// <param name="reason">Cycle trigger reason.</param>
    /// <returns>True for the on-arrival path.</returns>
    private static bool IsArrivalTrigger(string reason)
        => reason is "event" or "arrival-followup";

    /// <summary>
    /// F-M233 ("kein treffer, kein lauf"): an arrival-scoped round starts no run when
    /// its own arrivals queued nothing. A non-scoped round (schedule/manual) always
    /// proceeds. Extracted so the rule is testable without the Jellyfin host.
    /// </summary>
    /// <param name="scopedOnly">True for an arrival-scoped round.</param>
    /// <param name="freshCount">Items the seed just added for this direction.</param>
    /// <returns>True when the run must be skipped.</returns>
    public static bool ShouldSkipRunForArrivalScope(bool scopedOnly, int freshCount)
        => scopedOnly && freshCount == 0;

    /// <summary>
    /// F-M233: true when an arrival cycle has nothing to work and must end without
    /// widening to a full scan. Schedule and manual runs are never empty (they carry
    /// no item scope at all, so they always proceed).
    /// </summary>
    /// <param name="isArrivalTrigger">True for the event/follow-up triggers.</param>
    /// <param name="collectedCount">Number of collected arrival ids.</param>
    /// <returns>True when the cycle must end immediately.</returns>
    public static bool ArrivalCycleHasNothingToDo(bool isArrivalTrigger, int collectedCount)
        => isArrivalTrigger && collectedCount == 0;

    /// <summary>Fired by scheduled tasks and recovery fires. Returns true when the
    /// request STARTED a fresh cycle; false when a cycle is already running — the
    /// trigger is then RESCHEDULED by JobSpacingMinutes (user decision 25.09.2026:
    /// no merge, a running cycle is never widened) and the caller can finish.</summary>
    private volatile TaskCompletionSource<bool>? _cycleDone; // Task-visible completion

    /// <summary>
    /// Awaits the END of the cycle that a TriggerCycle call started (or
    /// the one currently running). Returns false when nothing is/was running or
    /// on timeout/cancel — the caller (scheduled task) can then finish, and
    /// Jellyfin's "task completed" line lands after the real work, not before.
    /// </summary>
    public async Task<bool> WaitForCycleAsync(TimeSpan timeout, CancellationToken ct)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_lock)
        {
            if (!_cycleActive)
            {
                return false;
            }
            tcs = _cycleDone ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return done == tcs.Task;
    }

    public bool TriggerCycle(string reason)
    {
        var dir = DirectionOf(reason);
        lock (_lock)
        {
            if (_cycleActive)
            {
                // (user decision 25.09.2026, "Kein merge, reschedule mit spacing"):
                // a running cycle is NEVER widened and the trigger is NEVER merged
                // into it. The other direction wants its own run — push it forward by
                // JobSpacingMinutes via the coordinator's one-shot fire, which re-checks
                // the global run lock when it comes due and defers again if still busy.
                var spacing = Math.Clamp(Plugin.Instance?.Configuration.JobSpacingMinutes ?? 15, 5, 120);
                var fireAt = DateTime.UtcNow.AddMinutes(spacing);
                bool accepted = true;
                if (dir != CycleDirection.DownloadOnly)
                {
                    accepted &= SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFireAt(upload: true, fireAt) ?? false;
                }

                if (dir != CycleDirection.UploadOnly)
                {
                    accepted &= SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFireAt(upload: false, fireAt) ?? false;
                }

                if (accepted)
                {
                    _logger.LogWarning(
                        // F-M204: a running cycle is never widened; the overlap is answered by a reschedule.
                        "[SubDL-Dispatch] {Reason} rescheduled +{Spacing} min — cycle already running (dir {Running}, requested {Requested}).",
                        reason, spacing, _pendingDirection, dir);
                }
                else
                {
                    _logger.LogWarning(
                        "[SubDL-Dispatch] {Reason} NOT rescheduled — reschedule limit reached for today; "
                        + "the budget resets at the next UTC day roll-over (00:00 UTC) (dir {Running}, requested {Requested}).",
                        reason, _pendingDirection, dir);
                }

                return false;
            }

            _cycleActive = true;
            _pendingDirection = dir; // fresh cycle: latch starts at the trigger's direction
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunCycleAsync(reason, dir).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MarkCycleFailure(ex.Message);
                _logger.LogError("[SubDL-Dispatch] {Reason} cycle failed: {Msg}", reason, ex.Message);
            }
            finally
            {
                lock (_lock)
                {
                    _cycleActive = false;
                    _cycleDone?.TrySetResult(true);
                    _cycleDone = null;
                }
                Fm152cFollowUpIfPending();
            }
        }, _cts.Token);
        return true;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  CYCLE: seed → download → upload → reseed → repeat|complete
    // ────────────────────────────────────────────────────────────────────────

        /// <summary>
    /// Cycle orchestrator: seed → run rounds until no new material appears.
    /// </summary>
    private async Task RunCycleAsync(string trigger, CycleDirection initialDirection)
    {
        var (dir, onlyLibs, onlyItems) = PrepareCycleState(trigger, initialDirection);

        // F-M233: scheduled, manual and recovery runs keep full coverage; only the
        // arrival path is narrowed to the collected items.
        if (IsArrivalTrigger(trigger))
        {
            // Guard: an arrival cycle must NEVER widen to a full scan. The follow-up
            // trigger fires 90 s after the cycle that drained the collector — if
            // nothing new arrived in the meantime, there is nothing to do and the
            // full-coverage path stays reserved for schedule/manual.
            if (ArrivalCycleHasNothingToDo(true, onlyItems?.Count ?? 0))
            {
                LogUtil.Normal(_logger, "[SubDL-Dispatch] {Trigger}: no arrivals collected — no run.", trigger);
                return;
            }
        }
        else
        {
            onlyItems = null;
        }

        // An ARRIVAL cycle has no waiting scheduled task to write the direction rows: Download and
        // Upload were only ever recorded by SubdlDownloadTask/SubdlUploadTask, and an event never
        // goes through them. Observed live 03.10.2026: two real arrival cycles ran (Ted Lasso 06:40,
        // Dark Matter 09:45), both directions did their work, yet the GUI kept showing the previous
        // scheduled run — only the Seeder row moved. The dispatcher therefore records those two rows
        // itself, but ONLY for the arrival triggers: on a scheduled or manual run the waiting task
        // owns them, and two writers on one row would fight.
        //
        // Operator order 08.10.2026 ("seeder gets blue only, download only download, upload only
        // upload — as it is successive"): the rows are NOT marked running here any more. This call
        // sat at the CYCLE's start, so both directions lit blue while the seeder was still scanning —
        // measured on prod, Seeder 19:12:52 and Upload 19:12:52 from one and the same cycle, two lit
        // lamps for one worker. Each direction now marks itself when its own run begins.
        try
        {
            await RunCycleBodyAsync(trigger, dir, onlyLibs, onlyItems).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Before the rows are written, so a failure is RED rather than green. The outer caller
            // calls this again — it fills only the fates that are still null, so that is a no-op.
            MarkCycleFailure(ex.Message);
            throw;
        }
        finally
        {
            if (IsArrivalTrigger(trigger))
            {
                RecordArrivalDirectionFinish(dir);
            }
        }
    }

    /// <summary>
    /// The cycle's work: seed and run each direction, then the follow-up rounds.
    /// <para>
    /// Split out of <see cref="RunCycleAsync"/> so the arrival rows can be written in a
    /// <c>finally</c>. Every exit — an early return while the seed lock is busy just as much as an
    /// exception out of the pipeline — then leaves a finished row instead of one stuck on "run".
    /// </para>
    /// </summary>
    /// <param name="trigger">Cycle trigger reason.</param>
    /// <param name="dir">Direction this cycle works.</param>
    /// <param name="onlyLibs">Libraries the event arrived in, or null for a full scan.</param>
    /// <param name="onlyItems">Item ids the event collected, or null for full coverage.</param>
    private async Task RunCycleBodyAsync(string trigger, CycleDirection dir, System.Collections.Generic.ISet<string>? onlyLibs, System.Collections.Generic.ISet<string>? onlyItems)
    {
        // Rev.3: clear stale stop markers at cycle start so a leftover marker
        // from a previous crashed/restarted run cannot abort this cycle before it begins.
        Pipeline.PipelineStopSignal.ClearStaleMarkers(Plugin.Instance?.DataFolderPath ?? string.Empty);

        var configForCycle = Plugin.Instance!.Configuration;

        // F-M149: download before upload in round 1.
        // F-M233: an arrival cycle is scoped (onlyItems) and must NOT start a run
        // when the seed found nothing new for that direction ("kein treffer, kein lauf").
        bool arrivalScoped = onlyItems != null;

        // F-M331 (operator order 08.10.2026): "Api limit oder download limit voll, downloader
        // startet erst garnicht." Asked ONCE per cycle, before the download seed, and only when the
        // cycle actually covers the download direction — a directed upload fire must not spend a
        // probe on a question it never asks. The answer is then consulted exactly where
        // `config.DownloadEnabled` is consulted, so a spent allowance behaves like the download
        // switch being off: no queue entries AND no run.
        _downloadAllowanceHeld = null;
        if (dir != CycleDirection.UploadOnly && configForCycle.DownloadEnabled)
        {
            _downloadAllowanceHeld = await ReadDownloadAllowanceAsync(configForCycle).ConfigureAwait(false);
            if (_downloadAllowanceHeld != null)
            {
                LogUtil.Normal(_logger, "[SubDL-Dispatch] download held back this cycle — {Reason} (F-M331).", _downloadAllowanceHeld);
            }
        }
        if (dir != CycleDirection.UploadOnly
            && !await SeedAndMaybeRunAsync(trigger, onlyLibs, configForCycle, upload: false, scopedOnly: arrivalScoped, onlyItems: onlyItems).ConfigureAwait(false))
        {
            return;
        }
        if (dir != CycleDirection.DownloadOnly
            && !await SeedAndMaybeRunAsync(trigger, onlyLibs, configForCycle, upload: true, scopedOnly: arrivalScoped, onlyItems: onlyItems).ConfigureAwait(false))
        {
            return;
        }

        await RunFollowUpRoundsAsync(trigger, onlyLibs, configForCycle, dir, onlyItems).ConfigureAwait(false);
        LogCycleEnd(trigger, dir);
    }

    /// <summary>
    /// Resets per-cycle state and consumes any latched direction.
    /// </summary>
    private (CycleDirection dir, System.Collections.Generic.ISet<string>? onlyLibs, System.Collections.Generic.ISet<string>? onlyItems) PrepareCycleState(string trigger, CycleDirection initialDirection)
    {
        _upQuotaStopped = false;
        _downQuotaStopped = false;
        _dirOutcomeUp = null;
        _dirOutcomeDown = null;
        _dirDetailUp = null;
        _dirDetailDown = null;
        _seederOutcome = null;
        _seederDetail = null;
        // Operator order 08.10.2026 ("jeder worker meldet nur sich selbst"): the seeder row
        // SUMS the cycle's legs. A Both cycle seeds the download leg and then the upload leg, and
        // the second write overwrote the first, so the download leg's numbers vanished from a row
        // that claims to summarise the cycle. Fresh per cycle, accumulated per leg.
        _seedLegsScanned = 0;
        _seedCoveredUp = false;
        _seedCoveredDown = false;
        _seedQueuedUp = 0;
        _seedQueuedDown = 0;
        _dirRowWrittenUp = false;
        _dirRowWrittenDown = false;
        lock (_lock) { _userStopActive = false; }
        try { _stopCts.Dispose(); } catch { }
        _stopCts = new CancellationTokenSource();
        LogUtil.Normal(_logger, "[SubDL-Dispatch] cycle start ({Trigger}, dir {Dir}).", trigger, initialDirection);

        CycleDirection dir;
        lock (_lock)
        {
            dir = initialDirection == CycleDirection.Both ? CycleDirection.Both : _pendingDirection;
            _pendingDirection = dir; // consumed
        }

        System.Collections.Generic.ISet<string>? onlyLibs;
        System.Collections.Generic.ISet<string>? onlyItems = null;
        lock (_lock)
        {
            onlyLibs = trigger == "event" ? _eventLibraries : null;

            // F-M233: the arrival path carries the ids that actually arrived. The
            // follow-up trigger reuses whatever the event left behind (its own window
            // is the during-cycle collector below). Taken under the same lock as the
            // libraries so the two sets cannot describe different windows.
            if (trigger == "event")
            {
                onlyItems = _pendingEventIds;
                _pendingEventIds = null; // consumed by this cycle
            }
            else if (trigger == "arrival-followup")
            {
                onlyItems = _pendingDuringCycleIds != null
                    ? new System.Collections.Generic.HashSet<string>(_pendingDuringCycleIds, System.StringComparer.OrdinalIgnoreCase)
                    : null;
            }
        }

        return (dir, onlyLibs, onlyItems);
    }

    private void LogCycleEnd(string trigger, CycleDirection dir)
    {
        if (_userStopActive)
        {
            LogUtil.Normal(_logger, "[SubDL-Dispatch] cycle end ({Trigger}, dir {Dir}) — user stop.", trigger, dir);
        }
        else
        {
            LogUtil.Normal(_logger, "[SubDL-Dispatch] cycle end ({Trigger}, dir {Dir}).", trigger, dir);
        }
    }

    /// <summary>
    /// Asks SubDL whether the download direction has any allowance left, ONCE per cycle.
    /// <para>
    /// Operator order 08.10.2026: "Api limit oder download limit voll, downloader startet erst
    /// garnicht." Measured on prod the same day: the 50/day download limit had been spent since
    /// 05:59, yet four further cycles (20:45, 21:09, 21:32, 21:33) each walked ~1 000 of 1 144
    /// queued items, spent the day's SEARCH allowance (59 → 194) and saved nothing — 104 s per
    /// cycle into the same wall.
    /// </para>
    /// <para>
    /// The check lives HERE, not in the seeder: the seeder fills queues and does not decide policy,
    /// so it takes the answer as a plain flag exactly like <c>config.DownloadEnabled</c>. One
    /// read-only call to <c>/me</c> per cycle, which consumes no allowance (verified: the settings
    /// page queries it freely).
    /// </para>
    /// <para>
    /// A failure to READ the counters is NOT a verdict — it returns null and the cycle proceeds as
    /// before. Fail-closed here would turn a network hiccup into a silent day without downloads,
    /// which is worse than one wasted run; the pipeline still guards itself.
    /// </para>
    /// </summary>
    /// <param name="config">Plugin configuration (credentials).</param>
    /// <returns>The reason to hold back, or null when the direction may proceed.</returns>
    private async Task<string?> ReadDownloadAllowanceAsync(PluginConfiguration config)
    {
        if (config.MissingCredentials().Count > 0)
        {
            return null; // the pipeline refuses on its own; not this check's verdict to make
        }

        var (api, http) = PluginServiceRegistrator.BuildApiClient(config);
        try
        {
            // F-M334 (operator order 09.10.2026): a DRY RUN fetches no FILE, so the download counter
            // cannot stop it. Measured on the test server: the limit stood at 50/50 and the probe held
            // back the one run whose whole purpose is to report what WOULD happen — the operator's
            // dry-run test could not be started at all. The counter is still read and reported below
            // when the run is real, which is when a file fetch is actually at stake.
            if (!config.DownloadDryRun)
            {
                // The download counter: it is the one that stops the FILE fetch, and the one that
                // was spent in the case F-M331 was written for.
                var down = await api.ReadQuotaAsync(forDownload: true, System.Threading.CancellationToken.None).ConfigureAwait(false);
                if (down == QuotaRead.Spent && api.Quota != null)
                {
                    return string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "download limit spent ({0}/{1})",
                        api.Quota.DownloadsUsed,
                        api.Quota.DownloadsLimit);
                }
            }

            // The search allowance applies to a dry run too, and this is not an oversight: a dry run
            // DOES search every item — it spends search quota, and it draws every line it prints from
            // those answers. Without the allowance it could not produce one line, so the hold is right
            // in both modes. Only the FILE fetch is what a dry run never reaches.
            var search = await api.ReadQuotaAsync(forDownload: false, System.Threading.CancellationToken.None).ConfigureAwait(false);
            if (search == QuotaRead.Spent && api.Quota != null)
            {
                return string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "search allowance spent ({0}/{1})",
                    api.Quota.SearchUsed,
                    api.Quota.SearchLimit);
            }

            return null;
        }
        catch (Exception ex)
        {
            // Unreadable is not spent — see the summary above.
            LogUtil.Detail(config.LogMode, _logger, "[SubDL-Dispatch] quota probe failed ({Msg}) — proceeding as before.", ex.Message);
            return null;
        }
        finally
        {
            http.Dispose();
        }
    }

    /// <summary>
    /// Seed + run helper used by round 1 and follow-up rounds.
    /// </summary>
    private async Task<bool> SeedAndMaybeRunAsync(string trigger, System.Collections.Generic.ISet<string>? onlyLibs, PluginConfiguration config, bool upload, bool scopedOnly, bool precheck = false, System.Collections.Generic.ISet<string>? onlyItems = null)
    {
        if (_userStopActive)
        {
            return true; // F-M131: user stop — no further seeding/running
        }
        // F-M331 (operator order 08.10.2026): the download switch and a spent allowance are the
        // SAME kind of gate here — both mean "this direction does no work this cycle": no queue
        // fill, no run, items stay due, and the direction still writes its own row.
        bool heldByAllowance = !upload && _downloadAllowanceHeld != null;
        if (upload ? !config.UploadEnabled : (!config.DownloadEnabled || heldByAllowance))
        {
            if (heldByAllowance)
            {
                SetDirectionOutcome(upload: false, Registry.WorkerRunRegistry.Outcome.Deferred, _downloadAllowanceHeld!);
                LogUtil.Normal(_logger, "[SubDL-Dispatch] download held back — {Reason}. Queued items stay due (F-M331).", _downloadAllowanceHeld);
            }

            return true; // toggle off (or no allowance) = direction permanently "done" for this cycle
        }
        bool seeded = await SeedAsync(trigger, onlyLibs, upload ? CycleDirection.UploadOnly : CycleDirection.DownloadOnly, precheck, onlyItems, downloadAllowed: _downloadAllowanceHeld == null).ConfigureAwait(false);
        if (!seeded)
        {
            return false; // seed lock busy (run active) — next event/anchor retries
        }

        if (upload && _upQuotaStopped) return true;
        if (!upload && _downQuotaStopped) return true;

        var fresh = upload ? _lastSeedNewIdsUp : _lastSeedNewIdsDown;

        // F-M233: an arrival cycle runs ONLY when its own arrivals produced new work.
        if (ShouldSkipRunForArrivalScope(scopedOnly, fresh.Count))
        {
            LogUtil.PerItem(config.LogMode, _logger, "[SubDL-Dispatch] no arrivals queued for {Dir} — no run.", upload ? "upload" : "download");
            return true; // nothing new this round — direction ends
        }

        var queueAll = upload ? _uploadQueue : _downloadQueue;
        if (queueAll.Any(x => x.State == QueueItem.ItemState.Queued))
        {
            // F-M233: the run is narrowed to the freshly queued arrivals, so leftover
            // queued work of earlier runs is not swept along by an arrival event.
            var scopeIds = scopedOnly ? new System.Collections.Generic.HashSet<string>(fresh, System.StringComparer.OrdinalIgnoreCase) : null;
            await RunDirectionAsync(upload: upload, scopeIds: scopeIds).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// F-M177 (21.09.2026): follow-up reseed runs exactly once per cycle, and only
    /// when new library arrivals were collected while seed/download/upload were
    /// already running. The forced scan then seeds those items; if that adds new
    /// work to the queues, download runs first, then upload.
    /// </summary>
    private async Task RunFollowUpRoundsAsync(string trigger, System.Collections.Generic.ISet<string>? onlyLibs, PluginConfiguration config, CycleDirection dir, System.Collections.Generic.ISet<string>? onlyItems = null)
    {
        bool followUpDown = Plugin.Instance?.Configuration?.FollowUpRoundsDownload != false
            && dir != CycleDirection.UploadOnly;
        bool followUpUp = Plugin.Instance?.Configuration?.FollowUpRoundsUpload != false
            && dir != CycleDirection.DownloadOnly;

        if (!followUpDown && !followUpUp)
        {
            return;
        }

        bool hasPending;
        System.Collections.Generic.ISet<string>? followUpItems;
        lock (_lock)
        {
            hasPending = (_pendingDuringCycleIds?.Count ?? 0) > 0;

            // F-M233: the follow-up works what arrived while the cycle was running.
            // That is a LATER window than the one the cycle started with, so its own
            // set wins; without one the inherited scope stays.
            followUpItems = hasPending
                ? new System.Collections.Generic.HashSet<string>(_pendingDuringCycleIds!, System.StringComparer.OrdinalIgnoreCase)
                : onlyItems;
        }

        if (!hasPending)
        {
            LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger, "[SubDL-Dispatch] no during-cycle arrivals — skip follow-up reseed.");
            return;
        }

        LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger, "[SubDL-Dispatch] during-cycle arrivals pending — one follow-up reseed.");

        if (followUpDown && config.DownloadEnabled)
        {
            if (!await SeedAndMaybeRunAsync(trigger, onlyLibs, config, upload: false, scopedOnly: true, precheck: true, onlyItems: followUpItems).ConfigureAwait(false))
            {
                return;
            }
        }

        if (followUpUp && config.UploadEnabled)
        {
            if (!await SeedAndMaybeRunAsync(trigger, onlyLibs, config, upload: true, scopedOnly: true, precheck: true, onlyItems: followUpItems).ConfigureAwait(false))
            {
                return;
            }
        }

        LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger, "[SubDL-Dispatch] follow-up cycle done.");
    }


    /// <summary>
    /// Runs one direction against its queue: the pipeline gets the queued ids as
    /// QueueFilter and reports per-item outcomes via ItemResult → bookkeeping here.
    /// </summary>
    /// <summary>
    /// This direction's own one-line result, for its worker row.
    /// <para>
    /// Only the parts that happened are named, so a quiet run reads as quiet instead of as a row of
    /// zeroes. A run that never reached the pipeline reports that instead of borrowing a neighbour's
    /// sentence.
    /// </para>
    /// </summary>
    /// <param name="upload">Direction being described.</param>
    /// <param name="upSummary">Upload run summary, when the upload leg produced one.</param>
    /// <param name="downSummary">Download run summary, when the download leg produced one.</param>
    /// <returns>Short detail text for the direction's row.</returns>
    private static string DescribeDirectionRun(bool upload, Pipeline.RunSummary? upSummary, Pipeline.DownloadRunSummary? downSummary)
    {
        var parts = new System.Collections.Generic.List<string>(3);
        if (upload)
        {
            if (upSummary == null)
            {
                return "no run — nothing queued";
            }

            if (upSummary.Uploaded > 0)
            {
                parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} uploaded", upSummary.Uploaded));
            }

            if (upSummary.Failed > 0)
            {
                parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} failed", upSummary.Failed));
            }

            if (upSummary.RejectedCandidates > 0)
            {
                parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} rejected", upSummary.RejectedCandidates));
            }

            // Operator finding 08.10.2026, live on prod: a run that EXAMINED five items and refused
            // every one of them ("no-imdb/no-season-ep") read as "nothing to do". A row that hides the
            // work it did is worse than a short one — the operator reads the row to know whether the
            // worker looked at anything.
            //
            // The counter is FilesSkipped, NOT SkippedItems. Measured on the source: the upload
            // increments SkippedItems on exactly two paths (file-complete, dir/file filter) while
            // every refusal path increments FilesSkipped — the no-imdb ladder, the exhausted id
            // budget, no-surviving-stream, and the item that had candidates but uploaded none. A row
            // reading SkippedItems therefore printed nothing for the very run that exposed this,
            // which is how the first attempt at this fix taught nothing.
            if (upSummary.FilesSkipped > 0)
            {
                parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} skipped", upSummary.FilesSkipped));
            }
        }
        else
        {
            if (downSummary == null)
            {
                return "no run — nothing queued";
            }

            if (downSummary.Downloaded > 0)
            {
                parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} downloaded", downSummary.Downloaded));
            }

            if (downSummary.Failed > 0)
            {
                parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} failed", downSummary.Failed));
            }

            if (downSummary.NotAvailable > 0)
            {
                parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} unavailable", downSummary.NotAvailable));
            }

            if (downSummary.RejectedCandidates > 0)
            {
                parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} rejected", downSummary.RejectedCandidates));
            }

            // Same finding as the upload side: a run that looked at items and refused them all must
            // not read as "nothing to do". Here SkippedItems IS the counter for it — the download
            // increments it on ten refusal paths, including the no-id and filter paths.
            if (downSummary.SkippedItems > 0)
            {
                parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} skipped", downSummary.SkippedItems));
            }
        }

        return parts.Count == 0 ? "nothing to do" : string.Join(", ", parts);
    }

    /// <summary>
    /// Runs one direction against its queue. Orchestrates setup, execution,
    /// result bookkeeping and cleanup.
    /// </summary>
    private async Task RunDirectionAsync(bool upload, System.Collections.Generic.HashSet<string>? scopeIds = null)
    {
        if (_userStopActive)
        {
            return;
        }
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        if (plugin == null || config == null)
        {
            return;
        }

        bool enabled = upload ? config.UploadEnabled : config.DownloadEnabled;
        if (!enabled)
        {
            return;
        }

        if (!TryAcquireRunLock(upload))
        {
            return;
        }

        try
        {
            var (queue, filter, retriesAtStart) = PrepareDirectionRun(upload, scopeIds);
            if (filter.Count == 0)
            {
                return;
            }

            // This direction's run really starts HERE — mark its own row running only now. See
            // RunCycleAsync for why the cycle no longer lights both directions up front. The row is
            // started with no name so a row that has never run keeps its stored display name.
            MarkDirectionRunning(upload);

            var progress = new Progress<double>();
            var (upSummary, downSummary) = await ExecutePipelineAsync(upload, config, filter, progress).ConfigureAwait(false);
            // If this direction was stopped by marker, stop the whole cycle.
            // F-M293 (user decision 03.10.2026): gated on the MARKER, not on StopRequested alone.
            // A shutdown also leaves StopRequested true (the pipeline keeps its partial result and
            // flags the stop), and this call sets Cancelled/"user stop" for BOTH directions — so a
            // restart at 14:18 painted the download as a user stop although its run had finished
            // clean ten minutes earlier, and the upload as stopped by a button nobody pressed.
            // Measured on prod: 'cycle end ("event", dir Both) — user stop.' was logged for a
            // restart, which is the line the user then read off the page.
            if ((upload && upSummary?.StopRequested == true
                    && Pipeline.PipelineStopSignal.HasMarker(Plugin.Instance?.DataFolderPath ?? string.Empty, upload: true))
                || (!upload && downSummary?.StopRequested == true
                    && Pipeline.PipelineStopSignal.HasMarker(Plugin.Instance?.DataFolderPath ?? string.Empty, upload: false)))
            {
                RequestUserStop($"stop marker {(upload ? "upload" : "download")}");
            }
            // F-M207: single writer for the statistics row; the dry-run filter lives in StatusCounterDelta.
            ApplyStatusCounters(config, upSummary, downSummary);
            // F-M322 (operator order 08.10.2026): the auto-sync's own worker row, written here because
            // this is the only place that HOLDS the download run's summary — the waiting download task
            // never sees it. Its light answers the question the Download row cannot: did the alignment
            // work? Colour rule F-M268, and a dry run reports grey/`skipped` because a dry run aligns
            // nothing (F-M247: a dry run contributes nothing to the statistics).
            RecordAutoSyncRow(config, downSummary);
            // Operator order 08.10.2026 ("jeder worker meldet nur sich selbst"): this direction's row
            // states what THIS direction did, built from the run summary only this method holds —
            // the waiting task never sees it and had to borrow the seeder's sentence instead.
            // Deliberately BEFORE CleanupDirectionQueue: that call overwrites both fields when the run
            // was quota-stopped, and a quota stop is this direction's own stronger fate.
            SetDirectionDetail(upload, DescribeDirectionRun(upload, upSummary, downSummary));
            CleanupDirectionQueue(upload, queue, filter, retriesAtStart, upSummary, downSummary);
        }
        catch (OperationCanceledException)
        {
            // Not a user stop (those are absorbed in ExecutePipelineAsync) — a real abort.
            SetDirectionOutcome(upload, Registry.WorkerRunRegistry.Outcome.Cancelled, "run aborted");
            throw;
        }
        catch (Exception ex)
        {
            // RED for THIS direction. Without this the exception only reached the dispatcher's log
            // line, and the waiting task reported "ok" for a cycle that threw — live 30.09.2026
            // 17:22:39: "scheduled-upload cycle failed" while the GUI showed the upload as ok.
            SetDirectionOutcome(upload, Registry.WorkerRunRegistry.Outcome.Failed, ex.Message);
            throw;
        }
        finally
        {
            // The direction's OWN row, written from its own fate — start and finish, one worker.
            // In the finally so a failure or a stop still closes the row it opened; a direction that
            // never began leaves the row untouched (see FinishDirectionRow).
            FinishDirectionRow(upload);
            lock (_lock)
            {
                _runActive = false;
            }
        }
    }

    /// <summary>
    /// Acquires the global run lock for one direction.
    /// </summary>
    private bool TryAcquireRunLock(bool upload)
    {
        lock (_lock)
        {
            if (_runActive)
            {
                _logger.LogWarning("[SubDL-Dispatch] {Dir} skipped — spacing.", upload ? "Upload" : "Download");
                return false;
            }

            _runActive = true;
            return true;
        }
    }

    /// <summary>
    /// Builds the snapshot of queued items for this direction run.
    /// </summary>
    private (List<QueueItem> queue, System.Collections.Generic.HashSet<string> filter, System.Collections.Generic.Dictionary<string, int> retriesAtStart)
        PrepareDirectionRun(bool upload, System.Collections.Generic.HashSet<string>? scopeIds)
    {
        var queue = upload ? _uploadQueue : _downloadQueue;
        var queued = scopeIds == null
            ? queue.Where(x => x.State == QueueItem.ItemState.Queued).ToList()
            : queue.Where(x => x.State == QueueItem.ItemState.Queued && scopeIds.Contains(x.ItemId)).ToList();
        // 12.09.2026 (user decision, end-of-noodle-loop): snapshot the retry
        // counters — items still Queued with an UNCHANGED count after the run
        // were skipped without any outcome (refetch gap, nothing missing,
        // FILE SKIP) → neutral-done and removed below.
        var retriesAtStart = queued.ToDictionary(x => x.ItemId, x => x.Retries);
        var filter = queued.Select(x => x.ItemId).ToHashSet();
        return (queue, filter, retriesAtStart);
    }

    /// <summary>
    /// Executes the upload or download pipeline and returns its summary.
    /// Handles user-stop cancellation and lock-loss recovery scheduling.
    /// </summary>
    private async Task<(Pipeline.RunSummary? up, Pipeline.DownloadRunSummary? down)> ExecutePipelineAsync(
        bool upload, PluginConfiguration config, System.Collections.Generic.HashSet<string> filter, IProgress<double> progress)
    {
        if (upload)
        {
            var bundle = PluginServiceRegistrator.BuildPipeline(_loggerFactory, _libraryManager, _mediaSourceManager, config);
            bundle.Pipeline.QueueFilter = filter;
            bundle.Pipeline.IsArrivalRun = false;
            bundle.Pipeline.ItemResult += (id, outcome) => OnUploadItemResult(_uploadQueue, id, outcome);
            _upInFlightDryRun = config.DryRun; // F-M22: the outcome handler consults this per item
            try
            {
                using var stopLinkedUp = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _stopCts.Token); // F-M131
                var upSummary = await bundle.Pipeline.RunAsync(progress, stopLinkedUp.Token).ConfigureAwait(false);
                if (upSummary.SkippedByLock)
                {
                    ScheduleLockRetry(config, upload: true);
                    return (null, null);
                }
                return (upSummary, null);
            }
            catch (OperationCanceledException) when (_userStopActive)
            {
                LogUtil.Normal(_logger, "[SubDL] Upload run aborted — user stop (F-M131).");
                _upQuotaStopped = true;
                return (null, null);
            }
            finally
            {
                _upInFlightDryRun = false; // F-M22: the flag lives exactly as long as the run
                PluginServiceRegistrator.DisposePipeline(bundle);
                // F-M17y removed: postprocessing now runs on its own schedule (F-M176+).
                // It is no longer auto-triggered after every upload run.
            }
        }
        else
        {
            var bundle = PluginServiceRegistrator.BuildDownloadPipeline(_loggerFactory, _libraryManager, _mediaSourceManager, config);
            bundle.Pipeline.QueueFilter = filter;
            bundle.Pipeline.IsArrivalRun = false;
            bundle.Pipeline.ItemResult += (id, outcome) => OnItemResult(_downloadQueue, id, outcome);
            _downInFlightDryRun = config.DownloadDryRun; // F-M22: the outcome handler consults this per item
            try
            {
                using var stopLinkedDown = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _stopCts.Token); // F-M131
                var downSummary = await bundle.Pipeline.RunAsync(progress, stopLinkedDown.Token).ConfigureAwait(false);
                if (downSummary.SkippedByLock)
                {
                    ScheduleLockRetry(config, upload: false);
                    return (null, null);
                }
                return (null, downSummary);
            }
            catch (OperationCanceledException) when (_userStopActive)
            {
                LogUtil.Normal(_logger, "[SubDL-D] Download run aborted — user stop (F-M131).");
                _downQuotaStopped = true;
                return (null, null);
            }
            finally
            {
                _downInFlightDryRun = false; // F-M22: the flag lives exactly as long as the run
                PluginServiceRegistrator.DisposeDownloadPipeline(bundle);
            }
        }
    }

    /// <summary>
    /// A run that lost the global lock re-fires itself via the scheduler.
    /// </summary>
    private void ScheduleLockRetry(PluginConfiguration config, bool upload)
    {
        var fireAt = DateTime.UtcNow.AddMinutes(Math.Max(1, config.JobSpacingMinutes));
        ScheduledTasks.SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFireAt(upload, fireAt);
        _logger.LogWarning("[SubDL] {Dir} skipped by global run lock — self re-fire at {FireLocal} local (F-M146).", upload ? "Upload" : "Download", fireAt.ToLocalTime());
    }

    /// <summary>
    /// F-M23: updates the persisted cumulative status counters from pipeline summaries.
    /// <para>
    /// Stored in the database since 25.09.2026 (was: plugin configuration XML). The XML sits next to
    /// user settings while the data being counted lives in the database, so a database reset used to
    /// leave the counters behind and the display drifted from the data (measured 25.09.2026: 311
    /// uploaded rows in the database, 743 in the display). Writing them here keeps both in step.
    /// </para>
    /// </summary>
    /// <summary>
    /// F-M322 (operator order 08.10.2026): writes the auto-sync's own worker row for the download run.
    /// <para>
    /// The light answers ONE question — did the alignment work — and the operator asked for it as its
    /// own line, both in the Workers list and under the download switch. It uses the SAME status words
    /// and colours as every other worker (F-M268), so nothing new had to be invented for it:
    /// </para>
    /// <list type="bullet">
    /// <item>GREEN (`ok`) — the run aligned at least one subtitle, or found every candidate it measured
    /// already in sync (F-M321). Both are the work DONE; the statistics counter, by the operator's own
    /// order, covers only the files that were really MOVED.</item>
    /// <item>YELLOW/GREY (`skipped`) — the alignment is switched off, or the run measured nothing at
    /// all. Nothing was asked of it and nothing is broken.</item>
    /// <item>RED (`failed`) — the run reported failures and aligned nothing.</item>
    /// </list>
    /// <para>
    /// A dry run reports skipped with its own note: a dry run writes no file, so it cannot have aligned
    /// anything, and a green light would claim a correction that exists nowhere (F-M247).
    /// </para>
    /// </summary>
    /// <param name="config">Live configuration.</param>
    /// <param name="downSummary">The download run's summary, or null when this direction is the UPLOAD:
    /// the auto-sync belongs to the download run, and an upload run holds no alignment result.</param>
    private void RecordAutoSyncRow(PluginConfiguration config, Pipeline.DownloadRunSummary? downSummary)
    {
        var runs = Plugin.Instance?.WorkerRuns;
        if (runs == null)
        {
            return;
        }

        // The row belongs to the DOWNLOAD run. ExecutePipelineAsync returns (upSummary, null) for the
        // upload direction, so an upload run arrives here with no summary at all — guarded rather than
        // documented, because the earlier "never null at the call site" was written from the download
        // path alone and the NRE it hid repainted BOTH lamps red for a cycle whose download and upload
        // had just finished green (live 08.10.2026 18:21, prod and test alike). Keeping the download's
        // own verdict is the honest reading: this cycle did no alignment work to report.
        if (downSummary == null)
        {
            return;
        }

        if (downSummary.IsDryRun)
        {
            runs.Finish(
                Registry.WorkerRunRegistry.AutoSyncWorkerKey,
                Registry.WorkerRunRegistry.Name,
                Registry.WorkerRunRegistry.Outcome.Skipped,
                "dry run — nothing aligned",
                dryRun: true);
            return;
        }

        int aligned = downSummary.FittedToAudio;
        int alreadyGood = downSummary.AlreadyGoodAsDownloaded;

        if (!config.QaDownloadAutoSync)
        {
            runs.Finish(
                Registry.WorkerRunRegistry.AutoSyncWorkerKey,
                Registry.WorkerRunRegistry.Name,
                Registry.WorkerRunRegistry.Outcome.Skipped,
                "alignment switched off");
            return;
        }

        if (aligned > 0 || alreadyGood > 0)
        {
            string detail = aligned > 0
                ? $"{aligned} aligned, {alreadyGood} already in sync ({downSummary.FitMsTotal / 1000.0:0.#}s)"
                : $"{alreadyGood} already in sync — nothing to correct";
            runs.Finish(Registry.WorkerRunRegistry.AutoSyncWorkerKey, Registry.WorkerRunRegistry.Name, Registry.WorkerRunRegistry.Outcome.Ok, detail);
            return;
        }

        // F-M323: the row goes red for ITS OWN breakdown only. It used to read downSummary.Failed —
        // the DOWNLOAD's failure count — so an unrelated network error painted the alignment red,
        // a claim the alignment never made. Refusals and already-good files are outcomes, not
        // breakdowns, so neither reaches this branch.
        if (downSummary.AutoSyncFailed > 0)
        {
            runs.Finish(
                Registry.WorkerRunRegistry.AutoSyncWorkerKey,
                Registry.WorkerRunRegistry.Name,
                Registry.WorkerRunRegistry.Outcome.Failed,
                $"{downSummary.AutoSyncFailed} measurement(s) failed, nothing aligned");
            return;
        }

        runs.Finish(
            Registry.WorkerRunRegistry.AutoSyncWorkerKey,
            Registry.WorkerRunRegistry.Name,
            Registry.WorkerRunRegistry.Outcome.Skipped,
            "nothing to align this run");
    }

    /// <summary>
    /// F-M23: updates the persisted cumulative status counters from pipeline summaries.
    /// <para>
    /// Stored in the database since 25.09.2026 (was: plugin configuration XML). The XML sits next to
    /// user settings while the data being counted lives in the database, so a database reset used to
    /// leave the counters behind and the display drifted from the data (measured 25.09.2026: 311
    /// uploaded rows in the database, 743 in the display). Writing them here keeps both in step.
    /// </para>
    /// </summary>
    /// F-M207: the ONE writer of the cumulative counters; a database reset resets them with it.
    /// <param name="config">Live configuration.</param>
    /// <param name="upSummary">The upload run's summary, or null.</param>
    /// <param name="downSummary">The download run's summary, or null.</param>
    private void ApplyStatusCounters(
        PluginConfiguration config,
        Pipeline.RunSummary? upSummary,
        Pipeline.DownloadRunSummary? downSummary)
    {
        // F-M311: the seeder's language-code writes belong to this run. Consumed here so the second
        // direction of the same cycle cannot report them a second time.
        long langAllocated = _pendingLanguageCodesAllocated;
        _pendingLanguageCodesAllocated = 0;
        long looseRenamed = _pendingLooseSubtitlesRenamed;
        _pendingLooseSubtitlesRenamed = 0;

        // F-M247 (user decision 28.09.2026: "Dryrun geht nie in die Statistik"): the dry-run
        // filter lives in StatusCounterDelta.From, not here — this method stays a plain
        // write, and the rule sits in one testable place. A dry run does real work (search,
        // ranking, id test, QA) and so fills its summary, but it writes nothing to SubDL or
        // to disk; counting it reported subtitles nobody ever wrote. Live 28.09.2026: 771 of
        // the 858 "downloaded" entries came from one afternoon of dry runs.
        var delta = Pipeline.StatusCounterDelta.From(upSummary, downSummary, langAllocated, looseRenamed);
        if (delta.IsEmpty)
        {
            return;
        }

        try
        {
            Plugin.Instance!.AddStatusCounters(
                delta.Uploaded,
                delta.Downloaded,
                delta.RejectedDownload,
                delta.RejectedUpload,
                delta.FittedToAudio,
                delta.LanguageCodesAllocated,
                delta.LooseSubtitlesRenamed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubDL] failed to update the status counters.");
        }
    }

    /// <summary>
    /// Marks neutral-done items and removes finished items from the queue file.
    /// </summary>
    private void CleanupDirectionQueue(
        bool upload,
        List<QueueItem> queue,
        System.Collections.Generic.HashSet<string> filter,
        System.Collections.Generic.Dictionary<string, int> retriesAtStart,
        Pipeline.RunSummary? upSummary,
        Pipeline.DownloadRunSummary? downSummary)
    {
        // Umbau 12.09.2026 (user decision): Done/Purged leave the queue file
        // IMMEDIATELY — the queue holds only open work. Items without any
        // reported outcome (skipped/no-op) are neutral-done: removed too.
        // 13.09.2026 fix: EXCEPT on a quota-stopped run — unprocessed items must
        // stay Queued, otherwise they are silently dropped.
        // Rev.2: same for user-stop runs.
        bool quotaStopped = (upSummary?.QuotaStopped ?? 0) > 0 || (downSummary?.QuotaStopped ?? 0) > 0;
        bool stopRequested = upSummary?.StopRequested == true || downSummary?.StopRequested == true;
        if (upload) { _upQuotaStopped = quotaStopped; } else { _downQuotaStopped = quotaStopped; }

        // Record the fate so the waiting task reports it instead of a blanket "ok". Quota/rate limit
        // = deferred: the cycle finished, the work did not happen and is scheduled again.
        if (quotaStopped)
        {
            SetDirectionOutcome(
                upload,
                Registry.WorkerRunRegistry.Outcome.Deferred,
                upload ? "upload quota/rate limit — rescheduled" : "download quota/rate limit — rescheduled");
        }
        // F-M22 (defect fixed 02.10.2026): a dry run must not take work away. This block used to
        // mark every processed item `Done` and — via the RemoveAll below — delete it from
        // `cycle-queues.json` on DISK. A dry run reports outcomes for items it never worked (it sets
        // ItemOutcome.Done and counts a "would have saved"), so the next real run found those items
        // gone from the queue instead of waiting for them: the report-only mode consumed the work it
        // was describing. The queue is a stored statement about what is still open, and F-M22 allows
        // no stored verdict to be written or cleared by a dry run. With this guard the items stay
        // `Queued` and the next real run works them.
        //
        // Per direction: at most one of the two summaries is the run that just finished. The
        // worker-run status above is NOT held back — a dry run did run, and recording that it ran is
        // a truthful transparency record about the WORKER, not a stored verdict about an item.
        bool dryRun = upload ? upSummary?.IsDryRun == true : downSummary?.IsDryRun == true;

        if (!quotaStopped && !stopRequested && !dryRun)
        {
            foreach (var qi in queue)
            {
                if (qi.State == QueueItem.ItemState.Queued
                    && filter.Contains(qi.ItemId)
                    && retriesAtStart.TryGetValue(qi.ItemId, out var startRetries)
                    && qi.Retries == startRetries)
                {
                    qi.State = QueueItem.ItemState.Done;
                }
            }
        }

        int removed = dryRun ? 0 : queue.RemoveAll(x => x.State != QueueItem.ItemState.Queued);
        if (dryRun)
        {
            LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger,
                "[SubDL-Dispatch] DRY RUN — {Count} item(s) left Queued in the {Queue} queue (F-M22: a dry run removes no work).",
                queue.Count(x => x.State == QueueItem.ItemState.Queued), upload ? "upload" : "download");
        }
        else if (removed > 0)
        {
            LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger,"[SubDL-Dispatch] {Count} finished items removed from {Queue} queue.", removed, upload ? "upload" : "download");
        }

        SaveQueues();
    }

    /// <summary>
    /// Retry bookkeeping (user decision 12.09.2026):
    ///   Done → leaves the queue. RealFailure → stays, retry++, purge at limit.
    ///   NotAvailable → stays, counter untouched (refetch brings results).
    /// </summary>
    private void OnItemResult(List<QueueItem> queue, string itemId, Pipeline.DownloadPipeline.ItemOutcome outcome)
    {
        var qi = queue.FirstOrDefault(x => x.ItemId == itemId);
        if (qi == null || qi.State != QueueItem.ItemState.Queued)
        {
            return;
        }

        switch (outcome)
        {
            case Pipeline.DownloadPipeline.ItemOutcome.Done:
                // F-M22 (defect fixed 02.10.2026): this is where the real damage happened. A dry run
                // reports ItemOutcome.Done for items it only DESCRIBED (it sets `savedAny = true` and
                // counts a "would have saved"), and this handler marked them `Done` immediately —
                // live, not at cleanup. CleanupDirectionQueue then deleted them from
                // `cycle-queues.json`. So one dry run emptied the queue of every item it reported,
                // and the next real run had nothing to work: the report-only mode consumed the work
                // it was describing. F-M22: "what a dry run leaves behind must not change what a
                // later run finds."
                if (_upInFlightDryRun) { break; }
                ApplyItem(queue, itemId, QueueItem.ItemState.Done);
                break;

            case Pipeline.DownloadPipeline.ItemOutcome.NotAvailable:
                // NOT a retry — the refetch interval delegates this (user decision).
                break;

            case Pipeline.DownloadPipeline.ItemOutcome.RealFailure:
                ApplyItem(queue, itemId, QueueItem.ItemState.Queued, bumpRetry: true);
                break;
        }
    }

    private void OnUploadItemResult(List<QueueItem> queue, string itemId, Pipeline.UploadPipeline.ItemOutcome outcome)
        => ApplyOutcomeUpload(queue, itemId, outcome);

    private void ApplyOutcomeUpload(List<QueueItem> queue, string itemId, Pipeline.UploadPipeline.ItemOutcome outcome)
    {
        switch (outcome)
        {
            case Pipeline.UploadPipeline.ItemOutcome.Done:
                // F-M22: same as the download side — a dry upload reports Done for items it only
                // described, and that must not remove them from the queue.
                if (_upInFlightDryRun) { break; }
                ApplyItem(queue, itemId, QueueItem.ItemState.Done, bumpRetry: false);
                break;
            case Pipeline.UploadPipeline.ItemOutcome.RealFailure:
                ApplyItem(queue, itemId, QueueItem.ItemState.Queued, bumpRetry: true);
                break;
            default:
                break; // NotAvailable: no retry semantics on upload
        }
    }

    private void ApplyItem(List<QueueItem> queue, string itemId, QueueItem.ItemState state, bool bumpRetry)
    {
        var qi = queue.FirstOrDefault(x => x.ItemId == itemId);
        if (qi == null || qi.State != QueueItem.ItemState.Queued)
        {
            return;
        }

        if (bumpRetry)
        {
            qi.Retries++;
            if (qi.Retries >= MaxRetries)
            {
                qi.State = QueueItem.ItemState.Purged;
                _purgedIds.Add(qi.ItemId);
                _purgedDirty = true;
                _logger.LogWarning("[SubDL-Dispatch] Retry limit ({Max}) — purged: {Name}", MaxRetries, qi.Name);
            }
        }
        else if (state == QueueItem.ItemState.Done)
        {
            qi.State = QueueItem.ItemState.Done;
        }

        qi.ChangedUtc = DateTime.UtcNow;
    }

    private void ApplyItem(List<QueueItem> queue, string itemId, QueueItem.ItemState state)
    {
        ApplyItem(queue, itemId, state, bumpRetry: false);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  DIRECTION FATE (for the two wait-only scheduled tasks)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the last cycle did to one direction: <c>deferred</c> when the quota/rate limit stopped
    /// it or the run lock pushed it forward, <c>cancelled</c> on a user stop, null when the
    /// direction ran without a special fate.
    /// </summary>
    /// <param name="upload">True for the upload direction.</param>
    /// <returns>Outcome word and detail, both possibly null.</returns>
    public (string? Outcome, string? Detail) GetDirectionOutcome(bool upload)
        => upload ? (_dirOutcomeUp, _dirDetailUp) : (_dirOutcomeDown, _dirDetailDown);

    /// <summary>
    /// The seeder's fate in the cycle that just ended, or null when it reported none. Read from
    /// memory rather than the database row: a pre-check skip leaves the stored row untouched, so
    /// the row can still carry the PREVIOUS cycle's fate.
    /// </summary>
    /// <returns>Outcome word and detail, both possibly null.</returns>
    public (string? Outcome, string? Detail) GetSeederOutcome() => (_seederOutcome, _seederDetail);

    // F-M331 (operator order 08.10.2026): "Api limit oder download limit voll, downloader startet
    // erst garnicht." The reason the download direction may not be filled/run this cycle, or null
    // when it may. Read once per cycle before the download seed and consulted exactly where
    // `config.DownloadEnabled` is consulted, so a spent allowance acts like the download switch
    // being off rather than like a decision the seeder makes.
    private string? _downloadAllowanceHeld;

    // True when THIS cycle already wrote a direction's own row (start AND finish). The waiting
    // task and the arrival path then leave that row alone — see FinishDirectionRow.
    private bool _dirRowWrittenUp;
    private bool _dirRowWrittenDown;

    // The current cycle's seeder accounting, one entry per leg. See PrepareCycleState for why.
    private int _seedLegsScanned;
    private bool _seedCoveredUp;
    private bool _seedCoveredDown;
    private int _seedQueuedUp;
    private int _seedQueuedDown;

    /// <summary>
    /// The seeder row's detail for the cycle so far, naming ONLY the directions the cycle's scans
    /// actually covered.
    /// <para>
    /// A gated scan is handed one direction and never fills the foreign queue, so formatting both
    /// counts unconditionally printed a structural `0` for the direction it never looked at — read
    /// as "nothing to do" when the truth is "not asked" (operator order 08.10.2026).
    /// </para>
    /// </summary>
    /// <returns>Detail text such as "5 upload queued" or "5 upload, 3 download queued".</returns>
    private string SeederQueuedDetail()
    {
        var parts = new System.Collections.Generic.List<string>(2);
        if (_seedCoveredUp)
        {
            parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} upload", _seedQueuedUp));
        }

        if (_seedCoveredDown)
        {
            parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} download", _seedQueuedDown));
        }

        return parts.Count == 0 ? "scan finished" : string.Join(", ", parts) + " queued";
    }

    /// <summary>
    /// Whether THIS cycle already wrote the given direction's row itself (start and finish).
    /// <para>
    /// The waiting scheduled tasks and the arrival path both used to write these rows after the
    /// whole cycle, which is why a direction stayed blue while its SIBLING ran: the download leg
    /// ended, nobody finished its row, and the upload leg then lit a second lamp. A row written by
    /// the direction itself is complete and must not be overwritten with a cycle-level summary.
    /// </para>
    /// </summary>
    /// <param name="upload">Direction to ask about.</param>
    /// <returns>True when this cycle already wrote that row.</returns>
    public bool DirectionRowWritten(bool upload) => upload ? _dirRowWrittenUp : _dirRowWrittenDown;

    /// <summary>
    /// Writes a direction's row from ITS OWN result, start to finish — the row states what this
    /// direction did and nothing else.
    /// <para>
    /// Operator order 08.10.2026 ("jeder worker meldet nur sich selbst"; "seeder gets blue only,
    /// download only download, upload only upload — as it is successive"). Called from
    /// <see cref="RunDirectionAsync"/>'s finally, so it runs on the success path, on a failure and
    /// on a cancellation; a direction that had nothing to say (its queue was empty, so no run ever
    /// began) leaves the row to the waiting task exactly as before.
    /// </para>
    /// </summary>
    /// <param name="upload">Direction whose run just ended.</param>
    private void FinishDirectionRow(bool upload)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        var (outcome, detail) = GetDirectionOutcome(upload);
        if (outcome == null && detail == null)
        {
            return; // nothing happened in this direction — not this writer's row to claim
        }

        // A run that finished with nothing special to report is GREEN with its own one-line result;
        // a quota stop, a user stop or a failure keeps its own stronger word.
        var word = outcome ?? Registry.WorkerRunRegistry.Outcome.Ok;
        var text = detail ?? "cycle finished";
        var dryRun = upload ? plugin.Configuration.DryRun : plugin.Configuration.DownloadDryRun;
        if (upload)
        {
            plugin.WorkerRuns.Finish(Registry.WorkerRunRegistry.UploadWorkerKey, "Upload", word, text, dryRun);
            _dirRowWrittenUp = true;
        }
        else
        {
            plugin.WorkerRuns.Finish(Registry.WorkerRunRegistry.DownloadWorkerKey, "Download", word, text, dryRun);
            _dirRowWrittenDown = true;
        }
    }

    /// <summary>
    /// Marks ONE direction's row as running, because that direction's own run is starting.
    /// <para>
    /// Operator order 08.10.2026: "seeder gets blue only, download only download, upload only
    /// upload — as it is successive." A direction must therefore never light up for work the seeder
    /// or its sibling is doing. Called from <see cref="RunDirectionAsync"/> at the moment the
    /// direction's own queue is prepared, not when the enclosing cycle starts.
    /// </para>
    /// </summary>
    /// <param name="upload">Direction whose run is starting.</param>
    private static void MarkDirectionRunning(bool upload)
    {
        var runs = Plugin.Instance?.WorkerRuns;
        if (runs == null)
        {
            return;
        }

        if (upload)
        {
            runs.Start(Registry.WorkerRunRegistry.UploadWorkerKey, "Upload");
        }
        else
        {
            runs.Start(Registry.WorkerRunRegistry.DownloadWorkerKey, "Download");
        }
    }

    /// <summary>
    /// Sets a direction's detail WITHOUT touching its outcome — for a run that ended green and has
    /// no special fate of its own. A fate recorded deliberately (a quota stop, a user stop) is
    /// never overwritten, because <see cref="SetDirectionOutcome"/> owns both fields together and
    /// <see cref="CleanupDirectionQueue"/> runs after this.
    /// </summary>
    /// <param name="upload">Direction to record.</param>
    /// <param name="detail">This direction's own one-line result.</param>
    private void SetDirectionDetail(bool upload, string detail)
    {
        if (upload)
        {
            if (_dirOutcomeUp == null)
            {
                _dirDetailUp = detail;
            }
        }
        else if (_dirOutcomeDown == null)
        {
            _dirDetailDown = detail;
        }
    }

    /// <summary>
    /// Records the seeder's fate both in memory (for the waiting task) and in the database row
    /// (for the configuration page). Every seeder path must call this, including the ones that
    /// never scan — otherwise the row keeps a stale outcome.
    /// </summary>
    /// <param name="outcome">One of the <see cref="Registry.WorkerRunRegistry.Outcome"/> values.</param>
    /// <param name="detail">Short human-readable summary.</param>
    private void RecordSeeder(string outcome, string detail)
    {
        // A Both cycle scans the download leg and then finds the upload leg unchanged, whose pre-check
        // reports grey "no changes — scan skipped". That write used to REPLACE the scan's own numbers,
        // so a row claiming to summarise the cycle dropped the leg that actually ran and read as if the
        // cycle had found nothing. Once a leg of THIS cycle has scanned, a later grey verdict is logged
        // but does not take the row over; the numbers stay until the cycle ends.
        if (outcome == Registry.WorkerRunRegistry.Outcome.Skipped && _seedLegsScanned > 0)
        {
            LogUtil.Normal(_logger, "[SubDL-Seed] leg skipped after a scan this cycle — seeder row keeps the scan's result ({Detail}).", detail);
            return;
        }

        _seederOutcome = outcome;
        _seederDetail = detail;
        Plugin.Instance?.WorkerRuns.Finish(Registry.WorkerRunRegistry.SeederKey, "Seeder", outcome, detail);
    }

    /// <summary>
    /// Writes the arrival cycle's direction rows, ranked by the same rule the waiting tasks use
    /// (<see cref="Registry.WorkerRunRegistry.DescribeCycle"/>), so a row reads identically whether
    /// an arrival or a schedule produced the cycle. Without it a quota stop or a failed arrival
    /// cycle would keep showing the previous run's green — exactly the blindness it fixes.
    /// </summary>
    /// <param name="dir">Direction this cycle works.</param>
    private void RecordArrivalDirectionFinish(CycleDirection dir)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        var config = plugin.Configuration;
        var (seedOutcome, seedDetail) = GetSeederOutcome();

        // A DISABLED direction is not a green one. The arrival path leaves a switched-off direction
        // untouched — it has nothing to report — so DescribeCycle would fall through to `ok` and paint
        // "nothing happened here" as "this ran fine". The scheduled task records `skipped`/"disabled"
        // for exactly that case; the arrival path has to say the same thing.
        // Operator order 08.10.2026: a direction that RAN writes its own row, start to finish, and
        // this cycle-level write must leave it alone. What is left here is the case the direction
        // cannot report itself: its run never began (switched off, or nothing queued), where the row
        // would otherwise keep a stale outcome from a previous cycle.
        if (dir != CycleDirection.UploadOnly && !_dirRowWrittenDown)
        {
            if (!config.DownloadEnabled)
            {
                plugin.WorkerRuns.Finish(
                    Registry.WorkerRunRegistry.DownloadWorkerKey, "Download",
                    Registry.WorkerRunRegistry.Outcome.Skipped, "disabled");
            }
            else
            {
                var (downOutcome, downDetail) = GetDirectionOutcome(upload: false);
                var (outcome, detail) = Registry.WorkerRunRegistry.DescribeCycle(
                    cycleFinished: true, seedOutcome, seedDetail, downOutcome, downDetail);
                plugin.WorkerRuns.Finish(
                    Registry.WorkerRunRegistry.DownloadWorkerKey, "Download", outcome, detail, config.DownloadDryRun);
            }
        }

        if (dir != CycleDirection.DownloadOnly && !_dirRowWrittenUp)
        {
            if (!config.UploadEnabled)
            {
                plugin.WorkerRuns.Finish(
                    Registry.WorkerRunRegistry.UploadWorkerKey, "Upload",
                    Registry.WorkerRunRegistry.Outcome.Skipped, "disabled");
            }
            else
            {
                var (upOutcome, upDetail) = GetDirectionOutcome(upload: true);
                var (outcome, detail) = Registry.WorkerRunRegistry.DescribeCycle(
                    cycleFinished: true, seedOutcome, seedDetail, upOutcome, upDetail);
                plugin.WorkerRuns.Finish(
                    Registry.WorkerRunRegistry.UploadWorkerKey, "Upload", outcome, detail, config.DryRun);
            }
        }
    }

    /// <summary>
    /// Marks a FAILED cycle on the directions that have no fate of their own yet.
    /// <para>
    /// Belt and braces for failures outside a pipeline run (queue merge, save, follow-up). A fate
    /// that was already recorded deliberately — a quota stop, a user stop — is left alone: those
    /// describe what happened BEFORE the exception, and overwriting them would hide the root cause.
    /// </para>
    /// </summary>
    /// <param name="message">The exception message.</param>
    private void MarkCycleFailure(string message)
    {
        if (_dirOutcomeDown == null)
        {
            SetDirectionOutcome(upload: false, Registry.WorkerRunRegistry.Outcome.Failed, message);
        }

        if (_dirOutcomeUp == null)
        {
            SetDirectionOutcome(upload: true, Registry.WorkerRunRegistry.Outcome.Failed, message);
        }
    }

    private void SetDirectionOutcome(bool upload, string outcome, string detail)
    {
        if (upload)
        {
            _dirOutcomeUp = outcome;
            _dirDetailUp = detail;
        }
        else
        {
            _dirOutcomeDown = outcome;
            _dirDetailDown = detail;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  SEEDER CALL + QUEUE MERGE
    // ────────────────────────────────────────────────────────────────────────

    /// <param name="downloadAllowed">
    /// Whether the download queue may be filled this scan — the plain flag the seeder takes, exactly
    /// like its own <c>config.DownloadEnabled</c>. Lowered when SubDL reports no download allowance
    /// left (F-M331); the seeder never asks the API itself.
    /// </param>
    private async Task<bool> SeedAsync(string reason, System.Collections.Generic.ISet<string>? onlyLibraries = null, CycleDirection dir = CycleDirection.Both, bool precheck = false, System.Collections.Generic.ISet<string>? onlyItemIds = null, bool downloadAllowed = true)
    {
        // F-M188/F-M290: no library selected = there is nothing this seeder could look at, so it does
        // not even start. The same condition is checked again inside Scan(), but by then the run lock
        // is held, the pre-check has already walked the library for change stamps and the database has
        // been touched — all of it to arrive at "nothing to queue". Checked here it costs one lookup.
        //
        // GREY, not green: a scan that was never allowed to run must not read like one that ran and
        // found nothing. The wording matches Scan()'s own line so the two cannot be told apart in the
        // log either.
        if (LibraryScope.Create(_libraryManager, Plugin.Instance?.Configuration?.SelectedLibraries, _logger).IsEmpty)
        {
            LogUtil.Normal(_logger, "[SubDL-Seed] No libraries selected — seeder not started ({Reason}).", reason);
            _lastSeedNewIdsUp.Clear();
            _lastSeedNewIdsDown.Clear();
            _lastSeedNewItems = 0;
            RecordSeeder(Registry.WorkerRunRegistry.Outcome.Skipped, "no libraries selected");
            return true; // nothing to seed; the directions have no work either
        }

        // Cross-run mutual exclusion via the single global block file (stale detection inside).
        if (!await Pipeline.PipelineRunLock.AcquireAsync(_logger, "seeder").ConfigureAwait(false))
        {
            // Overlap: another component holds the one global lock. Reschedule this
            // direction by JobSpacingMinutes instead of dropping the cycle
            // (rework 25.09.2026). Both-direction seeds arm each side separately.
            var spacingSeed = Math.Clamp(Plugin.Instance?.Configuration.JobSpacingMinutes ?? 15, 5, 120);
            if (dir != CycleDirection.UploadOnly)
            {
                SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFireAt(upload: false, DateTime.UtcNow.AddMinutes(spacingSeed));
            }

            if (dir != CycleDirection.DownloadOnly)
            {
                SubdlSchedulerCoordinator.Instance?.ScheduleRecoveryFireAt(upload: true, DateTime.UtcNow.AddMinutes(spacingSeed));
            }

            // Lock busy → the direction is pushed forward: a deferral, not a run. The seeder never
            // scanned, so it reports the same: YELLOW, because nothing is broken and it will retry.
            if (dir != CycleDirection.DownloadOnly)
            {
                SetDirectionOutcome(upload: true, Registry.WorkerRunRegistry.Outcome.Deferred, "run lock busy — rescheduled");
            }
            if (dir != CycleDirection.UploadOnly)
            {
                SetDirectionOutcome(upload: false, Registry.WorkerRunRegistry.Outcome.Deferred, "run lock busy — rescheduled");
            }
            RecordSeeder(Registry.WorkerRunRegistry.Outcome.Deferred, "run lock busy — rescheduled");
            LogUtil.Normal(_logger, "[SubDL-Seed] Deferred by {Spacing} min — global run lock busy.", spacingSeed);
            return false;
        }

        try
        {
            if (_runActive)
            {
                // Seeder never overlaps a run (user decision 12.09.2026) — the
                // reseed step of the running cycle catches up. Deferred, not skipped: the work
                // is pending, not switched off.
                RecordSeeder(Registry.WorkerRunRegistry.Outcome.Deferred, "run in progress — reseed catches up");
                return false;
            }

            var config = Plugin.Instance!.Configuration;

            // (19.09.2026): a during-cycle arrival forces the next seed
            // Of EACH direction to do one real scan (bypasses the
            // pre-check, whose DateCreated/DateModified anchor misses fresh
            // imports whose media probe completes after the last scan stamp).
            // The item itself is a normal scan candidate; the probe has had the
            // debounce/cycle-runtime to finish, so IsUploadTodo/IsDownloadTodo
            // see the final stream list. The list is drained AFTER a successful
            // scan below (early returns here must not lose it).
            bool forcedScan;
            bool drainDuringCycle;
            lock (_lock)
            {
                drainDuringCycle = (_pendingDuringCycleIds?.Count ?? 0) > 0;
            }

            // F-M233: an explicit arrival id set is itself a reason to scan — the
            // DateCreated/DateModified pre-check would otherwise skip the scan and the
            // arrivals would never be looked at (the second direction of a follow-up
            // cycle sees an already-drained during-cycle list).
            // NOTE: the drain stays tied to drainDuringCycle, NOT to forcedScan —
            // otherwise an arrival cycle would clear the during-cycle list it is
            // itself filling while its scan runs, losing mid-scan arrivals.
            forcedScan = drainDuringCycle || onlyItemIds != null;
            if (forcedScan)
            {
                LogUtil.Normal(_logger, "[SubDL-Seed] during-cycle arrival pending — forcing scan ({Reason}, dir {Dir}).", reason, dir);
            }

            // (15.09.2026): rounds 2..n run a cheap JF-DB pre-check
            // (newest item DateModified/DateCreated) against the last real
            // scan's start time; no change → skip the scan entirely. Round 1
            // and the first post-restart seed always scan (MinValue stamp).
            var lastStamp = dir == CycleDirection.UploadOnly ? _lastScanStampUpUtc
                          : dir == CycleDirection.DownloadOnly ? _lastScanStampDownUtc
                          : (_lastScanStampUpUtc > _lastScanStampDownUtc ? _lastScanStampUpUtc : _lastScanStampDownUtc);
            if (precheck && !forcedScan && lastStamp > DateTime.MinValue)
            {
                var newest = _seeder.MaxChangedUtc(config, onlyLibraries);
                if (newest <= lastStamp)
                {
                    _lastSeedNewIdsUp.Clear();
                    _lastSeedNewIdsDown.Clear();
                    _lastSeedNewItems = 0;
                    LogUtil.Detail(_logger, "[SubDL-Seed] pre-check: no changes since {Stamp:HH:mm:ss} — skipped ({Reason}).", lastStamp.ToLocalTime(), reason);
                    // GREY, not green: the seeder did not scan — it looked, found no change and had
                    // nothing to do. Reporting "ok" here would read as a full scan.
                    RecordSeeder(Registry.WorkerRunRegistry.Outcome.Skipped, "no changes — scan skipped");
                    return true;
                }
                LogUtil.Detail(_logger, "[SubDL-Seed] pre-check: changed {Newest:HH:mm:ss} — full scan ({Reason}).", newest.ToLocalTime(), reason);
            }

            DateTime scanStartedUtc = DateTime.UtcNow;
            LogUtil.Detail(_logger, "[SubDL-Seed] start ({Reason}, dir {Dir}).", reason, dir);
            _lastSeedNewIdsUp.Clear();
            _lastSeedNewIdsDown.Clear();
            // The seeder is the longest part of a cycle and had NO row of its own, so the GUI showed
            // its work under the wait-only upload/download task (measured 30.09.2026: a 56-minute
            // scan while the GUI read "ok"). It records its own row here, before the scan, so a
            // restart mid-scan leaves a visible "run" instead of silence.
            var seederWorker = Plugin.Instance?.WorkerRuns;
            seederWorker?.Start(Registry.WorkerRunRegistry.SeederKey, "Seeder");
            SeedSnapshot snapshot;
            try
            {
                snapshot = _seeder.Scan(config, onlyLibraries, dir, onlyItemIds, downloadAllowed);

                // F-M311: hand the scan's writes to the statistics row. ADDED, not overwritten.
                // A scan does NOT always get a run: the direction ends before it when no arrivals
                // were queued ("no arrivals queued — no run") or when its queue holds nothing to do,
                // and the seeder has edited files either way. Assigning would let the NEXT scan's
                // number wipe the first one's before any run could report it — the work would then
                // be done on disk and absent from the statistics forever. Adding keeps it until a
                // run consumes it. Double counting is impossible: the counter is cleared exactly
                // once, by the writer, and only a scan adds to it.
                _pendingLanguageCodesAllocated += snapshot.LanguageCodesAllocated;
                _pendingLooseSubtitlesRenamed += snapshot.LooseSubtitlesRenamed;
                // Accumulate the leg, then report the cycle's running total — see SeederQueuedDetail.
                // The gate mirrors the scan's own: a leg only counts the direction it was asked for,
                // because the foreign queue is left structurally empty by that gate.
                if (dir != CycleDirection.DownloadOnly)
                {
                    _seedCoveredUp = true;
                    _seedQueuedUp += snapshot.Upload.Count;
                }

                if (dir != CycleDirection.UploadOnly)
                {
                    _seedCoveredDown = true;
                    _seedQueuedDown += snapshot.Download.Count;
                }

                _seedLegsScanned++;
                RecordSeeder(Registry.WorkerRunRegistry.Outcome.Ok, SeederQueuedDetail());
            }
            catch (Exception ex)
            {
                // RED: the scan threw — the direction cannot run at all.
                RecordSeeder(Registry.WorkerRunRegistry.Outcome.Failed, ex.Message);
                throw;
            }
            int newUp = MergeQueue(_uploadQueue, snapshot.Upload, _lastSeedNewIdsUp);
            int newDown = MergeQueue(_downloadQueue, snapshot.Download, _lastSeedNewIdsDown);
            _lastSeedNewItems = newUp + newDown;
            SaveQueues();
            // This was a REAL scan — move the fast-path anchor forward.
            SaveSeedStamp(scanStartedUtc, dir);
            // The forced scan happened — drain the during-cycle list.
            lock (_lock)
            {
                if (drainDuringCycle)
                {
                    _pendingDuringCycleIds = null;
                    LogUtil.Normal(_logger, "[SubDL-Seed] during-cycle arrival consumed — forced scan done ({Reason}, dir {Dir}).", reason, dir);
                }
            }
            // No candidate/queue counts in the log.
            LogUtil.Detail(_logger, "[SubDL-Seed] done ({Reason}, dir {Dir}).", reason, dir);
            return true;
        }
        finally
        {
            Pipeline.PipelineRunLock.Release(_logger, "seeder");
        }
    }

    /// <summary>
    /// Merge rule: new candidates enter as Queued (unless already known);
    /// existing entries keep their retry counter and state. Since the 12.09.2026
    /// queue-file rework the queue holds only OPEN work (Queued/RealFailure) —
    /// finished items are removed at run end, so a removed item re-entering as
    /// Queued is EXPECTED: the refetch gate (searchTracker/IsDue) and the
    /// content registry decide whether it is actually worked on again.
    /// </summary>
    private int MergeQueue(List<QueueItem> queue, List<QueueItem> candidates, System.Collections.Generic.HashSet<string>? newIdsSink = null)
    {
        var known = queue.Select(x => x.ItemId).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var c in candidates)
        {
            // Fix 13.09.2026: purged items never re-enter the queue (no
            // re-extract loop — the pipeline verdict was permanent).
            if (_purgedIds.Contains(c.ItemId))
            {
                LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger,"[SubDL-Seed] skip purged item: {Name}", c.Name);
                continue;
            }
            if (!known.Contains(c.ItemId))
            {
                queue.Add(c);
                added++;
                newIdsSink?.Add(c.ItemId); // Rev.3: sweep run scope = only the fresh arrivals
            }
        }
        return added;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  PERSISTENCE
    // ────────────────────────────────────────────────────────────────────────

    private sealed class QueueFile
    {
        public List<QueueItem> Upload { get; set; } = new();
        public List<QueueItem> Download { get; set; } = new();
    }

    private void LoadQueues()
    {
        // Load the last real seed-scan stamp (fast-path anchor).
        try
        {
            var stampPath = Path.Combine(Plugin.Instance!.DataFolderPath, SeedStampFileName);
            if (File.Exists(stampPath))
            {
                var parts = File.ReadAllLines(stampPath);
                // Legacy format (one line) sets both stamps; new format: line 1 = up, line 2 = down.
                if (parts.Length >= 2)
                {
                    _lastScanStampUpUtc = DateTime.Parse(parts[0].Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                    _lastScanStampDownUtc = DateTime.Parse(parts[1].Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                }
                else
                {
                    var legacy = DateTime.Parse(parts[0].Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                    _lastScanStampUpUtc = legacy;
                    _lastScanStampDownUtc = legacy;
                }
            }
        }
        catch (Exception ex)
        {
            LogUtil.Detail(_logger, "[SubDL-Dispatch] seed-scan stamp unreadable ({Msg}) — starting empty.", ex.Message);
        }

        try
        {
            if (!File.Exists(QueuePath))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<QueueFile>(File.ReadAllText(QueuePath));
            if (file != null)
            {
                _uploadQueue = file.Upload ?? new List<QueueItem>();
                _downloadQueue = file.Download ?? new List<QueueItem>();
            }

            // Fix 13.09.2026: reload the purged tombstones (no re-seed of
            // permanently failed items after a restart).
            if (File.Exists(PurgedPath))
            {
                var purged = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(PurgedPath), s_jsonOptions);
                if (purged != null)
                {
                    _purgedIds = new HashSet<string>(purged, System.StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL-Dispatch] Queue load failed: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Persist the fast-path anchor (start time of the last real scan).
    /// Synchronous on purpose (called from async SeedAsync — CA1849 wants sync
    /// file IO out of async context; the 20-byte write is negligible but the
    /// analyzer is strict).
    /// </summary>
    private void SaveSeedStamp(DateTime stampUtc, CycleDirection dir)
    {
        // Rev.2: only the scanned direction moves its stamp. A
        // direction-scoped scan must not burn the other side's pre-check.
        if (dir != CycleDirection.DownloadOnly)
        {
            _lastScanStampUpUtc = stampUtc;
        }

        if (dir != CycleDirection.UploadOnly)
        {
            _lastScanStampDownUtc = stampUtc;
        }

        try
        {
            var stampPath = Path.Combine(Plugin.Instance!.DataFolderPath, SeedStampFileName);
            var tmp = stampPath + ".tmp";
            File.WriteAllText(tmp, _lastScanStampUpUtc.ToString("o") + "\n" + _lastScanStampDownUtc.ToString("o"));
            File.Move(tmp, stampPath, true);
        }
        catch (Exception ex)
        {
            LogUtil.Detail(_logger, "[SubDL-Dispatch] seed-scan stamp save failed: {Msg}", ex.Message);
        }
    }

    private void SaveQueues()
    {
        try
        {
            // Tombstone prune removed (12.09.2026 rework): finished items are
            // removed at run end — this save only persists open work.

            var file = new QueueFile { Upload = _uploadQueue, Download = _downloadQueue };
            var tmp = QueuePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, s_jsonOptions));
            File.Move(tmp, QueuePath, true);

            // Fix 13.09.2026: persist the purged tombstones alongside the queues.
            if (_purgedDirty)
            {
                var purgedTmp = PurgedPath + ".tmp";
                File.WriteAllText(purgedTmp, JsonSerializer.Serialize(_purgedIds, s_jsonOptions));
                File.Move(purgedTmp, PurgedPath, true);
                _purgedDirty = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL-Dispatch] Queue save failed: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M111: re-read cycle-queues.json from disk (called after a database reset —
    /// the reset wiped the file, the in-memory copy must go too).
    /// </summary>
    public void ReloadQueues()
    {
        lock (_lock)
        {
            _uploadQueue = new List<QueueItem>();
            _downloadQueue = new List<QueueItem>();
        }

        LoadQueues();
        LogUtil.PerItem(Plugin.Instance?.Configuration.LogMode ?? LogLevelMode.Normal, _logger,"[SubDL-Dispatch] Queues reloaded (reset): {Up} up / {Down} down.", _uploadQueue.Count, _downloadQueue.Count);
    }

    /// <summary>
    /// F-M131: aborts the currently running cycle's pipeline runs (user stop via
    /// the Jellyfin scheduled-task cancel button). The cycle unwinds gracefully:
    /// the active run stops like a quota stop (remaining items stay due/queued),
    /// further seed rounds are skipped and the cycle logs 'user stop' and ends.
    /// </summary>
    public void RequestUserStop(string reason)
    {
        lock (_lock)
        {
            if (!_cycleActive)
            {
                LogUtil.Normal(_logger, "[SubDL-Dispatch] {Reason}: no cycle active — nothing to stop.", reason);
                return;
            }
            _userStopActive = true;
        }
        SetDirectionOutcome(upload: true, Registry.WorkerRunRegistry.Outcome.Cancelled, "user stop");
        SetDirectionOutcome(upload: false, Registry.WorkerRunRegistry.Outcome.Cancelled, "user stop");
        LogUtil.Normal(_logger, "[SubDL-Dispatch] {Reason}: user stop requested — aborting current run(s).", reason);
        try { _stopCts.Cancel(); } catch (ObjectDisposedException)
        {
            // _stopCts already disposed during shutdown — the caller only needs the cancellation signal once.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _debounceTimer?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        if (Instance == this)
        {
            Instance = null;
        }
    }
}
