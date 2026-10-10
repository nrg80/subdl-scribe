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
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Jellyfin.Plugin.SubdlScribe.Registry;
using Jellyfin.Plugin.SubdlScribe.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe;

/// <summary>
/// DI registration of shared services (F-M40): API client, registry, resolver.
/// The download pipeline (B3) consumes the same services via constructor injection.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Builds a bare SubDL API client outside a pipeline run — for a read-only probe.
    /// <para>
    /// ONE construction site for all three users (upload pipeline, download pipeline, and the
    /// dispatcher's pre-run quota probe). The probe reads the SAME counters the pipeline's own 429
    /// decision reads (F-M238) instead of inventing a second quota source: two sources for one
    /// statement can drift, and the second is invisible to check.
    /// </para>
    /// </summary>
    /// <param name="config">Plugin configuration (carries the credentials).</param>
    /// <returns>The client and the HttpClient it owns; the caller disposes the client.</returns>
    public static (SubdlApiClient Api, HttpClient Http) BuildApiClient(PluginConfiguration config)
    {
        // F-M235: the timeout lives in the handler now, per attempt, so a slow server is retried
        // (3 attempts) instead of surfacing as a cancellation. HttpClient.Timeout is disabled
        // (InfiniteTimeSpan) because it would otherwise cap the WHOLE request including retries
        // and cancel them mid-sequence.
        var retry = new TransientRetryHandler(new SocketsHttpHandler());
        var http = new HttpClient(retry) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        // F-M343: the client identity travels with EVERY call this client makes. The per-request
        // header inside SubdlApiClient covers search, download and upload; the login call builds
        // its own request and carried no agent at all (verified 10.10.2026: the call succeeds with
        // the plugin agent, with the old one and with none), so the identity is set once here as
        // the client's own default rather than at a fourth call site.
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SubdlApiClient.DefaultUserAgent);

        var api = new SubdlApiClient(http)
        {
            Username = config.Username,
            Password = config.Password,
            ApiKey = config.ApiKey
        };
        // The handler is built before the client, but the client owns the log channel: route the
        // retry lines back into it so a hidden retry still shows up.
        retry.Log = msg => api.RaiseLog(msg);
        return (api, http);
    }

    /// <summary>
    /// Builds a ready-to-use pipeline outside JF DI (for the scheduled task entry).
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>Disposable pipeline bundle.</returns>
    public static PipelineBundle BuildPipeline(ILoggerFactory loggerFactory, ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, PluginConfiguration config)
    {
        string dataDir = Plugin.Instance!.DataFolderPath;
        var db = Plugin.Instance.SharedDbContext;
        var registry = new ContentHashRegistry(db, null, loggerFactory.CreateLogger<ContentHashRegistry>());
        // F-M235: the timeout lives in the handler now, per attempt, so a slow server is retried
        // (3 attempts) instead of surfacing as a cancellation. HttpClient.Timeout is disabled
        // (InfiniteTimeSpan) because it would otherwise cap the WHOLE request including retries
        // and cancel them mid-sequence.
        var (api, http) = BuildApiClient(config);
        var tmdb = new TmdbImdbResolver(http, config.TmdbApiKey);
        // F-M20/F-M26: ONE pacing rhythm shared by upload + download (same SubDL account).
        // It spaces calls and jitters transfers — it holds no budget and refuses nothing.
        var limiter = new GlobalRateLimiter(config.UploadsPerHour, config.MinCallPauseSec);
        var logger = loggerFactory.CreateLogger<UploadPipeline>();
        var pipeline = new UploadPipeline(logger, libraryManager, mediaSourceManager, api, tmdb, config, limiter);
        return new PipelineBundle(pipeline, registry, http);
    }

    /// <summary>
    /// Disposes a pipeline bundle after the run.
    /// </summary>
    /// <param name="bundle">Bundle to dispose.</param>
    public static void DisposePipeline(PipelineBundle bundle)
    {
        bundle.Registry.Dispose();
        bundle.Http.Dispose();
    }

    /// <summary>
    /// Builds a ready-to-use download pipeline outside JF DI ([D] F-M41).
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>Disposable download pipeline bundle.</returns>
    public static DownloadPipelineBundle BuildDownloadPipeline(ILoggerFactory loggerFactory, ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, PluginConfiguration config)
    {
        string dataDir = Plugin.Instance!.DataFolderPath;
        var db = Plugin.Instance.SharedDbContext;
        var registry = new ContentHashRegistry(db, null, loggerFactory.CreateLogger<ContentHashRegistry>());
        // F-M235: the timeout lives in the handler now, per attempt, so a slow server is retried
        // (3 attempts) instead of surfacing as a cancellation. HttpClient.Timeout is disabled
        // (InfiniteTimeSpan) because it would otherwise cap the WHOLE request including retries
        // and cancel them mid-sequence.
        var (api, http) = BuildApiClient(config);
        var tmdb = new TmdbImdbResolver(http, config.TmdbApiKey);
        // F-M20/F-M26: same pacing semantics as the upload bundle — no shared budget, because
        // there is none left to share (F-M20, 03.10.2026).
        var limiter = new GlobalRateLimiter(config.UploadsPerHour, config.MinCallPauseSec);
        var searchTracker = new DownloadSearchTracker(db, loggerFactory.CreateLogger<DownloadSearchTracker>());
        var qaFails = new QaFailTracker(db, loggerFactory.CreateLogger<QaFailTracker>());
        var logger = loggerFactory.CreateLogger<DownloadPipeline>();
        var pipeline = new DownloadPipeline(logger, libraryManager, mediaSourceManager, api, tmdb, config, limiter, searchTracker, qaFails);
        return new DownloadPipelineBundle(pipeline, registry, http, searchTracker, qaFails);
    }

    /// <summary>
    /// Disposes a download pipeline bundle after the run.
    /// </summary>
    /// <param name="bundle">Bundle to dispose.</param>
    public static void DisposeDownloadPipeline(DownloadPipelineBundle bundle)
    {
        bundle.SearchTracker.Flush();
        bundle.Registry.Dispose();
        bundle.Http.Dispose();
    }

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Shared services registered for future consumers (download pipeline B3).
        // F-M111 (user decision 12.09.2026): event dispatcher singleton — owns the
        // ItemAdded subscription, the refetch/recovery cycle triggers and the
        // persisted queues. Seeder + workers are plain objects (no DI). The
        // scheduler coordinator stays for recovery fires and refetch ticks; both
        // forward to the dispatcher. Eager construction happens via the task
        // constructors (JF resolves task ctors at startup).
        serviceCollection.AddSingleton<SubdlDbContext>(_ =>
        {
            string dataDir = Plugin.Instance!.DataFolderPath;
            return new SubdlDbContext(dataDir);
        });
        serviceCollection.AddSingleton<ContentHashRegistry>(sp => new ContentHashRegistry(sp.GetRequiredService<SubdlDbContext>()));
        serviceCollection.AddSingleton<TmdbImdbResolver>(sp =>
        {
            // F-M235: same retry discipline for the DI-resolved resolver.
            var retry = new TransientRetryHandler(new System.Net.Http.SocketsHttpHandler());
            var http = new System.Net.Http.HttpClient(retry) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                      .CreateLogger<TmdbImdbResolver>();
            retry.Log = msg => Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(log, "[SubDL-Retry] {Msg}", msg);
            return new TmdbImdbResolver(http, Plugin.Instance!.Configuration.TmdbApiKey);
        });
        serviceCollection.AddSingleton<SubdlEventDispatcher>();
        serviceCollection.AddSingleton<SubdlSchedulerCoordinator>();
        serviceCollection.AddSingleton<SubdlApiClient>();
        serviceCollection.AddScoped<SubdlPostprocessTask>();
    }

    /// <summary>
    /// Disposable bundle of download pipeline + services for one run.
    /// </summary>
    public sealed class DownloadPipelineBundle : IDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DownloadPipelineBundle"/> class.
        /// </summary>
        public DownloadPipelineBundle(DownloadPipeline pipeline, ContentHashRegistry registry, HttpClient http, DownloadSearchTracker searchTracker, QaFailTracker qaFails)
        {
            Pipeline = pipeline;
            Registry = registry;
            Http = http;
            SearchTracker = searchTracker;
            QaFails = qaFails;
        }

        /// <summary>Gets the download pipeline.</summary>
        public DownloadPipeline Pipeline { get; }

        /// <summary>Gets the registry.</summary>
        public ContentHashRegistry Registry { get; }

        /// <summary>Gets the HTTP client.</summary>
        public HttpClient Http { get; }

        /// <summary>Gets the per-item search tracker (F-M47).</summary>
        public DownloadSearchTracker SearchTracker { get; }

        /// <summary>Gets the per-(item, language) QA-failure tracker.</summary>
        public QaFailTracker QaFails { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            SearchTracker.Flush();
            QaFails.Flush(); // Persist qa-failure counters after the run
            Registry.Dispose();
            Http.Dispose();
        }
    }

    /// <summary>
    /// Disposable bundle of pipeline + services for one run.
    /// </summary>
    public sealed class PipelineBundle : IDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PipelineBundle"/> class.
        /// </summary>
        public PipelineBundle(UploadPipeline pipeline, ContentHashRegistry registry, HttpClient http)
        {
            Pipeline = pipeline;
            Registry = registry;
            Http = http;
        }

        /// <summary>Gets the upload pipeline.</summary>
        public UploadPipeline Pipeline { get; }

        /// <summary>Gets the registry.</summary>
        public ContentHashRegistry Registry { get; }

        /// <summary>Gets the HTTP client.</summary>
        public HttpClient Http { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            Registry.Dispose();
            Http.Dispose();
        }
    }
}