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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// F-M628/F-M631: ein Beleg — und zwar genau einer je Paar aus Spur und Quelle.
/// <para>
/// WARUM DIESE TABELLE: die Quellenbelege lagen als feste Spaltenpaare am Datensatz
/// (<c>SubdlUploadId</c>, <c>TvSubtitlesUploadId</c>, <c>MoviesubtitlesUploadId</c> samt Zeitpunkten und
/// Abschluss-Stempeln, vierzehn Spalten). Eine vierte Quelle hätte das Schema geändert und jeden
/// Lesepfad mitgezogen. Hier ist eine Quelle ein WERT im Feld <see cref="Source"/>, kein Feld —
/// N Quellen erzeugen N Zeilen, nicht N Spalten (F-M632).
/// </para>
/// <para>
/// WAS „KEIN BELEG" HEISST: fehlt die Zeile, ist die Spur für diese Quelle offen. Es gibt kein Feld
/// „gehalten von" (F-M631) — eine Spur darf für eine Quelle belegt und für eine andere offen sein,
/// und genau das ist der Normalfall.
/// </para>
/// </summary>
public sealed class SourceMarkEntity
{
    /// <summary>Schlüssel der Zeile: <c>OwnerKey|Source</c>. Ein Beleg ist eindeutig adressierbar.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Der Schlüssel des Datensatzes, an dem der Beleg hängt (Spur, Sidecar oder Datei).</summary>
    public string OwnerKey { get; set; } = string.Empty;

    /// <summary>Die Quelle, für die dieser Beleg gilt — ein Wert, kein Spaltenname.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Kennung des Abrufs bei dieser Quelle, sofern die Quelle sie vergibt.</summary>
    public string? DownloadId { get; set; }

    /// <summary>Kennung der Einsendung bei dieser Quelle, sofern die Quelle sie vergibt.</summary>
    public string? UploadId { get; set; }

    /// <summary>Zeitpunkt der Einsendung bei dieser Quelle.</summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>Zeitpunkt, zu dem diese Quelle die Datei als abgeschlossen gemeldet hat.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Wann die Zeile zuletzt geschrieben wurde.</summary>
    public DateTime Written { get; set; }
}

/// <summary>
/// Zugriff auf die Belege je Quelle (F-M628). Der Ablauf fragt hier nach Quellen und nennt nie eine
/// quellenspezifische Spalte.
/// <para>
/// Angehängt wird der Store einmal beim Aufbau des Datenkontexts (<see cref="Attach"/>); ohne ihn
/// antwortet jede Abfrage „kein Beleg", und der Ablauf verhält sich wie vor der Einführung — offen
/// statt stillschweigend fertig.
/// </para>
/// </summary>
public static class SourceMarkStore
{
    private static readonly object Gate = new();

    private static ScribeSqliteContext? _db;

    /// <summary>Attaches the store.</summary>
    /// <param name="db">The plugin's data context.</param>
    public static void Attach(ScribeSqliteContext? db)
    {
        lock (Gate)
        {
            _db = db;
        }
    }

