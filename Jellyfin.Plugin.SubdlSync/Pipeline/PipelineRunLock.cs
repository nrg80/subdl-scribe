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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// (user decision 25.09.2026): ONE lock for every state-mutating component —
/// seeder, download, upload, postprocessing, database refresh, oshash-refresh. All six touch
/// the same data store, so a single mutual exclusion is what the design needs.
///
/// Overlap is NOT waited out. A caller that cannot acquire gets <c>false</c>
/// immediately and reschedules itself by JobSpacingMinutes (the scheduler coordinators
/// already expose Schedule*Fire for this). The previous implementation polled a block
/// file every 5 s for up to 10 minutes and logged a line per poll: measured on
/// 21.09.2026 that produced 36 identical "already active" lines and a 3.5-minute
/// stall while not preventing a single run (SkippedByLock never fired once).
///
/// Stale detection is retained and is the only reason the block file still exists:
/// the file carries "PID|UTC timestamp" of the holder, so a run that died without
/// cleanup (crash, hard kill) is detected two ways instead of waiting out a window —
///   1. the holder PID no longer exists → take over immediately;
///   2. the holder PID exists but the stamp is older than <see cref="RunCeiling"/>
///      → a run wedged inside a living process; take over.
/// A block file older than <see cref="StaleAfter"/> is taken over as a last resort.
///
/// Fail-open: a malformed or unreadable block file must never wedge the plugin.
/// </summary>
/// F-M94h: ONE global run lock for all six state-mutating components. Overlap is never waited
/// out: a caller that cannot acquire is refused at once and reschedules itself by the job spacing.
public static class PipelineRunLock
{
    /// <summary>Absolute file age after which any block file counts as abandoned.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// Longest legitimate single run. The full-library upload measured ~1 h, so a
    /// stamp beyond this means the holder is wedged, not busy — take over even
    /// though its process is still alive.
    /// </summary>
    private static readonly TimeSpan RunCeiling = TimeSpan.FromHours(6);

    /// <summary>
    /// Short grace for in-process hand-offs (seed → download → upload are sequential
    /// inside one cycle). Long enough to absorb thread scheduling, far short of the
    /// old 10-minute wait.
    /// </summary>
    private static readonly TimeSpan HandOffGrace = TimeSpan.FromSeconds(20);

    // In-process mutex: every component runs inside the single Jellyfin server
    // process, so a plain monitor already serializes acquire/release races.
    private static readonly object InternalLock = new();

    // One file for everything — the "who" argument is a log label only.
    private const string BlockFileName = "pipeline.block";

    /// <summary>Who currently holds the lock (single holder by design) — log label only.</summary>
    private static string? _holder;

    private static string BlockPath() =>
        Path.Combine(Jellyfin.Plugin.SubdlScribe.Plugin.Instance!.DataFolderPath, BlockFileName);

