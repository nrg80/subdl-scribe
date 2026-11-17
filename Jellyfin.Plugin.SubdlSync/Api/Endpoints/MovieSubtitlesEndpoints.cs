// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M507 (Etappe 2a, Nutzerauftrag 15.11.2026: „Nur das series halt nach tv show und movies nach
/// moviesubtitles gehen."): der DOWNLOAD-Endpunkt von moviesubtitles.org — der dritten Quelle, und der
/// einen, die nur Filme bedient.
/// <para>
/// Die Arbeitsteilung ist die des Hauses: die AUFLÖSUNG („welche Nummer hat dieser Film auf der Seite")
/// gehört in den Store und steht in <see cref="MsubMovieResolver"/> — sie schreibt
/// <c>MediaEntity.MsubMovieId</c> und liest sie beim nächsten Mal. Dieser Endpunkt ist der TRANSPORT:
/// er nimmt die Nummer aus <see cref="SubtitleQuery.MovieId"/> (oder löst selbst, wenn ein Aufrufer ohne
/// Store fragt), holt die EINZELNE Filmseite und gibt die Einträge der gesuchten Sprache zurück.
/// </para>
/// <para>
/// KEINE STAFFEL, KEINE FOLGE: ein Film hat beides nicht. Steht in der Anfrage trotzdem eine Staffel
/// oder eine Folge, ist das eine Serie — dann hat diese Quelle nichts zu suchen und antwortet leer,
/// statt die Episode als Film zu behandeln.
/// </para>
/// <para>
/// KEIN SCHREIBENDER ZUGRIFF: gelesen werden <c>search.php</c>, <c>movie-&lt;id&gt;.html</c>,
/// <c>subtitle-&lt;id&gt;.html</c> und <c>files/…</c>. Das Upload-Formular <c>add.php</c> wird nicht
/// betreten — auch nicht lesend mit einem Filter.
/// </para>
/// </summary>
public sealed class MovieSubtitlesDownloadEndpoint : IDownloadEndpoint
{
    /// <summary>
    /// Wie lange eine geholte Filmseite gilt. Ein Filmlauf fragt die Seite je fehlender Sprache; die
    /// Seite selbst ändert sich in Minuten nicht, und die Quelle ist eine fremde, nebenamtlich
    /// betriebene Seite — sie wird nicht sechsmal für dieselbe Antwort gefragt.
    /// </summary>
    private static readonly TimeSpan PageLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Ceiling of the page cache — a long run must not grow it without bound.</summary>
    private const int MaxCachedPages = 64;

