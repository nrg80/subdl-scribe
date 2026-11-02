// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Registry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M423 (operator order 02.11.2026): adopts uploads this account already published before the plugin
/// knew about them into the registry.
/// <para>
/// The problem it solves: a season only becomes a pack when EVERY episode carries the language as an
/// <c>uploaded</c> row. Rows are only written by this plugin, so everything an earlier seeder published
/// is invisible — on 02.11.2026 the registry held no row at all for 23 of 23 Person of Interest S1
/// episodes while SubDL carried fourteen English singles for them, and For All Mankind S1..S5 (10 of 10
/// episodes each) produced no pack for the same reason. The uploads exist; the bookkeeping does not.
/// </para>
/// <para>
/// The caller (an offline pass over <c>/user/myPublishedSubtitles</c>) knows path, stream position,
/// language and SubDL id, and this endpoint writes exactly the row the uploader would have written:
/// <c>Status = uploaded</c> with the id that makes the single deletable once the pack replaces it. It
/// never touches SubDL itself and never uploads anything.
/// </para>
/// </summary>
[ApiController]
[Route("Plugins/SubdlSync")]
public class SubdlAdoptController : ControllerBase
{
    private readonly ILogger<SubdlAdoptController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlAdoptController"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public SubdlAdoptController(ILogger<SubdlAdoptController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Writes registry rows for uploads that are already published on SubDL under this account.
    /// </summary>
    /// <param name="entries">One entry per (file, stream position, language) with its SubDL id.</param>
    /// <returns>How many rows were written, and why the rest were not.</returns>
    [HttpPost("AdoptUploads")]
    [AllowAnonymous]
    public IActionResult AdoptUploads([FromBody] List<AdoptEntry>? entries)
    {
        Plugin? plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        if (entries == null || entries.Count == 0)
        {
            return BadRequest(new { error = "no entries" });
        }

        var registry = plugin.Registry;
        int adopted = 0;
        int noFile = 0;
        int noHash = 0;
        int noId = 0;
        int badPos = 0;
        var files = new HashSet<string>(StringComparer.Ordinal);

        foreach (AdoptEntry entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Language))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.SubdlId))
            {
                noId++;
                continue;
            }

            if (entry.SubPos < 0)
            {
                badPos++;
                continue;
            }

            if (!System.IO.File.Exists(entry.Path))
            {
                noFile++;
                continue;
            }

            string? mediaHash = ContentHashRegistry.ComputeMediaHash(entry.Path);
            if (string.IsNullOrWhiteSpace(mediaHash))
            {
                noHash++;
                continue;
            }

            // The id is the whole point of the row: without it the single could never be deleted when a
            // pack replaces it, and a pack would leave a duplicate behind.
            registry.MarkEmbed(
                mediaHash,
                entry.SubPos,
                entry.Language,
                entry.HearingImpaired,
                SubtitleStatus.Uploaded,
                subdlId: entry.SubdlId);
            adopted++;
            files.Add(entry.Path);
        }

        LogUtil.Normal(
            _logger,
            "[SubDL] adopt: {Adopted} uploaded row(s) written for {Files} file(s) from already published SubDL entries | missing file {NoFile} | unreadable {NoHash} | no id {NoId} | bad position {BadPos}.",
            adopted, files.Count, noFile, noHash, noId, badPos);

        return Ok(new
        {
            adopted,
            files = files.Count,
            missingFile = noFile,
            unreadable = noHash,
            missingId = noId,
            badPosition = badPos
        });
    }
}

/// <summary>
/// One already published upload, as the registry should have recorded it.
/// </summary>
/// <param name="Path">Absolute path of the media file the subtitle came from.</param>
/// <param name="SubPos">ffmpeg subtitle stream position (0:s:N) inside that file.</param>
/// <param name="Language">SubDL language code.</param>
/// <param name="SubdlId">SubDL id of the published single.</param>
/// <param name="HearingImpaired">True when the published single is the SDH/HI variant.</param>
public sealed record AdoptEntry(
    string Path,
    int SubPos,
    string Language,
    string SubdlId,
    bool HearingImpaired = false);
