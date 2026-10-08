// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LiteDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Data;

/// <summary>
/// F-M327: the one-time import of the previous document store into the SQLite file.
/// <para>
/// Why an import rather than a reset. Measured on a live install before this was written: the old
/// file held 117 rows over ten areas. Most of them are rebuildable — media and track rows come back
/// from a rescan, OSHashes are pure cache, worker rows are cosmetic. Two areas are NOT: the retry
/// counters (an id-resolution failure sits at 3, a QA fail at 1) and the burned-candidate verdicts.
/// Losing those does not lose data, it loses WORK: a title that keeps failing is retried from
/// scratch, and a candidate that was already screened is fetched and screened again — which is the
/// exact failure the old engine's own comments record (41 downloads in one run against a 50/day
/// quota because the mark was never written).
/// </para>
/// <para>
/// So the source is read once, written into SQLite, and then kept — renamed aside, never deleted.
/// If the import throws, the SQLite file is left absent so the next start tries again, and the old
/// file is untouched either way.
/// </para>
/// <para>
/// Runs BEFORE the SQLite store is opened, and only when there is nothing to import INTO: the
/// presence of a non-empty SQLite file short-circuits everything, so this path is walked once in the
/// life of an installation.
/// </para>
/// </summary>
public static class LiteDbImport
{
    /// <summary>Suffix the imported source file is renamed to, so it is never read again.</summary>
    public const string ImportedSuffix = ".imported";

    /// <summary>
    /// Imports the old document store when, and only when, the SQLite file does not exist yet and a
    /// document store does.
    /// </summary>
    /// <param name="dataDir">Plugin data directory.</param>
    /// <param name="sqlitePath">Path of the SQLite file about to be created.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>True when an import ran.</returns>
    public static bool RunOnce(string dataDir, string sqlitePath, ILogger? logger)
    {
        try
        {
            if (File.Exists(sqlitePath) && new FileInfo(sqlitePath).Length > 0)
            {
                return false;
            }

            var legacyPath = DbFiles.LegacyPathIn(dataDir);
            if (!File.Exists(legacyPath) || new FileInfo(legacyPath).Length == 0)
            {
                return false;
            }

            // A previous run that threw before it could rename the source would leave an empty
            // SQLite file beside it. Remove it so EnsureCreated starts from a known state.
            if (File.Exists(sqlitePath))
            {
                File.Delete(sqlitePath);
            }

            logger?.LogWarning(
                "[SubDL-DB] importing the previous data file into SQLite (one time): {Source}",
                Path.GetFileName(legacyPath));

            var imported = Import(legacyPath, sqlitePath, logger);

            // Only now is the source set aside. A failed import leaves it exactly where it was, so
            // the next start retries with the original file.
            var aside = legacyPath + ImportedSuffix;
            if (File.Exists(aside))
            {
                File.Delete(aside);
            }

            File.Move(legacyPath, aside);

            logger?.LogWarning(
                "[SubDL-DB] import finished: {Areas} area(s), {Rows} row(s). The previous file is kept as {Aside}.",
                imported.Areas, imported.Rows, Path.GetFileName(aside));
            return true;
        }
        catch (Exception ex)
        {
            // An import that fails must not stop the plugin from starting: an empty store is usable,
            // a plugin that refuses to load is not. The source file is still in place.
            logger?.LogError(ex, "[SubDL-DB] importing the previous data file failed — starting with an empty store. The previous file was left untouched.");
            TryDelete(sqlitePath);
            return false;
        }
    }

