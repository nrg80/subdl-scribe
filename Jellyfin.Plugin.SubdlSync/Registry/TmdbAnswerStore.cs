using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Data;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// F-M542 (Operator-Anweisung 16.11.2026: „Ok. In den store damit"): die TMDb-Antworten liegen im
/// Store statt nur im Speicher.
/// <para>
/// Gemessen am 16.11.2026: 555 TMDb-Anfragen an einem Tag, davon 272 verschiedene — die Wiederholungen
/// kamen nicht aus einem Lauf (ein Zyklus fragte 24 verschiedene, 2 doppelt), sondern aus den 31
/// Plugin-Starts des Tages. <see cref="Api.TmdbImdbResolver"/> hielt seine Antworten in einem statischen
/// Wörterbuch; jeder Neustart warf sie weg und dieselben Titel, Ids und Serien wurden erneut bei TMDb
/// erfragt.
/// </para>
/// <para>
/// Hier steht deshalb genau das, was vorher nur im Wörterbuch stand: die FRAGE als Schlüssel und die
/// ANTWORT als Wert, unverändert die Pipe-Zeile des Resolvers (<c>imdb|tmdb|gefunden|istSerie</c> usw.).
/// Kein Film, kein Untertitel, keine Metadaten — nur „was hat TMDb auf diese Frage geantwortet".
/// </para>
/// <para>
/// Haltbarkeit: Ids ändern sich nicht, ein Treffer bleibt 180 Tage stehen. Ein Nichttreffer dagegen
/// veraltet, weil TMDb Titel nachträgt — er gilt 7 Tage. Das ist der Unterschied, der eine Serie nicht
/// für immer „nicht gefunden" sein lässt.
/// </para>
/// </summary>
public static class TmdbAnswerStore
{
    /// <summary>Shelf life of an answer that names an id.</summary>
    private static readonly TimeSpan HitLifetime = TimeSpan.FromDays(180);

    /// <summary>Shelf life of an answer that found nothing — TMDb adds titles over time.</summary>
    private static readonly TimeSpan MissLifetime = TimeSpan.FromDays(7);

    private static readonly object Gate = new();

    private static SubdlSqliteContext? _db;

    /// <summary>
    /// Attaches the store. Called once when the plugin builds its data context; without it the resolver
    /// keeps working exactly as before, memory only.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    public static void Attach(SubdlSqliteContext? db)
    {
        lock (Gate)
        {
            _db = db;
        }

        Prune();
    }

