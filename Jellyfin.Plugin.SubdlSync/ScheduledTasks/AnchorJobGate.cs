// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR
// PURPOSE. See the GNU General Public License for more details.

using System;

namespace Jellyfin.Plugin.SubdlScribe.ScheduledTasks;

/// <summary>
/// F-M368 (28.10.2026): the two decisions every diced-anchor job needs, as PURE functions — testable
/// without a server, a clock or a task manager.
/// <para>
/// Why this exists: the anchor blocks spelled both decisions inline, and the second one inside the
/// first — <c>due = … &amp;&amp; (marker != today || now &lt; until)</c> with <c>if (due &amp;&amp; now &gt;= until)</c>.
/// The two clauses exclude each other: while the deadline lies in the future the fire gate blocks, and
/// the moment it arrives the due-term falls to false, because the earlier fire already consumed the day
/// marker. A run that had deferred itself on a busy lock therefore never came back. Measured
/// 28.10.2026 16:25: "Database refresh re-fire scheduled at 15:40 UTC", followed by not one refresh
/// line for the rest of the day — the row stayed yellow at 16:24 for the rest of the evening.
/// </para>
/// <para>
/// And the deadline was written in TWO clock kinds: the anchor blocks with <c>DateTime.Now</c>, the
/// tasks with <c>DateTime.UtcNow</c>, while every comparison used <c>DateTime.Now</c>. In CET a
/// UTC-stamped deadline reads one hour in the past, in CEST two. Hence the split signature:
/// <paramref name="nowUtc"/> for deadlines, <paramref name="nowLocal"/> for anchors.
/// </para>
/// </summary>
public static class AnchorJobGate
{
    /// <summary>
    /// F-M368: due while the day marker is unconsumed OR a deferral is pending. The pending deferral has
    /// to keep the job due — the earlier fire must not bury the retry it scheduled itself.
    /// </summary>
    /// <param name="nowLocal">Local wall-clock time — anchors are diced in local time.</param>
    /// <param name="anchor">The diced anchor, or null when this interval has none today.</param>
    /// <param name="cadenceDue">Cadence gate (e.g. every 4th ISO week for a monthly prune).</param>
    /// <param name="lastMarker">Marker of the last fire (day or minute string).</param>
    /// <param name="marker">Marker of this anchor.</param>
    /// <param name="deferPending">True when a deferral deadline is armed.</param>
    /// <returns>True when the job may be considered.</returns>
    public static bool IsDue(
        DateTime nowLocal,
        DateTime? anchor,
        bool cadenceDue,
        string? lastMarker,
        string? marker,
        bool deferPending)
        => anchor.HasValue
           && nowLocal >= anchor.Value
           && cadenceDue
           && (lastMarker != marker || deferPending);

    /// <summary>
    /// F-M368: the deadline gate, evaluated SEPARATELY from <see cref="IsDue"/>. No deferral, or one that
    /// has arrived, lets the job through; a deadline in the future holds it back for another tick.
    /// </summary>
    /// <param name="nowUtc">The current instant — deadlines are UTC.</param>
    /// <param name="deferredUntil">The armed deadline, or <see cref="DateTime.MinValue"/>.</param>
    /// <returns>True when the job may fire.</returns>
    public static bool DeferralElapsed(DateTime nowUtc, DateTime deferredUntil)
        => deferredUntil == DateTime.MinValue || nowUtc >= deferredUntil;

    /// <summary>
    /// F-M368: deadlines are stored in UTC. A state file written before this change can carry a LOCAL
    /// value, and <see cref="DateTime"/> comparisons ignore <see cref="DateTime.Kind"/> — so it has to be
    /// converted on the way in, or the mixed clock survives the upgrade.
    /// </summary>
    /// <param name="value">The value read from the state file.</param>
    /// <returns>The same instant, always in UTC.</returns>
    public static DateTime NormalizeDeadline(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            // An "O"-round-trip never produces Unspecified (it carries "Z" or an offset); the branch
            // exists so a hand-edited state file cannot reintroduce the ambiguity.
            DateTimeKind.Unspecified when value != DateTime.MinValue
                => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };
}
