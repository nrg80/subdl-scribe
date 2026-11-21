// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Configuration;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M483 (operator order 15.11.2026: "im Prinzip möchte ich jetzt 2 download endpoints und 2 upload
/// endpoints einführen und den rest des plugins gleich lassen"): the direction an endpoint serves.
/// </summary>
public enum SubtitleDirection
{
    /// <summary>Fetching a subtitle from the source.</summary>
    Download,

    /// <summary>Sending a subtitle to the source.</summary>
    Upload,
}

/// <summary>
/// F-M483: what to look for — provider-neutral, so a caller does not have to know which source answers.
/// Not every source can use every field: SubDL works on IMDb/TMDb ids, tvsubtitles.net only on names.
/// </summary>
/// <param name="ShowName">Series title, needed by sources that have no id index; for a film the film title.</param>
/// <param name="ImdbId">IMDb id, when known.</param>
/// <param name="TmdbId">TMDb id, when known.</param>
/// <param name="Season">Season number.</param>
/// <param name="Episode">Episode number.</param>
/// <param name="Language">Target language, site-independent (two-letter, upper or lower case).</param>
/// <param name="Year">
/// F-M507: das Jahr, das eine Suche ohne Id-Index braucht (TMDb-Jahr, nicht das des Dateinamens). 0 heißt
/// „nicht bekannt" — dann darf eine Quelle, die ohne Jahr nicht sicher trifft, nichts liefern.
/// </param>
/// <param name="MovieId">
/// F-M507: die Nummer, unter der die Quelle den FILM führt, wenn der Store sie schon kennt
/// (<see cref="Jellyfin.Plugin.SubScribe.Data.MediaEntity.MsubMovieId"/>). Ist sie gesetzt, wird sie
/// benutzt statt erneut zu suchen — dieselbe Regel wie <c>UploadTicket.TvstShowId</c> für Serien.
/// </param>
public sealed record SubtitleQuery(
    string? ShowName,
    string? ImdbId,
    string? TmdbId,
    int Season,
    int Episode,
    string Language,
    int Year = 0,
    string? MovieId = null);

/// <summary>
/// F-M483: one offer a source has for a query. <paramref name="Id"/> is the source's own handle — a
/// URL for SubDL, a subtitle id for tvsubtitles.net — and is only meaningful to the endpoint that
/// produced it.
/// </summary>
/// <param name="Provider">The source this hit came from.</param>
/// <param name="Id">The source's handle, passed back to <see cref="IDownloadEndpoint.FetchAsync"/>.</param>
/// <param name="Language">Language as the source spells it.</param>
/// <param name="Rip">Rip/quality cell, empty when the source has none.</param>
/// <param name="Release">Release name, empty when the source has none.</param>
/// <param name="FullSeason">True when the offer covers a whole season.</param>
/// <param name="HearingImpaired">
/// F-M663: der Treffer ist die HI-Fassung (Ohr-Bild auf der Folgenseite, <c>.HI.</c> im Namen eines
/// Staffel-ZIP-Eintrags). Der Lauf legt ihn damit auf die HI-Seite und nie auf die regulaere — und
/// umgekehrt. Vorgabe false, damit jeder vorhandene Aufrufer unveraendert die regulaere Seite meint.
/// </param>
public sealed record SubtitleHit(
    SubtitleProvider Provider,
    string Id,
    string Language,
    string Rip,
    string Release,
    bool FullSeason,
    bool HearingImpaired = false);

/// <summary>
/// F-M483: the file an endpoint delivers — name and bytes, nothing decoded. The transport never
/// touches the encoding (F-M482): the bytes are whatever the source served.
/// </summary>
/// <param name="FileName">The member name the source (or its archive) carries.</param>
/// <param name="Data">The subtitle bytes.</param>
/// <param name="Language">Language as the source spells it.</param>
public sealed record SubtitlePayload(string FileName, byte[] Data, string Language);