    /// <summary>
    /// Reads an answer. Unknown or expired keys answer <c>false</c> and are forgotten, so the caller asks
    /// TMDb again.
    /// </summary>
    /// <param name="key">Question key <c>(text, isSeries)</c> of the resolver.</param>
    /// <param name="value">The stored answer.</param>
    /// <returns>True when a fresh answer exists.</returns>
    public static bool TryGet((string, bool) key, out string? value)
    {
        value = null;
        SubdlSqliteContext? db = Store();

        if (db == null)
        {
            return false;
        }

        string id = KeyOf(key);
        try
        {
            lock (Gate)
            {
                var row = db.TmdbAnswerRows.AsNoTracking().FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    return false;
                }

                if (IsStale(row.Value, row.Written))
                {
                    return false;
                }

                // Leer heisst "gefragt und nichts gefunden" — im Speicher ist das null, hier ist es
                // die leere Zeichenkette. Der Aufrufer bekommt dieselbe Antwort wie vorher.
                value = row.Value.Length == 0 ? null : row.Value;
                return true;
            }
        }
        catch (Exception)
        {
            // A cache that cannot answer must never stop a lookup.
            return false;
        }
    }

    /// <summary>
    /// Writes an answer. A write failure is swallowed: the memory tier still holds it for this process.
    /// </summary>
    /// <param name="key">Question key <c>(text, isSeries)</c> of the resolver.</param>
    /// <param name="value">The answer, <c>null</c> for "asked and found nothing".</param>
    public static void Put((string, bool) key, string? value)
    {
        SubdlSqliteContext? db = Store();

        if (db == null)
        {
            return;
        }

        string id = KeyOf(key);
        string stored = value ?? string.Empty;
        try
        {
            lock (Gate)
            {
                var row = db.TmdbAnswerRows.FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    row = new TmdbAnswerEntity { Id = id, Value = stored, Written = DateTime.UtcNow };
                    db.TmdbAnswerRows.Add(row);
                }
                else
                {
                    row.Value = stored;
                    row.Written = DateTime.UtcNow;
                }

                db.SaveChanges();
            }
        }
        catch (Exception)
        {
            // see above — the run must not fail on a cache write
        }
    }

    /// <summary>
    /// Deletes rows that can no longer be used by either shelf life. Called on attach; cheap and guarded.
    /// </summary>
    public static void Prune()
    {
        SubdlSqliteContext? db = Store();

        if (db == null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                DateTime hitCut = DateTime.UtcNow - HitLifetime;
                DateTime missCut = DateTime.UtcNow - MissLifetime;
                var alte = db.TmdbAnswerRows.Where(r => r.Written < missCut).ToList();
                bool changed = false;
                foreach (var row in alte)
                {
                    if (IsStale(row.Value, row.Written))
                    {
                        db.TmdbAnswerRows.Remove(row);
                        changed = true;
                    }
                }

                _ = hitCut;
                if (changed)
                {
                    db.SaveChanges();
                }
            }
        }
        catch (Exception)
        {
            // pruning is housekeeping
        }
    }

    /// <summary>
    /// F-M545: der angehaengte Kontext, notfalls nachgeholt. Am 16.11.2026 fehlte der Anhang im
    /// normalen Startpfad (nur <c>RecreateDbContext</c> haengte an) — der Store war die ganze Zeit
    /// leer, waehrend 42 von 42 TMDb-Fragen eines Zyklus Wiederholungen waren.
    /// </summary>
    /// <returns>The context, or null when there is none.</returns>
    private static SubdlSqliteContext? Store()
    {
        lock (Gate)
        {
            if (_db == null)
            {
                try
                {
                    _db = Plugin.Instance?.SharedDbContext?.Context;
                }
                catch (Exception)
                {
                    // kein Plugin-Kontext erreichbar: dann bleibt es beim Speicher
                }
            }

            return _db;
        }
    }

    /// <summary>
    /// The store key: the resolver's question plus whether the answer was asked for a series. Kept as a
    /// readable string so the table can be inspected in a plain SQLite viewer.
    /// </summary>
    /// <param name="key">Question key of the resolver.</param>
    /// <returns>Primary key.</returns>
    private static string KeyOf((string, bool) key) => string.Concat(key.Item1, key.Item2 ? "|S" : "|M");

    /// <summary>
    /// "Found nothing" answers expire after seven days, answers with an id after 180.
    /// </summary>
    /// <param name="value">Stored answer.</param>
    /// <param name="written">When it was written (UTC).</param>
    /// <returns>True when the row is too old to be used.</returns>
    private static bool IsStale(string? value, DateTime written)
    {
        var age = DateTime.UtcNow - written;
        return IsNegative(value) ? age > MissLifetime : age > HitLifetime;
    }

    /// <summary>
    /// True when the answer carries no id at all. The resolver writes <c>imdb|tmdb|found|series</c>,
    /// <c>imdb</c>, <c>tmdb</c> or <c>1</c>/<c>0</c> — all of them are checked here by shape.
    /// </summary>
    /// <param name="value">Stored answer.</param>
    /// <returns>True when no id came back.</returns>
    private static bool IsNegative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string[] parts = value.Split('|');
        return parts.All(part => part.Length == 0
            || part == "0"
            || part.Equals("false", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// F-M542: one TMDb answer, exactly as the resolver keeps it in memory.
/// </summary>
public class TmdbAnswerEntity
{
    /// <summary>
    /// Primary key: the resolver's question plus the series flag — e.g.
    /// <c>multi:CUNK ON CINEMA|2022|loose|S</c>, <c>imdb:tt1234567|S</c>.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The answer, unchanged from the memory dictionary: <c>imdb|tmdb|found|isSeries</c> for a title
    /// search, the bare id for a conversion, <c>1</c>/<c>0</c> for an existence test. Never a subtitle,
    /// never a title of ours.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>When the answer was written (UTC) — the age decides whether it is still used.</summary>
    public DateTime Written { get; set; } = DateTime.UtcNow;
}
