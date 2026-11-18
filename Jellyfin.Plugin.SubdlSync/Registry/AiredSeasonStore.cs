using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.SubScribe.Data;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SubScribe.Registry;

/// <summary>
/// F-M601 (Operator-Anweisung 18.11.2026: „Die imdb/tmdb folgenzahlabfragen könnte man auch beim pack
/// im store nach dem ersten mal speichern. Also erst store, wenn der nix hat von tmdb ziehen und dann
/// in den store einspeichern."): die Folgenzahl einer Staffel liegt im Store statt nur im Speicher.
/// <para>
/// Gemessen am 18.11.2026 im Lauf 14:29: die Pack-Planung fragte für 79 Staffeln die Folgenzahl ab —
/// **16.248 ms** für 79 Aufrufe (206 ms je Aufruf), also gut ein Fünftel der ganzen Pack-Phase. Die
/// Antwort liegt in <see cref="Api.SeasonEpisodeIndex"/> nur im Arbeitsspeicher („one answer per (imdb
/// id, season) per plugin lifetime"), und jeder Plugin-Start wirft sie weg — bei den heutigen
/// Neustarts (jeder Deploy einer) zahlt der erste Lauf danach die 16 s erneut.
/// </para>
/// <para>
/// Gespeichert wird nur die ANTWORT auf die Frage „welche Folgen sind gelaufen": die Folgennummern,
/// durch <c>|</c> getrennt. Kein Untertitel, kein Text, keine Metadaten. Schlüssel ist
/// <c>imdbId|season</c>.
/// </para>
/// <para>
/// Haltbarkeit: eine Staffel WÄCHST. Deshalb ist das Alter allein kein Maß — der Aufrufer prüft, ob
/// die gespeicherte Liste alle Folgen abdeckt, die auf der Platte liegen (siehe
/// <see cref="Covers"/>); deckt sie nicht, wird neu gefragt und überschrieben. Zusätzlich verfällt
/// eine Antwort nach 180 Tagen, damit auch eine nachträglich korrigierte Staffel irgendwann neu
/// geholt wird.
/// </para>
/// </summary>
public static class AiredSeasonStore
{
    /// <summary>Shelf life — a season's episode list is stable, but not forever.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);

    private static readonly object Gate = new();

    private static ScribeSqliteContext? _db;

    /// <summary>
    /// Attaches the store. Called once when the plugin builds its data context; without it the pack
    /// planning keeps asking on every start, exactly as before.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    public static void Attach(ScribeSqliteContext? db)
    {
        lock (Gate)
        {
            _db = db;
        }
    }

    /// <summary>
    /// Builds the key of one season.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <returns>The key, e.g. <c>tt1234567|3</c>.</returns>
    public static string KeyOf(string imdbId, int season)
        => imdbId.Trim().ToLowerInvariant() + "|" + season.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the stored episode numbers of a season.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="numbers">The stored episode numbers, when a fresh answer exists.</param>
    /// <returns>True when a fresh answer exists.</returns>
    public static bool TryGet(string imdbId, int season, out IReadOnlyList<int> numbers)
    {
        numbers = Array.Empty<int>();
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(imdbId) || season < 1)
        {
            return false;
        }

        string id = KeyOf(imdbId, season);
        try
        {
            lock (Gate)
            {
                var row = db.AiredSeasonRows.AsNoTracking().FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    return false;
                }

                if (row.Written + Lifetime < DateTime.UtcNow)
                {
                    return false;
                }

                var parsed = Parse(row.Value);
                if (parsed.Count == 0)
                {
                    return false;
                }

                numbers = parsed;
                return true;
            }
        }
        catch (Exception)
        {
            // Ein Store, der nicht antwortet, darf die Planung nicht anhalten — dann wird eben gefragt.
            return false;
        }
    }

    /// <summary>
    /// Stores the episode numbers of a season.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="numbers">The episode numbers that aired.</param>
    public static void Put(string imdbId, int season, IReadOnlyList<int> numbers)
    {
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(imdbId) || season < 1 || numbers.Count == 0)
        {
            return;
        }

        string id = KeyOf(imdbId, season);
        string value = string.Join("|", numbers.OrderBy(n => n).Select(n => n.ToString(CultureInfo.InvariantCulture)));
        try
        {
            lock (Gate)
            {
                var row = db.AiredSeasonRows.FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    db.AiredSeasonRows.Add(new AiredSeasonEntity { Id = id, Value = value, Written = DateTime.UtcNow });
                }
                else
                {
                    row.Value = value;
                    row.Written = DateTime.UtcNow;
                }

                db.SaveChanges();
            }
        }
        catch (Exception)
        {
            // Schreiben ist eine Verbesserung, kein Muss: der nächste Lauf fragt sonst einfach wieder.
        }
    }

    /// <summary>
    /// F-M601: covers a stored answer every episode that is on disk? A season that grew (new episodes
    /// arrived) is asked again instead of being judged by a stale list.
    /// </summary>
    /// <param name="stored">The episode numbers the store knows as aired.</param>
    /// <param name="onDisk">The episode numbers the library holds.</param>
    /// <returns>True when nothing on disk is missing from the stored list.</returns>
    public static bool Covers(IReadOnlyList<int> stored, IEnumerable<int> onDisk)
    {
        if (stored.Count == 0)
        {
            return false;
        }

        var known = stored as ISet<int> ?? new HashSet<int>(stored);
        return onDisk.All(known.Contains);
    }

    private static ScribeSqliteContext? Store()
    {
        lock (Gate)
        {
            return _db;
        }
    }

    private static List<int> Parse(string? value)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (string part in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number > 0)
            {
                result.Add(number);
            }
        }

        return result;
    }
}

/// <summary>
/// F-M601: the aired episode numbers of one season, as the pack planning needs them.
/// </summary>
public class AiredSeasonEntity
{
    /// <summary>Primary key: <c>imdbId|season</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The episode numbers that aired, <c>|</c>-separated and ascending.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>When the answer was written (UTC) — the age decides whether it is still used.</summary>
    public DateTime Written { get; set; } = DateTime.UtcNow;
}
