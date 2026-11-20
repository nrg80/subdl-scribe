using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M574 (Operator-Vorgabe 17.11.2026, zweimal nachgeschärft: „Imdb/tmdb bitte in der offiziellen
/// groß/Kleinschreibung"):
/// <code>
/// IMDb: tt15875168 / TMDb: 201124 — S01E05: Mo / Tombstone — 2022
/// </code>
/// Die Seite macht aus jedem Zeilenumbruch ein Leerzeichen (am 17.11.2026 in Produktion gesehen), also
/// trennt ein sichtbarer Gedankenstrich die drei Teile.
/// Der Kommentar ist der einzige Ort, an dem der richtige Folgentitel mitkommt — den Dateinamen baut die
/// Seite aus ihrem eigenen Folgeneintrag (an „Mo" S01E05 gemessen: Bibliothek „Tombstone", Seite
/// „Episode 5"). Eine Zeile fällt weg, wenn ihr Inhalt fehlt; geraten wird nichts.
/// </summary>
public static class UploadComment
{
    /// <summary>Baut den Kommentar für eine Folge.</summary>
    /// <param name="imdbId">IMDb-Kennung (Serie).</param>
    /// <param name="tmdbId">TMDb-Kennung (Serie).</param>
    /// <param name="season">Staffel.</param>
    /// <param name="episode">Folge.</param>
    /// <param name="showTitle">Serientitel.</param>
    /// <param name="episodeTitle">Folgentitel, null bei einem Platzhalter.</param>
    /// <param name="year">Jahr des Titels, 0 wenn unbekannt.</param>
    /// <returns>Der mehrzeilige Kommentar.</returns>
    public static string Series(
        string? imdbId, string? tmdbId, int season, int episode,
        string? showTitle, string? episodeTitle, int year)
    {
        var zweite = new List<string>
        {
            "S" + season.ToString("D2", CultureInfo.InvariantCulture)
                + "E" + episode.ToString("D2", CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(showTitle))
        {
            zweite[0] += ": " + showTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(episodeTitle))
        {
            zweite.Add(episodeTitle.Trim());
        }

        return Build(imdbId, tmdbId, string.Join(" / ", zweite), year);
    }

    /// <summary>
    /// Baut den Kommentar für einen Staffel-Pack (tvst): die Zeile nennt die Staffel statt der Folge,
    /// der Folgentitel entfällt. Dieselbe Handschrift wie <see cref="Series"/>, damit Einzel- und
    /// Pack-Upload nebeneinander lesbar bleiben — Beispiel:
    /// <c>IMDb: tt33246591 / TMDb: 271087 — S02: The Assassin — 2025</c>.
    /// </summary>
    /// <param name="imdbId">IMDb-Kennung (Serie).</param>
    /// <param name="tmdbId">TMDb-Kennung (Serie).</param>
    /// <param name="season">Staffel.</param>
    /// <param name="showTitle">Serientitel.</param>
    /// <param name="year">Jahr des Titels, 0 wenn unbekannt.</param>
    /// <returns>Der Kommentar.</returns>
    public static string Season(string? imdbId, string? tmdbId, int season, string? showTitle, int year)
    {
        var zeile = "S" + season.ToString("D2", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(showTitle))
        {
            zeile += ": " + showTitle.Trim();
        }

        return Build(imdbId, tmdbId, zeile, year);
    }

    /// <summary>Baut den Kommentar für einen Film (die Folgezeile entfällt).</summary>
    /// <param name="imdbId">IMDb-Kennung.</param>
    /// <param name="tmdbId">TMDb-Kennung.</param>
    /// <param name="movieTitle">Filmtitel.</param>
    /// <param name="year">Jahr, 0 wenn unbekannt.</param>
    /// <returns>Der mehrzeilige Kommentar.</returns>
    public static string Movie(string? imdbId, string? tmdbId, string? movieTitle, int year)
        => Build(imdbId, tmdbId, movieTitle, year);

    private static string Build(string? imdbId, string? tmdbId, string? zweiteZeile, int year)
    {
        var zeilen = new List<string>();
        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            ids.Add("IMDb: " + imdbId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(tmdbId))
        {
            ids.Add("TMDb: " + tmdbId.Trim());
        }

        if (ids.Count > 0)
        {
            zeilen.Add(string.Join(" / ", ids));
        }

        if (!string.IsNullOrWhiteSpace(zweiteZeile))
        {
            zeilen.Add(zweiteZeile.Trim());
        }

        if (year > 0)
        {
            zeilen.Add(year.ToString(CultureInfo.InvariantCulture));
        }

        // Operator-Entscheidung 17.11.2026: „Gedankenstrich bitte" — die Seite kennt keine Umbrüche.
        return string.Join(" — ", zeilen);
    }
}