/// <summary>
/// F-M483: what to send. One ticket serves both sources; a field a source cannot use is refused by
/// that source's endpoint with a reason instead of being silently dropped.
/// </summary>
/// <param name="ShowName">Series title — tvsubtitles.net resolves its show id from it.</param>
/// <param name="ImdbId">IMDb id — the comment field of tvsubtitles.net carries it, SubDL requires it.</param>
/// <param name="TmdbId">TMDb id, same role.</param>
/// <param name="Season">Season number.</param>
/// <param name="Episode">Episode number.</param>
/// <param name="Language">Target language (two-letter).</param>
/// <param name="FileName">File name to store the subtitle under.</param>
/// <param name="Data">The subtitle bytes.</param>
/// <param name="Rip">Rip/quality, when the source has such a field.</param>
/// <param name="Release">Release name.</param>
/// <param name="HearingImpaired">True for the SDH variant.</param>
/// <param name="DoubleEpisode">True for a double episode.</param>
/// <param name="SeasonPack">True when this is a season pack — only the pipeline builds those.</param>
/// <param name="TvstShowId">
/// F-M501: die Nummer der Serie bei tvsubtitles.net, wenn der Store sie kennt. Ist sie gesetzt, benutzt
/// der Endpunkt SIE und holt die Serienliste gar nicht erst — der Titel im Ticket ist dann nur noch
/// Text für Kommentar und Log.
/// </param>
/// <param name="MsubMovieId">
/// F-M508: die Nummer des FILMS bei moviesubtitles.org, wenn der Store sie kennt
/// (<see cref="Jellyfin.Plugin.SubScribe.Data.MediaEntity.MsubMovieId"/>) — dieselbe Regel wie
/// <paramref name="TvstShowId"/>, eine Ebene tiefer. Fehlt sie, löst der Endpunkt über Titel und Jahr
/// auf (<paramref name="MovieYear"/>).
/// </param>
/// <param name="MovieYear">
/// F-M508: das Jahr des Films aus dem TMDb-Datensatz. Ohne Jahr ist der Namensvergleich der Seite nicht
/// eng genug („filter=dune" antwortet fünf Filme) — dann wird nichts geraten.
/// </param>
public sealed record UploadTicket(
    string? ShowName,
    string? ImdbId,
    string? TmdbId,
    int Season,
    int Episode,
    string Language,
    string FileName,
    byte[] Data,
    string? Rip,
    string? Release,
    bool HearingImpaired,
    bool DoubleEpisode,
    bool SeasonPack,
    int? TvstShowId = null,
    int? MsubMovieId = null,
    int MovieYear = 0,
    string? EpisodeTitle = null);

/// <summary>
/// F-M483: what an upload endpoint answers. <paramref name="Ok"/> false plus a
/// <paramref name="Reason"/> is a refusal the caller can log; it is never an exception for a case the
/// caller could have known about.
/// </summary>
/// <param name="Provider">The source that answered.</param>
/// <param name="Ok">True when the source took the subtitle (or, in a dry run, would have).</param>
/// <param name="Reason">The refusal or skip reason, null on success.</param>
/// <param name="Bytes">Size of the body that was (or would be) sent.</param>
/// <param name="Answer">The source's own answer text, when it gave one.</param>
/// <param name="DryRun">True when nothing left the process.</param>
public sealed record EndpointAnswer(
    SubtitleProvider Provider,
    bool Ok,
    string? Reason,
    int Bytes,
    string? Answer,
    bool DryRun,
    /// <summary>
    /// F-M488: the source's own id for what this answer is about — the new upload's subtitle id at
    /// tvsubtitles.net. Null when the source does not hand one back or it could not be read back; a
    /// guessed id is worse than none, because the store would then vouch for it.
    /// </summary>
    string? Id = null,
    /// <summary>
    /// F-M609: der HTTP-Status der Antwort, wenn es eine gab — <c>null</c>, wenn gar nicht gesendet
    /// wurde (eigene Vorpruefung, Trockenlauf). Die Kette braucht ihn, um „die Seite hat abgelehnt"
    /// (4xx) von „keine Antwort" (429, 5xx) zu trennen: nur das erste schliesst das Paar.
    /// </summary>
    int? Status = null);

