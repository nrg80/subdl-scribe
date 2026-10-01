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
using Jellyfin.Plugin.SubdlScribe.Language;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// F-M234: what subtitle files are actually present on disk for a media file.
/// <para>
/// This exists because two different questions were being answered by one flag. "Is this item
/// finished?" is a stored verdict; "is this subtitle still there?" is a fact about the filesystem.
/// Only the second one can be re-checked, and it is the only thing that can prove a stored verdict
/// wrong — a deleted sidecar leaves the verdict intact and the item silently done forever.
/// </para>
/// <para>
/// F-M239: the NAME rules moved to <see cref="SidecarNaming"/> — the copy that used to live here did
/// not know the "sdh" marker and read <c>Movie.de.sdh.srt</c> as the phantom language "SD", which
/// made the refresh declare valid download marks stale. What stays in this class is the ROOT gate:
/// <see cref="RootsUsable"/> answers "may I trust a verdict of absent at all?", which is a question
/// about directories, not about file names.
/// </para>
/// </summary>
public static class SubtitlePresence
{
    /// <summary>
    /// F-M234 (C), fail-safe: true when every root exists and lists cleanly.
    /// <para>
    /// The same two-tier rule the oshash cache uses. An unmounted volume reports every path as
    /// missing; forgetting verdicts on that basis would destroy valid state, so a single unusable
    /// root means the caller must skip its deletions entirely. "Unknown" is never "deleted".
    /// </para>
    /// </summary>
    /// <summary>
    /// F-M234/F-M258: which of the requested languages have evidence SOMEWHERE — a sidecar file or
    /// an embedded track.
    /// <para>
    /// The refresh used to ask the disk alone and the pipeline asked the disk plus the embedded
    /// tracks, so the same file was judged differently by two parts of one plugin. On the live
    /// library that difference was not academic: 349 of 442 files carried their only German and
    /// English subtitles INSIDE the container, so the disk-only answer was "missing" and the
    /// refresh dropped 450 valid download marks in fourteen seconds. The configuration already says
    /// embedded tracks count (<c>DownloadOnlyMissing</c>); this is that rule, in one place, for the
    /// callers that judge a mark.
    /// </para>
    /// <para>
    /// No verdict is made here: the caller supplies what it could read. An unreadable directory
    /// still yields an empty set, so the caller must apply its own root gate first — "unknown" must
    /// not become "missing".
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <param name="embeddedLanguages">Languages read from the container, when they may count.</param>
    /// <returns>Languages with a sidecar or an embedded track.</returns>
    public static HashSet<string> CoveredLanguages(string mediaPath, IEnumerable<string>? embeddedLanguages)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(mediaPath))
        {
            foreach (var (_, lang, _) in SidecarNaming.List(mediaPath))
            {
                if (lang != null)
                {
                    covered.Add(lang);
                }
            }
        }

        foreach (var lang in embeddedLanguages ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrEmpty(lang))
            {
                covered.Add(lang);
            }
        }

        return covered;
    }

    /// <param name="roots">Candidate roots.</param>
    /// <returns>True when every root exists and can be listed.</returns>
    public static bool RootsUsable(IReadOnlyCollection<string>? roots)
    {
        if (roots == null || roots.Count == 0)
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return false;
            }

            try
            {
                using var e = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
                e.MoveNext();
            }
            catch
            {
                return false;
            }
        }

        return true;
    }
}
