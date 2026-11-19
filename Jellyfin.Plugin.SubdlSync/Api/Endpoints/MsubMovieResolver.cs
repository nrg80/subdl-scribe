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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Registry;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M507 (Etappe 2a): die Nummer des Films bei moviesubtitles.org — ermittelt, gespeichert,
/// wiederverwendet. Das Muster ist das von <see cref="TvstShowResolver"/>, weil die Frage dieselbe ist:
/// welche Nummer hat DIESES Medium bei der Gegenseite.
/// <para>
/// DIE REIHENFOLGE IST FEST UND KURZ:
/// <list type="number">
/// <item><description>
/// Steht <see cref="Data.MediaEntity.MsubMovieId"/> am Medium, wird SIE benutzt — kein Netzzugriff zur
/// Suche, kein Vergleich. Die Nummer wurde bewiesen, als sie geschrieben wurde.
/// </description></item>
/// <item><description>
/// Sonst wird der TMDb-Titel mit dem TMDb-Jahr in der Suche der Seite gesucht und eng verglichen
/// (<see cref="MovieSubtitlesClient.FindMovieAsync"/> → <see cref="MovieSubtitlesClient.ResolveMovie"/>:
/// Titel UND Jahr, bei Mehrdeutigkeit nichts). Ein Treffer wird ins Medium geschrieben und benutzt.
/// </description></item>
/// <item><description>
/// Kein Jahr, kein Titel oder kein eindeutiger Treffer: EINE Logzeile, kein Wurf — die Quelle fällt für
/// dieses Medium aus, die anderen laufen weiter. Im DOWNLOAD-Pfad wird nie angelegt; im UPLOAD-Pfad
/// darf angelegt werden (<see cref="ResolveOrCreateForMediaAsync"/>, F-M513), und auch dort nur nach
/// dem Doppeleintrag-Schutz.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// F-M513 — DIE ANLAGE GEHT NUR UEBER DIE ERWEITERTE FASSUNG, und nur der Upload-Pfad ruft sie: sie
/// sucht EINMAL (<see cref="MovieSubtitlesClient.CheckForAddAsync"/> beantwortet Auflösung UND
/// Doppeleintrag-Schutz in einer Runde) und legt nur an, wenn der Titel wirklich nicht da ist. Sie ist
/// die eine Stelle, an der die neue Nummer in den Store geht (<see cref="Data.MediaEntity.MsubMovieId"/>)
/// — dieselbe Spalte, dieselbe Methode wie bei der Auflösung.
/// </para>
/// </summary>
public sealed class MsubMovieResolver
{
    private readonly MovieSubtitlesClient _client;
    private readonly ILogger? _logger;

