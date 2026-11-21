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
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Configuration;
using Jellyfin.Plugin.SubScribe.Data;
using Jellyfin.Plugin.SubScribe.Registry;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M483: the tvsubtitles.net download endpoint. The source has no id index, so the series is resolved
/// by NAME out of its show list, then the season's episode pages are walked until one states the wanted
/// season and episode. That walk is the price of a source without an API — it is bounded by the season
/// (at most 22 pages) and stops at the first match.
/// <para>
/// WIRED = false (F-M484): the endpoint exists and is tested, but the pipeline's run loop still calls
/// SubDL only. Nothing is gained by claiming otherwise in the GUI.
/// </para>
/// </summary>
public sealed class TvSubtitlesDownloadEndpoint : IDownloadEndpoint
{
    private readonly TvSubtitlesClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvSubtitlesDownloadEndpoint"/> class.
    /// </summary>
    /// <param name="client">The tvsubtitles.net client.</param>
    public TvSubtitlesDownloadEndpoint(TvSubtitlesClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>
    /// F-M533: how many entries of the LAST search were dropped because a field contradicted the
    /// site's own vocabulary — the operator's rule (17.11.2026): report it, drop the entry, keep the
    /// run going.
    /// </summary>
    public int LastRejectedByFormat { get; private set; }

    /// <inheritdoc />
    public SubtitleProvider Provider => SubtitleProvider.TvSubtitles;

    /// <inheritdoc />
    public bool Wired => true;

    /// <summary>
    /// F-M483: the site's language code for a two-letter language. SubDL speaks ISO-ish codes, the site
    /// has its own set (F-M480).
    /// <para>
    /// F-M502: die Abbildung selbst steht seit der Operator-Anweisung vom 15.11.2026 („Aber halt tvst
    /// spezifisch. Das andere mapping ist subdl spezifisch.") in
    /// <see cref="TvSubtitlesClient.SiteLanguage"/> und wird hier nur durchgereicht — eine zweite
    /// Tabelle an dieser Stelle waere genau die gemeinsame Zuordnung, die es nicht geben soll.
    /// </para>
    /// </summary>
    /// <param name="language">Two-letter language, any case.</param>
    /// <returns>The site's code, or null when the site has no such language.</returns>
    public static string? SiteLanguage(string? language) => TvSubtitlesClient.SiteLanguage(language);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubtitleHit>> SearchAsync(
        SubtitleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var language = SiteLanguage(query.Language)
            ?? throw new ArgumentException(
                "tvsubtitles.net has no language '" + query.Language + "'", nameof(query));

        if (string.IsNullOrWhiteSpace(query.ShowName))
        {
            // No id index on this source: without a title there is nothing to look up.
            return Array.Empty<SubtitleHit>();
        }

        // F-M523: was diese Suche auflöst, gilt nur für sie. Der alte Wert darf nie weiterleben.
        _client.LastResolvedShowId = null;
        LastRejectedByFormat = 0;

        var shows = await _client.ShowsAsync(cancellationToken).ConfigureAwait(false);
        var showId = TvSubtitlesClient.ResolveShow(shows, query.ShowName);
        if (showId is null)
        {
            return Array.Empty<SubtitleHit>();
        }

        // F-M523: die Nummer an den Aufrufer weiterreichen — der Download schreibt sie in den Store,
        // damit der nächste Lauf die Serienliste gar nicht erst holen muss (F-M501).
        _client.LastResolvedShowId = showId.Value;

        // F-M664 (Betreiber-Anweisung 21.11.2026: „Innerhalb von tvst erst staffelzip dann einzel, dann
        // hi."): zwei Listen, weil es zwei Seiten und drei Stufen sind. `hits` traegt die REGULAERE Seite
        // (Stufe 1 und 2), `hiTreffer` die HI-Seite (Stufe 3). Die HI-Treffer werden erst am Ende
        // angehaengt — eingesammelt werden sie dort, wo sie anfallen (ZIP-Namen, Ohr-Bild), aber
        // weitergegeben erst, nachdem beide regulaeren Stufen durch sind.
        var hits = new List<SubtitleHit>();
        var hiTreffer = new List<SubtitleHit>();

        // F-M663 (Bediener-Anweisung 21.11.2026): STUFE 1 ist die Staffel-ZIP. Gemessen an MobLand S02:
        // 27 Dateien fuer 9 Folgen in EINER ZIP je Sprache (610.807 B), Regular und HI durch `.HI.`
        // getrennt. Der Merker im Client haelt sie fuer den ganzen Lauf, also kostet die Staffel EINEN
        // Abruf statt einen je Folge.
        bool regulaerGedeckt = false;
        bool hiGedeckt = false;
        if (query.Season > 0)
        {
            var pack = await _client.SeasonPackAsync(showId.Value, query.Season, language, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < pack.Count; i++)
            {
                var datei = pack[i];
                if (datei.Season != query.Season || datei.Episode != query.Episode)
                {
                    continue;
                }

                if (!string.Equals(datei.Language, language, StringComparison.Ordinal))
                {
                    continue;
                }

                string release = datei.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                    ? datei.Name[..^4]
                    : datei.Name;

                // F-M663: dieselben Tore wie ein Eintrag der Folgenseite — Sprache und Rip gegen das
                // Vokabular der Seite. Nur der MELDEWEG faellt hier aus: ein ZIP-Eintrag hat keine sid,
                // und eine „Report bad"-Anfrage mit erfundener Nummer waere schlimmer als kein Bericht.
                var pruefung = TvstMetadataChecks.CheckEntry(
                    new TvSubtitlesEntry(string.Empty, datei.Language, string.Empty, release, string.Empty, datei.HearingImpaired));
                if (pruefung.Count > 0)
                {
                    LastRejectedByFormat++;
                    continue;
                }

                var zipTreffer = new SubtitleHit(
                    SubtitleProvider.TvSubtitles,
                    SeasonPackId(showId.Value, query.Season, language, i),
                    datei.Language,
                    string.Empty,
                    release,
                    false,
                    datei.HearingImpaired);

                // F-M664: die Marke entscheidet die SEITE, nicht die Stufe. Die regulaere Fassung der
                // ZIP ist Stufe 1 und geht sofort weiter; die HI-Fassung wandert in den Stufe-3-Korb und
                // wird erst nach dem Einzelweg angehaengt.
                if (datei.HearingImpaired)
                {
                    hiGedeckt = true;
                    hiTreffer.Add(zipTreffer);
                }
                else
                {
                    regulaerGedeckt = true;
                    hits.Add(zipTreffer);
                }
            }
        }

        // F-M663/F-M664: STUFE 2 ist der Einzelweg der Folgenseite, und STUFE 3 ist HI. Beide brauchen
        // dieselbe Seite; gelesen wird sie EINMAL, aber jede Seite nimmt nur, was ihre Stufe deckt:
        //   * regulaere Eintraege nur, solange Stufe 1 (ZIP) den regulaeren Platz nicht gedeckt hat,
        //   * HI-Eintraege nur, solange die HI-Seite offen ist — und sie wandern in `hiTreffer`.
        // Ist beides gedeckt, wird die Folgenseite gar nicht gefragt.
        if (!regulaerGedeckt || !hiGedeckt)
        {
            var pages = await _client.EpisodePagesAsync(showId.Value, query.Season, cancellationToken)
                .ConfigureAwait(false);

            foreach (var page in pages)
            {
                var (season, episode) = await _client.EpisodeLabelAsync(page, cancellationToken).ConfigureAwait(false);

                // F-M533: eine Staffel/Folge-Angabe ausserhalb der Werte, die das Formular kennt, ist ein
                // Falscheintrag der Seite — melden und die Seite ueberspringen, nicht raten.
                if (await TvstMetadataChecks.EmitAsync(
                        _client, TvstMetadataChecks.CheckEpisodeLabel(season, episode), null, null, cancellationToken)
                        .ConfigureAwait(false))
                {
                    LastRejectedByFormat++;
                    continue;
                }

                if (season != query.Season || episode != query.Episode)
                {
                    continue;
                }

                var entries = await _client.EntriesAsync(page, cancellationToken).ConfigureAwait(false);
                foreach (var e in entries)
                {
                    if (!string.Equals(e.Language, language, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // F-M663/F-M664: eine Seite, die ihre Stufe schon gedeckt hat, wird hier nicht ein
                    // zweites Mal gefuellt — und eine HI-Datei landet nie auf der regulaeren Seite.
                    if (e.HearingImpaired ? hiGedeckt : regulaerGedeckt)
                    {
                        continue;
                    }

                    var falsch = TvstMetadataChecks.CheckEntry(e);
                    if (falsch.Count > 0)
                    {
                        await TvstMetadataChecks.EmitAsync(_client, falsch, e.SubtitleId, e.Release, cancellationToken)
                            .ConfigureAwait(false);
                        LastRejectedByFormat++;
                        continue;
                    }

                    var treffer = new SubtitleHit(
                        SubtitleProvider.TvSubtitles, e.SubtitleId, e.Language, e.Rip, e.Release, false, e.HearingImpaired);

                    if (e.HearingImpaired)
                    {
                        hiGedeckt = true;
                        hiTreffer.Add(treffer);
                    }
                    else
                    {
                        regulaerGedeckt = true;
                        hits.Add(treffer);
                    }
                }

                if (regulaerGedeckt && hiGedeckt)
                {
                    break;
                }
            }
        }

        // F-M664: STUFE 3 — erst JETZT gehen die HI-Treffer weiter, nach der Staffel-ZIP und nach dem
        // Einzeleintrag. Sie stehen deshalb hinter jeder regulaeren Zeile: wer die Antwort liest, sieht
        // die Reihenfolge der Stufen, nicht die Reihenfolge der Abrufe.
        hits.AddRange(hiTreffer);
        return hits;
    }

    /// <summary>
    /// F-M663: der Griff eines Treffers aus der Staffel-ZIP. Die Seite kennt fuer eine Staffel-ZIP
    /// keine Einzel-Ids (anders als fuer jede Datei der Folgenseite) — der Griff nennt deshalb, was der
    /// Merker im Client braucht: Serie, Staffel, Sprache und die Nummer des Eintrags im Archiv.
    /// </summary>
    /// <param name="showId">The site's show id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="language">The site's language code.</param>
    /// <param name="index">Position in the archive's entry list.</param>
    /// <returns>The handle, e.g. <c>seasonpack:3234:2:en:4</c>.</returns>
    public static string SeasonPackId(int showId, int season, string language, int index) =>
        "seasonpack:"
        + showId.ToString(CultureInfo.InvariantCulture) + ":"
        + season.ToString(CultureInfo.InvariantCulture) + ":"
        + (language ?? string.Empty).Trim().ToLowerInvariant() + ":"
        + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// F-M663: liest <see cref="SeasonPackId"/> zurueck. Falsch fuer jeden anderen Griff — eine
    /// Subtitle-id der Seite ist eine Zahl und darf nie als Archiv-Eintrag gelesen werden.
    /// </summary>
    /// <param name="id">The handle a hit carries.</param>
    /// <param name="showId">Show id, 0 when the handle is not a season pack.</param>
    /// <param name="season">Season, 0 when the handle is not a season pack.</param>
    /// <param name="language">The site's language code, empty when the handle is not a season pack.</param>
    /// <param name="index">Entry number, -1 when the handle is not a season pack.</param>
    /// <returns>True when the handle names an entry of a season archive.</returns>
    public static bool TryParseSeasonPackId(
        string? id, out int showId, out int season, out string language, out int index)
    {
        showId = 0;
        season = 0;
        language = string.Empty;
        index = -1;
        if (string.IsNullOrEmpty(id) || !id.StartsWith("seasonpack:", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = id.Split(':');
        return parts.Length == 5
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out showId)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out season)
            && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
            && (language = parts[3]).Length > 0
            && index >= 0;
    }

    /// <inheritdoc />
    public async Task<SubtitlePayload?> FetchAsync(
        SubtitleHit hit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (string.IsNullOrWhiteSpace(hit.Id))
        {
            return null;
        }

        // F-M663: ein Treffer aus der Staffel-ZIP wird aus dem Merker gelesen — KEINE zweite Anfrage.
        if (TryParseSeasonPackId(hit.Id, out int showId, out int season, out string packLanguage, out int index))
        {
            var (packName, packData) = await _client
                .DownloadPackMemberAsync(showId, season, packLanguage, index, cancellationToken)
                .ConfigureAwait(false);
            return packData.Length == 0 ? null : new SubtitlePayload(packName, packData, hit.Language);
        }

        var (name, data) = await _client.DownloadAsync(hit.Id, cancellationToken).ConfigureAwait(false);
        return data.Length == 0 ? null : new SubtitlePayload(name, data, hit.Language);
    }
}

/// <summary>
/// F-M483: the tvsubtitles.net upload endpoint. The site is written by a form, so the ticket is turned
/// into the form's fields (F-M481) and sent as multipart. A dry run builds the same body and sends
/// nothing — which is also the default, so a mis-wired caller cannot upload by accident.
/// </summary>
public sealed class TvSubtitlesUploadEndpoint : IUploadEndpoint
{
    private readonly TvSubtitlesClient _client;
    private readonly bool _dryRun;
    private readonly ILogger? _logger;
    private readonly bool _seasonPacks;
    private readonly ContentHashRegistry? _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvSubtitlesUploadEndpoint"/> class.
    /// </summary>
    /// <param name="client">The tvsubtitles.net client.</param>
    /// <param name="dryRun">True (default) builds the request and sends nothing. The plugin's own
    /// DryRun switch is passed in here — it is not read inside, so the endpoint stays testable.</param>
    /// <param name="logger">Optional logger — it only ever says why an id could not be read back.</param>
    /// <param name="seasonPacks">
    /// F-M641: der vorhandene Pack-Schalter der GUI (<see cref="PluginConfiguration.UploadSeasonPacks"/>),
    /// hereingereicht wie der Trockenlauf — der Endpunkt liest die Konfiguration nicht selbst. Steht er
    /// aus, wird ein Pack abgelehnt, statt an einer Einstellung vorbei zu senden.
    /// </param>
    /// <param name="registry">
    /// F-M642: der Store, in den ein erfolgreicher Pack seine Zeile schreibt. Optional, weil ein
    /// Trockenlauf und jede Suite ohne Store laufen muessen; ohne ihn wird nichts gebucht.
    /// </param>
    public TvSubtitlesUploadEndpoint(
        TvSubtitlesClient client,
        bool dryRun = true,
        ILogger? logger = null,
        bool seasonPacks = false,
        ContentHashRegistry? registry = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _dryRun = dryRun;
        _logger = logger;
        _seasonPacks = seasonPacks;
        _registry = registry;
    }

    /// <inheritdoc />
    public SubtitleProvider Provider => SubtitleProvider.TvSubtitles;

    /// <inheritdoc />
    public bool Wired => true;

    /// <inheritdoc />
    public async Task<EndpointAnswer> SendAsync(
        UploadTicket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.SeasonPack)
        {
            // F-M641 (Betreiber-Vorgabe 20.11.2026): ein Staffel-Pack geht als EIN Zip in den
            // Staffel-Slot der Seite (episode=0) — nicht als Folge ohne Nummer. Zwei Dinge muessen
            // vorher stimmen, sonst wird nichts gesendet:
            //   1. der vorhandene Pack-Schalter der GUI ist an (kein eigener Schalter fuer tvst),
            //   2. die Datei ist ein Zip (sonst legte die Seite eine .srt in den Staffel-Slot).
            if (!_seasonPacks)
            {
                return new EndpointAnswer(
                    Provider, false, "season packs are switched off in the settings (UploadSeasonPacks)",
                    0, null, _dryRun);
            }

            if (!ticket.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return new EndpointAnswer(
                    Provider, false, "a season pack is one .zip — the season slot takes the container, not a single .srt",
                    0, null, _dryRun);
            }
        }

        var language = TvSubtitlesDownloadEndpoint.SiteLanguage(ticket.Language);
        if (language is null)
        {
            return new EndpointAnswer(
                Provider, false, "tvsubtitles.net has no language '" + ticket.Language + "'", 0, null, _dryRun);
        }

        if (string.IsNullOrWhiteSpace(ticket.ShowName) && ticket.TvstShowId is not > 0)
        {
            return new EndpointAnswer(
                Provider, false, "a show name is needed — this source has no id index", 0, null, _dryRun);
        }

        // F-M501: die Nummer aus dem Store führt. Steht sie im Ticket, wird die Serienliste weder
        // geholt noch durchsucht — die Auflösung hat sie bereits geprüft und geschrieben. Nur ohne
        // Nummer (direkter Aufruf, ältere Zeile) wird der Name gesucht, und dann mit demselben engen
        // Vergleich wie dort (kein „enthält“).
        int? showId = ticket.TvstShowId is > 0 ? ticket.TvstShowId : null;
        if (showId is null)
        {
            var shows = await _client.ShowsAsync(cancellationToken).ConfigureAwait(false);
            showId = TvSubtitlesClient.ResolveShow(shows, ticket.ShowName!);
        }

        if (showId is null)
        {
            return new EndpointAnswer(
                Provider, false, "show not on tvsubtitles.net: " + ticket.ShowName, 0, null, _dryRun);
        }

        TvSubtitlesUpload upload;
        try
        {
            var tmdb = long.TryParse(ticket.TmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
                ? t
                : (long?)null;
            if (ticket.SeasonPack)
            {
                // F-M641: Staffel-Slot (episode=0), Zip, Pack-Name im release-Feld, Staffelzeile im
                // Kommentar. `hi` geht nur mit, wenn der GANZE Pack HI ist — der Aufrufer bildet je
                // (Sprache, Variante) ein eigenes Paket, deshalb beschreibt das Ticket den Pack.
                upload = TvSubtitlesClient.BuildSeasonPack(
                    showId.Value,
                    ticket.Season,
                    language!,
                    ticket.FileName,
                    ticket.Data,
                    ticket.Rip,
                    ticket.Release,
                    tmdb,
                    ticket.ImdbId,
                    hearingImpaired: ticket.HearingImpaired,
                    showTitle: ticket.ShowName,
                    year: ticket.MovieYear);
            }
            else
            {
                upload = TvSubtitlesClient.BuildUpload(
                    showId.Value,
                    ticket.Season,
                    ticket.Episode,
                    language,
                    ticket.FileName,
                    ticket.Data,
                    ticket.Rip,
                    ticket.Release,
                    tmdb,
                    ticket.ImdbId,
                    // F-M572 (Operator-Anweisung 17.11.2026: „Dann trag es zukünftig in die comments ein"):
                    // der Folgentitel steht im Kommentar. Die Seite baut den DATEINAMEN aus ihrem eigenen
                    // Folgeneintrag — an „Mo" S01E05 zu sehen: Jellyfin führt „Tombstone", die Seite zeigt
                    // „Episode 5", und der Name ist kein Feld, das wir senden. Der Kommentar ist der Ort, an
                    // dem der richtige Titel mitkommt.
                    commentExtra: null,
                    doubleEpisode: ticket.DoubleEpisode,
                    hearingImpaired: ticket.HearingImpaired,
                    showTitle: ticket.ShowName,
                    episodeTitle: ticket.EpisodeTitle,
                    year: ticket.MovieYear);
            }
        }
        catch (ArgumentException ex)
        {
            return new EndpointAnswer(Provider, false, ex.Message, ticket.Data.Length, null, _dryRun);
        }

        var result = await _client.UploadAsync(upload, _dryRun, cancellationToken).ConfigureAwait(false);
        var ok = !_dryRun && result.Status is >= 200 and < 300;

        // F-M612 (Operator-Entscheidung 18.11.2026: „Brauchen wir die kennung irgendwo für? Wenn nicht
        // kann es weg, schreiben wir keine oder eine eigene"): das Nachlesen der Kennung ist WEG.
        // F-M488 hatte es eingebaut, weil die Seite auf den Upload keine Id zurueckgibt; nachgelesen
        // wurde sie von der Folgenseite, mit 60 s Frist. Gemessen am 18.11.2026: **88 Versuche, 0
        // lesbare Kennungen, je ~15 s Wartezeit** — bei einer Datei mit ~24 Spuren sechs Minuten
        // Laufzeit fuer nichts. Gebraucht wird die Zahl nirgends: sie steht nur als Marke im Store
        // („fuer tvst hochgeladen"), und `MarkOf` schreibt ohne sie die eigene Marke
        // `own:tvsubtitles:<Zeit>:<hex>`. Adressiert wird die Seite nie mit dieser Nummer.
        string? id = null;

        // F-M642: ein erfolgreicher Pack schreibt seine Zeile — in SEINER Quelle, nicht in der von
        // SubDL. Der Schluessel nennt die Quelle, deshalb kann der tvst-Pack den SubDL-Pack derselben
        // Staffel nicht ueberschreiben. Gebucht wird nur, was wirklich hinausging: im Trockenlauf und
        // ohne Store bleibt die Zeile aus. Die Folgen kommen aus dem Zip, das gerade gesendet wurde —
        // was die Dateinamen nicht nennen, wird nicht geraten (F-M466: eine Zeile ohne Beleg ist
        // schlimmer als keine).
        if (ticket.SeasonPack && ok && _registry is not null && !string.IsNullOrWhiteSpace(ticket.ImdbId))
        {
            var episodes = TvSubtitlesClient.EpisodesInZip(ticket.Data);
            var imdb = ticket.ImdbId!;
            _registry.MarkAndFlush(() => _registry.RecordPack(
                imdb,
                ticket.Season,
                ticket.Language,
                ticket.HearingImpaired,
                ticket.Release,
                // F-M612: die Seite gibt auf den Upload keine Kennung zurueck.
                packId: null,
                episodes,
                absorbedIds: null,
                fingerprint: null,
                source: PackEntity.TvSubtitlesSource));
        }

        return new EndpointAnswer(
            Provider,
            _dryRun || ok,
            _dryRun ? null : (ok ? null : "HTTP " + result.Status),
            result.BodyBytes,
            result.Response,
            !result.Sent,
            id,
            // F-M609: der Status reist mit — 429/5xx sind keine Ablehnung, sondern keine Antwort.
            result.Sent ? result.Status : null);
    }
}
