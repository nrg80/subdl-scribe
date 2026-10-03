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

namespace Jellyfin.Plugin.SubdlScribe.Language;

/// <summary>
/// Maps ISO 639-2 (ffprobe/MediaBrowser) three-letter codes to SubDL two-letter codes (F-M10).
/// The codes are the ones SubDL accepts, verified against the API.
/// </summary>
public static class LanguageMapper
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["eng"] = "EN", ["deu"] = "DE", ["ger"] = "DE", ["fre"] = "FR", ["fra"] = "FR",
        ["jpn"] = "JA", ["kor"] = "KO", ["chi"] = "ZH", ["zho"] = "ZH",
        ["ara"] = "AR", ["hin"] = "HI", ["tur"] = "TR", ["dut"] = "NL", ["nld"] = "NL",
        ["swe"] = "SV", ["dan"] = "DA", ["nor"] = "NO", ["fin"] = "FI",
        ["cze"] = "CS", ["ces"] = "CS", ["hun"] = "HU", ["rum"] = "RO", ["ron"] = "RO",
        ["bul"] = "BG", ["gre"] = "EL", ["ell"] = "EL", ["heb"] = "HE",
        ["tha"] = "TH", ["vie"] = "VI", ["ind"] = "ID", ["may"] = "MS", ["msa"] = "MS",
        ["ukr"] = "UK", ["slo"] = "SK", ["slk"] = "SK", ["slv"] = "SL",
        ["lit"] = "LT", ["lav"] = "LV", ["est"] = "ET",
        ["tam"] = "TA", ["tel"] = "TE", ["ben"] = "BN", ["urd"] = "UR",
        ["tgl"] = "TL", ["fil"] = "TL", ["swa"] = "SW",
        ["aze"] = "AZ", ["geo"] = "KA", ["khm"] = "KM", ["alb"] = "SQ",
        ["por"] = "PT", ["spa"] = "ES", ["ita"] = "IT", ["pol"] = "PL", ["rus"] = "RU",
        ["fas"] = "FA", ["per"] = "FA", ["mal"] = "ML", ["mar"] = "MR",
        ["epo"] = "EO", ["ice"] = "IS", ["isl"] = "IS",
        ["cat"] = "CA", ["hrv"] = "HR", ["srp"] = "SR", ["glg"] = "GL",
        ["eus"] = "EU", ["baq"] = "EU",
        ["arm"] = "HY", ["bos"] = "BS", ["bel"] = "BE", ["bur"] = "MY", ["mya"] = "MY",
        ["kan"] = "KN", ["kur"] = "KU", ["pus"] = "PS", ["kin"] = "RW", ["sin"] = "SI",
        ["som"] = "SO", ["sun"] = "SU", ["cre"] = "CR",
        ["kaz"] = "KK", ["kir"] = "KY", ["nob"] = "NO", ["scc"] = "SR", ["fr"] = "FR",
        ["mac"] = "MK", ["mon"] = "MN",
    };

    /// <summary>
    /// F-M261: reverse of <see cref="Map"/>, from the SubDL two-letter code to the ISO 639-2/B
    /// code a Matroska track tag is written in.
    /// </summary>
    /// <remarks>
    /// Only the languages the offline detector can actually return are listed: the tag writer
    /// needs this for exactly those, and a half-filled table invites the mistake of trusting it
    /// for the rest. Anything unmapped falls back to the lower-case two-letter code, which is a
    /// legal Matroska tag (ffprobe reports it unchanged, as it does for <c>und</c>) — a usable
    /// tag rather than a missing one.
    /// <para>
    /// The B spelling (not the terminological T form) is used on purpose: <c>ger</c>, <c>fre</c>,
    /// <c>chi</c>, <c>cze</c>, <c>dut</c>, <c>gre</c>, <c>rum</c> and <c>slo</c> are what these
    /// containers carry in practice and what ffprobe prints back; <c>deu</c>/<c>fra</c> would
    /// still be read correctly by <see cref="MapToSubdl"/> but would read as a change against the
    /// file's own neighbours.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> ToIso6392Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EN"] = "eng", ["DE"] = "ger", ["FR"] = "fre", ["ES"] = "spa", ["IT"] = "ita",
        ["PT"] = "por", ["NL"] = "dut", ["DA"] = "dan", ["SV"] = "swe", ["NO"] = "nob",
        ["FI"] = "fin", ["PL"] = "pol", ["CS"] = "cze", ["SK"] = "slo", ["SL"] = "slv",
        ["HU"] = "hun", ["RO"] = "rum", ["BG"] = "bul", ["EL"] = "gre", ["RU"] = "rus",
        ["UK"] = "ukr", ["TR"] = "tur", ["AR"] = "ara", ["HE"] = "heb", ["FA"] = "fas",
        ["HI"] = "hin", ["UR"] = "urd", ["TH"] = "tha", ["VI"] = "vie", ["ID"] = "ind",
        ["MS"] = "may", ["TL"] = "tgl", ["SW"] = "swa", ["JA"] = "jpn", ["KO"] = "kor",
        ["ZH"] = "chi", ["TA"] = "tam", ["TE"] = "tel", ["KA"] = "geo", ["SQ"] = "alb",
        ["CA"] = "cat", ["HR"] = "hrv", ["SR"] = "srp", ["LT"] = "lit", ["LV"] = "lav",
        ["ET"] = "est", ["IS"] = "ice", ["MK"] = "mac", ["AF"] = "afr", ["BN"] = "ben",
        ["GU"] = "guj", ["KN"] = "kan", ["ML"] = "mal", ["MR"] = "mar", ["NE"] = "nep",
        ["PA"] = "pan", ["SI"] = "sin", ["SO"] = "som", ["AZ"] = "aze", ["KK"] = "kaz",
        ["KY"] = "kir", ["UZ"] = "uzb", ["HY"] = "arm", ["BE"] = "bel", ["BS"] = "bos",
        ["MY"] = "bur", ["KM"] = "khm", ["LA"] = "lat", ["CY"] = "wel", ["GA"] = "gle"
    };

    /// <summary>
    /// F-M261: maps a SubDL two-letter code to the ISO 639-2 tag to write into a container.
    /// </summary>
    /// <param name="subdlCode">Two-letter code as <see cref="MapToSubdl"/> returns it.</param>
    /// <returns>
    /// The ISO 639-2/B code, or the lower-case input when it is unmapped — a legal Matroska tag
    /// is better than none. Null only for empty input.
    /// </returns>
    public static string? ToIso6392(string? subdlCode)
    {
        if (string.IsNullOrWhiteSpace(subdlCode))
        {
            return null;
        }

        string key = subdlCode.Trim();
        if (ToIso6392Map.TryGetValue(key, out string? iso))
        {
            return iso;
        }

        return key.Length is >= 2 and <= 3 && char.IsLetter(key[0])
            ? key.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// Maps a three-letter (or two-letter) language tag to the SubDL two-letter code.
    /// Unknown tags map to the first two letters uppercased if length &gt;= 2, else null.
    /// </summary>
    public static string? MapToSubdl(string? threeLetter)
    {
        if (string.IsNullOrWhiteSpace(threeLetter))
        {
            return null;
        }

        string key = threeLetter.Trim().ToLowerInvariant();
        if (Map.TryGetValue(key, out string? code))
        {
            return code;
        }

        if (key.Length >= 2 && key.Length <= 3 && char.IsLetter(key[0]))
        {
            return key[..2].ToUpperInvariant();
        }

        return null;
    }



    /// <summary>
    /// F-M17y: reverse lookup from the full language name returned by /user/mySubtitles
    /// (e.g. "English") to the SubDL two-letter code (e.g. "EN"). Falls back to the first
    /// two letters of the name if no exact mapping exists.
    /// </summary>
    public static string? MapToSubdlName(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
        {
            return null;
        }

        string name = languageName.Trim();
        string lower = name.ToLowerInvariant();
        string? code = lower switch
        {
            "english" => "EN",
            "german" => "DE",
            "french" => "FR",
            "spanish" => "ES",
            "italian" => "IT",
            "portuguese" => "PT",
            "russian" => "RU",
            "japanese" => "JA",
            "korean" => "KO",
            "chinese" => "ZH",
            "arabic" => "AR",
            "hindi" => "HI",
            "turkish" => "TR",
            "dutch" => "NL",
            "swedish" => "SV",
            "danish" => "DA",
            "norwegian" => "NO",
            "finnish" => "FI",
            "czech" => "CS",
            "hungarian" => "HU",
            "romanian" => "RO",
            "bulgarian" => "BG",
            "greek" => "EL",
            "hebrew" => "HE",
            "thai" => "TH",
            "vietnamese" => "VI",
            "indonesian" => "ID",
            "malay" => "MS",
            "ukrainian" => "UK",
            "slovak" => "SK",
            "slovenian" => "SL",
            "lithuanian" => "LT",
            "latvian" => "LV",
            "estonian" => "ET",
            "tamil" => "TA",
            "telugu" => "TE",
            "bengali" => "BN",
            "urdu" => "UR",
            "tagalog" => "TL",
            "swahili" => "SW",
            "azerbaijani" => "AZ",
            "georgian" => "KA",
            "khmer" => "KM",
            "albanian" => "SQ",
            "persian" => "FA",
            "malayalam" => "ML",
            "marathi" => "MR",
            "esperanto" => "EO",
            "icelandic" => "IS",
            "catalan" => "CA",
            "croatian" => "HR",
            "serbian" => "SR",
            "galician" => "GL",
            "basque" => "EU",
            "armenian" => "HY",
            "bosnian" => "BS",
            "belarusian" => "BE",
            "burmese" => "MY",
            "kannada" => "KN",
            "kurdish" => "KU",
            "pashto" => "PS",
            "kinyarwanda" => "RW",
            "sinhala" => "SI",
            "somali" => "SO",
            "sundanese" => "SU",
            "kazakh" => "KK",
            "kyrgyz" => "KY",
            "macedonian" => "MK",
            "mongolian" => "MN",
            _ => null
        };

        return code ?? (name.Length >= 2 ? name[..2].ToUpperInvariant() : null);
    }

    private static bool TitleLooksBrazilian(string title)
    {
        string t = title.ToLowerInvariant();
        foreach (string hint in new[] { "brazil", "brazilian", "pt-br", "pob", "portuguese (brazil)" })
        {
            if (t.Contains(hint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
