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
    /// F-M374: the one entry a season pack carries in `releases` — SubDL titles the pack after it
    /// and ignores `name`. Other uploaders put a season release there (measured 01.11.2026 on
    /// tt0804484/tt0944947: "Foundation.2021.S03.COMPLETE.ATVP.WEB-DL…", "Game of Thrones, SEASON 8
    /// 4K x265 [23.976 FPS]"), never a list of episode releases — and this plugin mixes nothing:
    /// only when EVERY episode reduces to the same season release ("…S01E05…" → "…S01…") is that
    /// release named. Different groups name no release at all; the neutral label stands in, so the
    /// pack never claims a source it does not have.
    /// </summary>
    /// <param name="episodeReleaseNames">Release names in episode order.</param>
    /// <param name="seriesName">Series name when Jellyfin knows it.</param>
    /// <param name="imdbId">IMDb id — fallback title.</param>
    /// <param name="season">Season number.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <param name="builtRelease">
    /// F-M376: what the media files themselves say about the season — resolution, source, codec.
    /// Used when no file name carries a release, because a pack that names nothing says nothing.
    /// </param>
    /// <returns>The pack's single release entry.</returns>
    public static string PackRelease(
        IEnumerable<string> episodeReleaseNames,
        string? seriesName,
        string imdbId,
        int season,
        string language,
        bool hearingImpaired,
        string? builtRelease = null)
    {
        string? fromNames = SeasonReleaseFromNames(episodeReleaseNames, season);
        if (fromNames != null)
        {
            return fromNames;
        }

        return !string.IsNullOrWhiteSpace(builtRelease)
            ? builtRelease!
            : ReleaseLabel(seriesName, imdbId, season, language, hearingImpaired);
    }

    /// <summary>
    /// F-M378: the name one episode carries INSIDE a pack. It must not state the season: SubDL reads
    /// season and episode from the file name, and a member that names a season turns the whole
    /// submission into an episode range instead of a full season.
    /// </summary>
    /// <param name="episode">Episode number.</param>
    /// <param name="episodeTitle">Episode title as Jellyfin knows it, when it does.</param>
    /// <param name="language">SubDL language code.</param>
    /// <returns>Name such as <c>01 The Emperor's Peace.en.srt</c>.</returns>
    public static string MemberFileName(int episode, string? episodeTitle, string language)
    {
        string number = episode.ToString("00", CultureInfo.InvariantCulture);
        string title = (episodeTitle ?? string.Empty).Trim();

        // The set is fixed, not the platform's: the pack lands on SubDL's disk, where a name has to
        // survive every filesystem, and Linux' own list would let `:` and `?` through.
        foreach (char bad in "\\/:*?\"<>|")
        {
            title = title.Replace(bad.ToString(), " ", StringComparison.Ordinal);
        }

        title = string.Join(' ', title.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string stem = title.Length == 0 ? number : number + " " + title;
        return stem + "." + language.ToLowerInvariant() + ".srt";
    }

    /// <summary>
    /// F-M376: a season release built from facts the file itself carries — resolution and codec from
    /// the video stream, the documented source quality, and the series name. No group is invented: the
    /// name ends where the facts end. Returns null when the file says nothing usable.
    /// </summary>
    /// <param name="seriesName">Series name when Jellyfin knows it.</param>
    /// <param name="imdbId">IMDb id — stands in for a missing series name.</param>
    /// <param name="season">Season number.</param>
    /// <param name="quality">Documented quality code (web, bluray, dvd, hdtv, cam).</param>
    /// <param name="height">Video height in pixels, when the stream reports one.</param>
    /// <param name="codec">Video codec name from the stream.</param>
    /// <returns>Release-shaped name, or null.</returns>
    public static string? MediaRelease(
        string? seriesName,
        string imdbId,
        int season,
        string? quality,
        int? height,
        string? codec)
    {
        string? resolution = ResolutionToken(height);
        string? source = quality?.Trim().ToLowerInvariant() switch
        {
            "web" => "WEB",
            "bluray" => "BluRay",
            "dvd" => "DVD",
            "hdtv" => "HDTV",
            "cam" => "CAM",
            _ => null,
        };
        string? videoCodec = CodecToken(codec);

        if (resolution == null && source == null && videoCodec == null)
        {
            return null;
        }

        string series = string.IsNullOrWhiteSpace(seriesName)
            ? imdbId.Trim()
            : string.Join('.', seriesName!.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var parts = new List<string>
        {
            series,
            "S" + season.ToString("00", CultureInfo.InvariantCulture),
        };

        if (resolution != null)
        {
            parts.Add(resolution);
        }

        if (source != null)
        {
            parts.Add(source);
        }

        if (videoCodec != null)
        {
            parts.Add(videoCodec);
        }

        return string.Join('.', parts);
    }

    /// <summary>
    /// F-M376: the resolution token a release name would carry.
    /// </summary>
    /// <param name="height">Video height in pixels.</param>
    /// <returns>Token such as <c>1080p</c>, or null.</returns>
    public static string? ResolutionToken(int? height)
    {
        if (height is null || height <= 0)
        {
            return null;
        }

        int h = height.Value;
        return h switch
        {
            >= 2000 => "2160p",
            >= 1000 => "1080p",
            >= 700 => "720p",
            >= 560 => "576p",
            >= 460 => "480p",
            _ => null,
        };
    }

    /// <summary>
    /// F-M376: the codec token a release name would carry.
    /// </summary>
    /// <param name="codec">Codec name as the stream reports it.</param>
    /// <returns>Token such as <c>x265</c>, or null.</returns>
    public static string? CodecToken(string? codec)
    {
        return codec?.Trim().ToLowerInvariant() switch
        {
            "h264" or "avc" => "x264",
            "hevc" or "h265" => "x265",
            "av1" => "AV1",
            "vp9" => "VP9",
            "mpeg2video" or "mpeg2" => "MPEG2",
            _ => null,
        };
    }

    /// <summary>
    /// The season release every episode name agrees on, or null when they do not agree.
    /// </summary>
    /// <param name="episodeReleaseNames">Release names in episode order.</param>
    /// <param name="season">Season number.</param>
    /// <returns>Season form, or null.</returns>
    private static string? SeasonReleaseFromNames(IEnumerable<string> episodeReleaseNames, int season)
    {
        string seasonTag = "S" + season.ToString("00", CultureInfo.InvariantCulture);
        string? common = null;
        foreach (string name in episodeReleaseNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string reduced = ReduceToSeason(name.Trim(), seasonTag);
            if (reduced.Length == 0)
            {
                return null;
            }

            if (common is null)
            {
                common = reduced;
            }
            else if (!string.Equals(common, reduced, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return common;
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
