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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// Resolves an IMDb ID from a TMDB id via the TMDB REST API (user decision 08.09.2026).
/// REQUIRED component (F-M203, 01.10.2026): with no key configured a run does not start
/// at all — both pipelines refuse it up front and name the missing field (F-M19). The
/// reason it is not optional: id resolution is TMDb-authoritative in both directions, so
/// without it an episode id could reach SubDL where a show id belongs.
/// Results are cached per (tmdbId, isSeries, year, season, episode) for the process
/// lifetime (negative results too — avoids hammering TMDB while polling).
/// </summary>
public sealed class TmdbImdbResolver
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private static readonly System.Collections.Generic.Dictionary<(string, bool), string?> _cache = new();

    /// <summary>
    /// F-M24a (user decision 09.09.2026): per-call trace for TMDB requests — the
    /// pipelines forward these at VERBOSE level (SubDL API calls are Debug, TMDB
    /// is Verbose per user decision). api_key redacted before logging.
    /// </summary>
    public event Action<string>? Trace;

    /// <summary>
    /// F-M54 (user decision 09.09.2026): set when TMDB answers 401 — the API key
    /// is wrong. Reported ONCE via <see cref="AuthError"/> (Normal level); further
    /// calls are suppressed until restart (no pointless retry hammering).
    /// </summary>
    public bool AuthBroken { get; private set; }

    /// <summary>Raised once when TMDB rejects the key (401) — pipelines log it at Error/Normal.</summary>
    public event Action<string>? AuthError;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbImdbResolver"/> class.
    /// </summary>
    /// <param name="http">Shared HttpClient.</param>
    /// <param name="apiKey">TMDB API key (v3) or null when not configured.</param>
    public TmdbImdbResolver(HttpClient http, string? apiKey)
    {
        _http = http;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    /// <summary>Gets a value indicating whether a TMDB key is configured at all.</summary>
    public bool IsConfigured => _apiKey != null;

    /// <summary>
    /// TMDB v4 tokens (JWT, start with "eyJ") authenticate via the Authorization: Bearer
    /// header; classic v3 keys go into the api_key query parameter. This helper builds the
    /// request for whichever format is configured (user decision 09.09.2026: the existing
    /// v4 account token from movies2-nfo-generator.py is the source of truth; no extra v3 key).
    /// </summary>
    private HttpRequestMessage BuildTmdbRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (_apiKey!.StartsWith("eyJ", StringComparison.Ordinal))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return req;
    }

    private string TmdbUrl(string pathAndQuery)
    {
        // v4 JWT tokens do not work as api_key query parameter — strip it for Bearer auth.
        if (_apiKey!.StartsWith("eyJ", StringComparison.Ordinal))
        {
            return $"https://api.themoviedb.org/3/{pathAndQuery}";
        }

        var sep = pathAndQuery.Contains("?", StringComparison.Ordinal) ? "&" : "?";
        return $"https://api.themoviedb.org/3/{pathAndQuery}{sep}api_key={Uri.EscapeDataString(_apiKey)}";
    }


    /// <summary>Traces one TMDB round-trip with the api_key redacted (F-M24b).</summary>
    private void TraceCall(string method, string url, int status)
    {
        var clean = url;
        var keyIdx = clean.IndexOf("api_key=", StringComparison.OrdinalIgnoreCase);
        if (keyIdx >= 0)
        {
            var end = clean.IndexOf('&', keyIdx);
            clean = end < 0 ? clean[..keyIdx] + "api_key=[REDACTED]" : clean[..keyIdx] + "api_key=[REDACTED]" + clean[end..];
        }

        Trace?.Invoke($"TMDb {method} {clean} → {status}");
    }

    /// <summary>Raised when Jellyfin's id and TMDB's id disagree (user wants a report).</summary>
    public event Action<string>? IdMismatch;


    /// <summary>
    /// F-M190 (24.09.2026): resolves a title WITHOUT assuming the media type —
    /// TMDB's own answer decides whether it is a film or a series, and the
    /// season/episode numbers come from the file name. This is the watchdog's
    /// proven approach and the only one that works when an episode sits in a
    /// library typed "movies" (Jellyfin then reports it as a Movie and a
    /// film-only search finds nothing).
    /// </summary>
    /// <param name="title">Title or series name from the file NAME.</param>
    /// <param name="year">Production year or null.</param>
    /// <param name="preferSeries">True when the name carried a series marker.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ids plus the type TMDB actually returned, or nulls.</returns>
    public Task<(string? Imdb, string? Tmdb, bool Found, bool IsSeries)> ResolveByMultiSearchAsync(string title, int? year, bool preferSeries, CancellationToken ct)
        => ResolveByMultiSearchInternalAsync(title, year, preferSeries, strictYear: false, ct);

    /// <summary>
    /// F-M231 (27.09.2026, user decision "Wir testen jfs id immer gegen tmdb"): the STRICT
    /// twin of <see cref="ResolveByMultiSearchAsync"/> — it answers only when a hit's OWN
    /// year equals the year asked for. No year match means NOT FOUND, never a fallback to
    /// the most popular hit.
    /// <para>
    /// This is what tests a Jellyfin id against TMDb. The ordinary multi-search falls back
    /// to the first hit, so it cannot tell "TMDb agrees" from "TMDb found something else";
    /// against an item whose metadata pins the wrong title it silently confirms the pinned
    /// id. Measured 27.09.2026: a 110-minute 2026 film sitting in <c>Mayday/S01/</c> carries
    /// tmdb=90 (the 2003 documentary) in its tvshow.nfo, and the pair check passes because
    /// 90 really does resolve to that imdb id — the title and year were never asked. Used
    /// with the year from the FILE NAME it returns the 2026 film instead (tmdb=1137844).
    /// </para>
    /// </summary>
    public Task<(string? Imdb, string? Tmdb, bool Found, bool IsSeries)> ResolveByTitleYearAsync(string title, int? year, bool preferSeries, CancellationToken ct)
        => ResolveByMultiSearchInternalAsync(title, year, preferSeries, strictYear: true, ct);

    private async Task<(string? Imdb, string? Tmdb, bool Found, bool IsSeries)> ResolveByMultiSearchInternalAsync(string title, int? year, bool preferSeries, bool strictYear, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title) || AuthBroken)
        {
            return (null, null, false, preferSeries);
        }

        // Cached like the other lookups: the pipeline's later ladder stage asks
        // the same question when the gate already answered it, and a repeat
        // search must not cost a second API call. The strict twin shares the
        // cache entry (same HTTP call, different pick) but not the answer.
        var cacheKey = ("multi:" + title.Trim().ToUpperInvariant() + "|" + (year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + "|" + (strictYear ? "strict" : "loose"), preferSeries);
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out var cachedHit))
            {
                // Hit stored as "imdb|tmdb|found|isSeries".
                var parts = cachedHit?.Split('|');
                if (parts is { Length: 4 })
                {
                    return (parts[0].Length > 0 ? parts[0] : null, parts[1].Length > 0 ? parts[1] : null,
                            parts[2] == "1", parts[3] == "1");
                }

                if (cachedHit == null)
                {
                    return (null, null, false, preferSeries);
                }
            }
        }

        string? imdb = null;
        string? tmdb = null;
        bool found = false;
        bool isSeries = preferSeries;

        try
        {
            // F-M219 (27.09.2026): NO year parameter here. Measured against TMDB on
            // 27.09.2026, search/multi IGNORES it completely — `year`, `first_air_date_year`
            // and `primary_release_year` all return the byte-identical list ("The Office"
            // with year=1996 still leads with the 2005 US show). The typed endpoints do
            // honour it (search/tv?first_air_date_year=2001 → exactly the 2001 UK show).
            // So the year is applied HERE, on the results, and the typed search stays the
            // precision tool for a single known type.
            string pathAndQuery = $"search/multi?query={Uri.EscapeDataString(title)}";

            string url = TmdbUrl(pathAndQuery);
            using var req = BuildTmdbRequest(url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            TraceCall("GET", url, (int)resp.StatusCode);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                HandleAuthFailure();
            }
            else if (resp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    // F-M219: pick the hit whose OWN date agrees with the year we were given
                    // when one does, and fall back to the first film/series hit otherwise.
                    // Taking hits[0] blindly picks by popularity, not identity — "The Office"
                    // resolves to the 2005 US show even when the item is the 2001 UK one.
                    // A year is a Jellyfin value and may be the IMPORT year (F-M217), so a
                    // hit list with no year match is still used — that keeps the old
                    // behaviour for genuinely wrong years.
                    (string? Imdb, string? Tmdb, bool Found, bool IsSeries) first = (null, null, false, preferSeries);
                    (string? Imdb, string? Tmdb, bool Found, bool IsSeries) yearMatch = (null, null, false, preferSeries);
                    foreach (var hit in results.EnumerateArray())
                    {
                        // Only film/series hits carry usable ids; "person" hits are skipped.
                        if (!hit.TryGetProperty("media_type", out var mtEl) || mtEl.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var mt = mtEl.GetString();
                        if (mt is not ("movie" or "tv"))
                        {
                            continue;
                        }

                        if (!hit.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                        {
                            continue;
                        }

                        // The date field differs by media type: tv uses first_air_date,
                        // movies release_date. Both are "YYYY-MM-DD".
                        bool hitIsSeries = mt == "tv";
                        int? hitYear = null;
                        var dateField = hitIsSeries ? "first_air_date" : "release_date";
                        if (hit.TryGetProperty(dateField, out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
                        {
                            var raw = dateEl.GetString();
                            if (raw is { Length: >= 4 } && int.TryParse(raw.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var hy))
                            {
                                hitYear = hy;
                            }
                        }

                        var candidate = (imdb: (string?)null, tmdb: idEl.GetInt32().ToString(CultureInfo.InvariantCulture),
                                         found: true, isSeries: hitIsSeries);
                        if (!first.Found)
                        {
                            first = candidate;
                        }

                        if (year is int want && hitYear is int got && got == want)
                        {
                            // Exact identity: same title AND same year. This is the hit we
                            // want, so stop looking — it beats every later popularity match.
                            yearMatch = candidate;
                            break;
                        }
                    }

                    var chosen = yearMatch.Found ? yearMatch : first;

                    // F-M231: the strict twin refuses the fallback. Without a hit whose OWN
                    // year matches, the honest answer is "TMDb does not confirm this title at
                    // this year" — NOT the most popular hit of the same name. Returning the
                    // fallback here is what let a wrongly pinned item pass the id test.
                    if (strictYear && !yearMatch.Found)
                    {
                        Trace?.Invoke($"[TMDB] strict title+year test found no hit for \"{title}\" ({year?.ToString(CultureInfo.InvariantCulture) ?? "no year"}) — not confirming");
                        chosen = (null, null, false, preferSeries);
                    }

                    if (chosen.Found)
                    {
                        isSeries = chosen.IsSeries;
                        tmdb = chosen.Tmdb;
                        found = true;
                        imdb = await ResolveImdbAsync(tmdb!, isSeries, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"[TMDB] multi-search failed ({title}): {ex.Message}");
        }

        lock (_cache)
        {
            _cache[cacheKey] = $"{(imdb ?? string.Empty)}|{(tmdb ?? string.Empty)}|{(found ? "1" : "0")}|{(isSeries ? "1" : "0")}";
        }

        return (imdb, tmdb, found, isSeries);
    }

    /// <summary>
    /// Handles the 401 case: report once (Normal level via event) and switch into
    /// AuthBroken mode — every further call short-circuits to null without a request.
    /// </summary>
    private void HandleAuthFailure()
    {
        if (AuthBroken)
        {
            return;
        }

        AuthBroken = true;
        AuthError?.Invoke("TMDb API key rejected (HTTP 401) — TMDB lookups are disabled until the key is fixed in the plugin config. SubDL runs continue without TMDB resolution.");
    }

    /// <summary>
    /// F-M231 (27.09.2026, user decision "Wir testen jfs id immer gegen tmdb"): tests
    /// Jellyfin's ids against TMDb by TITLE and YEAR, and corrects them only when TMDb
    /// answers with a hit whose own year matches exactly.
    /// <para>
    /// Why a separate step: <see cref="ResolveAndValidateIdsAsync"/> checks that the two ids
    /// belong to the same entity, which is tautological — a wrongly pinned pair passes it
    /// (tmdb 90 ⇄ tt0386950 both describe the same series, so "they agree"). Title and year
    /// were never asked, and that is what lets a 110-minute 2026 film sitting under
    /// <c>Mayday/S01/</c> keep the 2003 documentary's identity through a whole run.
    /// </para>
    /// <para>
    /// Conservative by construction: no exact year match ⇒ Jellyfin's ids are kept
    /// unchanged. The step only ever ADDS a correction, so a missing year or a title TMDb
    /// spells differently costs nothing.
    /// </para>
    /// </summary>
    /// <returns>Ids, the type TMDb reported, and whether a correction was applied.</returns>
    public async Task<(string? Imdb, string? Tmdb, bool IsSeries, bool Corrected)> VerifyIdsAgainstTmdbAsync(string? jfImdb, string? jfTmdb, string title, int? year, bool isSeries, CancellationToken ct)
    {
        if (!IsConfigured || AuthBroken)
        {
            return (jfImdb, jfTmdb, isSeries, false);
        }

        // Without a year there is nothing to verify against — the strict test would find
        // no match for every title whose name TMDb carries more than once.
        if (year is not int wantYear || string.IsNullOrWhiteSpace(title))
        {
            return (jfImdb, jfTmdb, isSeries, false);
        }

        // The file name's year is the caller's job to prefer (F-M190: the name states what
        // a file IS); this step only needs the year it is handed.
        var strict = await ResolveByTitleYearAsync(title, wantYear, isSeries, ct).ConfigureAwait(false);
        if (!strict.Found || string.IsNullOrWhiteSpace(strict.Tmdb))
        {
            // TMDb has no hit for this title at this year. That is not proof of a wrong id
            // (a title spelled differently, or a year TMDb carries under another season),
            // so Jellyfin's ids stand untouched.
            Trace?.Invoke($"[TMDB] id test: no exact title+year hit for \"{title}\" ({wantYear}) — keeping Jellyfin's ids");
            return (jfImdb, jfTmdb, isSeries, false);
        }

        if (string.Equals(strict.Tmdb, jfTmdb, StringComparison.Ordinal))
        {
            // TMDb's own title+year answer IS Jellyfin's id — real confirmation, not the
            // pair check's tautology.
            Trace?.Invoke($"[TMDB] id test: \"{title}\" ({wantYear}) confirms Jellyfin's tmdb {strict.Tmdb}");
            return (jfImdb, jfTmdb, isSeries, false);
        }

        // A different entity carries this title at this year — TMDb wins, ids and type.
        IdMismatch?.Invoke($"ID conflict for \"{title}\" ({wantYear}): Jellyfin says tmdb={jfTmdb ?? "-"} imdb={jfImdb ?? "-"}, TMDb's title+year match says tmdb={strict.Tmdb} imdb={strict.Imdb ?? "-"} — using the TMDb id.");
        Trace?.Invoke($"[TMDB] id test: \"{title}\" ({wantYear}) → tmdb {strict.Tmdb} (Jellyfin had {jfTmdb ?? "-"}) — correcting");
        return (strict.Imdb ?? jfImdb, strict.Tmdb, strict.IsSeries, true);
    }

    /// <summary>
    /// Fetches the IMDb ID for a TMDB movie/tv id. Returns null when no key is
    /// configured, the lookup fails, or TMDB has no IMDB linked.
    /// </summary>
    /// <param name="tmdbId">TMDB movie id or tv id (NOT an episode/season id).</param>
    /// <param name="isSeries">True for tv, false for movie.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>IMDb ID (tt…) or null.</returns>
    public async Task<string?> ResolveImdbAsync(string tmdbId, bool isSeries, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(tmdbId) || AuthBroken)
        {
            return null;
        }

        var key = ("id:" + tmdbId, isSeries);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        string? imdb = null;
        try
        {
            string kind = isSeries ? "tv" : "movie";
            string url = TmdbUrl($"{kind}/{tmdbId}");
            using var req = BuildTmdbRequest(url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            TraceCall("GET", url, (int)resp.StatusCode);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                HandleAuthFailure(); // wrong TMDB key → Normal-level report once
            }
            else if (resp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("imdb_id", out var el) && el.ValueKind == JsonValueKind.String)
                {
                    string? v = el.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        imdb = v;
                    }
                }
            }

            // F-M190b (24.09.2026): the DETAIL endpoint reports imdb_id as null
            // for series (verified: tv/291084 and tv/95480 both null) while
            // /external_ids returns the real tt… for the same id — so a series
            // never got an IMDb id and every upload was skipped as "no-imdb".
            // Only called when the detail call left it empty (one extra request,
            // cached like the rest).
            if (imdb == null)
            {
                string extUrl = TmdbUrl($"{kind}/{tmdbId}/external_ids");
                using var extReq = BuildTmdbRequest(extUrl);
                using var extResp = await _http.SendAsync(extReq, ct).ConfigureAwait(false);
                TraceCall("GET", extUrl, (int)extResp.StatusCode);
                if (extResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    HandleAuthFailure();
                }
                else if (extResp.IsSuccessStatusCode)
                {
                    using var extDoc = await JsonDocument.ParseAsync(await extResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                    if (extDoc.RootElement.TryGetProperty("imdb_id", out var extEl) && extEl.ValueKind == JsonValueKind.String)
                    {
                        string? v = extEl.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            imdb = v;
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // network/API error → null (caller keeps waiting or skips); cached negative too
        }

        lock (_cache)
        {
            _cache[key] = imdb;
        }

        return imdb;
    }

    /// <summary>
    /// Last-resort path (user decision 08.09.2026): no IMDB and no TMDB id after the
    /// metadata wait → search TMDB by title (+year), take the best match and resolve
    /// its IMDb ID. Returns null when no key, no result, or no IMDB linked.
    /// </summary>
    /// <param name="title">Movie title or SERIES name (never an episode title).</param>
    /// <param name="year">Release/first-air year for disambiguation or null.</param>
    /// <param name="isSeries">True for tv, false for movie.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>IMDb ID (tt…) or null.</returns>
    /// <summary>(user decision 12.09.2026): IMDb→TMDB backfill. An item with
    /// an IMDb id but no TMDB id gets its TMDB id via /find/{imdb}?external_source=imdb_id
    /// so uploads carry the tmdb_id metadata on SubDL. Cached like the forward lookup;
    /// null on miss/failure (uploads proceed with imdb alone — never blocking).</summary>
    public async Task<string?> ResolveTmdbByImdbAsync(string imdbId, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(imdbId) || AuthBroken)
        {
            return null;
        }

        var key = ("imdb:" + imdbId, true);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        string? tmdb = null;
        try
        {
            string url = TmdbUrl($"find/{Uri.EscapeDataString(imdbId)}") + "&external_source=imdb_id";
            using var req = BuildTmdbRequest(url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            TraceCall("GET", url, (int)resp.StatusCode);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                HandleAuthFailure();
            }
            else if (resp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                var root = doc.RootElement;
                foreach (var listName in new[] { "movie_results", "tv_results" })
                {
                    if (!root.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var first = list[0];
                    if (first.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                    {
                        tmdb = idEl.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"[TMDB] find-by-imdb failed ({imdbId}): {ex.Message}");
        }

        lock (_cache)
        {
            _cache[key] = tmdb;
        }

        return tmdb;
    }

    public async Task<(string? Imdb, string? Tmdb)> ResolveImdbByTitleAsync(string title, int? year, bool isSeries, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title) || AuthBroken)
        {
            return (null, null);
        }

        var key = ("q:" + title.Trim().ToUpperInvariant() + "|" + (year?.ToString(CultureInfo.InvariantCulture) ?? ""), isSeries);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return (cached, null); // Cache stores the imdb part only (legacy entries)
            }
        }

        string? imdb = null;
        string? tmdb = null;
        try
        {
            string kind = isSeries ? "tv" : "movie";
            string yearParam = isSeries ? "first_air_date_year" : "year";
            string pathAndQuery = $"search/{kind}?query={Uri.EscapeDataString(title)}";
            if (year is int y)
            {
                pathAndQuery += $"&{yearParam}={y}";
            }

            // F-M217 (27.09.2026): the year is a JELLYFIN value and it is often the
            // IMPORT year, not the production year — measured 27.09.2026 on the test
            // library: Reacher carried ProductionYear 2025 (the show started 2022),
            // Agent X 2026 (started 2015), Slow Horses 2026 (2022), MobLand 2026. A
            // series search with such a year returns 0 hits, while the identical
            // query WITHOUT the year finds them immediately. The film-only branch
            // below repeats the query the same way and takes the first hit when a
            // year-filtered search came back empty.

            string url = TmdbUrl(pathAndQuery);
            using var req = BuildTmdbRequest(url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            TraceCall("GET", url, (int)resp.StatusCode);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                HandleAuthFailure(); // wrong TMDB key → Normal-level report once
                imdb = null;
            }
            else if (resp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                var hits = ReadTitleSearchHits(doc);
                if (hits.Count == 0 && year is not null)
                {
                    // F-M217: the year came from Jellyfin and may be the import year
                    // (see the note above) — the same query WITHOUT it is the retry
                    // that recovers such a title. One extra call, only on a miss.
                    string retryUrl = TmdbUrl($"search/{kind}?query={Uri.EscapeDataString(title)}");
                    using var retryReq = BuildTmdbRequest(retryUrl);
                    using var retryResp = await _http.SendAsync(retryReq, ct).ConfigureAwait(false);
                    TraceCall("GET", retryUrl, (int)retryResp.StatusCode);
                    if (retryResp.IsSuccessStatusCode)
                    {
                        using var retryDoc = await JsonDocument.ParseAsync(await retryResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                        hits = ReadTitleSearchHits(retryDoc);
                        if (hits.Count > 0)
                        {
                            // The year was wrong; the title alone was enough. The RETRY stays
                            // (F-M217); the counter that used to report it here was withdrawn
                            // (operator order 08.10.2026), so nothing is raised — the log line
                            // below the search is what names the recovery now.
                        }
                    }
                }

                if (hits.Count > 0)
                {
                    // Keep BOTH ids — the search-hit's TMDB id AND its IMDb
                    tmdb = hits[0];
                    imdb = await ResolveImdbAsync(tmdb, isSeries, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // search/parse error → null; cached negative
        }

        lock (_cache)
        {
            _cache[key] = imdb;
        }

        return (imdb, tmdb); // Both ids (tmdb may be null on resolve miss)
    }

    /// <summary>
    /// Reads the numeric tmdb ids out of a title-search response (F-M217).
    /// </summary>
    /// <param name="doc">The parsed search payload.</param>
    /// <returns>Ids in response order; empty when the payload carries none.</returns>
    private static System.Collections.Generic.List<string> ReadTitleSearchHits(JsonDocument doc)
    {
        var ids = new System.Collections.Generic.List<string>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var hit in results.EnumerateArray())
        {
            if (hit.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
            {
                ids.Add(idEl.GetInt32().ToString(CultureInfo.InvariantCulture));
            }
        }

        return ids;
    }

    /// <summary>
    /// F-M191 (24.09.2026): what is this IMDb id — an episode or the show itself?
    /// TMDB's /find answers tv_episode_results for an episode (its "show_id" IS the
    /// series TMDB id) and tv_results for the show. Verified live: tt38949652 →
    /// episode S1E10 of show 291084 (Best Medicine), tt21289596 → S3E3 of 95480
    /// (Slow Horses), tt6037988 → the show itself (Last Seen, 258230).
    /// </summary>
    /// <param name="imdbId">IMDb id, episode-level or show-level.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>(seriesTmdbId, isEpisode) or (null,false) when TMDB knows nothing.</returns>
    private async Task<(string? TvId, bool IsEpisode)> FindShowByImdbAsync(string imdbId, CancellationToken ct)
    {
        var key = ("showOf:" + imdbId, false);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                var parts = cached?.Split('|');
                return parts is { Length: 2 }
                    ? (parts[0].Length > 0 ? parts[0] : null, parts[1] == "1")
                    : (null, false);
            }
        }

        string? tvId = null;
        bool isEpisode = false;
        try
        {
            string url = TmdbUrl($"find/{Uri.EscapeDataString(imdbId)}") + "&external_source=imdb_id";
            using var req = BuildTmdbRequest(url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            TraceCall("GET", url, (int)resp.StatusCode);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                HandleAuthFailure();
            }
            else if (resp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                var root = doc.RootElement;
                if (TryFirstArrayId(root, "tv_episode_results", "show_id", out var showId))
                {
                    tvId = showId;
                    isEpisode = true;
                }
                else if (TryFirstArrayId(root, "tv_results", "id", out var seriesId))
                {
                    tvId = seriesId;
                }
            }
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"[TMDB] find-show failed ({imdbId}): {ex.Message}");
        }

        lock (_cache)
        {
            _cache[key] = $"{(tvId ?? string.Empty)}|{(isEpisode ? "1" : "0")}";
        }

        return (tvId, isEpisode);
    }

    /// <summary>
    /// F-M202 (25.09.2026, user decision): Jellyfin carries an EPISODE TMDB id and the
    /// file name carries season/episode — together they prove which SHOW the file belongs
    /// to. /find cannot walk an episode id up (verified: find?external_source=tmdb_id
    /// returns EMPTY), but the episode id can be CONFIRMED from the other side: search the
    /// title from the FILE NAME, walk to that show's S/E, and accept the show only when the
    /// episode id there equals Jellyfin's id. Measured on the test library: 9/9 exact
    /// matches (Best Medicine S1E1 → 6676484, … S2E1 → 7530224, Last Seen S1E1 → 7321029).
    ///
    /// This closes the gap F-M191 left open: with no IMDb id and only an episode TMDB id,
    /// the 10 affected files had no path to a show id at all.
    ///
    /// SAFETY — this is NEVER a guess. Title search can return several shows ("Last Seen"
    /// also matches "Last Seen On Holiday", "Last Seen Alive"); the episode-id equality
    /// check is the only thing that makes the mapping provable. No confirmed match ⇒
    /// (null, null) ⇒ the item is skipped, exactly like any other unresolvable series.
    /// </summary>
    /// <param name="episodeTmdbId">Jellyfin's TMDB id, known to be an EPISODE id.</param>
    /// <param name="title">Series title taken from the FILE NAME.</param>
    /// <param name="year">First-air year or null.</param>
    /// <param name="season">Season number from the file name.</param>
    /// <param name="episode">Episode number from the file name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>(showImdb, showTmdb) on a CONFIRMED match, else (null, null).</returns>
    public async Task<(string? Imdb, string? Tmdb)> ResolveShowByEpisodeIdAsync(
        string episodeTmdbId, string title, int? year, int season, int episode, CancellationToken ct)
    {
        if (!IsConfigured || AuthBroken || string.IsNullOrWhiteSpace(episodeTmdbId)
            || string.IsNullOrWhiteSpace(title) || season < 0 || episode < 0)
        {
            return (null, null);
        }

        var cacheKey = ($"showByEp:{episodeTmdbId}|{title.Trim().ToUpperInvariant()}|{season}|{episode}", true);
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                var p = cached?.Split('|');
                return p is { Length: 2 }
                    ? (p[0].Length > 0 ? p[0] : null, p[1].Length > 0 ? p[1] : null)
                    : (null, null);
            }
        }

        string? showImdb = null;
        string? showTmdb = null;
        try
        {
            var multi = await ResolveByMultiSearchAsync(title, year, true, ct).ConfigureAwait(false);
            if (!multi.Found || string.IsNullOrWhiteSpace(multi.Tmdb))
            {
                return (null, null);
            }

            string candidateShow = multi.Tmdb!;

            // The proof: does THIS show carry an episode with Jellyfin's episode id at
            // the season/episode the file name states?
            string url = TmdbUrl($"tv/{candidateShow}/season/{season}/episode/{episode}");
            using (var req = BuildTmdbRequest(url))
            using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
            {
                TraceCall("GET", url, (int)resp.StatusCode);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    HandleAuthFailure();
                    return (null, null);
                }

                if (resp.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                    if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                    {
                        string tmdbEpisodeId = idEl.GetInt32().ToString(CultureInfo.InvariantCulture);
                        if (tmdbEpisodeId.Equals(episodeTmdbId, StringComparison.Ordinal))
                        {
                            showTmdb = candidateShow;
                            showImdb = multi.Imdb;

                            // A series' IMDb id: the multi-search hit may carry one, but the
                            // detail endpoint is authoritative for the show (the multi result
                            // for a mis-typed item often has it empty).
                            if (string.IsNullOrWhiteSpace(showImdb))
                            {
                                showImdb = await ResolveImdbAsync(candidateShow, true, ct).ConfigureAwait(false);
                            }

                            IdMismatch?.Invoke(
                                $"Episode id confirmed for \"{title}\": Jellyfin says tmdb={episodeTmdbId} (episode S{season}E{episode}) — using the series-level ids imdb={showImdb ?? "-"}, tmdb={showTmdb} (TMDB wins).");
                            Trace?.Invoke($"[TMDB] episode id {episodeTmdbId} confirmed as S{season}E{episode} of show {candidateShow} — series ids {showImdb ?? "-"}/{showTmdb}");
                        }
                        else
                        {
                            Trace?.Invoke($"[TMDB] episode id {episodeTmdbId} NOT confirmed against show {candidateShow} S{season}E{episode} (found {tmdbEpisodeId}) — refusing the mapping");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"[TMDB] show-by-episode-id failed ({episodeTmdbId}): {ex.Message}");
        }

        lock (_cache)
        {
            _cache[cacheKey] = $"{(showImdb ?? string.Empty)}|{(showTmdb ?? string.Empty)}";
        }

        return (showImdb, showTmdb);
    }

    /// <summary>Reads a numeric id out of the first entry of a /find result array.</summary>
    /// <param name="root">Parsed /find response.</param>
    /// <param name="listName">Array name (tv_results, tv_episode_results, …).</param>
    /// <param name="prop">Property holding the id (id, show_id).</param>
    /// <param name="id">The id as a string, or null.</param>
    /// <returns>True when an id was found.</returns>
    private static bool TryFirstArrayId(JsonElement root, string listName, string prop, out string? id)
    {
        id = null;
        if (!root.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            return false;
        }

        if (list[0].TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number)
        {
            id = el.GetInt32().ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    /// <summary>Is this TMDB id a SHOW id at all? /tv/{episodeId} answers 404.</summary>
    /// <param name="tmdbId">TMDB id to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when /tv/{id} answers with a success status.</returns>
    private async Task<bool> IsShowIdAsync(string tmdbId, CancellationToken ct)
    {
        var key = ("isShow:" + tmdbId, false);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached == "1";
            }
        }

        bool ok = false;
        try
        {
            string url = TmdbUrl($"tv/{Uri.EscapeDataString(tmdbId)}");
            using var req = BuildTmdbRequest(url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            TraceCall("GET", url, (int)resp.StatusCode);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                HandleAuthFailure();
            }
            else
            {
                ok = resp.IsSuccessStatusCode;
            }
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"[TMDB] show-id check failed ({tmdbId}): {ex.Message}");
        }

        lock (_cache)
        {
            _cache[key] = ok ? "1" : "0";
        }

        return ok;
    }

    /// <summary>
    /// F-M191 (24.09.2026, user decision): a SERIES item must carry SHOW-level ids —
    /// TMDB and SubDL expect the tvshow id together with season and episode, never an
    /// episode id. Jellyfin's provider ids for a mis-typed episode (an "S01E05" file
    /// in a library typed "movies") are EPISODE ids in BOTH fields — measured live:
    /// Tmdb 6676484 = episode S1E1, Imdb tt38949652 = episode S1E10 of the SAME show.
    /// Those went to /tv/{id}, which answers 404; the code then silently kept them, so
    /// the upload carried an episode id where the series id belongs (and the ID
    /// conflict report could never fire, because the 404 path skipped the comparison).
    /// Every correction is reported at Normal level, TMDB being the source of truth.
    /// </summary>
    /// <param name="jfImdb">Jellyfin's IMDb id (may be null/empty).</param>
    /// <param name="jfTmdb">Jellyfin's TMDB id (may be null/empty).</param>
    /// <param name="title">Series name for the report line.</param>
    /// <param name="year">First-air year or null (F-M202 title-search hint).</param>
    /// <param name="season">Season from the FILE NAME (-1 when unknown, F-M202).</param>
    /// <param name="episode">Episode from the FILE NAME (-1 when unknown, F-M202).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>(imdb, tmdb) at SHOW level — individually null when unresolvable.</returns>
    public async Task<(string? Imdb, string? Tmdb)> NormalizeSeriesLevelIdsAsync(
        string? jfImdb, string? jfTmdb, string title, int? year, int season, int episode, CancellationToken ct)
    {
        string? imdb = string.IsNullOrWhiteSpace(jfImdb) ? null : jfImdb;
        string? tmdb = string.IsNullOrWhiteSpace(jfTmdb) ? null : jfTmdb;

        if (!IsConfigured || AuthBroken || (imdb == null && tmdb == null))
        {
            return (imdb, tmdb);
        }

        string? showImdb = imdb;
        string? showTmdb = tmdb;
        bool changed = false;

        if (imdb != null)
        {
            // The IMDb id is the working entry point: /find tells us what the item is.
            var (tvId, isEpisode) = await FindShowByImdbAsync(imdb, ct).ConfigureAwait(false);
            if (tvId != null)
            {
                if (isEpisode)
                {
                    // Episode-level IMDb → take the SHOW's IMDb (null when TMDB has
                    // none: an episode id must never reach the upload metadata).
                    showImdb = await ResolveImdbAsync(tvId, true, ct).ConfigureAwait(false);
                    changed = true;
                }

                if (showTmdb == null)
                {
                    showTmdb = tvId;
                    changed = true;
                }
                else if (!showTmdb.Equals(tvId, StringComparison.OrdinalIgnoreCase))
                {
                    // An episode TMDB id sitting next to a show-level IMDb id.
                    showTmdb = tvId;
                    changed = true;
                }
            }
            else
            {
                // F-M191b (24.09.2026): TMDB does not know this IMDb id AT ALL
                // (/find returns every result array empty) and the TMDB id — if
                // present — is not a show id either. For a SERIES item the IMDb id
                // cannot be trusted then: it is typically an episode id TMDB does
                // not index (measured: tt11358534, Best Medicine S02E01 — /find
                // empty, tmdb 7530224 → 404). Keeping it silently uploaded an
                // episode IMDb id as the SERIES id, which is exactly what a series
                // upload must never carry. Drop both ids so the caller's type-free
                // TMDB title search resolves the show by name ("Best Medicine" →
                // tv 291084, imdb tt36885662) — the same fail-closed rule as
                // "no IMDb resolvable ⇒ no upload".
                showImdb = null;
                if (tmdb != null && !await IsShowIdAsync(tmdb, ct).ConfigureAwait(false))
                {
                    showTmdb = null;
                }

                changed = true;
            }
        }
        else if (tmdb != null && !await IsShowIdAsync(tmdb, ct).ConfigureAwait(false))
        {
            // Episode-level TMDB id WITHOUT an IMDb id. F-M191 dropped it here, which
            // left the item with no ids at all. F-M202 (25.09.2026): the episode id
            // plus the file name's S/E can PROVE the show — try that first, and only
            // drop the id when the proof fails.
            var (byEpImdb, byEpTmdb) = await ResolveShowByEpisodeIdAsync(tmdb, title, year, season, episode, ct).ConfigureAwait(false);
            if (byEpTmdb != null)
            {
                showImdb = byEpImdb;
                showTmdb = byEpTmdb;
            }
            else
            {
                // /find cannot walk an episode id up (find?external_source=tmdb_id
                // returns EMPTY for 6817835, verified), so drop it instead of burning
                // 404 round-trips every run.
                showTmdb = null;
            }

            changed = true;
        }

        if (changed)
        {
            IdMismatch?.Invoke(
                $"Episode-level ids for \"{title}\": Jellyfin says imdb={jfImdb ?? "-"}, tmdb={jfTmdb ?? "-"} — using the series-level ids imdb={showImdb ?? "-"}, tmdb={showTmdb ?? "-"} (TMDB wins).");
            Trace?.Invoke($"[TMDB] series-level normalization \"{title}\": {jfImdb ?? "-"}/{jfTmdb ?? "-"} → {showImdb ?? "-"}/{showTmdb ?? "-"}");
        }

        return (showImdb, showTmdb);
    }

    /// <summary>
    /// F-M151 (20.09.2026): upload ID quality gate. Validates and/or corrects the IMDb/TMDB
    /// IDs that Jellyfin reports for a media item. Missing IDs are filled via TMDB,
    /// conflicting IDs are corrected towards the TMDB source of truth, and items that
    /// cannot be resolved at all return nulls so the caller can skip them.
    ///
    /// For episodes this always operates on the SERIES level (tvshow IMDB/TMDB), never the
    /// episode IMDB — the SubDL upload metadata requires series id + season + episode.
    /// </summary>
    /// <param name="jfImdb">Jellyfin's current IMDb ID (may be null/empty).</param>
    /// <param name="jfTmdb">Jellyfin's current TMDB ID (may be null/empty).</param>
    /// <param name="title">Movie title or SERIES name for title-search fallback.</param>
    /// <param name="year">Release/first-air year or null.</param>
    /// <param name="isSeries">True for tv series (episode), false for movie.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>(imdbId, tmdbId) — corrected/filled or (null,null) when unresolvable.</returns>
    /// F-M28a: cross-checks and corrects Jellyfin's ids via TMDb whenever a key is configured.
    public async Task<(string? Imdb, string? Tmdb)> ResolveAndValidateIdsAsync(string? jfImdb, string? jfTmdb, string title, int? year, bool isSeries, CancellationToken ct)
    {
        if (!IsConfigured || AuthBroken)
        {
            // Gate is active in config, but TMDB is unavailable — keep Jellyfin's IDs as-is.
            return (string.IsNullOrWhiteSpace(jfImdb) ? null : jfImdb,
                    string.IsNullOrWhiteSpace(jfTmdb) ? null : jfTmdb);
        }

        jfImdb = string.IsNullOrWhiteSpace(jfImdb) ? null : jfImdb;
        jfTmdb = string.IsNullOrWhiteSpace(jfTmdb) ? null : jfTmdb;

        string? resolvedImdb = null;
        string? resolvedTmdb = null;

        // Case 1: both IDs present — cross-validate via TMDB.
        if (jfImdb != null && jfTmdb != null)
        {
            var imdbFromTmdb = await ResolveImdbAsync(jfTmdb, isSeries, ct).ConfigureAwait(false);
            if (imdbFromTmdb != null && !imdbFromTmdb.Equals(jfImdb, StringComparison.OrdinalIgnoreCase))
            {
                // F-M190 (24.09.2026, user decision): report the disagreement at
                // NORMAL level (not only in the debug trace) and let TMDB win.
                IdMismatch?.Invoke($"ID conflict for \"{title}\": Jellyfin says {jfImdb}, TMDB says {imdbFromTmdb} (tmdb={jfTmdb}) — using the TMDB id.");
                Trace?.Invoke($"[TMDB] ID mismatch: JF imdb={jfImdb}, TMDB says {imdbFromTmdb} for tmdb={jfTmdb} — correcting to TMDB");
                resolvedImdb = imdbFromTmdb;
                resolvedTmdb = jfTmdb;
            }
            else
            {
                // TMDB either confirms the pair or has nothing to add — Jellyfin's
                // ids stand (user rule: no TMDB imdb ⇒ take the JF ids).
                resolvedImdb = jfImdb;
                resolvedTmdb = jfTmdb;
            }
        }
        else if (jfTmdb != null)
        {
            // Case 2: only TMDB — resolve IMDb.
            resolvedTmdb = jfTmdb;
            resolvedImdb = await ResolveImdbAsync(jfTmdb, isSeries, ct).ConfigureAwait(false);
        }
        else if (jfImdb != null)
        {
            // Case 3: only IMDb — resolve TMDB (best effort, never blocking: an
            // upload with imdb alone is valid).
            resolvedImdb = jfImdb;
            resolvedTmdb = await ResolveTmdbByImdbAsync(jfImdb, ct).ConfigureAwait(false);
        }
        else
        {
            // Case 4 (F-M190, user decision 24.09.2026): NEITHER id present. The
            // standard path is now a TYPE-FREE TMDB query: TMDB decides whether
            // the title is a film or a series (Jellyfin's library typing is not
            // trusted for this — an episode in a "movies" library is imported as
            // a Movie), and its ids are the answer. Without an IMDb id from TMDB
            // there is nothing to upload with, so the caller skips the item.
            var multi = await ResolveByMultiSearchAsync(title, year, isSeries, ct).ConfigureAwait(false);
            if (multi.Found)
            {
                return (multi.Imdb, multi.Tmdb);
            }

            return (null, null);
        }

        return (resolvedImdb, resolvedTmdb);
    }

}
