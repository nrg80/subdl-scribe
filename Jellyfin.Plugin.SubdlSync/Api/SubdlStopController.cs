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
using System.IO;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SIO = System.IO;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// (20.09.2026): stop signal via marker files. The GUI writes a file;
/// the pipeline checks it before every item and stops itself.
/// </summary>
[ApiController]
[Route("Plugins/SubdlSync")]
public class SubdlStopController : ControllerBase
{
    private readonly ILogger<SubdlStopController> _logger;

    public SubdlStopController(ILogger<SubdlStopController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Request a stop for the given direction. Creates a marker file that the
    /// pipeline will see and stop itself.
    /// </summary>
    [HttpPost("Stop")]
    [AllowAnonymous]
    public IActionResult RequestStop([FromQuery] string direction)
    {
        try
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return StatusCode(503, new { error = "Plugin instance not available" });
            }

            var dataDir = plugin.DataFolderPath;
            SIO.Directory.CreateDirectory(dataDir);
            int created = 0;
            if (string.Equals(direction, "upload", StringComparison.OrdinalIgnoreCase) || string.Equals(direction, "all", StringComparison.OrdinalIgnoreCase))
            {
                SIO.File.WriteAllText(SIO.Path.Combine(dataDir, ".stop-upload"), DateTime.UtcNow.ToString("O"));
                created++;
            }

            if (string.Equals(direction, "download", StringComparison.OrdinalIgnoreCase) || string.Equals(direction, "all", StringComparison.OrdinalIgnoreCase))
            {
                SIO.File.WriteAllText(SIO.Path.Combine(dataDir, ".stop-download"), DateTime.UtcNow.ToString("O"));
                created++;
            }

            if (created == 0)
            {
                return BadRequest(new { error = "direction must be upload, download or all" });
            }

            LogUtil.Normal(_logger, "[SubDL] Stop marker created for {Direction}.", direction);
            ScheduledTasks.SubdlEventDispatcher.Instance?.RequestUserStop($"stop marker {direction}");
            return Ok(new { ok = true, direction });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubDL] failed to create stop marker");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel (remove) stop markers.
    /// </summary>
    [HttpPost("Resume")]
    [AllowAnonymous]
    public IActionResult ClearStop([FromQuery] string direction)
    {
        try
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return StatusCode(503, new { error = "Plugin instance not available" });
            }

            var dataDir = plugin.DataFolderPath;
            int removed = 0;
            if (string.Equals(direction, "upload", StringComparison.OrdinalIgnoreCase) || string.Equals(direction, "all", StringComparison.OrdinalIgnoreCase))
            {
                var path = SIO.Path.Combine(dataDir, ".stop-upload");
                if (SIO.File.Exists(path)) { SIO.File.Delete(path); removed++; }
            }

            if (string.Equals(direction, "download", StringComparison.OrdinalIgnoreCase) || string.Equals(direction, "all", StringComparison.OrdinalIgnoreCase))
            {
                var path = SIO.Path.Combine(dataDir, ".stop-download");
                if (SIO.File.Exists(path)) { SIO.File.Delete(path); removed++; }
            }

            return Ok(new { ok = true, removed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubDL] failed to clear stop marker");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
