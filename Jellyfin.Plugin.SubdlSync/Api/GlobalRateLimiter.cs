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
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// Global rate limiter shared by upload AND download pipelines (F-M20/F-M26).
/// One hourly bucket for all SubDL API calls (search, download, upload) +
/// random jitter on every inter-call pause (anti-thundering-herd, F-M26).
/// Thread-safe across both pipelines.
/// </summary>
public sealed class GlobalRateLimiter
{
    private readonly object _lock = new();
    private readonly Random _rng = new();
    private DateTime _hourBucket = DateTime.MinValue;
    private int _callsThisHour;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlobalRateLimiter"/> class.
    /// </summary>
    /// <param name="maxCallsPerHour">Combined hourly cap for ALL API calls (upload + download).</param>
    /// <param name="minCallPauseSec"> (user decision 15.09.2026): minimum pause
    /// between two API calls in seconds (0.1–10). Overrides the derived 3600/cap
    /// interval when SMALLER; SubDL allows 600 req/min (0.1 s spacing). Default 0.5.</param>
    public GlobalRateLimiter(int maxCallsPerHour, double minCallPauseSec = -1)
    {
        MaxCallsPerHour = Math.Clamp(maxCallsPerHour, 1, 2000);

        // (25.09.2026): the "derive from the hourly cap" sentinel is -1 and must survive.
        // The old expression clamped the sentinel too — Math.Clamp(-1, 0.1, 10) is 0.1, never
        // -1 — so the derived branch in InterCallPauseMs could not be reached and the
        // documented behaviour was unreachable code. Clamp only a real value.
        MinCallPauseSec = minCallPauseSec < 0 ? -1 : Math.Clamp(minCallPauseSec, 0.1, 10);
    }

    /// <summary>Gets the combined hourly cap.</summary>
    public int MaxCallsPerHour { get; }

    /// <summary>Gets the explicit minimum inter-call pause (seconds); -1 = derive from hourly cap (behaviour).</summary>
    public double MinCallPauseSec { get; }

    /// <summary>
    /// F-M20: tries to acquire one API-call slot from the global hourly bucket.
    /// Returns false when the hour cap is exhausted (caller should stop the run).
    /// </summary>
    /// <summary>
    /// (user decision 14.09.2026): when the hourly bucket is exhausted,
    /// the UTC instant of its next roll-over (used by the pipelines to hand the
    /// wait over to the scheduler as a recovery fire); null = capacity free.
    /// </summary>
    public DateTime? NextRollOverUtc
    {
        get
        {
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _hourBucket).TotalHours >= 1 || _callsThisHour < MaxCallsPerHour)
                {
                    return null;
                }
                return _hourBucket.AddHours(1);
            }
        }
    }

    public bool TryAcquireSlot()
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _hourBucket).TotalHours >= 1)
            {
                _hourBucket = now;
                _callsThisHour = 0;
            }

            if (_callsThisHour >= MaxCallsPerHour)
            {
                return false;
            }

            _callsThisHour++;
            return true;
        }
    }

    /// <summary>
    /// Pause between two bare API calls that carry no actual transfer
    /// (an empty search, a skip, a metadata lookup). Deterministic: the explicit
    /// <see cref="MinCallPauseSec"/> when configured, otherwise the interval derived
    /// from the hourly cap (3600/cap).
    /// <para>
    /// (25.09.2026, user decision): this pause carries NO jitter. The jitter belongs to the
    /// transfer rhythm (<see cref="TransferPauseMs"/>), where it hides the load pattern;
    /// spreading bare calls adds nothing. Earlier this method applied ±30% jitter here — on
    /// the derived branch only, which the configured default (0.5 s) made unreachable, so the
    /// randomness was real code that could never run.
    /// </para>
    /// </summary>
    /// <returns>Delay in milliseconds.</returns>
    /// F-M26a: bare API calls are deterministic; only real transfers get the plus/minus 30 % band.
    public int InterCallPauseMs()
    {
        // (15.09.2026): when an explicit minimum pause is configured
        // (0.1–10 s), use it instead of the derived 3600/cap interval — it can
        // only SHORTEN the wait (SubDL allows 600 req/min). The hourly cap stays
        // as the hard bucket; this just spaces calls.
        if (MinCallPauseSec >= 0)
        {
            return (int)(MinCallPauseSec * 1000);
        }

        // (user decision 10.09.2026 "rate limit enforcement we leave to SubDL...
        // meanwhile we continue at the steered rate"): the base interval IS the steered
        // rate (3600s / cap). Floor 1 s (API friendliness), no upper clamp — the pause
        // spreads the cap evenly across the hour and the real limits are SubDL's business.
        return Math.Max(3_600_000 / MaxCallsPerHour, 1000);
    }

    /// <summary>
    /// Pause between two REAL transfers (upload→upload, download→download):
    /// the steered rate-limit interval (3600/cap), randomised by ±30%.
    /// <para>
    /// Not derived from <see cref="MinCallPauseSec"/> — that 0.1–10 s value is only the
    /// minimum pause between bare API calls (see <see cref="InterCallPauseMs"/>).
    /// </para>
    /// <para>
    /// (25.09.2026, user decision): the ±30% jitter belongs HERE (F-M26). Measuring the live
    /// run showed the opposite of the intent: consecutive uploads sat at 9.42–9.54 s, i.e. a
    /// fixed rhythm, while the randomness sat on a branch that could not execute. Transfers
    /// are the load a shared API sees; keeping them on a metronome invites the very
    /// synchronised bursts the jitter exists to avoid.
    /// </para>
    /// </summary>
    public int TransferPauseMs()
    {
        int baseMs = Math.Max(3_600_000 / Math.Max(MaxCallsPerHour, 1), 1000);

        lock (_rng)
        {
            // ±30% around the base pause (F-M26 band)
            double factor = 0.7 + (_rng.NextDouble() * 0.6);
            return (int)(baseMs * factor);
        }
    }



    /// <summary>
    /// Acquire a slot WITHOUT the jittered pause (pause stays with the
    /// caller). Non-blocking: false = hourly cap exhausted (caller stops and
    /// hands the wait to the scheduler via the roll-over fire).
    /// </summary>
    public async Task<bool> TryAcquireSlotAsync(CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return TryAcquireSlot();
    }

    public async Task<bool> ThrottleAsync(CancellationToken ct)
    {
        if (!TryAcquireSlot())
        {
            return false;
        }

        await Task.Delay(InterCallPauseMs(), ct).ConfigureAwait(false);
        return true;
    }
}