    /// <summary>
    /// F-M507: die Nummern, die DIESER Lauf schon ermittelt hat. Der Aufruf kommt je Sprache, und ein
    /// Film mit vier fehlenden Sprachen darf nicht viermal bei der Seite suchen — im Trockenlauf schon
    /// gar nicht, denn dort wird nichts gespeichert und die zweite Sprache fände den Store leer.
    /// Der Schlüssel ist das Medium (media hash), nicht der Titel: zwei Filme desselben Titels bleiben
    /// zwei Medien.
    /// </summary>
    private readonly Dictionary<string, MsubMovieResolution> _resolvedThisRun = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="MsubMovieResolver"/> class.
    /// </summary>
    /// <param name="client">Der moviesubtitles.org-Client (Suche und Filmseite).</param>
    /// <param name="logger">Optionaler Logger — er nennt den Grund, wenn nichts geschrieben wurde.</param>
    public MsubMovieResolver(MovieSubtitlesClient client, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    /// <summary>F-M507: beginnt einen Lauf — die Merkliste gilt je Lauf, nicht je Prozess.</summary>
    public void BeginRun() => _resolvedThisRun.Clear();

    /// <summary>
    /// F-M507: die Filmnummer für dieses Medium — gespeichert, sonst gesucht (TMDb-Titel und TMDb-Jahr).
    /// <para>
    /// F-M513: DIESE FASSUNG LEGT NIE AN. Sie ist der Weg des DOWNLOAD-Pfades und der einzige, den ein
    /// Aufrufer nehmen darf, der nichts schreiben will.
    /// </para>
    /// </summary>
    /// <param name="registry">Der Store, aus dem die gespeicherte Nummer kommt und in den die neue geht.</param>
    /// <param name="mediaHash">Medium (OSHash); null heißt „kein Medium bekannt" — dann führt der Pfad.</param>
    /// <param name="movie">Der TMDb-Datensatz des Films; sein Titel und sein Jahr führen.</param>
    /// <param name="dryRun">True: nichts schreiben (der Trockenlauf hinterlässt keine Spur, F-M22).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="mediaPath">F-M517: der Pfad der Mediendatei — die zweite Adresse der Medienzeile,
    /// wenn kein Hash bekannt ist.</param>
    /// <returns>Das Ergebnis; <see cref="MsubMovieResolution.Resolved"/> false heißt „Quelle überspringen".</returns>
    public Task<MsubMovieResolution> ResolveForMediaAsync(
        ContentHashRegistry registry,
        string? mediaHash,
        TmdbMovieInfo movie,
        bool dryRun,
        CancellationToken ct,
        string? mediaPath = null)
        => ResolveCoreAsync(registry, mediaHash, movie, dryRun, allowCreate: false, ct, mediaPath);

    /// <summary>
    /// F-M513: dieselbe Auflösung — und, wenn der Titel auf der Seite <b>wirklich</b> fehlt, das Anlegen.
    /// <para>
    /// NUR DER UPLOAD-PFAD DARF SIE RUFFEN. Sie ist die einzige Stelle, an der ein Film entsteht: eine
    /// Suchrunde (Auflösung + Doppeleintrag-Schutz, <see cref="MovieSubtitlesClient.CheckForAddAsync"/>),
    /// dann — und nur bei leerem, lesbarem Ergebnis — <c>POST add-movie.php</c>, dann die Nachsuche nach
    /// der neuen Nummer. Jede neue Nummer geht sofort in <c>MsubMovieId</c>.
    /// </para>
    /// <para>
    /// Ein Trockenlauf baut dieselbe Nutzlast und meldet ihre Groesse
    /// (<see cref="MsubMovieResolution.AddMovieBytes"/>), sendet aber nichts und schreibt nichts.
    /// </para>
    /// </summary>
    /// <param name="registry">Der Store.</param>
    /// <param name="mediaHash">Medium (OSHash); null heißt „kein Medium bekannt" — dann führt der Pfad.</param>
    /// <param name="movie">Der TMDb-Datensatz; Titel und Jahr führen, das Jahr ist Pflicht.</param>
    /// <param name="dryRun">True: nichts anlegen, nichts schreiben — aber dieselbe Nutzlast bauen.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="mediaPath">F-M517: der Pfad der Mediendatei — die zweite Adresse der Medienzeile.</param>
    /// <returns>Das Ergebnis; <see cref="MsubMovieStatus.Created"/> heißt „neu angelegt und gespeichert".</returns>
    public Task<MsubMovieResolution> ResolveOrCreateForMediaAsync(
        ContentHashRegistry registry,
        string? mediaHash,
        TmdbMovieInfo movie,
        bool dryRun,
        CancellationToken ct,
        string? mediaPath = null)
        => ResolveCoreAsync(registry, mediaHash, movie, dryRun, allowCreate: true, ct, mediaPath);

    /// <summary>
    /// F-M507/F-M513: die Leiter in einer Fassung — gespeichert, gemerkt, gesucht, und (nur mit
    /// <paramref name="allowCreate"/>) angelegt.
    /// </summary>
    /// <param name="registry">Der Store.</param>
    /// <param name="mediaHash">Medium (OSHash).</param>
    /// <param name="movie">Der TMDb-Datensatz.</param>
    /// <param name="dryRun">True: nichts schreiben, nichts anlegen.</param>
    /// <param name="allowCreate">True nur im Upload-Pfad: ein fehlender Film darf angelegt werden.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="mediaPath">F-M517: der Pfad der Mediendatei — die zweite Adresse der Medienzeile.</param>
    /// <returns>Das Ergebnis der Leiter.</returns>
    private async Task<MsubMovieResolution> ResolveCoreAsync(
        ContentHashRegistry registry,
        string? mediaHash,
        TmdbMovieInfo movie,
        bool dryRun,
        bool allowCreate,
        CancellationToken ct,
        string? mediaPath = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(movie);

        // (a) Die gespeicherte Nummer führt. Kein Netzzugriff zur Suche: sie wurde geprüft, als sie
        // geschrieben wurde, und eine erneute Suche könnte sie nur durch eine schlechtere Antwort ersetzen.
        // F-M517: ohne Medien-Hash wird dieselbe Zeile über ihren PFAD gelesen — sonst wäre eine über
        // den Pfad gespeicherte Nummer zwar da, aber für jeden folgenden Lauf unsichtbar.
        var cachedKey = !string.IsNullOrEmpty(mediaHash)
            ? mediaHash!
            : !string.IsNullOrWhiteSpace(mediaPath) ? "path:" + mediaPath : "title:" + movie.Name;
        var stored = !string.IsNullOrEmpty(mediaHash)
            ? registry.GetMedia(mediaHash!)?.MsubMovieId
            : registry.MsubMovieIdAtPath(mediaPath);
        if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var known) && known > 0)
        {
            return new MsubMovieResolution(known, MsubMovieStatus.Stored);
        }

