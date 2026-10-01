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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// SubDL API v3 client (F-M8, F-M9, F-M19). Login → Bearer token; upload is a 3-step flow
/// (getNId → uploadSingleSubtitle → uploadSubtitle).
/// Credentials are never logged (F-M24b).
/// </summary>
public sealed class SubdlApiClient
{
    // F-M63 (user decision 09.09.2026 "official endpoint"): the official documented API
    // host is https://api.subdl.com (subdl.com/api-doc lists it for search, upload and /me;
    // api3.subdl.com appears NOWHERE in the docs — it is an undocumented mirror).
    private const string ApiBase = "https://api.subdl.com";
    private readonly HttpClient _http;
    private string? _token;
    private DateTime _tokenObtained;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlApiClient"/> class.
    /// </summary>
    /// <param name="http">HttpClient instance.</param>
    public SubdlApiClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Event raised (message only) when a token needs to be logged — never credentials.</summary>
    public event Action<string>? Log;

    /// <summary>
    /// F-M24a (fixed 09.09.2026, user decision): debug trace per API round-trip —
    /// EVERY search, download, upload, login call with method + status. The
    /// pipelines forward these to ILogger at LogLevel.Debug. Message only; URLs are
    /// stripped of the api_key parameter before logging (F-M24b).
    /// </summary>
    public event Action<string>? DebugTrace;

    /// <summary>Event raised on every completed API round-trip (watchdog heartbeat source, NF-4).</summary>
    public event Action? ApiActivity;

    /// <summary>
    /// Set when the API hit a server-side rate limit (HTTP 429) that backoff cannot
    /// clear — callers should STOP the run instead of burning the candidate list.
    /// </summary>
    public bool ServerRateLimited { get; private set; }

    /// <summary>
    /// F-M72 (09.09.2026): set when the upload hit the 500-per-hour cap — SubDL
    /// reports it as HTTP 200 + status:false + "…500 subtitles per hour…" message.
    /// Not an error: the item stays due; the pipeline stops the run and a later
    /// run continues with the fresh hourly budget.
    /// </summary>
    public bool HourlyUploadCapHit { get; private set; }

    /// <summary>
    /// F-M49 (user decision 08.09.2026 evening "which messages to react to"): set when
    /// SubDL answers 403 "not_authorized" — the API key is invalid/revoked. Waiting or
    /// retrying is pointless: pipelines must stop IMMEDIATELY with a clear
    /// "check credentials" error, not treat it as a rate/limit problem.
    /// </summary>
    public bool AuthBroken { get; private set; }



    /// <summary>
    /// F-M49: server-reported UTC time when the daily download quota resets
    /// (x-ratelimit-reset header preferred, retry-after seconds as fallback).
    /// Null when the 429 response carried no usable reset information.
    /// </summary>
    public DateTime? RateLimitResetUtc { get; private set; }

    /// <summary>
    /// F-M58 (09.09.2026): true when the last 429 was a TRANSIENT server
    /// overload (body error "service_busy" with retryAfterSeconds), NOT the
    /// account daily limit. Callers should wait the short retryAfterSeconds
    /// window and continue the run instead of aborting it. Verified live
    /// 09.09.2026: 12 fast search calls → 429 {"error":"service_busy",
    /// "retryAfterSeconds":5} while the panel showed 24/2000 requests —
    /// the "daily limit" interpretation was wrong for this variant.
    /// </summary>
    public bool TransientOverload { get; private set; }

    /// <summary>
    /// F-M58: seconds to wait before the next call after a transient-overload
    /// 429 (server-reported retryAfterSeconds, default 5 when absent).
    /// </summary>
    public int TransientRetrySeconds { get; private set; }

    // ==================== F-M152 (rev.10) — v2 rate state ====================
    // JEDER v2-Response traegt x-ratelimit-limit/remaining/reset (live
    // verifiziert 17.09.2026). Diese Props halten den letzten Stand —
    // kein Extra-Call noetig. /me wird NICHT mehr angerufen.

    /// <summary>F-M152: last v2 x-ratelimit-limit value (API daily limit, 2000).</summary>
    public int? V2RateLimit { get; private set; }

    /// <summary>F-M152: last v2 x-ratelimit-remaining value (live counter).</summary>
    public int? V2RateRemaining { get; private set; }

    /// <summary>F-M152: last v2 x-ratelimit-reset timestamp (exact server reset).</summary>
    public DateTime? V2RateResetUtc { get; private set; }

    /// <summary>
    /// F-M152: true when the last 429 carried NO x-ratelimit-* headers
    /// (Cloudflare/Edge 429 — server-level overload, route-independent).
    /// Such a 429 is NOT the daily quota: fail-safe anchor = next 00:00 UTC.
    /// </summary>
    public bool Edge429NoHeaders { get; private set; }

    /// <summary>
    /// F-M152: parses x-ratelimit-* headers from ANY response (v2 responses
    /// carry them on every call, including 429 and 200+status:false).
    /// Returns true when at least one rate header was present.
    /// </summary>
    public bool ObserveRateHeaders(HttpResponseMessage resp)
    {
        bool any = false;
        string? lim = resp.Headers.TryGetValues("x-ratelimit-limit", out var limV) ? limV.FirstOrDefault() : null;
        string? rem = resp.Headers.TryGetValues("x-ratelimit-remaining", out var remV) ? remV.FirstOrDefault() : null;
        string? rst = resp.Headers.TryGetValues("x-ratelimit-reset", out var rstV) ? rstV.FirstOrDefault() : null;
        if (int.TryParse(lim, out int limI) && limI >= 0)
        {
            V2RateLimit = limI; any = true;
        }
        if (int.TryParse(rem, out int remI) && remI >= 0)
        {
            V2RateRemaining = remI; any = true;
        }
        if (!string.IsNullOrWhiteSpace(rst)
            && DateTime.TryParse(rst, null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime rstDt))
        {
            V2RateResetUtc = rstDt; any = true;
        }
        if (any)
        {
            LogInfo($"[SubDL] rate headers - limit: {lim ?? "-"} | remaining: {rem ?? "-"} | reset: {rst ?? "-"}");
        }
        return any;
    }

    /// <summary>
    /// F-M49: clears the rate-limit flag after the caller successfully waited out
    /// the daily-limit window — the next download starts with a clean slate.
    /// Also clears the F-M58 transient-overload markers.
    /// </summary>
    public void ResetRateLimitFlag()
    {
        ServerRateLimited = false;
        RateLimitResetUtc = null;
        TransientOverload = false;
        TransientRetrySeconds = 0;
    }

    /// <summary>
    /// F-M72: clears the hourly-upload-cap flag so the next run/continuation
    /// starts with a clean slate.
    /// </summary>
    public void ResetHourlyCapFlag()
    {
        HourlyUploadCapHit = false;
    }

