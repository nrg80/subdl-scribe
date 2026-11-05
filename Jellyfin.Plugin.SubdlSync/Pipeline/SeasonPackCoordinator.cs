using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Language;
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
    /// <param name="AbsorbedSingleId">
    /// F-M391: SubDL id of the single upload this member replaces, when the member came from the
    /// filler. The single is deleted BEFORE the pack goes up — the operator's order — because a pack
    /// must not compete with an entry that carries the same content.
    /// </param>
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
        string? VideoCodec = null,
        string? AbsorbedSingleId = null);

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
    private readonly IPackFiller? _filler;

    /// <summary>
    /// F-M406 (operator order 02.11.2026): the run's progress heartbeat. The watchdog aborts a run
    /// after six minutes without one, and the pack loop used to build silently — it was killed at
    /// eleven to twelve minutes with a dozen packs done, which read like "the plugin only manages
    /// five percent". The loop now reports every episode it works on.
    /// </summary>
    private readonly Action? _progress;

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
    /// <param name="filler">
    /// F-M389: supplies the episodes a buffer does not cover, from subtitles that are already
    /// uploaded. Null keeps the pre-02.11.2026 behaviour byte for byte: an incomplete buffer goes
    /// up the single way.
    /// </param>
    public SeasonPackCoordinator(
        SubdlApiClient api,
        ContentHashRegistry registry,
        ILogger logger,
        PluginConfiguration config,
        IReadOnlyDictionary<(string ImdbId, int Season), IReadOnlyList<int>> complete,
        ISet<(string ImdbId, int Season)> packable,
        Action<string, int> countUploaded,
        Action<int> countFailed,
        Action<string> reportDone,
        IPackFiller? filler = null,
        Action? progress = null)
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
        _filler = filler;
        _progress = progress;
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
    /// <para>
    /// F-M389 (operator order 02.11.2026): before that, an incomplete buffer is offered the filler.
    /// An episode whose subtitle is ALREADY on SubDL does not have to be in this run for the season
    /// to be a pack — it is fetched from the library, gated and proven, and then joins the buffer.
    /// Only when EVERY missing episode fills is the pack sent; a season that cannot be completed
    /// stays single, exactly as before, because a partial pack is a false claim.
    /// </para>
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

            if (await TryFillAsync(entry.Key, entry.Value, aired, ct).ConfigureAwait(false))
            {
                continue;
            }

            LogUtil.Normal(
                _logger,
                "[SubDL] season pack skipped — {Imdb} S{Season} [{Lang}]: {Have} of {Want} episode(s), the rest stays single.",
                entry.Key.ImdbId, entry.Key.Season, SeasonPack.LangTag(entry.Key.Lang, entry.Key.Hi), entry.Value.Count, aired.Count);
            await UploadSinglesAsync(entry.Value, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// F-M389: completes an incomplete buffer from the subtitles that are already uploaded, and
    /// sends it as a pack when every missing episode arrived.
    /// <para>
    /// The buffer always holds at least one NEW candidate — it exists only because something was
    /// added to it — so a season whose subtitles are all up never reaches this point, and no
    /// "nothing to send" branch is needed. What the filler adds is filler-only: it is asked per
    /// missing episode, in episode order, and the first null ends the attempt. Half a season is not
    /// a pack, and neither is a pack whose one doubtful member was quietly left out.
    /// </para>
    /// </summary>
    /// <param name="key">Buffer key.</param>
    /// <param name="buffer">The candidates this run collected.</param>
    /// <param name="aired">Aired episode set of the season.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the buffer went up as a pack; false when the singles path has to do it.</returns>
    private async Task<bool> TryFillAsync(
        (string ImdbId, int Season, string Lang, bool Hi) key,
        List<Candidate> buffer,
        IReadOnlyList<int> aired,
        CancellationToken ct)
    {
        if (_filler == null)
        {
            return false;
        }

        var missing = SeasonPack.Uncovered(aired, buffer.Select(c => c.Episode));
        var filled = new List<Candidate>(missing.Count);
        foreach (int episode in missing)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = await _filler
                .FillAsync(key.ImdbId, key.Season, episode, key.Lang, key.Hi, ct)
                .ConfigureAwait(false);
            if (candidate == null)
            {
                // One episode that cannot join is enough: the season goes up as singles. The filler
                // has named the episode and the reason already, so nothing is guessed here.
                return false;
            }

            filled.Add(candidate);
        }

        if (filled.Count == 0)
        {
            return false;
        }

        LogFilled(key, filled.Count, buffer.Count);
        await UploadAsync(key, buffer.Concat(filled).ToList(), aired, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// F-M389 (operator order 02.11.2026, revised the same day): packs a season the queue no longer
    /// touches at all — every episode is up already, so the run has nothing new to send and the pack
    /// consists of what the filler proves is already there.
    /// <para>
    /// The members are re-sent inside the container, which is how SubDL builds a pack; what the rule
    /// forbids is INVENTING one. Each member therefore comes from <see cref="IPackFiller"/>, which
    /// runs the single path's gates and demands the content be known. A season that cannot be
    /// completed from what is up sends nothing at all — there is no single upload to fall back to,
    /// because those subtitles are already on SubDL (that is why <see cref="UploadAsync"/> is asked
    /// not to dissolve a refused container into singles here).
    /// </para>
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="lang">SubDL language code the pack is built for.</param>
    /// <param name="hi">HI/SDH variant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task FillOnlyAsync(string imdbId, int season, string lang, bool hi, CancellationToken ct)
    {
        var key = (imdbId, season, lang, hi);
        if (_filler == null || !_complete.TryGetValue((imdbId, season), out var aired))
        {
            return;
        }

        // F-M415 (operator order 02.11.2026): the decision belongs BEFORE the extraction. Is this
        // season already up as a pack, the run does not extract anything for it — it used to pull
        // every episode out of every file first and only then notice that there was nothing to do.
        if (_registry != null && _registry.HasPack(imdbId, season, lang, hi))
        {
            LogUtil.Normal(
                _logger,
                "[SubDL] season pack skipped — {Imdb} S{Season} [{Lang}]: already up as a pack, nothing is extracted.",
                imdbId, season, SeasonPack.LangTag(lang, hi));
            return;
        }

        // F-M416 (operator order 02.11.2026): completeness is a property of the LANGUAGE, and the
        // state already answers it. A season can be complete for EN and incomplete for DE because
        // two episodes carry no German track; asking "is every aired episode known with this
        // language" costs one look into the store and saves the whole extraction when the answer is
        // no. The remaining episodes still go up — as singles, not as a pack.
        // F-M442: what counts is what is REALLY up — the skip messages used to say "nothing is re-sent —
        // it is already up" even for a season whose singles had never been uploaded.
        var upNow = _registry != null
            ? _registry.EpisodesWithLanguage(imdbId, season, lang, hi)
            : new List<int>();
        string stateSentence = upNow.Count >= aired.Count
            ? $"all {aired.Count} episode(s) are already up as singles, nothing is re-sent"
            : $"{upNow.Count} of {aired.Count} episode(s) are up, the remaining {aired.Count - upNow.Count} go up as singles after this pass";

        if (_registry != null)
        {
            var withLanguage = upNow;
            if (!SeasonPack.IsComplete(aired, withLanguage))
            {
                // F-M433 (operator order 03.11.2026, "wieso geht Tracker einzeln hoch?"): the store only
                // knows what was already uploaded or observed. A season whose texts are going up for the
                // FIRST time looked like a season without that language — the pack was refused and every
                // episode went up as a single, straight from its file. So the files are asked before the
                // pack is dropped; looking at a stream list costs no extraction, which is why F-M416's
                // saving still holds for the case it was meant for (no track at all).
                var carried = new HashSet<int>();
                foreach (int episode in SeasonPack.AiredNumbers(aired))
                {
                    ct.ThrowIfCancellationRequested();
                    if (_filler.FileCarriesLanguage(imdbId, season, episode, lang, hi))
                    {
                        carried.Add(episode);
                    }
                }

                if (!SeasonPack.IsComplete(aired, carried))
                {
                    LogUtil.Normal(
                        _logger,
                        "[SubDL] season pack skipped — {Imdb} S{Season} [{Lang}]: {Have} of {Aired} episode(s) carry this language in the store and {Carried} of {Aired} in their own file — no pack for it, this pass sends nothing.",
                        imdbId, season, SeasonPack.LangTag(lang, hi), withLanguage.Count, aired.Count, carried.Count);
                    return;
                }

                LogUtil.Normal(
                    _logger,
                    "[SubDL] season pack planned from the files — {Imdb} S{Season} [{Lang}]: the store knows {Have} of {Aired}, but all {Carried} file(s) carry the language themselves (F-M433).",
                    imdbId, season, SeasonPack.LangTag(lang, hi), withLanguage.Count, aired.Count, carried.Count);
            }
        }

        // F-M442 (operator 04.11.2026, "Verbesserungsvorschläge 1 bis 4"): quality and frame rate are
        // readable from the path and the stream list — the uniformity check runs BEFORE the first
        // extraction instead of after all of them. Measured 04.11.2026: a season with 7x 23,976 and
        // 3x 23,809 fps had every one of its ten files extracted and was refused afterwards.
        var spread = new List<(int Episode, string? Quality, string? Framerate)>(aired.Count);
        foreach (int episode in SeasonPack.AiredNumbers(aired))
        {
            var (quality, framerate) = _filler.SourceFacts(imdbId, season, episode);
            if (string.IsNullOrWhiteSpace(quality) || string.IsNullOrWhiteSpace(framerate))
            {
                spread.Clear();
                break;
            }

            spread.Add((episode, quality, framerate));
        }

        if (spread.Count == aired.Count)
        {
            var qualities = spread.Select(x => x.Quality!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var framerates = spread.Select(x => x.Framerate!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (qualities.Count > 1 || framerates.Count > 1)
            {
                var deviating = spread
                    .Where(x => !string.Equals(x.Quality, qualities[0], StringComparison.OrdinalIgnoreCase)
                             || !string.Equals(x.Framerate, framerates[0], StringComparison.OrdinalIgnoreCase))
                    .Select(x => $"S{key.season:00}E{x.Episode:00} ({x.Quality}/{x.Framerate})")
                    .ToList();
                LogUtil.Normal(
                    _logger,
                    "[SubDL] season pack skipped before any extraction — {Imdb} S{Season} [{Lang}]: {QualityCount} quality level(s) and {FramerateCount} frame rate(s) across {Aired} episode(s); {Deviating} carries another source, and {State} (F-M442).",
                    key.imdbId, key.season, key.lang, qualities.Count, framerates.Count, aired.Count,
                    string.Join(", ", deviating.Take(6)), stateSentence);
                return;
            }
        }

        var group = new List<Candidate>(aired.Count);
        foreach (int episode in SeasonPack.AiredNumbers(aired))
        {
            ct.ThrowIfCancellationRequested();
            _progress?.Invoke(); // F-M406: an episode being extracted is progress, not a stall
            var candidate = await _filler.FillAsync(imdbId, season, episode, lang, hi, ct).ConfigureAwait(false);
            if (candidate == null)
            {
                // Half a season is not a pack, and there is nothing to send the single way: the
                // episode's subtitle is up already or it never was — either way this run is done.
                LogUtil.Normal(
                    _logger,
                    "[SubDL] season pack skipped — {Imdb} S{Season} [{Lang}]: episode {Episode} cannot join, and {State} (F-M442).",
                    imdbId, season, SeasonPack.LangTag(lang, hi), episode, stateSentence);
                return;
            }

            group.Add(candidate);
        }

        // One release, one quality, one frame rate — the check of the buffered path applies here
        // too, and it is asked BEFORE the upload: a season of mixed sources is not a pack, and
        // re-sending it as singles would only duplicate what is already up.
        bool uniform = group.Select(c => c.Quality).Distinct().Count() == 1
                       && group.Select(c => c.Framerate).Distinct().Count() == 1;
        if (!uniform || group.Any(c => c.Forced))
        {
            LogUtil.Normal(
                _logger,
                "[SubDL] season pack skipped — {Imdb} S{Season} [{Lang}]: the episodes do not share one source, and {State} (F-M442).",
                imdbId, season, SeasonPack.LangTag(lang, hi), stateSentence);
            return;
        }

        LogFilled(key, group.Count, 0);
        await UploadAsync(key, group, aired, ct, uploadSinglesOnRefusal: false).ConfigureAwait(false);
    }

    /// <summary>
    /// F-M389: the one line that says a pack was completed from material that is already uploaded —
    /// how much came from the registry and how much this run collected.
    /// </summary>
    /// <param name="key">Buffer key.</param>
    /// <param name="filled">Members supplied by the filler.</param>
    /// <param name="fresh">Members this run collected itself (0 for a fill-only pack).</param>
    private void LogFilled((string ImdbId, int Season, string Lang, bool Hi) key, int filled, int fresh)
        => LogUtil.Normal(
            _logger,
            "[SubDL] season pack filled — {Imdb} S{Season} [{Lang}]: {Filled} episode(s) from subtitles already uploaded, {New} from this run.",
            key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi), filled, fresh);

    private async Task UploadAsync(
        (string ImdbId, int Season, string Lang, bool Hi) key,
        List<Candidate> group,
        IReadOnlyList<int> aired,
        CancellationToken ct,
        bool uploadSinglesOnRefusal = true)
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
                key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi));
            if (uploadSinglesOnRefusal)
            {
                await UploadSinglesAsync(ordered, ct).ConfigureAwait(false);
            }

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
                name, ordered.Count, key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi), ordered.Min(c => c.Episode), ordered.Max(c => c.Episode));
            return;
        }

        // F-M396: a pack that is already up with exactly these members is not sent again. The fill-only
        // plan has its own guard, but the buffer path had none — so a season whose row says "packed"
        // could still go up a second time just because something was queued for it. Same members and
        // same release means there is nothing new in the container; a NEW episode changes the set and
        // the pack is legitimately sent.
        var recorded = _registry.GetPack(key.ImdbId, key.Season, key.Lang, key.Hi);
        if (recorded != null)
        {
            string want = string.Join(",", ordered.Select(c => c.Episode).OrderBy(e => e));
            bool sameMembers = string.Equals(recorded.Episodes, want, StringComparison.Ordinal);
            bool sameRelease = string.IsNullOrEmpty(recorded.ReleaseName)
                || string.Equals(recorded.ReleaseName, first.ReleaseName, StringComparison.Ordinal);
            if (sameMembers && sameRelease)
            {
                LogUtil.Normal(
                    _logger,
                    "[SubDL] season pack already up with these members — {Imdb} S{Season} [{Lang}]: {Members}, nothing is re-sent.",
                    key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi), want);
                return;
            }
        }

        // F-M397 (operator order 02.11.2026): comes ANY member of this pack from SubDL itself — a
        // subtitle we downloaded, or a member we unpacked out of a downloaded pack — the whole season
        // is not packed. Not just that member: the operator's rule is "one srt of SubDL origin kills
        // the pack", because a container built around such text is a re-upload by construction.
        var fromSubdl = ordered
            .Where(c => _registry.IsSubdlOrigin(ContentHashRegistry.ComputeHash(c.Srt)))
            .Select(c => $"S{key.Season:00}E{c.Episode:00}")
            .ToList();
        if (fromSubdl.Count > 0)
        {
            LogUtil.Normal(
                _logger,
                "[SubDL] season pack refused — {Imdb} S{Season} [{Lang}]: {Count} member(s) came from SubDL itself ({Episodes}); a pack is never built around those.",
                key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi), fromSubdl.Count, string.Join(", ", fromSubdl.Take(5)));
            return;
        }

        // F-M391 (operator order 02.11.2026): the single uploads this pack absorbs are deleted FIRST.
        // A pack that competes with entries carrying the same content is the case we do not want to
        // find out about on SubDL's side; deleting first makes the pack the only carrier.
        var absorbed = ordered
            .Where(x => !string.IsNullOrWhiteSpace(x.AbsorbedSingleId))
            .Select(x => (Episode: x.Episode, Id: x.AbsorbedSingleId!))
            .ToList();
        // F-M412 (operator order 02.11.2026): "da muss stehen ob wir es hochgeladen haben und ob wir
        // es loeschen koennen". The state answers both: the row says whether we uploaded the single,
        // and its SubDL id says whether we can delete it. A member without that id is named here, so
        // a pack never leaves a duplicate behind silently.
        var undeletable = ordered
            .Where(x => string.IsNullOrWhiteSpace(x.AbsorbedSingleId))
            .Select(x => $"S{key.Season:00}E{x.Episode:00}")
            .ToList();
        if (undeletable.Count > 0)
        {
            LogUtil.Normal(
                _logger,
                "[SubDL] season pack {Imdb} S{Season} [{Lang}]: {Count} member(s) carry no SubDL id in the store ({Episodes}) — uploaded by us, but not deletable, they stay beside the pack.",
                key.ImdbId, key.Season, key.Lang, undeletable.Count, string.Join(", ", undeletable.Take(6)));
        }

        var deletedIds = new List<string>();
        foreach (var single in absorbed)
        {
            bool gone = await _api.DeletePublishedSubtitleAsync(single.Id, ct).ConfigureAwait(false);
            if (gone)
            {
                deletedIds.Add(single.Id);
                LogUtil.Normal(
                    _logger,
                    "[SubDL] season pack absorbs the single upload of {Imdb} S{Season}E{Episode} [{Lang}] — id {Id} deleted before the pack.",
                    key.ImdbId, key.Season, single.Episode, SeasonPack.LangTag(key.Lang, key.Hi), single.Id);
            }
            else
            {
                // Not fatal: the pack is still the better artefact, and the entry that refused to go
                // stays visible as a duplicate rather than silently blocking the season.
                LogUtil.Normal(
                    _logger,
                    "[SubDL] season pack could NOT delete the single upload of {Imdb} S{Season}E{Episode} [{Lang}] — id {Id} stays beside the pack.",
                    key.ImdbId, key.Season, single.Episode, SeasonPack.LangTag(key.Lang, key.Hi), single.Id);
            }
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
                    result.Reason ?? "skipped", key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi));
                if (uploadSinglesOnRefusal)
                {
                    await UploadSinglesAsync(ordered, ct).ConfigureAwait(false);
                }

                return;
            }

            _countFailed(ordered.Count);
            _logger.LogError("[SubDL] SEASON PACK FAILED {Imdb} S{Season} [{Lang}]: {Reason}", key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi), result.Reason);
            if (deletedIds.Count > 0)
            {
                // The singles are gone and the pack did not land. Say it out loud: the next fill-only
                // pass rebuilds the same pack from the library (the content is still known), and nobody
                // should have to guess why the episodes vanished from the panel.
                _logger.LogWarning(
                    "[SubDL] {Count} single upload(s) of {Imdb} S{Season} [{Lang}] were deleted for a pack that then failed — ids {Ids}. The next run rebuilds the pack from the library.",
                    deletedIds.Count, key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi), string.Join(",", deletedIds));
            }
            return;
        }

        // The Upload API answers a completed upload with {"status":true,"message":"subtitle sent for
        // review"} — no id. The pack's identity in our own rows is therefore the pack NAME when the
        // response carries no id: it is unique per season and content, and it is what the
        // postprocessing pass can match against /user/mySubtitles.
        // F-M414 (measured 02.11.2026, probe pack id 376481): the upload answer carries no id — it
        // says "subtitle sent for review" — but /user/mySubtitles lists the entry seconds later with
        // its UploadId. The pack asks for it the same way a single does, matched by the release name
        // it was uploaded under and the season it covers.
        string? packId = ReadPackId(result.Response);
        if (string.IsNullOrWhiteSpace(packId))
        {
            packId = await ResolveOwnPackIdAsync(releases: PackReleases(ordered, key), key, ct).ConfigureAwait(false)
                     ?? name;
            if (packId == name)
            {
                // F-M421: the pack row then carries the NAME instead of an id — say so, because a pack
                // without an id cannot be deleted or matched against the account's own list later.
                _logger.LogWarning(
                    "[SubDL] no pack id for {Imdb} S{Season} [{Lang}] — the pack row carries its name \"{Name}\" instead of an id.",
                    key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi), name);
            }
        }

        foreach (var candidate in ordered)
        {
            var c = candidate;
            _registry.MarkAndFlush(() => _registry.MarkEmbed(
                c.MediaHash, c.SubPos, c.Lang, c.HearingImpaired,
                SubtitleStatus.Uploaded, contentHash: ContentHashRegistry.ComputeHash(c.Srt),
                subdlId: packId, forced: c.Forced));
            _reportDone(c.ItemId);
        }

        // F-M390: the pack is now a fact in the store, not an inference from the subtitle rows. One row
        // per season + language + variant, updated in place — it is what stops the fill-only pass from
        // rebuilding the same pack on every run.
        _registry.MarkAndFlush(() => _registry.RecordPack(
            key.ImdbId,
            key.Season,
            key.Lang,
            key.Hi,
            first.ReleaseName,
            packId,
            ordered.Select(x => x.Episode),
            deletedIds.Count > 0 ? deletedIds : null));

        _countUploaded($"{key.ImdbId} S{key.Season} [{key.Lang}]", ordered.Count);
        LogUtil.Normal(
            _logger,
            "[SubDL] SEASON PACK {Name} — {Count} episode(s), {Imdb} S{Season} [{Lang}], Ef/Ee {From}-{To}, id {Id}",
            name, ordered.Count, key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi),
            ordered.Min(c => c.Episode), ordered.Max(c => c.Episode), packId ?? "-");
    }

    /// <summary>
    /// F-M414: the SubDL upload id of this pack, read back from <c>/user/mySubtitles</c>. The list
    /// names the entry after the release (pack: "…S01…", single: "…S01E08…") and states the language
    /// in words ("Turkish"), so the match runs over the release name first and falls back to IMDb
    /// id, season and language, newest first.
    /// </summary>
    private async Task<string?> ResolveOwnPackIdAsync(
        IReadOnlyList<string> releases, (string ImdbId, int Season, string Lang, bool Hi) key, CancellationToken ct)
    {
        List<OwnSubtitleEntry>? entries;
        try
        {
            entries = await _api.ListMySubtitlesAsync(ct, maxPages: 2).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SubDL] could not read mySubtitles for the pack id.");
            return null;
        }

        var byRelease = entries?.FirstOrDefault(e => releases.Any(r => MatchesRelease(e.Name, r)));
        byRelease ??= entries?
            .Where(e => string.Equals(e.ImdbId, key.ImdbId, StringComparison.OrdinalIgnoreCase)
                        && e.Season == key.Season && SameLanguage(e.Language, key.Lang))
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefault();

        if (byRelease != null)
        {
            LogUtil.Normal(
                _logger,
                "[SubDL] pack id {Id} taken from mySubtitles for {Imdb} S{Season} [{Lang}] — without it the pack could never be matched against the account's own list.",
                byRelease.HandleId, key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi));
            return byRelease.HandleId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // F-M432: same fallback as the single path — once SubDL has published everything, the submission
        // list is empty and the pack id has to come from the published list, matched by its release name.
        List<OwnSubtitleEntry>? published;
        try
        {
            published = await _api.ListMyPublishedSubtitlesAsync(ct, maxPages: 2).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SubDL] could not read myPublishedSubtitles for the pack id.");
            return null;
        }

        var publishedMatch = published?.FirstOrDefault(e => releases.Any(r => MatchesRelease(e.Name, r)));
        if (publishedMatch == null)
        {
            return null;
        }

        // F-M435: the published list carries the entry id next to the upload id — the entry id is the
        // one /user/deletePublishedSubtitle takes, so that is what the pack row is written with.
        LogUtil.Normal(
            _logger,
            "[SubDL] pack entry id {Id} taken from myPublishedSubtitles for {Imdb} S{Season} [{Lang}] (upload id {UploadId}) — the submission list knows nothing, the published list names it (F-M432/F-M435).",
            publishedMatch.HandleId, key.ImdbId, key.Season, SeasonPack.LangTag(key.Lang, key.Hi), publishedMatch.UploadId);
        return publishedMatch.HandleId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>F-M414: the entry's release name carries ours, extension and case aside.</summary>
    private static bool MatchesRelease(string? entryName, string? ours)
    {
        if (string.IsNullOrWhiteSpace(entryName) || string.IsNullOrWhiteSpace(ours))
        {
            return false;
        }

        string a = System.IO.Path.GetFileNameWithoutExtension(entryName.Trim());
        string b = System.IO.Path.GetFileNameWithoutExtension(ours.Trim());
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
               || a.Contains(b, StringComparison.OrdinalIgnoreCase)
               || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>F-M414: "Turkish" and "TR" are the same language; both spellings come over the wire.</summary>
    private static bool SameLanguage(string? entryLanguage, string? ours)
    {
        if (string.IsNullOrWhiteSpace(entryLanguage) || string.IsNullOrWhiteSpace(ours))
        {
            return true;
        }

        if (string.Equals(entryLanguage, ours, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? mapped = LanguageMapper.MapToSubdlName(entryLanguage);
        return string.Equals(mapped, ours, StringComparison.OrdinalIgnoreCase)
               || entryLanguage.StartsWith(ours, StringComparison.OrdinalIgnoreCase);
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
