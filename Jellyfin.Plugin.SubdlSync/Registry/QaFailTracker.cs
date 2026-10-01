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
using LiteDB;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// Per-(file, language) QA budget and the record of burned download candidates.
/// <para>
/// Two different things, kept apart on purpose:
/// </para>
/// <list type="bullet">
/// <item><description>the fail counter — how often this language failed for this file, used to give up after a configured number of tries</description></item>
/// <item><description>the burned candidates — which specific SubDL releases were already fetched and discarded, so they are never fetched again</description></item>
/// </list>
/// <para>
/// The burned candidates used to be stored as extra subtitle rows, which put a remote-release
/// verdict into the subtitle area where it does not belong and made the subtitle readers filter it
/// out again by direction. They now live in their own area (4) keyed by item + language + SubDL id,
/// which is the grain the verdict actually has.
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

    /// <summary>Counter key for the per-(item, language) fail budget.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    /// <returns>Namespaced counter key.</returns>
    public static string CounterKey(string itemId, string language)
        => "qa-fail:" + itemId + "|" + language.ToUpperInvariant();

    private CounterEntity GetCounter(string itemId, string language)
        => _db.Counters.FindOne(Query.EQ("Key", CounterKey(itemId, language)))
           ?? new CounterEntity { Key = CounterKey(itemId, language) };

    /// <summary>True when this language exhausted its retry budget for this file.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    /// <param name="limit">Configured limit; 0 or less disables giving up.</param>
    /// <returns>True when exhausted.</returns>
    public bool IsExhausted(string itemId, string language, int limit)
    {
        if (limit <= 0)
        {
            return false;
        }

        return GetCounter(itemId, language).Value >= limit;
    }

    /// <summary>SubDL release ids already fetched and discarded for this file and language.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    /// <returns>Discarded release ids.</returns>
    public List<string> GetSkippedCandidates(string itemId, string language)
    {
        // The language comparison runs in LINQ-to-objects, NOT inside the LiteDB query.
        // LiteDB cannot translate string.Equals(..., StringComparison.OrdinalIgnoreCase) and
        // emitted `(($.ItemId = @p0) AND (( = $.Language) = true))`, which failed the entire
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

    /// <summary>Increments the fail counter for this file and language.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    public void RecordFailure(string itemId, string language)
    {
        var e = GetCounter(itemId, language);
        e.Value++;
        e.Updated = DateTime.UtcNow;
        _db.Counters.Upsert(e);
        LogUtil.Detail(_logger, "[SubDL-DB] qa-fail {Item}|{Lang} = {Count}", itemId, language, e.Value);
    }

    /// <summary>
    /// Clears the fail budget and the burned candidates for this file and language — called after a
    /// real save, because a successful download proves the problem was not the file.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    public void RecordSuccess(string itemId, string language)
    {
        _db.Counters.DeleteMany(Query.EQ("Key", CounterKey(itemId, language)));
        _db.RejectedCandidates.DeleteMany(Query.And(
            Query.EQ("ItemId", itemId),
            Query.EQ("Language", language.ToUpperInvariant())));
        LogUtil.Detail(_logger, "[SubDL-DB] qa-fail reset {Item}|{Lang}", itemId, language);
    }

    /// <summary>Number of files with a QA fail budget.</summary>
    public int Count => _db.Counters.Count(Query.StartsWith("Key", "qa-fail:"));

    /// <summary>Removes counters and burned candidates of items that no longer exist.</summary>
    /// <param name="itemExists">Predicate over Jellyfin item ids.</param>
    /// <returns>Number of removed rows.</returns>
    public int PruneDeadItems(Func<string, bool> itemExists)
    {
        var deadItemIds = _db.Counters.FindAll()
            .Where(c => c.Key.StartsWith("qa-fail:", StringComparison.Ordinal)
                     && !itemExists(c.Key["qa-fail:".Length..].Split('|')[0]))
            .Select(c => c.Key["qa-fail:".Length..].Split('|')[0])
            .Concat(_db.RejectedCandidates.FindAll()
                .Where(c => !itemExists(c.ItemId))
                .Select(c => c.ItemId))
            .Distinct()
            .ToList();

        int removed = 0;
        foreach (var itemId in deadItemIds)
        {
            removed += _db.Counters.DeleteMany(Query.StartsWith("Key", CounterKey(itemId, string.Empty)));
            removed += _db.RejectedCandidates.DeleteMany(Query.EQ("ItemId", itemId));
        }

        if (removed > 0)
        {
            LogUtil.Detail(_logger, "[SubDL-DB] qa-fail prune {Removed}", removed);
        }

        return removed;
    }

    /// <summary>No-op flush: LiteDB writes at the point of the write.</summary>
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
