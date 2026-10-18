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

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// F-M350 (operator order 18.10.2026): the <c>framerate</c> code an upload reports.
/// <para>
/// Until now the metadata step sent the literal <c>"0"</c>, which SubDL documents as "default /
/// unknown". That was not a decision, it was a leftover: the value was never wired, although the
/// frame rate is already read in this codebase for the download-side FPS gate (F-M43 stage 1) and the
/// item's video stream carries it. SubDL shows the field as the release's frame rate and its own
/// filters read it, so sending "unknown" for a file whose rate is known makes the upload less findable
/// than it could be — the same shape as the hardcoded <c>quality</c> that F-M344 replaced.
/// </para>
/// <para>
/// THE VALUE LIST IS SUBDL'S, NOT AN INVENTED ONE. Measured on the live search API across 120
/// subtitle entries: code <c>0</c> always arrives with <c>fps: null</c>, code <c>2</c> always with
/// <c>fps: 23.976</c> — the numeric codes are a table, and the numbers below are the ones the
/// download side of this plugin already maps (<c>SubdlApiClient</c>, "0 = unknown; 2 ⇒ 23.976,
/// 3 ⇒ 25, 4 ⇒ 29.97, 5 ⇒ 24, 7 ⇒ 30"). No other code was observed in the sample and none is
/// invented here: a rate that is not in the table (48, 50, 59.94, 60 …) stays <c>0</c>, which is
/// exactly what shipped before, so an unusual rate cannot regress into a wrong claim.
/// </para>
/// <para>
/// NEAREST WINS. Container rates arrive as exact values (23.976023…, 29.970029…) and as the plain
/// integers people expect (24, 25, 30), and 23.976 sits only 0.024 away from 24 — a fixed epsilon
/// would have to be smaller than that and would then reject the float noise of the real values. So
/// the closest table entry is chosen and a candidate further away than <see cref="Tolerance"/> is
/// refused. The two neighbours are 0.03 apart at worst, so nothing is ambiguous within tolerance.
/// </para>
/// </summary>
public static class SubdlFramerate
{
    /// <summary>SubDL's "default / unknown" code. Also the answer for every rate not in the table.</summary>
    public const string Unknown = "0";

    /// <summary>Largest distance from a table entry that still counts as that entry.</summary>
    public const double Tolerance = 0.05;

    /// <summary>The codes this class may return. Asserted by T156, so a later edit cannot invent one.</summary>
    public static IReadOnlyList<string> Allowed { get; } = new[] { "0", "2", "3", "4", "5", "7" };

    /// <summary>The table as SubDL documents it: 2 ⇒ 23.976, 3 ⇒ 25, 4 ⇒ 29.97, 5 ⇒ 24, 7 ⇒ 30.</summary>
    private static readonly (double Fps, string Code)[] Table =
    {
        (23.976, "2"),
        (25.0, "3"),
        (29.97, "4"),
        (24.0, "5"),
        (30.0, "7"),
    };

    /// <summary>
    /// Maps a frame rate to SubDL's code, or <see cref="Unknown"/> when there is nothing to claim.
    /// </summary>
    /// <param name="fps">Frame rate of the item's video stream, or null when it is not known.</param>
    /// <returns>One of <see cref="Allowed"/>.</returns>
    public static string CodeFor(double? fps)
    {
        if (fps is not { } value || double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return Unknown;
        }

        string best = Unknown;
        double bestDistance = double.MaxValue;
        foreach ((double tableFps, string code) in Table)
        {
            double distance = Math.Abs(value - tableFps);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = code;
            }
        }

        return bestDistance <= Tolerance ? best : Unknown;
    }

    /// <summary>
    /// Maps the item's video stream to SubDL's code, using the AVERAGE rate first and the REAL one as
    /// the fallback (operator order 18.10.2026 "dann bauen").
    /// </summary>
    /// <remarks>
    /// Neither field is trustworthy on its own — measured over the operator's 2302 video files, they
    /// fail on different subsets: 27 files carry a broken average (1000) with a correct real rate,
    /// 18 carry a good average with a real rate that is 0.05+ off (29.9166), and 11 have both mapping
    /// to a DIFFERENT code (23.9756 ⇒ 2 against 24 ⇒ 5). With the fallback, 2257 of 2302 files report
    /// a code (98.0 %); average alone gives 96.8 %, real alone 97.3 %.
    /// <para>
    /// The average goes first because it is the PLAYBACK rate — frames divided by duration, which is
    /// what SubDL's <c>fps</c> means — while the real rate is only the timing grid (a file with every
    /// second frame dropped reports avg 12.6 against real 25). The download gate (F-M43 stage 1) reads
    /// the same order, so a file gets the same rate in the gate and in the upload. On the 11 conflicts
    /// the choice is therefore consistency, not truth: both readings are defensible and the difference
    /// only shows on music videos.
    /// </para>
    /// <para>
    /// A broken average is not a rate: 1000 comes from containers that report one frame in one
    /// millisecond (reproduced with ffmpeg), so it is refused by the table and the real rate takes over.
    /// </para>
    /// </remarks>
    /// <param name="averageFrameRate">The stream's average frame rate (0 when unknown).</param>
    /// <param name="realFrameRate">The stream's real base frame rate (0 when unknown).</param>
    /// <returns>One of <see cref="Allowed"/>.</returns>
    public static string CodeFor(double? averageFrameRate, double? realFrameRate)
    {
        string fromAverage = CodeFor(averageFrameRate);
        if (fromAverage != Unknown)
        {
            return fromAverage;
        }

        return CodeFor(realFrameRate);
    }
}
