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

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// One subtitle DATUM: a language plus the properties OF THAT DATUM — hearing-impaired and forced.
/// <para>
/// This is the atom the whole plugin judges. A hearing-impaired subtitle is not a property of the
/// media file and not a modifier of a language — it is its own subtitle, with its own identity, its
/// own row and its own hash. <c>Movie.de.srt</c> and <c>Movie.de.sdh.srt</c> are two files, two
/// sidecar rows keyed by their content, and two entries in the embedded area when they sit in the
/// container. The tuple (language, hearing-impaired, forced) is therefore the smallest thing a
/// question can be asked about, and every reader and writer in the plugin asks at that grain.
/// </para>
/// <para>
/// F-M282 (user decision 02.10.2026): the tuple replaces the string token vocabulary. The download
/// mark used to carry <c>DE</c> and <c>DE:hi</c> as text inside a comma-separated language list,
/// which meant the properties had to be re-derived — and every projection that mapped a token back to
/// its plain language was a place the conflation re-entered: a missing variant re-requested the
/// LANGUAGE, so the regular subtitle was fetched over the file already on disk (41 downloads in one
/// run against a 50/day limit, 35 marks withheld in the same run, the same content hash on three
/// consecutive days). A structured tuple cannot be projected back by accident: there is no spelling
/// of "DE" that carries the variant, and no parse that can lose it.
/// </para>
/// <para>
/// F-M284 (user decision 02.10.2026): <b>the two properties are NOT symmetric, and the type says
/// so.</b> Jellyfin reports both on its streams, so both are readable at the source — but only
/// hearing-impaired has a counterpart on SubDL's side:
/// <list type="bullet">
/// <item><b>hearing-impaired is bilateral.</b> SubDL serves it from two separate server-side pools
/// (<c>&amp;hi=1</c> / <c>&amp;hi=0</c>, F-M241) and accepts the flag on upload, so the datum can be
/// SEARCHED for and DELIVERED.</item>
/// <item><b>forced is local.</b> SubDL has no forced filter and returns no forced field — measured
/// live 02.10.2026: <c>forced=1</c>, <c>forced=0</c> and no parameter all returned the same 10
/// candidates, the response carries only <c>hi</c>, and 0 of 22 candidates across three languages
/// carried "forced" in the release name. A forced subtitle therefore cannot be sought, fetched or
/// announced; it can only be <b>observed</b>.</item>
/// </list>
/// The consequence is the rule the datum carries: <see cref="Forced"/> never counts as coverage. A
/// language whose only track is forced is NOT covered — a forced track carries only the lines of
/// foreign-language scenes, not the film's dialogue (F-M246) — so the regular datum stays open and is
/// searched for. Before this type existed that rule was re-derived in five separate call sites; it
/// now lives in one reader.
/// </para>
/// <para>
/// The type is a value: two tuples with the same language and the same flags ARE the same datum, so
/// sets and dictionaries of them answer "do I already have this file" without a comparison helper.
/// The language is normalized on construction, which is what makes that equality hold across the
/// case differences the sources produce (Jellyfin writes <c>eng</c>, the configuration <c>EN</c>).
/// </para>
/// </summary>
public readonly record struct SubtitleRef
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleRef"/> struct.
    /// </summary>
    /// <param name="language">Language code (two or three letters, any case).</param>
    /// <param name="hearingImpaired">True for the hearing-impaired / SDH variant.</param>
    /// <param name="forced">True for a forced track (foreign-language scenes only, F-M246).</param>
    public SubtitleRef(string? language, bool hearingImpaired, bool forced = false)
    {
        Language = (language ?? string.Empty).Trim().ToUpperInvariant();
        HearingImpaired = hearingImpaired;
        Forced = forced;
    }

    /// <summary>Language code, normalized to upper case.</summary>
    public string Language { get; }

    /// <summary>True when this datum is the hearing-impaired variant of its language.</summary>
    public bool HearingImpaired { get; }

    /// <summary>
    /// True when this datum is a forced track — it carries the lines of foreign-language scenes, not
    /// the film's dialogue, so it is observed but never counts as coverage (F-M246/F-M284).
    /// </summary>
    public bool Forced { get; }

    /// <summary>True when the tuple names a language at all — an empty one is no datum.</summary>
    public bool IsUsable => Language.Length > 0;

    /// <summary>
    /// True when this datum can be DELIVERED — i.e. SubDL has a counterpart for it.
    /// <para>
    /// A forced datum is observed locally only: it cannot be sought (no server-side filter), fetched,
    /// or announced (no upload field). Callers that plan work must check this; callers that merely
    /// record what a file carries must not.
    /// </para>
    /// </summary>
    public bool IsDeliverable => IsUsable && !Forced;

    /// <summary>
    /// The tuple as one readable line, for logs and reports. <c>DE</c>, <c>DE (HI)</c>,
    /// <c>DE (forced)</c>, <c>DE (HI, forced)</c>.
    /// <para>
    /// Deliberately NOT the old <c>DE:hi</c> spelling: that form was a parseable token and a
    /// separator that could be re-read as a key. This one is presentation only — nothing parses it.
    /// </para>
    /// </summary>
    /// <returns>Readable form.</returns>
    public override string ToString()
    {
        if (!HearingImpaired && !Forced)
        {
            return Language;
        }

        var marks = new System.Collections.Generic.List<string>(2);
        if (HearingImpaired)
        {
            marks.Add("HI");
        }

        if (Forced)
        {
            marks.Add("forced");
        }

        return Language + " (" + string.Join(", ", marks) + ")";
    }

    /// <summary>
    /// The data a configured language set requires: the regular subtitle always, the variant when the
    /// hearing-impaired switch asks for it.
    /// <para>
    /// This replaces <c>RequiredTokens</c>. The switch turning on ADDS the variant data, so an item
    /// that already has its subtitles simply becomes due for the variant; turning it off removes
    /// them, so nothing has to be cleaned up afterwards.
    /// </para>
    /// <para>
    /// F-M284: no required datum is ever forced. A forced track is not a deliverable target — it is
    /// what the file happens to carry, and the requirement it creates is the REGULAR subtitle, which
    /// stays open precisely because the forced track does not cover it.
    /// </para>
    /// </summary>
    /// <param name="languages">Configured target languages.</param>
    /// <param name="includeHearingImpaired">True when the HI switch is on.</param>
    /// <returns>One datum per required file.</returns>
    public static System.Collections.Generic.List<SubtitleRef> Required(
        System.Collections.Generic.IEnumerable<string> languages, bool includeHearingImpaired)
    {
        var required = new System.Collections.Generic.List<SubtitleRef>();
        foreach (var language in languages)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                continue;
            }

            required.Add(new SubtitleRef(language, false));
            if (includeHearingImpaired)
            {
                required.Add(new SubtitleRef(language, true));
            }
        }

        return required;
    }
}