    /// <summary>
    /// Reads every area of the old file and writes it into the new one.
    /// <para>
    /// Reads as <see cref="BsonDocument"/>, never as mapped entities: field types in the old file
    /// (int vs long) disagree with the entity classes, and a mapped read dies with
    /// <c>InvalidCastException</c> on an unrelated field. The old engine's own repair path learned
    /// this the same way.
    /// </para>
    /// </summary>
    /// <param name="legacyPath">Path of the document store.</param>
    /// <param name="sqlitePath">Path of the SQLite file to write.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>Areas and rows written.</returns>
    private static (int Areas, long Rows) Import(string legacyPath, string sqlitePath, ILogger? logger)
    {
        // Read everything first, with the source open read-only and for as short a time as possible.
        var read = new Dictionary<string, List<BsonDocument>>(StringComparer.OrdinalIgnoreCase);
        using (var source = new LiteDatabase(new ConnectionString { Filename = legacyPath, ReadOnly = true }))
        {
            foreach (var area in source.GetCollectionNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                read[area] = source.GetCollection(area).FindAll().ToList();
            }
        }

        int areas = 0;
        long rows = 0;

        using (var ctx = new SubdlSqliteContext(sqlitePath))
        {
            ctx.Database.EnsureCreated();
            ctx.ConfigurePragmas();

            foreach (var area in read)
            {
                var docs = area.Value;
                if (docs.Count == 0)
                {
                    continue;
                }

                switch (area.Key.ToLowerInvariant())
                {
                    case "meta":
                        ctx.MetaRows.AddRange(docs.Select(ToMeta));
                        break;
                    case "oshashes":
                        ctx.OshashRows.AddRange(docs.Select(ToOshash));
                        break;
                    case "media":
                        ctx.MediaRows.AddRange(docs.Select(ToMedia));
                        break;
                    case "embeds":
                        ctx.EmbedRows.AddRange(docs.Select(ToEmbed));
                        break;
                    case "sidecars":
                        ctx.SidecarRows.AddRange(docs.Select(ToSidecar));
                        break;
                    case "rejected_candidates":
                        ctx.RejectedRows.AddRange(docs.Select(ToRejected));
                        break;
                    case "counters":
                        ctx.CounterRows.AddRange(docs.Select(ToCounter));
                        break;
                    case "runs":
                        ctx.RunRows.AddRange(docs.Select(ToRun));
                        break;
                    case "status_stats":
                        ctx.StatusRows.AddRange(docs.Select(ToStatus));
                        break;
                    case "worker_runs":
                        ctx.WorkerRunRows.AddRange(docs.Select(ToWorkerRun));
                        break;
                    default:
                        // An area this build does not know is skipped on purpose: writing rows into
                        // an area with no schema would fail the whole import for a leftover.
                        logger?.LogWarning("[SubDL-DB] import skipped unknown area {Area} ({Rows} row(s)).", area.Key, docs.Count);
                        continue;
                }

                ctx.SaveChanges();
                areas++;
                rows += docs.Count;
            }
        }

        return (areas, rows);
    }

    /// <summary>Reads a string field, or null when absent.</summary>
    private static string? S(BsonDocument d, string name)
    {
        if (!d.TryGetValue(name, out var v) || v.IsNull)
        {
            return null;
        }

        return v.AsString;
    }

    /// <summary>Reads a required string field, empty when absent.</summary>
    private static string SReq(BsonDocument d, string name) => S(d, name) ?? string.Empty;

    /// <summary>Reads an int field; absent reads as 0.</summary>
    private static int I(BsonDocument d, string name)
    {
        if (!d.TryGetValue(name, out var v) || v.IsNull)
        {
            return 0;
        }

        return v.IsInt32 ? v.AsInt32 : (int)v.AsInt64;
    }

    /// <summary>Reads an int field that may be absent, as null.</summary>
    private static int? INull(BsonDocument d, string name)
    {
        if (!d.TryGetValue(name, out var v) || v.IsNull)
        {
            return null;
        }

        return v.IsInt32 ? v.AsInt32 : (int)v.AsInt64;
    }

    /// <summary>Reads a long field; absent reads as 0.</summary>
    private static long L(BsonDocument d, string name)
    {
        if (!d.TryGetValue(name, out var v) || v.IsNull)
        {
            return 0;
        }

        return v.IsInt64 ? v.AsInt64 : v.AsInt32;
    }

    /// <summary>Reads a bool field; absent reads as false.</summary>
    private static bool B(BsonDocument d, string name)
        => d.TryGetValue(name, out var v) && !v.IsNull && v.AsBoolean;

    /// <summary>
    /// Reads a bool field that may be ABSENT, as null. F-M285: the three-valued flag is the whole
    /// point — a stored false is a statement, an absent field is a gap. The import must not turn a
    /// gap into a false claim.
    /// </summary>
    private static bool? BNull(BsonDocument d, string name)
    {
        if (!d.TryGetValue(name, out var v) || v.IsNull)
        {
            return null;
        }

        return v.AsBoolean;
    }

    /// <summary>Reads a DateTime field, or null when absent.</summary>
    private static DateTime? D(BsonDocument d, string name)
    {
        if (!d.TryGetValue(name, out var v) || v.IsNull)
        {
            return null;
        }

        return v.AsDateTime;
    }

    /// <summary>Reads a required DateTime field, defaulting to now.</summary>
    private static DateTime DReq(BsonDocument d, string name) => D(d, name) ?? DateTime.UtcNow;

    private static MetaEntity ToMeta(BsonDocument d) => new()
    {
        Id = SReq(d, "_id"),
        SchemaVersion = I(d, "SchemaVersion"),
        PluginVersion = S(d, "PluginVersion"),
        JellyfinVersion = S(d, "JellyfinVersion"),
        Updated = DReq(d, "Updated"),
    };

    private static OshashEntity ToOshash(BsonDocument d) => new()
    {
        Id = SReq(d, "_id"),
        Hash = SReq(d, "Hash"),
        Size = L(d, "Size"),
        Updated = DReq(d, "Updated"),
        MtimeUnix = L(d, "MtimeUnix"),
    };

