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
using System.Runtime.InteropServices;
using Jellyfin.Plugin.SubdlScribe.Data;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// Central OSHash cache backed by the plugin's data store (F-M88c, F-M119).
/// </summary>
/// F-M61b: keyed by file path; a lookup validates size AND mtime and treats a mismatch as a miss,
/// so a replaced file is re-hashed automatically.
public sealed class OshashCache
{
    private readonly SubdlDbContext _db;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    private static StringComparer PathComparer { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public OshashCache(SubdlDbContext db, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public string? Lookup(string mediaPath, long size, long mtimeUnix, TimeSpan revalidateEvery, out DateTime validatedUtc)
    {
        validatedUtc = DateTime.MinValue;
        var e = _db.Oshashes.FindById(mediaPath);
        if (e == null || e.Size != size || e.MtimeUnix != mtimeUnix)
        {
            LogUtil.Detail(_logger, "[SubDL-DB] oshash lookup miss {Path}", mediaPath);
            return null;
        }

        validatedUtc = e.Updated;
        if (revalidateEvery != TimeSpan.Zero && e.Updated < DateTime.UtcNow - revalidateEvery)
        {
            LogUtil.Detail(_logger, "[SubDL-DB] oshash lookup expired {Path}", mediaPath);
            return null;
        }

        LogUtil.Detail(_logger, "[SubDL-DB] oshash lookup hit {Path}", mediaPath);
        return e.Hash;
    }

    public IReadOnlyList<(string Path, long Size, long MtimeUnix, long ValidatedUnix)> Snapshot()
    {
        return _db.Oshashes.FindAll()
            .Select(e => (e.Id, e.Size, e.MtimeUnix, (long)(e.Updated - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds))
            .ToList();
    }

    public void Store(string mediaPath, string hash, long size, long mtimeUnix)
    {
        _db.Oshashes.Upsert(new OshashEntity
        {
            Id = mediaPath,
            Hash = hash,
            Size = size,
            MtimeUnix = mtimeUnix,
            Updated = DateTime.UtcNow
        });
        LogUtil.Detail(_logger, "[SubDL-DB] oshash store {Path} {Hash}", mediaPath, hash);
    }





    public void Remove(string mediaPath)
    {
        _db.Oshashes.Delete(mediaPath);
        LogUtil.Detail(_logger, "[SubDL-DB] oshash remove {Path}", mediaPath);
    }

    public int PruneStalePaths(IReadOnlyList<string> libraryRoots)
    {
        if (libraryRoots == null || libraryRoots.Count == 0)
        {
            return 0;
        }

        foreach (var root in libraryRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return 0;
            }

            try
            {
                using var e = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
                e.MoveNext();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        var dead = _db.Oshashes.FindAll()
            .Where(e => !File.Exists(e.Id))
            .Select(e => e.Id)
            .ToList();

        foreach (var p in dead)
        {
            _db.Oshashes.Delete(p);
        }

        return dead.Count;
    }

    /// <summary>
    /// Derive the distinct roots a set of stored paths lives under — "/data/movies2/..." -> "/data/movies2".
    /// <para>
    /// Shared by the refresh's two file-side sweeps (the OSHash cache and the media rows) so they cannot
    /// disagree about whether a volume is present. A fail-safe that guards one and not the other would
    /// let a dead mount be refused by the cache sweep and accepted by the media sweep in the SAME run.
    /// </para>
    /// </summary>
    /// <param name="paths">Stored paths.</param>
    /// <returns>Distinct roots.</returns>
    public static List<string> RootsFromPaths(IEnumerable<string> paths)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            // two levels below filesystem root: "/data/movies2/..." → "/data/movies2"
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                roots.Add("/" + string.Join('/', parts[0], parts[1]));
            }
            else if (parts.Length == 1)
            {
                roots.Add("/" + parts[0]);
            }
        }

        return roots.ToList();
    }

    public IReadOnlyList<string> GetAllPaths()
    {
        return _db.Oshashes.FindAll().Select(e => e.Id).ToList();
    }

    public void Flush()
    {
        // The store writes at the point of the write — nothing to do.
    }
}