/// <summary>
/// F-M483: ONE download endpoint. Two implementations exist — SubDL and tvsubtitles.net — and the
/// pipelines talk to this shape instead of to a concrete client.
/// </summary>
public interface IDownloadEndpoint
{
    /// <summary>Gets the source this endpoint serves.</summary>
    SubtitleProvider Provider { get; }

    /// <summary>Gets a value indicating whether the endpoint is wired into the pipeline's run loop.
    /// An endpoint can exist and still not be wired (F-M484) — the switches say so.</summary>
    bool Wired { get; }

    /// <summary>Looks for offers for this query.</summary>
    /// <param name="query">What to look for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The offers in the source's order; empty when it has none.</returns>
    Task<IReadOnlyList<SubtitleHit>> SearchAsync(SubtitleQuery query, CancellationToken cancellationToken = default);

    /// <summary>Fetches the file behind a hit.</summary>
    /// <param name="hit">The hit this endpoint produced.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The payload, or null when the source delivered nothing usable.</returns>
    Task<SubtitlePayload?> FetchAsync(SubtitleHit hit, CancellationToken cancellationToken = default);
}

/// <summary>
/// F-M483: ONE upload endpoint. Two implementations exist — SubDL and tvsubtitles.net.
/// </summary>
public interface IUploadEndpoint
{
    /// <summary>Gets the source this endpoint serves.</summary>
    SubtitleProvider Provider { get; }

    /// <summary>Gets a value indicating whether the endpoint is wired into the pipeline's run loop.</summary>
    bool Wired { get; }

    /// <summary>Sends one subtitle.</summary>
    /// <param name="ticket">What to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer; a refusal is Ok=false with a reason, not an exception.</returns>
    Task<EndpointAnswer> SendAsync(UploadTicket ticket, CancellationToken cancellationToken = default);
}

