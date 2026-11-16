// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// F-M517: WIE eine Gegenseiten-Nummer (<c>MsubMovieId</c>, <c>TvstShowId</c>) ans Medium kam.
/// <para>
/// Die Antwort ist Teil des Ergebnisses, nicht Schmuck: eine Zeile, die „gespeichert" behauptet,
/// ohne zu sagen wodurch, war der Defekt — der Medien-Hash fehlt bei einer Datei, die noch keinen
/// OSHash-Eintrag hat (oder deren Datei nicht lesbar ist), und dann schrieb der alte Weg stumm
/// nichts, während die Logzeile „gespeichert" sagte.
/// </para>
/// </summary>
public enum MediaNumberWrite
{
    /// <summary>Nichts geschrieben: kein Medien-Hash UND keine eindeutige Zeile mit diesem Pfad.</summary>
    None = 0,

    /// <summary>Über den Medien-Hash — die Zeile, deren Id der Hash ist.</summary>
    ByHash = 1,

    /// <summary>
    /// Über den PFAD: kein Hash bekannt, aber genau EINE Medienzeile trägt diesen Pfad
    /// (<see cref="Data.MediaEntity.Path"/>). Dieselbe Spalte wird geschrieben, nur die Zeile wird
    /// anders gefunden.
    /// </summary>
    ByPath = 2,
}
