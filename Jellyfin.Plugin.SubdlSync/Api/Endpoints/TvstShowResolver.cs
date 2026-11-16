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
using Jellyfin.Plugin.SubScribe.Data;
using Jellyfin.Plugin.SubScribe.Registry;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

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
/// begrenzt (weiter reicht das Formular der Seite nicht). Danach wird die Nummer <b>nachgesehen</b>
/// (F-M516: erst die Seitensuche, dann die Liste) und mit demselben engen Vergleich geprüft; erst wenn
/// sie dort steht, wird sie geschrieben. Die Antwort auf die Anlage allein entscheidet nichts: bestätigt
/// sie den Erfolg nicht und die Serie steht trotzdem da, war sie schon vorhanden (<c>existed</c>);
/// bestätigt sie ihn und die Nummer ist nirgends lesbar, lautet das Ergebnis ehrlich
/// <c>created-id-unknown</c> und es wird NICHTS geschrieben.
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
    /// <param name="mediaHash">Medium (OSHash); null heißt „kein Medium bekannt" — dann führt der Pfad.</param>
    /// <param name="series">Der TMDb-Datensatz, dessen Titel führt.</param>
    /// <param name="dryRun">True: nichts anlegen, nichts schreiben.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="mediaPath">F-M517: der Pfad der Mediendatei — die zweite Adresse der Medienzeile,
    /// wenn kein Hash bekannt ist.</param>
    /// <returns>Das Ergebnis; <see cref="TvstShowResolution.Resolved"/> false heißt „tvst überspringen".</returns>
    public async Task<TvstShowResolution> ResolveForMediaAsync(
        ContentHashRegistry registry,
        string? mediaHash,
        TmdbSeriesInfo series,
        bool dryRun,
        CancellationToken ct,
        string? mediaPath = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(series);

        // (a) Die gespeicherte Nummer führt. Kein Netzzugriff: sie wurde geprüft, als sie geschrieben
        // wurde, und eine erneute Suche könnte sie nur durch eine schlechtere Antwort ersetzen.
        // F-M517: ohne Medien-Hash wird dieselbe Zeile über ihren PFAD gelesen — sonst bliebe eine über
        // den Pfad gespeicherte Nummer für jeden folgenden Lauf unsichtbar (dieselbe Lücke wie bei der
        // Filmnummer, dieselbe Zeile, dieselbe Antwort).
        var stored = !string.IsNullOrEmpty(mediaHash)
            ? registry.GetMedia(mediaHash!)?.TvstShowId
            : registry.TvstShowIdAtPath(mediaPath);
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
            var wie = Persist(registry, mediaHash, mediaPath, exact.Value);
            Log("[tvsubtitles] {Media}: \"{Title}\" ist Serie {Id} — {Note}",
                mediaHash ?? "?", title, exact.Value, MediaNumberNote.Of(wie));
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

        // F-M516: gesendet wird der Rumpf des echten Formulars (name, year_s, seasons, go als
        // multipart/form-data). Die Antwort wird KLASSIFIZIERT, nicht geglaubt — siehe unten.
        string outcome;
        int? status = null;
        string? siteText = null;
        try
        {
            var answer = await _client
                .AddShowAsync(TvSubtitlesClient.BuildAddShow(title, year, seasons), dryRun: false, ct)
                .ConfigureAwait(false);
            outcome = answer.Outcome;
            status = answer.Status;
            siteText = answer.Response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Ein Abbruch nach dem Absenden ist KEIN Beweis, dass nichts angelegt wurde. Deshalb wird
            // auch hier nachgesehen; erst das Ergebnis der Suche entscheidet.
            outcome = TvstAddShowOutcome.Unclear;
            siteText = ex.Message;
            Log("[tvsubtitles] {Media}: Anlegen von \"{Title}\" ohne verwertbare Antwort ({Error}) — es wird nachgesehen.",
                mediaHash ?? "?", title, ex.Message);
        }

        // Die Nummer wird NICHT angenommen, sondern nachgesehen — die Antwort trägt keine Id. Erst
        // die Suche (und als Rückfall die Liste) sagt, ob und als was die Serie jetzt dasteht.
        var found = await FindShowIdAsync(title, ct).ConfigureAwait(false);
        var settled = SettleStatus(outcome, found);
        if (found is > 0)
        {
            var wie = Persist(registry, mediaHash, mediaPath, found.Value);
            if (settled == TvstShowStatus.Created)
            {
                Log("[tvsubtitles] {Media}: \"{Title}\" ({Year}) als Serie {Id} angelegt und gespeichert — {Note}",
                    mediaHash ?? "?", title, year, found.Value, MediaNumberNote.Of(wie));
            }
            else
            {
                // Die Seite hat die Anlage nicht bestätigt, die Serie steht aber da: dann war sie
                // schon vorhanden (Doppeleintrag vermieden) — gespeichert wird die gefundene Nummer.
                Log("[tvsubtitles] {Media}: \"{Title}\" steht bereits als Serie {Id} auf der Seite (die Antwort auf die Anlage lautete \"{Outcome}\") — Nummer gespeichert, nichts doppelt angelegt — {Note}",
                    mediaHash ?? "?", title, found.Value, outcome, MediaNumberNote.Of(wie));
            }

            return new TvstShowResolution(found.Value, settled);
        }

        if (settled == TvstShowStatus.CreatedIdUnknown)
        {
            Log("[tvsubtitles] {Media}: \"{Title}\" wurde angelegt, aber die Nummer war nicht lesbar (Suche und Liste ohne Treffer) — es wird nichts gespeichert.",
                mediaHash ?? "?", title);
        }
        else if (settled == TvstShowStatus.Refused)
        {
            Log("[tvsubtitles] {Media}: \"{Title}\" wurde von der Seite abgelehnt (HTTP {Status}): {Text} — nichts wird gespeichert, tvst wird übersprungen.",
                mediaHash ?? "?", title, status?.ToString(CultureInfo.InvariantCulture) ?? "?", siteText ?? string.Empty);
        }
        else
        {
            Log("[tvsubtitles] {Media}: \"{Title}\" — die Seite hat die Anlage nicht bestätigt und die Serie steht nicht in der Liste; nichts wird gespeichert, tvst wird übersprungen.",
                mediaHash ?? "?", title);
        }

        return new TvstShowResolution(null, settled);
    }

    /// <summary>
    /// F-M516: was aus Antwort und Nachschau folgt. Die Regel ist absichtlich klein und prüfbar:
    /// <list type="bullet">
    /// <item><description>Serie gefunden und die Seite hat bestätigt → <c>created</c>.</description></item>
    /// <item><description>Serie gefunden, aber ohne Bestätigung → <c>existed</c> (sie stand schon da).</description></item>
    /// <item><description>Nicht gefunden, aber bestätigt → <c>created-id-unknown</c> (ehrlich: angelegt, Nummer nicht lesbar).</description></item>
    /// <item><description>Nicht gefunden und abgelehnt → <c>refused</c>.</description></item>
    /// <item><description>Alles andere → <c>unclear</c>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="outcome">Der Ausgang der Anlage (aus <see cref="TvstAddShowOutcome"/>).</param>
    /// <param name="showId">Die danach gefundene Nummer, oder null.</param>
    /// <returns>Einer der Werte aus <see cref="TvstShowStatus"/>.</returns>
    public static string SettleStatus(string outcome, int? showId)
    {
        if (showId is > 0)
        {
            return outcome == TvstAddShowOutcome.Created ? TvstShowStatus.Created : TvstShowStatus.Existed;
        }

        return outcome switch
        {
            TvstAddShowOutcome.Created => TvstShowStatus.CreatedIdUnknown,
            TvstAddShowOutcome.Refused => TvstShowStatus.Refused,
            _ => TvstShowStatus.Unclear,
        };
    }

    /// <summary>
    /// F-M516: die Nummer nach der Anlage — erst über die Seitensuche (ein kleiner Abruf, gemessen
    /// liefert sie die neue Serie sofort), dann über die vollständige Serienliste. Beides wird eng
    /// verglichen (<see cref="TvSubtitlesClient.ResolveShow"/>); die Suche ist weich, ihr Ergebnis
    /// allein wäre kein Beweis.
    /// </summary>
    /// <param name="title">Der Name, den die Anlage trug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die Nummer, oder null wenn keine Antwort sie zeigt.</returns>
    private async Task<int?> FindShowIdAsync(string title, CancellationToken ct)
    {
        try
        {
            var hits = await _client.SearchShowsAsync(title, ct).ConfigureAwait(false);
            if (hits.Count > 0)
            {
                var id = TvSubtitlesClient.ResolveShow(hits, title);
                if (id is > 0)
                {
                    return id;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[tvsubtitles] show search not readable: {Error}", ex.Message);
        }

        var shows = await ShowsOrNullAsync(ct).ConfigureAwait(false);
        return shows is null ? null : TvSubtitlesClient.ResolveShow(shows, title);
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
    /// F-M501/F-M517: schreibt die Nummer ins Medium.
    /// <para>
    /// F-M517: ohne Medien-Hash wird dieselbe Zeile über ihren PFAD gefunden
    /// (<see cref="ContentHashRegistry.SetTvstShowId"/>) — dieselbe Spalte <c>TvstShowId</c>, nur eine
    /// andere Adresse. Der Serienfall litt unter demselben Loch wie die Filmnummer: die Zeile existierte,
    /// der Hash fehlte, und der alte Weg kehrte stumm zurück.
    /// </para>
    /// </summary>
    /// <param name="registry">Der Store.</param>
    /// <param name="mediaHash">Medium; null heißt „kein Medium bekannt" — dann führt der Pfad.</param>
    /// <param name="mediaPath">F-M517: der Pfad der Mediendatei.</param>
    /// <param name="showId">Die gesehene Nummer.</param>
    /// <returns>Wie geschrieben wurde.</returns>
    private static MediaNumberWrite Persist(
        ContentHashRegistry registry, string? mediaHash, string? mediaPath, int showId)
        => registry.SetTvstShowId(mediaHash, mediaPath, showId);

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

    /// <summary>
    /// F-M516: die Seite hat die Anlage angelegt bestätigt, die Nummer war aber nirgends lesbar — dann
    /// wird NICHTS gespeichert und das ehrlich gesagt.
    /// </summary>
    public const string CreatedIdUnknown = "created-id-unknown";

    /// <summary>
    /// F-M516: die Seite hat die Anlage nicht bestätigt, die Serie stand aber danach in der Liste — sie
    /// war also schon da. Gespeichert wird die gefundene Nummer, angelegt wurde nichts.
    /// </summary>
    public const string Existed = "existed";

    /// <summary>F-M516: die Seite hat die Anlage mit einem Fehlerstatus abgelehnt.</summary>
    public const string Refused = "refused";

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
