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

namespace Jellyfin.Plugin.SubdlScribe.Api;

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