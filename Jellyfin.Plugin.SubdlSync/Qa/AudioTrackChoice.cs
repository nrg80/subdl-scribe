// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// F-M296: which audio track an analysis should read — decided by LANGUAGE, not
// by stream order.
//
// WHY THIS EXISTS
//
// Both audio-reading gates used to decode `0:a:0`, the first audio stream, with
// a comment defending the hard index (a missing stream must fail loudly instead
// of silently resolving to another one). That reasoning is sound about FAILING
// and wrong about WHICH stream. Measured over 304 files of this library that
// carry sidecar subtitles: the priority rule below picks a different track than
// `0:a:0` in 34 of 387 (file, subtitle-language) cases — about 9 %. The recurring
// shape is an Italian release whose FIRST track is the Italian dub and whose
// English original sits on track 1; a German subtitle then wants English (prio 2)
// and an English subtitle wants English too (prio 1), while the hard index reads
// the dub. 76 files carry no language tag on their first track at all.
//
// WHAT IT DOES NOT CLAIM
//
// The choice matters less than one might expect: on the one multi-track file
// measured closely (Italian + English audio) both tracks returned the same
// verdict (span 17.4 s vs. 18.1 s, median 8.4 s vs. 7.8 s, deviation 0.6-0.7 s),
// because the envelope reads sound, not language. The rule's value is that the
// assumption "the first track is the right one" is measurably false on these
// files — not that it changes the numbers much.
//
// PRIORITY (user specification)
//
//   1. an audio track whose language equals the SUBTITLE's language
//   2. an English audio track
//   3. the first track without a language tag, else the first track at all
//
// Language codes are matched through an explicit ISO map: a subtitle file name
// carries `en`/`de` (2 letters) while a container tag carries `eng`/`deu` (3).
// Comparing the two sets directly never matches — a first attempt at counting
// this reported "subtitle language in NO audio track: 252 of 304" and "wrong
// first track: 0", both pure artefacts of that mismatch. Hence the map, and the
// rule that a zero or a perfect value is root-caused rather than reported.
using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SubdlScribe.Qa;

/// <summary>
/// F-M296: chooses the audio stream an audio-reading gate should decode, by
/// language and the priority rule above.
/// </summary>
public static class AudioTrackChoice
{
    /// <summary>Audio-relative index of the stream a gate should read (feeds <c>-map 0:a:N</c>).</summary>
    public const int FallbackPosition = 0;

    /// <summary>2-letter code to every 3-letter tag a container may carry for it.</summary>
    private static readonly Dictionary<string, string[]> Iso = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ["eng"],
        ["de"] = ["deu", "ger"],
        ["es"] = ["spa"],
        ["fr"] = ["fra", "fre"],
        ["it"] = ["ita"],
        ["pt"] = ["por"],
        ["nl"] = ["nld", "dut"],
        ["sv"] = ["swe"],
        ["no"] = ["nor"],
        ["da"] = ["dan"],
        ["fi"] = ["fin"],
        ["pl"] = ["pol"],
        ["cs"] = ["ces", "cze"],
        ["hu"] = ["hun"],
        ["el"] = ["ell", "gre"],
        ["ro"] = ["ron", "rum"],
        ["ru"] = ["rus"],
        ["uk"] = ["ukr"],
        ["tr"] = ["tur"],
        ["ar"] = ["ara"],
        ["he"] = ["heb"],
        ["hi"] = ["hin"],
        ["zh"] = ["zho", "chi"],
        ["ja"] = ["jpn"],
        ["ko"] = ["kor"],
        ["th"] = ["tha"],
        ["vi"] = ["vie"],
        ["id"] = ["ind"],
        ["ms"] = ["msa", "may"]
    };

    /// <summary>3-letter tag to its 2-letter code, built from <see cref="Iso"/>.</summary>
    private static readonly Dictionary<string, string> To2 = BuildTo2();

    private static Dictionary<string, string> BuildTo2()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string two, string[] threes) in Iso)
        {
            foreach (string three in threes)
            {
                map[three] = two;
            }
        }

        return map;
    }

    /// <summary>One decision, with its reason, so a log line can name what was chosen and why.</summary>
    /// <param name="Position">Audio-relative position to decode, for <c>-map 0:a:N</c>.</param>
    /// <param name="Priority">1 = language match, 2 = English, 3 = untagged/first.</param>
    /// <param name="Reason">Short human-readable explanation.</param>
    /// <param name="TrackCount">How many audio tracks the file carries.</param>
    public readonly record struct Choice(int Position, int Priority, string Reason, int TrackCount);

    /// <summary>
    /// Canonical 2-letter form of a language tag in either length, or <c>""</c> when
    /// the tag is missing, <c>und</c>/<c>undefined</c>, or unknown.
    /// </summary>
    /// <param name="tag">Tag as found in a file name or a container.</param>
    /// <returns>2-letter code, or empty.</returns>
    public static string Canonical(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        string t = tag.Trim();
        if (t.Equals("und", StringComparison.OrdinalIgnoreCase)
            || t.Equals("undefined", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (t.Length == 2)
        {
            return Iso.ContainsKey(t) ? t.ToLowerInvariant() : t.ToLowerInvariant();
        }

        return To2.TryGetValue(t, out string? two) ? two : t.ToLowerInvariant();
    }

    /// <summary>
    /// Picks the audio stream to read. Never throws; an empty or absent stream list
    /// yields position 0, which is what the caller would have used before.
    /// </summary>
    /// <param name="streams">Jellyfin's streams for the item, in container order.</param>
    /// <param name="subtitleLang">The subtitle's language (2- or 3-letter).</param>
    /// <returns>The choice.</returns>
    public static Choice Choose(IEnumerable<MediaStream>? streams, string? subtitleLang)
    {
        var audio = (streams ?? [])
            .Where(s => s.Type == MediaStreamType.Audio)
            .Select(s => Canonical(s.Language))
            .ToList();

        if (audio.Count == 0)
        {
            return new Choice(FallbackPosition, 3, "no audio stream listed — first position assumed", 0);
        }

        string want = Canonical(subtitleLang);

        // Prio 1: the track that speaks the subtitle's language.
        if (want.Length > 0)
        {
            int hit = audio.FindIndex(l => l.Length > 0 && l == want);
            if (hit >= 0)
            {
                return new Choice(hit, 1, $"track language matches the subtitle ({want})", audio.Count);
            }
        }

        // Prio 2: English.
        int eng = audio.FindIndex(l => l == "en");
        if (eng >= 0)
        {
            return new Choice(eng, 2, "English track (subtitle language not among the tracks)", audio.Count);
        }

        // Prio 3: a track with no language tag, else the first track.
        int untagged = audio.FindIndex(l => l.Length == 0);
        if (untagged >= 0)
        {
            return new Choice(untagged, 3, "first track without a language tag", audio.Count);
        }

        return new Choice(FallbackPosition, 3, "no matching and no English track — first position", audio.Count);
    }
}
