using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// F-M389 (operator order 02.11.2026): supplies the missing episode of a season that is otherwise
/// ready for a pack.
/// <para>
/// A complete season used to dissolve into single uploads as soon as one of its episodes was not
/// part of the current run — the episode was up already, so there was nothing to send for it, and
/// the pack lost its eighth member. The filler closes that gap: for an episode nobody has queued,
/// it returns the candidate a pack may carry, built from the subtitle that is already on SubDL.
/// </para>
/// <para>
/// The contract is deliberately narrow and fail-closed. A filler returns a candidate ONLY when the
/// text it extracted passes the same gates the single upload path applies AND its content is
/// already known to the registry — that is the proof that it was uploaded before. Anything else is
/// <c>null</c>: an invented episode, a QA-failed subtitle or a never-uploaded track must never
/// enter a pack, because the pack claims to be the complete season.
/// </para>
/// </summary>
public interface IPackFiller
{
    /// <summary>
    /// Builds the pack candidate for one aired episode of a season, from the subtitle the library
    /// already holds for it.
    /// </summary>
    /// <param name="imdbId">Series IMDb id — the season is identified by this and the season number.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number that no candidate of the buffer covers.</param>
    /// <param name="lang">SubDL language code the pack is built for.</param>
    /// <param name="hearingImpaired">HI/SDH variant the pack is built for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The candidate, or null when this episode cannot join the pack.</returns>
    Task<SeasonPackCoordinator.Candidate?> FillAsync(
        string imdbId, int season, int episode, string lang, bool hearingImpaired, CancellationToken ct);

    /// <summary>
    /// F-M442 (operator 04.11.2026): the source facts of an aired episode — quality and frame rate,
    /// read WITHOUT extracting anything. The coordinator asks them before the first fill, so a season
    /// of mixed sources is refused without pulling a single text out of a container.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <returns>Quality and frame rate, null where the library cannot answer.</returns>
    (string? Quality, string? Framerate) SourceFacts(string imdbId, int season, int episode);

    /// <summary>
    /// F-M433 (operator order 03.11.2026, "wieso geht Tracker einzeln hoch?"): says whether the episode's
    /// FILE carries an embedded track of this language and variant.
    /// <para>
    /// The store only knows what was already uploaded or observed, so a season whose texts are going up
    /// for the FIRST time is unknown to it — the completeness gate read that as "the language is missing"
    /// and refused the pack, while the single phase uploaded every episode straight from its file. The
    /// file is the better witness and looking at the stream list costs no extraction.
    /// </para>
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="lang">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <returns>True when at least one embedded track of that language and variant exists.</returns>
    bool FileCarriesLanguage(string imdbId, int season, int episode, string lang, bool hearingImpaired);

    /// <summary>
    /// F-M433: the language pairs the files of a season carry. The pack plan starts from the store's
    /// languages; this adds the ones only the files know, so a first-time season can still be a pack.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <returns>The pairs found in the files of that season.</returns>
    IReadOnlyCollection<(string Language, bool HearingImpaired)> FileLanguages(string imdbId, int season);
}