        // (a2) Was DIESER Lauf schon ermittelt hat, wird nicht ein zweites Mal gesucht — der Store ist
        // dann leer, weil der Trockenlauf nichts schreibt (F-M22) oder weil kein Medium bekannt ist. Ein
        // Fehlschlag steht ebenso in der Merkliste: eine Seite, die den Film nicht führt, führt ihn
        // eine Sprache später auch nicht.
        lock (_resolvedThisRun)
        {
            if (_resolvedThisRun.TryGetValue(cachedKey, out var schonGefragt))
            {
                return schonGefragt;
            }
        }

        var title = (movie.Name ?? string.Empty).Trim();
        if (title.Length < 2)
        {
            Log("[moviesubtitles] {Media}: der TMDb-Datensatz nennt keinen brauchbaren Titel — die Quelle wird übersprungen.",
                mediaHash ?? "?");
            return new MsubMovieResolution(null, MsubMovieStatus.NoTitle);
        }

        if (movie.ReleaseYear is not > 0)
        {
            // Ohne Jahr ist der Namensvergleich der Seite nicht eng genug (`filter=dune` antwortet fünf
            // Filme), und ein geratener Treffer wäre eine Nummer, die im Store steht und einen anderen
            // Film meint.
            Log("[moviesubtitles] {Media}: TMDb nennt kein Jahr für '{Title}' — die Quelle wird übersprungen.",
                mediaHash ?? "?", title);
            return new MsubMovieResolution(null, MsubMovieStatus.NoYear);
        }

        if (!allowCreate)
        {
            var movieId = await _client.FindMovieAsync(title, movie.ReleaseYear, ct).ConfigureAwait(false);
            if (movieId is not > 0)
            {
                Log("[moviesubtitles] {Media}: '{Title}' ({Year}) steht nicht (oder nicht eindeutig) in der Suche der Seite — die Quelle wird übersprungen.",
                    mediaHash ?? "?", title, movie.ReleaseYear.Value);
                return Remember(cachedKey, new MsubMovieResolution(null, MsubMovieStatus.NotFound));
            }

            if (dryRun)
            {
                // F-M22: der Trockenlauf hinterlässt keine Spur — er benutzt die Nummer, schreibt sie aber nicht.
                return Remember(cachedKey, new MsubMovieResolution(movieId.Value, MsubMovieStatus.WouldStore));
            }

            var wie = Persist(registry, mediaHash, mediaPath, movieId.Value);
            Log("[moviesubtitles] {Media}: '{Title}' ({Year}) ist Film {Id} — {Note}",
                mediaHash ?? "?", title, movie.ReleaseYear.Value, movieId.Value,
                Note(wie));
            return Remember(cachedKey, new MsubMovieResolution(movieId.Value, MsubMovieStatus.Found));
        }

