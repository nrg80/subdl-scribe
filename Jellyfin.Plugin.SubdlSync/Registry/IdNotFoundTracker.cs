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
/// Per-item id-resolution failure tracker backed by LiteDB (F-M66).
/// </summary>
public sealed class IdNotFoundTracker
{
    private readonly SubdlDbContext _db;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public IdNotFoundTracker(SubdlDbContext db, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    private static string Key(string itemId) => "id-not-found:" + itemId;

    private CounterEntity Get(string itemId)
        => _db.Counters.FindOne(Query.EQ("Key", Key(itemId))) ?? new CounterEntity { Key = Key(itemId) };

    public bool IsExhausted(string itemId, int limit)
    {
        if (limit <= 0)
        {
            return false;
        }

        return Get(itemId).Value >= limit;
    }

    public int Peek(string itemId)
    {
        return Get(itemId).Value;
    }

    public void RecordFailure(string itemId)
    {
        var e = Get(itemId);
        e.Value++;
        e.Updated = DateTime.UtcNow;
        _db.Counters.Upsert(e);
        LogUtil.Detail(_logger, "[SubDL-DB] id-not-found failure {Item} = {Count}", itemId, e.Value);
    }

    public void RecordSuccess(string itemId)
    {
        _db.Counters.DeleteMany(Query.EQ("Key", Key(itemId)));
        LogUtil.Detail(_logger, "[SubDL-DB] id-not-found reset {Item}", itemId);
    }

    public int PruneDeadItems(Func<string, bool> itemExists)
    {
        var dead = _db.Counters.FindAll()
            .Where(c => c.Key.StartsWith("id-not-found:", StringComparison.Ordinal) && !itemExists(c.Key["id-not-found:".Length..]))
            .Select(c => c.Id)
            .ToList();

        var removed = _db.Counters.DeleteMany(Query.In("_id", dead.Select(id => new BsonValue(id)).ToArray()));
        if (removed > 0)
        {
            LogUtil.Detail(_logger, "[SubDL-DB] id-not-found prune {Removed}", removed);
        }
        return removed;
    }

    public void Flush()
    {
        // LiteDB writes immediately.
    }
}