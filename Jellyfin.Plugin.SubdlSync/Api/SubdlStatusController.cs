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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// (20.09.2026): live traffic-light status for SubDL/TMDB credentials,
/// API health and media-directory write access. Used by the plugin status tab.
/// </summary>
[ApiController]
[Route("Plugins/SubdlSync")]
public class SubdlStatusController : ControllerBase
{
    private readonly ILogger<SubdlStatusController> _logger;
    private readonly ILibraryManager _libraryManager;
    private static readonly HttpClient _httpClient = new();

    public SubdlStatusController(ILogger<SubdlStatusController> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Get a traffic-light status overview.
    /// </summary>
    /// <returns>Status object with subdl, tmdb and directory entries.</returns>
    [HttpGet("Status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var result = new StatusDto
        {
            Subdl = await CheckSubdlAsync(cfg, ct).ConfigureAwait(false),
            SubdlLogin = await CheckSubdlLoginAsync(cfg, ct).ConfigureAwait(false),
            Tmdb = await CheckTmdbAsync(cfg, ct).ConfigureAwait(false),
            Directories = CheckDirectories(cfg),
            Timestamp = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
        return Ok(result);
    }

    /// <summary>
    /// Reads the cumulative GUI counters from the database.
    /// <para>
    /// Kept out of <c>getPluginConfiguration</c> on purpose: the counters live in the data file now
    /// (25.09.2026), so the GUI must ask the database, not the configuration.
    /// </para>
    /// </summary>
    /// <returns>Uploaded/downloaded totals and the period start.</returns>
    [HttpGet("Stats")]
    [AllowAnonymous]
    public IActionResult GetStats()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        var row = plugin.GetStatusStats();

        // F-M218: the quality counters ride along with the volume counters.
        return Ok(new
        {
            Uploaded = row.Uploaded,
            Downloaded = row.Downloaded,
            TypeCorrectedByFileName = row.TypeCorrectedByFileName,
            TmdbYearFilterMisses = row.TmdbYearFilterMisses,
            RejectedDownload = row.RejectedDownload,
            RejectedUpload = row.RejectedUpload,
            SinceUtc = row.SinceUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Updated = row.Updated.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    /// <summary>
    /// The last run of every worker, read from the DATA FILE.
    /// <para>
    /// The postprocessing status used to be static in-memory state, so it read "never run" after every
    /// Jellyfin restart even when the task had run minutes earlier (measured 30.09.2026). This endpoint
    /// answers from the stored rows instead, which survive a restart, and reports every known worker —
    /// including ones that have never run, so the section is complete from the first page load.
    /// </para>
    /// </summary>
    /// <returns>One entry per worker with its last run.</returns>
    [HttpGet("WorkerRuns")]
    [AllowAnonymous]
    public IActionResult GetWorkerRuns()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        var rows = plugin.WorkerRuns.List();
        return Ok(new
        {
            Workers = rows.Select(r => new
            {
                r.Id,
                r.Name,
                Started = r.Started?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Ended = r.Ended?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                r.Outcome,
                r.Detail,
                r.DryRun
            }),
            Timestamp = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    /// <summary>
    /// Zeroes the cumulative GUI counters and restarts the period ("Reset statistics").
    /// </summary>
    /// <param name="confirm">Must be exactly "true".</param>
    /// <returns>The zeroed counters.</returns>
    [HttpPost("StatsReset")]
    public IActionResult ResetStats([FromQuery] string confirm = "")
    {
        if (!string.Equals(confirm, "true", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { ok = false, error = "confirmation-missing" });
        }

        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { ok = false, error = "plugin not initialized" });
        }

        var row = plugin.ResetStatusStats();
        LogUtil.Normal(_logger, "[SubDL] statistics reset: counters zeroed, period restarted.");

        // F-M218/F-M219: the response is fed straight back into the renderer by the GUI
        // (window.subdlRenderStats(res)), so it must carry the SAME fields the Stats GET
        // returns. Returning only the volume counters left the quality line reading
        // "undefined typed by file name, undefined TMDb year-filter misses…" right after a
        // reset — the F-M216 class of bug, one endpoint over.
        return Ok(new
        {
            ok = true,
            Uploaded = row.Uploaded,
            Downloaded = row.Downloaded,
            TypeCorrectedByFileName = row.TypeCorrectedByFileName,
            TmdbYearFilterMisses = row.TmdbYearFilterMisses,
            RejectedDownload = row.RejectedDownload,
            RejectedUpload = row.RejectedUpload,
            SinceUtc = row.SinceUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Updated = row.Updated.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    private async Task<ApiStatusDto> CheckSubdlLoginAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var dto = new ApiStatusDto { Label = "SubDL Login", Configured = !string.IsNullOrWhiteSpace(cfg.Username) && !string.IsNullOrWhiteSpace(cfg.Password) };
        if (!dto.Configured)
        {
            dto.Light = "yellow";
            dto.Message = "Username/password not configured";
            return dto;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "SubDL-Sync-Status/1.0");
            var client = new SubdlApiClient(http)
            {
                ApiKey = cfg.ApiKey,
                Username = cfg.Username,
                Password = cfg.Password
            };
            var token = await client.LoginAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                dto.Light = "red";
                dto.Message = "Login succeeded but returned no token";
            }
            else
            {
                dto.Light = "green";
                dto.Message = "Login OK";
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("credentials wrong", StringComparison.OrdinalIgnoreCase))
        {
            dto.Light = "red";
            dto.Message = "Login failed: wrong username/password";
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not authorized", StringComparison.OrdinalIgnoreCase))
        {
            dto.Light = "red";
            dto.Message = "Login failed: not authorized — check API key";
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase))
        {
            dto.Light = "yellow";
            dto.Message = "Login rate-limited — try again later";
        }
        catch (Exception ex)
        {
            dto.Light = "red";
            dto.Message = $"Login failed: {ex.Message}";
        }

        return dto;
    }

    private async Task<ApiStatusDto> CheckSubdlAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var dto = new ApiStatusDto { Label = "SubDL API", Configured = !string.IsNullOrWhiteSpace(cfg.ApiKey) };
        if (!dto.Configured)
        {
            dto.Light = "yellow";
            dto.Message = "API key not configured — required for subtitle search";
            return dto;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            // Lightweight search probe: query for a well-known imdb id with no real expectation of hits.
            var url = $"https://api.subdl.com/api/v1/subtitles?api_key={WebUtility.UrlEncode(cfg.ApiKey)}&imdb_id=tt0137523&languages=en&subs_per_page=1";
            using var resp = await _httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            {
                dto.Light = "red";
                dto.Message = "API key invalid / not authorized";
            }
            else if ((int)resp.StatusCode == 429)
            {
                dto.Light = "yellow";
                dto.Message = "Rate limited (HTTP 429)";
                dto.RateLimited = true;
            }
            else if (resp.IsSuccessStatusCode)
            {
                dto.Light = "green";
                dto.Message = "API reachable";
            }
            else
            {
                dto.Light = "red";
                dto.Message = $"API returned HTTP {(int)resp.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            dto.Light = "red";
            dto.Message = $"Probe error: {ex.Message}";
        }

        return dto;
    }

    private async Task<ApiStatusDto> CheckTmdbAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var dto = new ApiStatusDto { Label = "TMDb API", Configured = !string.IsNullOrWhiteSpace(cfg.TmdbApiKey) };
        if (!dto.Configured)
        {
            // REQUIRED, not optional (user decision 25.09.2026): id resolution is
            // TMDB-authoritative and series are skipped entirely without a key, so a
            // missing key is a defect the user must see as red — not a yellow
            // "optional, fine to leave empty" hint.
            dto.Light = "red";
            dto.Message = "Not configured — REQUIRED: series are skipped without it (films still upload)";
            return dto;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var url = $"https://api.themoviedb.org/3/configuration?api_key={WebUtility.UrlEncode(cfg.TmdbApiKey)}";
            using var resp = await _httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                dto.Light = "red";
                dto.Message = "API key invalid (HTTP 401)";
            }
            else if ((int)resp.StatusCode == 429)
            {
                dto.Light = "yellow";
                dto.Message = "Rate limited (HTTP 429)";
                dto.RateLimited = true;
            }
            else if (resp.IsSuccessStatusCode)
            {
                dto.Light = "green";
                dto.Message = "API reachable";
            }
            else
            {
                dto.Light = "red";
                dto.Message = $"API returned HTTP {(int)resp.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            dto.Light = "red";
            dto.Message = $"Probe error: {ex.Message}";
        }

        return dto;
    }

    private List<DirectoryStatusDto> CheckDirectories(PluginConfiguration cfg)
    {
        var result = new List<DirectoryStatusDto>();
        if (_libraryManager == null)
        {
            return result;
        }

        var scope = LibraryScope.Create(_libraryManager, cfg.SelectedLibraries, _logger);
        if (scope.IsEmpty)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = _libraryManager.GetVirtualFolders();
        foreach (var folder in folders)
        {
            // F-M213 (26.09.2026, user decision): the report is about the
            // SELECTION, never about the walk scope. A nested selection's items
            // do live in the outer library (F-M189) and the walk must keep
            // enumerating it — but listing that outer library here showed a
            // directory the user never picked (/data/movies2 next to the
            // selected /data/movies2/Test_SubDL), which reads as "all of this is
            // scanned". A status row therefore names what was selected.
            if (!scope.IsSelectedName(folder.Name))
            {
                continue;
            }

            foreach (var loc in folder.Locations)
            {
                if (!seen.Add(loc))
                {
                    continue;
                }

                var dto = new DirectoryStatusDto
                {
                    LibraryName = folder.Name,
                    Path = loc,
                    Exists = Directory.Exists(loc),
                    Writable = false
                };

                if (!dto.Exists)
                {
                    dto.Light = "red";
                    dto.Message = "Directory does not exist";
                }
                else
                {
                    try
                    {
                        var probe = Path.Combine(loc, ".subdl-write-probe");
                        System.IO.File.WriteAllBytes(probe, new byte[] { 0x70 });
                        System.IO.File.Delete(probe);
                        dto.Writable = true;
                        dto.Light = "green";
                        dto.Message = "Writable";
                    }
                    catch (Exception ex)
                    {
                        dto.Light = "red";
                        dto.Message = $"Not writable: {ex.Message}";
                    }
                }

                result.Add(dto);
            }
        }

        return result;
    }

    /// <summary>DTO returned by /Plugins/SubdlSync/Status.</summary>
    public class StatusDto
    {
        public ApiStatusDto Subdl { get; set; } = new();
        public ApiStatusDto SubdlLogin { get; set; } = new();
        public ApiStatusDto Tmdb { get; set; } = new();
        public List<DirectoryStatusDto> Directories { get; set; } = new();
        public string Timestamp { get; set; } = string.Empty;
    }

    /// <summary>Traffic-light status for a single API.</summary>
    public class ApiStatusDto
    {
        public string Label { get; set; } = string.Empty;
        public string Light { get; set; } = "red";
        public bool Configured { get; set; }
        public bool RateLimited { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Traffic-light status for a single library directory.</summary>
    public class DirectoryStatusDto
    {
        public string LibraryName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool Exists { get; set; }
        public bool Writable { get; set; }
        public string Light { get; set; } = "red";
        public string Message { get; set; } = string.Empty;
    }
}