    /// <summary>
    /// Legt die Tabelle an, falls sie fehlt. Additiv, damit ein Bestand weiterlesbar bleibt.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="logger">Optional logger.</param>
    public static void EnsureTable(ScribeSqliteContext db, ILogger? logger = null)
    {
        try
        {
            db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS source_marks ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "OwnerKey TEXT NOT NULL DEFAULT '', "
                + "Source TEXT NOT NULL DEFAULT '', "
                + "DownloadId TEXT NULL, "
                + "UploadId TEXT NULL, "
                + "UploadedAt TEXT NULL, "
                + "CompletedAt TEXT NULL, "
                + "Written TEXT NOT NULL DEFAULT '');");
            db.Database.ExecuteSqlRaw(
                "CREATE UNIQUE INDEX IF NOT EXISTS ix_source_marks_owner_source "
                + "ON source_marks (OwnerKey, Source);");
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[Sub Scribe] could not create the source_marks table (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>Zeilen-Schlüssel eines Belegs.</summary>
    /// <param name="ownerKey">Key of the record the mark belongs to.</param>
    /// <param name="source">The source.</param>
    /// <returns><c>OwnerKey|Source</c>.</returns>
    public static string KeyOf(string ownerKey, string source)
        => string.Concat(ownerKey, "|", source);

    /// <summary>Der Beleg einer Quelle für einen Datensatz, oder null.</summary>
    /// <param name="ownerKey">Key of the record.</param>
    /// <param name="source">The source.</param>
    /// <returns>The mark, or null when this source holds nothing for this record.</returns>
    public static SourceMarkEntity? Get(string ownerKey, string source)
    {
        ScribeSqliteContext? db = _db;
        if (db == null || string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(source))
        {
            return null;
        }

        string id = KeyOf(ownerKey, source);
        try
        {
            lock (Gate)
            {
                return db.SourceMarkRows.AsNoTracking().FirstOrDefault(r => r.Id == id);
            }
        }
        catch (Exception)
        {
            // Ein Store, der nicht antwortet, heißt „kein Beleg" — offen, nicht fertig.
            return null;
        }
    }

    /// <summary>Alle Belege eines Datensatzes, eine Zeile je Quelle (F-M632).</summary>
    /// <param name="ownerKey">Key of the record.</param>
    /// <returns>The marks, empty when none.</returns>
    public static IReadOnlyList<SourceMarkEntity> ForOwner(string ownerKey)
    {
        ScribeSqliteContext? db = _db;
        if (db == null || string.IsNullOrEmpty(ownerKey))
        {
            return Array.Empty<SourceMarkEntity>();
        }

        try
        {
            lock (Gate)
            {
                return db.SourceMarkRows.AsNoTracking().Where(r => r.OwnerKey == ownerKey).ToList();
            }
        }
        catch (Exception)
        {
            return Array.Empty<SourceMarkEntity>();
        }
    }

    /// <summary>
    /// Schreibt den Beleg EINER Quelle und lässt die Belege anderer Quellen unberührt (F-M634).
    /// </summary>
    /// <param name="ownerKey">Key of the record.</param>
    /// <param name="source">The source the mark belongs to.</param>
    /// <param name="downloadId">Fetch id at that source, or null.</param>
    /// <param name="uploadId">Submission id at that source, or null.</param>
    /// <param name="uploadedAt">When the submission happened, or null.</param>
    /// <param name="completedAt">When that source reported the file done, or null.</param>
    public static void Set(
        string ownerKey,
        string source,
        string? downloadId = null,
        string? uploadId = null,
        DateTime? uploadedAt = null,
        DateTime? completedAt = null)
    {
        ScribeSqliteContext? db = _db;
        if (db == null || string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(source))
        {
            return;
        }

        string id = KeyOf(ownerKey, source);
        try
        {
            lock (Gate)
            {
                var row = db.SourceMarkRows.FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    row = new SourceMarkEntity { Id = id, OwnerKey = ownerKey, Source = source };
                    db.SourceMarkRows.Add(row);
                }

                if (downloadId != null)
                {
                    row.DownloadId = downloadId;
                }

                if (uploadId != null)
                {
                    row.UploadId = uploadId;
                }

                if (uploadedAt != null)
                {
                    row.UploadedAt = uploadedAt;
                }

                if (completedAt != null)
                {
                    row.CompletedAt = completedAt;
                }

                row.Written = DateTime.UtcNow;
                db.SaveChanges();
            }
        }
        catch (Exception)
        {
            // Ein Schreibfehler darf den Lauf nicht anhalten; der nächste Lauf schreibt erneut.
        }
    }

    /// <summary>
    /// Zählt die Belege je Quelle. Die Zahl ist die Prüfgrundlage beim Formatwechsel (F-M637).
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <returns>Source → number of marks.</returns>
    public static Dictionary<string, int> CountBySource(ScribeSqliteContext db)
    {
        var zaehler = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gruppe in db.SourceMarkRows.AsNoTracking().GroupBy(r => r.Source).Select(g => new { Source = g.Key, N = g.Count() }).ToList())
        {
            zaehler[gruppe.Source] = gruppe.N;
        }

        return zaehler;
    }

