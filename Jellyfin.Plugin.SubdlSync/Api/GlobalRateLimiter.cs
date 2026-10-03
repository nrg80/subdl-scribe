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
/// The pacing rhythm shared by the upload AND the download pipeline (F-M20/F-M26).
/// <para>
/// It owns NO budget. An earlier build kept a per-run hourly call bucket here and let it
/// stop a run — a limit the plugin had invented itself and then obeyed. SubDL publishes
/// only DAILY counters, so the bucket stopped runs the server would have allowed; it is
/// removed (03.10.2026, user decision). What remains is the spacing between calls and the
/// plus/minus 30 % jitter on real transfers (anti-thundering-herd, F-M26).
/// </para>
/// <para>Nothing in this class can refuse a call. Thread-safe across both pipelines.</para>
/// </summary>
public sealed class GlobalRateLimiter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GlobalRateLimiter"/> class.
    /// </summary>
    /// <param name="transfersPerHour">Configured transfer rate (F-M20). It is the base for the
    /// interval between two real transfers (3600/rate); it is NOT a ceiling — nothing here
    /// counts calls or refuses one.</param>
    /// <param name="minCallPauseSec"> (user decision 15.09.2026): pause between two API calls
    /// in seconds (0.1–10, default 0.5). Overrides the derived 3600/rate interval when set;
    /// SubDL allows 600 req/min (0.1 s spacing).</param>
    public GlobalRateLimiter(int transfersPerHour, double minCallPauseSec = -1)
    {
        TransfersPerHour = Math.Clamp(transfersPerHour, 1, 2000);

        // (25.09.2026): the "derive from the transfer rate" sentinel is -1 and must survive.
        // The old expression clamped the sentinel too — Math.Clamp(-1, 0.1, 10) was 0.1, never
        // -1 — so the derived branch in InterCallPauseMs could not be reached and the
        // documented behaviour was unreachable code. Clamp only a real value.
        MinCallPauseSec = minCallPauseSec < 0 ? -1 : Math.Clamp(minCallPauseSec, 0.1, 10);
    }

    /// <summary>Gets the configured transfer rate (transfers per hour, F-M20).</summary>
    public int TransfersPerHour { get; }

    /// <summary>Gets the explicit pause between API calls (seconds); -1 = derive from the transfer rate.</summary>
    public double MinCallPauseSec { get; }

    /// <summary>
    /// F-M26a: pause between two bare API calls that carry no actual transfer (an empty
    /// search, a skip, a metadata lookup). Deterministic: the explicit
    /// <see cref="MinCallPauseSec"/> when configured, otherwise the interval derived from the
    /// transfer rate (3600/rate). This pause carries NO jitter — the jitter belongs to the
    /// transfer rhythm (<see cref="TransferPauseMs"/>).
    /// </summary>
    /// <returns>Delay in milliseconds.</returns>
    public int InterCallPauseMs()
    {
        if (MinCallPauseSec >= 0)
        {
            return (int)(MinCallPauseSec * 1000);
        }

        // (user decision 10.09.2026 "rate limit enforcement we leave to SubDL...
        // meanwhile we continue at the steered rate"): the base interval IS the steered
        // rate (3600s / rate). Floor 1 s (API friendliness), no upper clamp.
        return Math.Max(3_600_000 / TransfersPerHour, 1000);
    }

    /// <summary>
    /// F-M26: pause between two REAL transfers (upload→upload, download→download): the
    /// steered interval (3600/rate), randomised by ±30 %.
    /// </summary>
    /// <returns>Delay in milliseconds.</returns>
    public int TransferPauseMs()
    {
        int baseMs = Math.Max(3_600_000 / Math.Max(TransfersPerHour, 1), 1000);

        // ±30 % around the base pause (F-M26 band). Random.Shared is thread-safe, so the
        // rhythm needs no lock of its own.
        return (int)(baseMs * (Random.Shared.NextDouble() * 0.6 + 0.7));
    }

    /// <summary>
    /// Waits one bare-call pause. This is the whole throttle now — it cannot fail, because
    /// there is no bucket left to run dry (F-M20).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes after the pause.</returns>
    public Task PauseAsync(CancellationToken ct) => Task.Delay(InterCallPauseMs(), ct);
}
