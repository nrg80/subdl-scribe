// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Configuration;

namespace Jellyfin.Plugin.SubdlScribe.Api.Endpoints;

/// <summary>
/// F-M504/F-M509 (Operator-Klarstellung 15.11.2026: „Seperate into subdl and tvst. Tvst is splitted into
/// tvst and moviesub itself"): die Reihenfolge, in der die Quellen gefragt werden — EINE Stelle für beide
/// Richtungen.
/// <para>
/// ES GIBT ZWEI QUELLEN, NICHT DREI. Auf oberster Ebene stehen SubDL und die <b>tvst-Familie</b>. Die
/// Familie ist in sich aufgeteilt in zwei Adressen: tvsubtitles.net führt Serien, moviesubtitles.org
/// führt Filme. Welche der beiden genommen wird, entscheidet ALLEIN die Medienart aus TMDb
/// (<c>isSeries</c>) — nie ein Dateiname und nie ein Bibliotheksname.
/// </para>
/// <para>
/// DESHALB BLEIBT DIE EINSTELLUNG ZWEIWERTIG (<see cref="PluginConfiguration.DownloadSourceOrder"/>:
/// SubDL zuerst / tvst-Familie zuerst). Die Reihenfolge gilt auf Familien-Ebene: „tvst zuerst" heißt bei
/// einer Serie tvsubtitles.net zuerst und bei einem Film moviesubtitles.org zuerst — in beiden Fällen
/// danach SubDL. „SubDL zuerst" ist die Umkehrung. Eine dritte Position in der Reihenfolge gibt es
/// nicht; die Familie löst intern auf ihre zuständige Seite auf (<see cref="FamilyPage"/>).
/// </para>
/// <para>
/// Vorher (F-M504) stand hier ein Führer plus „alle übrigen in Enum-Reihenfolge". Das war eine Aussage
/// über Anbieter, nicht über Quellen, und ließ bei Filmen nur SubDL übrig — die tvst-Familie konnte dort
/// nicht führen, weil ihre Serien-Seite für Filme nie zuständig ist. Mit der Familien-Ebene wirkt die
/// Einstellung in beiden Medienarten.
/// </para>
/// </summary>
public static class SourceOrder
{
    /// <summary>
    /// F-M504: die Quelle, deren Abfrage die Hauptsuche selbst fährt — SubDLs Id-Suche
    /// (<c>RunV2SearchAsync</c>) mit Id-Prüfung/Backfill, HI-Pool und Quota-Pfaden. Sie wird NICHT
    /// über ihren Endpunkt gefragt, und sie ist die eine Quelle, deren Frage kein Endpunkt-Abruf ist.
    /// <para>
    /// Der Name steht HIER und nicht in der Pipeline, weil die Frage-Reihenfolge dieselbe Aussage
    /// braucht: an welcher Stelle der Liste die SubDL-Suche läuft, entscheidet, wer zuerst gefragt
    /// wird. Die Pipeline und die Prüfung lesen deshalb dieselbe Konstante.
    /// </para>
    /// </summary>
    public const SubtitleProvider MainSearch = SubtitleProvider.Subdl;

    /// <summary>
    /// F-M509: gehört diese Adresse zur tvst-Familie? tvsubtitles.net und moviesubtitles.org sind EINE
    /// Quelle mit zwei Adressen — nicht zwei Quellen neben SubDL.
    /// </summary>
    /// <param name="provider">Die Adresse.</param>
    /// <returns>Wahr für eine Adresse der Familie.</returns>
    public static bool InFamily(SubtitleProvider provider) =>
        provider is SubtitleProvider.TvSubtitles or SubtitleProvider.MovieSubtitles;

    /// <summary>
    /// F-M509: die Adresse, unter der die Familie diese Medienart führt. Serie → tvsubtitles.net,
    /// Film → moviesubtitles.org. Das ist die EINZIGE Stelle, an der die Medienart eine Seite auswählt.
    /// </summary>
    /// <param name="isSeries">Wahr für eine Serie, falsch für einen Film (aus TMDb).</param>
    /// <returns>Die zuständige Seite der Familie.</returns>
    public static SubtitleProvider FamilyPage(bool isSeries) =>
        isSeries ? SubtitleProvider.TvSubtitles : SubtitleProvider.MovieSubtitles;