    private static MediaEntity ToMedia(BsonDocument d) => new()
    {
        Id = SReq(d, "_id"),
        Path = S(d, "Path"),
        JellyfinItemId = S(d, "JellyfinItemId"),
        ImdbId = S(d, "ImdbId"),
        TmdbId = S(d, "TmdbId"),
        SdId = S(d, "SdId"),
        IsSeries = B(d, "IsSeries"),
        Season = INull(d, "Season"),
        Episode = INull(d, "Episode"),
        LanguagesAvailable = S(d, "LanguagesAvailable"),
        SubtitlesUploadedAt = D(d, "SubtitlesUploadedAt"),
        LastSearchUtc = D(d, "LastSearchUtc"),
        LastSearchLanguages = S(d, "LastSearchLanguages"),
        LastSeen = DReq(d, "LastSeen"),
    };

    private static EmbedTrackEntity ToEmbed(BsonDocument d) => new()
    {
        Id = SReq(d, "_id"),
        MediaHash = SReq(d, "MediaHash"),
        SubPos = I(d, "SubPos"),
        Language = SReq(d, "Language"),
        HearingImpaired = BNull(d, "HearingImpaired"),
        Forced = BNull(d, "Forced"),
        ContentHash = S(d, "ContentHash"),
        Status = SReq(d, "Status"),
        Reason = S(d, "Reason"),
        StatusAt = DReq(d, "StatusAt"),
        SubdlId = S(d, "SubdlId"),
        DetectionAttemptedAt = D(d, "DetectionAttemptedAt"),
        DetectionOutcome = S(d, "DetectionOutcome"),
    };

    private static SidecarEntity ToSidecar(BsonDocument d) => new()
    {
        Id = SReq(d, "_id"),
        Language = SReq(d, "Language"),
        HearingImpaired = BNull(d, "HearingImpaired"),
        Forced = BNull(d, "Forced"),
        ContentHash = S(d, "ContentHash"),
        Status = SReq(d, "Status"),
        Reason = S(d, "Reason"),
        StatusAt = DReq(d, "StatusAt"),
        SubdlId = S(d, "SubdlId"),
        FileName = S(d, "FileName"),
        MediaHash = SReq(d, "MediaHash"),
        Path = S(d, "Path"),
        ImdbId = S(d, "ImdbId"),
        TmdbId = S(d, "TmdbId"),
        IsSeries = B(d, "IsSeries"),
        Season = INull(d, "Season"),
        Episode = INull(d, "Episode"),
    };

    private static RejectedCandidateEntity ToRejected(BsonDocument d) => new()
    {
        Id = SReq(d, "_id"),
        ItemId = SReq(d, "ItemId"),
        Language = SReq(d, "Language"),
        SubdlId = SReq(d, "SubdlId"),
        Reason = SReq(d, "Reason"),
        Updated = DReq(d, "Updated"),
    };

    /// <summary>
    /// Reads a counter row. The old area's primary key was an auto-increment integer; the business
    /// key <c>Key</c> is what the new schema keys on (F-M194), so the numeric id is not carried over.
    /// </summary>
    private static CounterEntity ToCounter(BsonDocument d) => new()
    {
        Key = SReq(d, "Key"),
        Value = I(d, "Value"),
        Expires = D(d, "Expires"),
        Updated = DReq(d, "Updated"),
    };

    private static PipelineRunEntity ToRun(BsonDocument d) => new()
    {
        Direction = SReq(d, "Direction"),
        Trigger = SReq(d, "Trigger"),
        Started = DReq(d, "Started"),
        Ended = D(d, "Ended"),
        ItemsTotal = I(d, "ItemsTotal"),
        ItemsDone = I(d, "ItemsDone"),
    };

    private static StatusStatsEntity ToStatus(BsonDocument d) => new()
    {
        Id = SReq(d, "_id"),
        Uploaded = L(d, "Uploaded"),
        Downloaded = L(d, "Downloaded"),
        RejectedDownload = L(d, "RejectedDownload"),
        RejectedUpload = L(d, "RejectedUpload"),
        FittedToAudio = L(d, "FittedToAudio"),
        LanguageCodesAllocated = L(d, "LanguageCodesAllocated"),
        LooseSubtitlesRenamed = L(d, "LooseSubtitlesRenamed"),
        SinceUtc = D(d, "SinceUtc"),
        Updated = DReq(d, "Updated"),
    };

    private static WorkerRunEntity ToWorkerRun(BsonDocument d) => new()
    {
        Id = SReq(d, "_id"),
        Name = SReq(d, "Name"),
        Started = D(d, "Started"),
        Ended = D(d, "Ended"),
        Outcome = string.IsNullOrEmpty(S(d, "Outcome")) ? "never" : S(d, "Outcome")!,
        Detail = SReq(d, "Detail"),
        DryRun = B(d, "DryRun"),
    };

    /// <summary>Deletes a file, ignoring a failure — cleanup must never mask the real error.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Nothing to do: the file is a leftover, not state.
        }
    }
}
