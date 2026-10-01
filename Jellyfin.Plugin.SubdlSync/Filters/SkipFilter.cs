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

namespace Jellyfin.Plugin.SubdlScribe.Filters;

/// <summary>
/// Skip filters (F-M34, F-M35): configurable directory and filename patterns.
/// Matching is case-insensitive substring (no regex).
/// </summary>
public sealed class SkipFilter
{
    private readonly List<string> _dirPatterns;
    private readonly List<string> _filePatterns;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkipFilter"/> class.
    /// </summary>
    /// <param name="dirPatterns">Directory path substring patterns.</param>
    /// <param name="filePatterns">Filename substring patterns.</param>
    public SkipFilter(IEnumerable<string> dirPatterns, IEnumerable<string> filePatterns)
    {
        _dirPatterns = new List<string>(dirPatterns);
        _filePatterns = new List<string>(filePatterns);
    }

    /// <summary>
    /// Returns the matching skip reason for a media path, or null if it should be processed.
    /// </summary>
    public string? GetSkipReason(string mediaPath)
    {
        string p = mediaPath.ToLowerInvariant();
        foreach (string pattern in _dirPatterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern) && p.Contains(pattern.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                return $"skip-dir ({pattern.Trim()})";
            }
        }

        string fileName = System.IO.Path.GetFileName(p);
        foreach (string pattern in _filePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }
            // Fix 13.09.2026 (user decision "ok für Endung"): dot-patterns are file
            // EXTENSIONS (".part", ".tmp", ".!qb") and must match as suffix only —
            // substring matching produced false positives like "The.Hunting.Party"
            // (".party" contains ".part"). Bare-word patterns ("sample", "trailer",
            // "incomplete") stay substring by design (F-M34).
            string trimmed = pattern.Trim().ToLowerInvariant();
            if (trimmed.StartsWith('.'))
            {
                if (fileName.EndsWith(trimmed, StringComparison.Ordinal))
                {
                    return $"skip-pattern ({pattern.Trim()})";
                }
            }
            else if (fileName.Contains(trimmed, StringComparison.Ordinal))
            {
                return $"skip-pattern ({pattern.Trim()})";
            }
        }

        return null;
    }
}