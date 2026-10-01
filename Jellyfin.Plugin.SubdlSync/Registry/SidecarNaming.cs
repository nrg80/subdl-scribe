// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubdlScribe.Language;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// F-M239: the ONE reader of a sidecar file name.
/// <para>
/// Three places used to parse the same names with their own copy of the rules — the uploader
/// (which knows the <c>sdh</c>/<c>hi</c> marker), the download pipeline's missing-language check,
/// and the database refresh's on-disk check (neither of which knew it). The copies disagreed, and
/// the disagreement was not cosmetic: <c>MapToSubdl("sdh")</c> falls back to the first two letters
/// of an unknown tag, so the refresh read <c>Movie.de.sdh.srt</c> as the language "SD" — a language
/// that does not exist — while <c>DE</c> looked absent. It then dropped the download mark as stale
/// and the file was refetched on the next run, every run.
/// </para>
/// <para>
/// The marker is the whole point of the disagreement: <c>sdh</c>/<c>hi</c> name a VARIANT of a
/// language, never a language. They are consumed as a marker and the real language token is read
/// from the position before them.
/// </para>
/// </summary>
public static class SidecarNaming
{
    /// <summary>
    /// Builds the file name a sidecar gets next to the media file.
    /// <para>
    /// F-M260: the writer's counterpart to <see cref="Parse"/>, in the SAME class on purpose. The
    /// downloader used to compose these names inline (<c>ExternalName</c>, plus two hand-written
    /// <c>.sdh.srt</c> concatenations), while <see cref="Parse"/> and <see cref="List"/> read them
    /// back — four places with the same idea of a name and no shared definition. That is how the
    /// earlier disagreement arose: <c>MapToSubdl("sdh")</c> read a hearing-impaired sidecar as the
    /// language "SD". Composing here, where parsing lives, means a name the writer produces is one
    /// the reader recognizes by construction.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file path (supplies directory and base name).</param>
    /// <param name="lang">Language code.</param>
    /// <param name="hearingImpaired">True for the <c>.sdh</c> variant (F-M260).</param>
    /// <param name="slot">1-based slot; 1 is the plain name, higher numbers get a suffix.</param>
    /// <returns>Full path of the sidecar.</returns>
    public static string Build(string mediaPath, string lang, bool hearingImpaired = false, int slot = 1)
    {
        string dir = Path.GetDirectoryName(mediaPath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(mediaPath);
        string suffix = hearingImpaired ? ".sdh" : string.Empty;
        string slotSuffix = slot > 1
            ? "." + slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        return Path.Combine(dir, $"{baseName}.{lang.ToLowerInvariant()}{suffix}{slotSuffix}.srt");
    }

    /// <summary>
    /// True when a name token marks a hearing-impaired variant rather than a language.
    /// <para>
    /// Only <c>sdh</c> qualifies. The earlier rule also accepted <c>hi</c>, which is wrong on two
    /// counts: <c>hi</c> is the ISO 639-1 code for <b>Hindi</b> — the mapper maps <c>hin</c> to
    /// <c>HI</c>, so a real Hindi subtitle named <c>Movie.hi.srt</c> was read as a hearing-impaired
    /// marker and, because no language token preceded it, dropped entirely. And nothing in this
    /// plugin ever writes <c>.hi.srt</c>: the downloader writes <c>.sdh.srt</c>, and Hindi sidecars
    /// are written <c>.hi.srt</c> by every other tool. The marker is <c>sdh</c> only.
    /// </para>
    /// </summary>
    /// <param name="token">Name token to test.</param>
    /// <returns>True when the token is a hearing-impaired marker.</returns>
    public static bool IsHearingImpairedToken(string token)
        => token.Equals("sdh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the language and hearing-impaired flag out of a sidecar file name.
    /// <para>
    /// Recognized shapes, all relative to the media base name:
    /// <c>&lt;base&gt;.srt</c> (unlabeled, English by Jellyfin convention),
    /// <c>&lt;base&gt;.&lt;lang&gt;.srt</c>,
    /// <c>&lt;base&gt;.&lt;lang&gt;.sdh.srt</c> / <c>&lt;base&gt;.&lt;lang&gt;.hi.srt</c>,
    /// and the numbered extra slots the downloader writes
    /// (<c>&lt;base&gt;.&lt;lang&gt;.2.srt</c>, <c>.3</c>, …).
    /// </para>
    /// <para>
    /// The language token itself keeps the permissive resolution it always had (two letters direct,
    /// three letters through the ISO 639-2 map, unknown three-letter tags falling back to their
    /// first two letters). Only the marker case is strict — that fallback is precisely what must
    /// not see a marker.
    /// </para>
    /// </summary>
    /// <param name="fileNameWithoutExtension">Sidecar name without the .srt extension.</param>
    /// <param name="baseName">Media file name without extension.</param>
    /// <returns>Language (null when the name carries none) and HI flag; null when the name is not a sidecar at all.</returns>
    public static (string? Lang, bool HearingImpaired)? Parse(string fileNameWithoutExtension, string baseName)
    {
        if (string.IsNullOrEmpty(fileNameWithoutExtension))
        {
            return null;
        }

        // Unlabeled "<base>.srt" — the name carries NO language, and saying nothing
        // is not the same as saying English. Until 01.10.2026 this returned "EN" on
        // Jellyfin's convention, which made an unlabelled file upload as English even
        // when its text was German (user decision 30.09.2026: an unlabelled loose SRT
        // goes through language detection, and a file whose language cannot be
        // established is skipped — never guessed at). Reported as (null, false) so the
        // caller can tell "no language in the name" from "the name says EN".
        if (fileNameWithoutExtension.Equals(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return (null, false);
        }

        string[] parts = fileNameWithoutExtension.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        string last = parts[^1];

        // Hearing-impaired marker: the language token sits BEFORE it
        // (Movie.de.sdh.srt → DE, hi=true). If it does not resolve, the name says nothing.
        if (IsHearingImpairedToken(last))
        {
            string? markerLang = parts.Length >= 3 ? ResolveToken(parts[^2]) : null;
            return markerLang == null ? null : (markerLang, true);
        }

        // Numbered extra slot the downloader writes for a second/third candidate of one
        // language: "<base>.<lang>.2.srt". Without this the slot files were invisible.
        // F-M260: the slot and the marker COMBINE — "<base>.<lang>.sdh.2.srt" is slot 2 of the
        // hearing-impaired variant. The writer (Build) produces that shape, so the reader must
        // accept it; otherwise the plugin writes a file it cannot recognize one line later.
        if (parts.Length >= 3 && last.Length > 0 && last.Length <= 2 && AllDigits(last))
        {
            if (parts.Length >= 4 && IsHearingImpairedToken(parts[^2]))
            {
                string? slotHiLang = ResolveToken(parts[^3]);
                return slotHiLang == null ? null : (slotHiLang, true);
            }

            string? slotLang = ResolveToken(parts[^2]);
            return slotLang == null ? null : (slotLang, false);
        }

        string? lang = ResolveToken(last);
        return lang == null ? null : (lang, false);
    }

    /// <summary>
    /// The sidecar files next to a media file, in one directory listing.
    /// <para>
    /// Callers need different projections of the same fact — the uploader wants paths, the
    /// existence checks want just the languages, the HI check wants the flag — so the listing is
    /// done once here and projected by the callers, rather than each caller walking the directory
    /// with its own idea of what a file name means.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <returns>One entry per recognized sidecar.</returns>
    public static List<(string Path, string? Lang, bool HearingImpaired)> List(string mediaPath)
    {
        var result = new List<(string, string?, bool)>();
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return result;
        }

        string dir = Path.GetDirectoryName(mediaPath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(mediaPath);

        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, baseName + "*.srt"))
            {
                if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // crashed atomic write — leftover temp, not a subtitle
                }

                var parsed = Parse(Path.GetFileNameWithoutExtension(f), baseName);
                if (parsed != null)
                {
                    result.Add((f, parsed.Value.Lang, parsed.Value.HearingImpaired));
                }
            }
        }
        catch
        {
            // Directory listing failed. An empty result means "cannot prove it is there",
            // which every caller treats as a reason to look again — the safe direction.
        }

