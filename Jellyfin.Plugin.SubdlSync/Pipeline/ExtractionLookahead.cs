using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Pipeline;

/// <summary>
/// F-M596: haelt bis zu drei Dateien extrahiert bereit, waehrend die aktuelle noch sendet.
/// <para>
/// Operator-Anweisung 18.11.2026 („Das wäre aber sophisticated. Kriegen wir das sicher abgewickelt?" —
/// „Ja"), nach der ersten Messung nachgezogen („Unbedingt"). Messung 1 (26 Spuren, Einstellung 500/h):
/// Abstand Median 10,0 s = 7,6 s Vorbereitung, 0,2 s Warten, 2,3 s Uebertragung — und der Vorlauf wurde
/// nur in **2 von 26** Spuren benutzt. Grund: die Kette bricht bei uebersprungenen Dateien. Der Vorlauf
/// galt der naechsten Datei, die der Kopf-Ausstieg dann uebersprang, und die naechste, die wirklich
/// lief, fing kalt bei ~8 s an.
/// </para>
/// <para>
/// Deshalb jetzt eine kleine Pumpe: der Lauf meldet bis zu DREI kommende Kandidaten an
/// (<see cref="Plan"/>), die Pumpe extrahiert sie nacheinander — immer nur eine Datei gleichzeitig,
/// nie zwei ffmpeg-Prozesse — und legt das Ergebnis ab. Faellt eine Datei unterwegs weg (uebersprungen),
/// verschwindet ihr Ergebnis beim naechsten Plan wieder. Der Speicherbedarf ist der Text von hoechstens
/// drei Dateien (wenige MB je Datei).
/// </para>
/// <para>
/// Was dadurch NICHT passiert: keine zweite Verbindung, keine zweite Quelle, kein vorgezogenes Urteil,
/// keine vorgezogene Zeile im Store, keine Aenderung der Reihenfolge. Die Extraktion liest eine Datei
/// und schreibt Textdateien in ein temporaeres Verzeichnis; alles, was zaehlt oder schreibt, bleibt im
/// Lauf an seinem Platz. Ein verworfener Vorlauf ist deshalb folgenlos. Ein Abbruch des Laufs beendet
/// den ffmpeg-Prozess wirklich (F-M596 in <see cref="FfmpegTools"/>).
/// </para>
/// </summary>
public sealed class ExtractionLookahead : IDisposable
{
    /// <summary>Wie viele Dateien hoechstens im Voraus bereitgehalten werden.</summary>
    private const int MaxKandidaten = 3;

    private readonly object _lock = new();

    /// <summary>Fertige oder laufende Extraktionen, je Datei.</summary>
    private readonly Dictionary<string, Task<(Dictionary<int, string?> Texts, int ExitCode)>> _laeufe =
        new(StringComparer.Ordinal);

    /// <summary>Die angemeldeten Kandidaten in Reihenfolge.</summary>
    private readonly List<string> _plan = new();

    /// <summary>Die Positionen, die fuer eine Datei vorbereitet wurden.</summary>
    private readonly Dictionary<string, List<int>> _positionen = new(StringComparer.Ordinal);

