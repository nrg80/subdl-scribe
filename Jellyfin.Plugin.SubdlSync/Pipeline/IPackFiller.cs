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
}
