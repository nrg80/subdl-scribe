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
using Jellyfin.Plugin.SubdlScribe.Configuration;

namespace Jellyfin.Plugin.SubdlScribe.Api.Endpoints;

/// <summary>
/// F-M486/F-M487 (operator order 15.11.2026: "Kreuzkontamination bitte verhindern — ein Download von
/// tvst soll nicht bei subdl hochgeladen werden und umgekehrt"): the provenance of a subtitle — which
/// source it came from — and the one rule that keeps the two sources apart.
/// <para>
/// The store carries the provenance per subtitle (<c>Origin</c>, <c>OriginId</c>, schema 10). It is the
/// only place the fact survives: the file on disk says nothing about where it came from, and a content
/// hash names the TEXT, not its origin.
/// </para>
/// </summary>
public static class Provenance
{
    /// <summary>The value written for SubDL.</summary>
    public const string Subdl = "subdl";

    /// <summary>The value written for tvsubtitles.net.</summary>
    public const string TvSubtitles = "tvsubtitles";

    /// <summary>
    /// F-M486: the store value for a source.
    /// </summary>
    /// <param name="provider">The source.</param>
    /// <returns>The value the store keeps.</returns>
    public static string Name(SubtitleProvider provider) => provider switch
    {
        SubtitleProvider.TvSubtitles => TvSubtitles,
        _ => Subdl,
    };

    /// <summary>
    /// F-M486: the source a stored value names, or null when the row does not say.
    /// </summary>
    /// <param name="origin">The stored value.</param>
    /// <returns>The source, or null.</returns>
    public static SubtitleProvider? Parse(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        if (string.Equals(origin.Trim(), TvSubtitles, StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleProvider.TvSubtitles;
        }

        return string.Equals(origin.Trim(), Subdl, StringComparison.OrdinalIgnoreCase)
            ? SubtitleProvider.Subdl
            : null;
    }

    /// <summary>
    /// F-M487: may this subtitle go to that source?
    /// <para>
    /// UNKNOWN provenance is allowed everywhere — a hand copy, an embedded track or an older row says
    /// nothing about where it came from, and refusing those would stop the plugin from uploading the
    /// operator's own files. A KNOWN provenance is allowed at its own source only: a subtitle fetched
    /// from tvsubtitles.net never becomes a SubDL upload, and a SubDL download never goes back out to
    /// tvsubtitles.net. That is the whole rule, and it is deliberately this short.
    /// </para>
    /// </summary>
    /// <param name="origin">The stored provenance (null/empty = not known).</param>
    /// <param name="target">The source that would take the upload.</param>
    /// <returns>True when the upload may go there.</returns>
    public static bool MayUploadTo(string? origin, SubtitleProvider target)
    {
        var known = Parse(origin);
        return known is null || known.Value == target;
    }

    /// <summary>
    /// F-M487: the sentence a refused cross-source upload is logged with — the same wording in the log
    /// and in the store's reason, so a looked-up row explains itself.
    /// </summary>
    /// <param name="origin">The stored provenance.</param>
    /// <param name="target">The source that was refused.</param>
    /// <returns>The reason line.</returns>
    public static string CrossSourceReason(string origin, SubtitleProvider target) =>
        "cross-source: subtitle came from " + origin + ", not uploading it to " + Name(target);
}