    private readonly MovieSubtitlesClient _client;
    private readonly ILogger? _logger;
    private readonly Dictionary<int, (MovieSubtitlesMoviePage Page, DateTime FetchedAt)> _pages = new();
    private readonly object _pagesLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieSubtitlesDownloadEndpoint"/> class.
    /// </summary>
    /// <param name="client">Der moviesubtitles.org-Client.</param>
    /// <param name="logger">Optionaler Logger — er nennt den Grund, wenn ein Film nicht aufgelöst wird.</param>
    public MovieSubtitlesDownloadEndpoint(MovieSubtitlesClient client, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    /// <inheritdoc />
    public SubtitleProvider Provider => SubtitleProvider.MovieSubtitles;

    /// <inheritdoc />
    /// <remarks>
    /// F-M507/F-M508: der Download dieser Quelle IST verdrahtet, und seit Etappe 2b existiert auch ihr
    /// Upload-Endpunkt (<see cref="MovieSubtitlesUploadEndpoint"/>, standardmäßig gesperrt) —
    /// <see cref="SubtitleEndpointCatalog.HasUploadPath"/> sagt beides.
    /// </remarks>
    public bool Wired => true;

    /// <summary>
    /// F-M507: das Sprachkürzel der Seite für einen Sprachcode, oder null. Nur durchgereicht an
    /// <see cref="MovieSubtitlesClient.SiteLanguage"/> — die Tabelle dieser Seite steht dort und nirgends
    /// sonst (F-M502-Regel: keine gemeinsame Sprachabbildung zwischen den Quellen).
    /// </summary>
    /// <param name="language">Der Sprachcode, in beliebiger Schreibweise.</param>
    /// <returns>Das Kürzel der Seite, oder null wenn sie die Sprache nicht führt.</returns>
    public static string? SiteLanguage(string? language) => MovieSubtitlesClient.SiteLanguage(language);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubtitleHit>> SearchAsync(
        SubtitleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Ein Film hat keine Staffel und keine Folge. Steht eine da, ist es eine Serie — dann ist diese
        // Quelle nicht zuständig (und sagt es durch eine leere Antwort, nicht durch einen Wurf).
        if (query.Season > 0 || query.Episode > 0)
        {
            return Array.Empty<SubtitleHit>();
        }

        // Führt die Seite die Sprache nicht (HI, ZH, NL, SV …), hat sie nichts anzubieten. Das ist ein
        // Ergebnis, kein Fehler: die Sprache ist nicht kaputt, diese Quelle hat sie nicht.
        var code = MovieSubtitlesClient.SiteLanguage(query.Language);
        if (code is null)
        {
            Log("[moviesubtitles] no language '{Lang}' on this source — nothing asked", query.Language);
            return Array.Empty<SubtitleHit>();
        }

        int? movieId = int.TryParse(query.MovieId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var known)
            && known > 0
            ? known
            : await ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        if (movieId is null)
        {
            return Array.Empty<SubtitleHit>();
        }

        var page = await PageAsync(movieId.Value, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return Array.Empty<SubtitleHit>();
        }

        // Die Sprache eines Eintrags kommt aus SEINER Flagge (F-M506), nicht aus der Abschnittsüberschrift
        // — ein Eintrag ohne Flagge steht gar nicht erst in der Liste.
        return page.Entries
            .Where(e => string.Equals(e.Language, code, StringComparison.Ordinal))
            .Select(e => new SubtitleHit(
                SubtitleProvider.MovieSubtitles,
                e.SubtitleId,
                e.Language,
                e.Rip,

                // Der Release-Name ist die Grundlage des Textvergleichs (Score). Nennt der Eintrag keinen,
                // trägt der Name den Rip und die Quelle — ein leerer Name würde jeden Vergleich gegen
                // jeden anderen Eintrag gleich aussehen lassen.
                e.Release.Length > 0 ? e.Release : "moviesubtitles " + e.Rip,
                FullSeason: false))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<SubtitlePayload?> FetchAsync(
        SubtitleHit hit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hit);

        if (!int.TryParse(hit.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var subtitleId)
            || subtitleId <= 0)
        {
            return null;
        }

        var download = await _client
            .DownloadAsync(subtitleId, hit.Language, cancellationToken)
            .ConfigureAwait(false);

        if (download.Data.Length == 0)
        {
            return null;
        }

        if (download.IsMultiPart)
        {
            // `parts: 3` ist ein GESCHNITTENER Film, kein Staffelpack: die Teile sind nicht austauschbar.
            // Der Client nimmt den Teil mit dem Sprachkürzel (sonst den ersten) und kennzeichnet das —
            // hier steht nur, dass es passiert ist.
            Log(
                "[moviesubtitles] entry {Id} is a cut film ({N} parts) — took {Member}",
                subtitleId,
                download.Members.Count,
                download.Name);
        }

        return new SubtitlePayload(download.Name, download.Data, hit.Language);
    }

    /// <summary>
    /// F-M507: die Filmnummer, wenn der Aufrufer sie nicht mitgebracht hat — über Titel und Jahr
    /// (<see cref="MovieSubtitlesClient.FindMovieAsync"/>), also über die Schreibweisen, die die Seite
    /// versteht. Ohne Titel oder ohne Jahr gibt es hier keinen Treffer und keine Vermutung.
    /// </summary>
    /// <param name="query">Die Anfrage.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die Nummer, oder null.</returns>
    private async Task<int?> ResolveAsync(SubtitleQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.ShowName) || query.Year <= 0)
        {
            // Ohne Jahr ist der Namensvergleich der Seite nicht eng genug: `filter=dune` antwortet fünf
            // Filme. Der Aufrufer (der Auflöser) protokolliert das; hier wird nichts geraten.
            Log("[moviesubtitles] no title or no year for '{Title}' — nothing resolved", query.ShowName ?? "?");
            return null;
        }

