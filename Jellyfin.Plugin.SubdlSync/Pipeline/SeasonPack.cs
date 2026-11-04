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
    /// F-M435 (operator order 03.11.2026, "Log bitte korrigieren"): the language tag the season-pack
    /// log lines print. "EN" alone named two different things — the SDH text and the non-HI text of
    /// the same language are separate rows and separate packs, and the log made them look like one.
    /// Every pack line goes through here so none can print the bare language again.
    /// </summary>
    /// <param name="language">Language code as stored, e.g. EN.</param>
    /// <param name="hearingImpaired">Whether the pack carries the hearing-impaired variant.</param>
    /// <returns>The tag to print inside the brackets.</returns>
    public static string LangTag(string language, bool hearingImpaired)
        => language + (hearingImpaired ? " HI" : " non-HI");

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
    /// F-M389 (operator order 02.11.2026): the aired episodes a buffer does not cover — the ones a
    /// pack still needs, in episode order.
    /// <para>
    /// This is the pack's question turned around. <see cref="IsComplete"/> answers "is the season
    /// whole"; this answers "which episode is missing", because a season may be filled from the
    /// subtitles that are already on SubDL instead of being dissolved into singles.
    /// </para>
    /// </summary>
    /// <param name="aired">Aired set.</param>
    /// <param name="covered">Episode numbers the buffer already holds.</param>
    /// <returns>The aired numbers nobody covers, ascending; empty when the buffer is the season.</returns>
    public static IReadOnlyList<int> Uncovered(IEnumerable<int> aired, IEnumerable<int> covered)
    {
        var have = covered.ToHashSet();
        return AiredNumbers(aired).Where(e => !have.Contains(e)).ToList();
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
    /// <param name="hi">True for the hearing-impaired (SDH) variant; appended to the name.</param>
    /// <returns>File name.</returns>
    public static string Name(string imdbId, int season, string packHash, string language, bool hi = false)
        => $"{imdbId}.S{season.ToString("00", CultureInfo.InvariantCulture)}.{packHash}.{language}{HiMarker(hi)}";

    /// <summary>
    /// The suffix that states the hearing-impaired (SDH) variant in a NAME. SubDL carries the
    /// variant as the <c>hi</c> field, and that field ends where the name begins: whoever reads the
    /// zip, the member file or the entry title sees only the name. Empty for the regular track, so
    /// no existing name changes shape.
    /// </summary>
    /// <param name="hi">True for the SDH/HI variant.</param>
    /// <returns><c>.SDH</c> for the variant, the empty string otherwise.</returns>
    public static string HiMarker(bool hi) => hi ? ".SDH" : string.Empty;

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
    /// F-M378: the name one episode carries INSIDE a pack. It MUST state the season and the episode:
    /// SubDL reads both from the member's own file name, and a member without them is listed as
    /// <c>S00E05</c> — the pack then hangs under season zero.
    /// </summary>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="episodeTitle">Episode title as Jellyfin knows it, when it does.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hi">True for the hearing-impaired (SDH) variant; appended to the name.</param>
    /// <returns>Name such as <c>01 The Emperor's Peace.en.srt</c>.</returns>
    public static string MemberFileName(int season, int episode, string? episodeTitle, string language, bool hi = false)
    {
        string number = "S" + season.ToString("00", CultureInfo.InvariantCulture)
            + "E" + episode.ToString("00", CultureInfo.InvariantCulture);
        string title = (episodeTitle ?? string.Empty).Trim();

        // The set is fixed, not the platform's: the pack lands on SubDL's disk, where a name has to
        // survive every filesystem, and Linux' own list would let `:` and `?` through.
        foreach (char bad in "\\/:*?\"<>|")
        {
            title = title.Replace(bad.ToString(), " ", StringComparison.Ordinal);
        }

        title = string.Join(' ', title.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string stem = title.Length == 0 ? number : number + " - " + title;
        return stem + "." + language.ToLowerInvariant() + HiMarker(hi) + ".srt";
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
    /// <param name="width">Video width in pixels, when the stream reports one.</param>
    /// <param name="height">Video height in pixels, when the stream reports one — fallback only.</param>
    /// <param name="codec">Video codec name from the stream.</param>
    /// <param name="releaseGroup">Group the media file name states, if any.</param>
    /// <returns>Release-shaped name, or null.</returns>
    public static string? MediaRelease(
        string? seriesName,
        string imdbId,
        int season,
        string? quality,
        int? width,
        int? height,
        string? codec,
        string? releaseGroup = null)
    {
        string? resolution = ResolutionToken(width, height);
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

        // F-M381: a series name is not a release name yet — "Dexter: Resurrection" became
        // "Dexter:.Resurrection", and that colon travelled into SubDL's zip name. The same character
        // set the member names already strip is stripped here, so the name survives a filesystem.
        string rawSeries = string.IsNullOrWhiteSpace(seriesName) ? imdbId.Trim() : seriesName!.Trim();
        foreach (char bad in "\\/:*?\"<>|")
        {
            rawSeries = rawSeries.Replace(bad.ToString(), string.Empty, StringComparison.Ordinal);
        }

        string series = string.Join('.', rawSeries.Split(' ', StringSplitOptions.RemoveEmptyEntries));

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

        string joined = string.Join('.', parts);
        return string.IsNullOrWhiteSpace(releaseGroup) ? joined : joined + "-" + releaseGroup!.Trim();
    }

    /// <summary>
    /// F-M376: the resolution token a release name would carry.
    /// </summary>
    /// <param name="width">Video width in pixels — decides when known.</param>
    /// <param name="height">Video height in pixels — only the fallback when the width is unknown.</param>
    /// <returns>Token such as <c>1080p</c>, or null.</returns>
    /// <remarks>
    /// F-M380 (measured 01.11.2026, Dexter: Resurrection): the WIDTH decides, not the height. A
    /// cropped 1080p encode reads 1920x800, and 800 fell into the <c>&gt;= 700</c> bucket — the pack was
    /// named 720p while the file is 1080p class. The height is only consulted when no width was reported,
    /// which keeps the old behaviour for sources that expose height alone.
    /// </remarks>
    public static string? ResolutionToken(int? width, int? height)
    {
        if (width is > 0)
        {
            int w = width.Value;
            return w switch
            {
                >= 3800 => "2160p",
                >= 1900 => "1080p",
                >= 1250 => "720p",
                >= 1000 => "576p",
                >= 700 => "480p",
                _ => null,
            };
        }

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
    /// The release group a media file name states, if it states one. Nothing is invented: a name that
    /// ends in an episode title yields null, and so does a technical token such as <c>-AAC5.1</c>.
    /// </summary>
    /// <param name="releaseName">File name without extension, or a release name.</param>
    /// <returns>Group token such as <c>Rapta</c>, or null.</returns>
    /// <remarks>
    /// F-M430 (operator order 03.11.2026: "60 Prozent ist zuwenig"): the old rule — the last
    /// <c>-</c>-segment and nothing else — read one of the four spellings the library uses. The reason
    /// for the misses was shape, not absence:
    /// <list type="bullet">
    /// <item><description><c>…x264-NeoNoir</c> — dash.</description></item>
    /// <item><description><c>…x264.YIFY</c> — dot-separated (scene releases).</description></item>
    /// <item><description><c>…HEVC x265 BONE</c>, <c>…(1080p AMZN WEB-DL x265 Ghost)</c> — space-separated, sometimes inside brackets.</description></item>
    /// <item><description><c>…x265-MeGusta[EZTVx.to]</c>, <c>…x264.[MPup]</c>, <c>…x264-[YTS.LT]</c> — a site tag or a bracketed group behind the group.</description></item>
    /// </list>
    /// The rule now: strip a trailing bracket or parenthesis (and read inside it only when the outside
    /// states nothing), then take the last token after <c>-</c>, <c>.</c> or a space — provided the token
    /// before it is a TECHNICAL token (codec, source, audio, resolution, bit depth, HDR, edition). That
    /// predecessor is what keeps an episode title out: <c>…S01E03.OutKast</c> has an episode tag in front
    /// of it, not a codec, so the answer stays null. A name that states no release at all states no group
    /// either — nothing is invented.
    /// </remarks>
    public static string? ReleaseGroup(string? releaseName)
    {
        return ReleaseGroup(releaseName, 0);
    }

    /// <summary>The recursive worker behind <see cref="ReleaseGroup(string?)"/>.</summary>
    private static string? ReleaseGroup(string? releaseName, int depth)
    {
        if (string.IsNullOrWhiteSpace(releaseName) || depth > 3)
        {
            return null;
        }

        string name = releaseName.Trim();

        // A trailing bracket or parenthesis hides two things at once: the site tag the uploader added
        // ("[EZTVx.to]") and, in the bracket itself, the group ("[MPup]"). Strip it, ask the rest, and
        // only if the rest says nothing read the inside.
        Match wrapped = Regex.Match(name, @"[\[\{]([^\[\]\{\}]{2,40})[\]\}]$");
        if (wrapped.Success)
        {
            string rest = name[..wrapped.Index].Trim();
            return ReleaseGroup(rest, depth + 1) ?? GroupInside(wrapped.Groups[1].Value);
        }

        wrapped = Regex.Match(name, @"\(([^()]{2,40})\)$");
        if (wrapped.Success)
        {
            string rest = name[..wrapped.Index].Trim();
            return ReleaseGroup(rest, depth + 1) ?? GroupInside(wrapped.Groups[1].Value);
        }

        int dash = name.LastIndexOf('-');
        if (dash > 0 && dash < name.Length - 1)
        {
            string? tail = ShapeOfTail(name[(dash + 1)..].Trim());
            if (tail != null)
            {
                return tail;
            }
        }

        return LastTokenAfter(name, '.') ?? LastTokenAfter(name, ' ');
    }

    /// <summary>
    /// F-M430: the group inside the last <c>-</c>-segment. Two shapes beyond the plain token are read
    /// because the library writes them: <c>…x264-ZoroSenpai_EniaHD</c> (an uploader pair, the group is
    /// the first half) and <c>…x264-rothebehap.mp4</c> (a second extension left in the name).
    /// A segment that contains whitespace is a TITLE, not a group — "… - S02E12 - Meet the Evilmans"
    /// must not yield "Meet".
    /// </summary>
    private static string? ShapeOfTail(string tail)
    {
        if (IsPlainToken(tail))
        {
            return tail;
        }

        if (tail.Any(char.IsWhiteSpace))
        {
            return null;
        }

        foreach (char separator in new[] { '_', '.' })
        {
            int at = tail.IndexOf(separator, StringComparison.Ordinal);
            if (at <= 0)
            {
                continue;
            }

            string first = tail[..at].Trim();
            if (IsPlainToken(first) && !IsTechnicalToken(first))
            {
                return first;
            }
        }

        return null;
    }

    /// <summary>
    /// F-M430: the group named inside a bracket the name ends with.
    /// </summary>
    private static string? GroupInside(string inner)
    {
        // "(1080p AMZN WEB-DL x265 Ghost)" — the group sits behind a technical token.
        string? behindTech = LastTokenAfter(inner, ' ');
        if (behindTech != null)
        {
            return behindTech;
        }

        // "[MPup]" — the bracket is the group itself.
        if (IsPlainToken(inner))
        {
            return inner;
        }

        // "[YTS.AM]", "[YTS.GG - YTS.BZ]" — a brand behind a domain. Length is the divider that keeps a
        // site watermark out: brands are short ("YTS", "YIFY"), watermarks are not ("THENKIRI.COM").
        string firstWord = inner.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            ?? string.Empty;
        int dot = firstWord.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            string brand = firstWord[..dot];
            if (IsPlainToken(brand) && !IsTechnicalToken(brand) && brand.Length <= 5)
            {
                return brand;
            }
        }

        return null;
    }

    /// <summary>F-M430: last token after a separator, when the token before it is technical.</summary>
    private static string? LastTokenAfter(string name, char separator)
    {
        var parts = name.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        string last = parts[^1].Trim();
        string previous = parts[^2].Trim();
        return IsPlainToken(last) && IsTechnicalToken(previous) ? last : null;
    }

    /// <summary>F-M430: is this a token that could be a release group at all?</summary>
    private static bool IsPlainToken(string token)
    {
        if (token.Length is < 2 or > 16)
        {
            return false;
        }

        bool hasLetter = false;
        foreach (char c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }

            hasLetter |= char.IsAsciiLetter(c);
        }

        return hasLetter;
    }

    /// <summary>
    /// F-M430: the tokens that stand IN FRONT of a group in a release name. Anything else means the
    /// name ends in its own title, and no group is claimed.
    /// </summary>
    private static bool IsTechnicalToken(string token)
    {
        return token.ToLowerInvariant() switch
        {
            "x264" or "x265" or "h264" or "h265" or "h.264" or "h.265" or "hevc" or "avc" or "xvid" or "divx"
                or "av1" or "vp9" or "mpeg2" or "mpeg4" => true,
            "web" or "webrip" or "web-dl" or "webdl" or "bluray" or "blu-ray" or "brrip" or "bdrip" or "dvdrip"
                or "dvd" or "hdtv" or "hdrip" or "remux" or "hdcam" => true,
            "aac" or "aac2" or "aac5" or "ac3" or "eac3" or "dd" or "ddp" or "ddp5" or "dts" or "truehd"
                or "atmos" or "flac" or "opus" or "mp3" => true,
            "2.0" or "5.1" or "7.1" or "4k" or "8k" or "8bit" or "10bit" or "8bits" or "10bits"
                or "hdr" or "hdr10" or "dv" or "sdr" => true,
            "proper" or "repack" or "extended" or "uncut" or "remastered" => true,
            _ => false,
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
    /// <param name="hi">True for the hearing-impaired (SDH) variant; appended to the name.</param>
    /// <returns>File name.</returns>
    public static string NeutralFileName(bool isSeries, string imdbId, int season, int episode, string language, string contentHash, bool hi = false)
        => isSeries
            ? $"{imdbId}.S{season.ToString("00", CultureInfo.InvariantCulture)}E{episode.ToString("00", CultureInfo.InvariantCulture)}.{contentHash}.{language}{HiMarker(hi)}.srt"
            : $"{imdbId}.{contentHash}.{language}{HiMarker(hi)}.srt";

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
