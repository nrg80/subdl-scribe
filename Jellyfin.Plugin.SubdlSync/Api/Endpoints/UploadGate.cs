// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using Jellyfin.Plugin.SubdlScribe.Configuration;

namespace Jellyfin.Plugin.SubdlScribe.Api.Endpoints;

/// <summary>
/// F-M489 (operator order 15.11.2026: "Reup und Kreuzkontamination sollen verhindert werden. Also auch
/// rein reup von tvst auf tvst. Kein reup von subdl auf subdl"): which sources may still take THIS
/// subtitle, decided in ONE place.
/// <para>
/// Two independent reasons can forbid a source, and both must be answered per source, not per run:
/// </para>
/// <list type="bullet">
/// <item><description>the switch is off or the provenance belongs to the other source (F-M487);</description></item>
/// <item><description>that source ALREADY has this subtitle — its own upload mark is in the store
/// (F-M488). A re-upload to the same source is exactly as unwanted as a cross-source upload.</description></item>
/// </list>
/// <para>
/// The consequence is the case that used to be invisible: a subtitle uploaded to tvsubtitles.net long ago
/// is still allowed to go to SubDL, and a subtitle that SubDL already has is still allowed to go to
/// tvsubtitles.net. Only the pair "same source, again" is refused.
/// </para>
/// </summary>
public static class UploadGate
{
    /// <summary>
    /// F-M489: the decision for both sources at once.
    /// </summary>
    /// <param name="subdlSwitch">Is the SubDL upload switch on?</param>
    /// <param name="tvSubtitlesSwitch">Is the tvsubtitles.net upload switch on?</param>
    /// <param name="origin">The subtitle's provenance as stored (null/empty = unknown).</param>
    /// <param name="subdlAlready">Does SubDL already have this subtitle (its own mark in the store)?</param>
    /// <param name="tvSubtitlesAlready">Does tvsubtitles.net already have it?</param>
    /// <returns>Which sources may run, and why nothing runs when both are false.</returns>
    public static UploadGateResult Decide(
        bool subdlSwitch, bool tvSubtitlesSwitch, string? origin, bool subdlAlready, bool tvSubtitlesAlready)
    {
        var subdlSwitched = subdlSwitch && Provenance.MayUploadTo(origin, SubtitleProvider.Subdl);
        var tvSwitched = tvSubtitlesSwitch && Provenance.MayUploadTo(origin, SubtitleProvider.TvSubtitles);

        var subdl = subdlSwitched && !subdlAlready;
        var tv = tvSwitched && !tvSubtitlesAlready;

        if (subdl || tv)
        {
            return new UploadGateResult(subdl, tv, null);
        }

        // Why not — the most specific answer wins, because it is the one that explains the row.
        if (!subdlSwitched && !tvSwitched)
        {
            var known = Provenance.Parse(origin);
            return new UploadGateResult(false, false, known is null ? "no-upload-source" : "cross-source");
        }

        var already = (subdlSwitched ? "subdl" : string.Empty) + (tvSwitched && subdlSwitched ? "+" : string.Empty)
            + (tvSwitched ? "tvsubtitles" : string.Empty);
        return new UploadGateResult(false, false, "already-uploaded:" + already);
    }
}

/// <summary>
/// F-M489: the result of <see cref="UploadGate.Decide"/>.
/// </summary>
/// <param name="Subdl">May the subtitle go to SubDL in this run?</param>
/// <param name="TvSubtitles">May it go to tvsubtitles.net in this run?</param>
/// <param name="SkipReason">Why neither may run, or null when at least one may.</param>
public readonly record struct UploadGateResult(bool Subdl, bool TvSubtitles, string? SkipReason);
