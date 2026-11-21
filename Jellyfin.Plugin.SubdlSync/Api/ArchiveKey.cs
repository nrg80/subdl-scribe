using System;
using System.Globalization;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// F-M370: the part of a download URL that names the ARCHIVE, not the entry inside it.
/// <para>
/// SubDL hands out one URL per archive entry, so a season pack that serves nine episodes
/// arrives as nine different URLs that share nothing but their first path segment — measured
/// on 31.10.2026: <c>Xhfe8mD1w5/5Ibw144l42</c>, <c>Xhfe8mD1w5/SUK5YnspZX</c>, … for one pack,
/// <c>0p9xSnSU4g/…</c> nine times, <c>BlwZSYtLaU/…</c> seven times. Keying a cache on the full
/// URL therefore never hits, and the same pack is downloaded ten times in one run.
/// </para>
/// <para>
/// The key comes from the URL alone — it is known before anything is fetched, so a cache can
/// answer without a request. Only characters that are safe as a file name pass; anything else
/// yields <c>null</c> and is simply not cached, rather than cached under a key that mixes two
/// archives.
/// </para>
/// </summary>
public static class ArchiveKey
{
    private const string SubtitlePath = "/subtitle/";

    /// <summary>Path marker of the v2 single-archive route.</summary>
    private const string V2SubtitlePath = "/api/v2/subtitles/";

    /// <summary>
    /// Builds the cache key for a download URL.
    /// </summary>
    /// <param name="fullUrl">Absolute download URL, with or without query string.</param>
    /// <returns>The archive segment, or <c>null</c> when the URL carries no usable one.</returns>
    public static string? FromUrl(string? fullUrl)
    {
        if (string.IsNullOrWhiteSpace(fullUrl))
        {
            return null;
        }

        int query = fullUrl.IndexOf('?', StringComparison.Ordinal);
        string path = query >= 0 ? fullUrl[..query] : fullUrl;

        int at = path.IndexOf(SubtitlePath, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            string rest = path[(at + SubtitlePath.Length)..].Trim('/');
            if (rest.Length == 0)
            {
                return null;
            }

            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            string segment = slash >= 0 ? rest[..slash] : rest;

            // …/subtitle/1778460-1115627.zip is a bare archive: the file name IS the archive,
            // and its dot would otherwise disqualify it.
            if (slash < 0 && segment.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                segment = segment[..^4];
            }

            return IsUsable(segment) ? segment : null;
        }

        // F-M451: the v2 single-archive route names its archive by subtitle id —
        // /api/v2/subtitles/<nId>/download?format=zip. Same archive, same key every time, so a
        // season pack fetched once is free for every further episode of that pack.
        int v2 = path.IndexOf(V2SubtitlePath, StringComparison.OrdinalIgnoreCase);
        if (v2 >= 0)
        {
            string rest = path[(v2 + V2SubtitlePath.Length)..].Split('/')[0].Trim();
            return IsUsable(rest) ? "v2-" + rest : null;
        }

        // Any other form: a bare zip at the end of the path is its own archive.
        string file = path[(path.LastIndexOf('/') + 1)..];
        if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string stem = file[..^4];
            return IsUsable(stem) ? stem : null;
        }

        return null;
    }

    /// <summary>
    /// Whether the URL can be cached at all, so the client can decide before it asks a cache a
    /// question the cache cannot answer.
    /// </summary>
    /// <param name="fullUrl">Absolute download URL.</param>
    /// <returns>Whether a key exists.</returns>
    public static bool IsCacheable(string? fullUrl) => FromUrl(fullUrl) != null;

    /// <summary>
    /// F-M665 (Bediener-Entscheidung 21.11.2026: „bevorzugt staffelpacks von tvst und die dann auch
    /// cachen"): der Schluessel einer Staffel-ZIP im gemeinsamen Archiv-Cache.
    /// <para>
    /// Der Cache gehoert keiner Quelle — welche Quelle eine Datei geliefert hat, steht im Schluessel.
    /// Eigener Raum je Quelle, und zwar so, dass eine Kollision nicht bloss unwahrscheinlich, sondern
    /// unmoeglich ist: <see cref="FromUrl"/> laesst nur Buchstaben, Ziffern, <c>_</c> und <c>-</c> zu,
    /// hier steht ein <b>Punkt</b>. Kein Schluessel der anderen Quelle kann also je so aussehen, und
    /// ein Archiv mit gleichem Namen oder gleicher Id landet unter einem anderen Namen.
    /// </para>
    /// <para>
    /// Warum ein Punkt und kein Doppelpunkt: der Cache legt seine Dateien unter <c>key + ".zip"</c> ab.
    /// Ein Doppelpunkt ist auf Windows kein zulaessiger Dateiname — dort fiele die tvst-Staffel still
    /// aus dem Cache, ohne dass irgendwo etwas rot wuerde.
    /// </para>
    /// </summary>
    /// <param name="showId">The site's show id (<c>tvshow-&lt;id&gt;.html</c>).</param>
    /// <param name="season">Season number.</param>
    /// <param name="language">The site's language code, e.g. <c>en</c>.</param>
    /// <returns>The key, or <c>null</c> when the triple does not name a cacheable archive.</returns>
    public static string? ForTvstSeason(int showId, int season, string? language)
    {
        if (showId <= 0 || season <= 0)
        {
            return null;
        }

        string lang = (language ?? string.Empty).Trim().ToLowerInvariant();
        if (lang.Length is < 2 or > 3)
        {
            return null;
        }

        foreach (char c in lang)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return null;
            }
        }

        return "tvst."
            + showId.ToString(CultureInfo.InvariantCulture) + "."
            + season.ToString(CultureInfo.InvariantCulture) + "."
            + lang;
    }

    private static bool IsUsable(string segment)
    {
        if (segment.Length is < 4 or > 64)
        {
            return false;
        }

        foreach (char c in segment)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
            {
                return false;
            }
        }

        return true;
    }
}
