// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Configuration;

namespace Jellyfin.Plugin.SubdlScribe.Api.Endpoints;

/// <summary>
/// F-M504 (Nutzerergänzung 15.11.2026: „Dritte bis n quelle sind ebenfalls denkbar"): die Reihenfolge,
/// in der die Quellen gefragt werden — EINE Stelle für beide Richtungen.
/// <para>
/// Die Konfiguration kennt heute genau zwei Werte (<c>DownloadSourceOrder</c>: „SubDL zuerst" /
/// „tvsubtitles.net zuerst"). Das ist eine Aussage über die FÜHRENDE Quelle, nicht über die Anzahl:
/// wer führt, wird vorn einsortiert, und alle übrigen Quellen folgen in der Reihenfolge, in der sie im
/// Enum <see cref="SubtitleProvider"/> stehen. Damit ist eine dritte Quelle ohne Umbau einsortierbar:
/// ein Enum-Eintrag und ein Endpunkt genügen, an dieser Datei und an den Schleifen ändert sich nichts.
/// </para>
/// <para>
/// Die alternative Lesart — eine Liste von Anbietern in der Konfiguration — wäre die flexiblere, kostet
/// aber eine neue GUI-Option; der Operator hat für den Upload ausdrücklich „keine neue GUI-Option"
/// verlangt (F-M495), und für den Download gilt dieselbe Reihenfolge. Deshalb: ein Führer plus
/// Enum-Rest, kein zweiter Einstellungsblock.
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
    /// Die Quelle, die die konfigurierte Reihenfolge nach vorn stellt. Sie wird als ERSTE gefragt; alle
    /// anderen folgen (siehe <see cref="ForOrder"/>).
    /// </summary>
    /// <param name="order">Die gespeicherte Reihenfolge.</param>
    /// <returns>Die führende Quelle.</returns>
    public static SubtitleProvider Leading(PluginConfiguration.DownloadSourceOrder order) =>
        order == PluginConfiguration.DownloadSourceOrder.SubdlFirst
            ? SubtitleProvider.Subdl
            : SubtitleProvider.TvSubtitles;

    /// <summary>
    /// ALLE Quellen in Frage-Reihenfolge: die führende zuerst, danach die übrigen in Enum-Reihenfolge.
    /// Ob eine Quelle tatsächlich gefragt wird (Schalter an, Endpunkt verdrahtet), entscheidet der
    /// Aufrufer JE QUELLE — diese Methode sagt nur, in welcher Reihenfolge gefragt würde.
    /// </summary>
    /// <param name="order">Die gespeicherte Reihenfolge.</param>
    /// <returns>Alle Quellen, führende zuerst.</returns>
    public static IReadOnlyList<SubtitleProvider> ForOrder(PluginConfiguration.DownloadSourceOrder order)
    {
        var fuehrend = Leading(order);
        return Enum.GetValues<SubtitleProvider>()
            .Where(provider => provider != fuehrend)
            .Prepend(fuehrend)
            .ToList();
    }
}