        // F-M513: EINE Suchrunde, die Auflösung UND Doppeleintrag-Schutz beantwortet. Gefragt werden die
        // Schreibweisen der Seite (Titel, Titel ohne Artikel, invertierte Form), verglichen wird eng über
        // Titel UND Jahr — dieselbe Runde entscheidet, ob es den Film gibt und ob er angelegt werden darf.
        var check = await _client.CheckForAddAsync(title, movie.ReleaseYear.Value, ct).ConfigureAwait(false);
        if (check.Status == MovieSubtitlesAddStatus.Take)
        {
            // Der Titel steht mit DIESEM Jahr da: diese Nummer nehmen, nichts anlegen.
            if (dryRun)
            {
                return Remember(cachedKey, new MsubMovieResolution(check.MovieId, MsubMovieStatus.WouldStore));
            }

            var wie = Persist(registry, mediaHash, mediaPath, check.MovieId!.Value);
            Log("[moviesubtitles] {Media}: '{Title}' ({Year}) ist Film {Id} — {Note}",
                mediaHash ?? "?", title, movie.ReleaseYear.Value, check.MovieId!.Value,
                Note(wie));
            return Remember(cachedKey, new MsubMovieResolution(check.MovieId, MsubMovieStatus.Found));
        }

        if (check.Status == MovieSubtitlesAddStatus.Refuse)
        {
            // Der Titel steht da, nur nicht mit diesem Jahr (oder mit unbekanntem Jahr, oder mehrfach).
            // Dann wird NICHTS angelegt: genau das wäre das Doppel, das der Nutzer nicht will.
            Log("[moviesubtitles] '{Title}' ({Year}): die Seite führt den Titel schon — gefundenes Jahr {FoundYear}, {Hits} Treffer; nichts angelegt, kein Upload (F-M513)",
                title, movie.ReleaseYear.Value, check.FoundYear, check.Hits);
            return Remember(
                cachedKey,
                new MsubMovieResolution(null, MsubMovieStatus.ExistsOtherYear, FoundYear: check.FoundYear));
        }

        if (check.Status == MovieSubtitlesAddStatus.Unclear)
        {
            // Keine lesbare Antwort: „gibt es nicht" ist damit NICHT belegt — also nicht anlegen.
            Log("[moviesubtitles] {Media}: die Suche der Seite antwortete für '{Title}' ({Year}) nicht lesbar — nichts angelegt, die Quelle wird übersprungen (F-M513).",
                mediaHash ?? "?", title, movie.ReleaseYear.Value);
            return Remember(cachedKey, new MsubMovieResolution(null, MsubMovieStatus.Unclear));
        }

        // ---- kein Treffer, und die Suche war lesbar: anlegen -------------------------------------
        MovieSubtitlesAddMovie nutzlast;
        try
        {
            nutzlast = MovieSubtitlesClient.BuildAddMovie(title, movie.ReleaseYear.Value);
        }
        catch (ArgumentException ex)
        {
            // Das Formular nimmt diese Angaben nicht (leerer Name, unplausibles Jahr) — dann entsteht
            // keine Anfrage, und hier wird auch nichts behauptet.
            Log("[moviesubtitles] '{Title}' ({Year}): das Anlege-Formular nimmt diese Angaben nicht ({Reason}) — nichts angelegt (F-M513).",
                title, movie.ReleaseYear.Value, ex.Message);
            return Remember(cachedKey, new MsubMovieResolution(null, MsubMovieStatus.CreateFailed, Answer: ex.Message));
        }

