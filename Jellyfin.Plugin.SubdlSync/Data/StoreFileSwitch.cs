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
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// Der Formatwechsel beim Start (F-M637, F-M638): die Vorgängerdatei wird zur neuen Datei, geprüft,
/// und erst dann zur live Datei.
/// </summary>
public static class StoreFileSwitch
{
    /// <summary>Was der Wechsel getan hat.</summary>
    public enum Outcome
    {
        /// <summary>Es gab keine Vorgängerdatei — der Store startet leer.</summary>
        NothingToConvert,

        /// <summary>Die neue Datei war schon da; die Vorgängerdatei bleibt liegen (F-M638).</summary>
        AlreadyCurrent,

        /// <summary>Umgestellt: die neue Datei ist live, die Vorgängerdatei bleibt als Sicherung liegen.</summary>
        Converted,

        /// <summary>Abgebrochen: die Prüfsumme stimmte nicht, die Vorgängerdatei bleibt live.</summary>
        KeptPrevious,
    }

    /// <summary>
    /// Führt den Wechsel aus. Läuft VOR dem Öffnen des Stores und ist der einzige Pfad, der die
    /// Vorgängerdatei liest.
    /// </summary>
    /// <param name="dataDir">Plugin data directory.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>Which file the live store has to be, and why.</returns>
    public static (Outcome Outcome, string LivePath) Run(string dataDir, ILogger? logger)
    {
        string neu = DbFiles.PathIn(dataDir);
        string alt = DbFiles.PreviousPathIn(dataDir);

        if (File.Exists(neu))
        {
            return (Outcome.AlreadyCurrent, neu);
        }

        if (!File.Exists(alt))
        {
            // F-M639: keine Vorgängerdatei, kein Übernahmepfad — der Store startet leer, und eine
            // irgendwo liegende ältere Datei wird weder gelesen noch angefasst.
            return (Outcome.NothingToConvert, neu);
        }

        string tmp = neu + ".tmp";
        try
        {
            // WAL-Punkt vor dem Kopieren: sonst fehlt der kopierten Datei, was noch im Side-File liegt.
            Checkpoint(alt);
            foreach (string suffix in DbFiles.SideSuffixes)
            {
                string side = DbFiles.SidePath(tmp, suffix);
                if (File.Exists(side))
                {
                    File.Delete(side);
                }
            }

            File.Copy(alt, tmp, overwrite: true);

            // Der Aufbau der neuen Datei geht denselben Weg wie jeder Start — EnsureCreated, die
            // Spalten-Nachzüge und die Belegtabelle inklusive. Nur der Wechsel selbst wird übersprungen,
            // sonst riefe er sich selbst auf.
            using (var db = new ScribeDbContext(dataDir, tmp, logger))
            {
                var geschrieben = SourceMarkStore.BackfillFromColumns(db.Context);
                var spalten = SourceMarkStore.CountColumnsBySource(db.Context);
                var belege = SourceMarkStore.CountBySource(db.Context);

                foreach (var paar in spalten)
                {
                    belege.TryGetValue(paar.Key, out int ist);
                    if (ist != paar.Value)
                    {
                        logger?.LogWarning(
                            "[Sub Scribe] format change aborted: source \"{Source}\" has {Columns} record(s) with marks in the columns but {Marks} mark(s) in the new table — the previous file stays the live store (F-M637).",
                            paar.Key,
                            paar.Value,
                            ist);
                        Abandon(tmp);
                        return (Outcome.KeptPrevious, alt);
                    }
                }

                long dateien = db.Media.FindAll().LongCount();
                logger?.LogInformation(
                    "[Sub Scribe] format change verified: {Records} record(s), marks per source {Marks} — previous file kept beside it (F-M637/F-M638).",
                    dateien,
                    string.Join(", ", Belege(belege)));
            }

            // Ein liegengebliebenes Side-File der LIVE-Datei würde nach dem Umbenennen auf die neue Datei
            // angewendet — es gehört zu der Datei, die dort vorher lag, und wird deshalb vorher entfernt.
            foreach (string suffix in DbFiles.SideSuffixes)
            {
                string side = DbFiles.SidePath(neu, suffix);
                if (File.Exists(side))
                {
                    File.Delete(side);
                }
            }

            File.Move(tmp, neu, overwrite: true);
            logger?.LogInformation(
                "[Sub Scribe] store file is now {New}; the previous file {Old} stays where it is as the fallback (F-M638).",
                Path.GetFileName(neu),
                Path.GetFileName(alt));
            return (Outcome.Converted, neu);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "[Sub Scribe] format change failed ({Msg}) — the previous file stays the live store (F-M637).",
                ex.Message);
            Abandon(tmp);
            return (Outcome.KeptPrevious, alt);
        }
    }

    private static IEnumerable<string> Belege(Dictionary<string, int> belege)
    {
        foreach (var paar in belege)
        {
            yield return paar.Key + "=" + paar.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static void Checkpoint(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    private static void Abandon(string tmp)
    {
        try
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            foreach (string suffix in DbFiles.SideSuffixes)
            {
                string side = DbFiles.SidePath(tmp, suffix);
                if (File.Exists(side))
                {
                    File.Delete(side);
                }
            }
        }
        catch (Exception)
        {
            // Ein liegengebliebener .tmp ist harmlos; er wird beim nächsten Start überschrieben.
        }
    }
}
