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
    public static string Build(string mediaPath, string lang, bool hearingImpaired = false, int slot = 1,
                               bool forced = false)
    {
        string dir = Path.GetDirectoryName(mediaPath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(mediaPath);
        string suffix = hearingImpaired ? ".sdh" : string.Empty;
        if (forced)
        {
            // F-M284: the marker is part of the datum, so it belongs in the name this class writes.
            // A rename that dropped it would leave a forced file looking like the film's dialogue.
            suffix += ".forced";
        }

        string slotSuffix = slot > 1
            ? "." + slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        return Path.Combine(dir, $"{baseName}.{lang.ToLowerInvariant()}{suffix}{slotSuffix}.srt");
    }

    /// <summary>
    /// The name a sidecar should carry once its language is known, lowest free slot first.
    /// <para>
    /// F-M278 (user decision 01.10.2026): a subtitle file whose NAME says nothing is renamed to the
    /// shape this plugin itself writes, because the name is the only thing Jellyfin, MediaElch and
    /// every reader in this codebase can see. An unlabelled <c>&lt;container&gt;.srt</c> is
    /// recognized by <see cref="Parse"/> as "no language", so it is skipped by
    /// <see cref="PresentTokens"/>, never recorded as a fact, and the item is searched for a
    /// language its own disk already holds.
    /// </para>
    /// <para>
    /// Slot 1 is the plain name. A higher slot is chosen when that combination is already on disk —
    /// two unlabelled files detected as the same language are two entries, and a rename that
    /// overwrote one with the other would destroy a subtitle to tidy a file name. The rule is pure:
    /// the caller hands in the names that exist, so it can be asserted with plain strings.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <param name="lang">The language, already resolved (by name token or by detection).</param>
    /// <param name="hearingImpaired">True when the file is the variant.</param>
    /// <param name="existingNames">File names already present in the directory, as <see cref="Parse"/>'s caller sees them.</param>
    /// <returns>Full path of the name to move the file to. May already exist when every slot is taken.</returns>
    public static string PlanTarget(string mediaPath, string lang, bool hearingImpaired,
                                    System.Collections.Generic.ISet<string> existingNames,
                                    bool forced = false)
    {
        for (int slot = 1; slot <= 999; slot++)
        {
            string candidate = Build(mediaPath, lang, hearingImpaired, slot, forced);
            if (existingNames == null || !existingNames.Contains(Path.GetFileName(candidate)))
            {
                return candidate;
            }
        }

        // Every slot taken (999 files of one language beside one media file). Return the plain
        // name so the caller's own existence check refuses the move — never a name that would
        // overwrite one of them.
        return Build(mediaPath, lang, hearingImpaired, 1);
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
    /// True when a name token marks a FORCED track rather than a language.
    /// <para>
    /// F-M284 (user decision 02.10.2026): <c>forced</c> is the marker every other tool writes
    /// (<c>Movie.de.forced.srt</c>), so it is read like <c>sdh</c> is. It is not a marker the plugin
    /// writes itself: SubDL has no forced counterpart (measured live 02.10.2026 — no filter, no
    /// field, 0 of 22 candidates named forced), so nothing is ever downloaded as a forced sidecar.
    /// Recognizing it is what lets such a file be recorded as what it IS instead of being counted as
    /// the film's dialogue.
    /// </para>
    /// <para>
    /// Strictly the whole token, for the same reason the HI marker is: a substring rule would read
    /// real names as markers and lose their language.
    /// </para>
    /// </summary>
    /// <param name="token">Name token to test.</param>
    /// <returns>True when the token is a forced marker.</returns>
    public static bool IsForcedToken(string token)
        => token.Equals("forced", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the MARKERS (hearing-impaired, forced) out of a sidecar name, without resolving a
    /// language and without needing the media base name.
    /// <para>
    /// F-M284: extracted so the two callers that need only the flags — the sidecar backfill, which
    /// holds a stored file name rather than a directory to walk, and <see cref="Parse"/> — cannot
    /// disagree about what a marker is. The scan starts at the END and skips the numbered slot
    /// first, because that is where the writer puts it (<c>&lt;base&gt;.&lt;lang&gt;.sdh.2.srt</c>).
    /// </para>
    /// </summary>
    /// <param name="fileNameWithoutExtension">Sidecar name without the .srt extension.</param>
    /// <returns>The two flags the name states.</returns>
    public static (bool HearingImpaired, bool Forced) ReadFlags(string? fileNameWithoutExtension)
    {
        if (string.IsNullOrEmpty(fileNameWithoutExtension))
        {
            return (false, false);
        }

        string[] parts = fileNameWithoutExtension.Split('.');
        int end = parts.Length - 1;

        // The numbered slot sits LAST (F-M260), so it is stepped over before any marker is read.
        if (end > 0 && parts[end].Length > 0 && parts[end].Length <= 2 && AllDigits(parts[end]))
        {
            end--;
        }

        bool hi = false;
        bool forced = false;
        while (end > 0 && (IsHearingImpairedToken(parts[end]) || IsForcedToken(parts[end])))
        {
            if (IsHearingImpairedToken(parts[end]))
            {
                hi = true;
            }
            else
            {
                forced = true;
            }

            end--;
        }

        return (hi, forced);
    }

    /// <summary>
    /// Reads the language and BOTH properties (hearing-impaired, forced) out of a sidecar file name.
    /// <para>
    /// Recognized shapes, all relative to the media base name:
    /// <c>&lt;base&gt;.srt</c> (no language in the name),
    /// <c>&lt;base&gt;.&lt;lang&gt;.srt</c>,
    /// <c>&lt;base&gt;.&lt;lang&gt;.sdh.srt</c> / <c>&lt;base&gt;.&lt;lang&gt;.hi.srt</c>,
    /// <c>&lt;base&gt;.&lt;lang&gt;.forced.srt</c>,
    /// the two markers combined (<c>&lt;base&gt;.&lt;lang&gt;.sdh.forced.srt</c>, either order),
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
    /// <returns>Language (null when the name carries none) with both flags; null when the name is not a sidecar at all.</returns>
    public static (string? Lang, bool HearingImpaired, bool Forced)? Parse(string fileNameWithoutExtension, string baseName)
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
        // established is skipped — never guessed at). Reported as (null, false, false) so the
        // caller can tell "no language in the name" from "the name says EN".
        if (fileNameWithoutExtension.Equals(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return (null, false, false);
        }

        string[] parts = fileNameWithoutExtension.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        int end = parts.Length - 1;

        // F-M260: the numbered slot sits LAST and the markers come BEFORE the language token. A
        // slot is 1-2 digits ("<base>.<lang>.2.srt"), which is also how it is told apart from a
        // 2-letter language or from a marker token.
        if (parts.Length >= 3 && end > 0 && parts[end].Length > 0 && parts[end].Length <= 2 && AllDigits(parts[end]))
        {
            end--;
        }

        // F-M284: the markers are read as a SET, in either order, so "de.sdh.forced" and
        // "de.forced.sdh" both resolve to DE with both properties. Reading them one-by-one was the
        // shape that could not express a track that is both.
        bool hi = false;
        bool forced = false;
        while (end > 0 && (IsHearingImpairedToken(parts[end]) || IsForcedToken(parts[end])))
        {
            if (IsHearingImpairedToken(parts[end]))
            {
                hi = true;
            }
            else
            {
                forced = true;
            }

            end--;
        }

        string? lang = ResolveToken(parts[end]);
        return lang == null ? null : (lang, hi, forced);
    }

    /// <summary>
    /// The sidecar files next to a media file, in one directory listing.
    /// <para>
    /// Callers need different projections of the same fact — the uploader wants paths, the
    /// existence checks want just the languages, the coverage reader wants the DATA (language plus
    /// both flags) — so the listing is done once here and projected by the callers, rather than each
    /// caller walking the directory with its own idea of what a file name means. F-M284: the tuple
    /// carries forced as well, because a `.forced.srt` is not the film's dialogue and must not be
    /// counted as coverage.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <returns>One entry per recognized sidecar.</returns>
    public static List<(string Path, string? Lang, bool HearingImpaired, bool Forced)> List(string mediaPath)
    {
        var result = new List<(string, string?, bool, bool)>();
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
                    result.Add((f, parsed.Value.Lang, parsed.Value.HearingImpaired, parsed.Value.Forced));
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
        foreach (var (_, lang, hi, _) in List(mediaPath))
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
    /// F-M282 (user decision 02.10.2026): the pairs that have file evidence next to the media file.
    /// <para>
    /// This replaces <c>PresentTokens</c> and with it the whole string-token vocabulary
    /// (<c>HiTokenSuffix</c>, <c>IsHiToken</c>, <c>TokenLanguage</c>). Those existed to carry a
    /// hearing-impaired property through a comma-separated language list — and every helper that
    /// mapped a token back to its plain language was a place the conflation re-entered, because a
    /// language-keyed reader then re-requested the LANGUAGE for a missing variant and fetched the
    /// regular subtitle over the file already on disk.
    /// </para>
    /// <para>
    /// A pair cannot be projected back by accident: there is no spelling of <c>DE</c> that carries
    /// the variant and no parse that can lose it. The rule of the old reader is kept: a subtitle of
    /// language L proves L whether or not it is the variant, and proves L (HI) only when it IS.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Media file path.</param>
    /// <returns>Pairs with file evidence.</returns>
    public static HashSet<SubtitleRef> PresentPairs(string mediaPath)
    {
        var found = new HashSet<SubtitleRef>();
        foreach (var (_, lang, hi, _) in List(mediaPath))
        {
            if (lang == null)
            {
                // No language in the name and no detection has run: proves nothing (see Present).
                continue;
            }

            found.Add(new SubtitleRef(lang, false));
            if (hi)
            {
                found.Add(new SubtitleRef(lang, true));
            }
        }

        return found;
    }

    /// <summary>
    /// True when this stream is a subtitle track that carries the film's DIALOGUE — readable text,
    /// not forced.
    /// <para>
    /// F-M284 (user decision 02.10.2026): this is the ONE predicate behind every "is this track part
    /// of the film?" decision in the plugin. A forced track carries the lines of foreign-language
    /// scenes only (F-M246), so it is not dialogue and must not count as coverage, must not be
    /// uploaded, and needs no language tag. A bitmap track carries pixels (F-M6/F-M257), so it cannot
    /// be read at all.
    /// </para>
    /// <para>
    /// It exists as a named rule because the same judgement used to be re-derived from Jellyfin's
    /// flags in five separate call sites (the seeder twice, the upload pipeline, the language gate,
    /// and the embedded-presence check). Copies drift: the four name parsers this codebase used to
    /// carry are the standing proof, and one of them silently read <c>.sdh</c> as the language "SD".
    /// A caller that needs a different QUESTION asks a different predicate; a caller that needs the
    /// same question calls this one.
    /// </para>
    /// </summary>
    /// <param name="stream">Media stream.</param>
    /// <returns>True when the stream is a non-forced text subtitle track.</returns>
    public static bool IsDialogueStream(MediaStream? stream)
        => stream != null && stream.IsTextSubtitleStream && !IsForcedStream(stream);

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
            // F-M246/F-M257/F-M284: the ONE predicate — a forced track carries only foreign-language
            // scenes, a bitmap track carries pixels (86 items were once marked "settled" for a
            // language whose only subtitle was unreadable). Neither is dialogue, so neither covers.
            if (s.Type != MediaStreamType.Subtitle || !IsDialogueStream(s))
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
    /// <summary>
    /// F-M284 (user decision 02.10.2026): the embedded subtitle tracks of a file, with BOTH
    /// properties, observed rather than filtered.
    /// <para>
    /// This used to skip forced and bitmap streams outright, which meant the registry never held a
    /// row for a track that exists — and then every caller that cared re-derived the exclusion from
    /// Jellyfin's stream flag, in five separate places (the seeder twice, the upload pipeline, the
    /// language gate, and the coverage check). A fact that is never recorded is a fact that gets
    /// re-guessed: the rule now lives in ONE reader, and the row states what the file carries.
    /// </para>
    /// <para>
    /// Bitmap tracks are still EXCLUDED, and that is a different matter: they carry no text at all
    /// (F-M6), so there is no subtitle datum to record — no language to resolve, nothing to upload,
    /// nothing to hash.
    /// </para>
    /// <para>
    /// A forced track is recorded with <c>Forced = true</c> and deliberately still carries its
    /// language: that is the whole point of recording it. It does not count as coverage anywhere
    /// (a forced track is not the film's dialogue, F-M246), so the language stays open and is
    /// searched for — and because the row exists, the reason is visible instead of having to be
    /// re-derived.
    /// </para>
    /// </summary>
    /// <param name="streams">The item's stream list.</param>
    /// <returns>One triple per mappable, non-bitmap TEXT embedded subtitle track.</returns>
    public static List<(int SubPos, string Lang, bool HearingImpaired, bool Forced)> EmbeddedTracks(IEnumerable<MediaStream>? streams)
    {
        var result = new List<(int, string, bool, bool)>();
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
            if (!s.IsTextSubtitleStream)
            {
                continue; // bitmap: no text, so no datum to record (F-M6)
            }

            string? lang = LanguageMapper.MapToSubdl(s.Language);
            if (lang != null)
            {
                result.Add((pos, lang, IsHearingImpairedStream(s), IsForcedStream(s)));
            }
        }

        return result;
    }

    /// <summary>
    /// F-M258: the stream positions the item HAS, whether or not their language is readable.
    /// <para>
    /// <see cref="EmbeddedTracks"/> reports only tracks whose language resolves, so a track whose
    /// tag Jellyfin cannot read yet — notably right after the language gate wrote it, because
    /// Jellyfin caches its stream list — is absent from that list even though it exists. A caller
    /// comparing stored rows against it would read "the language is unknown" as "the track is
    /// gone" and drop a valid row. This answers the other half of the question: does the position
    /// still exist at all? Positions follow the same rule as <see cref="EmbeddedTracks"/> — the
    /// unfiltered, non-external subtitle list, because ffmpeg's <c>0:s:N</c> counts every subtitle
    /// stream including forced and bitmap ones.
    /// </para>
    /// </summary>
    /// <param name="streams">The item's stream list.</param>
    /// <returns>Positions of every non-external subtitle stream.</returns>
    public static HashSet<int> SubtitlePositions(IEnumerable<MediaStream>? streams)
    {
        var positions = new HashSet<int>();
        if (streams == null)
        {
            return positions;
        }

        int pos = 0;
        foreach (var s in streams)
        {
            if (s.Type != MediaStreamType.Subtitle || s.IsExternal)
            {
                continue;
            }

            positions.Add(pos);
            pos++;
        }

        return positions;
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
