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
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Data;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// Per-item file-access/extraction retry tracker backed by the plugin's data store (F-M60).
/// </summary>
public sealed class FileRetryTracker
{
    private readonly SubdlDbContext _db;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public FileRetryTracker(SubdlDbContext db, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    private static string Key(string itemId) => "file-retry:" + itemId;

    private CounterEntity Get(string itemId)
        => _db.Counters.FindOne(c => c.Key == Key(itemId)) ?? new CounterEntity { Key = Key(itemId) };

    public bool IsExhausted(string itemId, int limit)
    {
        if (limit <= 0)
        {
            return false;
        }

        return (Get(itemId).Value) >= limit;
    }

    public void RecordFailure(string itemId)
    {
        var e = Get(itemId);
        e.Value++;
        e.Updated = DateTime.UtcNow;
        _db.Counters.Upsert(e);
        LogUtil.Detail(_logger, "[SubDL-DB] file-retry failure {Item} = {Count}", itemId, e.Value);
    }

    public void RecordSuccess(string itemId)
    {
        _db.Counters.DeleteMany(c => c.Key == Key(itemId));
        LogUtil.Detail(_logger, "[SubDL-DB] file-retry reset {Item}", itemId);
    }

    public int PruneDeadItems(Func<string, bool> itemExists)
    {
        var dead = _db.Counters.FindAll()
            .Where(c => c.Key.StartsWith("file-retry:", StringComparison.Ordinal) && !itemExists(c.Key["file-retry:".Length..]))
            .Select(c => c.Key)
            .ToList();

        var removed = _db.Counters.DeleteMany(c => dead.Contains(c.Key));
        if (removed > 0)
        {
            LogUtil.Detail(_logger, "[SubDL-DB] file-retry prune {Removed}", removed);
        }
        return removed;
    }

    public int Count => _db.Counters.FindAll().Count(c => c.Key.StartsWith("file-retry:", StringComparison.Ordinal));

    public void Flush()
    {
        // The store writes at the point of the write — nothing to do.
    }
}