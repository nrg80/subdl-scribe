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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe;

/// <summary>
/// Resolves the configured library selection to the concrete media PATHS it
/// covers, so a selection keeps working when libraries are NESTED.
/// </summary>
/// <remarks>
/// F-M189 (24.09.2026, user decision "das muss auch mit Unterverzeichnissen
/// funktionieren"): a library whose path sits INSIDE another library's path
/// (e.g. Test_SubDL = /data/movies2/Test_SubDL while Movies = /data/movies2)
/// does not own its items in Jellyfin — the scan logs "Found duplicate path",
/// the items stay in the OUTER library and <c>GetCollectionFolders</c> returns
/// that outer name. A name-only comparison therefore never matches the inner
/// selection: measured on the test server, selecting "Test_SubDL" queued 0 of
/// 26 files while the items resolved to "Movies". Matching the item PATH
/// against the selected libraries' locations fixes that without touching the
/// library layout, and stays correct in the ordinary non-nested case (an item
/// inside library X is under one of X's locations).
/// </remarks>
internal sealed class LibraryScope
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>Selected library name → its media roots (normalized, no trailing separator).</summary>
    private readonly List<Entry> _entries;

    /// <summary>Library names worth walking: the selection plus its nesting neighbours.</summary>
    private readonly HashSet<string> _relevantLibraryNames = new(StringComparer.OrdinalIgnoreCase);

    private LibraryScope(ILibraryManager libraryManager, List<Entry> entries, HashSet<string> relevant)
    {
        _libraryManager = libraryManager;
        _entries = entries;
        _relevantLibraryNames = relevant;
    }

    private sealed record Entry(string Name, List<string> Paths);

    /// <summary>Path comparison must follow the filesystem's case semantics.</summary>
    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>True when nothing is selected — callers treat that as a hard stop.</summary>
    internal bool IsEmpty => _entries.Count == 0;

    /// <summary>Selected library names (case-insensitive) — kept for logging.</summary>
    internal IEnumerable<string> Names => _entries.Select(e => e.Name);

    /// <summary>
    /// True when a whole library must be walked for this selection: either it IS
    /// selected, or it CONTAINS a selected library's path (the outer library of a
    /// nested selection), or it sits INSIDE a selected path. Without the
    /// containment cases a nested selection would never be enumerated — the outer
    /// library holds the items, the inner one holds none.
    /// </summary>
    internal bool IsLibraryRelevant(string? libraryName)
        => libraryName is { Length: > 0 } n && _relevantLibraryNames.Contains(n);

    /// <summary>
    /// Builds a scope from the configured library names. Resolving the locations
    /// costs one <c>GetVirtualFolders()</c> call, so build ONE scope per
    /// scan/run and reuse it for every item.
    /// </summary>
    internal static LibraryScope Create(ILibraryManager libraryManager, IEnumerable<string>? selected, ILogger? logger = null)
    {
        var names = new List<string>();
        foreach (var n in selected ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(n);
            }
        }

        var entries = new List<Entry>();
        List<(string Name, string Loc)>? all = null;

        if (names.Count > 0)
        {
            try
            {
                all = new List<(string, string)>();
                foreach (var folder in libraryManager.GetVirtualFolders())
                {
                    if (folder?.Name is not { Length: > 0 } fname)
                    {
                        continue;
                    }

                    foreach (var loc in folder.Locations ?? Enumerable.Empty<string>())
                    {
                        all.Add((fname, loc));
                    }
                }
            }
            catch (Exception ex)
            {
                // Fail-soft: the locations are an ADDITION to the name check, never a
                // replacement — losing them degrades nested libraries to the old
                // name-only behaviour instead of losing the scope entirely.
                logger?.LogWarning("[SubDL] Library locations unresolvable ({Msg}) — falling back to library names.", ex.Message);
            }
        }

        foreach (var name in names)
        {
            var paths = new List<string>();
            if (all != null)
            {
                foreach (var (fname, loc) in all)
                {
                    if (!string.Equals(fname, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var norm = Normalize(loc);
                    if (norm.Length > 0 && !paths.Contains(norm, PathComparer))
                    {
                        paths.Add(norm);
                    }
                }
            }

            entries.Add(new Entry(name, paths));
        }

        // Which libraries must be WALKED for this selection? The selected ones,
        // plus every library that contains or is contained by a selected path —
        // with a nested layout the items live in the outer library while the
        // selection names the inner one. Without this the pipeline's folder walk
        // finds nothing to filter and reports an empty run.
        var relevant = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (all != null)
        {
            var selectedPaths = entries.SelectMany(e => e.Paths).ToList();
            foreach (var (fname, loc) in all)
            {
                var norm = Normalize(loc);
                if (norm.Length == 0)
                {
                    continue;
                }

                bool overlaps = false;
                foreach (var sel in selectedPaths)
                {
                    if (IsUnder(norm, sel) || IsUnder(sel, norm))
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (overlaps)
                {
                    relevant.Add(fname);
                }
            }
        }

        return new LibraryScope(libraryManager, entries, relevant);
    }

    /// <summary>True when the item belongs to a selected library (by path or by name).</summary>
    internal bool Contains(BaseItem? item) => ResolveSelectedName(item) != null;

    /// <summary>
    /// The selected library that covers the item, or null when none does.
    /// Path match wins (the only check that survives nesting), the JF-resolved
    /// collection-folder name is the fallback.
    /// </summary>
    internal string? ResolveSelectedName(BaseItem? item)
    {
        if (item == null || _entries.Count == 0)
        {
            return null;
        }

        // 1) PATH — pure filesystem comparison, no JF lookup.
        var path = item.Path;
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var entry in _entries)
            {
                foreach (var root in entry.Paths)
                {
                    if (IsUnder(path, root))
                    {
                        return entry.Name;
                    }
                }
            }
        }

        // 2) NAME — covers items JF resolves to a selected library that has no
        //    usable location, and items whose Path is empty.
        try
        {
            foreach (var folder in _libraryManager.GetCollectionFolders(item))
            {
                if (folder?.Name is not { Length: > 0 } name)
                {
                    continue;
                }

                foreach (var entry in _entries)
                {
                    if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Name;
                    }
                }
            }
        }
        catch
        {
            // Name resolution is the FALLBACK — an exception here must not crash a
            // per-item path. Callers see "not selected" and log the aggregate.
        }

        return null;
    }

    /// <summary>True when the path lies under one of the selected libraries' roots.</summary>
    internal bool IsUnderAny(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var entry in _entries)
        {
            foreach (var root in entry.Paths)
            {
                if (IsUnder(path, root))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when the library NAME itself is selected (fast path for folder walks).</summary>
    internal bool IsSelectedName(string? libraryName)
        => libraryName is { Length: > 0 } n && _entries.Any(e => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Path lies at or below <paramref name="root"/>, on a directory boundary.</summary>
    internal static bool IsUnder(string path, string root)
    {
        if (path.Equals(root, PathComparison))
        {
            return true;
        }

        if (path.Length <= root.Length || !path.StartsWith(root, PathComparison))
        {
            return false;
        }

        // Boundary check: "/data/movies2Extra" must NOT match root "/data/movies2".
        var c = path[root.Length];
        return c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
    }

    /// <summary>Absolute path without a trailing directory separator.</summary>
    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
        }
        catch
        {
            return raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
