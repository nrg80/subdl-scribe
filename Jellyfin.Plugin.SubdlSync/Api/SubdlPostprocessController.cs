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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// Manual trigger for upload postprocessing and debug endpoint for diagnostics.
/// </summary>
[ApiController]
[Route("Plugins/SubdlSync")]
public class SubdlPostprocessController : ControllerBase
{
    private readonly ILogger<SubdlPostprocessController> _logger;

    public SubdlPostprocessController(ILogger<SubdlPostprocessController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Run upload postprocessing immediately (cleans up rejected entries from /user/mySubtitles).
    /// </summary>
    [HttpPost("PostprocessUploads")]
    [AllowAnonymous]
    public async Task<IActionResult> PostprocessUploads()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        // F-M291 (operator order 08.10.2026): a MANUAL trigger always runs, with upload switched on
        // or off. The upload binding belongs to the automatic run only, and it is enforced where that
        // run is armed (the scheduler's anchor).
        LogUtil.Normal(_logger, "[SubDL] Manual upload postprocessing triggered via API.");
        await UploadPipeline.RunUploadPostprocessingAsync().ConfigureAwait(false);
        return Ok(new { status = "postprocessing completed" });
    }

    /// <summary>
    /// Count /user/mySubtitles entries on the first N pages for verification.
    /// </summary>
    [HttpPost("CountMySubtitles")]
    [AllowAnonymous]
    public async Task<IActionResult> CountMySubtitles([FromQuery] int pages = 5)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        var cfg = plugin.Configuration;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var api = new SubdlApiClient(http)
        {
            Username = cfg.Username,
            Password = cfg.Password,
            ApiKey = cfg.ApiKey
        };

        try
        {
            await api.LoginAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = "SubDL login failed", message = ex.Message });
        }

        var perPageCounts = new List<int>();
        var allEntries = new List<OwnSubtitleEntry>();
        var seenIds = new HashSet<int>();
        int total = 0;
        int page = 1;
        bool more = true;
        bool paginationUnsupported = false;
        // SubDL ignores page/per_page (see SubdlApiClient.ListMySubtitlesAsync), so every "page"
        // returns the same list. Count distinct upload ids and stop at the first page without new ones.
        while (more && page <= Math.Max(1, pages))
        {
            var entries = await api.ListMySubtitlesPageAsync(page, 100, CancellationToken.None).ConfigureAwait(false);
            if (entries == null)
            {
                perPageCounts.Add(-1);
                break;
            }

            int newOnPage = 0;
            foreach (var e in entries)
            {
                if (e.UploadId > 0 && !seenIds.Add(e.UploadId))
                {
                    continue;
                }

                allEntries.Add(e);
                newOnPage++;
            }

            perPageCounts.Add(entries.Count);
            total += newOnPage;
            more = entries.Count == 100 && newOnPage > 0;
            if (entries.Count == 100 && newOnPage == 0)
            {
                paginationUnsupported = true;
            }

            page++;
        }

        var statusBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in allEntries)
        {
            var status = e.Status ?? "unknown";
            if (!statusBreakdown.ContainsKey(status))
                statusBreakdown[status] = 0;
            statusBreakdown[status]++;
        }

        return Ok(new
        {
            pagesRequested = pages,
            perPageCounts,
            total,
            distinctUploadIds = allEntries.Count,
            hasMore = more,
            paginationUnsupported,
            statusBreakdown
        });
    }

    /// <summary>
    /// Get the current postprocessing run status and result of the last run.
    /// </summary>
    [HttpGet("PostprocessStatus")]
    [AllowAnonymous]
    public IActionResult GetPostprocessStatus()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        return Ok(new
        {
            running = UploadPipeline.IsPostprocessingRunning,
            startedUtc = UploadPipeline.PostprocessingStartedUtc,
            pendingTotal = UploadPipeline.PostprocessingPendingTotal,
            pendingRemaining = UploadPipeline.PostprocessingPendingRemaining,
            resolved = UploadPipeline.PostprocessingResolved,
            accepted = UploadPipeline.PostprocessingAccepted,
            rejected = UploadPipeline.PostprocessingRejected,
            deleted = UploadPipeline.PostprocessingDeleted,
            lastResult = UploadPipeline.PostprocessingLastResult
        });
    }
}
