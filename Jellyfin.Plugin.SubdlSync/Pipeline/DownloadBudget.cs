// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
using System;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// F-M95 / F-M50: the ONE place that turns the candidate setting into the number the search and the
/// download loop use.
/// <para>
/// There was a SECOND parameter here, <c>keepBest</c> (F-M242), which raised both numbers to the
/// "best subtitles to keep per language" count. Both the setting and that raise are GONE (operator
/// order 08.10.2026): a language now receives exactly one corrected file, and the walk's stop is no
/// longer a user-tunable count. Keeping the parameter after removing the setting would have left a
/// number in the signature that nothing can set — the same kind of dead knob F-M242 was created to
/// fix.
/// </para>
/// </summary>
public static class DownloadBudget
{
    /// <summary>
    /// F-M95 / F-M50 (operator order 08.10.2026): the ONE number the search width and the Auto-Sync
    /// walk both use, derived from the single setting
    /// "Auto-Sync attempts and max. download/search limit".
    /// <para>
    /// <b>Two settings became one.</b> The search's per-language width and the walk's stop were two
    /// knobs describing the same intent, and at their equal defaults the walk's cap was checked FIRST
    /// and counted the same attempts — so <em>the cap always fired first and the correction budget
    /// could never trigger</em>. Measured by replaying the loop: at 3/3 the cap stopped the walk in
    /// every ordering, and the budget only ever won when the cap was raised above it, which was the
    /// keep-best raise that went with F-M319.
    /// </para>
    /// <para>
    /// The operator's rule is one setting with two effects: it decides <b>how many candidates per
    /// language are searched</b> and <b>how many candidates the Auto-Sync may pull before it gives
    /// up</b>.
    /// </para>
    /// <para>
    /// There was a SECOND parameter here, <c>keepBest</c> (F-M242), which raised both numbers to the
    /// "best subtitles to keep per language" count. Both the setting and that raise are GONE
    /// (F-M319): a language now receives exactly one corrected file.
    /// </para>
    /// </summary>
    /// <param name="correctionAttempts">The configured correction attempts.</param>
    /// <returns>The search's per-language width; 0 = no early stop.</returns>
    public static int SearchEarlyStopThreshold(int correctionAttempts)
        => correctionAttempts <= 0 ? 0 : correctionAttempts; // F-M50: 0 = unlimited, stays 0
}
