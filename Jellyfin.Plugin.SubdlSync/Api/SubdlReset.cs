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
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M90: registry reset endpoint — clears the plugin's LiteDB state.
/// After confirmation, the database file is renamed with a timestamped backup
/// and the shared context is recreated. The next run starts fresh: hashes
/// recompute, streams re-screen, uploads re-check against SubDL.
/// Route: POST /Plugins/SubdlReset/Reset?confirm=true
/// </summary>
[ApiController]
[Route("Plugins/[controller]/[action]")]
public class SubdlReset : ControllerBase
{
    private readonly ILogger<SubdlReset> _logger;

    public SubdlReset(ILogger<SubdlReset> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resets the requested state. Requires confirm=true.
    /// </summary>
    /// <param name="confirm">Must be exactly "true".</param>
    /// <param name="scope">all | upload | download — which direction's state to wipe.</param>
    [HttpPost]
    public IActionResult Reset([FromQuery] string confirm = "", [FromQuery] string scope = "all")
    {
        if (!string.Equals(confirm, "true", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { ok = false, error = "confirmation-missing" });
        }

        string dataDir = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("plugin not initialized");

        var db = Plugin.Instance.SharedDbContext;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string dbPath = Data.DbFiles.PathIn(dataDir);
        string backupPath = dbPath + ".bak-" + stamp;

        try
        {
            if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                db.Dispose();
                if (System.IO.File.Exists(dbPath))
                {
                    System.IO.File.Copy(dbPath, backupPath, overwrite: true);
                    System.IO.File.Delete(dbPath);
                }

                // F-M181: keep only the most recent backup; delete older .bak-* files.
                foreach (var old in System.IO.Directory.GetFiles(dataDir, Data.DbFiles.BackupPrefix + "*"))
                {
                    if (!string.Equals(old, backupPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try { System.IO.File.Delete(old); }
                        catch (Exception ex) { _logger.LogWarning(ex, "[SubDL] Could not delete old backup {Old}.", old); }
                    }
                }

                // Also clean up old JSON state files if still present.
                foreach (var old in GetLegacyStateFiles())
                {
                    var oldPath = Path.Combine(dataDir, old);
                    if (System.IO.File.Exists(oldPath))
                    {
                        System.IO.File.Move(oldPath, oldPath + ".bak-" + stamp, overwrite: true);
                    }
                }

                Plugin.Instance.RecreateDbContext();

                // The counters live in the data file now, so the whole-scope reset zeroes them as
                // part of the same operation. Without this the display would keep reporting totals
                // from before the reset while the database it describes is empty — the exact drift
                // the move out of the configuration XML was meant to remove.
                Plugin.Instance.ResetStatusStats();
                LogUtil.Normal(_logger, "[SubDL] Reset \"all\": status counters zeroed with the database.");
            }
            else if (scope.Equals("upload", StringComparison.OrdinalIgnoreCase))
            {
                // Upload side = the embedded tracks of every file.
                db.Embeds.DeleteAll();

                // F-M192 (24.09.2026): the flags must be written back as the SAME
                // objects that were mutated. The old code called
                // Update(db.Media.FindAll()) — a SECOND, freshly deserialized list
                // whose UploadFileComplete is still true — so the reset reported
                // "completed" while the database kept every marker (measured: reset
                // at 15:41:50, subdl-sync.db mtime stayed 15:07 and the next run
                // skipped all 25 files as file-complete). Collect the touched rows
                // and persist exactly those.
                var touched = new List<MediaEntity>();
                foreach (var media in db.Media.FindAll().Where(m => m.SubtitlesUploadedAt != null))
                {
                    media.SubtitlesUploadedAt = null;
                    touched.Add(media);
                }

                if (touched.Count > 0)
                {
                    db.Media.Update(touched);
                }

                LogUtil.Normal(_logger, "[SubDL] Reset \"upload\": {Count} file-complete marker(s) cleared.", touched.Count);
            }
            else if (scope.Equals("download", StringComparison.OrdinalIgnoreCase))
            {
                // Download side = downloaded sidecars and burned candidates.
                db.Sidecars.DeleteMany(x => x.Status == SubtitleStatus.Downloaded);
                db.RejectedCandidates.DeleteAll();
                db.Counters.DeleteMany(x => x.Key.StartsWith("qa-fail:") || x.Key.StartsWith("id-not-found:"));
                foreach (var media in db.Media.Find(x => x.LastSearchUtc != null))
                {
                    media.LastSearchUtc = null;
                    media.LastSearchLanguages = null;
                    db.Media.Update(media);
                }

                // F-M283 (user decision 02.10.2026): there is no download completion mark to clear.
                // The reset keeps the part that is real state — the downloaded sidecar rows, the
                // burned candidates, the QA counters and the search stamps — and drops the sweep
                // over `SubtitlesDownloadedAt`, because the item is re-asked from its evidence on
                // the very next run. Nothing needs clearing to make it due again.
                LogUtil.Normal(_logger, "[SubDL] Reset \"download\": downloaded sidecars, burned candidates and search stamps cleared. No completion mark exists (F-M283).");
            }
            else
            {
                return Ok(new { ok = false, error = "bad-scope" });
            }

            ScheduledTasks.SubdlEventDispatcher.Instance?.ReloadQueues();
            _logger.LogWarning("[SubDL] Registry reset ({Scope}) completed; backup {Backup}.", scope, backupPath);
            return Ok(new { ok = true, scope, backup = Path.GetFileName(backupPath) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubDL] Registry reset ({Scope}) failed.", scope);
            return Ok(new { ok = false, error = "reset-error" });
        }
    }

    /// <summary>Lists restorable database backups.</summary>
    [HttpGet]
    public IActionResult List()
    {
        string dataDir = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("plugin not initialized");

        var backups = new List<string>();
        try
        {
            foreach (var f in System.IO.Directory.GetFiles(dataDir, Data.DbFiles.BackupPrefix + "*"))
            {
                var suffix = Path.GetFileName(f).Substring(Data.DbFiles.BackupPrefix.Length);
                if (System.Text.RegularExpressions.Regex.IsMatch(suffix, "^[0-9]{8}-[0-9]{6}$"))
                {
                    backups.Add(suffix);
                }
            }
        }
        catch { /* unreadable dir — leave empty */ }

        backups.Sort(StringComparer.Ordinal);
        backups.Reverse();
        return Ok(new { ok = true, file = Data.DbFiles.Name, backups });
    }

    /// <summary>
    /// Restores the database from a timestamped backup.
    /// </summary>
    /// <param name="confirm">Must be exactly "true".</param>
    /// <param name="stamp">Backup timestamp in yyyyMMdd-HHmmss format.</param>
    [HttpPost]
    public IActionResult Restore([FromQuery] string confirm = "", [FromQuery] string stamp = "")
    {
        if (!string.Equals(confirm, "true", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { ok = false, error = "confirmation-missing" });
        }

        if (string.IsNullOrEmpty(stamp)
            || !System.Text.RegularExpressions.Regex.IsMatch(stamp, "^[0-9]{8}-[0-9]{6}$"))
        {
            return Ok(new { ok = false, error = "bad-args" });
        }

        string dataDir = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("plugin not initialized");
        string dbPath = Data.DbFiles.PathIn(dataDir);
        string backupPath = dbPath + ".bak-" + stamp;

        if (!System.IO.File.Exists(backupPath))
        {
            return Ok(new { ok = false, error = "backup-missing" });
        }

        try
        {
            var db = Plugin.Instance.SharedDbContext;
            db.Dispose();

            string restoreStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            if (System.IO.File.Exists(dbPath))
            {
                System.IO.File.Copy(dbPath, dbPath + ".pre-restore-" + restoreStamp, overwrite: true);
            }

            System.IO.File.Copy(backupPath, dbPath, overwrite: true);
            Plugin.Instance.RecreateDbContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubDL] Restore failed.");
            return Ok(new { ok = false, error = "restore-error" });
        }

        ScheduledTasks.SubdlEventDispatcher.Instance?.ReloadQueues();
        _logger.LogWarning("[SubDL] Restored database from backup {Stamp}.", stamp);
        return Ok(new { ok = true, stamp });
    }

    private static IReadOnlyList<string> GetLegacyStateFiles() => new[]
    {
        "content-hash-registry.json",
        "oshash-cache.json",
        "file-retry-state.json",
        "download-search-state.json",
        "download-search-meta.json",
        "id-not-found-state.json",
        "qa-fail-state.json",
        "cycle-queues.json"
    };
}