    /// <summary>
    /// Zählt, wie viele Datensätze die festen Spalten JE QUELLE tragen — die Gegenzahl zur Prüfung.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <returns>Source → number of records whose columns hold something.</returns>
    public static Dictionary<string, int> CountColumnsBySource(ScribeSqliteContext db)
    {
        // Die Quellenbelege (Abruf, Einsendung, Zeitpunkt) stehen an den SPUR-Zeilen — eingebettete Spur
        // und Sidecar; die Abschluss-Stempel stehen an der DATEI-Zeile. Beides wird je Quelle gezählt,
        // weil beides in derselben Belegtabelle landet.
        var spuren = db.EmbedRows.AsNoTracking().Select(m => new
            {
                m.Id, m.SubdlDownloadId, m.SubdlUploadId, m.SubdlUploadedAt,
                m.TvSubtitlesDownloadId, m.TvSubtitlesUploadId, m.TvSubtitlesUploadedAt,
                m.MoviesubtitlesDownloadId, m.MoviesubtitlesUploadId, m.MoviesubtitlesUploadedAt,
            })
            .ToList()
            .Select(m => new SpurBeleg(m.Id, m.SubdlDownloadId, m.SubdlUploadId, m.SubdlUploadedAt,
                m.TvSubtitlesDownloadId, m.TvSubtitlesUploadId, m.TvSubtitlesUploadedAt,
                m.MoviesubtitlesDownloadId, m.MoviesubtitlesUploadId, m.MoviesubtitlesUploadedAt))
            .Concat(db.SidecarRows.AsNoTracking().Select(m => new
            {
                m.Id, m.SubdlDownloadId, m.SubdlUploadId, m.SubdlUploadedAt,
                m.TvSubtitlesDownloadId, m.TvSubtitlesUploadId, m.TvSubtitlesUploadedAt,
                m.MoviesubtitlesDownloadId, m.MoviesubtitlesUploadId, m.MoviesubtitlesUploadedAt,
            })
            .ToList()
            .Select(m => new SpurBeleg(m.Id, m.SubdlDownloadId, m.SubdlUploadId, m.SubdlUploadedAt,
                m.TvSubtitlesDownloadId, m.TvSubtitlesUploadId, m.TvSubtitlesUploadedAt,
                m.MoviesubtitlesDownloadId, m.MoviesubtitlesUploadId, m.MoviesubtitlesUploadedAt)))
            .ToList();

        var dateien = db.MediaRows.AsNoTracking()
            .Select(m => new { m.CompleteSubdl, m.CompleteTvSubtitles, m.CompleteMsub })
            .ToList();

        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["subdl"] = spuren.Count(s => !string.IsNullOrEmpty(s.SubdlDownloadId) || !string.IsNullOrEmpty(s.SubdlUploadId) || s.SubdlUploadedAt != null)
                        + dateien.Count(d => d.CompleteSubdl != null),
            ["tvsubtitles"] = spuren.Count(s => !string.IsNullOrEmpty(s.TvSubtitlesDownloadId) || !string.IsNullOrEmpty(s.TvSubtitlesUploadId) || s.TvSubtitlesUploadedAt != null)
                              + dateien.Count(d => d.CompleteTvSubtitles != null),
            ["moviesubtitles"] = spuren.Count(s => !string.IsNullOrEmpty(s.MoviesubtitlesDownloadId) || !string.IsNullOrEmpty(s.MoviesubtitlesUploadId) || s.MoviesubtitlesUploadedAt != null)
                                 + dateien.Count(d => d.CompleteMsub != null),
        };
    }

    /// <summary>
    /// Füllt die Belegtabelle aus den festen Spalten eines Bestands (F-M637: einmalig, vor dem
    /// Umschalten). Eine Quelle, die in den Spalten nichts hat, bekommt keine Zeile — fehlender Beleg
    /// heißt offen.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <returns>Source → number of marks written.</returns>
    public static Dictionary<string, int> BackfillFromColumns(ScribeSqliteContext db)
    {
        var zaehler = new Dictionary<string, int>(StringComparer.Ordinal);
        var jetzt = DateTime.UtcNow;

        foreach (var spur in db.EmbedRows.AsNoTracking().ToList())
        {
            Schreib(db, zaehler, jetzt, spur.Id, "subdl", spur.SubdlDownloadId, spur.SubdlUploadId, spur.SubdlUploadedAt, null);
            Schreib(db, zaehler, jetzt, spur.Id, "tvsubtitles", spur.TvSubtitlesDownloadId, spur.TvSubtitlesUploadId, spur.TvSubtitlesUploadedAt, null);
            Schreib(db, zaehler, jetzt, spur.Id, "moviesubtitles", spur.MoviesubtitlesDownloadId, spur.MoviesubtitlesUploadId, spur.MoviesubtitlesUploadedAt, null);
        }

        foreach (var sidecar in db.SidecarRows.AsNoTracking().ToList())
        {
            Schreib(db, zaehler, jetzt, sidecar.Id, "subdl", sidecar.SubdlDownloadId, sidecar.SubdlUploadId, sidecar.SubdlUploadedAt, null);
            Schreib(db, zaehler, jetzt, sidecar.Id, "tvsubtitles", sidecar.TvSubtitlesDownloadId, sidecar.TvSubtitlesUploadId, sidecar.TvSubtitlesUploadedAt, null);
            Schreib(db, zaehler, jetzt, sidecar.Id, "moviesubtitles", sidecar.MoviesubtitlesDownloadId, sidecar.MoviesubtitlesUploadId, sidecar.MoviesubtitlesUploadedAt, null);
        }

        foreach (var datei in db.MediaRows.AsNoTracking().ToList())
        {
            Schreib(db, zaehler, jetzt, datei.Id, "subdl", null, null, null, datei.CompleteSubdl);
            Schreib(db, zaehler, jetzt, datei.Id, "tvsubtitles", null, null, null, datei.CompleteTvSubtitles);
            Schreib(db, zaehler, jetzt, datei.Id, "moviesubtitles", null, null, null, datei.CompleteMsub);
        }

        db.SaveChanges();
        return zaehler;
    }

    private static void Schreib(
        ScribeSqliteContext db,
        Dictionary<string, int> zaehler,
        DateTime jetzt,
        string ownerKey,
        string quelle,
        string? downloadId,
        string? uploadId,
        DateTime? uploadedAt,
        DateTime? completedAt)
    {
        if (string.IsNullOrEmpty(downloadId) && string.IsNullOrEmpty(uploadId) && uploadedAt == null && completedAt == null)
        {
            return;
        }

        db.SourceMarkRows.Add(new SourceMarkEntity
        {
            Id = KeyOf(ownerKey, quelle),
            OwnerKey = ownerKey,
            Source = quelle,
            DownloadId = downloadId,
            UploadId = uploadId,
            UploadedAt = uploadedAt,
            CompletedAt = completedAt,
            Written = jetzt,
        });
        zaehler[quelle] = zaehler.TryGetValue(quelle, out int n) ? n + 1 : 1;
    }
}

