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
/// F-M95 / F-M50 / F-M242: the ONE place that turns the two candidate settings into the
/// numbers the search and the download loop use.
/// <para>
/// The search used to receive a hard-coded 3 while its own comment named
/// <c>DownloadMaxCandidatesPerLanguage</c>, so raising the setting widened the download budget
/// but not the search: the page walk still stopped after three candidates per language and the
/// loop never saw a fourth. The two settings also contradicted each other in silence — with
/// <c>KeepBestPerLanguage = 4</c> and the default budget of 3 the pipeline could not write four
/// files, because the budget break fired first.
/// </para>
/// <para>
/// Both numbers are derived here so they cannot drift apart again.
/// </para>
/// </summary>
public static class DownloadBudget
{
    /// <summary>
    /// F-M50 / F-M242: how many candidates may be downloaded per (item, language) before the
    /// language counts as "not available".
    /// <para>
    /// The configured budget, raised to the keep-best count when the user asks for more files
    /// than the budget would allow to be tried: F-M242 promises that X means X numbered files,
    /// and a budget below X makes that unreachable. <c>0</c> means unlimited and stays unlimited.
    /// </para>
    /// </summary>
    public static int EffectiveDownloadCap(int configuredBudget, int keepBest)
    {
        if (configuredBudget <= 0)
        {
            return 0; // F-M50: 0 = unlimited
        }

        return Math.Max(configuredBudget, Math.Max(1, keepBest));
    }

    /// <summary>
    /// F-M95: the per-language early-stop threshold for the search page walk. <c>0</c> disables
    /// the early stop entirely (F-M50's "unlimited").
    /// <para>
    /// Deliberately the same number as <see cref="EffectiveDownloadCap"/>: fetching fewer
    /// candidates than the loop is allowed to try would make the download cap unreachable, and
    /// fetching more is quota spent on candidates the loop discards.
    /// </para>
    /// </summary>
    public static int SearchEarlyStopThreshold(int configuredBudget, int keepBest)
        => EffectiveDownloadCap(configuredBudget, keepBest);
}
