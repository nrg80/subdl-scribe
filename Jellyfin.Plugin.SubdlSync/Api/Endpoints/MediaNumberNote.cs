// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.

using Jellyfin.Plugin.SubdlScribe.Registry;

namespace Jellyfin.Plugin.SubdlScribe.Api.Endpoints;

/// <summary>
/// F-M517: was eine Logzeile über das Speichern einer Gegenseiten-Nummer sagen darf.
/// <para>
/// Sie sagt WIE (über den Medien-Hash oder über den Pfad) — oder ehrlich, dass nichts geschrieben
/// wurde und warum. Vorher stand dort bei fehlendem Hash „gespeichert" bzw. „nicht gespeichert: kein
/// Medien-Hash bekannt", obwohl die Zeile über ihren Pfad erreichbar war; beides war eine Aussage
/// über den Weg, nicht über das Ergebnis.
/// </para>
/// <para>
/// Der Text steht EINMAL, weil die Film- und die Serienadresse dieselbe Frage beantworten und zwei
/// Kopien derselben deutschen Zeile nur auseinanderlaufen können.
/// </para>
/// </summary>
public static class MediaNumberNote
{
    /// <summary>Geschrieben über den Medien-Hash.</summary>
    public const string ByHash = "gespeichert (über den Medien-Hash).";

    /// <summary>Geschrieben über den Pfad, weil kein Medien-Hash bekannt war.</summary>
    public const string ByPath = "gespeichert über den Pfad (kein Medien-Hash bekannt).";

    /// <summary>
    /// Nichts geschrieben: kein Medien-Hash UND keine eindeutige Zeile mit diesem Pfad. Die Nummer
    /// gilt dann nur für diesen Lauf.
    /// </summary>
    public const string NotStored =
        "nicht gespeichert: kein Medien-Hash und keine eindeutige Pfad-Zeile im Store — die Nummer gilt nur für diesen Lauf.";

    /// <summary>
    /// Der Vermerk zu einem Schreibausgang.
    /// </summary>
    /// <param name="write">Wie geschrieben wurde (oder <see cref="MediaNumberWrite.None"/>).</param>
    /// <returns>Der Satz für die Logzeile.</returns>
    public static string Of(MediaNumberWrite write) => write switch
    {
        MediaNumberWrite.ByHash => ByHash,
        MediaNumberWrite.ByPath => ByPath,
        _ => NotStored,
    };
}