        return await _client.FindMovieAsync(query.ShowName, query.Year, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// F-M507: die Filmseite — aus dem Cache, solange sie frisch ist, sonst frisch geholt.
    /// </summary>
    /// <param name="movieId">Die Filmnummer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die Seite, oder null wenn sie nicht lesbar war.</returns>
    private async Task<MovieSubtitlesMoviePage?> PageAsync(int movieId, CancellationToken ct)
    {
        lock (_pagesLock)
        {
            if (_pages.TryGetValue(movieId, out var cached)
                && DateTime.UtcNow - cached.FetchedAt < PageLifetime)
            {
                return cached.Page;
            }
        }

        MovieSubtitlesMoviePage page;
        try
        {
            page = await _client.MovieAsync(movieId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Eine fremde Seite, die nicht antwortet, ist kein Fehler des Laufs: diese Quelle hat für
            // dieses Medium nichts, die anderen laufen weiter.
            _logger?.LogWarning("[moviesubtitles] film page {Id} not readable: {Error}", movieId, ex.Message);
            return null;
        }

        lock (_pagesLock)
        {
            if (_pages.Count >= MaxCachedPages)
            {
                _pages.Clear();
            }

            _pages[movieId] = (page, DateTime.UtcNow);
        }

        return page;
    }

    private void Log(string template, params object?[] args)
    {
        if (_logger is null)
        {
            return;
        }

        _logger.LogInformation(template, args);
    }
}

/// <summary>
/// F-M508 (Etappe 2b, Nutzerauftrag 15.11.2026: „Nur das series halt nach tv show und movies nach
/// moviesubtitels gehen. Als upload und download"): der UPLOAD-Endpunkt von moviesubtitles.org — die
/// Film-Adresse der tvst-Familie.
/// <para>
/// EIN FILM, KEINE STAFFEL: das Ticket trägt Titel, Jahr und (wenn der Store ihn kennt) die Filmnummer;
/// Staffel und Folge sind hier keine Angaben, sondern ein Widerspruch — steht eine da, ist das Medium
/// eine Serie, und diese Adresse hat dann nichts zu senden.
/// </para>
/// <para>
/// DIE NUTZLAST kommt aus <see cref="MovieSubtitlesClient.BuildUpload"/>: <c>tvid lang rip release</c>,
/// dann <c>author</c> NUR wenn gesetzt (ein fehlender Teil überschreibt keine Vorbelegung der Seite),
/// dann <c>UFile</c> (und <c>UFile2</c>/<c>UFile3</c>, wenn es mehrere Teile gibt), zuletzt
/// <c>comments</c> und <c>go</c>. <c>release</c> ist die RELEASE-GRUPPE aus dem Release-Namen, nie der
/// Dateiname (F-M505) — die Feldreihenfolge und die Ablehnungen stehen im Client, nicht hier.
/// </para>
/// <para>
/// F-M511: <c>rip</c> wird auf dem Weg ins Formular über <see cref="MovieSubtitlesClient.RipFromQuality"/>
/// gezogen und NIE roh durchgereicht — das Ticket trägt die Qualität des Release-Namens ("web",
/// "1080p BluRay x264"), das Formular nur seine neun Werte. Die Abbildung sitzt hier, weil dieser
/// Endpunkt die eine Naht jedes Film-Uploads ist und die Nutzlast vor der Trockenlauf-Weiche gebaut
/// wird: was der Trockenlauf zeigt, ist der Rumpf, der im Ernstfall geht.
/// </para>
/// <para>
/// WAS DIE SEITE BEI ANNAHME SAGT, IST NICHT BELEGT. Der Endpunkt erfindet deshalb weder eine Id noch
/// eine Moderation: er reicht den Text der Antwort durch, meldet Erfolg nur bei HTTP 2xx und gibt
/// <see cref="EndpointAnswer.Id"/> als <c>null</c> zurück. Die Kennung vergibt die Kette
/// (<c>own:moviesubtitles:&lt;Zeit&gt;:&lt;hex&gt;</c>, F-M488) — „hochgeladen, Id unbekannt" ist eine
/// belegbare Aussage, „Id 12345" wäre eine Behauptung.
/// </para>
/// <para>
/// F-M513: DIESER ENDPUNKT LEGT NICHTS AN — er ist Transport. Fehlt die Filmnummer, sucht er sie
/// (Titel und Jahr) und verweigert, wenn er sie nicht findet. Das Anlegen eines fehlenden Films steht
/// im Upload-Pfad der Kette, in <see cref="MsubMovieResolver.ResolveOrCreateForMediaAsync"/>: dort
/// stehen der Store, die eine Suchrunde (Auflösung <b>und</b> Doppeleintrag-Schutz) und die Spalte
/// <c>MsubMovieId</c>. Ein zweiter Anlage-Ort hier wäre ein zweiter Weg zu einer Nummer, die niemand
/// geprüft hat.
/// </para>
/// <para>
/// GESPERRT: dieser Endpunkt wird über den Schalter der Quelle erreicht, und der ist standardmäßig aus.
/// In dieser Etappe wird er ausschließlich gegen Fixtures geprüft — die Nutzlast wird gebaut, gesendet
/// wird nichts (der Trockenlauf ist der Vorgabewert von <c>dryRun</c>).
/// </para>
/// </summary>
public sealed class MovieSubtitlesUploadEndpoint : IUploadEndpoint
{
    private readonly MovieSubtitlesClient _client;
    private readonly bool _dryRun;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieSubtitlesUploadEndpoint"/> class.
    /// </summary>
    /// <param name="client">Der moviesubtitles.org-Client.</param>
    /// <param name="dryRun">True (Vorgabe) baut den Rumpf und sendet nichts. Der Schalter des Plugins wird
    /// hereingereicht und nicht hier gelesen, damit der Endpunkt prüfbar bleibt.</param>
    /// <param name="logger">Optionaler Logger — er nennt den Grund, wenn nicht gesendet wurde.</param>
    public MovieSubtitlesUploadEndpoint(MovieSubtitlesClient client, bool dryRun = true, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _dryRun = dryRun;
        _logger = logger;
    }

    /// <inheritdoc />
    public SubtitleProvider Provider => SubtitleProvider.MovieSubtitles;

    /// <inheritdoc />
    public bool Wired => true;

    /// <inheritdoc />
    public async Task<EndpointAnswer> SendAsync(
        UploadTicket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        // Ein Film hat weder Staffel noch Folge. Steht eines davon im Ticket, ist das Medium eine Serie —
        // dann ist die Film-Adresse der Familie nicht zuständig, und sie sagt es, statt die Folge als Film
        // unter einer falschen Nummer einzureichen.
        if (ticket.Season > 0 || ticket.Episode > 0)
        {
            return Refuse("a film has no season and no episode — this is a series, not this source's medium");
        }

        if (ticket.SeasonPack)
        {
            return Refuse("this source has no season packs — a film is one file");
        }

        var language = MovieSubtitlesDownloadEndpoint.SiteLanguage(ticket.Language);
        if (language is null)
        {
            return Refuse("moviesubtitles.org has no language '" + ticket.Language + "'");
        }

        // F-M508: die Nummer aus dem Store führt. Steht sie im Ticket, wird die Suche der Seite weder
        // gefragt noch ausgewertet — die Auflösung (<see cref="MsubMovieResolver"/>) hat sie bereits
        // geprüft und geschrieben. Nur ohne Nummer (direkter Aufruf, ältere Zeile) wird über Titel und
        // Jahr gesucht, und nur wenn BEIDES da ist: ohne Jahr antwortet die Suche der Seite fünf Filme.
        int? movieId = ticket.MsubMovieId is > 0 ? ticket.MsubMovieId : null;
        if (movieId is null)
        {
            var title = (ticket.ShowName ?? string.Empty).Trim();
            if (title.Length == 0 || ticket.MovieYear <= 0)
            {
                return Refuse("a film title AND its year are needed — this source has no id index");
            }

            movieId = await _client.FindMovieAsync(title, ticket.MovieYear, cancellationToken)
                .ConfigureAwait(false);
            if (movieId is not > 0)
            {
                // F-M513: hier wird NICHT angelegt — das ist Sache des Auflösers im Upload-Pfad
                // (MsubMovieResolver.ResolveOrCreateForMediaAsync), der die Doppeleintrag-Prüfung
                // mitbringt. Der Grund nennt deshalb auch nur, was hier geprüft wurde.
                return Refuse("film not resolvable on moviesubtitles.org by title and year: "
                    + title + " (" + ticket.MovieYear + ")");
            }
        }

        MovieSubtitlesUpload upload;
        try
        {
            var tmdb = long.TryParse(ticket.TmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
                ? t
                : (long?)null;

            // ticket.Release ist hier der RELEASE-NAME der Mediendatei (nicht die Gruppe): BuildUpload
            // schneidet die Gruppe heraus (F-M505). author bleibt null — das Plugin führt keinen
            // Autor-Namen, und ein leerer Teil ist besser als ein geratener.
            //
            // F-M511: die QUALITAET geht NICHT roh in das Formular. ticket.Rip ist die Angabe aus dem
            // Release-Namen ("web", "1080p BluRay x264", "x265"), das Auswahlfeld der Seite kennt neun
            // Werte und sonst nichts — BuildUpload weist alles andere ab. Gemessen 15.11.2026 auf
            // jf-test: die Kette reichte "web" durch, BuildUpload antwortete "rip must be empty or one
            // of: DVDRip, BluRay, BrRip, BDRip, HDRip, WEB, WEBRip, WEB-DL", und der Endpunkt machte
            // daraus Ok=false. Nur die leere Angabe kam durch — also scheiterte JEDER Film-Upload.
            //
            // Die Abbildung steht HIER und nicht im Ticket an den Aufrufstellen: der Endpunkt ist die
            // eine Naht, durch die jeder Film-Upload geht (Kette wie direkter Aufruf), und die Nutzlast
            // wird EINMAL VOR der Trockenlauf-Weiche gebaut — Trockenlauf und Ernst zeigen deshalb
            // denselben Rumpf, koennen also nicht auseinanderlaufen. Der Serien-Pfad macht dasselbe mit
            // TvSubtitlesClient.RipFromQuality; die TABELLEN bleiben getrennt (F-M506): dort wird
            // "WEB-DL" zu "WEB", hier bleibt es "WEB-DL".
            upload = MovieSubtitlesClient.BuildUpload(
                movieId.Value,
                language,
                ticket.Release,
                ticket.FileName,
                ticket.Data,
                MovieSubtitlesClient.RipFromQuality(ticket.Rip),
                author: null,
                tmdb,
                ticket.ImdbId,
                commentExtra: null,
                movieTitle: ticket.ShowName,
                year: ticket.MovieYear);
        }
        catch (ArgumentException ex)
        {
            return new EndpointAnswer(Provider, false, ex.Message, ticket.Data.Length, null, _dryRun);
        }

        var result = await _client.UploadAsync(upload, _dryRun, cancellationToken).ConfigureAwait(false);
        var ok = !_dryRun && result.Status is >= 200 and < 300;

        if (_dryRun)
        {
            return new EndpointAnswer(Provider, true, null, result.BodyBytes, null, DryRun: true);
        }

        if (!ok)
        {
            // Keine Deutung der Antwort: der Status ist die einzige Tatsache, die die Seite sicher
            // liefert, und ihr Text geht unverändert mit. Eine Ablehnung, die wir erfinden, wäre so
            // falsch wie eine Annahme, die wir erfinden.
            return new EndpointAnswer(
                Provider, false, "HTTP " + result.Status, result.BodyBytes, result.Response, false);
        }

        // ANGENOMMEN, Id UNBEKANNT: 2xx heißt, die Seite hat den Rumpf angenommen. Ob sie ihn später
        // moderiert oder ablehnt, sagt sie hier nicht — und was sie bei Annahme sagt, ist nicht belegt.
        // Keine Id, keine Moderation, nur ihr Text.
        _logger?.LogInformation(
            "[moviesubtitles] upload answered HTTP {Status} — accepted, id unknown; the answer text is passed through unchanged",
            result.Status);
        return new EndpointAnswer(Provider, true, null, result.BodyBytes, result.Response, false);
    }

    private EndpointAnswer Refuse(string reason) =>
        new(Provider, false, reason, 0, null, _dryRun);
}
