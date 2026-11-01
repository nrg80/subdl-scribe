using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// F-M372: the arithmetic and the bytes of a season pack — no I/O, no API, so the rules can be
/// tested without a library and without SubDL.
/// <para>
/// A pack exists only where a season is complete (see <see cref="SeasonEpisodeIndex"/>) and every
/// episode passed the same gates a single upload passes. What remains here are the three things
/// that decide identity: which episodes count, what the container is called, and what goes in it.
/// </para>
/// </summary>
public static partial class SeasonPack
{
    /// <summary>
    /// One item of a run, as far as the pack plan needs it: identity, series IMDb id and the
    /// position inside the season. Movies carry a null IMDb id and never join a pack.
    /// </summary>
    /// <param name="Id">Item id.</param>
    /// <param name="ImdbId">Series IMDb id (null for films and unresolved items).</param>
    /// <param name="Season">Season number.</param>
    /// <param name="Episode">Episode number.</param>
    public readonly record struct RunItem(string Id, string? ImdbId, int Season, int Episode);

    /// <summary>
    /// F-M373: the order a run works in. A season that goes up as a pack is closed in one go — its
    /// queued episodes run consecutively and in episode order — so the buffer never outlives a
    /// season and no season waits for the end of the run. Everything else keeps the order it had.
    /// </summary>
    /// <param name="items">The queued items of the run.</param>
    /// <param name="packable">Seasons whose queued episodes cover the whole aired season.</param>
    /// <returns>Item ids in working order.</returns>
    public static IReadOnlyList<string> ClosureOrder(
        IReadOnlyList<RunItem> items, ISet<(string ImdbId, int Season)> packable)
    {
        var grouped = items
            .Where(i => i.ImdbId != null && packable.Contains((i.ImdbId!, i.Season)))
            .OrderBy(i => i.ImdbId, StringComparer.Ordinal)
            .ThenBy(i => i.Season)
            .ThenBy(i => i.Episode)
            .Select(i => i.Id)
            .ToList();

        var rest = items
            .Where(i => i.ImdbId == null || !packable.Contains((i.ImdbId!, i.Season)))
            .Select(i => i.Id)
            .ToList();

        return grouped.Concat(rest).ToList();
    }

    /// <summary>The episode numbers that count as aired: everything above zero, distinct, ascending.</summary>
    /// <param name="raw">Episode numbers as the index reported them.</param>
    /// <returns>Aired set.</returns>
    public static IReadOnlyList<int> AiredNumbers(IEnumerable<int> raw)
        => raw.Where(n => n > 0).Distinct().OrderBy(n => n).ToList();

    /// <summary>
    /// True when the episodes we hold are exactly the season that aired — same set, no gap,
    /// no extra. A missing episode makes the pack a false claim, an extra one makes it unaskable.
    /// </summary>
    /// <param name="aired">Aired set.</param>
    /// <param name="present">Episode numbers we can actually deliver.</param>
    /// <returns>True when both sets are equal.</returns>
    public static bool IsComplete(IEnumerable<int> aired, IEnumerable<int> present)
    {
        var want = AiredNumbers(aired).ToList();
        var have = AiredNumbers(present).ToList();
        return want.Count > 0 && want.Count == have.Count && want.SequenceEqual(have);
    }

    /// <summary>
    /// The pack name <c>{imdb}.S{ss}.{pack hash}.{LANG}</c> — the <c>name</c> field SubDL uses as
    /// the zip name on download, so the downloaded file reads <c>{imdb}.S{ss}.{hash}.{LANG}.zip</c>.
    /// Same shape as a single upload, except that the hash describes the pack and not one subtitle.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="packHash">Lowercase MD5 over the episode content hashes.</param>
    /// <param name="language">SubDL language code.</param>
    /// <returns>File name.</returns>
    public static string Name(string imdbId, int season, string packHash, string language)
        => $"{imdbId}.S{season.ToString("00", CultureInfo.InvariantCulture)}.{packHash}.{language}";

    /// <summary>
    /// The string that has to stand FIRST in a pack's release list (F-M374). SubDL titles a
    /// full-season upload after the first element of `releases` and ignores the `name` field
    /// (measured 01.11.2026: name "MEIN-PACK-NAME" + releases ["RELEASE-MARKER-XYZ"] was listed
    /// as "RELEASE-MARKER-XYZ" in /user/mySubtitles). Without this the pack is titled after the
    /// first episode's release name and therefore reads like a single episode.
    /// </summary>
    /// <param name="seriesName">Series name when Jellyfin knows it; the IMDb id otherwise.</param>
    /// <param name="imdbId">IMDb id — fallback title.</param>
    /// <param name="season">Season number.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <returns>Pack title, also used as the download's ZIP name stem.</returns>
    public static string ReleaseLabel(string? seriesName, string imdbId, int season, string language, bool hearingImpaired)
    {
        string title = string.IsNullOrWhiteSpace(seriesName) ? imdbId : seriesName!.Trim();
        return $"{title} S{season.ToString("00", CultureInfo.InvariantCulture)} - complete season"
            + (hearingImpaired ? " (SDH)" : string.Empty)
            + $" [{language}]";
    }

