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
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// The pacing rhythm shared by the upload AND the download pipeline (F-M20/F-M26).
/// <para>
/// It owns NO budget. An earlier build kept a per-run hourly call bucket here and let it
/// stop a run — a limit the plugin had invented itself and then obeyed. SubDL publishes
/// only DAILY counters, so the bucket stopped runs the server would have allowed; it is
/// removed (03.10.2026, user decision). What remains is the spacing between calls and the
/// plus/minus 30 % jitter on real transfers (anti-thundering-herd, F-M26).
/// </para>
/// <para>Nothing in this class can refuse a call. Thread-safe across both pipelines.</para>
/// </summary>
public sealed class GlobalRateLimiter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GlobalRateLimiter"/> class.
    /// </summary>
    /// <param name="transfersPerHour">Configured transfer rate (F-M20). It is the base for the
    /// interval between two real transfers (3600/rate); it is NOT a ceiling — nothing here
    /// counts calls or refuses one.</param>
    /// <param name="minCallPauseSec"> (user decision 15.09.2026): pause between two API calls
    /// in seconds (0.1–10, default 0.5). Overrides the derived 3600/rate interval when set;
    /// SubDL allows 600 req/min (0.1 s spacing).</param>
    public GlobalRateLimiter(int transfersPerHour, double minCallPauseSec = -1)
    {
        TransfersPerHour = Math.Clamp(transfersPerHour, 1, 2000);

        // (25.09.2026): the "derive from the transfer rate" sentinel is -1 and must survive.
        // The old expression clamped the sentinel too — Math.Clamp(-1, 0.1, 10) was 0.1, never
        // -1 — so the derived branch in InterCallPauseMs could not be reached and the
        // documented behaviour was unreachable code. Clamp only a real value.
        MinCallPauseSec = minCallPauseSec < 0 ? -1 : Math.Clamp(minCallPauseSec, 0.1, 10);
    }

    /// <summary>Gets the configured transfer rate (transfers per hour, F-M20).</summary>
    public int TransfersPerHour { get; }

    /// <summary>Gets the explicit pause between API calls (seconds); -1 = derive from the transfer rate.</summary>
    public double MinCallPauseSec { get; }

    /// <summary>
    /// F-M26a: pause between two bare API calls that carry no actual transfer (an empty
    /// search, a skip, a metadata lookup). Deterministic: the explicit
    /// <see cref="MinCallPauseSec"/> when configured, otherwise the interval derived from the
    /// transfer rate (3600/rate). This pause carries NO jitter — the jitter belongs to the
    /// transfer rhythm (<see cref="TransferPauseMs"/>).
    /// </summary>
    /// <returns>Delay in milliseconds.</returns>
    public int InterCallPauseMs()
    {
        if (MinCallPauseSec >= 0)
        {
            return (int)(MinCallPauseSec * 1000);
        }

        // (user decision 10.09.2026 "rate limit enforcement we leave to SubDL...
        // meanwhile we continue at the steered rate"): the base interval IS the steered
        // rate (3600s / rate). Floor 1 s (API friendliness), no upper clamp.
        return Math.Max(3_600_000 / TransfersPerHour, 1000);
    }

    /// <summary>
    /// F-M26: pause between two REAL transfers (upload→upload, download→download): the
    /// steered interval (3600/rate), randomised by ±30 %.
    /// </summary>
    /// <returns>Delay in milliseconds.</returns>
    public int TransferPauseMs()
    {
        int baseMs = Math.Max(3_600_000 / Math.Max(TransfersPerHour, 1), 1000);

        // ±30 % around the base pause (F-M26 band). Random.Shared is thread-safe, so the
        // rhythm needs no lock of its own.
        return (int)(baseMs * (Random.Shared.NextDouble() * 0.6 + 0.7));
    }

    /// <summary>
    /// F-M310: milliseconds of audio alignment not yet credited against the transfer rhythm.
    /// Written on the thread that measured a fit, read at the next pause.
    /// </summary>
    private long _fitMsPending;

    /// <summary>
    /// F-M310: books the duration of one audio alignment. The alignment runs AFTER a subtitle was
    /// downloaded and BEFORE the next real download, so it sits inside a transfer pause that was
    /// sized for a download only — the rhythm was charging the run for time it spent correcting,
    /// not transferring. Nothing is banked: the credit can only bring the NEXT pause down.
    /// </summary>
    /// <param name="ms">Measured milliseconds. Zero and negative values are ignored.</param>
    public void AddFitMs(long ms)
    {
        if (ms <= 0)
        {
            return;
        }

        // Interlocked although both pipelines walk their items sequentially today: the limiter is
        // shared across the directions, and "sequential" is a property of the caller, not of this
        // class — assuming it here would make the arithmetic silently wrong the day it changes.
        System.Threading.Interlocked.Add(ref _fitMsPending, ms);
    }

    /// <summary>
    /// F-M310: the pause before a real transfer, less the alignment time booked since the last
    /// one. Floor 0: the plugin never waits a negative time, and an alignment longer than the
    /// pause simply means no further wait. The credit is CONSUMED here — taking it twice would
    /// shrink two pauses for one alignment.
    /// </summary>
    /// <returns>Delay in milliseconds, never below zero.</returns>
    public int TransferPauseMsLessFit()
    {
        int pause = TransferPauseMs();

        // Exchange, not read-then-clear: two concurrent readers must not both spend the same
        // credit, which is exactly what the two-call form would allow.
        long credit = System.Threading.Interlocked.Exchange(ref _fitMsPending, 0);

        return (int)Math.Max(0, pause - credit);
    }

    // ---- F-M594: der Takt als Slot vor dem Transfer ------------------------------------------
    //
    // Operator-Anweisung 18.11.2026: „Lässt sich die zeitliche Differenz seit letztem
    // download/upload ende messen und die pause exakt anpassen? Dann müsste die
    // download/upload rate limit pause exakt vor dem tatsächlich stattfindenden
    // upload/download stattfinden." Bis hier wurde nach einem Transfer BLIND die Pause
    // abgewartet (TransferPauseMs/TransferPauseMsLessFit); Extraktion, Gate-Pruefungen und
    // Vorbereitung der naechsten Spur kamen obendrauf. Gemessen am 18.11.2026 bei 600/h:
    // eingestellt 6 s, tatsaechlich 8,2 s je Spur nach Wanduhr (rund 440/h). Jetzt haelt die
    // Klasse einen Zeitanker: Ende des letzten ECHTEN Transfers. Vor dem naechsten Transfer
    // wird nur die DIFFERENZ zum Ziel (Anker + Intervall) gewartet — Vorbereitungszeit ist
    // damit Teil der Pause statt ein Zuschlag, und die eingestellte Rate ist die tatsaechliche
    // Rate, solange die Vorbereitung kuerzer dauert als das Intervall.
    //
    // Der Anker ist STATISCH: Upload und Download teilen sich `UploadsPerHour` (ein Feld, eine
    // Rate), also teilen sie sich auch den Anker — sonst zahlte jede Richtung ihren eigenen
    // Takt und die Summe laege ueber der Einstellung. Er zaehlt nur ECHTE Transfers
    // (F-M547/F-M589): eine Spur ohne Uebertragung setzt ihn nicht, die naechste startet
    // deshalb ohne Wartezeit.

    /// <summary>F-M594: Ende des letzten echten Transfers, prozessweit geteilt (Upload + Download).</summary>
    private static DateTime _lastTransferDoneUtc = DateTime.MinValue;

    /// <summary>F-M594: Schloss um den Anker — die Quellen einer Spur laufen parallel.</summary>
    private static readonly object _slotLock = new();

    /// <summary>
    /// F-M594: wartet, bis der naechste Transferslot erreicht ist. Ziel ist
    /// „Ende des letzten Transfers + Intervall (3600/Rate, ±30 %)"; gewartet wird nur die
    /// Differenz. Ohne Anker (erster Transfer, oder der letzte liegt lange zurueck) wartet
    /// nichts — die Rate ist ein Deckel, kein Metronom, das Leerlauf nachholt.
    /// </summary>
    /// <param name="ct">Abbruch des Laufs.</param>
    public async Task WaitForTransferSlotAsync(CancellationToken ct)
    {
        int interval = TransferPauseMs();
        DateTime anchor;
        lock (_slotLock)
        {
            anchor = _lastTransferDoneUtc;
        }

        if (anchor == DateTime.MinValue)
        {
            return;
        }

        TimeSpan warten = anchor.AddMilliseconds(interval) - DateTime.UtcNow;
        if (warten <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(warten, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// F-M594: setzt den Anker auf JETZT — nach einem Transfer, der wirklich Bytes bewegt hat.
    /// Eine Spur, die nichts uebertraegt (Skip, Tor, Fehlschlag ohne Antwort), ruft das NICHT;
    /// sie soll den Rhythmus nicht bestrafen (F-M547/F-M589).
    /// </summary>
    public void MarkTransferDone()
    {
        lock (_slotLock)
        {
            _lastTransferDoneUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Waits one bare-call pause. This is the whole throttle now — it cannot fail, because
    /// there is no bucket left to run dry (F-M20).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes after the pause.</returns>
    public Task PauseAsync(CancellationToken ct) => Task.Delay(InterCallPauseMs(), ct);
}