/// <summary>
/// F-M483/F-M484: the list of endpoints the run may use, decided by the four switches (F-M477) and by
/// what is actually wired. Deliberately a plain object, not a container registration: the pipelines
/// stay as they are, they only ask which endpoints are available.
/// </summary>
public sealed class SubtitleEndpointCatalog
{
    private readonly IReadOnlyList<IDownloadEndpoint> _downloads;
    private readonly IReadOnlyList<IUploadEndpoint> _uploads;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleEndpointCatalog"/> class.
    /// </summary>
    /// <param name="downloads">All implemented download endpoints.</param>
    /// <param name="uploads">All implemented upload endpoints.</param>
    public SubtitleEndpointCatalog(
        IReadOnlyList<IDownloadEndpoint> downloads, IReadOnlyList<IUploadEndpoint> uploads)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
    }

    /// <summary>Gets every implemented download endpoint, switched on or not.</summary>
    public IReadOnlyList<IDownloadEndpoint> AllDownloads => _downloads;

    /// <summary>Gets every implemented upload endpoint, switched on or not.</summary>
    public IReadOnlyList<IUploadEndpoint> AllUploads => _uploads;

    /// <summary>
    /// F-M484: the download endpoints that may run now — the switch is on AND the endpoint is wired.
    /// </summary>
    /// <param name="config">The live configuration.</param>
    /// <returns>The usable endpoints.</returns>
    public IReadOnlyList<IDownloadEndpoint> EnabledDownloads(PluginConfiguration config) =>
        _downloads.Where(e => e.Wired && config.IsDownloadEnabled(e.Provider)).ToList();

    /// <summary>
    /// F-M484: the upload endpoints that may run now.
    /// </summary>
    /// <param name="config">The live configuration.</param>
    /// <returns>The usable endpoints.</returns>
    public IReadOnlyList<IUploadEndpoint> EnabledUploads(PluginConfiguration config) =>
        _uploads.Where(e => e.Wired && config.IsUploadEnabled(e.Provider)).ToList();

    /// <summary>
    /// F-M484: the endpoints that are implemented but refused, each with the sentence the page shows.
    /// This is the same "no silent switch" rule as F-M366/F-M478 in one place, so the GUI, the worker
    /// note and a log line cannot tell three different stories.
    /// </summary>
    /// <param name="config">The live configuration.</param>
    /// <param name="direction">Which direction to explain.</param>
    /// <returns>Provider and reason, for every endpoint that does not run.</returns>
    public IReadOnlyList<(SubtitleProvider Provider, string Reason)> Refused(
        PluginConfiguration config, SubtitleDirection direction)
    {
        ArgumentNullException.ThrowIfNull(config);
        var result = new List<(SubtitleProvider, string)>();
        if (direction == SubtitleDirection.Download)
        {
            foreach (var endpoint in _downloads)
            {
                if (!endpoint.Wired)
                {
                    result.Add((endpoint.Provider, NotWiredReason(endpoint.Provider, SubtitleDirection.Download)));
                }
                else if (!config.IsDownloadEnabled(endpoint.Provider))
                {
                    result.Add((endpoint.Provider, "switched off"));
                }
            }

            return result;
        }

        foreach (var endpoint in _uploads)
        {
            if (!endpoint.Wired)
            {
                result.Add((endpoint.Provider, NotWiredReason(endpoint.Provider, SubtitleDirection.Upload)));
            }
            else if (!config.IsUploadEnabled(endpoint.Provider))
            {
                result.Add((endpoint.Provider, "switched off"));
            }
        }

        return result;
    }

    /// <summary>
    /// F-M484: why an implemented endpoint is not in the run — the wording the page and the worker note share.
    /// </summary>
    /// <param name="provider">The source.</param>
    /// <param name="direction">The direction asked about.</param>
    /// <returns>The sentence.</returns>
    public static string NotWiredReason(SubtitleProvider provider, SubtitleDirection direction) =>
        PluginConfiguration.MissingProviderReason(provider) ?? "switched off";

    /// <summary>
    /// F-M507 (Operator-Auflage 15.11.2026: „Bevor wir falsche Sprachen hochladen, laden wir nix hoch."):
    /// gibt es für diese Quelle in DIESEM Build einen Upload-Weg?
    /// <para>
    /// Die Frage ist nicht „ist der Schalter an" und nicht „ist sie konfiguriert", sondern „existiert der
    /// Endpunkt überhaupt". Sie steht hier, weil diese Klasse die Liste der Endpunkte führt: wer eine
    /// Quelle umsonst in eine Kettenreihenfolge stellt, bekommt später einen Upload-Versuch auf einen Weg,
    /// den es nicht gibt.
    /// </para>
    /// <para>
    /// F-M508 (Etappe 2b): moviesubtitles.org hat seinen Upload-Endpunkt bekommen, also ist die Antwort
    /// für ihn jetzt <c>true</c> — der Weg ist verdrahtet. SCHARF IST ER DAMIT NICHT: der Schalter ist
    /// standardmäßig aus (F-M507: null → aus), und der Upload bleibt gesperrt, bis die Sprachabbildung
    /// der Seite gegen die echte Seite belegt ist. Diese Methode sagt „der Weg existiert", nicht „er
    /// läuft" — das entscheidet der Schalter.
    /// </para>
    /// </summary>
    /// <param name="provider">The source asked about.</param>
    /// <returns>True when this build has an upload endpoint for it.</returns>
    public static bool HasUploadPath(SubtitleProvider provider) =>
        provider is SubtitleProvider.Subdl or SubtitleProvider.TvSubtitles or SubtitleProvider.MovieSubtitles;
}
