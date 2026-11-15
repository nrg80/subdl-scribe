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
    /// F-M487 (verschaerft durch Operator-Anweisung 15.11.2026: "Herkunft subdl: nie zurueck zu subdl, nie
    /// zu tvst"): darf dieser Untertitel zu jener Quelle?
    /// <para>
    /// UNBEKANNTE Herkunft ist ueberall erlaubt — eine Handkopie, eine eingebettete Spur oder eine alte
    /// Zeile sagt nichts darueber, woher der Text kommt, und sie abzuweisen hiesse, die eigenen Dateien des
    /// Operators nicht mehr zu veroeffentlichen. Eine BEKANNTE Herkunft ist NIRGENDS mehr erlaubt: was von
    /// SubDL heruntergeladen wurde, geht weder zurueck zu SubDL (Reup) noch zu tvsubtitles.net
    /// (Kreuzkontamination), und ein tvst-Download genauso wenig in die eine wie in die andere Richtung.
    /// Ein Download ist ein Fund, kein eigener Kandidat — er wird nicht veroeffentlicht.
    /// </para>
    /// <para>
    /// Der Parameter <paramref name="target"/> bleibt in der Signatur, weil jeder Aufrufer die Frage JE
    /// QUELLE stellt und der Grund im Log je Quelle genannt wird; die Antwort haengt seit der
    /// Verschaerfung nur noch an der Herkunft.
    /// </para>
    /// </summary>
    /// <param name="origin">The stored provenance (null/empty = not known).</param>
    /// <param name="target">The source that would take the upload.</param>
    /// <returns>True when the upload may go there.</returns>
    public static bool MayUploadTo(string? origin, SubtitleProvider target)
    {
        _ = target;
        return Parse(origin) is null;
    }

    /// <summary>
    /// F-M500: der Satz, mit dem eine gesperrte Herkunft im Log steht — er nennt WOHER der Text kommt und
    /// warum er nirgends mehr hochgeht. <c>null</c>, wenn die Herkunft unbekannt ist (dann sperrt nichts).
    /// </summary>
    /// <param name="origin">The stored provenance (null/empty = not known).</param>
    /// <returns>The sentence for the log, or null.</returns>
    public static string? OriginBlockedReason(string? origin)
        => Parse(origin) is { } bekannt
            ? "origin:" + Name(bekannt) + " — downloaded there, never uploaded"
            : null;

    /// <summary>
    /// F-M488 (operator decision 15.11.2026: "aber dann vergeben wir eine upload id"): an upload always
    /// gets a mark. When the source reports its own id, that id is used. When it does not — tvsubtitles.net
    /// answers the upload without one — the plugin mints its own, prefixed <c>own:</c> so nobody ever
    /// mistakes it for an id of the site. That is the difference between "we uploaded it and know where"
    /// and "we uploaded it and only know that it happened": both are recorded, neither is guessed.
    /// </summary>
    /// <param name="provider">The source that took the upload.</param>
    /// <param name="contentHash">The subtitle's content hash — it names the text we sent.</param>
    /// <param name="release">The release name the upload used.</param>
    /// <param name="utcNow">The moment of the upload (passed in, so a test can pin it).</param>
    /// <returns>A minted id, always prefixed <c>own:</c>.</returns>
    public static string OwnUploadId(
        SubtitleProvider provider, string contentHash, string release, DateTime utcNow)
    {
        var seed = contentHash + "|" + release + "|" + utcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        var hex = Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "own:{0}:{1}:{2}",
            Name(provider),
            utcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture),
            hex);
    }

    /// <summary>
    /// F-M488: true when a stored upload id is one of ours (minted), not the site's.
    /// </summary>
    /// <param name="uploadId">The stored value.</param>
    /// <returns>True for a minted id.</returns>
    public static bool IsOwnUploadId(string? uploadId) =>
        uploadId is not null && uploadId.StartsWith("own:", StringComparison.Ordinal);

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
