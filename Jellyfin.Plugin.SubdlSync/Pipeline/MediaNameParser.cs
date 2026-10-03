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
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// Derives the media TYPE and the season/episode numbers from a FILE NAME.
/// </summary>
/// <remarks>
/// F-M190 (24.09.2026, user decision): the file name states what a file IS —
/// "Serie.S01E05.1080p" is season 1, episode 5 — and that is more reliable than
/// Jellyfin's folder classification. When an episode lives in a library typed
/// "movies" (a perfectly normal setup, and exactly how the nested test library
/// is configured), Jellyfin imports it as a <c>Movie</c>, so class-based type
/// detection searches TMDB for a FILM named after the episode ("Norway No How")
/// and finds nothing. The movies2 watchdog resolved this the same way: a regex
/// over the name decides series-vs-movie, independent of any library setting.
/// </remarks>
internal static class MediaNameParser
{
    private static readonly char[] Separators = { '.', '_', '-', ' ' };

    /// <summary>Quality/release tags stripped from the title head.</summary>
    private static readonly string[] QualityTags =
        { "1080p", "720p", "2160p", "4K", "WEBRip", "WEB-DL", "WEB", "BluRay", "BRRip", "HDTV", "HEVC", "x264", "x265" };

    private static readonly string[] TrailingTitleStrip =
        { "1080p", "720p", "2160p", "4K", "WEBRip", "WEB-DL", "BluRay", "HDTV" };

    /// <summary>TV-recording pattern: a date+time stamp marks a capture, not a year.</summary>
    private static readonly Regex TvStampPattern = new(
        @"^(?<title>.+?)[\.\s_](?<date>20\d{2}[0-1]\d[0-3]\d)(?:[\.\s_](?<time>\d{4,8}))?$",
        RegexOptions.Compiled);

    /// <summary>SxxExx — the primary series marker. The trailing separator is optional so a
    /// name ENDING in the marker is read too: "For All Mankind S02E01.mp4" carries no character
    /// after "E01", and the previous mandatory `[.\s_-]` made it invisible — 40 files in the
    /// measured library were typed as films and searched by their episode marker.</summary>
    private static readonly Regex SeasonEpisodePattern = new(
        @"[\.\s_-][Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,3})(?:[\.\s_-]|$)",
        RegexOptions.Compiled);

    /// <summary>F-M250: the "1x01" spelling of season/episode (also "01x01", "2x8"). Common in
    /// Italian and older scene releases; read as a series marker like SxxExx.</summary>
    private static readonly Regex SeasonXEpisodePattern = new(
        @"(?<![\d])(?:^|[\.\s_\[\(])(?<season>[1-9]\d?)[xX](?<episode>\d{1,3})(?:[\.\s_\-\)\]]|$)",
        RegexOptions.Compiled);

