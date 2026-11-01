using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M372: how many episodes a season HAS — asked at IMDb, TMDB as the fallback.
/// <para>
/// A season pack claims to SubDL that the season is complete (<c>is_full_season=true</c>).
/// That claim is about the season that AIRED, not about the folder on disk: eight of ten
/// episodes is a complete folder and an incomplete season. The library can therefore not
/// answer the question — it only knows what it holds.
/// </para>
/// <para>
/// IMDb's own GraphQL endpoint answers it per season. Two details matter:
/// every episode carries <c>series.episodeNumber</c>, and episode <b>0</b> is a real entry
/// (Game of Thrones S01 reports 11 episodes: ten plus the unaired original pilot at E0),
/// so the aired set is the distinct numbers above zero.
/// </para>
/// </summary>
public static class SeasonEpisodeIndex
{
    private const string GraphQl = "https://caching.graphql.imdb.com/";
    private const string TmdbBase = "https://api.themoviedb.org/3";

    /// <summary>One answer per (imdb id, season) per plugin lifetime — a season never changes.</summary>
    private static readonly Dictionary<(string ImdbId, int Season), IReadOnlyList<int>> Cache = new();

    /// <summary>
    /// Aired episode numbers of one season, ascending. <c>null</c> means "nobody answered" —
    /// the caller must then not pack.
    /// </summary>
    /// <param name="imdbId">Series IMDb id (tt…).</param>
    /// <param name="season">Season number.</param>
    /// <param name="tmdbId">TMDB series id, used for the fallback.</param>
    /// <param name="tmdbApiKey">TMDB API key, empty when TMDB is not configured.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aired episode numbers, or null.</returns>
    public static async Task<IReadOnlyList<int>?> AiredAsync(
        string imdbId, int season, string? tmdbId, string? tmdbApiKey, ILogger? logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || season < 1)
        {
            return null;
        }

        var key = (imdbId, season);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }

        var fromImdb = await FromImdbAsync(imdbId, season, logger, ct).ConfigureAwait(false)
                       ?? await FromTmdbAsync(tmdbId, tmdbApiKey, season, logger, ct).ConfigureAwait(false);

        if (fromImdb is { Count: > 0 })
        {
            lock (Cache)
            {
                Cache[key] = fromImdb;
            }

            return fromImdb;
        }

        return null;
    }

    private static async Task<IReadOnlyList<int>?> FromImdbAsync(string imdbId, int season, ILogger? logger, CancellationToken ct)
    {
        const string Query =
            "query($id: ID!, $s: [String!], $n: Int){ title(id:$id){ episodes { episodes(first:$n, filter:{includeSeasons:$s})" +
            "{ total edges { node { series { episodeNumber { episodeNumber } } } } } } } }";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var body = JsonSerializer.Serialize(new
            {
                query = Query,
                variables = new { id = imdbId, s = new[] { season.ToString(System.Globalization.CultureInfo.InvariantCulture) }, n = 500 }
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, GraphQl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            // IMDb's endpoint answers 403 to strangers; the site's own client name and country
            // are what it expects. Nothing is authenticated by this — it is the public frontend.
            req.Headers.TryAddWithoutValidation("x-imdb-client-name", "imdb-web-next");
            req.Headers.TryAddWithoutValidation("x-imdb-user-country", "US");
            req.Headers.TryAddWithoutValidation("origin", "https://www.imdb.com");
            req.Headers.TryAddWithoutValidation("referer", "https://www.imdb.com/");

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger?.LogDebug("[SubDL] season index: IMDb answered {Status} for {Imdb} S{Season}.", (int)resp.StatusCode, imdbId, season);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("title", out var title)
                || title.ValueKind != JsonValueKind.Object
                || !title.TryGetProperty("episodes", out var eps)
                || !eps.TryGetProperty("episodes", out var conn)
                || !conn.TryGetProperty("edges", out var edges)
                || edges.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var numbers = new List<int>();
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.TryGetProperty("node", out var node)
                    && node.TryGetProperty("series", out var series)
                    && series.ValueKind == JsonValueKind.Object
                    && series.TryGetProperty("episodeNumber", out var en)
                    && en.ValueKind == JsonValueKind.Object
                    && en.TryGetProperty("episodeNumber", out var num)
                    && num.TryGetInt32(out int n))
                {
                    numbers.Add(n);
                }
            }

            return SeasonPack.AiredNumbers(numbers);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger?.LogDebug(ex, "[SubDL] season index: IMDb lookup failed for {Imdb} S{Season}.", imdbId, season);
            return null;
        }
    }

    private static async Task<IReadOnlyList<int>?> FromTmdbAsync(string? tmdbId, string? tmdbApiKey, int season, ILogger? logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tmdbId) || string.IsNullOrWhiteSpace(tmdbApiKey))
        {
            return null;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            string url = $"{TmdbBase}/tv/{tmdbId}/season/{season}?api_key={Uri.EscapeDataString(tmdbApiKey)}";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger?.LogDebug("[SubDL] season index: TMDB answered {Status} for {Tmdb} S{Season}.", (int)resp.StatusCode, tmdbId, season);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("episodes", out var eps) || eps.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var numbers = new List<int>();
            foreach (var ep in eps.EnumerateArray())
            {
                if (ep.TryGetProperty("episode_number", out var n) && n.TryGetInt32(out int num))
                {
                    numbers.Add(num);
                }
            }

            return SeasonPack.AiredNumbers(numbers);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger?.LogDebug(ex, "[SubDL] season index: TMDB lookup failed for {Tmdb} S{Season}.", tmdbId, season);
            return null;
        }
    }
}
