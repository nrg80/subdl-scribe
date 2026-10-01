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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M235: tolerates a transport timeout three times before giving up, and never
/// retries a user stop.
/// <para>
/// A timeout is a transient condition, exactly like the 5xx login answer handled in F-M232 — the
/// server may simply be slow. Before this, an expired request threw <c>TaskCanceledException</c>,
/// which the API clients do not catch (they catch <c>JsonException</c> only), so it travelled up
/// into the run loop's <c>catch (OperationCanceledException)</c> and was reported as "cancelled —
/// keeping the partial result". A slow server therefore looked like the user pressing stop, and
/// the item was silently dropped without a retry.
/// </para>
/// <para>
/// The two cancellations must be told apart, and only one source of truth can do it: the token the
/// CALLER passed. If that token is already cancelled the user (or the run) asked us to stop, and
/// repeating the request would make the stop button ineffective. If it is still alive, the cancel
/// came from this handler's own per-attempt timeout and the request is worth repeating.
/// </para>
/// </summary>
public sealed class TransientRetryHandler : DelegatingHandler
{
    /// <summary>Attempts per request, including the first. The login path has used 3 since F-M232.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>Default per-attempt timeout for API calls (metadata, search, list).</summary>
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Per-request override of the attempt timeout. A subtitle archive is orders of magnitude
    /// larger than a JSON answer, so the caller raises the budget for a file download instead of
    /// letting one global value decide for both.
    /// </summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> AttemptTimeoutKey =
        new("SubDL.AttemptTimeout");

    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _backoffBase;
    /// <summary>Optional diagnostic sink; set after construction when the client owns the log channel.</summary>
    private Action<string>? _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransientRetryHandler"/> class.
    /// </summary>
    /// <param name="inner">Inner handler.</param>
    /// <param name="attemptTimeout">Per-attempt timeout; null uses <see cref="DefaultAttemptTimeout"/>.</param>
    /// <param name="backoffBase">First backoff delay; doubled per further attempt.</param>
    /// <param name="log">Optional diagnostic sink (the plugin passes its LogInfo channel).</param>
    public TransientRetryHandler(HttpMessageHandler inner, TimeSpan? attemptTimeout = null, TimeSpan? backoffBase = null)
        : base(inner)
    {
        _attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
        _backoffBase = backoffBase ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Gets or sets the diagnostic sink. Settable because the handler is built before the API client
    /// that owns the run's log channel; without wiring it, a retry would be invisible in the log.
    /// </summary>
    public Action<string>? Log
    {
        get => _log;
        set => _log = value;
    }

    /// <summary>Gets the number of requests that needed more than one attempt, for run diagnostics.</summary>
    public int RetriedRequests { get; private set; }

    /// <summary>Gets the number of extra attempts spent on those requests.</summary>
    public int ExtraAttempts { get; private set; }

    /// <summary>Gets the number of requests abandoned after exhausting their attempts.</summary>
    public int GaveUp { get; private set; }

    /// <summary>
    /// True when a failed attempt is worth repeating: the caller is still asking for it, and the
    /// failure indicates a transport problem rather than an answer.
    /// </summary>
    /// <param name="attempt">1-based attempt that just failed.</param>
    /// <param name="maxAttempts">Attempt budget.</param>
    /// <param name="callerCancelled">Whether the CALLER's token is cancelled.</param>
    /// <param name="transient">Whether the failure is a transport failure.</param>
    /// <returns>True when another attempt should be made.</returns>
    public static bool ShouldRetry(int attempt, int maxAttempts, bool callerCancelled, bool transient)
        => !callerCancelled && transient && attempt < maxAttempts;

    /// <summary>
    /// True when the exception describes a transport problem (timeout, connection drop) as opposed
    /// to a cancellation the caller asked for.
    /// </summary>
    /// <param name="ex">Exception to classify.</param>
    /// <param name="callerCancelled">Whether the CALLER's token is cancelled.</param>
    /// <returns>True when the failure is transient.</returns>
    public static bool IsTransient(Exception? ex, bool callerCancelled)
    {
        if (callerCancelled)
        {
            return false; // an explicit stop is never repeated
        }

        return ex switch
        {
            // The per-attempt CTS fired, or the connection died mid-flight.
            TaskCanceledException => true,
            OperationCanceledException => true,
            HttpRequestException => true,
            System.IO.IOException => true,
            _ => false
        };
    }

    /// <summary>
    /// Classifies an exhausted request into a message that names what actually happened, so the
    /// log does not report a slow server as a stop.
    /// </summary>
    /// <param name="uri">Request uri.</param>
    /// <param name="attempts">Attempts spent.</param>
    /// <param name="callerCancelled">Whether the CALLER's token is cancelled.</param>
    /// <param name="transient">Whether the last failure was transient.</param>
    /// <returns>A log line.</returns>
    public static string DescribeExhausted(Uri? uri, int attempts, bool callerCancelled, bool transient)
    {
        string what = uri?.AbsolutePath ?? "(unknown)";
        if (callerCancelled)
        {
            return $"request to {what} cancelled by the caller after {attempts} attempt(s) — not repeated";
        }

        return transient
            ? $"request to {what} gave up after {attempts} attempts — the server did not answer in time"
            : $"request to {what} failed after {attempts} attempt(s)";
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A request body can be read only once, so buffer it up front and rebuild the request per
        // attempt. Without this a retry would throw on the consumed content instead of repeating.
        byte[]? body = null;
        if (request.Content != null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        TimeSpan timeout = request.Options.TryGetValue(AttemptTimeoutKey, out var custom) ? custom : _attemptTimeout;
        int maxAttempts = DefaultMaxAttempts;

        for (int attempt = 1; ; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(timeout);

            HttpRequestMessage outgoing = attempt == 1 && body == null ? request : CloneRequest(request, body);

            try
            {
                var response = await base.SendAsync(outgoing, attemptCts.Token).ConfigureAwait(false);
                if (attempt > 1)
                {
                    ExtraAttempts += attempt - 1;
                }

                return response;
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken.IsCancellationRequested))
            {
                bool last = !ShouldRetry(attempt, maxAttempts, cancellationToken.IsCancellationRequested, transient: true);
                if (last)
                {
                    GaveUp++;
                    _log?.Invoke(DescribeExhausted(request.RequestUri, attempt, cancellationToken.IsCancellationRequested, transient: true));
                    throw;
                }

                if (attempt == 1)
                {
                    RetriedRequests++;
                }
                else
                {
                    ExtraAttempts++;
                }

                var wait = TimeSpan.FromTicks(_backoffBase.Ticks * (1L << (attempt - 1)));
                _log?.Invoke($"request to {request.RequestUri?.AbsolutePath ?? "(unknown)"} timed out (attempt {attempt}/{maxAttempts}) — retrying in {wait.TotalSeconds:0.#}s");
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!ReferenceEquals(outgoing, request))
                {
                    outgoing.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Builds a fresh request for another attempt, carrying method, uri, version, options, headers
    /// and a copy of the buffered body.
    /// </summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage source, byte[]? body)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };

        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in source.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        if (body != null)
        {
            var content = new ByteArrayContent(body);
            if (source.Content != null)
            {
                foreach (var header in source.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            clone.Content = content;
        }

        return clone;
    }
}
