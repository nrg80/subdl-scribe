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

    // F-M343: /me is a SubDL call like any other, so it identifies the plugin too.
    private static readonly Lazy<HttpClient> SharedHttp = new(() => SubdlApiClient.NewHttpClient(TimeSpan.FromSeconds(20)));

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
    /// <summary>
    /// The LOCAL deferral state for the answer: every worker that has a deferred fire pending, with
    /// the moment it is due (F-M288/F-M294, reworked 03.10.2026).
    /// <para>
    /// The server counters cannot express this. They can read 3/1000 while a worker is already
    /// deferred, so a box built from them alone shows blue bars and no hint that nothing will run
    /// for the next quarter of an hour. The values come from the scheduler's pending fires — the
    /// same source the Workers list and the server log read, so all three agree.
    /// </para>
    /// <para>
    /// The user's rule (03.10.2026): "defer whenever a fire was scheduled, whichever worker". So
    /// this is not limited to the two directions — the database refresh, the OSHash refresh and
    /// postprocessing carry fires of their own and are named by their own worker name.
    /// </para>
    /// <para>
    /// The reason comes from the worker's own stored row, passed through verbatim rather than
    /// summarised: a deferral is a run-lock push just as often as it is a rate limit, and a label
    /// saying "rate limit" on a lock deferral would send the reader looking for a quota problem
    /// that does not exist.
    /// </para>
    /// </summary>
    /// <returns>The deferred workers, each with its short name, cause and due moment; empty when nothing is deferred.</returns>
    private static object LocalRateLimitState()
    {
        var entries = new System.Collections.Generic.List<object>();
        try
        {
            var coord = Jellyfin.Plugin.SubdlScribe.ScheduledTasks.SubdlSchedulerCoordinator.Instance;
            if (coord == null)
            {
                return new { deferred = entries };
            }

            var fires = coord.GetPendingFires();
            if (fires.Count == 0)
            {
                // Nothing pending: the line stays empty. A deferral whose fire is gone is over — the
                // worker already ran again, and a stale row must not paint a line for it.
                return new { deferred = entries };
            }

            var registry = Jellyfin.Plugin.SubdlScribe.Plugin.Instance?.WorkerRuns;

            // Display order follows the Workers list, so the line reads in the same sequence as the
            // lamps above it. A worker with a fire but no known name still appears — under its key.
            foreach (var worker in Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.KnownWorkers)
            {
                if (!fires.TryGetValue(worker.Key, out var due))
                {
                    continue;
                }

                var reason = string.Empty;
                var row = registry?.Get(worker.Key);
                if (row != null && string.Equals(row.Outcome,
                        Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.Outcome.Deferred,
                        StringComparison.Ordinal))
                {
                    reason = row.Detail ?? string.Empty;
                }

                entries.Add(new
                {
                    name = worker.Name,
                    key = worker.Key,
                    reason,
                    due = due.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                });
            }
        }
        catch
        {
            // The plugin may not be fully started (page requested during a restart) — the line then
            // simply carries no note; it must never fail the quota call itself.
        }

        return new { deferred = entries };
    }

    /// <summary>
    /// The stored outcome and detail of one worker row, or empty strings when it has none.
    /// </summary>
    private static (string? Outcome, string? Detail) ReadWorker(Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry registry, string key)
    {
        var row = registry.Get(key);
        return (row?.Outcome, row?.Detail);
    }

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
        return new { plan, search, downloads, cached, local = LocalRateLimitState(), fetchedAt = fetchedUtc.ToString("O") };
    }
}