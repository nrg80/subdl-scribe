// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// F-M547: die eine Pause, die an einer EXTERNEN Anfrage haengt — und nur dort.
/// <para>
/// Operator-Regel vom 16.11.2026: „Und nur wenn eine externe api tmdb,subdl gefragt wird oder tvst/mst
/// das api rate limit (einstellbar 0.5 sec) einziehen. Immer danach. Sonst keine limits." Der Wert ist
/// <see cref="Configuration.PluginConfiguration.MinCallPauseSec"/> (Default 0,5 s); 0 schaltet die Pause
/// ab.
/// </para>
/// <para>
/// Sie wird von <see cref="PacingHandler"/> gezogen, der in JEDER HTTP-Kette des Plugins sitzt. Damit
/// gilt sie fuer TMDb, SubDL, tvsubtitles und moviesubtitles gleichzeitig, ohne dass ein Client, ein
/// Aufrufer oder eine neue Quelle davon wissen muss — jede Antwort wird bezahlt, jede vergessene Stelle
/// gibt es nicht.
/// </para>
/// </summary>
public static class ExternalApiPacer
{
    /// <summary>Default pause after an external call, in milliseconds (0,5 s).</summary>
    public const int DefaultPauseMs = 500;

    private static int _pauseMs = DefaultPauseMs;

    /// <summary>Gets the pause that is currently drawn after each external call.</summary>
    public static int PauseMs => Volatile.Read(ref _pauseMs);

    /// <summary>
    /// Sets the pause from the configuration value. A negative value means „derive from the transfer
    /// rate" in the limiter's language; here it means the documented default.
    /// </summary>
    /// <param name="minCallPauseSec">Configured pause in seconds (-1 = default).</param>
    public static void Configure(double minCallPauseSec)
    {
        int ms = minCallPauseSec < 0
            ? DefaultPauseMs
            : (int)System.Math.Round(minCallPauseSec * 1000);
        Volatile.Write(ref _pauseMs, ms < 0 ? 0 : ms);
    }

    /// <summary>
    /// Waits the configured pause after an external call. A pause of 0 returns immediately — that is the
    /// operator's „sonst keine limits".
    /// </summary>
    /// <param name="ct">Cancellation token of the run.</param>
    /// <returns>A task that completes after the pause.</returns>
    public static async Task AfterCallAsync(CancellationToken ct)
    {
        int ms = PauseMs;
        if (ms > 0 && !ct.IsCancellationRequested)
        {
            await Task.Delay(ms, ct).ConfigureAwait(false);
        }
    }
}
