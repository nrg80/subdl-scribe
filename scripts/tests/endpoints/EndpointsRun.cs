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

// T204 / F-M483-F-M484 (operator order 15.11.2026): two download endpoints and two upload endpoints,
// the rest of the plugin unchanged.
//
// What is asserted here, and why each one would bite if it drifted:
//   1. FOUR endpoints exist, two per direction, one per source — the whole point of the order.
//   2. The CATALOG hands out only what may run: switch on AND wired. A catalog that ignores the
//      wiring would let a caller believe tvsubtitles.net runs.
//   3. An implemented-but-unwired endpoint NAMES its reason, in the same words the page shows —
//      one sentence, not three (F-M366 line).
//   4. The SubDL download endpoint maps the client's candidate onto the neutral hit; the mapping is
//      pure, so it is checked without a network call.
//   5. The tvsubtitles download endpoint really walks the captured pages: show list → season →
//      episode page → entries filtered to the wanted language → fetch. The fake handler serves the
//      fixtures, so a changed page shape shows up as a red check.
//   6. The tvsubtitles upload endpoint builds the form body and sends NOTHING in a dry run, and
//      refuses a season pack and an unknown language with a reason.
//   7. The SubDL upload endpoint refuses what it cannot do (season pack, no id, non-UTF-8 bytes)
//      instead of half-sending it.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Api;
using Jellyfin.Plugin.SubdlScribe.Api.Endpoints;
using Jellyfin.Plugin.SubdlScribe.Configuration;

internal static class Program
{
    private static int _failures;
    private static int _checks;
    private static string _fixtures = string.Empty;

    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        _fixtures = Path.Combine(root, "scripts/tests/tvsubtitles/fixtures");

        // ---- 1. four endpoints, two per direction ----------------------------------------------
        var tvClient = new TvSubtitlesClient(new HttpClient(new FixtureHandler(_fixtures)));
        var subdlClient = new SubdlApiClient(new HttpClient(new FixtureHandler(_fixtures)));
        var catalog = new SubtitleEndpointCatalog(
            new IDownloadEndpoint[]
            {
                new SubdlDownloadEndpoint(subdlClient),
                new TvSubtitlesDownloadEndpoint(tvClient),
            },
            new IUploadEndpoint[]
            {
                new SubdlUploadEndpoint(subdlClient),
                new TvSubtitlesUploadEndpoint(tvClient, dryRun: true),
            });

        Check("there are two download endpoints, one per source",
            catalog.AllDownloads.Count == 2
            && catalog.AllDownloads.Select(e => e.Provider).Distinct().Count() == 2);
        Check("there are two upload endpoints, one per source",
            catalog.AllUploads.Count == 2
            && catalog.AllUploads.Select(e => e.Provider).Distinct().Count() == 2);
        Check("both sources are wired in both directions (F-M485)",
            catalog.AllDownloads.All(e => e.Wired) && catalog.AllUploads.All(e => e.Wired));

        // ---- 2. the catalog obeys switches AND wiring ------------------------------------------
        var aus = Conf(c => { c.DownloadEnabled = false; c.UploadEnabled = false; });
        Check("everything off yields no endpoint",
            catalog.EnabledDownloads(aus).Count == 0 && catalog.EnabledUploads(aus).Count == 0);

        var nurDownload = Conf(c => { c.DownloadEnabled = true; c.UploadEnabled = false; });
        Check("the download switch alone yields exactly the SubDL download endpoint",
            catalog.EnabledDownloads(nurDownload).Count == 1
            && catalog.EnabledDownloads(nurDownload)[0].Provider == SubtitleProvider.Subdl
            && catalog.EnabledUploads(nurDownload).Count == 0);

        var beide = Conf(c => { c.DownloadEnabled = true; c.UploadEnabled = true; });
        Check("switching SubDL on does not drag tvsubtitles into the run",
            catalog.EnabledDownloads(beide).All(e => e.Provider == SubtitleProvider.Subdl)
            && catalog.EnabledUploads(beide).All(e => e.Provider == SubtitleProvider.Subdl));