    /// <summary>
    /// F-M58 (09.09.2026): classifies a 429 response. SubDL uses TWO different
    /// 429 variants: (a) the real daily quota limit and (b) a transient server
    /// overload with body {"error":"service_busy","message":"Temporarily
    /// overloaded, retry shortly","retryAfterSeconds":5} — verified live
    /// 09.09.2026 while the panel showed only 24/2000 requests used. Variant
    /// (b) must NOT stop the run: wait retryAfterSeconds and continue.
    /// Sets TransientOverload/TransientRetrySeconds for variant (b), and
    /// ServerRateLimited/RateLimitResetUtc for variant (a).
    /// </summary>
    /// <param name="resp">The 429 response (body already read into <paramref name="body"/>).</param>
    /// <param name="body">The response body text (never logged verbatim here).</param>
    private void Classify429(HttpResponseMessage resp, string body)
    {
        ServerRateLimited = true;
        TransientOverload = false;
        TransientRetrySeconds = 0;
        Edge429NoHeaders = false;

        // Log the raw 429 once per occurrence.
        string? rawReset = resp.Headers.TryGetValues("x-ratelimit-reset", out var rr) ? rr.FirstOrDefault() : null;
        string? rawRetry = resp.Headers.TryGetValues("retry-after", out var ry) ? ry.FirstOrDefault() : null;
        LogInfo($"[SubDL] 429 raw response - body: {body} | x-ratelimit-reset: {rawReset ?? "absent"} | retry-after: {rawRetry ?? "absent"}");

        // F-M152 (rev.10): ALWAYS observe rate headers first - the v2 middleware
        // populates them on 429 as well. Header present -> authoritative quota
        // diagnosis (exact reset). Headers absent -> Edge 429 (server-level
        // overload, route-independent): NOT the daily quota. No extra diagnostic
        // call (/me removed) - fail-safe anchor = next 00:00 UTC (pipeline maps
        // Edge429NoHeaders=true to that).
        bool headers = ObserveRateHeaders(resp);
        DateTime? hdrReset = ParseDailyLimitReset(resp);

        int retrySec = 0;
        bool bodyServiceBusy = false;
        bool bodyRateLimit = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errEl)
                && errEl.ValueKind == JsonValueKind.String)
            {
                string? errName = errEl.GetString();

                // F-M238: SubDL answers a short-term trip as {"error":"rate_limit",
                // "message":"Rate Limit","retryAfterSeconds":22} — measured live with
                // 613/2000 requests used and 0/50 downloads used, i.e. NOT the daily
                // allowance. It must be respaced, never turned into a day-long stop.
                if (string.Equals(errName, "rate_limit", StringComparison.OrdinalIgnoreCase))
                {
                    bodyRateLimit = true;
                    retrySec = 30; // used only when the body names nothing
                }
                else if (string.Equals(errName, "service_busy", StringComparison.OrdinalIgnoreCase))
                {
                    bodyServiceBusy = true;
                    retrySec = 5; // verified default
                }

                if (bodyRateLimit || bodyServiceBusy)
                {
                    if (doc.RootElement.TryGetProperty("retryAfterSeconds", out var rEl)
                        && rEl.ValueKind == JsonValueKind.Number)
                    {
                        int reported = rEl.GetInt32();
                        if (reported > 0 && reported <= 300)
                        {
                            retrySec = reported;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON 429 body (e.g. Cloudflare HTML). Handled below via headers.
        }

        if (headers && hdrReset != null)
        {
            // Variant (a): authoritative daily-limit 429 - exact reset from headers.
            RateLimitResetUtc = hdrReset;
            return;
        }

        if (bodyServiceBusy)
        {
            // Variant (b): transient overload (service_busy) - short retry window.
            // (F-M58 semantics kept: in-run retry, no recovery fire.)
            TransientOverload = true;
            TransientRetrySeconds = retrySec > 0 ? retrySec : 5;
            return;
        }

        if (bodyRateLimit)
        {
            // Variant (c) F-M238: short-term rate limit, NOT the daily allowance.
            // Treated exactly like the overload variant: wait the server-reported
            // window and continue. Which of the two it is cannot be read from the
            // status code alone - the caller confirms against the live counters
            // before it stops anything (SubdlApiClient.ReadQuotaAsync).
            TransientOverload = true;
            TransientRetrySeconds = retrySec > 0 ? retrySec : 30;
            return;
        }

        // F-M152 (rev.10): no rate headers, no service_busy -> Edge 429
        // (Cloudflare/server-level). NOT the daily quota, NOT transient-continuable:
        // the run stops and the pipeline fail-safes the anchor to next 00:00 UTC
        // (+30..300 min jitter). No extra diagnostic call into the overloaded API.
        Edge429NoHeaders = true;
        TransientRetrySeconds = 30;
    }

    /// <summary>
    /// Gets the live account quota as last observed (F-M238), or null when it has
    /// never been read. Read on demand from the same endpoint the settings page
    /// uses (<c>GET /api/v2/me</c>, <c>usage.search</c> / <c>usage.downloads</c>), so the
    /// pipeline can decide from the NUMBERS instead of guessing from a status code.
    /// </summary>
    public SubdlQuotaSnapshot? Quota { get; private set; }

    /// <summary>
    /// F-M238: reads the two counters the 429 decision needs and reports what they say
    /// about the direction that just hit a 429.
    /// <para>
    /// The status code alone cannot answer this: SubDL answers 429 both when the daily
    /// allowance is gone AND when a short-term rate limit tripped while plenty of quota
    /// remained. The counters are the only thing that tells the two apart, and the
    /// <c>reset_at</c> they carry is the timestamp a next-day fire must not undercut.
    /// </para>
    /// <para>
    /// "Unreadable" is returned as its own answer rather than folded into "not exhausted",
    /// because the two call for opposite reactions: an unknown allowance retries at the
    /// day-long pace, an available one is ridden out within the hour.
    /// </para>
    /// </summary>
    /// <param name="forDownload">True for the download counter, false for search/upload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the counters say about this direction.</returns>
    public async Task<QuotaRead> ReadQuotaAsync(bool forDownload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            return QuotaRead.Unreadable;
        }

        try
        {
            var url = $"{ApiBase}/api/v2/me?api_key={Uri.EscapeDataString(ApiKey)}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LogInfo($"[SubDL] quota read: /me answered HTTP {(int)resp.StatusCode} — allowance unknown, day-long pace.");
                return QuotaRead.Unreadable;
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var snap = SubdlQuotaSnapshot.FromJson(body);
            if (snap == null || !snap.IsKnown(forDownload))
            {
                LogInfo("[SubDL] quota read: response carries no usable limit — allowance unknown, day-long pace.");
                return QuotaRead.Unreadable;
            }

            Quota = snap;

            // F-M238: the server's own reset timestamp becomes the anchor for the
            // next-day fire. Jitter is added ON TOP of it, so the retry can never land
            // before the allowance is actually back. A response that carries no reset
            // leaves the previous anchor untouched rather than inventing one.
            if (snap.ResetAtUtc.HasValue)
            {
                RateLimitResetUtc = snap.ResetAtUtc.Value;
                Edge429NoHeaders = false;
            }

            LogInfo($"[SubDL] quota read: search {snap.SearchUsed}/{snap.SearchLimit} (remaining {snap.SearchRemaining}), "
                + $"downloads {snap.DownloadsUsed}/{snap.DownloadsLimit} (remaining {snap.DownloadsRemaining}), "
                + $"reset {snap.ResetAtUtc:yyyy-MM-dd HH:mm} UTC.");

            bool spent = forDownload ? snap.DownloadsExhausted : snap.SearchExhausted;
            return spent ? QuotaRead.Spent : QuotaRead.Available;
        }
        catch (Exception ex)
        {
            // Any failure here must not decide anything: keep the day-long pace.
            LogInfo($"[SubDL] quota read failed ({ex.GetType().Name}) — allowance unknown, day-long pace.");
            return QuotaRead.Unreadable;
        }
    }

    /// <summary>Gets or sets the SubDL username (F-M19).</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the SubDL password (F-M19).</summary>
    public string? Password { get; set; }

    /// <summary>Gets or sets the SubDL API key — preferred if set (F-M19).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets the SubDL user-agent used for all requests.</summary>
    public string UserAgent { get; set; } = "Jellyfin.Plugin.SubdlSync/1.0";

    private void LogInfo(string msg) => Log?.Invoke(msg);

    /// <summary>
    /// F-M235: pushes a message into this client's log channel from the outside. The transient-retry
    /// handler is constructed before the client exists and cannot know the run's log sink, so the
    /// bundle wires the handler's callback to this method and hidden retries stay visible.
    /// </summary>
    /// <param name="msg">Message to log.</param>
    public void RaiseLog(string msg) => LogInfo(msg);

    /// <summary>
    /// F-M24a debug trace: one line per API round-trip. The URL is logged WITHOUT
    /// the api_key (F-M24b — credentials never in logs).
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="url">Full request URL (api_key is stripped).</param>
    /// <param name="status">Response status code.</param>
    /// <param name="detail">Optional short detail (e.g. candidate count).</param>
    private string CleanUrl(string url)
    {
        var clean = url;
        var keyIdx = clean.IndexOf("api_key=", StringComparison.OrdinalIgnoreCase);
        if (keyIdx >= 0)
        {
            var end = clean.IndexOf('&', keyIdx);
            clean = end < 0 ? clean[..keyIdx] + "api_key=[REDACTED]" : clean[..keyIdx] + "api_key=[REDACTED]" + clean[end..];
        }
        return clean;
    }

    /// <summary>(user decision 15.09.2026): request and response are traced
    /// as TWO lines at Debug level. Never Verbose (F-M24a keeps per-API traces
    /// Debug-only; verbose has its own detail lines).</summary>
    private void TraceCall(string method, string url)
        => DebugTrace?.Invoke($"API call {method} {CleanUrl(url)}");

    private void TraceResponse(string method, string url, int status, string? detail = null)
        // Response line repeats only the path (not host+full query) —
        // the call line right above already carries the full URL.
        => DebugTrace?.Invoke(detail == null
            ? $"API response {method} {ShortUrl(url)} → {status}"
            : $"API response {method} {ShortUrl(url)} → {status} ({detail})");

    /// <summary>Host + path only, query stripped (was: full URL each line).</summary>
    private string ShortUrl(string url)
    {
        var clean = CleanUrl(url);
        var q = clean.IndexOf('?', StringComparison.Ordinal);
        return q >= 0 ? clean[..q] : clean;
    }

    private void Trace(string method, string url, int status, string? detail = null)
        => TraceResponse(method, url, status, detail);



    /// <summary>
    /// Logs in with username/password (password grants upload rights). Returns the bearer token.
    /// The API key alone is NOT sufficient for uploads — login is mandatory (verified 07.09.2026).
    /// F-M58 (09.09.2026): the login endpoint is also affected by transient server
    /// overloads — during the 09.09.2026 post-reset run it answered a 429 with a
    /// NON-JSON body ("Invalid..." plain text), which crashed the upload task with
    /// JsonReaderException. Now every call is retried up to 3 times: 429 → wait
    /// retryAfterSeconds (min 5s), non-JSON/5xx → 5s backoff. Wrong credentials
    /// (NOT_FOUND/403) still abort immediately (F-M49, unchanged).
    /// </summary>
    public async Task<string> LoginAsync(CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
        var payload = new Dictionary<string, string>
        {
            ["email"] = Username ?? string.Empty,
            ["password"] = Password ?? string.Empty,
            ["timeZone"] = "UTC"
        };
        using var content = new FormUrlEncodedContent(payload);
        using var resp = await _http.PostAsync($"{ApiBase}/login", content, ct).ConfigureAwait(false);
        ApiActivity?.Invoke();
        Trace("POST", $"{ApiBase}/login", (int)resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // F-M49 (user decision 08.09.2026 "which messages to react to"): the login
        // endpoint answers wrong credentials with PLAIN TEXT "NOT_FOUND" (HTTP 404)
        // — not JSON. Parse it as such instead of crashing with JsonException, and
        // mark auth broken so the pipelines stop with a clear message instead of
        // burning items with pointless retries.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound
            && body.Trim().Equals("NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            AuthBroken = true;
            throw new InvalidOperationException("SubDL login failed: credentials wrong (NOT_FOUND) — check username/password in the plugin config.");
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            AuthBroken = true;
            throw new InvalidOperationException("SubDL login failed: not authorized (403) — check the API key in the plugin config.");
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            Classify429(resp, body);
            if (attempt < maxAttempts)
            {
                int waitSec = TransientOverload && TransientRetrySeconds > 0 ? TransientRetrySeconds : 5;
                LogInfo($"SubDL login: transient 429 (attempt {attempt}/{maxAttempts}) — retrying in {waitSec}s.");
                await Task.Delay(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false);
                continue;
            }

            throw new InvalidOperationException("SubDL login failed: 429 persisted after retries.");
        }

        // F-M58: the overload also produces non-JSON bodies (plain-text error pages)
        // → old code crashed with JsonReaderException. Retry those like a 5xx.
        bool jsonOk = false;
        try
        {
            using var probe = JsonDocument.Parse(body);
            jsonOk = true;
        }
        catch (JsonException)
        {
            // non-JSON body
        }

        // F-M232 (28.09.2026, live 02:05-02:06): the FINAL attempt must be classified
        // too. The old guard `!jsonOk && attempt < maxAttempts` let a non-JSON body on
        // the last attempt fall through to the JsonDocument.Parse below, which threw a
        // RAW JsonException ("'I' is an invalid start of a value") straight out of
        // LoginAsync — SubDL answered three HTTP 500 text/plain overload pages and the
        // cycle died unclassified ("cycle failed") instead of deferring like every
        // other transient overload (F-M67: 3 retries, then an overload fire).
        if (!jsonOk && attempt < maxAttempts)
        {
            LogInfo($"SubDL login: non-JSON response (HTTP {(int)resp.StatusCode}, attempt {attempt}/{maxAttempts}) — retrying in 5s.");
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            continue;
        }

        if (!jsonOk)
        {
            TransientOverload = true;
            TransientRetrySeconds = 5;
            throw new InvalidOperationException(
                $"SubDL login failed: HTTP {(int)resp.StatusCode}, non-JSON body after {maxAttempts} attempts — server overloaded, deferring.");
        }

        // A JSON body behind a 5xx is still a server error, never a credentials or
        // content verdict: classify it transient so the caller defers instead of
        // reporting a config problem to the user (F-M232).
        if ((int)resp.StatusCode >= 500)
        {
            TransientOverload = true;
            TransientRetrySeconds = 5;
            throw new InvalidOperationException(
                $"SubDL login failed: HTTP {(int)resp.StatusCode} after {maxAttempts} attempts — server overloaded, deferring.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("status", out var statusEl) || statusEl.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException($"SubDL login failed: {Trim(body)}");
        }

        _token = root.GetProperty("token").GetString();
        _tokenObtained = DateTime.UtcNow;
        LogInfo("SubDL login OK");
        return _token!;
        }

        throw new InvalidOperationException("SubDL login failed: retries exhausted.");
    }



    /// <summary>
    /// Performs the 3-step upload: getNId → uploadSingleSubtitle → uploadSubtitle (F-M8).
    /// </summary>
    public async Task<UploadResult> UploadSubtitleAsync(
        string srtContent,
        string subFileName,
        string subdlLanguage,
        string releaseName,
        string? imdbId,
        string? tmdbId,
        bool isSeries,
        int season,
        int? episode,
        CancellationToken ct,
        bool hearingImpaired = false)
    {
        string token = _token ?? throw new InvalidOperationException("Not logged in (call LoginAsync first)");
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            // F-M28: IMDB is mandatory — caller must have skipped earlier
            return UploadResult.Skipped("no-imdb");
        }

        // Step 1: getNId
        using var nReq = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/user/getNId");
        nReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        TraceCall("GET", $"{ApiBase}/user/getNId");
        using var nResp = await _http.SendAsync(nReq, ct).ConfigureAwait(false);
            ApiActivity?.Invoke();
        Trace("GET", "user/getNId", (int)nResp.StatusCode);
        var nBody = await nResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? nId = null;
        try
    {
            using var nd = JsonDocument.Parse(nBody);
            if (nd.RootElement.TryGetProperty("n_id", out var nIdEl))
            {
                nId = nIdEl.GetString();
            }
        }
        catch (JsonException)
        {
            // fall through to error below
        }

        if (string.IsNullOrWhiteSpace(nId))
        {
            return UploadResult.Failed($"getNId failed: {Trim(nBody)}");
        }

        // Step 2: uploadSingleSubtitle (multipart)
        // NOTE: built MANUALLY — SubDL rejects MultipartFormDataContent output
        // ("Invalid request parameters"), only the plain requests-style format
        // with bare name= parts works.
        var srtBytes = Encoding.UTF8.GetBytes(srtContent);
        var boundary = "----Boundary" + Guid.NewGuid().ToString("N");
        var sb = new System.Text.StringBuilder();
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Disposition: form-data; name=\"n_id\"\r\n\r\n");
        sb.Append(nId).Append("\r\n");
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Disposition: form-data; name=\"subtitle\"; filename=\"").Append(subFileName).Append("\"\r\n");
        sb.Append("Content-Type: application/octet-stream\r\n\r\n");
        byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        byte[] body = new byte[head.Length + srtBytes.Length + tail.Length];
        head.CopyTo(body, 0);
        srtBytes.CopyTo(body, head.Length);
        tail.CopyTo(body, head.Length + srtBytes.Length);

        using var upReq = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/user/uploadSingleSubtitle")
        {
            Content = new ByteArrayContent(body)
        };
        upReq.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data")
        {
            Parameters = { new System.Net.Http.Headers.NameValueHeaderValue("boundary", $"\"{boundary}\"") }
        };
        upReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        TraceCall("POST", $"{ApiBase}/user/uploadSingleSubtitle");
        using var upResp = await _http.SendAsync(upReq, ct).ConfigureAwait(false);
        ApiActivity?.Invoke();
        Trace("POST", "user/uploadSingleSubtitle", (int)upResp.StatusCode);
        var upBody = await upResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? fileNId = null;
        try
        {
            using var ud = JsonDocument.Parse(upBody);
            if (ud.RootElement.TryGetProperty("file", out var fileEl) && fileEl.ValueKind == JsonValueKind.Object
                && fileEl.TryGetProperty("file_n_id", out var fni))
            {
                fileNId = fni.GetString();
            }
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrWhiteSpace(fileNId))
        {
            return UploadResult.Failed($"uploadSingleSubtitle failed: {Trim(upBody)}");
        }

        // Step 3: uploadSubtitle (metadata)
        var meta = new Dictionary<string, string>
        {
            ["file_n_ids"] = JsonSerializer.Serialize(new[] { fileNId }),
            ["n_id"] = nId,
            ["type"] = isSeries ? "tv" : "movie",
            ["name"] = Path.GetFileNameWithoutExtension(subFileName),
            ["lang"] = subdlLanguage,
            ["quality"] = "web",
            ["production_type"] = "0",
            ["releases"] = JsonSerializer.Serialize(new[] { releaseName }),
            ["framerate"] = "0",
            ["comment"] = "",
            ["season"] = season.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // F-M71: hi is no longer hardcoded — SDH/HI variants are uploaded with
            // hi=true, from the hearing-impaired detection (F-M71/F-M254).
            ["hi"] = hearingImpaired ? "true" : "false",
            ["is_full_season"] = "false"
        };
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            meta["imdb_id"] = imdbId;
        }
        if (!string.IsNullOrWhiteSpace(tmdbId))
        {
            meta["tmdb_id"] = tmdbId;
        }
        if (episode.HasValue)
        {
            meta["ef"] = episode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            meta["ee"] = episode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        using var metaReq = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/user/uploadSubtitle")
        {
            Content = new FormUrlEncodedContent(meta)
        };
        metaReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        TraceCall("POST", $"{ApiBase}/user/uploadSubtitle");
        using var metaResp = await _http.SendAsync(metaReq, ct).ConfigureAwait(false);
        ApiActivity?.Invoke();
        Trace("POST", "user/uploadSubtitle", (int)metaResp.StatusCode);
        var metaBody = await metaResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // F-M72 (user decision 09.09.2026 "500/h limit"): the upload hourly cap does NOT
        // come as 429 — SubDL answers HTTP 200 with status:false and a message body
        // ("Upload limit reached (500 subtitles per hour). Please wait and continue
        // later — your account can keep uploading next hour."). The seeder treats
        // 'limit' in the message as a run stop + requeue.
        // A real 429 still goes through Classify429 (daily_limit vs service_busy).
        if ((int)metaResp.StatusCode == 429)
        {
            Classify429(metaResp, metaBody);
            return UploadResult.Failed("rate-limited");
        }

        bool isHourlyUploadCap = false;
        bool serverDuplicate = false; // Fix 13.09.2026: SubDL duplicate verdict
        bool ok;
        try
        {
            using var md = JsonDocument.Parse(metaBody);
            ok = md.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.True;
            if (!ok && md.RootElement.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            {
                string msg = msgEl.GetString() ?? string.Empty;
                if (msg.Contains("500 subtitles per hour", StringComparison.OrdinalIgnoreCase)
                    || (msg.Contains("limit", StringComparison.OrdinalIgnoreCase) && msg.Contains("hour", StringComparison.OrdinalIgnoreCase)))
                {
                    isHourlyUploadCap = true;
                }
                // Fix 13.09.2026 (user decision, Unicorn Academy case): SubDL
                // discards duplicates server-side — recognize the verdict so the
                // caller settles the position instead of counting a fake upload.
                if (msg.Contains("already exist", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("identical file already published", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
                {
                    serverDuplicate = true;
                }
            }
        }
        catch (JsonException)
        {
            ok = false;
        }

        if (isHourlyUploadCap)
        {
            // Not a failure — the item stays due and the run stops cleanly.
            HourlyUploadCapHit = true;
            return UploadResult.Skipped("upload-hourly-cap");
        }

        if (serverDuplicate)
        {
            // Definitive verdict — the subtitle is already on SubDL. Skip (not
            // fail): the caller settles the position, nothing to retry.
            return UploadResult.Skipped("duplicate-content");
        }

        if (!ok)
        {
            return UploadResult.Failed($"uploadSubtitle failed: {Trim(metaBody)}");
        }

        System.Text.Json.JsonDocument? responseDoc = null;
        try
        {
            responseDoc = System.Text.Json.JsonDocument.Parse(metaBody);
        }
        catch (System.Text.Json.JsonException)
        {
            responseDoc = null;
        }

        return UploadResult.Success(responseDoc);
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];

    /// <summary>
    /// Searches SubDL for subtitle candidates (F-M41/F-M45, F-M17a).
    /// GET /api/v1/subtitles with api_key + imdb_id/tmdb_id (hard mode) or film_name (soft mode)
    /// AND the verified server-side filters season_number, episode_number, languages.
    /// All filters are STRICT (user decision 08.09.2026 — no fallbacks): what the API
    /// returns is authoritative. Search errors still fail-open (F-M17d).
    /// </summary>
    /// <param name="imdbId">IMDb ID (series IMDB for TV) — preferred.</param>
    /// <param name="tmdbId">TMDB fallback id.</param>
    /// <param name="filmName">Title for the risky soft-mode search (F-M45, only used when ids are absent).</param>
    /// <param name="season">Season (TV) or 0.</param>
    /// <param name="episode">Episode number (TV) or 0 — sent as episode_number.</param>
    /// <param name="languages">Comma-separated SubDL language codes (EN, DE...) or null/empty for all.</param>
    /// <param name="maxPages">Page cap (safety, default 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="maxCandidatesPerLanguage">Early-stop threshold per language; 0 disables it.</param>
    /// <param name="hearingImpaired">F-M241: when set, the server returns ONLY that side of the HI split (`&amp;hi=1` / `&amp;hi=0`); null leaves it unfiltered.</param>
    /// <returns>List of candidates; empty when nothing found or the search failed (fail-open).</returns>
    public async Task<List<SubtitleCandidate>?> SearchSubtitlesAsync(string? imdbId, string? tmdbId, string? filmName, int season, int episode, string? languages, int maxPages, CancellationToken ct, int maxCandidatesPerLanguage = 0, bool? hearingImpaired = null)
    {
        var result = new List<SubtitleCandidate>();
        string apiKey = ApiKey ?? throw new InvalidOperationException("Search requires the SubDL API key");

        int page = 1;
        while (page <= Math.Max(1, maxPages))
        {
            string url = $"{ApiBase}/api/v1/subtitles?api_key={Uri.EscapeDataString(apiKey)}";
            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                url += $"&imdb_id={Uri.EscapeDataString(imdbId)}";
            }
            else if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                url += $"&tmdb_id={Uri.EscapeDataString(tmdbId)}";
            }
            else if (!string.IsNullOrWhiteSpace(filmName))
            {
                // F-M45 soft mode: risky title-based search (only when hard match disabled)
                url += $"&film_name={Uri.EscapeDataString(filmName)}";
            }
            else
            {
                return result; // nothing to search by
            }

            // (19.09.2026): SubDL requires "type=tv|movie" for TMDB-id
            // matching (live: tmdb_id without type returns status:false/0, with
            // type=tv → 10 hits). season>0 ⇒ TV, else movie.
            url += season > 0 ? "&type=tv" : "&type=movie";

            if (season > 0)
            {
                url += $"&season_number={season}";
            }

            if (episode > 0)
            {
                // F-M17a fix (08.09.2026): narrow to this episode — without it a season
                // packs ~40 languages x 10 episodes = ~400 candidates and the page cap
                // hides matching releases on later pages (verified: dups on page 39/40).
                url += $"&episode_number={episode}";
            }

            if (!string.IsNullOrWhiteSpace(languages))
            {
                // Server-side language filter (verified 08.09.2026: parameter name is
                // "languages", comma-separated, case-insensitive — "language"/"lang" are ignored).
                url += $"&languages={Uri.EscapeDataString(languages)}";
            }

            // F-M241 (user decision 28.09.2026): the HI split is a SERVER-side filter, verified
            // live against /api/v1/subtitles. Without &hi the API answers a MIXTURE (measured on
            // tt0448694: 30 releases, 11 regular + 19 hearing-impaired), so the caller's ranking
            // had to sort the two apart from one list and could only ever use what the mix
            // happened to contain. Asking twice returns each side's own pool — measured: 13
            // regular, 30 HI, and the two share ZERO releases, so the union (43) is larger than
            // the mixture (30). hi=0 and "no parameter" are NOT the same request: 13 vs 30.
            if (hearingImpaired.HasValue)
            {
                url += hearingImpaired.Value ? "&hi=1" : "&hi=0";
            }

            // F-M215: ask SubDL to unpack season/range packs. With unpack=1 every
            // candidate also carries "unpack_files" — the list of the individual
            // episode files inside the archive, each with its own season/episode and
            // a ready single-file url. Without it a pack is opaque, and the ZIP path
            // can only take the FIRST .srt — which is how one episode's subtitle
            // ended up written next to six different episodes (26.09.2026).
            url += "&unpack=1";

            url += $"&page={page}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", UserAgent);
            TraceCall("GET", url);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            ApiActivity?.Invoke();
            Trace("GET", url, (int)resp.StatusCode, $"page {page}");

            // F-M48 fix (08.09.2026): the search previously ignored the HTTP status —
            // a 429 body {"status": false} fell into the fail-open path and silently
            // returned "no candidates". null = search did not complete (429/5xx/403);
            // empty list = search completed, genuinely no candidates.
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                Classify429(resp, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); // F-M58
                return null;
            }

            // F-M49 (user decision 08.09.2026): 403 = key invalid/revoked — no retry,
            // no wait; the caller must abort the run and tell the user to check
            // credentials. Verified live: {"error":"not_authorized","message":"Not Authorized"}.
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                AuthBroken = true;
                return null;
            }

            if ((int)resp.StatusCode >= 500)
            {
                return null; // server error — retry next run, do not mark "searched"
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            List<SubtitleCandidate>? batch = null;
            int totalPages = 1;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("status", out var st) || st.ValueKind != JsonValueKind.True)
                {
                    return result; // search error → fail-open (empty)
                }

                if (root.TryGetProperty("totalPages", out var tp) && tp.ValueKind == JsonValueKind.Number)
                {
                    totalPages = tp.GetInt32();
                }

                if (root.TryGetProperty("subtitles", out var subsEl) && subsEl.ValueKind == JsonValueKind.Array)
                {
                    batch = new List<SubtitleCandidate>();
                    foreach (var s in subsEl.EnumerateArray())
                    {
                        var cand = SubtitleCandidate.FromJson(s);
                        if (cand != null)
                        {
                            batch.Add(cand);
                        }
                    }

                    // Film-level ids from results[0] onto every candidate
                    SubtitleCandidate.StampFilmIds(batch, root);
                }
            }
            catch (JsonException)
            {
                return result; // malformed → fail-open
            }

            if (batch == null || batch.Count == 0)
            {
                break;
            }

            result.AddRange(batch);

            // Early-stop once every requested language has enough candidates —
            // the pipeline only keeps MaxCandidatesPerLanguage per language anyway, so
            // further pages are pure API-quota waste (verified 11.09.2026: ~half of the
            // 2015 daily requests were pagination over pages we discard).
            if (maxCandidatesPerLanguage > 0 && !string.IsNullOrWhiteSpace(languages))
            {
                var wanted = languages.Split(',').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var perLang = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in result)
                {
                    var code = (c.Language ?? string.Empty).Trim();
                    if (code.Length == 0) continue;
                    perLang[code] = perLang.GetValueOrDefault(code) + 1;
                }
                bool satisfied = wanted.All(w => perLang.GetValueOrDefault(w) >= maxCandidatesPerLanguage);
                if (satisfied)
                {
                    Trace("STOP", $"early-stop after page {page}: all {wanted.Count} languages have >= {maxCandidatesPerLanguage} candidates", 0, "F-M95");
                    break;
                }
            }

            if (page >= totalPages)
            {
                break;
            }

            page++;
        }

        return result;
    }





    /// <summary>
    /// V2 single-file download by nId — GET /api/v2/subtitles/{nId}/download?format=file
    /// (Bearer auth). Same retry contract as v1 downloads (429/5xx backoff, F-M27/F-M58).
    /// </summary>
    /// <summary>
    /// F-M235: per-attempt budget for a subtitle FILE. A whole-season archive is orders of
    /// magnitude larger than a metadata answer, and holding it to the metadata timeout is what
    /// turns a slow-but-working download into a timeout.
    /// </summary>
    private static readonly TimeSpan FileTransferTimeout = TimeSpan.FromMinutes(5);

    public async Task<byte[]?> DownloadV2FileAsync(string nId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nId))
        {
            return null;
        }

        string apiKey = ApiKey ?? throw new InvalidOperationException("Download requires the SubDL API key");
        string fullUrl = $"{ApiBase}/api/v2/subtitles/{Uri.EscapeDataString(nId)}/download?format=file";
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.Add("User-Agent", UserAgent);
            // F-M235: a subtitle archive is far larger than a JSON answer, so it gets its own
            // per-attempt budget instead of sharing the metadata timeout.
            req.Options.Set(TransientRetryHandler.AttemptTimeoutKey, FileTransferTimeout);
            TraceCall("GET", fullUrl);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            ApiActivity?.Invoke();
            Trace("GET", fullUrl, (int)resp.StatusCode, $"v2 download attempt {attempt}");
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                Classify429(resp, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || (int)resp.StatusCode >= 500)
            {
                if (attempt == maxAttempts)
                {
                    return null;
                }

                int backoffMs = 2000 << (attempt - 1);
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                continue;
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// F-M17y: lists the user's own uploads (rejected and accepted) from SubDL.
    /// Used to clean up the dashboard after a duplicate upload verdict.
    /// IMPORTANT: SubDL's /user/mySubtitles ignores page/per_page/offset/limit entirely and
    /// always returns the same complete list (verified 2026-09-24: page=1/2, per_page=10/100/500,
    /// offset=100/252, limit, start all returned byte-identical payloads, no pagination headers).
    /// Iterating "pages" therefore re-reads the same entries N times, which silently inflated the
    /// postprocessing counters (5 x 650 = 3250 "fetched" instead of the real 650). We keep the loop
    /// for forward compatibility, but deduplicate by UploadId and stop at the first page that adds
    /// no new id, so the returned list and the reported totals are truthful.
    /// </summary>
    public async Task<List<OwnSubtitleEntry>?> ListMySubtitlesAsync(CancellationToken ct, int maxPages = 10, Func<OwnSubtitleEntry, bool>? shouldStop = null)
    {
        string token = _token ?? throw new InvalidOperationException("Not logged in (call LoginAsync first)");
        var result = new List<OwnSubtitleEntry>();
        var seenIds = new HashSet<int>();
        int page = 1;
        int perPage = 100;
        int pagesRead = 0;
        while (page <= Math.Max(1, maxPages))
        {
            string url = $"{ApiBase}/user/mySubtitles?page={page}&per_page={perPage}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            TraceCall("GET", url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            ApiActivity?.Invoke();
            Trace("GET", "user/mySubtitles", (int)resp.StatusCode, $"page {page}");
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("status", out var st) || st.ValueKind != JsonValueKind.True)
                {
                    return null;
                }

                if (!doc.RootElement.TryGetProperty("subtitles", out var subsEl) || subsEl.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                pagesRead++;

                int added = 0;
                bool sawDuplicate = false;
                foreach (var item in subsEl.EnumerateArray())
                {
                    var entry = OwnSubtitleEntry.FromJson(item);
                    if (entry == null)
                    {
                        continue;
                    }

                    // Deduplicate by SubDL upload id. Entries without a usable id (UploadId <= 0)
                    // are always kept so that a parser regression can never silently drop rows.
                    if (entry.UploadId > 0)
                    {
                        if (!seenIds.Add(entry.UploadId))
                        {
                            sawDuplicate = true;
                            continue;
                        }
                    }

                    result.Add(entry);
                    added++;
                    if (shouldStop != null && shouldStop(entry))
                    {
                        return result;
                    }
                }

                // The API serves the same full list for every page number, so a page that yields
                // no new ids means we have seen everything: stop instead of re-reading it.
                // This also covers the normal case of a short last page (< perPage).
                if (added == 0 || subsEl.GetArrayLength() < perPage)
                {
                    if (sawDuplicate)
                    {
                        Trace("GET", "user/mySubtitles", 200,
                            $"pagination unsupported by API: page {page} repeated already-seen ids, stopped after {pagesRead} page(s)");
                    }

                    break;
                }
            }
            catch (JsonException)
            {
                return null;
            }

            page++;
        }

        return result;
    }


    /// <summary>
    /// F-M17y-b: fetch exactly one page of /user/mySubtitles for diagnostics.
    /// </summary>
    public async Task<List<OwnSubtitleEntry>?> ListMySubtitlesPageAsync(int page, int perPage, CancellationToken ct)
    {
        string token = _token ?? throw new InvalidOperationException("Not logged in (call LoginAsync first)");
        var result = new List<OwnSubtitleEntry>();
        string url = $"{ApiBase}/user/mySubtitles?page={page}&per_page={perPage}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        TraceCall("GET", url);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        ApiActivity?.Invoke();
        Trace("GET", "user/mySubtitles", (int)resp.StatusCode, $"page {page}");
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("status", out var st) || st.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("subtitles", out var subsEl) || subsEl.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in subsEl.EnumerateArray())
            {
                var entry = OwnSubtitleEntry.FromJson(item);
                if (entry != null)
                {
                    result.Add(entry);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return result;
    }
    /// <summary>
    /// F-M17y: deletes one of the user's own uploads by its SubDL internal upload id.
    /// Returns true when the server reports deletion.
    /// </summary>
    public async Task<bool> DeleteMySubtitleAsync(int uploadId, CancellationToken ct)
    {
        string token = _token ?? throw new InvalidOperationException("Not logged in (call LoginAsync first)");
        string url = $"{ApiBase}/user/deleteSubtitle?id={uploadId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        TraceCall("GET", url);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        ApiActivity?.Invoke();
        Trace("GET", "user/deleteSubtitle", (int)resp.StatusCode, $"id {uploadId}");
        if (!resp.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// F-M49: parses the daily download-quota reset time from a 429 response.
    /// x-ratelimit-reset (ISO 8601 UTC timestamp) preferred, retry-after seconds
    /// as fallback. Only sane values are accepted: in the future and at most 24 h
    /// away — anything else is treated as unknown (caller stops the run).
    /// </summary>
    /// <param name="resp">The 429 response.</param>
    /// <returns>UTC reset time, or null when unusable.</returns>
    private static DateTime? ParseDailyLimitReset(HttpResponseMessage resp)
    {
        string? resetHdr = resp.Headers.TryGetValues("x-ratelimit-reset", out var resetValues)
            ? resetValues.FirstOrDefault()
            : null;
        if (!string.IsNullOrWhiteSpace(resetHdr)
            && DateTime.TryParse(resetHdr, null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime reset)
            && reset > DateTime.UtcNow
            && reset - DateTime.UtcNow <= TimeSpan.FromHours(24))
        {
            return reset;
        }

        string? retryHdr = resp.Headers.TryGetValues("retry-after", out var retryValues)
            ? retryValues.FirstOrDefault()
            : null;
        if (int.TryParse(retryHdr, out int retrySec)
            && retrySec > 0
            && retrySec <= 86400)
        {
            return DateTime.UtcNow.AddSeconds(retrySec);
        }

        return null;
    }

    /// <summary>
    /// Downloads a subtitle file from dl.subdl.com. Handles the URL prefix variants
    /// and retries on HTTP 429/5xx with exponential backoff (F-M11/F-M27): x2, x4,
    /// x8 — max 3 attempts, then gives up (caller moves to the next candidate).
    /// </summary>
    /// <param name="dlUrl">Download path or URL from the search result.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Raw subtitle bytes (SRT extracted when ZIP-packaged) or null on failure.</returns>
    public async Task<byte[]?> DownloadSubtitleFileAsync(string dlUrl, CancellationToken ct, int zipSeason = 0, int zipEpisode = 0)
    {
        string fullUrl;
        if (dlUrl.StartsWith('/'))
        {
            fullUrl = $"https://dl.subdl.com{dlUrl}";
        }
        else if (dlUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            fullUrl = dlUrl.Replace("://subdl.com", "://dl.subdl.com", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            fullUrl = $"https://dl.subdl.com/{dlUrl}";
        }

        byte[]? bytes = null;
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.Add("User-Agent", UserAgent);
            // F-M235: same per-request budget as the v2 download — whole-season archives.
            req.Options.Set(TransientRetryHandler.AttemptTimeoutKey, FileTransferTimeout);
            TraceCall("GET", fullUrl);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            ApiActivity?.Invoke();
            Trace("GET", fullUrl, (int)resp.StatusCode, $"download attempt {attempt}");
            if (resp.IsSuccessStatusCode)
            {
                bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                break;
            }

            // F-M27: 429/5xx → exponential backoff (2s, 4s, 8s), then give up.
            // A persistent 429 means SubDL rate-limited us server-side: mark it so
            // the caller stops the run instead of pointlessly walking every candidate.
            // F-M58: a service_busy 429 sets TransientOverload — the caller waits the
            // short retryAfterSeconds window instead of the full daily-limit wait.
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                Classify429(resp, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); // F-M58
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || (int)resp.StatusCode >= 500)
            {
                if (attempt == maxAttempts)
                {
                    return null;
                }

                int backoffMs = 2000 << (attempt - 1); // 2s, 4s
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                continue;
            }

            return null; // other status codes (404 etc.) — no retry
        }

        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        // ZIP packaging: extract the first .srt entry (ported from Phase 4)
        if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            try
            {
                using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));

                // F-M215 fallback: a pack without unpack_files (or with an unusable
                // listing) still must not hand us the wrong episode. When the caller
                // knows which episode it wants, prefer the archive entry that names it,
                // and only fall back to "first .srt" when NOTHING in the archive does —
                // otherwise one episode's subtitle is written next to another episode.
                string? wanted = null;
                if (zipEpisode > 0)
                {
                    foreach (var e in zip.Entries)
                    {
                        if (!e.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (SubtitleCandidate.EpisodeFromFileName(e.Name, zipSeason) == zipEpisode)
                        {
                            wanted = e.FullName;
                            break;
                        }
                    }

                    if (wanted == null)
                    {
                        // No entry names our episode: the archive is either a single-file
                        // release (one entry — safe to take) or a pack we cannot resolve.
                        int srtCount = 0;
                        foreach (var e in zip.Entries)
                        {
                            if (e.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                            {
                                srtCount++;
                            }
                        }

                        if (srtCount > 1)
                        {
                            return null; // multi-file pack, episode unresolvable → do not guess
                        }
                    }
                }

                foreach (var entry in zip.Entries)
                {
                    if (!entry.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (wanted != null && !string.Equals(entry.FullName, wanted, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    await using var es = await entry.OpenAsync(ct).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await es.CopyToAsync(ms, ct).ConfigureAwait(false);
                    return ms.ToArray();
                }

                return null; // ZIP without .srt
            }
            catch (System.IO.InvalidDataException)
            {
                return null; // corrupt ZIP
            }
        }

        return bytes;
    }
}

/// <summary>A SubDL search candidate (verified fields, see subdl-api-search-fields.md).</summary>
public sealed class SubtitleCandidate
{
    /// <summary>Gets or sets the release name (scoring basis).</summary>
    public string ReleaseName { get; set; } = string.Empty;

    /// <summary>IMDb ID of the matched film from results[0] (null when absent).</summary>
    public string? ImdbId { get; set; }

    /// <summary>TMDB id of the matched film from results[0] (string of the numeric id, null when absent).</summary>
    public string? TmdbId { get; set; }

    /// <summary>Gets or sets the 2-letter uppercase SubDL language.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the download path (dl.subdl.com prefix needed).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the season (0 when absent).</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the episode (0 when absent — full-season releases).</summary>
    public int Episode { get; set; }

    /// <summary>Gets or sets the episode range start (0 when absent).</summary>
    public int EpisodeFrom { get; set; }

    /// <summary>Gets or sets the episode range end (0 when absent).</summary>
    public int EpisodeEnd { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a full-season pack.</summary>
    public bool FullSeason { get; set; }

    /// <summary>Gets or sets the framerate code (0 = unknown; 2 ⇒ 23.976, 3 ⇒ 25, 4 ⇒ 29.97, 5 ⇒ 24, 7 ⇒ 30).</summary>
    public int Framerate { get; set; }

    /// <summary>
    /// Gets or sets the literal fps the API sent in its "fps" field (e.g. 23.976), or null when
    /// absent. Distinct from <see cref="Framerate"/>, which is a CODE, not a rate — the old code
    /// mixed the two by falling back from code to rate, which cannot be right: code 2 means 23.976,
    /// so a literal 2 in that slot would have meant something else entirely.
    /// </summary>
    public double? FpsLiteral { get; set; }

    /// <summary>
    /// Gets the real FPS (F-M43 Stufe 1): the literal the API sent when present, else the rate
    /// belonging to the framerate code. 0 when neither is known.
    /// </summary>
    public double Fps
    {
        get
        {
            var fromCode = Framerate switch
            {
                2 => 23.976,
                3 => 25.0,
                4 => 29.97,
                5 => 24.0,
                7 => 30.0,
                _ => 0.0
            };
            return fromCode != 0.0 ? fromCode : FpsLiteral ?? 0.0;
        }
    }

    /// <summary>Gets or sets the hearing-impaired flag.</summary>
    public bool HearingImpaired { get; set; }

    /// <summary>Gets or sets the download count (often absent in search responses). no longer used as a scoring criterion.</summary>
    public long DownloadCount { get; set; }

    /// <summary>V2 API match_score 0..1 (files/search batch). Null = absent (v1/GET searches without scoring).</summary>
    public double? MatchScore { get; set; }

    /// <summary>V2 subtitle nId (subtitlePage path segment, e.g. "b92JNb43Cc") —
    /// addresses /api/v2/subtitles/{nId}/download?format=file. Empty when unknown.</summary>
    public string NId { get; set; } = string.Empty;

    /// <summary>Original file name on SubDL ("name" field, e.g. "Lioness S03E06.zip").</summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>Stable subtitle id parsed from the download url
    /// ("/subtitle/3676577-8593549.zip" → "3676577-8593549"); identifies the EXACT
    /// SubDL entry (the download), not the release family.</summary>
    public string SubdlId { get; set; } = string.Empty;

    /// <summary>Uploader name ("author" field).</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>Gets or sets the unpacked single-file entries of a season/range pack
    /// (API field "unpack_files", only present when the search ran with unpack=1).
    /// Each entry carries its own season/episode plus a ready single-file download url,
    /// which is what makes per-episode selection inside a pack possible (F-M215).</summary>
    public List<SubtitleUnpackFile> UnpackFiles { get; set; } = new();

    /// <summary>Parses a candidate from the API's subtitles array element.</summary>
    /// <param name="el">JSON element.</param>
    /// <returns>Candidate or null when the url is missing.</returns>
    public static SubtitleCandidate? FromJson(JsonElement el)
    {
        string? url = null;
        if (el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
        {
            url = u.GetString();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var c = new SubtitleCandidate { Url = url };

        // Stable subtitle id from the url path ("/subtitle/<id>.<ext>").
        var urlPath = url;
        var qIdx = urlPath.IndexOf('?', System.StringComparison.Ordinal);
        if (qIdx >= 0)
        {
            urlPath = urlPath[..qIdx];
        }

        var segs = urlPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length >= 2 && segs[^2] == "subtitle")
        {
            var file = segs[^1]; // e.g. "3676577-8593549.zip"
            var dot = file.IndexOf('.', System.StringComparison.Ordinal);
            c.SubdlId = dot > 0 ? file[..dot] : file;
        }
        if (el.TryGetProperty("release_name", out var rn) && rn.ValueKind == JsonValueKind.String)
        {
            c.ReleaseName = rn.GetString() ?? string.Empty;
        }

        if (el.TryGetProperty("language", out var lg) && lg.ValueKind == JsonValueKind.String)
        {
            c.Language = (lg.GetString() ?? string.Empty).ToUpperInvariant();
        }

        c.Season = GetInt(el, "season");
        c.Episode = GetInt(el, "episode");
        c.EpisodeFrom = GetInt(el, "episode_from");
        c.EpisodeEnd = GetInt(el, "episode_end");

        // These three are NOT read with an int-only reader. Live measurement (28.09.2026, 37 hits
        // across 4 queries): full_season and hi arrive as JSON booleans, and fps arrives as a numeric
        // STRING ("23.976") or not at all. The old code read all three with GetInt, which accepts only
        // JsonValueKind.Number, so it returned 0 for every one of them — silently. For hi that meant
        // "no candidate is ever hearing impaired", which is what made the HI switch do nothing at all.
        c.FullSeason = JsonFields.GetBool(el, "full_season");
        c.HearingImpaired = JsonFields.GetBool(el, "hi");
        c.Framerate = GetInt(el, "framerate");

        // The literal fps, when the API sends one. Kept alongside the code so Fps reports a real
        // value for entries that carry "fps" but no "framerate" code.
        var fpsLiteral = JsonFields.GetDouble(el, "fps");
        c.FpsLiteral = fpsLiteral > 0 ? fpsLiteral : null;
        c.DownloadCount = GetInt(el, "download_count");
        if (el.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
        {
            c.OriginalName = nm.GetString() ?? string.Empty; // Original SubDL file name
        }

        if (el.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.String)
        {
            c.Author = au.GetString() ?? string.Empty; // Uploader
        }

        // F-M215: unpack_files — the per-file list of a season or range pack.
        // Present only when the search ran with unpack=1. Each entry addresses one
        // single episode file and carries its own season/episode, so a pack can be
        // resolved to the CORRECT episode instead of blindly taking the first .srt.
        if (el.TryGetProperty("unpack_files", out var uf) && uf.ValueKind == JsonValueKind.Array)
        {
            foreach (var fe in uf.EnumerateArray())
            {
                var parsed = SubtitleUnpackFile.FromJson(fe);
                if (parsed != null)
                {
                    c.UnpackFiles.Add(parsed);
                }
            }
        }

        return c;
    }

    /// <summary>
    /// Parses the film-level ids from the search response's results[0]
    /// element and stamps them onto every candidate (per-candidate id fields do
    /// not exist in the API — ids live on the film header, see subdl.com/api-doc).
    /// </summary>
    public static void StampFilmIds(List<SubtitleCandidate> candidates, JsonElement root)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return;
        }

        string? imdb = null;
        string? tmdb = null;
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
        {
            var film = results[0];
            if (film.TryGetProperty("imdb_id", out var imEl) && imEl.ValueKind == JsonValueKind.String)
            {
                imdb = imEl.GetString();
            }

            if (film.TryGetProperty("tmdb_id", out var tmEl) && tmEl.ValueKind == JsonValueKind.Number)
            {
                tmdb = tmEl.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (film.TryGetProperty("tmdb_id", out tmEl) && tmEl.ValueKind == JsonValueKind.String)
            {
                tmdb = tmEl.GetString();
            }
        }

        if (imdb == null && tmdb == null)
        {
            return;
        }

        foreach (var c in candidates)
        {
            c.ImdbId ??= imdb;
            c.TmdbId ??= tmdb;
        }
    }

    /// <summary>Finds the unpack file that belongs to the searched season/episode.
    /// A pack without unpack_files (search ran without unpack=1) yields null so the
    /// caller can fall back to matching the ZIP entry name instead.</summary>
    /// <param name="season">Wanted season (0 = any).</param>
    /// <param name="episode">Wanted episode (0 = any).</param>
    /// <returns>The matching file, or null when the pack carries no usable listing.</returns>
    public SubtitleUnpackFile? FindUnpackFile(int season, int episode)
    {
        if (UnpackFiles.Count == 0)
        {
            return null;
        }

        // Exact season+episode first: this is the authoritative match.
        foreach (var f in UnpackFiles)
        {
            if (f.Episode > 0 && f.Episode == episode && (season <= 0 || f.Season == season))
            {
                return f;
            }
        }

        if (episode > 0)
        {
            // Episode exact, season not reported by the API.
            foreach (var f in UnpackFiles)
            {
                if (f.Episode == episode && f.Season <= 0)
                {
                    return f;
                }
            }

            // Episode number embedded in the file name (the S0106 style SubDL uses
            // carries no separate season/episode fields but does name the episode).
            foreach (var f in UnpackFiles)
            {
                if (EpisodeFromFileName(f.Name, season) == episode)
                {
                    return f;
                }
            }
        }

        return null;
    }

    /// <summary>Extracts an episode number from a file name — S01E06, s01.e06, 1x06 and
    /// SubDL's S0106 style. Returns 0 when nothing is recognisable, and also 0 when the
    /// named season contradicts <paramref name="season"/>, so a same-numbered episode of
    /// another season is never picked.</summary>
    /// <param name="name">File name of the pack entry.</param>
    /// <param name="season">Wanted season (0 = any).</param>
    /// <returns>Episode number, or 0.</returns>
    internal static int EpisodeFromFileName(string name, int season)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        foreach (var pattern in new[] { @"[Ss](\d{1,2})[\s._-]?[Ee](\d{1,3})\b", @"\b(\d{1,2})x(\d{2})\b", @"[Ss](\d{2})(\d{2})\b" })
        {
            var m = System.Text.RegularExpressions.Regex.Match(name, pattern);
            if (!m.Success)
            {
                continue;
            }

            int sn = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (season > 0 && sn != season)
            {
                return 0;
            }

            return int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return 0;
    }

    private static int GetInt(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
        {
            return v.GetInt32();
        }

        return 0;
    }
}

/// <summary>
/// F-M17y: one entry from /user/mySubtitles — the user's own upload record,
/// including the internal upload id needed for /user/deleteSubtitle.
/// </summary>
public sealed class OwnSubtitleEntry
{
    /// <summary>Internal upload id; pass to /user/deleteSubtitle?id={UploadId}.</summary>
    public int UploadId { get; set; }

    /// <summary>Subtitle n_id.</summary>
    public string NId { get; set; } = string.Empty;

    /// <summary>IMDb id of the title.</summary>
    public string? ImdbId { get; set; }

    /// <summary>TMDB id of the title.</summary>
    public int? TmdbId { get; set; }

    /// <summary>Release/file name as stored on SubDL.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Language name or code.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Upload creation time (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Status: e.g. "accepted", "rejected".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Rejection reason when Status == "rejected".</summary>
    public string RejectReason { get; set; } = string.Empty;

    /// <summary>Season (TV) or 0.</summary>
    public int Season { get; set; }

    /// <summary>Episode (TV) or 0.</summary>
    public int Episode { get; set; }

    /// <summary>Optional content hash if the API reports it.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Parses a /user/mySubtitles array element.</summary>
    public static OwnSubtitleEntry? FromJson(JsonElement el)
    {
        if (!el.TryGetProperty("subtitle", out var sub) || sub.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var e = new OwnSubtitleEntry();
        if (sub.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            e.UploadId = idEl.GetInt32();
        }

        if (sub.TryGetProperty("n_id", out var nIdEl) && nIdEl.ValueKind == JsonValueKind.String)
        {
            e.NId = nIdEl.GetString() ?? string.Empty;
        }

        if (sub.TryGetProperty("imdb_id", out var imEl) && imEl.ValueKind == JsonValueKind.String)
        {
            e.ImdbId = imEl.GetString();
        }

        if (sub.TryGetProperty("tmdb_id", out var tmEl))
        {
            if (tmEl.ValueKind == JsonValueKind.Number)
            {
                e.TmdbId = tmEl.GetInt32();
            }
            else if (tmEl.ValueKind == JsonValueKind.String && int.TryParse(tmEl.GetString(), out int tm))
            {
                e.TmdbId = tm;
            }
        }

        if (sub.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
        {
            e.Name = nameEl.GetString() ?? string.Empty;
        }

        if (sub.TryGetProperty("lang", out var langEl) && langEl.ValueKind == JsonValueKind.String)
        {
            e.Language = langEl.GetString() ?? string.Empty;
        }
        else if (sub.TryGetProperty("language", out langEl) && langEl.ValueKind == JsonValueKind.String)
        {
            e.Language = langEl.GetString() ?? string.Empty;
        }

        if (sub.TryGetProperty("created_at", out var createdEl) && createdEl.ValueKind == JsonValueKind.String)
        {
            if (DateTime.TryParse(createdEl.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            {
                e.CreatedAt = dt;
            }
        }

        if (sub.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
        {
            e.Status = statusEl.GetString() ?? string.Empty;
        }

        if (sub.TryGetProperty("rejectReason", out var rejectEl) && rejectEl.ValueKind == JsonValueKind.String)
        {
            e.RejectReason = rejectEl.GetString() ?? string.Empty;
        }

        if (sub.TryGetProperty("season", out var seasonEl) && seasonEl.ValueKind == JsonValueKind.Number)
        {
            e.Season = seasonEl.GetInt32();
        }

        if (sub.TryGetProperty("episode", out var epEl) && epEl.ValueKind == JsonValueKind.Number)
        {
            e.Episode = epEl.GetInt32();
        }
        else if (sub.TryGetProperty("ef", out var efEl) && efEl.ValueKind == JsonValueKind.Number)
        {
            e.Episode = efEl.GetInt32();
        }

        return e;
    }
}

/// <summary>F-M215: one entry of a pack's "unpack_files" array — a single subtitle
/// file inside the archive, carrying its own season/episode and a ready download url.</summary>
public sealed class SubtitleUnpackFile
{
    /// <summary>Gets or sets the file name inside the pack (e.g. "Agent.X.S01E06.Pilot...srt").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the release name of this single file.</summary>
    public string ReleaseName { get; set; } = string.Empty;

    /// <summary>Gets or sets the season (0 when the API reports none).</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the episode (0 when the API reports none).</summary>
    public int Episode { get; set; }

    /// <summary>Gets or sets the uppercase language code.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this file is hearing impaired.</summary>
    public bool HearingImpaired { get; set; }

    /// <summary>Gets or sets the ready single-file download url
    /// ("/subtitle/&lt;page&gt;/&lt;file_n_id&gt;?api_key=..."), not a .zip.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the MD5 SubDL stores for this single file — directly
    /// comparable with our content hash (F-M186).</summary>
    public string Md5 { get; set; } = string.Empty;

    /// <summary>Parses one entry of the unpack_files array.</summary>
    /// <param name="el">JSON element.</param>
    /// <returns>Entry, or null when it carries no download url.</returns>
    public static SubtitleUnpackFile? FromJson(JsonElement el)
    {
        string? url = null;
        if (el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
        {
            url = u.GetString();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var f = new SubtitleUnpackFile { Url = url };

        if (el.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
        {
            f.Name = nm.GetString() ?? string.Empty;
        }

        if (el.TryGetProperty("release_name", out var rn) && rn.ValueKind == JsonValueKind.String)
        {
            f.ReleaseName = rn.GetString() ?? string.Empty;
        }

        if (el.TryGetProperty("language", out var lg) && lg.ValueKind == JsonValueKind.String)
        {
            f.Language = (lg.GetString() ?? string.Empty).ToUpperInvariant();
        }

        if (el.TryGetProperty("md5", out var md) && md.ValueKind == JsonValueKind.String)
        {
            f.Md5 = md.GetString() ?? string.Empty;
        }

        if (el.TryGetProperty("season", out var se) && se.ValueKind == JsonValueKind.Number)
        {
            f.Season = se.GetInt32();
        }

        if (el.TryGetProperty("episode", out var ep) && ep.ValueKind == JsonValueKind.Number)
        {
            f.Episode = ep.GetInt32();
        }

        // Shared reader: hi is a JSON boolean in search responses, but the unpack list has been seen
        // with the 0/1 form. JsonFields.ToBool accepts both, so this no longer needs its own guess.
        if (el.TryGetProperty("hi", out var hi))
        {
            f.HearingImpaired = JsonFields.ToBool(hi);
        }

        // The API reports season/episode as 0 on some entries even though the file
        // name names them (live: "Agent.X.US.S0106.HDTV-LOL.srt").
        if (f.Episode <= 0)
        {
            f.Episode = SubtitleCandidate.EpisodeFromFileName(f.Name, 0);
        }

        return f;
    }
}