    /// <summary>
    /// F-M374: the release list a season pack carries. Other uploaders name ONE season release in
    /// it (measured 01.11.2026 on tt0804484/tt0944947: "Foundation.2021.S03.COMPLETE.ATVP.WEB-DL…",
    /// "Game of Thrones, SEASON 8 4K x265 [23.976 FPS]") — never the episode releases, because
    /// SubDL titles the pack after the first entry. So each episode release name is reduced to its
    /// season form ("…S01E05…" → "…S01…"); where the episodes come from one release that leaves a
    /// single season name, where they do not, the distinct ones stand side by side, first episode
    /// first. A release name without any episode marker cannot be reduced and falls back to the
    /// neutral label.
    /// </summary>
    /// <param name="episodeReleaseNames">Release names in episode order.</param>
    /// <param name="seriesName">Series name when Jellyfin knows it.</param>
    /// <param name="imdbId">IMDb id — fallback title.</param>
    /// <param name="season">Season number.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <returns>Release list, at most three entries, season label as the last resort.</returns>
    public static IReadOnlyList<string> SeasonReleases(
        IEnumerable<string> episodeReleaseNames,
        string? seriesName,
        string imdbId,
        int season,
        string language,
        bool hearingImpaired)
    {
        string seasonTag = "S" + season.ToString("00", CultureInfo.InvariantCulture);
        var seen = new List<string>();
        foreach (string name in episodeReleaseNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string reduced = ReduceToSeason(name.Trim(), seasonTag);
            if (reduced.Length == 0 || seen.Contains(reduced, StringComparer.Ordinal))
            {
                continue;
            }

            seen.Add(reduced);
            if (seen.Count == 3)
            {
                break;
            }
        }

        if (seen.Count == 0)
        {
            seen.Add(ReleaseLabel(seriesName, imdbId, season, language, hearingImpaired));
        }

        return seen;
    }

    /// <summary>
    /// Cuts an episode marker out of a release name and leaves the season form behind.
    /// </summary>
    /// <param name="releaseName">Release name.</param>
    /// <param name="seasonTag">Season tag such as <c>S01</c>.</param>
    /// <returns>Season form, or an empty string when the name carries no episode marker.</returns>
    private static string ReduceToSeason(string releaseName, string seasonTag)
    {
        string reduced = EpisodeMarker().Replace(releaseName, seasonTag);
        if (string.Equals(reduced, releaseName, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        while (reduced.Contains("..", StringComparison.Ordinal))
        {
            reduced = reduced.Replace("..", ".", StringComparison.Ordinal);
        }

        return reduced.Trim(' ', '.', '-', '_');
    }

    /// <summary>
    /// Episode markers as they appear in release names: <c>S01E05</c>, <c>s1e5</c>, <c>1x05</c>.
    /// </summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"(?i)s\d{1,2}[\s._-]?e\d{1,4}|\d{1,2}x\d{1,4}")]
    private static partial Regex EpisodeMarker();

    /// <summary>
    /// The name a single subtitle upload carries (F-M29/F-M30). Lives here because the pack's
    /// inner entries must stay byte-identical in name to what a single upload would have sent.
    /// </summary>
    /// <param name="isSeries">Series or movie.</param>
    /// <param name="imdbId">IMDb id.</param>
    /// <param name="season">Season (TV) or 0.</param>
    /// <param name="episode">Episode (TV) or 0.</param>
    /// <param name="language">Language code.</param>
    /// <param name="contentHash">MD5 of the subtitle content.</param>
    /// <returns>File name.</returns>
    public static string NeutralFileName(bool isSeries, string imdbId, int season, int episode, string language, string contentHash)
        => isSeries
            ? $"{imdbId}.S{season.ToString("00", CultureInfo.InvariantCulture)}E{episode.ToString("00", CultureInfo.InvariantCulture)}.{contentHash}.{language}.srt"
            : $"{imdbId}.{contentHash}.{language}.srt";

    /// <summary>
    /// The pack's identity: MD5 over the episodes' content hashes in episode order.
    /// <para>
    /// There is no container to hash — the ZIPPING IS SUBDL'S (the Upload API takes one file per
    /// call, collects their <c>file_n_id</c>s and uses the <c>name</c> field as the zip name on
    /// download). The identity therefore describes the same thing the zip would have: which
    /// subtitles, in which order, with which text. A corrected episode yields a new hash, so a
    /// corrected season is a new pack.
    /// </para>
    /// </summary>
    /// <param name="contentHashesInEpisodeOrder">One content hash per episode, ascending.</param>
    /// <returns>Hex digest.</returns>
    public static string PackHash(IEnumerable<string> contentHashesInEpisodeOrder)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(string.Join("\n", contentHashesInEpisodeOrder)))).ToLowerInvariant();

    /// <summary>Lowercase MD5 of raw bytes — the same convention the subtitle hashes use.</summary>
    /// <param name="bytes">Bytes.</param>
    /// <returns>Hex digest.</returns>
    public static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
}
