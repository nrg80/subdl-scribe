using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Pipeline;

/// <summary>
/// F-M596: bereitet die Extraktion GENAU EINER Datei vor, waehrend die aktuelle noch sendet.
/// <para>
/// Operator-Anweisung 18.11.2026 („Das wäre aber sophisticated. Kriegen wir das sicher abgewickelt?" —
/// „Ja"): gemessen am 18.11.2026 zerfiel der Abstand zwischen zwei Uploads in 9,0 s Vorbereitung
/// (davon der eine ffmpeg-Durchlauf), 2,4 s Uebertragung und 0,0 s Warten am Slot. Der Takt von 7,2 s
/// bei 500/h wurde damit nie erreicht — die Rate war vorbereitungsgebunden. Der Vorlauf zieht den
/// EINZIGEN lesenden, seiteneffektfreien Schritt nach vorn: mehr nicht.
/// </para>
/// <para>
/// Was dadurch NICHT passiert: keine zweite Verbindung, keine zweite Quelle, kein vorgezogenes Urteil,
/// keine vorgezogene Zeile im Store, keine Aenderung der Reihenfolge. Die Extraktion liest eine Datei
/// und schreibt Textdateien in ein temporaeres Verzeichnis; alles, was zaehlt oder schreibt, bleibt im
/// Lauf an seinem Platz. Deshalb ist der Vorlauf auch dann harmlos, wenn er verworfen wird: die
/// Ergebnisse sind reproduzierbar, und ein Treffer wird nur benutzt, wenn Positionen UND Datei
/// uebereinstimmen.
/// </para>
/// <para>
/// Grenzen: hoechstens EIN Vorlauf gleichzeitig (der jeweils aeltere wird abgebrochen und beendet),
/// hoechstens EINE Datei im Voraus, und ein Abbruch des Laufs beendet den ffmpeg-Prozess wirklich
/// (F-M596 in <see cref="FfmpegTools"/>). Der Speicherbedarf ist der Text EINER Datei — gemessen an
/// einer 43-Spuren-Folge wenige Megabyte.
/// </para>
/// </summary>
public sealed class ExtractionLookahead : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task<(Dictionary<int, string?> Texts, int ExitCode)>? _task;
    private string? _key;
    private List<int> _positions = new();

    /// <summary>
    /// Startet die Extraktion fuer <paramref name="mediaPath"/>, sofern nicht schon genau diese Datei
    /// mit genau diesen Positionen vorbereitet wird. Der Aufruf kehrt sofort zurueck.
    /// </summary>
    /// <param name="mediaPath">Die Datei, die als naechste dran waere.</param>
    /// <param name="positions">Die zu extrahierenden Untertitel-Positionen (0:s:N).</param>
    /// <param name="ffmpegPath">Der ffmpeg-Binary.</param>
    /// <param name="logger">Protokoll.</param>
    /// <param name="config">Konfiguration (nur Lesen).</param>
    /// <param name="ct">Abbruch des Laufs.</param>
    public void Start(
        string mediaPath,
        IReadOnlyList<int> positions,
        string ffmpegPath,
        ILogger logger,
        PluginConfiguration config,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || positions.Count == 0 || ct.IsCancellationRequested)
        {
            return;
        }

        lock (_lock)
        {
            if (_key == mediaPath && _task != null && !_task.IsCompleted)
            {
                return;
            }

            AbbrechenUnterSchloss();

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _key = mediaPath;
            _positions = positions.ToList();
            _task = FfmpegTools.ExtractAllAsync(ffmpegPath, mediaPath, _positions, logger, config, cts.Token);
        }
    }

    /// <summary>
    /// Nimmt das vorgezogene Ergebnis, wenn es zu dieser Datei und zu ALLEN geforderten Positionen
    /// passt — sonst wird normal extrahiert. Ein Vorlauf, der nur einen Teil der Positionen deckt,
    /// wird verworfen: eine halbe Antwort waere schlimmer als eine frische Frage.
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
        Task<(Dictionary<int, string?> Texts, int ExitCode)>? passend = null;

        lock (_lock)
        {
            bool deckt = _task != null
                && _key == mediaPath
                && positions.All(p => _positions.Contains(p));

            if (deckt)
            {
                passend = _task;
            }
            else
            {
                AbbrechenUnterSchloss();
            }
        }

        if (passend != null)
        {
            try
            {
                var (texts, exit) = await passend.ConfigureAwait(false);
                return (texts, exit, true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Der Vorlauf wurde aus einem anderen Grund beendet (aelterer Vorlauf ersetzt) —
                // dann ist jetzt die normale Extraktion dran.
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_task, passend))
                    {
                        _task = null;
                        _key = null;
                        _positions = new List<int>();
                        _cts?.Dispose();
                        _cts = null;
                    }
                }
            }
        }

        var frisch = await FfmpegTools.ExtractAllAsync(ffmpegPath, mediaPath, positions, logger, config, ct)
            .ConfigureAwait(false);
        return (frisch.Texts, frisch.ExitCode, false);
    }

    /// <summary>Verwirft einen noch laufenden Vorlauf und beendet seinen ffmpeg-Prozess.</summary>
    public void Dispose()
    {
        Task<(Dictionary<int, string?> Texts, int ExitCode)>? offen;
        lock (_lock)
        {
            offen = _task;
            AbbrechenUnterSchloss();
            _task = null;
            _key = null;
            _positions = new List<int>();
        }

        if (offen != null)
        {
            try
            {
                // Kurz warten, damit der Abbruch greift und ffmpeg beendet ist, bevor der Lauf endet.
                offen.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Ein verworfener Vorläufer ist kein Fehler des Laufs.
            }
        }
    }

    /// <summary>Bricht den laufenden Vorlauf ab. Nur unter <see cref="_lock"/> aufrufen.</summary>
    private void AbbrechenUnterSchloss()
    {
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
        _task = null;
        _key = null;
        _positions = new List<int>();
    }
}
