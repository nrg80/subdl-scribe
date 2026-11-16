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

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// Result of a single subtitle upload attempt.
/// </summary>
public class UploadResult
{
    /// <summary>Gets a value indicating whether the upload succeeded.</summary>
    public bool Ok { get; private init; }

    /// <summary>Gets a value indicating whether the item was skipped (with reason).</summary>
    public bool SkippedItem { get; private init; }

    /// <summary>Gets the skip or error reason.</summary>
    public string? Reason { get; private init; }

    /// <summary>
    /// Optional raw response payload from the SubDL uploadSubtitle call.
    /// </summary>
    public System.Text.Json.JsonDocument? Response { get; init; }

    /// <summary>
    /// F-M488: the id the SOURCE gave our upload, when it gave one. SubDL fills it from its response,
    /// the second source from the read-back of its episode page. Null means "no id known" — it is never
    /// guessed, because the store would then vouch for a row that does not exist.
    /// </summary>
    public string? SourceId { get; init; }

    /// <summary>
    /// F-M550: the source that actually answered. The log used to name no source at all, and the old
    /// <c>[SubDL]</c> prefix in front of an upload line read like "uploaded to SubDL" even when the
    /// upload went to tvsubtitles. Set by the chain where the source is known, not by its caller.
    /// </summary>
    public Configuration.SubtitleProvider? Source { get; set; }

    /// <summary>
    /// Creates a success result carrying the source's own id for the upload.
    /// </summary>
    /// <param name="sourceId">The id the source reported, or null.</param>
    /// <returns>The result.</returns>
    public static UploadResult Success(string? sourceId) => new() { Ok = true, SourceId = sourceId };

    /// <summary>
    /// Creates a success result.
    /// </summary>
    public static UploadResult Success() => new() { Ok = true };

    /// <summary>
    /// Creates a success result with the raw SubDL response attached.
    /// </summary>
    public static UploadResult Success(System.Text.Json.JsonDocument? response) => new() { Ok = true, Response = response };

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    public static UploadResult Failed(string reason) => new() { Ok = false, Reason = reason };

    /// <summary>
    /// Creates a skip result.
    /// </summary>
    public static UploadResult Skipped(string reason) => new() { Ok = false, SkippedItem = true, Reason = reason };
}