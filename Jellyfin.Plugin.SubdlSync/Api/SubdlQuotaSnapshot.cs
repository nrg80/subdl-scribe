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
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M238: the two SubDL allowances, read from <c>GET /api/v2/me</c> — the same
/// numbers the settings page shows, so GUI and pipeline can never disagree.
/// <para>
/// Search and download are SEPARATE allowances with separate limits. A run can be
/// out of downloads while search has plenty left (and the reverse), so every decision
/// names the direction it means instead of asking "is the quota gone".
/// </para>
/// <para>
/// <see cref="ResetAtUtc"/> is the server's own reset timestamp. It is the floor for a
/// next-day fire: the retry must never be scheduled before it, because before it the
/// allowance is still spent and the fire would only burn another request.
/// </para>
/// </summary>
public sealed class SubdlQuotaSnapshot
{
    /// <summary>Gets or sets the search requests used today.</summary>
    public int SearchUsed { get; set; }

    /// <summary>Gets or sets the daily search request limit.</summary>
    public int SearchLimit { get; set; }

    /// <summary>Gets or sets the remaining search requests.</summary>
    public int SearchRemaining { get; set; }

    /// <summary>Gets or sets the subtitle downloads used today.</summary>
    public int DownloadsUsed { get; set; }

    /// <summary>Gets or sets the daily download limit.</summary>
    public int DownloadsLimit { get; set; }

    /// <summary>Gets or sets the remaining downloads.</summary>
    public int DownloadsRemaining { get; set; }

    /// <summary>Gets or sets the server-reported reset of both allowances, in UTC.</summary>
    public DateTime? ResetAtUtc { get; set; }

    /// <summary>
    /// Gets a value indicating whether the search allowance is spent. Falls back to
    /// comparing used against limit when the endpoint sends no explicit remaining value,
    /// so a missing field cannot read as "plenty left".
    /// </summary>
    public bool SearchExhausted => SearchLimit > 0 && (SearchRemaining <= 0 || SearchUsed >= SearchLimit);

    /// <summary>
    /// Gets a value indicating whether the subtitle-download allowance is spent — the
    /// counter that stops a download run, measured independently of search.
    /// </summary>
    public bool DownloadsExhausted => DownloadsLimit > 0 && (DownloadsRemaining <= 0 || DownloadsUsed >= DownloadsLimit);

    /// <summary>
    /// Reports whether the counters for a direction were actually present. A response
    /// without them is not "no quota left" and not "quota available" — it is unknown, and
    /// the caller must not treat it as permission to keep spending.
    /// </summary>
    /// <param name="forDownload">True for the download counter, false for search.</param>
    /// <returns>True when the direction's limit was part of the response.</returns>
    public bool IsKnown(bool forDownload) => forDownload ? DownloadsLimit > 0 : SearchLimit > 0;

    /// <summary>
    /// Parses the <c>/api/v2/me</c> body. Missing sections yield zeroed counters, which
    /// <see cref="SearchExhausted"/>/<see cref="DownloadsExhausted"/> treat as "not
    /// exhausted" only when no limit is known at all — the caller keeps its conservative
    /// fallback for that case.
    /// </summary>
    /// <param name="json">Response body.</param>
    /// <returns>Snapshot, or null when the body is not a usable quota object.</returns>
    public static SubdlQuotaSnapshot? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var snap = new SubdlQuotaSnapshot();

            // The counters sit under "usage" on v2; accept the flat legacy shape too,
            // because the endpoint decides the shape and a silent zero would be
            // indistinguishable from an exhausted account.
            var usage = root.TryGetProperty("usage", out var us) && us.ValueKind == JsonValueKind.Object
                ? us
                : root;

            if (usage.TryGetProperty("search", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                snap.SearchUsed = Int(s, "used");
                snap.SearchLimit = Int(s, "limit");
                snap.SearchRemaining = Int(s, "remaining");
                snap.ResetAtUtc = Date(s, "reset_at") ?? snap.ResetAtUtc;
            }

            if (usage.TryGetProperty("downloads", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                snap.DownloadsUsed = Int(d, "used");
                snap.DownloadsLimit = Int(d, "limit");
                snap.DownloadsRemaining = Int(d, "remaining");
                snap.ResetAtUtc = Date(d, "reset_at") ?? snap.ResetAtUtc;
            }

            return snap;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Int(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            return 0;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return n;
        }

        // A counter sent as a string is still a counter — parse it rather than
        // reporting zero, which would read as "spent" in the comparisons above.
        if (v.ValueKind == JsonValueKind.String
            && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
        {
            return p;
        }

        return 0;
    }

    private static DateTime? Date(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTime.TryParse(
            v.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var dt)
            ? dt
            : null;
    }
}