    /// <summary>
    /// F-M509: führt die tvst-Familie die Frage-Reihenfolge an? Die Einstellung ist eine Aussage über
    /// die Familie, nicht über eine ihrer beiden Adressen.
    /// </summary>
    /// <param name="order">Die gespeicherte Reihenfolge.</param>
    /// <returns>Wahr, wenn die Familie zuerst gefragt wird.</returns>
    public static bool FamilyFirst(PluginConfiguration.DownloadSourceOrder order) => order switch
    {
        PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst => true,
        PluginConfiguration.DownloadSourceOrder.SubdlFirst => false,

        // Ein unbekannter Zahlenwert kann nur aus einer von Hand geschriebenen Datei kommen. Er fuehrt
        // die Familie — so, wie der Vorgabewert der Einstellung steht — statt still SubDL vorn einzusetzen.
        _ => true,
    };

    /// <summary>
    /// F-M507 (Nutzerauftrag 15.11.2026: „Nur das series halt nach tv show und movies nach
    /// moviesubtitels gehen."): ist diese Adresse für diese MEDIENART zuständig?
    /// <para>
    /// Die Regel ist eine Aussage über die Quelle, nicht über den Dateinamen und nicht über den
    /// Bibliotheksnamen — die Medienart kommt aus der TMDb-Auflösung (<c>isSeries</c>), nie aus einer
    /// Schreibweise:
    /// <list type="bullet">
    /// <item><description>SubDL unterscheidet nicht — es indiziert Serien und Filme über Ids und bleibt
    /// für beides zuständig.</description></item>
    /// <item><description>tvsubtitles.net führt Serien: nur <c>isSeries</c>.</description></item>
    /// <item><description>moviesubtitles.org führt Filme: nur <c>!isSeries</c>.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="provider">Die Quelle.</param>
    /// <param name="isSeries">Wahr für eine Serie, falsch für einen Film (aus TMDb).</param>
    /// <returns>Wahr, wenn diese Quelle für diese Medienart gefragt werden darf.</returns>
    public static bool AppliesTo(SubtitleProvider provider, bool isSeries) => provider switch
    {
        SubtitleProvider.Subdl => true,
        SubtitleProvider.TvSubtitles => isSeries,
        SubtitleProvider.MovieSubtitles => !isSeries,
        _ => false,
    };

    /// <summary>
    /// F-M509: die beiden Quellen in Frage-Reihenfolge — die Familie als die eine Adresse, die für diese
    /// Medienart zuständig ist, und SubDL. Genau zwei Einträge, in jeder Medienart.
    /// <para>
    /// Ob eine Quelle tatsächlich gefragt wird (Schalter an, Endpunkt verdrahtet, konfiguriert),
    /// entscheidet der Aufrufer JE QUELLE — diese Methode sagt nur, in welcher Reihenfolge gefragt
    /// würde. Sie ist damit auch die Antwort auf „welche Seite der Familie, und wo steht sie": beides
    /// kommt aus <paramref name="isSeries"/> und <paramref name="order"/>.
    /// </para>
    /// </summary>
    /// <param name="order">Die gespeicherte Reihenfolge (Familien-Ebene).</param>
    /// <param name="isSeries">Wahr für eine Serie, falsch für einen Film.</param>
    /// <returns>Die zuständigen Quellen, zuerst gefragte zuerst.</returns>
    public static IReadOnlyList<SubtitleProvider> ForMedia(
        PluginConfiguration.DownloadSourceOrder order, bool isSeries)
    {
        var seite = FamilyPage(isSeries);
        return FamilyFirst(order)
            ? new[] { seite, MainSearch }
            : new[] { MainSearch, seite };
    }
}
