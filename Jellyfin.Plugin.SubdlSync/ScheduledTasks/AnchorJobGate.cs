// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. Sub Scribe is distributed WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR
// PURPOSE. See the GNU General Public License for more details.

using System;
using System.Globalization;

namespace Jellyfin.Plugin.SubScribe.ScheduledTasks;

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
    /// F-M666 (operator order 21.11.2026, „Retetch zählt auch den Postprocessing mit"): the upload
    /// postprocessing holds the refetch back like a held run lock — same deferral, same deadline, same
    /// budget, and the slot stays claimable because the marker is not consumed.
    /// <para>
    /// It is a pure reason, not a second mechanism: the caller does with the string exactly what it does
    /// with "run holds the lock". Three ways the answer can be "no hold", each of them deliberate:
    /// the upload direction is off (nothing can fire the postprocessing, so an old moment must not block
    /// the refetch for ever), the delay is 0 (the operator switched the postprocessing off — F-M445 — and
    /// no anchor exists), and the armed moment has already arrived (the postprocessing block ran first in
    /// this tick and either fired it or could not, in which case holding the refetch would be a loop).
    /// </para>
    /// </summary>
    /// <param name="nowUtc">The current instant — moments are UTC.</param>
    /// <param name="postprocessDueUtc">The armed postprocessing moment, or null when none is armed.</param>
    /// <param name="postprocessingRunning">True while an upload postprocessing run is working.</param>
    /// <param name="uploadEnabled">The upload direction of the source — the postprocessing only exists with it (F-M291).</param>
    /// <param name="postprocessDelayMinutes">The configured delay; 0 means the postprocessing is inactive (F-M445).</param>
    /// <returns>The reason for the status line and the log, or null when the refetch may fire.</returns>
    public static string? UploadPostprocessHold(
        DateTime nowUtc,
        DateTime? postprocessDueUtc,
        bool postprocessingRunning,
        bool uploadEnabled,
        int postprocessDelayMinutes)
    {
        // A running run is the immediate truth and outranks everything else: it also holds the run lock,
        // so the refetch is deferred either way — but it should say WHY.
        if (postprocessingRunning)
        {
            return "upload postprocessing is running";
        }

        if (!uploadEnabled || postprocessDelayMinutes <= 0)
        {
            return null;
        }

        if (postprocessDueUtc is not { } due || due <= nowUtc)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(due, TimeZoneInfo.Local);
        return "upload postprocessing pending until "
               + local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

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
