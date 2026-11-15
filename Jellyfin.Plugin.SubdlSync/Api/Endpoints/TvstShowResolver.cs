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
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Registry;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Api.Endpoints;

/// <summary>
/// F-M501 (Operator-Anweisung 15.11.2026): die Nummer der Serie bei tvsubtitles.net — ermittelt,
/// gespeichert, wiederverwendet.
/// <para>
/// WARUM ES DIESE KLASSE GIBT. Der tvst-Upload braucht eine Seriennummer, und bis hierher löste der
/// Endpunkt sie bei JEDEM Aufruf neu aus dem DATEINAMEN (<c>MediaNameParser</c>, dann
/// <see cref="TvSubtitlesClient.ResolveShow"/> mit „enthält“-Stufe). Beides ist eine Vermutung über
/// eine Schreibweise: der Dateiname trägt, was der Uploader getippt hat, und „enthält“ trifft bei
/// „The Middle“ auch „Malcolm in the Middle“. Die Nummer stand nirgends, also konnte sie auch nicht
/// gelernt werden — und eine Serie, die die Seite nicht führt, wurde nie angelegt, weshalb der Upload
/// für sie dauerhaft mit „show not on tvsubtitles.net“ endete.
/// </para>
/// <para>
/// DIE REIHENFOLGE IST FEST UND KURZ (Operator-Entscheidung: „Tmdb titel führt“, „Tmdb titel führt auch
/// für Doppeleinträge“):
/// <list type="number">
/// <item><description>
/// Steht <see cref="MediaEntity.TvstShowId"/> am Medium, wird SIE benutzt — kein Netzzugriff zur Suche,
/// kein Vergleich, keine Anlage. Die Nummer ist bewiesen worden, als sie geschrieben wurde.
/// </description></item>
/// <item><description>
/// Sonst wird der TMDb-Titel in der Serienliste der Seite gesucht — eng verglichen: Groß-/Kleinschreibung
/// egal, <c>&amp;</c>/<c>and</c>/<c>und</c> gleich, Leerraum normalisiert, sonst Gleichheit
/// (<see cref="TvSubtitlesClient.NormalizeShowName"/>). Kein Teilstring-Treffer, kein Ähnlichkeitsmaß.
/// Ein Treffer wird ins Medium geschrieben und benutzt.
/// </description></item>
/// <item><description>
/// Kein Treffer: die Serie wird angelegt — Name und Jahr aus TMDb, Staffelzahl aus TMDb auf 1..22
/// begrenzt (weiter reicht das Formular der Seite nicht). Danach wird die Liste ERNEUT geholt und mit
/// demselben engen Vergleich gesucht; erst wenn die Nummer dort steht, wird sie geschrieben. Eine unklare
/// Antwort der Seite (Timeout, unparsbare Seite) wird einmal nachgefragt; bleibt es unklar, wird NICHTS
/// geschrieben, eine Zeile protokolliert und tvst für dieses Medium übersprungen.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// ANLEGEN HÖCHSTENS EINMAL JE MEDIUM UND LAUF. Ein Lauf, der eine Serie nicht anlegen kann, darf sie
/// nicht bei jeder Folge erneut versuchen: die Seite ist ein Formular, kein Idempotenz-Versprechen, und
/// ein Fehlschlag wiederholt sich in derselben Sekunde nicht anders. <see cref="BeginRun"/> leert die
/// Sperre zu Beginn eines Laufs.
/// </para>
/// <para>
/// NICHTS HIERAN BLOCKIERT DIE KETTE. Jeder Ausgang ist ein Ergebnis, kein Wurf: fehlt die Nummer,
/// überspringt der Aufrufer tvst und SubDL bekommt seinen Versuch wie zuvor. Im Trockenlauf wird
/// nichts angelegt und nichts geschrieben.
/// </para>
/// </summary>
public sealed class TvstShowResolver
{
    private readonly TvSubtitlesClient _client;
    private readonly ILogger? _logger;