/// <summary>
/// Die quellenbezogenen Felder EINER Spur, ausgelesen aus einer Zeile — damit dieselbe Zählung und
/// dieselbe Rückfüllung für eingebettete Spuren und Sidecars gilt.
/// </summary>
/// <param name="Id">Key of the track row.</param>
/// <param name="SubdlDownloadId">SubDL fetch id.</param>
/// <param name="SubdlUploadId">SubDL submission id.</param>
/// <param name="SubdlUploadedAt">SubDL submission time.</param>
/// <param name="TvSubtitlesDownloadId">tvsubtitles fetch id.</param>
/// <param name="TvSubtitlesUploadId">tvsubtitles submission id.</param>
/// <param name="TvSubtitlesUploadedAt">tvsubtitles submission time.</param>
/// <param name="MoviesubtitlesDownloadId">moviesubtitles fetch id.</param>
/// <param name="MoviesubtitlesUploadId">moviesubtitles submission id.</param>
/// <param name="MoviesubtitlesUploadedAt">moviesubtitles submission time.</param>
internal sealed record SpurBeleg(
    string Id,
    string? SubdlDownloadId,
    string? SubdlUploadId,
    DateTime? SubdlUploadedAt,
    string? TvSubtitlesDownloadId,
    string? TvSubtitlesUploadId,
    DateTime? TvSubtitlesUploadedAt,
    string? MoviesubtitlesDownloadId,
    string? MoviesubtitlesUploadId,
    DateTime? MoviesubtitlesUploadedAt);
