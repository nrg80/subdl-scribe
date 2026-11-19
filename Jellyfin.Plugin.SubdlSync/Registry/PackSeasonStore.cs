using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.SubScribe.Data;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SubScribe.Registry;

/// <summary>
/// F-M604 (Operator-Anweisung 18.11.2026: „Gespeicherte fassung mit Signatur. Bei neuer Signatur versuch
/// einer neuen packbildung." und die Frage davor: „was ist wenn die Packmitglieder sich geändert haben?
/// … eine Prüfsumme über die filegroßen und file speicherdaten … oder gibt es einfachere/effizientere
/// wege? Speicherdatum des letzten packmitglieds z.b?"): das Urteil „für diese Staffel lässt sich kein
/// Pack bilden" liegt im Store — mit einer **Signatur** der Mitglieder, damit es nur so lange gilt, wie
/// die Lage dieselbe ist.
/// <para>
/// Warum eine Signatur und keine Prüfsumme über die Daten: die Frage „haben sich die Packmitglieder
/// geändert" beantworten Größe und Speicherdatum der Datei (ein <c>stat</c>, kein Lesen). Wird eine
/// Folge neu gerippt, ändert sich die Größe oder der Zeitstempel; kommt eine Folge dazu, ändert sich die
/// Liste. Beides steht in der Signatur, beides kostet keinen Lesevorgang. Der OSHash im Store wäre die
/// exakte Identität, verlangt aber die Datei zu lesen — für ein „hat sich was geändert" ist das zu teuer.
/// </para>
/// <para>
/// Was in die Signatur eingeht: die gelaufenen Folgennummern (die Staffel wächst) und je Folgendatei
/// Folgennummer, Größe und Speicherdatum (UTC-Ticks). Was NICHT eingeht: Spuren, Hashes, Untertitel —
/// die ändern sich mit einer neuen Datei, und dann ändert sich auch die Signatur.
/// </para>
/// <para>
/// Urteile: <c>no-pack</c> (nichts zu bilden, Grund gespeichert) und <c>pack</c> (ein Pack war möglich;
/// die Zeile hält nur fest, dass für diese Signatur schon geprüft wurde). Nur <c>no-pack</c> überspringt
/// die Arbeit — bei <c>pack</c> läuft die Planung normal weiter, weil das Pack selbst noch fehlen kann.
/// </para>
/// </summary>
public static class PackSeasonStore
{
    /// <summary>Season-level verdict (no language): the release groups do not cover the season.</summary>
    public const string SeasonTag = "";

    /// <summary>No pack can be formed for this season and signature.</summary>
    public const string NoPack = "no-pack";

    /// <summary>A pack was possible for this season and signature.</summary>
    public const string PackPossible = "pack";

    private static readonly object Gate = new();

    private static ScribeSqliteContext? _db;

    /// <summary>
    /// Attaches the store. Called once when the plugin builds its data context; without it the pack
    /// planning judges every season on every run, exactly as before.
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
    /// Builds the key of one verdict.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="langTag">Language tag, or <see cref="SeasonTag"/> for a season-level verdict.</param>
    /// <returns>The key, e.g. <c>tt1234567|3|de</c>.</returns>
    public static string KeyOf(string imdbId, int season, string langTag)
        => imdbId.Trim().ToLowerInvariant() + "|" + season.ToString(CultureInfo.InvariantCulture)
           + "|" + (langTag ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Builds the signature of the pack members.
    /// </summary>
    /// <param name="aired">The episode numbers that aired.</param>
    /// <param name="files">The episode files on disk: episode number, size in bytes, storage date.</param>
    /// <returns>A compact, stable signature — same members, same string.</returns>
    public static string SignatureOf(IEnumerable<int> aired, IEnumerable<(int Episode, long Size, DateTime Written)> files)
    {
        string gelaufen = string.Join(
            ",",
            (aired ?? Array.Empty<int>()).Distinct().OrderBy(n => n).Select(n => n.ToString(CultureInfo.InvariantCulture)));

        string mitglieder = string.Join(
            ";",
            (files ?? Array.Empty<(int Episode, long Size, DateTime Written)>())
                .GroupBy(f => f.Episode)
                .Select(g => g.OrderByDescending(f => f.Written).First())
                .OrderBy(f => f.Episode)
                .Select(f => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1}:{2}",
                    f.Episode,
                    f.Size,
                    f.Written.ToUniversalTime().Ticks)));

