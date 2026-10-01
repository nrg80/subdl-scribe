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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M70 (user decision 09.09.2026 "show API-key usage in the general menu"):
/// plugin API endpoint that reports the live SubDL account quota
/// (requestUsage / downloadUsage) to the config page. The endpoint proxies
/// the SubDL /api/v1/me call SERVER-SIDE so the API key never travels to the
/// browser. Verified live 09.09.2026: /api/v1/me does NOT consume request
/// quota (two consecutive calls, counter unchanged at 2125/2000) — the config
/// page can query freely. Answers are cached for 5 minutes per process
/// (anti-hammering; the panel view does not need second-freshness).
/// Route: GET /Plugins/{pluginId}/Quota  (plugin controller route prefix).
/// </summary>
[ApiController]
[Route("Plugins/[controller]/[action]")]
public class SubdlQuota : ControllerBase
{
    private readonly ILogger<SubdlQuota> _logger;

    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });

    // F-M70: 5-minute cache — repeated page loads/reloads don't hit SubDL at all.
    // Key + fetch time travel WITH the cache so (a) a changed API key never shows
    // the old key's numbers (cache bypass on key change, user decision 09.09.2026)
    // and (b) the UI can display when the data was last fetched.
    private static (DateTime ExpiresUtc, DateTime FetchedUtc, string Key, JsonElement Body)? _cached;
    private static readonly object CacheLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlQuota"/> class.
    /// </summary>
    public SubdlQuota(ILogger<SubdlQuota> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// GET /Plugins/SubdlQuota/Get — live quota for the configured API key.
    /// </summary>
    /// <returns>JSON: { requestUsage:{today,total,limit}, downloadUsage:{…}, cached:bool } or { error }.</returns>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        string? key = Plugin.Instance?.Configuration.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return Ok(new { error = "no-api-key" });
        }

        lock (CacheLock)
        {
            if (_cached.HasValue && _cached.Value.ExpiresUtc > DateTime.UtcNow
                && string.Equals(_cached.Value.Key, key, StringComparison.Ordinal))
            {
                return Ok(Wrap(_cached.Value.Body, _cached.Value.FetchedUtc, cached: true));
            }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await SharedHttp.Value
                .GetAsync($"https://api.subdl.com/api/v2/me?api_key={Uri.EscapeDataString(key)}", cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[SubDL] Quota endpoint: /me answered HTTP {Code}", (int)resp.StatusCode);
                return Ok(new { error = "subdl-http-" + (int)resp.StatusCode });
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone(); // clone out of the disposing doc

            lock (CacheLock)
            {
                _cached = (DateTime.UtcNow.AddMinutes(5), DateTime.UtcNow, key, root);
            }

            return Ok(Wrap(root, DateTime.UtcNow, cached: false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SubDL] Quota endpoint failed: {Msg}", ex.Message);
            return Ok(new { error = "unreachable" });
        }
    }

    // (15.09.2026): v2/me shape — plan + separated counters.
    // The UI renders exactly the docs structure: plan, search, downloads,
    // errors. All strings resolved server-side; UI only formats numbers.
    private static object Wrap(JsonElement me, DateTime fetchedUtc, bool cached)
    {
        string plan = "Free";
        if (me.ValueKind == JsonValueKind.Object && me.TryGetProperty("plan", out var pl)
            && pl.ValueKind == JsonValueKind.Object && pl.TryGetProperty("name", out var pn)
            && pn.ValueKind == JsonValueKind.String)
        {
            plan = pn.GetString() ?? "Free";
        }

        JsonElement search = default, downloads = default;
        if (me.ValueKind == JsonValueKind.Object && me.TryGetProperty("usage", out var us)
            && us.ValueKind == JsonValueKind.Object)
        {
            if (us.TryGetProperty("search", out var s) && s.ValueKind == JsonValueKind.Object) search = s.Clone();
            if (us.TryGetProperty("downloads", out var d) && d.ValueKind == JsonValueKind.Object) downloads = d.Clone();
        }

        // FetchedAt: ISO-8601 UTC; the UI renders it in the viewer's timezone (user decision 09.09.2026)
        return new { plan, search, downloads, cached, fetchedAt = fetchedUtc.ToString("O") };
    }
}