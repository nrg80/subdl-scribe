// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Data;
using LiteDB;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

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
    private readonly SubdlDbContext _db;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadSearchTracker"/> class.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="logger">Optional logger.</param>
    public DownloadSearchTracker(SubdlDbContext db, ILogger? logger = null)
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

        return DateTime.UtcNow - media.LastSearchUtc.Value >= gap;
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
        _db.Media.Update(media);
        LogUtil.Detail(_logger, "[SubDL-DB] search stamp cleared for {Item} — rework due now", itemId);
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
            LogUtil.Detail(_logger, "[SubDL-DB] search stamp skipped — no media record for item {Item}", itemId);
            return;
        }

        media.LastSearchUtc = DateTime.UtcNow;
        media.LastSearchLanguages = Normalize(currentLanguages);
        _db.Media.Update(media);
        LogUtil.Detail(_logger, "[SubDL-DB] search stamp {Item} langs={Langs}", itemId, media.LastSearchLanguages);
    }

    /// <summary>Records a search with no language list.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    public void MarkSearched(string itemId) => MarkSearched(itemId, new List<string>());

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
            _db.Media.Update(media);
        }

        LogUtil.Detail(_logger, "[SubDL-DB] search stamps cleared: {Count}", stamped.Count);
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
            _db.Media.Update(media);
        }

        if (dead.Count > 0)
        {
            LogUtil.Detail(_logger, "[SubDL-DB] search stamp prune {Removed}", dead.Count);
        }

        return dead.Count;
    }

    /// <summary>No-op flush: LiteDB writes at the point of the update.</summary>
    public void Flush()
    {
    }

    private MediaEntity? FindByItemId(string itemId)
        => _db.Media.FindOne(x => x.JellyfinItemId == itemId);

    private static string Normalize(IReadOnlyList<string> languages)
        => string.Join(",", languages.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
}
