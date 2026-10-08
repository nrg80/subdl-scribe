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
/// The search used to receive a hard-coded 3 while its own comment named
/// <c>DownloadMaxCandidatesPerLanguage</c>, so raising the setting widened the download budget but not
/// the search: the page walk still stopped after three candidates per language and the loop never saw a
/// fourth.
/// </para>
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
    /// F-M50: how many candidates may be downloaded per (item, language) before the language counts as
    /// "not available".
    /// <para>
    /// <c>0</c> means unlimited.
    /// </para>
    /// </summary>
    /// <param name="configuredBudget">The configured download budget.</param>
    /// <returns>The effective cap; 0 = unlimited.</returns>
    public static int EffectiveDownloadCap(int configuredBudget)
    {
        if (configuredBudget <= 0)
        {
            return 0; // F-M50: 0 = unlimited
        }

        return configuredBudget;
    }

    /// <summary>
    /// F-M95: the per-language early-stop threshold for the search page walk. <c>0</c> disables the
    /// early stop entirely (F-M50's "unlimited").
    /// <para>
    /// Deliberately the same number as <see cref="EffectiveDownloadCap(int)"/>: fetching fewer
    /// candidates than the loop is allowed to try would make the download cap unreachable, and fetching
    /// more is quota spent on candidates the loop discards.
    /// </para>
    /// </summary>
    /// <param name="configuredBudget">The configured download budget.</param>
    /// <returns>The early-stop threshold; 0 = no early stop.</returns>
    public static int SearchEarlyStopThreshold(int configuredBudget)
        => EffectiveDownloadCap(configuredBudget);
}
