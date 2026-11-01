using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Registry;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// F-M372: holds back the episodes of a complete season until the season is whole, then sends ONE
/// pack instead of one upload per episode.
/// <para>
/// The two rules the operator set decide everything here: an incomplete season stays single, and so
/// does a season whose episodes did not all pass the gates. The buffer therefore only ever collects;
/// whether a pack happens is decided when it is full, and what is left over at the end of the run
/// goes up the single way.
/// </para>
/// <para>
/// A pack is always one language, one HI flag, one source: <c>lang</c>, <c>hi</c>, <c>quality</c>,
/// <c>framerate</c> and <c>releases</c> are single fields on SubDL's side, so mixed episodes cannot
/// share a container.
/// </para>
/// </summary>
public sealed class SeasonPackCoordinator
{
    /// <summary>One episode's contribution to a possible pack.</summary>
    /// <param name="Item">Jellyfin item (for the per-item outcome report).</param>
    /// <param name="ItemId">Item id as the queue knows it.</param>
    /// <param name="MediaHash">Media hash that keys the registry row.</param>
    /// <param name="ImdbId">Series IMDb id.</param>
    /// <param name="TmdbId">Series TMDB id, when known.</param>
    /// <param name="Season">Season number.</param>
    /// <param name="Episode">Episode number.</param>
    /// <param name="Lang">SubDL language code.</param>
    /// <param name="HearingImpaired">HI/SDH variant.</param>
    /// <param name="Forced">Forced track flag (never part of a pack).</param>
    /// <param name="SubPos">ffmpeg stream position of the source track.</param>
    /// <param name="Srt">Subtitle text, already canonical.</param>
    /// <param name="ReleaseName">Release name the episode came from.</param>
    /// <param name="Quality">Documented quality code.</param>
    /// <param name="Framerate">Documented frame rate code.</param>
    /// <param name="VideoWidth">Video width in pixels — the file's own word on its resolution class.</param>
    /// <param name="VideoHeight">Video height in pixels — kept for cropped encodes that need it as a fallback.</param>
    /// <param name="VideoCodec">Video codec name from the stream.</param>
    public sealed record Candidate(
        BaseItem Item,
        string ItemId,
        string MediaHash,
        string ImdbId,
        string? TmdbId,
        int Season,
        int Episode,
        string Lang,
        bool HearingImpaired,
        bool Forced,
        int SubPos,
        string Srt,
        string ReleaseName,
        string Quality,
        string Framerate,
        int? VideoWidth = null,
        int? VideoHeight = null,
        string? VideoCodec = null);

    private readonly SubdlApiClient _api;
    private readonly ContentHashRegistry _registry;
    private readonly ILogger _logger;
    private readonly PluginConfiguration _config;
    private readonly IReadOnlyDictionary<(string ImdbId, int Season), IReadOnlyList<int>> _complete;
    private readonly ISet<(string ImdbId, int Season)> _packable;
    private readonly Dictionary<(string ImdbId, int Season, string Lang, bool Hi), List<Candidate>> _buffers = new();
    private readonly Action<string, int> _countUploaded;
    private readonly Action<int> _countFailed;
    private readonly Action<string> _reportDone;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonPackCoordinator"/> class.
    /// </summary>
    /// <param name="api">SubDL client.</param>
    /// <param name="registry">Content hash registry.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="complete">Seasons that are complete, with their aired episode set.</param>
    /// <param name="packable">
    /// Seasons whose QUEUED episodes cover the whole aired season. Only those are buffered: a season
    /// whose other episodes are already up individually cannot become a pack, and making it wait for
    /// the end of the run would delay its uploads for nothing (F-M373).
    /// </param>
    /// <param name="countUploaded">Called with (season label, number uploaded) after a pack.</param>
    /// <param name="countFailed">Called with the number of failed episodes.</param>
    /// <param name="reportDone">Called with an item id whose position is done.</param>
    public SeasonPackCoordinator(
        SubdlApiClient api,
        ContentHashRegistry registry,
        ILogger logger,
        PluginConfiguration config,
        IReadOnlyDictionary<(string ImdbId, int Season), IReadOnlyList<int>> complete,
        ISet<(string ImdbId, int Season)> packable,
        Action<string, int> countUploaded,
        Action<int> countFailed,
        Action<string> reportDone)
    {
        _api = api;
        _registry = registry;
        _logger = logger;
        _config = config;
        _complete = complete;
        _packable = packable;
        _countUploaded = countUploaded;
        _countFailed = countFailed;
        _reportDone = reportDone;
    }

