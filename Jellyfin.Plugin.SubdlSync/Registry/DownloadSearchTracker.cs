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

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubScribe.Data;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubScribe.Pipeline;

namespace Jellyfin.Plugin.SubScribe.Registry;

/// <summary>
/// Per-file download search tracker.
/// <para>
/// The search timestamp and the language list it belongs to live on the file record itself
/// (area 2, <see cref="MediaEntity.LastSearchUtc"/> / <see cref="MediaEntity.LastSearchLanguages"/>),
/// because that is what they describe: this file was searched, for these languages. The previous
/// separate table kept them next to a duplicated global "last languages" row and needed an explicit
/// reset whenever the configured languages changed.
/// </para>
/// <para>
/// No reset pass is needed any more: the comparison that reset was meant to achieve
/// ("was this file searched for the languages I want now?") is made per file at the moment of asking.
/// A stored list that differs from the current one simply makes the file due again.
/// </para>
/// </summary>
public sealed class DownloadSearchTracker
{
    private readonly ScribeDbContext _db;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadSearchTracker"/> class.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="logger">Optional logger.</param>
    /// <summary>
    /// F-M661: factor by which an EMPTY answer stretches the refetch gap. A title no source carries
    /// is not asked again every day — that spent ~2.000 SubDL searches per run on 1.160 targets, of
    /// which 1.128 came back empty. Any search that returns something clears the mark and the
    /// normal interval applies again. Capped so a title never sleeps longer than a month.
    /// </summary>
    public const int EmptySearchFactor = 7;

    /// <summary>The stretch of time an empty answer buys. Zero gap stays zero (never held back).</summary>
    /// <param name="gap">Configured refetch gap.</param>
    /// <returns>The gap to use while the last answer was empty.</returns>
    public static TimeSpan EmptyGap(TimeSpan gap)
        => gap <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromDays(Math.Min(30d, gap.TotalDays * EmptySearchFactor));

