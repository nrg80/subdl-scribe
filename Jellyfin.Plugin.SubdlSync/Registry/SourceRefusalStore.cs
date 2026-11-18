using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.SubScribe.Data;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SubScribe.Registry;

/// <summary>
/// F-M605 (Operator-Freigabe 18.11.2026: „Ok", nach der Frage „Ja aber legen wir nicht auch bei tvst neu
/// an?"): das **Nein einer Quelle** wird mit der Signatur der Spur gemerkt, damit dieselbe Spur nicht in
/// jedem Lauf erneut gefragt wird.
/// <para>
/// Anlass: F-M603 schloss nur den Fall „angelegt, Kennung nicht lesbar". Jeder andere Zustand einer
/// Adresse — bei tvsubtitles <c>would-create</c>, <c>create-already-tried</c>, <c>refused</c>,
/// <c>unclear</c>, <c>no-title</c>, <c>no-year</c>, bei moviesubtitles die entsprechenden Film-Zustaende —
/// endete weiterhin als SKIP ohne Marke. Damit blieb das Paar offen, die Datei kehrte in jeden Lauf
/// zurueck, wurde jedes Mal entpackt und endete mit 0 Uploads (gemessen an „The End Of Oak Street":
/// 209 offen-Zeilen, 176 SKIPs, 0 Uploads, ~20 s je Lauf).
/// </para>
/// <para>
/// Gemerkt wird nur ein **inhaltliches** Nein (<see cref="ShouldRemember"/>): „die Quelle kennt den Film
/// nicht", „die Seite hat die Serie abgelehnt", „kein TMDb-Eintrag". Ein voruebergehendes Nein — Rate
/// Limit, Zeitueberschreitung, HTTP 5xx, Abbruch des Laufs — wird NICHT gemerkt, das muss beim naechsten
/// Lauf wieder gefragt werden duerfen.
/// </para>
/// <para>
/// Schluessel: <c>mediaHash|subPos|langTag|quelle</c>. Signatur: der Inhalts-Hash des Untertitels. Ein
/// neuer Text (andere Datei, korrigierte Fassung) traegt eine andere Signatur, dann wird wieder gefragt;
/// ebenso bei einem neu gerippten Medium, weil dann der <c>mediaHash</c> ein anderer ist.
/// </para>
/// </summary>
public static class SourceRefusalStore
{
    private static readonly object Gate = new();

    private static ScribeSqliteContext? _db;

    /// <summary>
    /// F-M609 (Operator-Anweisung 18.11.2026): die Whitelist ist WEG. Gemerkt wird jedes
    /// <see cref="UploadVerdict.Refused"/> — also jede Entscheidung, egal ob die Seite sie getroffen hat
    /// oder eine unserer Vorpruefungen. Die frueheren Praefixe (`film-source-*`, `second-source-show-*`,
    /// „kein TMDb-Eintrag") waren nur eine Naeherung an dieselbe Aussage: damals musste man jeden neuen
    /// Grund von Hand eintragen, und „season must be 1..22" fiel durch — die Staffel kostete deshalb in
    /// JEDEM Lauf vier Fehlschlaege samt ffmpeg-Extraktion (gemessen 18.11.2026, Grey's Anatomy S23).
    /// Ausgenommen bleiben allein die Stoerungen, und die sind jetzt am Urteil erkennbar
    /// (<see cref="UploadVerdict.NoVerdict"/>), nicht am Wortlaut.
    /// </summary>
    private static readonly string[] Unused_prefixes_removed_F_M609 =
    {
        "film-source-",
        "second-source-show-",
        "second-source-no-tmdb-id",
        "second-source-no-series-record",
        "second-source-title-closed",
    };

    /// <summary>
    /// Attaches the store. Called once when the plugin builds its data context; without it every source
    /// is asked again on every run, exactly as before.
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
    /// Answers whether a reason is a substantive refusal (worth remembering) or a transient one.
    /// </summary>
    /// <param name="reason">The reason a source gave.</param>
    /// <returns>True when the refusal should be remembered.</returns>
    public static bool ShouldRemember(string? reason, Api.UploadVerdict verdict)
    {
        // F-M609: das Urteil entscheidet, nicht der Wortlaut. Ein „Nein" ohne Begruendung ist immer
        // noch ein Nein; eine Stoerung wird nie gemerkt, auch wenn sie wie eine Ablehnung klingt.
        _ = reason;
        return verdict == Api.UploadVerdict.Refused;
    }

