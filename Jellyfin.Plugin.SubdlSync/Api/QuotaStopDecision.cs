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

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M238: what the counters say about one direction. "Unreadable" is its own answer
/// because it calls for the same day-long pace as "Spent" — see <see cref="QuotaStopDecision"/>.
/// </summary>
public enum QuotaRead
{
    /// <summary>The allowance has room; the 429 was a short-term trip.</summary>
    Available,

    /// <summary>The allowance is spent.</summary>
    Spent,

    /// <summary>No usable counter was returned; the allowance is unknown.</summary>
    Unreadable
}

/// <summary>
/// F-M238: what a 429 leaves behind — the retry shape for the direction that stopped.
/// </summary>
public enum QuotaStop
{
    /// <summary>The allowance is spent. One fire after the server's reset plus jitter.</summary>
    NextDayFire,

    /// <summary>A short-term trip. One fire after <c>JobSpacingMinutes</c>.</summary>
    Respaced,

    /// <summary>The allowance is spent but the direction's toggle is off, or an arrival run owns the slot. Clean stop, no fire.</summary>
    CleanStopNoFire
}

/// <summary>
/// F-M238 (user decision 28.09.2026): the decision after a 429, as a rule apart from
/// the code that acts on it.
/// <para>
/// It sits here — pure, with no config, no clock and no coordinator — because the
/// expensive mistakes are all in this one branch: a spent allowance that stops without
/// scheduling anything never retries until the next regular anchor, and an unknown
/// allowance read optimistically becomes a retry storm against a limit that may already
/// be reached. Both are answerable from four booleans, so both are asserted directly.
/// </para>
/// </summary>
public static class QuotaStopDecision
{
    /// <summary>
    /// Decides the retry shape.
    /// <para>
    /// An allowance that is SPENT and one that could not be READ take the same path: the
    /// day-long one. The pipeline cannot tell "no quota left" from "quota unknown", and of
    /// the two possible mistakes only one is recoverable within the hour — treating an
    /// unknown allowance as available spends requests against a limit that may be reached.
    /// </para>
    /// </summary>
    /// <param name="continueAfterLimit">The direction's "Continue after daily limit" setting.</param>
    /// <param name="isArrivalRun">True when an arrival event started this run.</param>
    /// <param name="exhausted">True when the counter for this direction is spent.</param>
    /// <param name="unreadable">True when the counters could not be read or carry no limit.</param>
    /// <returns>The retry shape for this direction.</returns>
    public static QuotaStop Decide(
        bool continueAfterLimit,
        bool isArrivalRun,
        bool exhausted,
        bool unreadable)
    {
        bool dayLong = exhausted || unreadable;
        if (!dayLong)
        {
            // A short-term trip is respaced regardless of the toggle: the toggle governs
            // the day-long wait, not whether a rate limit may be ridden out this hour.
            return QuotaStop.Respaced;
        }

        // The toggle decides whether a spent allowance earns a fire at all; an arrival run
        // never holds the slot (its cycle would block the other direction behind it).
        return continueAfterLimit && !isArrivalRun
            ? QuotaStop.NextDayFire
            : QuotaStop.CleanStopNoFire;
    }
}
