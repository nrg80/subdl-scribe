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
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Data;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// F-M320 (operator order 08.10.2026): the FAIL COUNTER is GONE. It counted saveless runs of a
/// (item, language) pair and closed the pair once it reached the limit, and both readers of that
/// verdict — the queue gate and the pipeline's work list — were removed with it. The operator's rule
/// is that <c>DownloadQaRetryLimit</c> bounds the FIT (F-M318), not the other gates, so the value is
/// read from the configuration where the fit needs it and nothing counts rejections here any more.
/// <para>
/// What remains is the ONLY thing this class was ever asked to remember: which specific SubDL
/// releases were already fetched and discarded, so the next run walks fresh candidates instead of
/// paying for the same mistake twice (F-M200).
/// </para>
/// </summary>
public sealed class QaFailTracker
{
    private readonly SubdlDbContext _db;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QaFailTracker"/> class.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="logger">Optional logger.</param>
    public QaFailTracker(SubdlDbContext db, ILogger? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>SubDL release ids already fetched and discarded for this file and language.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    /// <returns>Discarded release ids.</returns>
    public List<string> GetSkippedCandidates(string itemId, string language)
    {
        // The language comparison runs in LINQ-to-objects, NOT pushed down as SQL.
        // No provider can translate string.Equals(..., StringComparison.OrdinalIgnoreCase) and
        // emitted `(($.ItemId = @p0) AND (( = $.Language) = true))`, which failed the entire
        // F-M193a: no construct the database cannot translate may cross into a query.
        // download cycle with "Invalid BsonExpression when converted from Linq expression".
        // Same pattern as ContentHashRegistry.GetRejectedCandidates; the ItemId index still
        // does the heavy lifting.
        return _db.RejectedCandidates
            .Find(x => x.ItemId == itemId)
            .Where(x => string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.SubdlId)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Remembers discarded download candidates. Upserts on item + language + release id, so
    /// re-recording one is an update, never a second row.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    /// <param name="subdlIds">Release ids to remember.</param>
    /// <param name="reason">Why they were discarded.</param>
    public void RecordSkippedCandidates(string itemId, string language, IEnumerable<string> subdlIds,
                                        string reason = RejectReason.CandidateRejected)
    {
        int recorded = 0;
        foreach (var id in subdlIds.Where(IsUploadId).Distinct())
        {
            // F-M200: the verdict is burned here, keyed by item + language + SubDL id.
            _db.RejectedCandidates.Upsert(new RejectedCandidateEntity
            {
                Id = SubdlDbContext.CandidateKey(itemId, language, id),
                ItemId = itemId,
                Language = language.ToUpperInvariant(),
                SubdlId = id,
                Reason = reason,
                Updated = DateTime.UtcNow
            });
            recorded++;
        }

        if (recorded > 0)
        {
            PruneOldest(itemId, language);
            LogUtil.Detail(_logger, "[SubDL-DB] burned candidates {Item}|{Lang} +{Count}", itemId, language, recorded);
        }
    }

    /// <summary>
    /// Clears the burned candidates for this file and language — called after a real save, because a
    /// successful download proves these discards do not apply to this file any more.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    public void RecordSuccess(string itemId, string language)
    {
        // The stored language is the UPPER-CASE form the key builder wrote, so the comparison is
        // ordinal and exact. It must not become a case-insensitive one: OrdinalIgnoreCase would also
        // match a row stored in another case, which the previous engine's exact EQ never did.
        var upper = language.ToUpperInvariant();
        _db.RejectedCandidates.DeleteMany(c => c.ItemId == itemId && c.Language == upper);
        LogUtil.Detail(_logger, "[SubDL-DB] burned candidates reset {Item}|{Lang}", itemId, language);
    }

    /// <summary>Removes the burned candidates of items that no longer exist.</summary>
    /// <param name="itemExists">Predicate over Jellyfin item ids.</param>
    /// <returns>Number of removed rows.</returns>
    public int PruneDeadItems(Func<string, bool> itemExists)
    {
        var deadItemIds = _db.RejectedCandidates.FindAll()
            .Where(c => !itemExists(c.ItemId))
            .Select(c => c.ItemId)
            .Distinct()
            .ToList();

        int removed = 0;
        foreach (var itemId in deadItemIds)
        {
            removed += _db.RejectedCandidates.DeleteMany(c => c.ItemId == itemId);
        }

        if (removed > 0)
        {
            LogUtil.Detail(_logger, "[SubDL-DB] burned candidate prune {Removed}", removed);
        }

        return removed;
    }

    /// <summary>No-op flush: the store writes at the point of the write.</summary>
    public void Flush()
    {
    }

    /// <summary>
    /// Keeps the newest 50 burned candidates per file and language, so a pathological sequence of
    /// candidates cannot grow the record without bound.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    private void PruneOldest(string itemId, string language)
    {
        // Language filtered in LINQ-to-objects — see GetSkippedCandidates for why.
        var all = _db.RejectedCandidates
            .Find(x => x.ItemId == itemId)
            .Where(x => string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Updated)
            .ToList();

        if (all.Count <= 50)
        {
            return;
        }

        foreach (var old in all.Skip(50))
        {
            _db.RejectedCandidates.Delete(old.Id);
        }
    }

    /// <summary>
    /// True for a plausible SubDL upload id: one or two all-digit segments.
    /// </summary>
    /// <param name="s">Candidate id.</param>
    /// <returns>True when it looks like an id.</returns>
    private static bool IsUploadId(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        var parts = s.Split('-');
        return parts.Length is 1 or 2 && parts.All(p => p.Length > 0 && p.All(char.IsDigit));
    }
}
