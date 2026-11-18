using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.SubScribe.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Registry;

/// <summary>
/// F-M608 (Operator-Anweisung 18.11.2026: „Anlegen, nachsuchen, fail 1: 2 Sekunden pause, fail 2: 4
/// sekunden pause, fail 3: 8 Sekunden pause. Fail4: alles pairs des movies der serie dauerhaft
/// schließen" — und „Bei tvst und mst natürlich nur"): das **Anlegen und Nachsuchen** eines Titels wird
/// gestaffelt wiederholt, und nach dem vierten Fehlschlag ist der Titel **dauerhaft** geschlossen.
/// <para>
/// Warum nur die zwei Adressen mit Anlage: subdl hat keinen solchen Schritt — dort wird ein Untertitel
/// eingereicht und fertig. Bei tvsubtitles und moviesubtitles muss dagegen erst die Serie bzw. der Film
/// angelegt und dann wiedergefunden werden; die Seite braucht dafür manchmal einen Moment, und genau
/// dieser Moment ist der Grund fuer die Pausen 2 s / 4 s / 8 s.
/// </para>
/// <para>
/// Die Sperre haengt am **Titel** (IMDb-Kennung, sonst TMDb-Kennung), nicht am Inhalt: sie schliesst
/// damit alle Paare dieses Films bzw. dieser Serie auf einmal — genau wie angewiesen. Sie faellt nur
/// durch <see cref="Clear"/> weg, also wenn ein Versuch gelingt.
/// </para>
/// </summary>
public static class TitleFailureStore
{
    /// <summary>Number of failed attempts after which a title is closed for good.</summary>
    public const int ClosingAttempt = 4;

    private static readonly object Gate = new();

    private static ScribeSqliteContext? _db;

    /// <summary>
    /// F-M615 (Operator-Anweisung 18.11.2026: „Do it"): ein Protokoll, damit ein stiller Fehlschlag
    /// nicht mehr unsichtbar bleibt. Bis hierher fingen <see cref="IsClosed"/>, <see cref="Close"/>,
    /// <see cref="Note"/> und <see cref="Clear"/> jede Ausnahme ohne Wort ab — die Titelsperre hielt
    /// deshalb in vier Laeufen nicht, und niemand konnte sehen, warum.
    /// </summary>
    private static ILogger? _log;

    /// <summary>Warnung „Store nicht angehaengt" nur einmal je Prozess.</summary>
    private static bool _warnedNoStore;

    /// <summary>
    /// F-M615: haengt das Protokoll an. Ohne Protokoll bleibt das Verhalten wie vorher, nur stumm.
    /// </summary>
    /// <param name="log">Das Protokoll des Plugins.</param>
    public static void AttachLogger(ILogger? log)
    {
        lock (Gate)
        {
            _log = log;
        }
    }