    /// <summary>
    /// Builds the key of one refusal.
    /// </summary>
    /// <param name="mediaHash">Hash of the media file.</param>
    /// <param name="subPos">Stream position of the track on an embedded subtitle, -1 for a loose file.</param>
    /// <param name="langTag">Language tag (with the HI marker, as the plugin writes it).</param>
    /// <param name="source">The source that said no.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string mediaHash, int subPos, string langTag, string source)
        => (mediaHash ?? string.Empty).Trim().ToLowerInvariant()
           + "|" + subPos.ToString(CultureInfo.InvariantCulture)
           + "|" + (langTag ?? string.Empty).Trim().ToLowerInvariant()
           + "|" + (source ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Reads a remembered refusal, but only when it belongs to exactly this content.
    /// </summary>
    /// <param name="mediaHash">Hash of the media file.</param>
    /// <param name="subPos">Stream position, -1 for a loose file.</param>
    /// <param name="langTag">Language tag.</param>
    /// <param name="source">The source.</param>
    /// <param name="signature">Content hash of the subtitle right now.</param>
    /// <param name="reason">The remembered reason.</param>
    /// <returns>True when this exact content was already refused.</returns>
    public static bool StillRefused(
        string mediaHash, int subPos, string langTag, string source, string signature, out string reason)
    {
        reason = string.Empty;
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(mediaHash) || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        string id = KeyOf(mediaHash, subPos, langTag, source);
        try
        {
            lock (Gate)
            {
                var row = db.SourceRefusalRows.AsNoTracking().FirstOrDefault(r => r.Id == id);
                if (row == null || !string.Equals(row.Signature, signature, StringComparison.Ordinal))
                {
                    return false;
                }

                reason = row.Reason ?? string.Empty;
                return true;
            }
        }
        catch (Exception)
        {
            // Ein Store, der nicht antwortet, darf den Versand nicht anhalten — dann wird eben gefragt.
            return false;
        }
    }

    /// <summary>
    /// Remembers a refusal.
    /// </summary>
    /// <param name="mediaHash">Hash of the media file.</param>
    /// <param name="subPos">Stream position, -1 for a loose file.</param>
    /// <param name="langTag">Language tag.</param>
    /// <param name="source">The source that said no.</param>
    /// <param name="signature">Content hash of the subtitle.</param>
    /// <param name="reason">Why it said no.</param>
    public static void Put(
        string mediaHash, int subPos, string langTag, string source, string signature, string reason,
        Api.UploadVerdict verdict = Api.UploadVerdict.Refused)
    {
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(mediaHash) || string.IsNullOrEmpty(signature)
            || !ShouldRemember(reason, verdict))
        {
            return;
        }

        string id = KeyOf(mediaHash, subPos, langTag, source);
        try
        {
            lock (Gate)
            {
                var row = db.SourceRefusalRows.FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    db.SourceRefusalRows.Add(new SourceRefusalEntity
                    {
                        Id = id,
                        Signature = signature,
                        Reason = Trunc(reason, 200),
                        Written = DateTime.UtcNow,
                    });
                }
                else
                {
                    row.Signature = signature;
                    row.Reason = Trunc(reason, 200);
                    row.Written = DateTime.UtcNow;
                }

                db.SaveChanges();
            }
        }
        catch (Exception)
        {
            // Ein nicht gespeichertes Nein kostet nur eine Frage im naechsten Lauf — nie diesen Lauf.
        }
    }

    /// <summary>
    /// Forgets a refusal — called when the source finally took the subtitle, so a later, changed file
    /// does not inherit an old judgement.
    /// </summary>
    /// <param name="mediaHash">Hash of the media file.</param>
    /// <param name="subPos">Stream position, -1 for a loose file.</param>
    /// <param name="langTag">Language tag.</param>
    /// <param name="source">The source that took it.</param>
    public static void Forget(string mediaHash, int subPos, string langTag, string source)
    {
        ScribeSqliteContext? db = Store();
        if (db == null || string.IsNullOrWhiteSpace(mediaHash))
        {
            return;
        }

        string id = KeyOf(mediaHash, subPos, langTag, source);
        try
        {
            lock (Gate)
            {
                var row = db.SourceRefusalRows.FirstOrDefault(r => r.Id == id);
                if (row != null)
                {
                    db.SourceRefusalRows.Remove(row);
                    db.SaveChanges();
                }
            }
        }
        catch (Exception)
        {
            // Vergessen ist Hygiene, kein Muss.
        }
    }

    private static string Trunc(string? text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max]);

    private static ScribeSqliteContext? Store()
    {
        lock (Gate)
        {
            return _db;
        }
    }
}

/// <summary>
/// F-M605: one remembered source refusal — which track, which source, which content it was refused for.
/// </summary>
public class SourceRefusalEntity
{
    /// <summary>Primary key: <c>mediaHash|subPos|langTag|source</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Content hash of the subtitle the refusal belongs to.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>The reason the source gave.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>When it was written (UTC).</summary>
    public DateTime Written { get; set; } = DateTime.UtcNow;
}