    /// <summary>
    /// F-M501: die Medien, für die dieser Lauf das Anlegen schon versucht hat. Der Schlüssel ist das
    /// Medium (media hash), nicht der Titel: zwei Folgen derselben Serie sind zwei Medien und legen
    /// dieselbe Serie NICHT zweimal an — gefunden wird sie beim zweiten Mal in der Liste.
    /// </summary>
    private readonly HashSet<string> _createAttempted = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TvstShowResolver"/> class.
    /// </summary>
    /// <param name="client">Der tvsubtitles.net-Client (Seitenliste und Anlage).</param>
    /// <param name="logger">Optionaler Logger — er nennt den Grund, wenn nichts geschrieben wurde.</param>
    public TvstShowResolver(TvSubtitlesClient client, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    /// <summary>F-M501: beginnt einen Lauf — die Anlege-Sperre gilt je Lauf, nicht je Prozess.</summary>
    public void BeginRun() => _createAttempted.Clear();

    /// <summary>
    /// F-M501: die Nummer der Serie für dieses Medium — in der festgelegten Reihenfolge (gespeichert →
    /// exakt in der Liste → anlegen und erneut exakt suchen).
    /// </summary>
    /// <param name="registry">Der Store, aus dem die gespeicherte Nummer kommt und in den die neue geht.</param>
    /// <param name="mediaHash">Medium (OSHash); null heißt „kein Medium bekannt“ — dann wird nichts geschrieben.</param>
    /// <param name="series">Der TMDb-Datensatz, dessen Titel führt.</param>
    /// <param name="dryRun">True: nichts anlegen, nichts schreiben.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Das Ergebnis; <see cref="TvstShowResolution.Resolved"/> false heißt „tvst überspringen“.</returns>
    public async Task<TvstShowResolution> ResolveForMediaAsync(
        ContentHashRegistry registry,
        string? mediaHash,
        TmdbSeriesInfo series,
        bool dryRun,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(series);

        // (a) Die gespeicherte Nummer führt. Kein Netzzugriff: sie wurde geprüft, als sie geschrieben
        // wurde, und eine erneute Suche könnte sie nur durch eine schlechtere Antwort ersetzen.
        var stored = string.IsNullOrEmpty(mediaHash) ? null : registry.GetMedia(mediaHash)?.TvstShowId;
        if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var known) && known > 0)
        {
            return new TvstShowResolution(known, TvstShowStatus.Stored);
        }

        var title = (series.Name ?? string.Empty).Trim();
        if (title.Length < 2)
        {
            Log("[tvsubtitles] {Media}: der TMDb-Datensatz nennt keinen brauchbaren Titel — tvst wird übersprungen.",
                mediaHash ?? "?");
            return new TvstShowResolution(null, TvstShowStatus.NoTitle);
        }

        // (b) Exakt in der Serienliste. Eine leere oder nicht lesbare Liste ist KEIN „es gibt die Serie
        // nicht“ — sie wird einmal nachgefragt, und erst wenn sie wieder unklar ist, gilt der Lauf als
        // blind (siehe unten).
        var shows = await ShowsOrNullAsync(ct).ConfigureAwait(false);
        if (shows is null)
        {
            Log("[tvsubtitles] {Media}: die Serienliste der Seite war nicht lesbar (zweimal) — \"{Title}\" bleibt unaufgelöst, nichts wird geschrieben.",
                mediaHash ?? "?", title);
            return new TvstShowResolution(null, TvstShowStatus.Unclear);
        }

        var exact = TvSubtitlesClient.ResolveShow(shows, title);
        if (exact is > 0)
        {
            Persist(registry, mediaHash, exact.Value);
            return new TvstShowResolution(exact.Value, TvstShowStatus.Found);
        }

        // (c) Anlegen — im Trockenlauf nie.
        if (dryRun)
        {
            return new TvstShowResolution(null, TvstShowStatus.WouldCreate);
        }

        var attemptKey = string.IsNullOrEmpty(mediaHash) ? "title:" + title : mediaHash;
        if (!_createAttempted.Add(attemptKey))
        {
            // Schon einmal in diesem Lauf versucht: kein zweiter Versuch, kein Schreibvorgang.
            return new TvstShowResolution(null, TvstShowStatus.CreateAlreadyTried);
        }

        if (series.FirstAirYear is not int year || year < 1900 || year > 2100)
        {
            Log("[tvsubtitles] {Media}: TMDb nennt kein Erstausstrahlungsjahr für \"{Title}\" — die Seite verlangt eines, also wird nichts angelegt.",
                mediaHash ?? "?", title);
            return new TvstShowResolution(null, TvstShowStatus.NoYear);
        }

        // Die Seite kennt höchstens MaxSeason Staffeln; ihre eigene Prüfung lässt nur 1..22 zu.
        var seasons = Math.Clamp(series.Seasons ?? 1, 1, TvSubtitlesClient.MaxSeason);