    /// <summary>
    /// Attaches the store. Called once when the plugin builds its data context; without it the ladder
    /// counts within the run only, exactly as the pauses alone would do.
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
    /// Builds the key of one title.
    /// </summary>
    /// <param name="source">The address that has to create titles: <c>tvsubtitles</c> or <c>moviesubtitles</c>.</param>
    /// <param name="titleId">IMDb id (preferred) or TMDb id of the title.</param>
    /// <returns>The key, e.g. <c>moviesubtitles|tt27165187</c>.</returns>
    public static string KeyOf(string source, string titleId)
        => (source ?? string.Empty).Trim().ToLowerInvariant() + "|" + (titleId ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// F-M610 (Operator-Frage 18.11.2026: „Anzeichen dafür das wir zuviel schließen/ uberblocken?"): diese
    /// Gruende sind ein BELEG („die Seite fuehrt den Titel, die Kennung ist nur nicht lesbar") und KEIN
    /// Nein — sie duerfen einen Titel nicht dauerhaft schliessen. Vor F-M610 tat die Leiter genau das:
    /// 12 Titel lagen in der Sperre, obwohl die Seite sie fuehrt (gemessen 18.11.2026: 57 Zeilen
    /// <c>exists-id-not-readable</c>, 29 Zeilen <c>exists-other-year</c>). Eine solche Zeile wird beim
    /// Lesen verworfen statt befolgt — kein Aufraeumlauf noetig.
    /// </summary>
    private static readonly string[] EvidenceReasons =
    {
        "created-id-not-readable",
        "exists-id-not-readable",
        "created-id-unknown",
    };

    /// <summary>
    /// Answers whether a title was closed after the ladder ran out.
    /// </summary>
    /// <param name="source">The address.</param>
    /// <param name="titleId">The title's id.</param>
    /// <param name="reason">Why it was closed.</param>
    /// <returns>True when every pair of this title is closed.</returns>
    public static bool IsClosed(string source, string titleId, out string reason)
    {
        reason = string.Empty;
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(titleId))
        {
            return false;
        }

        string id = KeyOf(source, titleId);
        try
        {
            lock (Gate)
            {
                var row = db.TitleFailureRows.AsNoTracking().FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    _log?.LogInformation("[Sub Scribe] title store: {Key} NOT in the store — the ladder runs again (F-M618)", id);
                    return false;
                }

                if (!row.Closed)
                {
                    _log?.LogInformation("[Sub Scribe] title store: {Key} open, attempts {Attempts} ({Reason}) (F-M618)", id, row.Attempts, row.Reason);
                    return false;
                }

                // F-M610: eine Sperre, die auf einem Beleg beruht, ist keine Sperre. Sie faellt hier weg
                // (auch aus dem Store, damit die Zaehlung nicht weiterlaeuft).
                if (EvidenceReasons.Any(e => string.Equals(e, row.Reason, StringComparison.OrdinalIgnoreCase)))
                {
                    var alt = db.TitleFailureRows.FirstOrDefault(r => r.Id == id);
                    if (alt != null)
                    {
                        db.TitleFailureRows.Remove(alt);
                        db.SaveChanges();
                    }

                    return false;
                }

                _log?.LogInformation(
                    "[Sub Scribe] title store: {Key} is closed ({Reason}, attempts {Attempts}) — the ladder is skipped (F-M615)",
                    id, row.Reason, row.Attempts);
                reason = row.Reason ?? string.Empty;
                return true;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning("[Sub Scribe] title store: reading {Key} failed: {Type}: {Msg} (F-M615)", id, ex.GetType().Name, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Counts one failed attempt.
    /// </summary>
    /// <param name="source">The address.</param>
    /// <param name="titleId">The title's id.</param>
    /// <param name="reason">Why the attempt failed.</param>
    /// <returns>The attempt number, starting at 1.</returns>
    public static int Note(string source, string titleId, string reason)
    {
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(titleId))
        {
            return 1;
        }

        string id = KeyOf(source, titleId);
        try
        {
            lock (Gate)
            {
                var row = db.TitleFailureRows.FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    row = new TitleFailureEntity { Id = id, Attempts = 0, Closed = false };
                    db.TitleFailureRows.Add(row);
                }

                row.Attempts++;
                row.Reason = Trunc(reason, 200);
                row.Written = DateTime.UtcNow;
                db.SaveChanges();
                _log?.LogInformation("[Sub Scribe] title store: {Key} attempt {Attempts} written ({Reason}) (F-M615)", id, row.Attempts, row.Reason);
                return row.Attempts;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning("[Sub Scribe] title store: writing {Key} failed: {Type}: {Msg} (F-M615)", id, ex.GetType().Name, ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Closes every pair of this title for good.
    /// </summary>
    /// <param name="source">The address.</param>
    /// <param name="titleId">The title's id.</param>
    /// <param name="reason">Why — kept for the log line.</param>
    public static void Close(string source, string titleId, string reason)
    {
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(titleId))
        {
            return;
        }

        string id = KeyOf(source, titleId);
        try
        {
            lock (Gate)
            {
                var row = db.TitleFailureRows.FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    row = new TitleFailureEntity { Id = id };
                    db.TitleFailureRows.Add(row);
                }

                row.Closed = true;
                row.Attempts = ClosingAttempt;
                row.Reason = Trunc(reason, 200);
                row.Written = DateTime.UtcNow;
                db.SaveChanges();
                _log?.LogInformation("[Sub Scribe] title store: {Key} closed and written ({Reason}) (F-M615)", id, reason);
            }
        }
        catch (Exception ex)
        {
            // Eine Sperre, die nicht gespeichert werden kann, kostet nur einen weiteren Versuch — aber
            // jetzt steht im Protokoll, dass es passiert ist (F-M615).
            _log?.LogWarning("[Sub Scribe] title store: closing {Key} failed: {Type}: {Msg} (F-M615)", id, ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>
    /// Forgets the failures of a title — called when an attempt finally succeeded.
    /// </summary>
    /// <param name="source">The address.</param>
    /// <param name="titleId">The title's id.</param>
    public static void Clear(string source, string titleId)
    {
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(titleId))
        {
            return;
        }

        string id = KeyOf(source, titleId);
        try
        {
            lock (Gate)
            {
                var row = db.TitleFailureRows.FirstOrDefault(r => r.Id == id);
                if (row != null && (row.Attempts > 0 || row.Closed))
                {
                    db.TitleFailureRows.Remove(row);
                    db.SaveChanges();
                }
            }
        }
        catch (Exception)
        {
            // Hygiene, kein Muss.
        }
    }

    private static string Trunc(string? text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max]);

    private static ScribeSqliteContext? Store()
    {
        lock (Gate)
        {
            // F-M617: erst selbst helfen (wie der TMDb-Store seit F-M545), dann warnen.
            if (_db == null)
            {
                try
                {
                    _db = Plugin.Instance?.SharedDbContext?.Context;
                }
                catch (Exception)
                {
                    // Kein Plugin-Kontext erreichbar.
                }
            }

            if (_db == null && !_warnedNoStore)
            {
                _warnedNoStore = true;
                _log?.LogWarning(
                    "[Sub Scribe] title store: NOT attached — the ladder can only count within the run, nothing is written (F-M615)");
            }

            return _db;
        }
    }
}

/// <summary>
/// F-M608: how often creating and finding a title failed, and whether it is closed for good.
/// </summary>
public class TitleFailureEntity
{
    /// <summary>Primary key: <c>source|titleId</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Failed attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>True when every pair of this title is closed.</summary>
    public bool Closed { get; set; }

    /// <summary>The last reason, short enough for one log line.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>When it was last touched (UTC).</summary>
    public DateTime Written { get; set; } = DateTime.UtcNow;
}
