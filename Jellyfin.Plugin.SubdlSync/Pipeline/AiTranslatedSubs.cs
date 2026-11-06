// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version.
//
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Api;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// F-M449: one SubDL account publishes machine-translated subtitles. Whether those uploads may be
/// downloaded is a switch on its own (`Allow AI translated subs`, off by default), and the answer is
/// applied to the SEARCH RESULT — a candidate from that account never enters ranking, never costs a
/// download unit and never becomes a fallback for an item that has no other candidate.
/// <para>
/// The match runs on the uploader name of the API response, not on the release name and not on the
/// entry title: the account is the one stable carrier, while a machine-translated release can be
/// named anything. Spacing, dots, hyphens and underscores are removed before the comparison, so
/// "SUBDL AI", "subdl_ai" and "SubDL.AI" all land on the same key; the comparison is EQUALITY,
/// so a different account whose name merely starts the same way keeps its candidates.
/// </para>
/// </summary>
public static class AiTranslatedSubs
{
    /// <summary>Normalised uploader key of the machine-translation account.</summary>
    private const string Account = "subdlai";

    /// <summary>
    /// Whether an uploader name belongs to the machine-translation account.
    /// </summary>
    /// <param name="author">The <c>author</c> field of a search result.</param>
    /// <returns>True when the name normalises to the account key.</returns>
    public static bool IsAiUploader(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            return false;
        }

        var key = new string(author.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key.Equals(Account, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes every candidate of the machine-translation account from a pool, in place.
    /// </summary>
    /// <param name="pool">The candidate pool of one search; may be null or empty.</param>
    /// <returns>The number of candidates removed.</returns>
    public static int Remove(IList<SubtitleCandidate>? pool)
    {
        if (pool == null || pool.Count == 0)
        {
            return 0;
        }

        int removed = 0;
        for (int i = pool.Count - 1; i >= 0; i--)
        {
            if (IsAiUploader(pool[i].Author))
            {
                pool.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }
}