        MovieSubtitlesAddMovieResult angelegt;
        try
        {
            angelegt = await _client.AddMovieAsync(nutzlast, dryRun, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("[moviesubtitles] '{Title}' ({Year}): das Anlegen scheiterte ({Error}) — nichts gespeichert, die Quelle wird übersprungen (F-M513).",
                title, movie.ReleaseYear.Value, ex.Message);
            return Remember(cachedKey, new MsubMovieResolution(null, MsubMovieStatus.CreateFailed, Answer: ex.Message));
        }

        if (angelegt.Outcome == MovieSubtitlesAddOutcome.DryRun)
        {
            // Trockenlauf: derselbe Rumpf, kein POST. Die Groesse ist die des Ernstfalls, weil die
            // Nutzlast VOR der Weiche gebaut wird.
            Log("[moviesubtitles] DRY-RUN: '{Title}' ({Year}) würde angelegt — {Bytes} Bytes Formular-Rumpf, nichts gesendet (F-M513).",
                title, movie.ReleaseYear.Value, angelegt.BodyBytes);
            return Remember(
                cachedKey,
                new MsubMovieResolution(
                    null, MsubMovieStatus.WouldCreate, AddMovieBytes: angelegt.BodyBytes));
        }

        if (angelegt.Outcome == MovieSubtitlesAddOutcome.Exists)
        {
            // F-M515: die Seite hat die Anlage verweigert („This Movie already exist“) — und die Nachsuche
            // nennt den Film. Dann ist die Nummer die Antwort: sie wird benutzt und gespeichert, aber es
            // wird KEIN zweiter Anlage-Versuch unternommen.
            var wie = Persist(registry, mediaHash, mediaPath, angelegt.MovieId!.Value);
            Log("[moviesubtitles] '{Title}' ({Year}): die Seite meldet „{Answer}“ — Id {Id} aus der Nachsuche übernommen, nichts angelegt (F-M515) — {Note}",
                title, movie.ReleaseYear.Value, MovieSubtitlesClient.AddMovieExistsMarker,
                angelegt.MovieId!.Value, Note(wie));
            return Remember(cachedKey, new MsubMovieResolution(angelegt.MovieId, MsubMovieStatus.ExistsFound));
        }

        if (angelegt.Outcome == MovieSubtitlesAddOutcome.ExistsUnknownId)
        {
            // Live gemessen 16.11.2026: die Seite sagt „existiert schon“ und führt den Film trotzdem
            // nirgends — Suche, Buchstabenliste und add.php?filter= bleiben leer. Ohne tvid kein Upload,
            // und keine Nummer wird erfunden. Ein zweiter POST fällt nicht.
            Log("[moviesubtitles] '{Title}' ({Year}): die Seite meldet „{Answer}“, die Suche nennt aber keine Id — nichts gespeichert, kein Upload (F-M515)",
                title, movie.ReleaseYear.Value, MovieSubtitlesClient.AddMovieExistsMarker);
            return Remember(
                cachedKey,
                new MsubMovieResolution(null, MsubMovieStatus.ExistsWithoutId, Answer: angelegt.Response));
        }

        if (angelegt.Outcome == MovieSubtitlesAddOutcome.Rejected)
        {
            // F-M515: 2xx ohne den belegten Marker (live: das unveränderte Formular). Das ist KEINE
            // Anlage — der Textauszug ist der Grund, gesucht wird nicht, gespeichert wird nichts.
            Log("[moviesubtitles] '{Title}' ({Year}): die Seite hat nicht angelegt ({Reason}) — nichts gespeichert, kein Upload (F-M515).",
                title, movie.ReleaseYear.Value, angelegt.Reason ?? "?");
            return Remember(
                cachedKey,
                new MsubMovieResolution(
                    null, MsubMovieStatus.CreateFailed, Answer: angelegt.Response ?? angelegt.Reason));
        }

        if (!angelegt.Ok)
        {
            // Kein 2xx: der Status ist der Grund, der Text der Seite geht mit. Nichts gespeichert.
            Log("[moviesubtitles] '{Title}' ({Year}): die Seite hat das Anlegen abgelehnt ({Reason}) — nichts gespeichert, kein Upload (F-M513).",
                title, movie.ReleaseYear.Value, angelegt.Reason ?? "?");
            return Remember(
                cachedKey,
                new MsubMovieResolution(
                    null, MsubMovieStatus.CreateFailed, Answer: angelegt.Response ?? angelegt.Reason));
        }

        if (angelegt.MovieId is not > 0)
        {
            // ANGELEGT, ID NICHT LESBAR: der POST wurde angenommen, aber die Nachsuche nennt den Film
            // nicht. Ohne tvid gibt es keinen Upload — und eine Nummer wird nicht erfunden.
            Log("[moviesubtitles] '{Title}' ({Year}) wurde angelegt, aber die Nachsuche nannte keine Id — nichts gespeichert, kein Upload (F-M513).",
                title, movie.ReleaseYear.Value);
            return Remember(
                cachedKey,
                new MsubMovieResolution(null, MsubMovieStatus.CreatedWithoutId, Answer: angelegt.Response));
        }

        // Die Nummer steht fest: SOFORT in den Store, an derselben Spalte wie die Auflösung —
        // und seit F-M517 auch dann, wenn nur der PFAD die Zeile adressiert.
        var geschrieben = Persist(registry, mediaHash, mediaPath, angelegt.MovieId.Value);
        Log("[moviesubtitles] '{Title}' ({Year}) neu angelegt — Id {Id} — {Note}",
            title, movie.ReleaseYear.Value, angelegt.MovieId.Value, Note(geschrieben));
        return Remember(cachedKey, new MsubMovieResolution(angelegt.MovieId, MsubMovieStatus.Created));
    }