        try
        {
            var answer = await _client
                .AddShowAsync(TvSubtitlesClient.BuildAddShow(title, year, seasons), dryRun: false, ct)
                .ConfigureAwait(false);
            if (answer.Status is not (>= 200 and < 300))
            {
                Log("[tvsubtitles] {Media}: \"{Title}\" konnte nicht angelegt werden (HTTP {Status}) — nichts wird geschrieben, tvst wird übersprungen.",
                    mediaHash ?? "?", title, answer.Status?.ToString(CultureInfo.InvariantCulture) ?? "?");
                return new TvstShowResolution(null, TvstShowStatus.Unclear);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("[tvsubtitles] {Media}: Anlegen von \"{Title}\" scheiterte ({Error}) — nichts wird geschrieben, tvst wird übersprungen.",
                mediaHash ?? "?", title, ex.Message);
            return new TvstShowResolution(null, TvstShowStatus.Unclear);
        }

        // Die Nummer wird NICHT angenommen, sondern nachgesehen: das Formular antwortet ohne Id, und
        // eine erfundene Nummer wäre ein Store, der für eine Zeile bürgt, die es nicht gibt.
        var after = await ShowsOrNullAsync(ct).ConfigureAwait(false);
        if (after is null)
        {
            Log("[tvsubtitles] {Media}: \"{Title}\" wurde angelegt, aber die Serienliste war danach nicht lesbar — die Nummer bleibt unbekannt und wird nicht geschrieben.",
                mediaHash ?? "?", title);
            return new TvstShowResolution(null, TvstShowStatus.Unclear);
        }

        var created = TvSubtitlesClient.ResolveShow(after, title);
        if (created is not > 0)
        {
            Log("[tvsubtitles] {Media}: \"{Title}\" wurde angelegt, steht aber nicht (auch nicht eng verglichen) in der Serienliste — die Nummer bleibt unbekannt und wird nicht geschrieben.",
                mediaHash ?? "?", title);
            return new TvstShowResolution(null, TvstShowStatus.Unclear);
        }

        Persist(registry, mediaHash, created.Value);
        Log("[tvsubtitles] {Media}: \"{Title}\" ({Year}) als Serie {Id} angelegt und gespeichert.",
            mediaHash ?? "?", title, year, created.Value);
        return new TvstShowResolution(created.Value, TvstShowStatus.Created);
    }

    /// <summary>
    /// F-M501: die Serienliste der Seite — einmal, bei unklarer Antwort (<c>null</c>, leere Liste,
    /// Ausnahme, Timeout) ein zweites Mal. Danach ist die Antwort, was sie ist.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die Liste, oder null wenn sie auch beim zweiten Versuch unklar blieb.</returns>
    private async Task<IReadOnlyDictionary<int, string>?> ShowsOrNullAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var shows = await _client.ShowsAsync(ct).ConfigureAwait(false);
                if (shows.Count > 0)
                {
                    return shows;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Ein Timeout/eine Ausnahme ist eine unklare Antwort, kein „Serie fehlt“. Der zweite
                // Versuch steht direkt daneben — mehr nicht, sonst wird aus einem Ausfall ein Sturm.
                _logger?.LogWarning("[tvsubtitles] show list not readable (attempt {Attempt}): {Error}", attempt + 1, ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// F-M501: schreibt die Nummer ins Medium — und nur dorthin, nur wenn sie wirklich gesehen wurde.
    /// </summary>
    /// <param name="registry">Der Store.</param>
    /// <param name="mediaHash">Medium; null heißt „kein Medium bekannt“, dann bleibt es beim Benutzen.</param>
    /// <param name="showId">Die gesehene Nummer.</param>
    private static void Persist(ContentHashRegistry registry, string? mediaHash, int showId)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return;
        }

        registry.EnsureMedia(mediaHash, patch: media =>
            media.TvstShowId = showId.ToString(CultureInfo.InvariantCulture));
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
/// F-M501: die Ausgänge der Serienauflösung — je einer eine Entscheidung des Aufrufers.
/// </summary>
public static class TvstShowStatus
{
    /// <summary>Die Nummer stand am Medium (F-M501 a).</summary>
    public const string Stored = "stored";

    /// <summary>Der TMDb-Titel stand exakt in der Serienliste; die Nummer wurde gespeichert (F-M501 b).</summary>
    public const string Found = "found";

    /// <summary>Die Serie wurde angelegt und danach exakt wiedergefunden (F-M501 c).</summary>
    public const string Created = "created";

    /// <summary>Der Trockenlauf hätte angelegt — er hat es nicht getan und nichts geschrieben.</summary>
    public const string WouldCreate = "would-create";

    /// <summary>Die Seite antwortete unklar (zweimal): nichts geschrieben, tvst übersprungen.</summary>
    public const string Unclear = "unclear";

    /// <summary>Der TMDb-Datensatz trug keinen brauchbaren Titel.</summary>
    public const string NoTitle = "no-title";

    /// <summary>TMDb nennt kein Erstausstrahlungsjahr — die Seite verlangt eines beim Anlegen.</summary>
    public const string NoYear = "no-year";

    /// <summary>Das Anlegen wurde in diesem Lauf für dieses Medium schon einmal versucht.</summary>
    public const string CreateAlreadyTried = "create-already-tried";
}

/// <summary>
/// F-M501: was die Auflösung ergeben hat.
/// </summary>
/// <param name="ShowId">Die Nummer der Serie, oder null.</param>
/// <param name="Status">Einer der Werte aus <see cref="TvstShowStatus"/>.</param>
public sealed record TvstShowResolution(int? ShowId, string Status)
{
    /// <summary>Gets a value indicating whether eine Nummer vorliegt, mit der der Upload arbeiten kann.</summary>
    public bool Resolved => ShowId is > 0;
}
