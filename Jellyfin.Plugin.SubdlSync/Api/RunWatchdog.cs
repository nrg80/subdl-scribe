// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubdlScribe.Api
{
    /// <summary>
    /// In-run watchdog (NF-4): aborts a pipeline run when no heartbeat has been
    /// recorded for a grace period scaled from the configured rate limit.
    /// The grace is a multiple of the rate period (3600 / calls-per-hour),
    /// linearly interpolated: 3x at the minimum rate (1/h → 3h), 20x at the
    /// maximum (500/h → ~2.4 min). Waiting out a configured rate pause is
    /// always legal; only a genuinely frozen run exceeds the grace.
    /// </summary>
    /// F-M209: torn down on EVERY exit path, so a run that leaves on an exception leaves no timer
    /// and no subscription behind.
    public sealed class RunWatchdog : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationToken _linked;
        private readonly Func<TimeSpan> _grace;
        private readonly Action<string>? _onAbort;
        private readonly string _name;
        private readonly object _lock = new();
        private DateTime _lastHeartbeat = DateTime.UtcNow;
        private bool _aborted;
        private Task? _monitor;

        /// <summary>
        /// Initializes a watchdog. <paramref name="grace"/> is evaluated at
        /// check time so live config changes apply mid-run.
        /// </summary>
        public RunWatchdog(string name, CancellationToken userToken, Func<TimeSpan> grace, Action<string>? onAbort = null)
        {
            _name = name;
            _grace = grace;
            _onAbort = onAbort;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, _cts.Token).Token;
        }

        /// <summary>Gets the token that aborts the run (user cancel OR watchdog trip).</summary>
        public CancellationToken Token => _linked;

        /// <summary>
        /// Grace from the configured calls-per-hour, linearly scaled:
        /// multiple = 3 + (rate-1)/499 * 17 (3x @ 1/h … 20x @ 500/h).
        /// </summary>
        public static TimeSpan GraceFromRate(int callsPerHour)
        {
            int rate = Math.Clamp(callsPerHour, 1, 500);
            double periodSec = 3600.0 / rate;
            double multiple = 3.0 + ((rate - 1) / 499.0) * 17.0;
            return TimeSpan.FromSeconds(multiple * periodSec);
        }

        /// <summary>Record a heartbeat — the run is alive and making progress.</summary>
        public void Heartbeat()
        {
            lock (_lock)
            {
                _lastHeartbeat = DateTime.UtcNow;
            }
        }

        /// <summary>Starts the background monitor loop.</summary>
        public void Start()
        {
            if (_monitor != null)
            {
                return;
            }

            _monitor = Task.Run(async () =>
            {
                while (!_linked.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), _linked).ConfigureAwait(false);
                    TimeSpan grace = _grace();
                    TimeSpan idle;
                    lock (_lock)
                    {
                        idle = DateTime.UtcNow - _lastHeartbeat;
                    }

                    if (idle > grace)
                    {
                        lock (_lock)
                        {
                            if (_aborted)
                            {
                                return;
                            }

                            _aborted = true;
                        }

                        _onAbort?.Invoke($"[{_name}] Watchdog: no progress for {idle.TotalMinutes:F1} min (grace {grace.TotalMinutes:F1} min from rate limit) — aborting run.");
                        _ = _cts.CancelAsync();
                        return;
                    }
                }
            }, _linked);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}