    private Task? _pumpe;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Meldet die kommenden Kandidaten an (hoechstens <see cref="MaxKandidaten"/>, in Reihenfolge) und
    /// startet die Pumpe, falls sie nicht schon laeuft. Der Aufruf kehrt sofort zurueck.
    /// </summary>
    /// <param name="kandidaten">Datei und Positionen, naechste zuerst.</param>
    /// <param name="ffmpegPath">Der ffmpeg-Binary.</param>
    /// <param name="logger">Protokoll.</param>
    /// <param name="config">Konfiguration (nur Lesen).</param>
    /// <param name="ct">Abbruch des Laufs.</param>
    public void Plan(
        IEnumerable<(string Path, List<int> Positions)> kandidaten,
        string ffmpegPath,
        ILogger logger,
        PluginConfiguration config,
        CancellationToken ct)
    {
        var neu = kandidaten
            .Where(k => !string.IsNullOrWhiteSpace(k.Path) && k.Positions.Count > 0)
            .Take(MaxKandidaten)
            .ToList();

        lock (_lock)
        {
            _plan.Clear();
            foreach (var (pfad, positionen) in neu)
            {
                _plan.Add(pfad);
                _positionen[pfad] = positionen;
            }

            // Was nicht mehr im Plan steht, ist unterwegs weggefallen (uebersprungen): ein fertiges
            // Ergebnis wegwerfen, damit weder Speicher noch ein toter Lauf liegen bleibt.
            foreach (string alt in _laeufe.Keys.Where(p => !_plan.Contains(p)).ToList())
            {
                if (_laeufe[alt].IsCompleted)
                {
                    _laeufe.Remove(alt);
                    _positionen.Remove(alt);
                }
            }

            foreach (string alt in _positionen.Keys.Where(p => !_plan.Contains(p) && !_laeufe.ContainsKey(p)).ToList())
            {
                _positionen.Remove(alt);
            }

            if (_pumpe == null && _plan.Count > 0 && !ct.IsCancellationRequested)
            {
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _pumpe = Task.Run(() => PumpeAsync(ffmpegPath, logger, config, _cts.Token), CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Nimmt das vorgezogene Ergebnis, wenn diese Datei vorbereitet wurde und ALLE geforderten
    /// Positionen gedeckt sind — sonst wird normal extrahiert. Eine halbe Antwort waere schlimmer als
    /// eine frische Frage.
    /// </summary>
    /// <param name="mediaPath">Die Datei, die jetzt dran ist.</param>
    /// <param name="positions">Die jetzt gebrauchten Positionen.</param>
    /// <param name="ffmpegPath">Der ffmpeg-Binary.</param>
    /// <param name="logger">Protokoll.</param>
    /// <param name="config">Konfiguration (nur Lesen).</param>
    /// <param name="ct">Abbruch des Laufs.</param>
    /// <returns>Texte, Exit-Code und ob das Ergebnis aus dem Vorlauf kam.</returns>
    public async Task<(Dictionary<int, string?> Texts, int ExitCode, bool FromLookahead)> TakeOrExtractAsync(
        string mediaPath,
        IReadOnlyList<int> positions,
        string ffmpegPath,
        ILogger logger,
        PluginConfiguration config,
        CancellationToken ct)
    {
        Task<(Dictionary<int, string?> Texts, int ExitCode)>? vorbereitet = null;

        lock (_lock)
        {
            if (_laeufe.TryGetValue(mediaPath, out var lauf)
                && _positionen.TryGetValue(mediaPath, out var vorbereitetPositionen)
                && positions.All(p => vorbereitetPositionen.Contains(p)))
            {
                vorbereitet = lauf;
                _laeufe.Remove(mediaPath);
                _plan.Remove(mediaPath);
                _positionen.Remove(mediaPath);
            }
        }

        if (vorbereitet != null)
        {
            try
            {
                var (texts, exit) = await vorbereitet.ConfigureAwait(false);
                return (texts, exit, true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Der Vorlauf wurde beendet (Plan ersetzt) — dann eben frisch.
            }
            catch (Exception ex)
            {
                // Ein gescheiterter Vorlauf ist kein Urteil: frisch versuchen.
                logger.LogDebug("[Sub Scribe] look-ahead extraction failed for {File}: {Msg}", mediaPath, ex.Message);
            }
        }

        // F-M613: erst die Pumpe freigeben, dann selbst extrahieren — nie zwei ffmpeg gleichzeitig.
        Freigeben();
        var frisch = await FfmpegTools.ExtractAllAsync(ffmpegPath, mediaPath, positions, logger, config, ct)
            .ConfigureAwait(false);
        return (frisch.Texts, frisch.ExitCode, false);
    }

    /// <summary>
    /// F-M613: gibt die Pumpe frei, bevor der Lauf selbst extrahiert — es soll nie zwei ffmpeg
    /// gleichzeitig geben. Ein Plan, der danach noch gebraucht wird, meldet sich neu an und startet
    /// die Pumpe wieder.
    /// </summary>
    public void Freigeben()
    {
        Task? pumpe;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            pumpe = _pumpe;
            cts = _cts;
            _pumpe = null;
            _cts = null;
        }

        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch (Exception)
        {
            // Eine bereits entsorgte Quelle ist kein Fehler des Laufs.
        }

        if (pumpe != null)
        {
            try
            {
                pumpe.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // Ein verworfener Vorlauf ist kein Fehler des Laufs.
            }
        }
    }

    /// <summary>Beendet die Pumpe und den laufenden ffmpeg-Prozess.</summary>
    public void Dispose()
    {
        Task? pumpe;
        Task<(Dictionary<int, string?> Texts, int ExitCode)>[] offen;

        lock (_lock)
        {
            pumpe = _pumpe;
            offen = _laeufe.Values.ToArray();
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch (Exception)
            {
                // Cancel/Dispose auf einer bereits entsorgten Quelle ist kein Fehler des Laufs.
            }

            _cts = null;
            _pumpe = null;
            _plan.Clear();
            _laeufe.Clear();
            _positionen.Clear();
        }

        if (pumpe != null)
        {
            try
            {
                // Kurz warten, damit der Abbruch greift und ffmpeg beendet ist, bevor der Lauf endet.
                pumpe.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Ein verworfener Vorlauf ist kein Fehler des Laufs.
            }
        }

        foreach (var lauf in offen)
        {
            try
            {
                lauf.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Siehe oben.
            }
        }
    }

    /// <summary>
    /// F-M596: die Pumpe — arbeitet den Plan der Reihe nach ab, immer eine Datei gleichzeitig, und
    /// hoert auf, sobald nichts mehr offen ist. Sie laeuft nur, solange Kandidaten angemeldet sind.
    /// </summary>
    /// <param name="ffmpegPath">Der ffmpeg-Binary.</param>
    /// <param name="logger">Protokoll.</param>
    /// <param name="config">Konfiguration (nur Lesen).</param>
    /// <param name="ct">Abbruch des Laufs.</param>
    /// <returns>Ein Task, der endet, wenn der Plan abgearbeitet ist.</returns>
    private async Task PumpeAsync(string ffmpegPath, ILogger logger, PluginConfiguration config, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? naechste;
            List<int>? positionen;

            lock (_lock)
            {
                naechste = _plan.FirstOrDefault(p => !_laeufe.ContainsKey(p));
                if (naechste == null)
                {
                    _pumpe = null;
                    return;
                }

                positionen = _positionen.TryGetValue(naechste, out var vorhanden)
                    ? vorhanden
                    : new List<int>();
            }

            if (positionen.Count == 0)
            {
                lock (_lock)
                {
                    _plan.Remove(naechste);
                }

                continue;
            }

            var lauf = FfmpegTools.ExtractAllAsync(ffmpegPath, naechste, positionen, logger, config, ct);

            lock (_lock)
            {
                _laeufe[naechste] = lauf;
            }

            try
            {
                await lauf.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Ergebnis oder Fehlschlag steht im Task; der Nehmer entscheidet.
            }

            lock (_lock)
            {
                // Weggefallen, waehrend extrahiert wurde: Ergebnis nicht aufheben.
                if (!_plan.Contains(naechste))
                {
                    _laeufe.Remove(naechste);
                    _positionen.Remove(naechste);
                }
            }
        }

        lock (_lock)
        {
            _pumpe = null;
        }
    }
}
