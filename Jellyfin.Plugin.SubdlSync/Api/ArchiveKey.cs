using System;

namespace Jellyfin.Plugin.SubdlScribe.Api;

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
