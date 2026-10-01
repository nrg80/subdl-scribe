// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubdlScribe.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// (20.09.2026): diagnostics endpoint that exposes LiteDB counters,
/// subtitle state and recent run records without requiring shell/SSH access.
/// </summary>
[ApiController]
[Route("Plugins/SubdlSync")]
public class SubdlDiagnosticsController : ControllerBase
{
    private readonly ILogger<SubdlDiagnosticsController> _logger;

    public SubdlDiagnosticsController(ILogger<SubdlDiagnosticsController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Get a diagnostic dump of the plugin database: counters, subtitle totals,
    /// per-media upload state and recent pipeline runs.
    /// </summary>
    [HttpGet("Diagnostics")]
    [AllowAnonymous]
    public IActionResult GetDiagnostics()
    {
        try
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return StatusCode(503, new { error = "Plugin instance not available" });
            }

            var db = plugin.SharedDbContext;
            var counters = db.Counters.FindAll().ToList();
            var notFoundCounters = counters
                .Where(c => c.Key.StartsWith("not-found:", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Value)
                .Take(50)
                .Select(c => new
                {
                    Key = c.Key,
                    Value = c.Value,
                    Updated = c.Updated,
                    Expires = c.Expires
                })
                .ToList();

            var embeds = db.Embeds.FindAll().ToList();
            var sidecars = db.Sidecars.FindAll().ToList();
            var media = db.Media.FindAll().ToList();
            var runs = db.Runs.FindAll().OrderByDescending(r => r.Started).Take(20).ToList();
            var oshashes = db.Oshashes.Count();
            var burnedCandidates = db.RejectedCandidates.Count();

            var mediaByUploadState = media
                .GroupBy(m => m.SubtitlesUploadedAt != null)
                .ToDictionary(g => g.Key ? "complete" : "pending", g => g.Count());

            var mediaSample = media
                .Where(m => m.SubtitlesUploadedAt == null)
                .OrderByDescending(m => m.LastSeen)
                .Take(20)
                .Select(m => new
                {
                    m.Id,
                    m.Path,
                    m.ImdbId,
                    m.TmdbId,
                    m.SdId,
                    m.IsSeries,
                    m.Season,
                    m.Episode,
                    m.SubtitlesUploadedAt,
                    m.LanguagesAvailable,
                    m.LastSeen
                })
                .ToList();

            var subtitleTotals = new
            {
                Total = embeds.Count + sidecars.Count,
                Embeds = embeds.Count,
                Sidecars = sidecars.Count,
                EmbedsByStatus = embeds.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count()),
                SidecarsByStatus = sidecars.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count()),
                RejectedReasons = embeds.Where(s => s.Reason != null).Select(s => s.Reason!)
                    .Concat(sidecars.Where(s => s.Reason != null).Select(s => s.Reason!))
                    .GroupBy(r => r)
                    .ToDictionary(g => g.Key, g => g.Count()),
                WithSubdlId = embeds.Count(s => !string.IsNullOrWhiteSpace(s.SubdlId))
                            + sidecars.Count(s => !string.IsNullOrWhiteSpace(s.SubdlId))
            };

            var result = new
            {
                DbPath = Data.DbFiles.PathIn(plugin.DataFolderPath),
                DbExists = System.IO.File.Exists(Data.DbFiles.PathIn(plugin.DataFolderPath)),
                DbSizeBytes = GetDbSize(plugin.DataFolderPath),
                Counters = new
                {
                    Total = counters.Count,
                    NotFound = notFoundCounters
                },
                Subtitles = subtitleTotals,
                Media = new
                {
                    Total = media.Count,
                    ByUploadState = mediaByUploadState,
                    PendingSample = mediaSample
                },
                Runs = runs.Select(r => new
                {
                    r.Id,
                    r.Direction,
                    r.Trigger,
                    Started = r.Started,
                    Ended = r.Ended,
                    r.ItemsTotal,
                    r.ItemsDone
                }),
                OshashCacheEntries = oshashes,
                RejectedCandidateEntries = burnedCandidates,
                Timestamp = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubDL] diagnostics endpoint failed");
            return StatusCode(500, new { error = ex.Message, stack = ex.StackTrace });
        }
    }

    private static long? GetDbSize(string dataFolderPath)
    {
        try
        {
            var path = DbFiles.PathIn(dataFolderPath);
            return System.IO.File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }
}