    /// <summary>
    /// F-M507: merkt sich das Ergebnis dieses Laufs — auch ein Fehlschlag. Sonst fragte jede weitere
    /// fehlende Sprache dieselbe Frage noch einmal, und die Seite ist ein Nebenamt, kein Cache.
    /// </summary>
    /// <param name="key">Medium bzw. Titel.</param>
    /// <param name="resolution">Das Ergebnis.</param>
    /// <returns>Das Ergebnis, unverändert.</returns>
    private MsubMovieResolution Remember(string key, MsubMovieResolution resolution)
    {
        lock (_resolvedThisRun)
        {
            _resolvedThisRun[key] = resolution;
        }

        return resolution;
    }

    /// <summary>
    /// F-M507/F-M517: schreibt die Nummer ins Medium — nur dorthin, und nur wenn die Seite sie genannt
    /// hat.
    /// <para>
    /// F-M517: DER SCHREIBWEG IST DERSELBE, DER FUND EIN ANDERER. Ohne Medien-Hash sucht
    /// <see cref="ContentHashRegistry.SetMsubMovieId"/> die Zeile über ihren PFAD
    /// (<c>MediaEntity.Path</c>) und schreibt dieselbe Spalte <c>MsubMovieId</c>. Vorher kehrte diese
    /// Methode bei fehlendem Hash stumm zurück, obwohl die Zeile existierte — gemessen am 16.11.2026
    /// 04:13 auf jf-test: `[moviesubtitles] ?: 'Hotel Transylvania 2' (2015) ist Film 13392 — nicht
    /// gespeichert: kein Medien-Hash bekannt`. Der Upload lief durch, die Nummer war weg.
    /// </para>
    /// </summary>
    /// <param name="registry">Der Store.</param>
    /// <param name="mediaHash">Medium; null heißt „kein Medium bekannt" — dann führt der Pfad.</param>
    /// <param name="mediaPath">F-M517: der Pfad der Mediendatei (zweite Adresse der Zeile).</param>
    /// <param name="movieId">Die gesehene Nummer.</param>
    /// <returns>Wie geschrieben wurde; <see cref="MediaNumberWrite.None"/> heißt „nicht gespeichert".</returns>
    private static MediaNumberWrite Persist(
        ContentHashRegistry registry, string? mediaHash, string? mediaPath, int movieId)
        => registry.SetMsubMovieId(mediaHash, mediaPath, movieId);

    /// <summary>
    /// F-M514/F-M517: was die Logzeile über das Speichern sagen darf — und was nicht.
    /// <para>
    /// Gemessen 16.11.2026 (jf-test 02:51): `[moviesubtitles] ?: 'Hotel Transylvania 2' (2015) ist
    /// Film 13392 — gespeichert.` Das `?` ist der Platzhalter für „kein Medien-Hash bekannt"
    /// (<c>Registry.GetMediaHash</c> kennt die eben geschriebene Datei noch nicht, es gibt keine
    /// OSHash-Zeile oder die Datei ist nicht lesbar) — und in diesem Fall schrieb der alte Weg
    /// nichts. Die Zeile behauptete trotzdem „gespeichert".
    /// </para>
    /// <para>
    /// F-M517: der Vermerk kommt jetzt aus dem tatsächlichen Schreib-Ausgang
    /// (<see cref="MediaNumberNote"/>): über den Medien-Hash, über den Pfad — oder ehrlich „nicht
    /// gespeichert" mit dem Grund. Ein Text, den beide Adressen (Film und Serie) benutzen.
    /// </para>
    /// <param name="write">Wie geschrieben wurde.</param>
    /// <returns>Der Vermerk für die Logzeile.</returns>
    private static string Note(MediaNumberWrite write) => MediaNumberNote.Of(write);

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
/// F-M507: die Ausgänge der Filmaulösung — je einer eine Entscheidung des Aufrufers.
/// </summary>
public static class MsubMovieStatus
{
    /// <summary>Die Nummer stand am Medium.</summary>
    public const string Stored = "stored";