        return "aired=" + gelaufen + "|files=" + mitglieder;
    }

    /// <summary>
    /// Reads the stored verdict of one key.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="langTag">Language tag, or <see cref="SeasonTag"/>.</param>
    /// <param name="signature">The signature the verdict was made for.</param>
    /// <param name="verdict">The verdict itself.</param>
    /// <param name="reason">Why the verdict was reached.</param>
    /// <returns>True when a verdict exists.</returns>
    public static bool TryGet(
        string imdbId, int season, string langTag, out string signature, out string verdict, out string reason)
    {
        signature = string.Empty;
        verdict = string.Empty;
        reason = string.Empty;
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(imdbId) || season < 1)
        {
            return false;
        }

        string id = KeyOf(imdbId, season, langTag);
        try
        {
            lock (Gate)
            {
                var row = db.PackSeasonRows.AsNoTracking().FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    return false;
                }

                signature = row.Signature ?? string.Empty;
                verdict = row.Verdict ?? string.Empty;
                reason = row.Reason ?? string.Empty;
                return true;
            }
        }
        catch (Exception)
        {
            // Ein Store, der nicht antwortet, darf die Planung nicht anhalten — dann wird eben geprüft.
            return false;
        }
    }

    /// <summary>
    /// Answers the one question the planning asks: „darf diese Staffel (bzw. dieses Paar) übersprungen
    /// werden, weil sich seit dem letzten Urteil nichts geändert hat?"
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="langTag">Language tag, or <see cref="SeasonTag"/>.</param>
    /// <param name="currentSignature">The signature of the members right now.</param>
    /// <param name="reason">Why it was judged impossible back then.</param>
    /// <returns>True when the stored verdict still applies.</returns>
    public static bool StillImpossible(string imdbId, int season, string langTag, string currentSignature, out string reason)
    {
        reason = string.Empty;
        if (!TryGet(imdbId, season, langTag, out string signature, out string verdict, out string gespeichert))
        {
            return false;
        }

        if (!string.Equals(verdict, NoPack, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(signature, currentSignature, StringComparison.Ordinal))
        {
            // F-M604: die Mitglieder haben sich geändert — neuer Versuch, ein Pack zu bilden.
            return false;
        }

        reason = gespeichert;
        return true;
    }

    /// <summary>
    /// Stores a verdict together with the signature it belongs to.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="langTag">Language tag, or <see cref="SeasonTag"/>.</param>
    /// <param name="signature">The signature of the members.</param>
    /// <param name="verdict"><see cref="NoPack"/> or <see cref="PackPossible"/>.</param>
    /// <param name="reason">Why — written into the log line when a season is skipped.</param>
    public static void Put(string imdbId, int season, string langTag, string signature, string verdict, string reason)
    {
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(imdbId) || season < 1 || string.IsNullOrEmpty(signature))
        {
            return;
        }

        string id = KeyOf(imdbId, season, langTag);
        try
        {
            lock (Gate)
            {
                var row = db.PackSeasonRows.FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    db.PackSeasonRows.Add(new PackSeasonEntity
                    {
                        Id = id,
                        Signature = signature,
                        Verdict = verdict ?? string.Empty,
                        Reason = Trunc(reason, 300),
                        Written = DateTime.UtcNow,
                    });
                }
                else
                {
                    row.Signature = signature;
                    row.Verdict = verdict ?? string.Empty;
                    row.Reason = Trunc(reason, 300);
                    row.Written = DateTime.UtcNow;
                }

                db.SaveChanges();
            }
        }
        catch (Exception)
        {
            // Ein Urteil, das nicht gespeichert werden kann, kostet nur den nächsten Lauf — nie den Lauf.
        }
    }

    private static string Trunc(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= max ? text : text[..max];
    }

    /// <summary>
    /// F-M617: holt sich einen fehlenden Kontext SELBST, statt still im Speicher zu arbeiten — dieselbe
    /// Selbsthilfe, die der TMDb-Store seit F-M545 hat. Der Anhang über
    /// <c>Plugin.AttachStores</c> bleibt der regulaere Weg; das hier ist das Netz darunter.
    /// </summary>
    /// <returns>Der Datenkontext, oder null, wenn es keinen gibt.</returns>
    private static ScribeSqliteContext? Store()
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
                    // Kein Plugin-Kontext erreichbar: dann bleibt es beim Speicher.
                }
            }

            return _db;
        }
    }
}

/// <summary>
/// F-M604: one stored pack verdict — season (or one language of it), the signature it was made for, and
/// why. The signature is what keeps an old verdict from suppressing a new attempt.
/// </summary>
public class PackSeasonEntity
{
    /// <summary>Primary key: <c>imdbId|season|langTag</c> (empty language = season-level verdict).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The signature of the members this verdict was made for.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary><c>no-pack</c> or <c>pack</c>.</summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>The reason, short enough for one log line.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>When the verdict was written (UTC).</summary>
    public DateTime Written { get; set; } = DateTime.UtcNow;
}
