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
    /// recorded for a fixed grace period of <see cref="Grace"/>.
    /// <para>
    /// The grace used to be scaled from the configured rate limit (2.4–3.8 min). That
    /// was wrong in both directions: it could abort a legal transfer — one file download is
    /// allowed 5 minutes (FileTransferTimeout), a value the scaled grace never reached — and
    /// it tied a robustness limit to a pacing setting. With the local call budget removed
    /// (F-M20) there is nothing left to scale from, so the grace is a constant that covers
    /// the longest legal single operation with room to spare.
    /// </para>
    /// <para>The heartbeat keeps the watchdog quiet while the run is making progress.</para>
    /// </summary>
    /// F-M209: torn down on EVERY exit path, so a run that leaves on an exception leaves no timer
    /// and no subscription behind.
    public sealed class RunWatchdog : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationToken _linked;
        private readonly TimeSpan _grace;
        private readonly Action<string>? _onAbort;
        private readonly string _name;
        private readonly object _lock = new();
        private DateTime _lastHeartbeat = DateTime.UtcNow;
        private bool _aborted;
        private Task? _monitor;

        /// <summary>
        /// Initializes a watchdog with a fixed idle grace.
        /// </summary>
        public RunWatchdog(string name, CancellationToken userToken, TimeSpan grace, Action<string>? onAbort = null)
        {
            _name = name;
            _grace = grace;
            _onAbort = onAbort;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, _cts.Token).Token;
        }

        /// <summary>Gets the token that aborts the run (user cancel OR watchdog trip).</summary>
        public CancellationToken Token => _linked;

        /// <summary>
        /// Gets the fixed idle grace: 6 minutes. It covers the 5-minute file-transfer
        /// timeout (SubdlApiClient.FileTransferTimeout) with room to spare, so a slow but
        /// working transfer is never mistaken for a frozen run.
        /// </summary>
        public static TimeSpan Grace => TimeSpan.FromMinutes(6);

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
                    TimeSpan grace = _grace;
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

                        _onAbort?.Invoke($"[{_name}] Watchdog: no progress for {idle.TotalMinutes:F1} min (grace {grace.TotalMinutes:F1} min) — aborting run.");
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