    /// <summary>Der TMDb-Titel stand eindeutig in der Suche; die Nummer wurde gespeichert.</summary>
    public const string Found = "found";

    /// <summary>Der Trockenlauf hätte gespeichert — er hat es nicht getan.</summary>
    public const string WouldStore = "would-store";

    /// <summary>Kein eindeutiger Treffer in der Suche der Seite.</summary>
    public const string NotFound = "not-found";

    /// <summary>
    /// F-M513: der Titel steht auf der Seite, aber nicht mit dem gesuchten Jahr (oder mit unbekanntem
    /// Jahr, oder in mehr als einer Zeile). Es wurde NICHTS angelegt — das wäre das Doppeleintrag-Risiko.
    /// </summary>
    public const string ExistsOtherYear = "exists-other-year";

    /// <summary>F-M513: der Trockenlauf hätte angelegt — gebaut wurde die Nutzlast, gesendet nichts.</summary>
    public const string WouldCreate = "would-create";

    /// <summary>F-M513: neu angelegt und die Nummer steht im Store.</summary>
    public const string Created = "created";

    /// <summary>F-M513: angelegt (HTTP 2xx), aber die Nachsuche nannte keine Nummer — kein tvid, kein Upload.</summary>
    public const string CreatedWithoutId = "created-id-not-readable";

    /// <summary>
    /// F-M513: das Anlegen wurde abgelehnt oder scheiterte — nichts gespeichert, kein Upload.
    /// </summary>
    public const string CreateFailed = "create-failed";

    /// <summary>
    /// F-M515: die Seite meldete „This Movie already exist“ (nichts angelegt), und die Nachsuche nannte
    /// die Nummer — sie wird benutzt und gespeichert.
    /// </summary>
    public const string ExistsFound = "exists-took-id";

    /// <summary>
    /// F-M515: die Seite meldete „This Movie already exist“, die Suche nennt aber keine Nummer — live
    /// gemessen der Regelfall bei einem eben angelegten Film. Kein tvid, kein Upload.
    /// </summary>
    public const string ExistsWithoutId = "exists-id-not-readable";

    /// <summary>F-M513: die Suche antwortete nicht lesbar — „gibt es nicht" ist nicht belegt, also nichts angelegt.</summary>
    public const string Unclear = "unclear";

    /// <summary>Der TMDb-Datensatz trug keinen brauchbaren Titel.</summary>
    public const string NoTitle = "no-title";

    /// <summary>TMDb nennt kein Jahr — ohne Jahr wird nicht geraten.</summary>
    public const string NoYear = "no-year";
}

/// <summary>
/// F-M507: was die Filmaulösung ergeben hat.
/// </summary>
/// <param name="MovieId">Die Nummer des Films, oder null.</param>
/// <param name="Status">Einer der Werte aus <see cref="MsubMovieStatus"/>.</param>
/// <param name="FoundYear">F-M513: das Jahr, unter dem der Titel schon steht (0 = die Seite nennt
/// keines) — nur bei <see cref="MsubMovieStatus.ExistsOtherYear"/> gesetzt.</param>
/// <param name="AddMovieBytes">F-M513: die Groesse der Anlege-Nutzlast, die im Trockenlauf gebaut wurde
/// (0, wenn nichts gebaut wurde) — sie ist die des Ernstfalls.</param>
/// <param name="Answer">F-M513: der Text der Seite, wenn einer vorliegt (Ablehnung beim Anlegen,
/// unlesbare Antwort) — durchgereicht, nicht gedeutet.</param>
public sealed record MsubMovieResolution(
    int? MovieId, string Status, int FoundYear = 0, int AddMovieBytes = 0, string? Answer = null)
{
    /// <summary>Gets a value indicating whether eine Nummer vorliegt, mit der der Abruf arbeiten kann.</summary>
    public bool Resolved => MovieId is > 0;
}