        var tvAn = Conf(c =>
        {
            c.DownloadEnabled = true;
            c.UploadEnabled = true;
            c.TvSubtitlesDownloadEnabled = true;
            c.TvSubtitlesUploadEnabled = true;
        });
        Check("the tvsubtitles switches now pull the second endpoint in, each on its own",
            catalog.EnabledDownloads(tvAn).Count == 2 && catalog.EnabledUploads(tvAn).Count == 2,
            "downloads=" + catalog.EnabledDownloads(tvAn).Count + " uploads=" + catalog.EnabledUploads(tvAn).Count);

        var tvNurDownload = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = false;
            c.TvSubtitlesDownloadEnabled = true;
            c.TvSubtitlesUploadEnabled = false;
        });
        Check("tvsubtitles download alone runs while both SubDL directions stay off",
            catalog.EnabledDownloads(tvNurDownload).Count == 1
            && catalog.EnabledDownloads(tvNurDownload)[0].Provider == SubtitleProvider.TvSubtitles
            && catalog.EnabledUploads(tvNurDownload).Count == 0);

        var tvNurUpload = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = false;
            c.TvSubtitlesDownloadEnabled = false;
            c.TvSubtitlesUploadEnabled = true;
        });
        Check("tvsubtitles upload alone runs while everything else is off",
            catalog.EnabledUploads(tvNurUpload).Count == 1
            && catalog.EnabledUploads(tvNurUpload)[0].Provider == SubtitleProvider.TvSubtitles
            && catalog.EnabledDownloads(tvNurUpload).Count == 0);

        var nurSubdlUpload = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = true;
            c.TvSubtitlesUploadEnabled = false;
        });
        Check("SubDL upload alone stays alone", catalog.EnabledUploads(nurSubdlUpload).Count == 1
            && catalog.EnabledUploads(nurSubdlUpload)[0].Provider == SubtitleProvider.Subdl);

        // ---- 3. the refusal names its reason, in the page's words ------------------------------
        var refusedDown = catalog.Refused(tvAn, SubtitleDirection.Download);
        var refusedUp = catalog.Refused(tvAn, SubtitleDirection.Upload);
        Check("with both switches on nothing is refused", refusedDown.Count == 0 && refusedUp.Count == 0);
        var tvAus = Conf(c => { c.DownloadEnabled = true; c.TvSubtitlesDownloadEnabled = false; });
        var refusedTvAus = catalog.Refused(tvAus, SubtitleDirection.Download);
        Check("a source whose switch is off is listed as refused, not silently missing",
            refusedTvAus.Count == 1 && refusedTvAus[0].Provider == SubtitleProvider.TvSubtitles
            && refusedTvAus[0].Reason == "switched off");
        Check("the sentence for a source is the one the config page prints",
            PluginConfiguration.MissingProviderReason(SubtitleProvider.TvSubtitles) is { Length: > 20 }
            && PluginConfiguration.MissingProviderReason(SubtitleProvider.TvSubtitles)!
                .Contains("Staffelpacks", StringComparison.Ordinal));
        Check("a wired endpoint that is switched off says so",
            catalog.Refused(aus, SubtitleDirection.Download)
                .Any(r => r.Provider == SubtitleProvider.Subdl && r.Reason == "switched off"));

        // ---- 4. the SubDL mapping is pure and complete ----------------------------------------
        var candidate = new SubtitleCandidate
        {
            ReleaseName = "The.Middle.S02E24.720p.HDTV.x264-LOL",
            Language = "DE",
            Url = "https://dl.subdl.com/subtitle/abc123.zip",
            Season = 2,
            Episode = 24,
            FullSeason = false,
        };
        var map = SubdlDownloadEndpoint.Map(candidate);
        Check("a SubDL candidate becomes a neutral hit without losing its handle",
            map.Provider == SubtitleProvider.Subdl && map.Id == candidate.Url
            && map.Language == "DE" && map.Release == candidate.ReleaseName && !map.FullSeason);
        Check("SubDL has no rip cell, so the hit does not invent one", map.Rip.Length == 0);
        Check("the language bridge knows the site's vocabulary and refuses the rest",
            TvSubtitlesDownloadEndpoint.SiteLanguage("DE") == "de"
            && TvSubtitlesDownloadEndpoint.SiteLanguage("pt") == "pt"
            && TvSubtitlesDownloadEndpoint.SiteLanguage("xx") is null);

        // ---- 5. the tvsubtitles download endpoint walks the captured pages ---------------------
        var tvDownload = new TvSubtitlesDownloadEndpoint(tvClient);
        var query = new SubtitleQuery("The Middle", null, null, 2, 24, "en");
        var hits = await tvDownload.SearchAsync(query).ConfigureAwait(false);
        Check("the search finds the episode page and keeps the wanted language",
            hits.Count == 1 && hits[0].Language == "en" && hits[0].Id == "170678",
            "found: " + string.Join(",", hits.Select(h => h.Language + "/" + h.Id)));
        var andereSprache = await tvDownload.SearchAsync(query with { Language = "fr" }).ConfigureAwait(false);
        Check("a different language yields that language's entry instead",
            andereSprache.Count == 1 && andereSprache[0].Id == "190460");
        var falscheFolge = await tvDownload.SearchAsync(query with { Episode = 23 }).ConfigureAwait(false);
        Check("an episode the source does not carry yields nothing, not the nearest one",
            falscheFolge.Count == 0);
        var unbekannteSerie = await tvDownload.SearchAsync(query with { ShowName = "Nope-Nope" }).ConfigureAwait(false);
        Check("an unknown show yields nothing", unbekannteSerie.Count == 0);
        var ohneNamen = await tvDownload.SearchAsync(query with { ShowName = null }).ConfigureAwait(false);
        Check("a source without an id index needs the name — no name, no hits", ohneNamen.Count == 0);

        var payload = await tvDownload.FetchAsync(hits[0]).ConfigureAwait(false);
        Check("the fetch delivers name and bytes, undecoded",
            payload is not null && payload.Data.Length > 0 && payload.FileName.EndsWith(".srt", StringComparison.Ordinal),
            payload is null ? "no payload" : payload.FileName + " " + payload.Data.Length + " B");

        // ---- 6. the tvsubtitles upload endpoint: dry run, and refusals -------------------------
        var tvUpload = new TvSubtitlesUploadEndpoint(tvClient, dryRun: true);
        var ticket = new UploadTicket(
            "The Middle", "tt1442464", "1422", 2, 24, "de", "The.Middle.S02E24.de.srt",
            Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhallo\n"), "HDTV", "FEVER", false, false, false);
        var dry = await tvUpload.SendAsync(ticket).ConfigureAwait(false);
        Check("a dry run reports a built body, sends nothing, and says it was a dry run",
            dry.Ok && dry.DryRun && dry.Bytes > ticket.Data.Length && dry.Provider == SubtitleProvider.TvSubtitles,
            "bytes=" + dry.Bytes);
        var pack = await tvUpload.SendAsync(ticket with { SeasonPack = true }).ConfigureAwait(false);
        Check("a season pack is refused — this source has none", !pack.Ok && pack.Bytes == 0
            && pack.Reason!.Contains("no season packs", StringComparison.Ordinal));
        var sprache = await tvUpload.SendAsync(ticket with { Language = "xx" }).ConfigureAwait(false);
        Check("an unknown language is refused before any request", !sprache.Ok && sprache.Reason!.Contains("xx", StringComparison.Ordinal));
        var ohneShow = await tvUpload.SendAsync(ticket with { ShowName = null }).ConfigureAwait(false);
        Check("without a show name nothing can be resolved", !ohneShow.Ok && ohneShow.Reason!.Contains("show name", StringComparison.Ordinal));
        var unbekannt = await tvUpload.SendAsync(ticket with { ShowName = "Nope-Nope" }).ConfigureAwait(false);
        Check("a show the site does not carry is refused with its name", !unbekannt.Ok
            && unbekannt.Reason!.Contains("Nope-Nope", StringComparison.Ordinal));

        // ---- 7. the SubDL upload endpoint refuses what it cannot do ---------------------------
        var subdlUpload = new SubdlUploadEndpoint(subdlClient);
        var keinPack = await subdlUpload.SendAsync(ticket with { SeasonPack = true }).ConfigureAwait(false);
        Check("SubDL refuses a season pack here — packs are the pipeline's job (F-M372)",
            !keinPack.Ok && keinPack.Reason!.Contains("season packs", StringComparison.Ordinal));
        var ohneImdb = await subdlUpload.SendAsync(ticket with { ImdbId = null }).ConfigureAwait(false);
        Check("SubDL refuses without an IMDb id", !ohneImdb.Ok && ohneImdb.Reason == "no-imdb");
        var cp1250 = await subdlUpload.SendAsync(
            ticket with { Data = new byte[] { 0x41, 0xF5, 0x42 } }).ConfigureAwait(false);
        Check("SubDL refuses bytes that are not UTF-8 instead of transcoding them",
            !cp1250.Ok && cp1250.Reason!.Contains("not UTF-8", StringComparison.Ordinal));
        var angemeldet = await subdlUpload.SendAsync(ticket).ConfigureAwait(false);
        Check("without a session the refusal is readable, not an exception",
            !angemeldet.Ok && angemeldet.Reason!.Contains("Not logged in", StringComparison.Ordinal));

        // ---- 8. die Einbindung in den Lauf: Quelltext-Bindung (F-M485) -----------------------
        var root2 = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
        var downloadSource = File.ReadAllText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        var uploadSource = File.ReadAllText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check("the download pipeline asks the second source exactly where a language would be given up",
            downloadSource.Contains("IsDownloadEnabled(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal)
            && downloadSource.Contains("ForeignCandidatesAsync(title, isSeries, season, episode, lang, ct)", StringComparison.Ordinal),
            "the fallback sits in the language loop, before \"not available\"");
        Check("a foreign candidate is fetched from its own endpoint, not over SubDL's routes",
            downloadSource.Contains("cand.Provider != Configuration.SubtitleProvider.Subdl", StringComparison.Ordinal)
            && downloadSource.Contains("Route = \"tvsubtitles\"", StringComparison.Ordinal));
        Check("the upload pipeline sends to the second source only when its switch is on and SubDL did not take it",
            uploadSource.Contains("IsUploadEnabled(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal)
            && uploadSource.Contains("SendToSecondSourceAsync(", StringComparison.Ordinal));
        Check("the SubDL upload call itself is untouched — the response body the caller reads stays",
            uploadSource.Contains("_api.UploadSubtitleAsync(srtContent, neutralName, lang, releaseName, quality, imdbId, tmdbId, isSeries, season, episode, ct, hearingImpaired, framerate)", StringComparison.Ordinal));
        Check("the second source never breaks the run — its errors are warnings",
            downloadSource.Contains("[tvsubtitles-D] search failed for", StringComparison.Ordinal));

        // ---- 9. Herkunft und Kreuzkontamination (F-M486/F-M487) -------------------------------
        Check("the store value names the source, and parses back",
            Provenance.Name(SubtitleProvider.Subdl) == "subdl"
            && Provenance.Name(SubtitleProvider.TvSubtitles) == "tvsubtitles"
            && Provenance.Parse("tvsubtitles") == SubtitleProvider.TvSubtitles
            && Provenance.Parse("SubDL") == SubtitleProvider.Subdl
            && Provenance.Parse(null) is null
            && Provenance.Parse("  ") is null);
        Check("a tvsubtitles find never goes to SubDL, and the other way round",
            !Provenance.MayUploadTo("tvsubtitles", SubtitleProvider.Subdl)
            && !Provenance.MayUploadTo("subdl", SubtitleProvider.TvSubtitles)
            && Provenance.MayUploadTo("tvsubtitles", SubtitleProvider.TvSubtitles)
            && Provenance.MayUploadTo("subdl", SubtitleProvider.Subdl));
        Check("unknown provenance stays allowed everywhere — a hand copy or an embedded track",
            Provenance.MayUploadTo(null, SubtitleProvider.Subdl)
            && Provenance.MayUploadTo(null, SubtitleProvider.TvSubtitles)
            && Provenance.MayUploadTo(string.Empty, SubtitleProvider.Subdl));
        Check("the refusal sentence names both sides",
            Provenance.CrossSourceReason("tvsubtitles", SubtitleProvider.Subdl)
                .Contains("tvsubtitles", StringComparison.Ordinal)
            && Provenance.CrossSourceReason("tvsubtitles", SubtitleProvider.Subdl)
                .Contains("subdl", StringComparison.Ordinal));
        Check("the download records the origin and the source's own id in the store",
            downloadSource.Contains("Api.Endpoints.Provenance.Name(cand.Provider)", StringComparison.Ordinal)
            && downloadSource.Contains("cand.Provider == Configuration.SubtitleProvider.Subdl ? cand.SubdlId : cand.Url", StringComparison.Ordinal));
        Check("the upload reads the origin from the store before it picks an endpoint",
            uploadSource.Contains("Registry.GetOrigin(contentHashEarly)", StringComparison.Ordinal)
            && uploadSource.Contains("Provenance.MayUploadTo(herkunft, Configuration.SubtitleProvider.Subdl)", StringComparison.Ordinal)
            && uploadSource.Contains("Provenance.MayUploadTo(herkunft, Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal));
        Check("the store schema carries the two columns and a migration step",
            File.ReadAllText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/Entities.cs"))
                .Contains("public string? OriginId { get; set; }", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SubdlDbContext.cs"))
                .Contains("ALTER TABLE sidecars ADD COLUMN Origin TEXT NULL;", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SubdlDbContext.cs"))
                .Contains("CurrentSchemaVersion = 10", StringComparison.Ordinal));

        Console.WriteLine();
        if (_failures > 0)
        {
            Console.WriteLine($">>> ENDPUNKTE: {_failures} von {_checks} Pruefungen fehlgeschlagen");
            return 1;
        }

        Console.WriteLine($">>> ENDPUNKTE OK: {_checks} Pruefungen bestanden");
        return 0;
    }

    private static PluginConfiguration Conf(Action<PluginConfiguration> mutate)
    {
        var c = new PluginConfiguration();
        c.Username = "u";
        c.Password = "p";
        c.ApiKey = "k";
        c.TmdbApiKey = "t";
        c.SelectedLibraries = new List<string> { "Movies" };
        c.DownloadLanguages = "EN, DE";
        mutate(c);
        return c;
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine("  [PASS] " + what);
            return;
        }

        _failures++;
        Console.WriteLine("  [FAIL] " + what + (detail is null ? string.Empty : " — " + detail));
    }

    /// <summary>
    /// Serves the captured tvsubtitles pages and a small ZIP, so the tvsubtitles endpoint can be driven
    /// end to end without the live site.
    /// </summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly string _dir;

        public FixtureHandler(string dir) => _dir = dir;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            var url = request.RequestUri!.ToString();
            var path = url.Replace("https://www.tvsubtitles.net/", string.Empty, StringComparison.Ordinal);

            if (path == "add.html")
            {
                return Html(File.ReadAllText(Path.Combine(_dir, "add-tvshow-select.html")));
            }

            if (path.StartsWith("download-", StringComparison.Ordinal))
            {
                // The site answers the download URL with the ZIP in one of its two shapes (F-M482).
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("The Middle - 2x24 - Back To Summer.HDTV.FEVER.en.srt");
                    using var stream = entry.Open();
                    var body = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhello\n");
                    stream.Write(body, 0, body.Length);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(buffer.ToArray()),
                };
            }

            var file = path switch
            {
                "tvshow-512.html" => "tvshow-512.html",
                "tvshow-512-2.html" => "tvshow-512-2.html",
                _ when path.StartsWith("episode-", StringComparison.Ordinal) => "episode-31911.html",
                _ when path.StartsWith("subtitle-", StringComparison.Ordinal) => "subtitle-page.html",
                _ => null,
            };

            if (file is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return Html(File.ReadAllText(Path.Combine(_dir, file)));
        }

        private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };
    }
}
