// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.

using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubScribe.Configuration;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M489 (operator order 15.11.2026: "Reup und Kreuzkontamination sollen verhindert werden. Also auch
/// rein reup von tvst auf tvst. Kein reup von subdl auf subdl"): which sources may still take THIS
/// subtitle, decided in ONE place.
/// <para>
/// Two independent reasons can forbid a source, and both must be answered per source, not per run:
/// </para>
/// <list type="bullet">
/// <item><description>the switch is off, or the provenance is KNOWN at all (F-M487, verschaerft: ein
/// Download geht nirgends hoch — weder zurueck zur eigenen Quelle noch zur anderen);</description></item>
/// <item><description>that source ALREADY has this subtitle — its own upload mark is in the store
/// (F-M488). A re-upload to the same source is exactly as unwanted as a cross-source upload.</description></item>
/// </list>
/// <para>
/// The consequence is the case that used to be invisible: a subtitle uploaded to tvsubtitles.net long ago
/// is still allowed to go to SubDL, and a subtitle that SubDL already has is still allowed to go to
/// tvsubtitles.net. Only the pair "same source, again" is refused.
/// </para>
/// <para>
/// F-M508: die dritte Adresse (moviesubtitles.org) wird genauso gefragt wie die beiden anderen — je
/// Adresse eigener Schalter, eigene Marke, eigene Antwort. Sie ist die Film-Adresse derselben Familie;
/// ob sie in diesem Lauf überhaupt zuständig ist, entscheidet die Medienart im Aufrufer
/// (<see cref="SourceOrder.AppliesTo"/>), nicht dieses Tor.
/// </para>
/// </summary>
public static class UploadGate
{
    /// <summary>
    /// F-M489: the decision for every source at once. Only the answers of the sources that are switched on
    /// are meaningful; a source that is off is false here, and the caller names it as "switched off"
    /// instead of "already uploaded".
    /// </summary>
    /// <param name="subdlSwitch">Is the SubDL upload switch on?</param>
    /// <param name="tvSubtitlesSwitch">Is the tvsubtitles.net upload switch on?</param>
    /// <param name="origin">The subtitle's provenance as stored (null/empty = unknown).</param>
    /// <param name="subdlAlready">Does SubDL already have this subtitle (its own mark in the store)?</param>
    /// <param name="tvSubtitlesAlready">Does tvsubtitles.net already have it?</param>
    /// <param name="movieSubtitlesSwitch">F-M508: is the moviesubtitles.org upload switch on?</param>
    /// <param name="movieSubtitlesAlready">F-M508: does moviesubtitles.org already have it?</param>
    /// <returns>Which sources may run, and why nothing runs when none of them may.</returns>
    // F-M656: alle drei Quellen sind Pflicht. Mit Vorgabewerten `false` fuer die Filmquelle liess ein
    // vergessener Aufruf moviesubtitles still aus — genau das war an einer Stelle der Fall.
    public static UploadGateResult Decide(
        bool subdlSwitch, bool tvSubtitlesSwitch, string? origin, bool subdlAlready, bool tvSubtitlesAlready,
        bool movieSubtitlesSwitch, bool movieSubtitlesAlready)
    {
        var subdlSwitched = subdlSwitch && Provenance.MayUploadTo(origin, SubtitleProvider.Subdl);
        var tvSwitched = tvSubtitlesSwitch && Provenance.MayUploadTo(origin, SubtitleProvider.TvSubtitles);
        var msubSwitched = movieSubtitlesSwitch && Provenance.MayUploadTo(origin, SubtitleProvider.MovieSubtitles);

        var subdl = subdlSwitched && !subdlAlready;
        var tv = tvSwitched && !tvSubtitlesAlready;
        var msub = msubSwitched && !movieSubtitlesAlready;

        if (subdl || tv || msub)
        {
            return new UploadGateResult(subdl, tv, msub, null);
        }

        // Why not — the most specific answer wins, because it is the one that explains the row.
        if (!subdlSwitched && !tvSwitched && !msubSwitched)
        {
            // F-M500 (Operator-Anweisung 15.11.2026): eine BEKANNTE Herkunft sperrt ALLE Quellen — nicht
            // nur die fremde. Der Grund nennt sie, statt nur "cross-source" zu sagen.
            return new UploadGateResult(false, false, false,
                Provenance.OriginBlockedReason(origin) ?? "no-upload-source");
        }

        // Nur die Quellen nennen, die ueberhaupt duerften — eine abgeschaltete Quelle ist nicht
        // "schon hochgeladen", sondern aus.
        var already = string.Join(
            "+",
            new[] { (subdlSwitched, "subdl"), (tvSwitched, "tvsubtitles"), (msubSwitched, "moviesubtitles") }
                .Where(pair => pair.Item1)
                .Select(pair => pair.Item2));
        return new UploadGateResult(false, false, false, "already-uploaded:" + already);
    }

    /// <summary>
    /// F-M508: darf DIESE Quelle in diesem Lauf? Die Kette liest ihre Antwort hier und nicht aus drei
    /// Feldern, damit ein vierter Anbieter nicht an vier Stellen nachgetragen werden muss.
    /// </summary>
    /// <param name="result">Das Urteil von <see cref="Decide"/>.</param>
    /// <param name="provider">Die gefragte Quelle.</param>
    /// <returns>Wahr, wenn diese Quelle den Untertitel nehmen darf.</returns>
    public static bool May(this UploadGateResult result, SubtitleProvider provider) => provider switch
    {
        SubtitleProvider.Subdl => result.Subdl,
        SubtitleProvider.TvSubtitles => result.TvSubtitles,
        SubtitleProvider.MovieSubtitles => result.MovieSubtitles,
        _ => false,
    };
}

/// <summary>
/// F-M489: the result of <see cref="UploadGate.Decide"/>.
/// </summary>
/// <param name="Subdl">May the subtitle go to SubDL in this run?</param>
/// <param name="TvSubtitles">May it go to tvsubtitles.net in this run?</param>
/// <param name="MovieSubtitles">F-M508: may it go to moviesubtitles.org in this run?</param>
/// <param name="SkipReason">Why no source may run, or null when at least one may.</param>
public readonly record struct UploadGateResult(
    bool Subdl, bool TvSubtitles, bool MovieSubtitles, string? SkipReason)
{
    /// <summary>F-M508: how many sources may take it — 0 means "nothing runs".</summary>
    public int AllowedCount => (Subdl ? 1 : 0) + (TvSubtitles ? 1 : 0) + (MovieSubtitles ? 1 : 0);
}