    public DownloadSearchTracker(ScribeDbContext db, ILogger? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Number of files that carry a search timestamp.</summary>
    public int Count => _db.Media.Count(x => x.LastSearchUtc != null);

    /// <summary>
    /// True when this Jellyfin item is due for a (re-)search: never searched, searched under a
    /// different language list, or the refetch gap has elapsed.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="gap">Refetch gap.</param>
    /// <param name="currentLanguages">Languages currently configured.</param>
    /// <returns>True when a search should run.</returns>
    public bool IsDue(string itemId, TimeSpan gap, IReadOnlyList<string> currentLanguages)
    {
        var media = FindByItemId(itemId);
        if (media?.LastSearchUtc == null)
        {
            return true;
        }

        var current = Normalize(currentLanguages);
        if (!string.Equals(media.LastSearchLanguages ?? string.Empty, current, StringComparison.OrdinalIgnoreCase))
        {
            // Searched under a different language list — that verdict does not cover what we want now.
            return true;
        }

        // F-M661: a search that came back empty does not buy the configured gap but the longer one
        // — the language list has already been checked above, so the mark belongs to what is asked
        // now. A search that found something clears the mark in MarkSearched.
        TimeSpan effective = media.LastSearchEmptyUtc != null ? EmptyGap(gap) : gap;
        return DateTime.UtcNow - media.LastSearchUtc.Value >= effective;
    }

    /// <summary>Is this item due, ignoring the language list.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="gap">Refetch gap.</param>
    /// <returns>True when due.</returns>
    public bool IsDue(string itemId, TimeSpan gap) => IsDue(itemId, gap, new List<string>());

    /// <summary>
    /// F-M234 (B): clears the search stamp of one item, so it is due again immediately.
    /// <para>
    /// Used when a stored "file complete" mark was proven wrong — the item must be reworked in
    /// THIS run. Leaving the stamp would make the refetch gate skip it and the correction would
    /// only take effect once the interval elapsed, which is exactly the silent delay being fixed.
    /// </para>
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <returns>True when a stamp was present and cleared.</returns>
    public bool ClearStamp(string itemId)
    {
        var media = FindByItemId(itemId);
        if (media?.LastSearchUtc == null)
        {
            return false;
        }

        media.LastSearchUtc = null;
        media.LastSearchLanguages = null;
        media.LastSearchEmptyUtc = null; // F-M661: der Leer-Merker haengt am Such-Stempel
        _db.Media.Update(media);
        LogUtil.Detail(_logger, "[Sub Scribe-DB] search stamp cleared for {Item} — rework due now", itemId);
        return true;
    }

    /// <summary>Records that a search ran for this item and which languages it covered.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="currentLanguages">Languages searched for.</param>
    public void MarkSearched(string itemId, IReadOnlyList<string> currentLanguages)
    {
        var media = FindByItemId(itemId);
        if (media == null)
        {
            LogUtil.Detail(_logger, "[Sub Scribe-DB] search stamp skipped — no media record for item {Item}", itemId);
            return;
        }

        media.LastSearchUtc = DateTime.UtcNow;
        media.LastSearchLanguages = Normalize(currentLanguages);

        // F-M661: dieser Stempel heisst "gefragt, und es kam etwas" — ein alter Leer-Merker faellt.
        media.LastSearchEmptyUtc = null;
        _db.Media.Update(media);
        LogUtil.Detail(_logger, "[Sub Scribe-DB] search stamp {Item} langs={Langs}", itemId, media.LastSearchLanguages);
    }

    /// <summary>Records a search with no language list.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    public void MarkSearched(string itemId) => MarkSearched(itemId, new List<string>());

    /// <summary>
    /// F-M661: notes that the search for this item came back EMPTY — no candidate from any asked
    /// source. The stamp only rides on a search that really ran (there must be a
    /// <see cref="MediaEntity.LastSearchUtc"/>), so an item that was never asked is not deferred.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <returns>True when the mark was stored.</returns>
    public bool MarkSearchEmpty(string itemId)
    {
        var media = FindByItemId(itemId);
        if (media?.LastSearchUtc == null)
        {
            return false;
        }

        media.LastSearchEmptyUtc = DateTime.UtcNow;
        _db.Media.Update(media);
        LogUtil.Detail(_logger, "[Sub Scribe-DB] empty search noted for {Item} — the next ask waits for the longer clock (F-M661)", itemId);
        return true;
    }

    /// <summary>
    /// Clears every search stamp, forcing all files due at the next run.
    /// <para>
    /// Not needed for a language change — the per-file comparison already handles that — but kept
    /// for the explicit reset action.
    /// </para>
    /// </summary>
    /// <returns>Number of stamps cleared.</returns>
    public int ResetAll()
    {
        var stamped = _db.Media.Find(x => x.LastSearchUtc != null).ToList();
        foreach (var media in stamped)
        {
            media.LastSearchUtc = null;
            media.LastSearchLanguages = null;
            media.LastSearchEmptyUtc = null; // F-M661
            _db.Media.Update(media);
        }

        LogUtil.Detail(_logger, "[Sub Scribe-DB] search stamps cleared: {Count}", stamped.Count);
        return stamped.Count;
    }

    /// <summary>Removes search stamps of files whose Jellyfin item is gone.</summary>
    /// <param name="itemExists">Predicate over Jellyfin item ids.</param>
    /// <returns>Number of stamps cleared.</returns>
    public int PruneDeadItems(Func<string, bool> itemExists)
    {
        var dead = _db.Media.Find(x => x.LastSearchUtc != null)
            .Where(m => !string.IsNullOrEmpty(m.JellyfinItemId) && !itemExists(m.JellyfinItemId!))
            .ToList();

        foreach (var media in dead)
        {
            media.LastSearchUtc = null;
            media.LastSearchLanguages = null;
            media.LastSearchEmptyUtc = null; // F-M661
            _db.Media.Update(media);
        }

        if (dead.Count > 0)
        {
            LogUtil.Detail(_logger, "[Sub Scribe-DB] search stamp prune {Removed}", dead.Count);
        }

        return dead.Count;
    }

    /// <summary>No-op flush: the store writes at the point of the update.</summary>
    public void Flush()
    {
    }

    private MediaEntity? FindByItemId(string itemId)
        => _db.Media.FindOne(x => x.JellyfinItemId == itemId);

    private static string Normalize(IReadOnlyList<string> languages)
        => string.Join(",", languages.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
}