    /// <summary>
    /// Removes a leftover block file once at plugin startup. After a restart no
    /// previous run can still be alive, so any file found here is a corpse.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public static void ClearOnStartup(ILogger logger)
    {
        lock (InternalLock)
        {
            try
            {
                var path = BlockPath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                    LogUtil.Normal(logger, "[SubDL-Lock] Removed leftover block file at startup (no previous run can still be alive).");
                }

                _holder = null;
            }
            catch (Exception ex)
            {
                logger?.LogWarning("[SubDL-Lock] Startup cleanup failed ({Msg}) — continuing.", ex.Message);
            }
        }
    }

    /// <summary>
    /// Acquires the lock with a short in-process grace. Returns false when another
    /// component holds it (or holds it again after the grace) — the caller must then
    /// reschedule itself by JobSpacingMinutes. There is no long wait any more.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="who">Component label for log lines (seeder, upload, download, …).</param>
    /// <param name="ct">Cancellation token; cancels the grace delay only.</param>
    /// <returns>True when the lock was acquired.</returns>
    public static async Task<bool> AcquireAsync(ILogger logger, string who, CancellationToken ct = default)
    {
        if (Acquire(logger, who))
        {
            return true;
        }

        try
        {
            await Task.Delay(HandOffGrace, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return Acquire(logger, who);
    }

    /// <summary>
    /// Non-blocking acquire.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="who">Component label for log lines.</param>
    /// <returns>True when the lock was acquired.</returns>
    public static bool Acquire(ILogger logger, string who)
    {
        lock (InternalLock)
        {
            return AcquireCore(logger, who);
        }
    }

    /// <summary>
    /// Non-invasive probe — true when the lock is FREE right now (no block file, or a
    /// stale one that would be taken over). Does not acquire anything; the caller
    /// acquires separately when it fires.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <returns>True when the lock is free.</returns>
    public static bool IsFree(ILogger logger)
    {
        try
        {
            var path = BlockPath();
            if (!File.Exists(path))
            {
                return true;
            }

            var ownerPid = ParseOwnerPid(path);
            if (ownerPid.HasValue && !IsProcessAlive(ownerPid.Value))
            {
                return true; // dead holder — Acquire takes over
            }

            var stamp = ParseStamp(path);
            if (stamp.HasValue && (DateTime.UtcNow - stamp.Value > StaleAfter
                || DateTime.UtcNow - stamp.Value > RunCeiling))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[SubDL-Lock] probe failed ({Msg}) — assuming free.", ex.Message);
            return true; // fail-open
        }
    }

    /// <summary>
    /// Releases the lock. Only the current holder may release; a stray call from a
    /// component that already handed it over is ignored.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="who">Component label.</param>
    public static void Release(ILogger logger, string who)
    {
        lock (InternalLock)
        {
            try
            {
                if (logger == null || who == null || !string.Equals(_holder, who, StringComparison.Ordinal))
                {
                    return;
                }

                var path = BlockPath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                _holder = null;
                LogUtil.Detail(logger, "[SubDL] {Who}: finished — lock released.", who);
            }
            catch (Exception ex)
            {
                logger?.LogWarning("[SubDL] {Who}: block file release failed: {Msg}", who, ex.Message);
            }
        }
    }

    private static bool AcquireCore(ILogger logger, string who)
    {
        try
        {
            var path = BlockPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (File.Exists(path))
            {
                var ownerPid = ParseOwnerPid(path);
                var stamp = ParseStamp(path);
                var age = stamp.HasValue ? DateTime.UtcNow - stamp.Value : StaleAfter + TimeSpan.FromMinutes(1);

                if (ownerPid.HasValue && !IsProcessAlive(ownerPid.Value))
                {
                    logger?.LogWarning(
                        "[SubDL-Lock] {Who}: previous holder (PID {Pid}) is gone — taking over.",
                        who,
                        ownerPid.Value);
                }
                else if (age > StaleAfter || age > RunCeiling)
                {
                    logger?.LogWarning(
                        "[SubDL-Lock] {Who}: holder PID {Pid} exceeded the run ceiling ({AgeMinutes} min) — taking over.",
                        who,
                        ownerPid?.ToString(CultureInfo.InvariantCulture) ?? "?",
                        (int)age.TotalMinutes);
                }
                else
                {
                    LogUtil.Detail(logger, 
                        "[SubDL-Lock] Busy: {Who} cannot start, held by {Holder} (PID {Pid}, {AgeMinutes} min) — reschedule by JobSpacingMinutes.",
                        who,
                        _holder ?? "?",
                        ownerPid?.ToString(CultureInfo.InvariantCulture) ?? "?",
                        (int)age.TotalMinutes);
                    return false;
                }
            }

            File.WriteAllText(
                path,
                string.Format(CultureInfo.InvariantCulture, "{0}|{1:O}", Environment.ProcessId, DateTime.UtcNow));
            _holder = who;
            LogUtil.Detail(logger, "[SubDL] {Who}: start (lock acquired).", who);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[SubDL] {Who}: block file check failed ({Msg}) — proceeding.", who, ex.Message);
            return true; // fail-open
        }
    }

    private static int? ParseOwnerPid(string path)
    {
        try
        {
            var first = File.ReadAllLines(path);
            if (first.Length == 0)
            {
                return null;
            }

            var line = first[0];
            var sep = line.IndexOf('|', StringComparison.Ordinal);
            if (sep <= 0)
            {
                return null;
            }

            return int.TryParse(line[..sep].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                ? pid
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? ParseStamp(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
            {
                return null;
            }

            var line = lines[0];
            var sep = line.IndexOf('|', StringComparison.Ordinal);
            if (sep < 0)
            {
                return null;
            }

            return DateTime.TryParse(
                line[(sep + 1)..].Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var stamp)
                ? stamp
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.HasExited == false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception)
        {
            return true; // cannot tell — fail-safe: treat as alive
        }
    }
}