    /// <summary>"Season 1 Episode 5" spelling of the same information.</summary>
    private static readonly Regex LongFormPattern = new(
        @"Season[\.\s_]+(?<season>\d{1,2})[\.\s_]+Episode[\.\s_]+(?<episode>\d{1,3})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Year at the head's tail — optionally wrapped in a bracket/parenthesis
    /// ("The Pitt (2025)", "Movie [1999]"). The previous pattern ended in a literal `]`
    /// instead of a closing class, so it could never match at all; every name then went
    /// through the "year anywhere" branch, which cuts at the year and left the opening
    /// bracket in the title ("The Pitt (" → 87 measured files).</summary>
    // F-M252: accepts (YYYY) and [YYYY] at the tail; the opening bracket is not part of the title.
    private static readonly Regex TailYearPattern = new(
        @"[\.\s_\-\[\(]((?:19|20)\d{2})\)?\]?[\.\s_\-]?$",
        RegexOptions.Compiled);

    /// <summary>Year anywhere in the head (first match wins).</summary>
    private static readonly Regex AnyYearPattern = new(
        @"(?:^|[\.\s_\-\[\(])((?:19|20)\d{2})(?:[\.\s_\-\)\]]|$)",
        RegexOptions.Compiled);

    /// <summary>Validates a stamp's month/day so a quality year never triggers the TV rule.</summary>
    private static readonly Regex StampDatePattern = new(
        @"^20(\d{2})(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])$",
        RegexOptions.Compiled);

    /// <summary>F-M249: a broadcast date in the MIDDLE of the name. The programme
    /// title FOLLOWS the date ("&lt;strand&gt;.&lt;YYYY&gt;.&lt;MM&gt;.&lt;DD&gt;.&lt;title&gt;.&lt;tags&gt;"), so the
    /// prefix is the strand and not the title. Only applied when text follows the date.</summary>
    private static readonly Regex MidDateStampPattern = new(
        @"(?:^|[\.\s_\[])20\d{2}[\.\s_-](?:0[1-9]|1[0-2])[\.\s_-](?:0[1-9]|[12]\d|3[01])(?:[\.\s_\]]|$)",
        RegexOptions.Compiled);

    /// <summary>F-M249: a bare episode marker ("E05") with no season. Only consulted when
    /// no SxxExx and no long form is present.</summary>
    private static readonly Regex BareEpisodePattern = new(
        @"(?<sep>^|[\.\s_\[-])[Ee](?<episode>\d{1,3})(?:[\.\s_\]]|$)",
        RegexOptions.Compiled);

    /// <summary>F-M249: release-codec tokens that may sit before a hyphen and a number —
    /// "x265-E5" is a release group's suffix, never an episode marker.</summary>
    private static readonly string[] CodecTokens =
        { "x264", "x265", "h264", "h265", "hevc", "av1", "aac", "ac3", "eac3", "ddp", "dts", "10bit", "8bit" };

    /// <summary>F-M249: tokens that end the title after a broadcast date — language
    /// markers and release tags. "Cunk.on.Shakespeare.EN.SUB.MPEG4.x264" → "Cunk on Shakespeare".</summary>
    private static readonly string[] TitleEndTokens =
        { "en", "de", "eng", "ger", "sub", "subs", "subbed", "multi", "mpeg4", "web", "webrip", "web-dl", "webdl",
          "hdtv", "x264", "x265", "h264", "h265", "hevc", "aac", "ac3", "ddp", "10bit", "8bit",
          "1080p", "720p", "2160p", "4k", "bluray", "brrip", "mpup" };

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Parses a file name (with or without directory/extension).
    /// </summary>
    /// <param name="fileName">The media file name.</param>
    /// <returns>Title/year/type plus season+episode when the name states them.</returns>
    internal static ParsedMediaName Parse(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new ParsedMediaName(null, null, false, null, null);
        }

        string name = Path.GetFileNameWithoutExtension(fileName);

        // TV-recording pattern first: its stamp is NOT a production year.
        var tvStamp = TvStampPattern.Match(name);
        if (tvStamp.Success)
        {
            var t = NormalizeTitle(tvStamp.Groups["title"].Value);
            if (t is { Length: >= 2 } && StampDatePattern.IsMatch(tvStamp.Groups["date"].Value))
            {
                return new ParsedMediaName(t, null, true, null, null);
            }
        }

        bool isSeries = false;
        int? season = null;
        int? episode = null;

        // SxxExx is authoritative for BOTH the type and the numbers.
        var se = SeasonEpisodePattern.Match(name);
        int cutIndex = -1;
        if (se.Success)
        {
            isSeries = true;
            cutIndex = se.Index;
            season = ParseInt(se.Groups["season"].Value);
            episode = ParseInt(se.Groups["episode"].Value);
        }
        else
        {
            var lf = LongFormPattern.Match(name);
            if (lf.Success)
            {
                isSeries = true;
                cutIndex = lf.Index;
                season = ParseInt(lf.Groups["season"].Value);
                episode = ParseInt(lf.Groups["episode"].Value);
            }
            else
            {
                // F-M250 (29.09.2026): "Reacher.2x01.Sportello.…" — the NxNN spelling. Without it
                // the item was typed as a FILM and searched as "Reacher 2x01 Sportello Automatico
                // ITA ENG" (0 hits); TMDb knows the series. Measured: 43 files.
                // Guarded: a four-digit leading number would make the year a season, so the
                // season is capped at two digits and must not be preceded by a digit.
                var nx = SeasonXEpisodePattern.Match(name);
                if (nx.Success)
                {
                    isSeries = true;
                    cutIndex = nx.Index;
                    season = ParseInt(nx.Groups["season"].Value);
                    episode = ParseInt(nx.Groups["episode"].Value);
                }
            }
        }

        // ---------- F-M249 (29.09.2026): two name shapes the parser read wrongly ----------
        // (1) BARE EPISODE MARKER, no season: "The Revolutionaries  E05.mkv". The series
        //     pattern needs "S<d>E<d>", so a bare "E05" was not seen at all: the item was
        //     treated as a MOVIE and searched as "The Revolutionaries E05" (0 hits), while
        //     TMDb knows the series. Measured 29.09.2026: 8 files in one season, all
        //     unresolvable for 3/3 ladder attempts.
        // (2) BROADCAST DATE IN THE MIDDLE, title AFTER it: the whole name is
        //     "<strand>.<YYYY>.<MM>.<DD>.<title>.<tags>", so the prefix is the strand
        //     ("BBC Documentaries") and the title follows the date. The old code took the
        //     FIRST year anywhere and cut there, which produced the strand as the title and
        //     searched it — TMDb answered with an unrelated film. Measured: 1 file.
        // Both branches are deliberately narrow: the bare-E rule requires an uppercase E
        // AND that no SxxExx/long form is present, and the date rule requires a separated
        // YYYY-MM-DD (TvStampPattern's compact YYYYMMDD is handled above) with text after it.
        if (cutIndex < 0)
        {
            var midDate = MidDateStampPattern.Match(name);
            if (midDate.Success && name[(midDate.Index + midDate.Length)..].Trim(' ', '.', '_', '-').Length > 0)
            {
                string after = name[(midDate.Index + midDate.Length)..].TrimStart(' ', '.', '_', '-');
                string dateless = CutAtReleaseTokens(NormalizeTitle(after));
                if (dateless.Length >= 2)
                {
                    var bareInTail = BareEpisodePattern.Match(after);
                    return new ParsedMediaName(
                        dateless,
                        null,
                        false,
                        null,
                        bareInTail.Success ? ParseInt(bareInTail.Groups["episode"].Value) : null);
                }
            }

            var bare = BareEpisodePattern.Match(name);
            int bareEPos = bare.Success ? bare.Index + bare.Groups["sep"].Length : -1;
            if (bare.Success && !LooksLikeCodecSuffix(name, bareEPos))
            {
                // Fall THROUGH to the shared head/year/quality cleanup instead of returning
                // here: that keeps this shape subject to the same title normalisation as
                // every other name (a year before the marker is still a year, and trailing
                // quality tags are still stripped). A separate early return would have made
                // the two paths drift apart.
                isSeries = true;
                cutIndex = bare.Index;
                episode = ParseInt(bare.Groups["episode"].Value);
            }
        }

        string head = cutIndex >= 0 ? name[..cutIndex] : name;

        int? year = null;
        var tailYear = TailYearPattern.Match(head);
        if (tailYear.Success)
        {
            year = ParseInt(tailYear.Groups[1].Value);
            head = head[..tailYear.Index];
        }
        else
        {
            var anyYear = AnyYearPattern.Match(head);
            if (anyYear.Success)
            {
                year = ParseInt(anyYear.Groups[1].Value);
                // Cut at the START of the match (the separator before the year), not at the
                // year itself: cutting at the year left the separator behind, and when that
                // separator was an opening bracket the title kept it — "The Pitt (2026)" →
                // "The Pitt (". The separator is never part of the title.
                head = head[..anyYear.Index];
            }
        }

        string title = NormalizeTitle(head);

        // Drop trailing quality tags that survived parsing.
        foreach (var tag in TrailingTitleStrip)
        {
            int idx = title.LastIndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx > 3)
            {
                title = title[..idx].TrimEnd(" ._-".ToCharArray());
            }
        }