    /// <summary>True when this season is complete AND fully queued, therefore worth buffering.</summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <returns>True when buffering applies.</returns>
    public bool ShouldBuffer(string? imdbId, int season)
        => !string.IsNullOrWhiteSpace(imdbId) && season > 0
           && _complete.ContainsKey((imdbId!, season))
           && _packable.Contains((imdbId!, season));

    /// <summary>
    /// Adds an episode's candidate to its buffer and uploads the pack as soon as the buffer holds
    /// every aired episode.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task AddAsync(Candidate candidate, CancellationToken ct)
    {
        var key = (candidate.ImdbId, candidate.Season, candidate.Lang, candidate.HearingImpaired);
        if (!_buffers.TryGetValue(key, out var list))
        {
            list = new List<Candidate>();
            _buffers[key] = list;
        }

        // One position per episode — a second track of the same language on the same episode cannot
        // be expressed in a pack, so it goes up single (the caller only buffers the first one).
        if (list.All(c => c.Episode != candidate.Episode))
        {
            list.Add(candidate);
        }

        var aired = _complete[(candidate.ImdbId, candidate.Season)];
        if (list.Count >= aired.Count)
        {
            _buffers.Remove(key);
            await UploadAsync(key, list, aired, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the run: every buffer that never got full because an episode was missing, failed a
    /// gate or carried no subtitle for that language goes up the single way.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task FlushAsync(CancellationToken ct)
    {
        foreach (var entry in _buffers.ToList())
        {
            _buffers.Remove(entry.Key);
            var aired = _complete[(entry.Key.ImdbId, entry.Key.Season)];
            if (entry.Value.Count >= aired.Count)
            {
                await UploadAsync(entry.Key, entry.Value, aired, ct).ConfigureAwait(false);
                continue;
            }

            LogUtil.Normal(
                _logger,
                "[SubDL] season pack skipped — {Imdb} S{Season} [{Lang}]: {Have} of {Want} episode(s), the rest stays single.",
                entry.Key.ImdbId, entry.Key.Season, entry.Key.Lang, entry.Value.Count, aired.Count);
            await UploadSinglesAsync(entry.Value, ct).ConfigureAwait(false);
        }
    }

    private async Task UploadAsync(
        (string ImdbId, int Season, string Lang, bool Hi) key,
        List<Candidate> group,
        IReadOnlyList<int> aired,
        CancellationToken ct)
    {
        var ordered = group.OrderBy(c => c.Episode).ToList();

        // A pack carries one release, one quality, one frame rate. Mixed sources are not a pack —
        // they are episodes that happen to share a season.
        // The release names differ per episode by nature ("Show.S01E03.1080p" vs "...E04..."), and
        // SubDL stores them as an ARRAY — so a pack carries all of them. What must not differ is the
        // SOURCE: quality and frame rate describe the whole season, and a container can only claim one.
        bool uniform = ordered.Select(c => c.Quality).Distinct().Count() == 1
                       && ordered.Select(c => c.Framerate).Distinct().Count() == 1;
        if (!uniform || ordered.Any(c => c.Forced))
        {
            LogUtil.Normal(
                _logger,
                "[SubDL] season pack skipped — {Imdb} S{Season} [{Lang}]: the episodes do not share one source, they stay single.",
                key.ImdbId, key.Season, key.Lang);
            await UploadSinglesAsync(ordered, ct).ConfigureAwait(false);
            return;
        }

        // SubDL builds the container itself: one file per call, then one metadata call with the
        // array of file ids. What we owe is the pack's NAME and its identity — the MD5 over the
        // episodes' content hashes, in episode order.
        // F-M378 (measured 01.11.2026): SubDL reads season and episode out of each file's OWN name —
        // `tt0804484.S01E01.<hash>.EN.srt` comes back as (s=1, e=1), `01 The Emperor's Peace.en.srt`
        // as (s=0, e=1). A member that names a season makes the whole submission an episode range
        // (episode_from/episode_end set, full_season=false, i.e. no pack). The members therefore
        // carry the episode NUMBER AND TITLE and no season, which is also how the seasoned
        // uploaders' packs are built inside.
        var files = ordered
            .Select(c => (FileName: SeasonPack.MemberFileName(key.Season, c.Episode, c.Item?.Name, key.Lang, key.Hi), Srt: c.Srt))
            .ToList();
        string name = SeasonPack.Name(
            key.ImdbId, key.Season, SeasonPack.PackHash(files.Select(f => ContentHashRegistry.ComputeHash(f.Srt))), key.Lang, key.Hi);
        var first = ordered[0];

        if (_config.DryRun)
        {
            // A dry run answers the question the switch asks — which seasons WOULD go up as a pack —
            // without writing anything to SubDL.
            LogUtil.Normal(
                _logger,
                "[SubDL] DRY-RUN would upload season pack {Name} — {Count} episode(s), {Imdb} S{Season} [{Lang}], Ef/Ee {From}-{To}",
                name, ordered.Count, key.ImdbId, key.Season, key.Lang, ordered.Min(c => c.Episode), ordered.Max(c => c.Episode));
            return;
        }

        UploadResult result = await _api.UploadSeasonPackAsync(
            files,
            name,
            key.Lang,
            PackReleases(ordered, key),
            first.Quality,
            key.ImdbId,
            first.TmdbId,
            key.Season,
            ordered.Min(c => c.Episode),
            ordered.Max(c => c.Episode),
            ct,
            key.Hi,
            first.Framerate).ConfigureAwait(false);

        if (!result.Ok)
        {
            // The one unproven step is the container itself (does SubDL take a zip here?). A refusal
            // at that step must not cost the season its subtitles, so it falls back to singles; a
            // rate limit or a real failure leaves the positions due for the next run.
            bool containerRefused = result.Reason != null && result.Reason.StartsWith("pack-file-refused", StringComparison.Ordinal);
            if (containerRefused || result.SkippedItem)
            {
                LogUtil.Normal(
                    _logger,
                    "[SubDL] season pack refused ({Reason}) — {Imdb} S{Season} [{Lang}] stays single.",
                    result.Reason ?? "skipped", key.ImdbId, key.Season, key.Lang);
                await UploadSinglesAsync(ordered, ct).ConfigureAwait(false);
                return;
            }

            _countFailed(ordered.Count);
            _logger.LogError("[SubDL] SEASON PACK FAILED {Imdb} S{Season} [{Lang}]: {Reason}", key.ImdbId, key.Season, key.Lang, result.Reason);
            return;
        }

        // The Upload API answers a completed upload with {"status":true,"message":"subtitle sent for
        // review"} — no id. The pack's identity in our own rows is therefore the pack NAME when the
        // response carries no id: it is unique per season and content, and it is what the
        // postprocessing pass can match against /user/mySubtitles.
        string? packId = ReadPackId(result.Response) ?? name;
        foreach (var candidate in ordered)
        {
            var c = candidate;
            _registry.MarkAndFlush(() => _registry.MarkEmbed(
                c.MediaHash, c.SubPos, c.Lang, c.HearingImpaired,
                SubtitleStatus.Uploaded, contentHash: ContentHashRegistry.ComputeHash(c.Srt),
                subdlId: packId, forced: c.Forced));
            _reportDone(c.ItemId);
        }

        _countUploaded($"{key.ImdbId} S{key.Season} [{key.Lang}]", ordered.Count);
        LogUtil.Normal(
            _logger,
            "[SubDL] SEASON PACK {Name} — {Count} episode(s), {Imdb} S{Season} [{Lang}], Ef/Ee {From}-{To}, id {Id}",
            name, ordered.Count, key.ImdbId, key.Season, key.Lang,
            ordered.Min(c => c.Episode), ordered.Max(c => c.Episode), packId ?? "-");
    }

    /// <summary>
    /// F-M374: SubDL titles a pack after the FIRST entry of `releases`, so it carries the season
    /// release — "…S01E05…" reduced to "…S01…", never the episode and never a mixture of groups.
    /// </summary>
    /// <param name="ordered">Candidates in episode order.</param>
    /// <param name="key">Buffer key.</param>
    /// <returns>Release list with the season label up front.</returns>
    private static List<string> PackReleases(List<Candidate> ordered, (string ImdbId, int Season, string Lang, bool Hi) key)
    {
        Candidate first = ordered[0];
        string? built = SeasonPack.MediaRelease(
            SeriesNameOf(first.Item),
            key.ImdbId,
            key.Season,
            first.Quality,
            first.VideoWidth,
            first.VideoHeight,
            first.VideoCodec,
            SeasonPack.ReleaseGroup(first.ReleaseName));

        return new List<string>
        {
            SeasonPack.PackRelease(
                ordered.Select(c => c.ReleaseName),
                SeriesNameOf(first.Item),
                key.ImdbId,
                key.Season,
                key.Lang,
                key.Hi,
                built)
        };
    }

    /// <summary>
    /// Jellyfin's series name for an episode item, when it has one.
    /// </summary>
    /// <param name="item">Episode item.</param>
    /// <returns>Series name or null.</returns>
    private static string? SeriesNameOf(BaseItem item)
        => item is Episode episode && !string.IsNullOrWhiteSpace(episode.SeriesName)
            ? episode.SeriesName
            : null;

    private async Task UploadSinglesAsync(List<Candidate> group, CancellationToken ct)
    {
        foreach (var c in group.OrderBy(x => x.Episode))
        {
            ct.ThrowIfCancellationRequested();
            var neutral = SeasonPack.NeutralFileName(true, c.ImdbId, c.Season, c.Episode, c.Lang, ContentHashRegistry.ComputeHash(c.Srt), c.HearingImpaired);
            UploadResult result = await _api.UploadSubtitleAsync(
                c.Srt, neutral, c.Lang, c.ReleaseName, c.Quality, c.ImdbId, c.TmdbId, true, c.Season, c.Episode,
                ct, c.HearingImpaired, c.Framerate).ConfigureAwait(false);

            if (result.Ok)
            {
                var candidate = c;
                _registry.MarkAndFlush(() => _registry.MarkEmbed(
                    candidate.MediaHash, candidate.SubPos, candidate.Lang, candidate.HearingImpaired,
                    SubtitleStatus.Uploaded, contentHash: ContentHashRegistry.ComputeHash(candidate.Srt),
                    forced: candidate.Forced));
                _countUploaded($"{c.ImdbId} S{c.Season} [{c.Lang}]", 1);
                _reportDone(c.ItemId);
                LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] UPLOADED {File} [{Lang}]", neutral, c.Lang);
                continue;
            }

            if (result.SkippedItem)
            {
                LogUtil.PerItem(_config.LogMode, _logger, "[SubDL] SKIP {File} [{Lang}] — {Reason}", neutral, c.Lang, result.Reason ?? "skipped");
                continue;
            }

            _countFailed(1);
            _logger.LogError("[SubDL] UPLOAD FAILED {File} [{Lang}]: {Reason}", neutral, c.Lang, result.Reason);
        }
    }

    /// <summary>
    /// Reads the SubDL upload id out of the uploadSubtitle response. It is what ties the episodes to
    /// the one remote object, so a later rejection can be fanned out over all of them.
    /// </summary>
    private static string? ReadPackId(JsonDocument? response)
    {
        if (response == null)
        {
            return null;
        }

        var root = response.RootElement;
        foreach (var path in new[] { new[] { "data", "upload", "id" }, new[] { "upload", "id" }, new[] { "id" }, new[] { "subtitle_id" } })
        {
            JsonElement cursor = root;
            bool ok = true;
            foreach (var step in path)
            {
                if (cursor.ValueKind == JsonValueKind.Object && cursor.TryGetProperty(step, out var next))
                {
                    cursor = next;
                }
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                string? text = cursor.ValueKind switch
                {
                    JsonValueKind.String => cursor.GetString(),
                    JsonValueKind.Number => cursor.GetRawText(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }
}
