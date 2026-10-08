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
using System.Linq;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// Which subtitle DATA are covered for one media file — the ONE answer every reader asks.
/// <para>
/// F-M283 (user decision 02.10.2026): "is this item done?" is no longer a stored verdict. The
/// download mark (<c>SubtitlesDownloadedAt</c> plus a comma-separated language list) was a SECOND
/// truth beside the rows that actually describe the subtitles, and the two drifted:
/// <list type="bullet">
/// <item>the short-circuit asked the disk PLUS the stored variant verdict, while the check that
/// decided whether to WRITE the mark asked the disk alone — so a variant embedded in the container
/// had a registry verdict and no sidecar, the withhold check reported "no file evidence" and refused
/// the mark, and the mark was then never written, the item stayed due, and every pass re-downloaded
/// the regular subtitle again;</item>
/// <item>a language-keyed mark let one file stand in for another: a successful regular save closed
/// the variant's slot although no variant file existed.</item>
/// </list>
/// With the mark gone there is nothing left to drift. Completeness is DERIVED, on every ask, from
/// the evidence that describes the subtitles themselves — and because it is derived it cannot be
/// overtaken by a deletion: removing <c>Movie.de.sdh.srt</c> removes its evidence, and the item is
/// due again with no invalidation step and no bookkeeping.
/// </para>
/// <para>
/// The evidence is read at the grain of <see cref="SubtitleRef"/>, from three sources, and all three
/// are consulted for every caller so that two readers of one question can never disagree again:
/// <list type="number">
/// <item><b>the files next to the media file</b> — the exact pair each sidecar name states. This is
/// what self-corrects, and it is why a deleted variant makes the item due;</item>
/// <item><b>the embedded rows of the container</b> — a track inside the file, with its own language
/// and its own hearing-impaired flag, written by whichever pass observed the streams (F-M257). This
/// is the source that used to be missing on the withhold side;</item>
/// <item><b>the language-level embedded evidence</b> a caller may hold (Jellyfin's stream list, when
/// the configuration counts embedded tracks as coverage). It speaks about a language, never about a
/// variant.</item>
/// </list>
/// </para>
/// <para>
/// The language rule is kept from the previous reader: a subtitle of language L proves L, whether or
/// not it is the hearing-impaired variant (SDH is the same dialogue with annotations, F-M243), and
/// it proves L (HI) only when it IS that variant. So a lone <c>Movie.de.sdh.srt</c> satisfies both
/// pairs of German, and a lone <c>Movie.de.srt</c> satisfies German only.
/// </para>
/// <para>
/// No verdict is made here about whether a datum SHOULD exist: the caller supplies the pairs it
/// requires. The coverage question is "is there evidence", and the caller combines it with the QA
/// budget to decide what to search for.
/// </para>
/// </summary>
public sealed class SubtitleCoverage
{
    private readonly HashSet<SubtitleRef> _covered;

    private SubtitleCoverage(HashSet<SubtitleRef> covered)
    {
        _covered = covered;
    }

    /// <summary>Every pair with evidence, for reporting and assertions.</summary>
    public IReadOnlyCollection<SubtitleRef> Covered => _covered;

    /// <summary>
    /// Reads the coverage of one media file from the registry and the disk.
    /// </summary>
    /// <param name="registry">Content registry (may be null in host-free tests).</param>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="embeddedLanguages">Language-level embedded evidence the caller holds, or null.</param>
    /// <param name="onlyOwnDownloads">
    /// F-M333 (operator order 08.10.2026): the "Only missing languages" switch OFF. When true, the
    /// ONLY evidence that closes a pair is OUR OWN recorded download for it — a sidecar that came
    /// with the library, an embedded track, and Jellyfin's stream list all stop counting. The pair
    /// therefore stays open until this plugin has fetched the language once, even when the file
    /// already carries it.
    /// </param>
    /// <returns>The coverage.</returns>
    public static SubtitleCoverage Read(
        ContentHashRegistry? registry,
        string? mediaPath,
        IEnumerable<string>? embeddedLanguages = null,
        bool onlyOwnDownloads = false)
    {
        var covered = new HashSet<SubtitleRef>();

        // 0. The switch-off mode, and it RETURN here on purpose: every source below counts evidence
        //    the file happens to have, and with the switch off that evidence is exactly what must not
        //    close a pair. "Once per language" is measured against OUR OWN record, so the rule has to
        //    read that record and nothing else. A `downloaded` row is the record of a fetch this
        //    plugin performed; the refresh forgets it when the file it names is gone (F-M234), which
        //    is what keeps a deleted subtitle coming back on the next run instead of staying closed
        //    forever.
        if (onlyOwnDownloads)
        {
            string? ownHash = registry?.GetMediaHash(mediaPath);
            if (registry != null && !string.IsNullOrEmpty(ownHash))
            {
                foreach (var row in registry.GetSidecars(ownHash))
                {
                    if (row.Status == Data.SubtitleStatus.Downloaded)
                    {
                        Add(covered, row.Language, row.HearingImpaired, row.Forced);
                    }
                }
            }

            return new SubtitleCoverage(covered);
        }

        // 1. The files beside the media file. Each name states its own data — including forced,
        //    which is why the listing carries all three parts (F-M284).
        if (!string.IsNullOrEmpty(mediaPath))
        {
            foreach (var (_, language, hearingImpaired, forced) in SidecarNaming.List(mediaPath))
            {
                Add(covered, language, hearingImpaired, forced);
            }
        }

        // 2. The rows describing the container's own tracks. These carry BOTH properties, which is
        //    why a variant or forced track inside the container can be judged without a sidecar.
        string? mediaHash = registry?.GetMediaHash(mediaPath);
        if (registry != null && !string.IsNullOrEmpty(mediaHash))
        {
            foreach (var row in registry.GetEmbeds(mediaHash))
            {
                Add(covered, row.Language, row.HearingImpaired, row.Forced);
            }
        }

        // 3. Language-level evidence from the streams, when the caller may count it. It speaks about
        //    a language and never about a variant, and a forced stream is not in it (F-M246).
        foreach (var language in embeddedLanguages ?? Enumerable.Empty<string>())
        {
            Add(covered, language, hearingImpaired: false, forced: false);
        }

        return new SubtitleCoverage(covered);
    }