        return result;
    }

    /// <summary>
    /// Which languages have a sidecar, and whether the file carrying it is the HI variant.
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <returns>Language → (any file present, hearing-impaired file present).</returns>
    public static Dictionary<string, (bool Any, bool Hi)> Present(string mediaPath)
    {
        var found = new Dictionary<string, (bool Any, bool Hi)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, lang, hi) in List(mediaPath))
        {
            if (lang == null)
            {
                // The name carries no language and no detection has run for it yet, so it
                // proves nothing about which language is covered. Skipped rather than
                // guessed: claiming "present" would suppress the fetch for a language that
                // is in fact missing.
                continue;
            }

            found.TryGetValue(lang, out var current);
            found[lang] = (true, current.Hi || hi);
        }

        return found;
    }

    /// <summary>
    /// Which of the wanted languages have no sidecar on disk.
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <param name="languages">Languages to check.</param>
    /// <returns>Languages without file evidence.</returns>
    public static List<string> MissingOnDisk(string mediaPath, IEnumerable<string> languages)
    {
        var present = Present(mediaPath);
        var missing = new List<string>();
        foreach (var lang in languages)
        {
            if (!present.ContainsKey(lang))
            {
                missing.Add(lang);
            }
        }

        return missing;
    }

    /// <summary>
    /// F-M240: languages whose hearing-impaired variant is absent from disk.
    /// <para>
    /// The download mark describes the language dimension, so a file that already carries its
    /// subtitles looks complete the moment the HI switch is turned on — and the HI variant is never
    /// fetched. Asking this question separates "the language is covered" from "the variant the
    /// switch asks for is covered". Disk evidence rather than a stored flag on purpose: it
    /// self-corrects when the HI file is deleted, and turning the switch off needs no bookkeeping
    /// at all.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <param name="languages">Languages to check.</param>
    /// <returns>Languages with no hearing-impaired sidecar.</returns>
    /// <summary>
    /// F-M243: the HI question, answered from the registry.
    /// <para>
    /// A check that looks only at files next to the media read a file whose
    /// English is EMBEDDED as "HI absent", so the language was queued and downloaded although the
    /// variant was already in the container. Measured on prod: 33 of 35 sampled files embed
    /// <c>eng</c> with no EN sidecar, so nearly every item was re-fetched for a variant it had.
    /// </para>
    /// <para>
    /// F-M254: the registry is the source, not the stream list. The embedded verdict is stored (the
    /// uploader writes it through <c>MarkEmbed</c>, the downloader through <c>MarkDownloaded</c>), so
    /// answering the question live meant a stored verdict could be ignored — a captioned track whose
    /// title Jellyfin does not surface read as "HI absent" while its own row said otherwise. The
    /// caller passes what the registry holds.
    /// </para>
    /// </summary>
    /// <param name="languages">Languages to check.</param>
    /// <param name="storedHi">Languages the registry records as hearing-impaired.</param>
    /// <returns>Languages with no recorded hearing-impaired variant.</returns>
    public static List<string> MissingHearingImpaired(
        IEnumerable<string> languages, IEnumerable<string>? storedHi)
    {
        var known = new HashSet<string>(storedHi ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return languages.Where(lang => !known.Contains(lang)).ToList();
    }

    /// <summary>
    /// F-M71's HI detection on an embedded stream, in ONE place. Jellyfin's own flag first, then the
    /// title. The uploader and the downloader each carried a copy of this; the uploader's copy never
    /// asked the flag, so a file whose HI stream was flagged but not titled read as two different
    /// things depending on which side asked.
    /// <para>
    /// F-M254: <c>cc</c> joins the markers. Closed captions and SDH describe the same audience — the
    /// captions carry the non-dialogue cues (<c>[ BEEPING ]</c>, <c>[ GRUNTING ]</c>, musical notes)
    /// that separate them from a plain subtitle — but only "SDH" was recognized. Files titled
    /// <c>CC</c> therefore wrote <c>false</c> into the registry, so the HI variant read as absent and
    /// the language was fetched again although the variant was already in the container. The marker
    /// is matched on a word boundary and not as a substring on purpose: <c>cc</c> sits inside real
    /// language names (<c>Occitan</c>), and reading that as hearing-impaired would repeat the
    /// <c>hi</c>/Hindi mistake this method's sibling <see cref="IsHearingImpairedToken"/> documents.
    /// </para>
    /// </summary>
    /// <param name="stream">Media stream.</param>
    /// <returns>True when the stream is a hearing-impaired subtitle track.</returns>
    public static bool IsHearingImpairedStream(MediaStream stream)
    {
        if (stream.IsHearingImpaired == true)
        {
            return true;
        }

        string title = stream.Title ?? string.Empty;
        return title.Contains("sdh", StringComparison.OrdinalIgnoreCase)
               || title.Contains("hearing impaired", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(title, @"(?<![a-z])cc(?![a-z])", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// F-M246: True when the stream is a FORCED subtitle track.
    /// <para>
    /// A forced track carries only the lines of foreign-language scenes, not the film's dialogue.
    /// It therefore never counts as a subtitle of the film — neither as coverage on the download
    /// side (where it must not mark a language present) nor as upload work (where there is nothing
    /// worth publishing: the same few lines, usually one cue).
    /// </para>
    /// <para>
    /// One predicate for both directions, so neither can drift. A hearing-impaired track is NOT
    /// affected: SDH is the same dialogue with annotations.
    /// </para>
    /// </summary>
    /// <param name="stream">Media stream.</param>
    /// <returns>True when the stream is a forced subtitle track.</returns>
    public static bool IsForcedStream(MediaStream stream)
        => stream != null && stream.IsForced == true;

    /// <summary>
    /// Language token → SubDL code: two letters direct, three through the mapper.
    /// </summary>
    /// <param name="token">Candidate token.</param>
    /// <returns>SubDL language code, or null.</returns>
    private static string? ResolveToken(string token)
    {
        if (IsHearingImpairedToken(token))
        {
            // A marker is never a language, even when a caller hands one in directly.
            return null;
        }

        if (token.Length == 2)
        {
            return token.ToUpperInvariant();
        }

        if (token.Length == 3)
        {
            return LanguageMapper.MapToSubdl(token);
        }

        return null;
    }

    /// <summary>
    /// F-M246: the languages an embedded subtitle track makes "present", in ONE place.
    /// <para>
    /// The download pipeline's missing-language check and the seeder's queueing gate each walked
    /// the subtitle streams with their own copy of this loop, and neither ever asked
    /// <c>MediaStream.IsForced</c>. A forced track carries only the lines of foreign-language
    /// scenes — it is not a subtitle of the film — yet it counted as full coverage for its
    /// language, so a file whose English existed only as a forced track was reported as settled
    /// and never received a real English subtitle. Measured on the 28.09.2026 library:
    /// 4 items whose English is forced-only (Taylor Swift — The Eras Tour, no sidecar), plus
    /// 8 whose Hindi is, all already marked "all target languages settled".
    /// </para>
    /// <para>
    /// Only TEXT subtitle streams count. A bitmap track (dvdsub, pgssub) carries painted pixels —
    /// there is nothing to read, nothing to extract and nothing to publish, so it is not coverage
    /// for its language (user decision 30.09.2026: "Bild sub ist nicht da"). Measured on the
    /// 30.09.2026 library: 86 items carry a target language ONLY as a bitmap track (`dvdsub`/`pgssub`,
    /// e.g. the "480p DVD x265 Panda" films and `Person Of Interest S02`), and every one of them was
    /// counted as covered, so no subtitle was ever fetched for them.
    /// </para>
    /// <para>
    /// A hearing-impaired track DOES count: it is the same film's dialogue, just annotated.
    /// So the rule is "any non-forced TEXT subtitle stream", and the HI switch is answered separately
    /// from the registry (F-M254) rather than from the stream list.
    /// </para>
    /// <para>
    /// One copy only, for the same reason the name parse has one: the two callers must give the
    /// same answer, and the seeder's answer decides whether the pipeline ever sees the item.
    /// </para>
    /// </summary>
    /// <param name="streams">Media streams of the item (may be null when unavailable).</param>
    /// <returns>Language codes carried by a non-forced embedded subtitle track.</returns>
    public static HashSet<string> EmbeddedPresentLanguages(IEnumerable<MediaStream>? streams)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (streams == null)
        {
            return found;
        }

        foreach (var s in streams)
        {
            // F-M246: forced tracks are not the film's subtitle.
            // F-M257: neither is a bitmap track — dvdsub/pgssub carry pixels, so they cannot be
            // read, extracted or published. Counting one as coverage left 86 items in the library
            // marked "settled" for a language whose only subtitle was unreadable.
            if (s.Type != MediaStreamType.Subtitle || IsForcedStream(s) || !s.IsTextSubtitleStream)
            {
                continue;
            }

            string? subdl = LanguageMapper.MapToSubdl(s.Language);
            if (subdl != null)
            {
                found.Add(subdl);
            }
        }

        return found;
    }

    /// <summary>
    /// F-M257: the embedded tracks of a file as (position, language, HI) triples — the ONE reader
    /// for callers that record or compare individual tracks rather than just their languages.
    /// <para>
    /// The position is the index into the UNFILTERED subtitle list, because ffmpeg's <c>0:s:N</c>
    /// counts bitmap and forced tracks as well; a position taken from a filtered list names a
    /// different stream. Forced tracks are left out for the same reason the presence check leaves
    /// them out (F-M246) — a forced track carries the lines of foreign-language scenes, not the
    /// film's dialogue.
    /// </para>
    /// <para>
    /// Only TEXT subtitle streams are returned, exactly as the uploader selects them. A bitmap track
    /// (dvdsub, pgssub) carries painted pixels: it cannot be extracted, so it can never be uploaded,
    /// and it is not a subtitle the reader can use either (user decision 30.09.2026: "Bild sub ist
    /// nicht da"). Recording it would claim a coverage that does not exist for either direction.
    /// </para>
    /// <para>
    /// Untagged streams are left out too: a language that cannot be mapped has no side in the
    /// registry, and inventing one was the phantom-language defect F-M239 removed.
    /// </para>
    /// <para>
    /// One copy only, shared by the seeder's observation write and any pipeline-side comparison, so
    /// the two cannot drift the way the four name parsers and the three HI readers did.
    /// </para>
    /// </summary>
    /// <param name="streams">All subtitle streams of the item, UNFILTERED (as Jellyfin returns them).</param>
    /// <returns>One triple per mappable, non-forced, TEXT embedded subtitle track.</returns>
    public static List<(int SubPos, string Lang, bool HearingImpaired)> EmbeddedTracks(IEnumerable<MediaStream>? streams)
    {
        var result = new List<(int, string, bool)>();
        var all = streams?
            .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal)
            .ToList();
        if (all == null || all.Count == 0)
        {
            return result;
        }

        for (int pos = 0; pos < all.Count; pos++)
        {
            var s = all[pos];
            if (IsForcedStream(s) || !s.IsTextSubtitleStream)
            {
                continue;
            }

            string? lang = LanguageMapper.MapToSubdl(s.Language);
            if (lang != null)
            {
                result.Add((pos, lang, IsHearingImpairedStream(s)));
            }
        }

        return result;
    }

    private static bool AllDigits(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