        if (string.IsNullOrWhiteSpace(title) || title.Length < 2)
        {
            return new ParsedMediaName(null, year, isSeries, season, episode);
        }

        return new ParsedMediaName(title, year, isSeries, season, episode);
    }

    /// <summary>F-M249: a marker written directly after a release-codec token and a hyphen
    /// ("x265-E5") is a release group's suffix, never an episode. Guards the bare-E rule
    /// against exactly the shape that made it dangerous to add.</summary>
    /// <param name="name">The full file name stem.</param>
    /// <param name="ePos">Index of the marker letter itself (not of its separator).</param>
    private static bool LooksLikeCodecSuffix(string name, int ePos)
    {
        // Only "<codec>-<E>" is rejected: the hyphen is what makes it a group suffix. Other
        // separators (dot, space, underscore, bracket) keep the marker as an episode.
        if (ePos < 2 || name[ePos - 1] != '-')
        {
            return false;
        }

        int start = ePos - 1;
        while (start > 0 && name[start - 1] != '.' && name[start - 1] != ' ' && name[start - 1] != '_' && name[start - 1] != '-')
        {
            start--;
        }

        string token = name[start..(ePos - 1)];
        foreach (var codec in CodecTokens)
        {
            if (token.Equals(codec, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>F-M249: cuts a normalised title at the first release/language token, so a
    /// title taken from AFTER a broadcast date ends where the tags begin.</summary>
    private static string CutAtReleaseTokens(string normalized)
    {
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var word in words)
        {
            bool isTag = false;
            foreach (var tag in TitleEndTokens)
            {
                if (word.Equals(tag, StringComparison.OrdinalIgnoreCase))
                {
                    isTag = true;
                    break;
                }
            }

            if (isTag)
            {
                break;
            }

            kept.Add(word);
        }

        // A trailing number that is not part of the title (e.g. the "2016 05 11" leftovers).
        while (kept.Count > 1 && int.TryParse(kept[^1], out _))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return string.Join(' ', kept).Trim(Separators);
    }

    /// <summary>Separators to spaces, collapse blanks, trim dashes.</summary>
    internal static string NormalizeTitle(string raw)
    {
        string t = raw.Replace('.', ' ').Replace('_', ' ').Trim();
        t = WhitespacePattern.Replace(t, " ");
        t = t.Trim(Separators);
        // A trailing bracket that was never closed is leftover punctuation, not a title
        // ("The Pitt (") — a title with a real bracket always carries both halves.
        while (t.Length > 0 && (t[^1] == '(' || t[^1] == '[' || t[^1] == '{'))
        {
            t = t[..^1].Trim(Separators);
        }

        return t;
    }

    private static int? ParseInt(string raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>Result of <see cref="MediaNameParser.Parse"/>.</summary>
/// <param name="Title">Title/series name, or null when the name yields none.</param>
/// <param name="Year">Production year, or null (never a recording stamp).</param>
/// <param name="IsSeries">True when the name carries a series marker.</param>
/// <param name="Season">Season number from the name, or null.</param>
/// <param name="Episode">Episode number from the name, or null.</param>
internal sealed record ParsedMediaName(string? Title, int? Year, bool IsSeries, int? Season, int? Episode);
