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

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M521 (Operator-Anweisung 17.11.2026: „Und logging für uploads?"): die zwei Zeilen, die eine
/// Anfrage an eine Seite der tvst-Familie belegen, und die kurze Antwort, die in der Verdichtungszeile
/// eines Laufs steht.
/// <para>
/// Warum das eine eigene Stelle ist: SubDL schreibt seit jeher „API call" und „API response" ins Log;
/// tvsubtitles.net und moviesubtitles.org schrieben NICHTS. Damit war „tvst wurde gefragt" eine
/// Behauptung ohne Beleg — die Zahl „0 tvsubtitles candidate(s)" sah genauso aus wie ein Lauf, in dem
/// die Adresse überhaupt nicht angesprochen wurde. Die Zeilen sind deshalb für BEIDE Richtungen
/// dieselben: Suche, Abruf und Hochladen gehen durch denselben Weg.
/// </para>
/// <para>
/// Die Stufe ist <b>Debug</b> und sonst keine (Regel F-M24a: je Anfrage zwei Zeilen, nur auf Debug;
/// Verbose hat seine eigenen Sammelzeilen). Im Normalbetrieb kostet das keine einzige Zeile.
/// </para>
/// </summary>
public static class SiteTrace
{
    /// <summary>
    /// The request line. Names method and path — never a query string, so no token and no
    /// search term can ride along into a log file.
    /// </summary>
    /// <param name="method">HTTP method as sent.</param>
    /// <param name="url">The request URL.</param>
    /// <returns>The line handed to the debug channel.</returns>
    public static string Call(string method, string url) => $"API call {method} {Short(url)}";

    /// <summary>The answer line: same path, plus the status code the site returned.</summary>
    /// <param name="method">HTTP method as sent.</param>
    /// <param name="url">The request URL.</param>
    /// <param name="status">The status code, 0 when the request failed before an answer.</param>
    /// <returns>The line handed to the debug channel.</returns>
    public static string Response(string method, string url, int status)
        => $"API response {method} {Short(url)} → {status}";

    /// <summary>
    /// The short form used inside a run's summary line: how the site answered and how many hits came
    /// out of it. A failure before any answer reads as <c>no answer</c>, not as a status of zero.
    /// </summary>
    /// <param name="status">The status code of the last request of that search, 0 when none arrived.</param>
    /// <param name="hits">How many candidates the answer produced.</param>
    /// <returns>Text like <c>HTTP 200, 3 hit(s)</c> or <c>no answer, 0 hit(s)</c>.</returns>
    public static string Answer(int status, int hits)
        => (status > 0 ? "HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no answer")
            + ", " + hits.ToString(System.Globalization.CultureInfo.InvariantCulture) + " hit(s)";

    /// <summary>Host and path of a URL, query and fragment stripped.</summary>
    /// <param name="url">The full URL.</param>
    /// <returns>The short form; the input unchanged when it cannot be parsed.</returns>
    internal static string Short(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        return uri.GetLeftPart(UriPartial.Path);
    }
}