    /// <summary>
    /// Reads coverage from a datum list alone — the host-free path, for tests and probes that hold
    /// no database.
    /// <para>
    /// The SAME expansion rule as the file reader applies here, and it has to: a subtitle of
    /// language L proves L whether or not it is the variant (SDH is the same dialogue with
    /// annotations, F-M243), so a datum recorded as <c>DE (HI)</c> must also mark <c>DE</c> as
    /// covered. Skipping the expansion made a variant-only file look like it was missing its plain
    /// language and be fetched again — the exact conflation this model exists to remove, arriving
    /// from the other side.
    /// </para>
    /// </summary>
    /// <param name="data">Data known to be present.</param>
    /// <returns>The coverage.</returns>
    public static SubtitleCoverage FromPairs(IEnumerable<SubtitleRef> data)
    {
        var covered = new HashSet<SubtitleRef>();
        foreach (var datum in data)
        {
            Add(covered, datum.Language, datum.HearingImpaired, datum.Forced);
        }

        return new SubtitleCoverage(covered);
    }

    /// <summary>True when this pair has evidence.</summary>
    /// <param name="reference">The pair to test.</param>
    /// <returns>True when covered.</returns>
    public bool Covers(SubtitleRef reference) => _covered.Contains(reference);

    /// <summary>
    /// The required pairs that have NO evidence — what is actually open for this file.
    /// <para>
    /// This is the single question the download pipeline, the seeder and the refresh all ask. It
    /// returns PAIRS, so a caller can no longer lose the variant by projecting a token back to its
    /// language: the regular file and its variant come back as two separate entries, and a search fed
    /// from this list asks SubDL for the variant it is missing rather than for the language again.
    /// </para>
    /// </summary>
    /// <param name="required">Pairs the caller requires.</param>
    /// <returns>The open pairs, in the order given.</returns>
    public List<SubtitleRef> Open(IEnumerable<SubtitleRef> required)
        => required.Where(r => r.IsUsable && !_covered.Contains(r)).ToList();

    /// <summary>
    /// The languages that have an open REGULAR file — the input for the <c>hi=0</c> search pool.
    /// </summary>
    /// <param name="open">Open pairs.</param>
    /// <returns>Language codes.</returns>
    public static List<string> RegularLanguages(IEnumerable<SubtitleRef> open)
        => Distinct(open.Where(r => !r.HearingImpaired));

    /// <summary>
    /// The languages that have an open VARIANT — the input for the <c>hi=1</c> search pool.
    /// <para>
    /// The two searches are separate pools on SubDL's side (F-M241), so they must be fed separately
    /// here too. Sending one language list to both is what fetched the regular subtitle over the file
    /// already on disk.
    /// </para>
    /// </summary>
    /// <param name="open">Open pairs.</param>
    /// <returns>Language codes.</returns>
    public static List<string> VariantLanguages(IEnumerable<SubtitleRef> open)
        => Distinct(open.Where(r => r.HearingImpaired));

    private static List<string> Distinct(IEnumerable<SubtitleRef> pairs)
        => pairs.Select(r => r.Language)
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void Add(HashSet<SubtitleRef> covered, string? language, bool? hearingImpaired, bool? forced)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        // F-M285 (user decision 02.10.2026): a row that does not STATE its properties cannot claim
        // coverage, and the two cases are not the same fact:
        //   false → a statement: this subtitle is not the variant (or is not forced)
        //   null  → a gap: the row was written before the field existed, or nothing could decide
        // Reading the gap as `false` is how an unknown becomes a false claim — a legacy forced row
        // would pass as the film's dialogue and keep its language settled forever.
        //
        // Treating the gap as "no coverage" is deliberately the direction that CONVERGES: the datum
        // reads as open, the item is worked once more, and that pass states the value explicitly.
        // The opposite default would look settled forever with nothing to correct it.
        if (hearingImpaired == null || forced == null)
        {
            return;
        }

        if (forced.Value)
        {
            // F-M284: a forced datum covers ONLY ITSELF. It carries the lines of foreign-language
            // scenes, not the film's dialogue (F-M246), so it must never mark the regular datum — nor
            // the plain variant — as present. Recording it is still worthwhile: it is a fact about the
            // file, and it explains why an item can carry a track of a language and still be due.
            covered.Add(new SubtitleRef(language, hearingImpaired.Value, true));
            return;
        }

        // The language is covered by any non-forced subtitle of it; the variant only by a variant.
        covered.Add(new SubtitleRef(language, false));
        if (hearingImpaired.Value)
        {
            covered.Add(new SubtitleRef(language, true));
        }
    }
}
