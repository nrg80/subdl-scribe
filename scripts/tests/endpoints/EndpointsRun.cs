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
using Jellyfin.Plugin.SubdlScribe.Registry;

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
        // F-M503: der tvst-Download ist im null-Fall AN. „alles aus" heisst deshalb seit dem
        // Operator-Entscheid vom 15.11.2026: BEIDE Download-Schalter aus — der Alt-Schalter allein
        // traegt die Quelle nicht mehr.
        // F-M507: „alles aus" verlangt jetzt DREI Download-Schalter, weil die dritte Quelle im
        // ungesetzten Fall an ist.
        var aus = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = false;
            c.TvSubtitlesDownloadEnabled = false;
            c.MovieSubtitlesDownloadEnabled = false;
        });
        Check("everything off yields no endpoint",
            catalog.EnabledDownloads(aus).Count == 0 && catalog.EnabledUploads(aus).Count == 0);

        var nurDownload = Conf(c =>
        {
            c.DownloadEnabled = true;
            c.UploadEnabled = false;
            c.TvSubtitlesDownloadEnabled = false;
            c.MovieSubtitlesDownloadEnabled = false;
        });
        Check("the download switch alone yields exactly the SubDL download endpoint",
            catalog.EnabledDownloads(nurDownload).Count == 1
            && catalog.EnabledDownloads(nurDownload)[0].Provider == SubtitleProvider.Subdl
            && catalog.EnabledUploads(nurDownload).Count == 0);

        var beide = Conf(c => { c.DownloadEnabled = true; c.UploadEnabled = true; });
        // F-M503 (Nutzerentscheid 15.11.2026): der ungesetzte tvst-Download-Schalter zieht die zweite
        // Quelle jetzt herein — das ist der Default, keine Regression. Der Upload bleibt unberuehrt.
        Check("an unset tvsubtitles DOWNLOAD switch now pulls the second source in — the upload switch does not (F-M503)",
            catalog.EnabledDownloads(beide).Count == 2
            && catalog.EnabledDownloads(beide).Any(e => e.Provider == SubtitleProvider.TvSubtitles)
            && catalog.EnabledUploads(beide).All(e => e.Provider == SubtitleProvider.Subdl),
            "downloads=" + catalog.EnabledDownloads(beide).Count + " uploads=" + catalog.EnabledUploads(beide).Count);

        var beideTvstAus = Conf(c =>
        {
            c.DownloadEnabled = true;
            c.UploadEnabled = true;
            c.TvSubtitlesDownloadEnabled = false;
        });
        Check("an explicitly switched-off tvsubtitles download stays out of the run (F-M503)",
            catalog.EnabledDownloads(beideTvstAus).All(e => e.Provider == SubtitleProvider.Subdl)
            && catalog.EnabledUploads(beideTvstAus).All(e => e.Provider == SubtitleProvider.Subdl));

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
        var downloadSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        var uploadSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var orderSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/SourceOrder.cs"));
        Check("the download pipeline asks EVERY enabled source before a language is given up (F-M485/F-M504)",
            // F-M504: die Quellen werden nicht mehr in der Sprachschleife gefragt, sondern VOR dem
            // Ausstieg bei leerer Hauptantwort — und die Schleife laeuft ueber alle Quellen in der
            // eingestellten Reihenfolge, nicht ueber zwei. Die Absicht der Pruefung bleibt: kein
            // Ausstieg, bevor die Quelle gefragt wurde.
            downloadSource.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal)
            // F-M507: der Aufruf traegt zusaetzlich TMDb-Id, Medienpfad und Medien-Hash — die dritte
            // Quelle loest ihren Film ueber TMDb auf und legt die Nummer am Medium ab.
            && downloadSource.Contains(
                "ForeignCandidatesAsync(quelle, title, isSeries, season, episode, lang, tmdbId, mediaPath, mediaHash, ct)",
                StringComparison.Ordinal)
            && downloadSource.Contains("if (candidates.Count == 0 && !fremdProSprache.Values.Any(", StringComparison.Ordinal)
            && downloadSource.IndexOf("ForeignCandidatesAsync(quelle, title", StringComparison.Ordinal)
               < downloadSource.IndexOf("no candidates for [{Langs}]", StringComparison.Ordinal),
            "the sources are asked before the empty exit");
        Check("a candidate is fetched from its own endpoint, not over SubDL's routes",
            downloadSource.Contains("if (cand.Provider != MainSearchSource)", StringComparison.Ordinal)
            && downloadSource.Contains("var endpoint = EndpointFor(cand.Provider);", StringComparison.Ordinal)
            && downloadSource.Contains("Route = Api.Endpoints.Provenance.Name(cand.Provider)", StringComparison.Ordinal));
        Check("the upload pipeline sends to the family address through the same gate, and its switch is asked (F-M485/F-M495/F-M509)",
            uploadSource.Contains("SourceAllowed(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal)
            && uploadSource.Contains("Configuration.SubtitleProvider.TvSubtitles => SendToTvSubtitlesAsync(", StringComparison.Ordinal)
            && uploadSource.Contains("Configuration.SubtitleProvider.MovieSubtitles => SendToMovieSubtitlesAsync(", StringComparison.Ordinal)
            // F-M509: die Bauform „alles ausser SubDL" ist weg — sie haette einen Film zur Serien-Adresse
            // geschickt und keiner dritten Quelle je einen eigenen Weg gelassen.
            && !uploadSource.Contains("SendToSecondSourceAsync(", StringComparison.Ordinal));
        Check("the SubDL upload call itself is untouched — the response body the caller reads stays",
            // F-M509: der Aufruf steht jetzt in der Versandtabelle der Quelle statt im Rumpf der Kette,
            // aber er bekommt dieselben Angaben — Framerate und HI eingeschlossen.
            uploadSource.Contains("Configuration.SubtitleProvider.Subdl => _api.UploadSubtitleAsync(", StringComparison.Ordinal)
            && uploadSource.Contains("srtContent, neutralName, lang, releaseName, quality, imdbId, tmdbId, isSeries, season,", StringComparison.Ordinal)
            && uploadSource.Contains("episode, ct, hearingImpaired, framerate),", StringComparison.Ordinal)
            && downloadSource.Contains("SubDL", StringComparison.Ordinal));
        Check("an additional source never breaks the run — its errors are warnings",
            downloadSource.Contains("[{Source}-D] search failed for", StringComparison.Ordinal));

        // ---- 9. Herkunft und Kreuzkontamination (F-M486/F-M487) -------------------------------
        Check("the store value names the source, and parses back",
            Provenance.Name(SubtitleProvider.Subdl) == "subdl"
            && Provenance.Name(SubtitleProvider.TvSubtitles) == "tvsubtitles"
            && Provenance.Parse("tvsubtitles") == SubtitleProvider.TvSubtitles
            && Provenance.Parse("SubDL") == SubtitleProvider.Subdl
            && Provenance.Parse(null) is null
            && Provenance.Parse("  ") is null);
        // F-M500 (Operator-Anweisung 15.11.2026, verschaerft): eine BEKANNTE Herkunft geht NIRGENDS hoch —
        // nicht zurueck zur eigenen Quelle (Reup) und nicht zur anderen (Kreuzkontamination). Vorher galt
        // "bekannt = nur zur eigenen Quelle"; das ist aufgehoben.
        Check("a tvsubtitles find goes nowhere — not to SubDL and not back to tvsubtitles",
            !Provenance.MayUploadTo("tvsubtitles", SubtitleProvider.Subdl)
            && !Provenance.MayUploadTo("tvsubtitles", SubtitleProvider.TvSubtitles)
            && !Provenance.MayUploadTo("subdl", SubtitleProvider.TvSubtitles)
            && !Provenance.MayUploadTo("subdl", SubtitleProvider.Subdl));
        Check("the origin is named in the reason the run logs",
            Provenance.OriginBlockedReason("subdl") is { } subdlSatz
            && subdlSatz.Contains("origin:subdl", StringComparison.Ordinal)
            && subdlSatz.Contains("never uploaded", StringComparison.Ordinal)
            && Provenance.OriginBlockedReason("tvsubtitles") is { } tvSatz
            && tvSatz.Contains("origin:tvsubtitles", StringComparison.Ordinal)
            && Provenance.OriginBlockedReason(null) is null,
            (Provenance.OriginBlockedReason("subdl") ?? "-") + " | " + (Provenance.OriginBlockedReason("tvsubtitles") ?? "-"));
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
            && uploadSource.Contains("UploadGate.Decide(", StringComparison.Ordinal)
            && uploadSource.Contains("herkunft,", StringComparison.Ordinal));
        Check("the store schema carries the two columns and a migration step",
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/Entities.cs"))
                .Contains("public string? OriginId { get; set; }", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SubdlDbContext.cs"))
                .Contains("ALTER TABLE sidecars ADD COLUMN Origin TEXT NULL;", StringComparison.Ordinal)
            // F-M501: der Marker steht auf 13 (media.TvstShowId kam dazu); die Pruefung fragt den
            // AKTUELLEN Stand, weil ein stehengebliebener Marker die Migration nicht mehr anzeigt.
            // F-M507: er steht inzwischen auf 14 (die Spalten der dritten Quelle).
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SubdlDbContext.cs"))
                .Contains("CurrentSchemaVersion = 14", StringComparison.Ordinal));

        // ---- 10. eigene Kennzeichen je Quelle und Richtung (F-M488) ---------------------------
        var entities = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/Entities.cs"));
        var dbc = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SubdlDbContext.cs"));
        Check("four own fields exist, one per source and direction",
            entities.Contains("public string? SubdlDownloadId { get; set; }", StringComparison.Ordinal)
            && entities.Contains("public string? SubdlUploadId { get; set; }", StringComparison.Ordinal)
            && entities.Contains("public string? TvSubtitlesDownloadId { get; set; }", StringComparison.Ordinal)
            && entities.Contains("public string? TvSubtitlesUploadId { get; set; }", StringComparison.Ordinal));
        Check("schema 11 adds them to sidecars AND embeds",
            dbc.Contains("CurrentSchemaVersion = 14", StringComparison.Ordinal)
            && dbc.Contains("ALTER TABLE sidecars ADD COLUMN TvSubtitlesUploadId TEXT NULL;", StringComparison.Ordinal)
            && dbc.Contains("ALTER TABLE embeds ADD COLUMN TvSubtitlesDownloadId TEXT NULL;", StringComparison.Ordinal));
        Check("the download writes the field of its own source",
            downloadSource.Contains("subdlDownloadId: cand.Provider == Configuration.SubtitleProvider.Subdl ? cand.SubdlId : null", StringComparison.Ordinal)
            && downloadSource.Contains("tvSubtitlesDownloadId: cand.Provider == Configuration.SubtitleProvider.TvSubtitles ? cand.Url : null", StringComparison.Ordinal));
        Check("the upload writes SubDL's id into SubDL's field",
            uploadSource.Contains("subdlUploadId: subdlMarke.Id", StringComparison.Ordinal)
            && uploadSource.Contains("subdlId: uploadId?.ToString(System.Globalization.CultureInfo.InvariantCulture)", StringComparison.Ordinal));
        Check("each family address carries its upload id in its OWN field (F-M508)",
            uploadSource.Contains("tvSubtitlesUploadId: tvstMarke.Id", StringComparison.Ordinal)
            && uploadSource.Contains("moviesubtitlesUploadId: msubMarke.Id", StringComparison.Ordinal)
            // die Marke kommt aus EINER Stelle, die die eigene Id der Quelle nimmt oder eine vergibt
            && uploadSource.Contains("knownId ?? ergebnis.SourceId", StringComparison.Ordinal));
        var ownId = Provenance.OwnUploadId(SubtitleProvider.TvSubtitles, "abc123", "The.Middle.S02E24.720p", new DateTime(2026, 11, 15, 14, 30, 12, DateTimeKind.Utc));
        Check("a missing id is replaced by our own, unmistakably prefixed",
            ownId.StartsWith("own:tvsubtitles:20261115T143012Z:", StringComparison.Ordinal)
            && Provenance.IsOwnUploadId(ownId)
            && !Provenance.IsOwnUploadId("170678"));
        Check("the minted id is reproducible for the same input and differs for another subtitle",
            ownId == Provenance.OwnUploadId(SubtitleProvider.TvSubtitles, "abc123", "The.Middle.S02E24.720p", new DateTime(2026, 11, 15, 14, 30, 12, DateTimeKind.Utc))
            && ownId != Provenance.OwnUploadId(SubtitleProvider.TvSubtitles, "def456", "The.Middle.S02E24.720p", new DateTime(2026, 11, 15, 14, 30, 12, DateTimeKind.Utc)));
        Check("only the source that took the upload gets its mark",
            uploadSource.Contains("subdlUploadId: subdlMarke.Id", StringComparison.Ordinal)
            && uploadSource.Contains("tvSubtitlesUploadId: tvstMarke.Id", StringComparison.Ordinal)
            && uploadSource.Contains("moviesubtitlesUploadId: msubMarke.Id", StringComparison.Ordinal)
            // MarkOf gibt (null, null) fuer eine Quelle, die nicht genommen hat — keine Marke ohne Upload
            && uploadSource.Contains("if (!ergebnisse.TryGetValue(quelle, out var ergebnis) || !ergebnis.Ok)", StringComparison.Ordinal)
            && !uploadSource.Contains("subdlUploadId: vonZweiter", StringComparison.Ordinal));
        Check("the id is read back from the episode page, never invented",
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/TvSubtitlesClient.cs"))
                .Contains("public async Task<string?> FindUploadedIdAsync(", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/UploadResult.cs"))
                .Contains("public string? SourceId { get; init; }", StringComparison.Ordinal));

        // ---- 11. Reup und Kreuzkontamination (F-M489) -----------------------------------------
        var g = UploadGate.Decide(subdlSwitch: true, tvSubtitlesSwitch: true, origin: null, subdlAlready: false, tvSubtitlesAlready: false);
        Check("nothing uploaded yet and both switches on: both sources may run", g.Subdl && g.TvSubtitles && g.SkipReason is null);
        Check("no reup on tvsubtitles: that source already has it",
            UploadGate.Decide(true, true, null, false, true) is { Subdl: true, TvSubtitles: false, SkipReason: null });
        Check("no reup on SubDL: that source already has it",
            UploadGate.Decide(true, true, null, true, false) is { Subdl: false, TvSubtitles: true, SkipReason: null });
        Check("both already have it: nothing runs, and the reason names both",
            UploadGate.Decide(true, true, null, true, true) is { Subdl: false, TvSubtitles: false, SkipReason: "already-uploaded:subdl+tvsubtitles" });
        Check("a SubDL upload does not block tvsubtitles and a tvst upload does not block SubDL",
            UploadGate.Decide(true, true, origin: null, true, false) is { Subdl: false, TvSubtitles: true, SkipReason: null }
            && UploadGate.Decide(true, true, origin: null, false, true) is { Subdl: true, TvSubtitles: false, SkipReason: null }
            && UploadGate.Decide(true, true, origin: null, true, true).SkipReason == "already-uploaded:subdl+tvsubtitles");
        Check("a known origin is refused at BOTH sources, with the origin named as the reason (F-M500)",
            UploadGate.Decide(true, true, "tvsubtitles", false, false) is { Subdl: false, TvSubtitles: false } tvTor
            && tvTor.SkipReason == "origin:tvsubtitles — downloaded there, never uploaded"
            && UploadGate.Decide(true, true, "subdl", false, false) is { Subdl: false, TvSubtitles: false } sdTor
            && sdTor.SkipReason == "origin:subdl — downloaded there, never uploaded");
        Check("switches off mean nothing runs",
            UploadGate.Decide(false, false, null, false, false).SkipReason == "no-upload-source");
        // F-M508: die Film-Adresse der Familie steht im SELBEN Tor — eigener Schalter, eigene Marke,
        // eigene Antwort. Sie ist die dritte Adresse im Tor, aber keine dritte Quelle in der Reihenfolge.
        Check("the film address is decided by the same gate, with its own switch and its own mark (F-M508)",
            UploadGate.Decide(false, false, null, false, false, movieSubtitlesSwitch: true, movieSubtitlesAlready: false)
                is { MovieSubtitles: true, Subdl: false, TvSubtitles: false, SkipReason: null }
            && UploadGate.Decide(true, true, null, false, false, movieSubtitlesSwitch: true, movieSubtitlesAlready: true)
                is { MovieSubtitles: false, SkipReason: null }
            && UploadGate.Decide(true, true, null, true, true, movieSubtitlesSwitch: true, movieSubtitlesAlready: true).SkipReason
                == "already-uploaded:subdl+tvsubtitles+moviesubtitles"
            && UploadGate.Decide(true, true, null, false, false, movieSubtitlesSwitch: true, movieSubtitlesAlready: false).AllowedCount == 3
            && !UploadGate.Decide(true, true, "moviesubtitles", false, false, movieSubtitlesSwitch: true, movieSubtitlesAlready: false).MovieSubtitles,
            "F-M508: eigenes Feld, eigene Frage");
        Check("the pipeline asks per source, through the one gate — twice, never inline",
            uploadSource.Contains("UploadGate.Decide(", StringComparison.Ordinal)
            && uploadSource.Split("UploadGate.Decide(").Length - 1 >= 2
            && uploadSource.Contains("Registry.IsUploadedTo(mediaHash, lang, hearingImpaired, Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal)
            && !uploadSource.Contains("Registry.IsUploaded(mediaHash, cand.Lang", StringComparison.Ordinal));

        // ---- 12. "hochgeladen" je Quelle (F-M490) ---------------------------------------------
        var registrySource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        Check("each source has its own uploaded timestamp, sidecars and embeds alike",
            entities.Contains("public DateTime? SubdlUploadedAt { get; set; }", StringComparison.Ordinal)
            && entities.Contains("public DateTime? TvSubtitlesUploadedAt { get; set; }", StringComparison.Ordinal)
            && dbc.Contains("CurrentSchemaVersion = 14", StringComparison.Ordinal)
            && dbc.Contains("ALTER TABLE sidecars ADD COLUMN SubdlUploadedAt TEXT NULL;", StringComparison.Ordinal)
            && dbc.Contains("ALTER TABLE embeds ADD COLUMN TvSubtitlesUploadedAt TEXT NULL;", StringComparison.Ordinal));
        Check("the timestamp is set for the source that took the upload, never for another",
            uploadSource.Contains("subdlUploadedAt: subdlMarke.At", StringComparison.Ordinal)
            && uploadSource.Contains("tvSubtitlesUploadedAt: tvstMarke.At", StringComparison.Ordinal)
            && uploadSource.Contains("moviesubtitlesUploadedAt: msubMarke.At", StringComparison.Ordinal)
            // EIN Zeitpunkt fuer Id und Stempel — sonst traegt die Marke eine andere Sekunde als der Upload
            && uploadSource.Contains("var jetzt = DateTime.UtcNow;", StringComparison.Ordinal));
        Check("\"uploaded to this source\" is read from its own timestamp first",
            registrySource.Contains("x.TvSubtitlesUploadedAt != null || !string.IsNullOrEmpty(x.TvSubtitlesUploadId)", StringComparison.Ordinal)
            && registrySource.Contains("x.SubdlUploadedAt != null", StringComparison.Ordinal));
        Check("a timestamp is never overwritten with nothing",
            registrySource.Contains("if (subdlUploadedAt.HasValue)", StringComparison.Ordinal)
            && registrySource.Contains("if (tvSubtitlesUploadedAt.HasValue)", StringComparison.Ordinal)
            && registrySource.Contains("if (moviesubtitlesUploadedAt.HasValue)", StringComparison.Ordinal));

        // ---- 13. Reihenfolge und Erfolgsbegriff (F-M491, F-M494) -----------------------------
        Check("the download order is a setting, and tvsubtitles is first by default (operator 15.11.2026)",
            downloadSource.Contains("DownloadOrder", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"))
                .Contains("DownloadSourceOrder.TvSubtitlesFirst;", StringComparison.Ordinal));
        Check("the GUI offers the order under downloads and saves the chosen value",
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"))
                .Contains("id=\"DownloadOrder\"", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"))
                .Contains("config.DownloadOrder =", StringComparison.Ordinal));
        Check("every source is asked and the candidates are MERGED, not replaced (F-M494/F-M504)",
            !downloadSource.Contains("langCands = fremd;", StringComparison.Ordinal)
            && downloadSource.Contains("candidates.AddRange(fremd);", StringComparison.Ordinal)
            && downloadSource.Contains("{Counts}, order {Order}", StringComparison.Ordinal)
            && downloadSource.Contains("langCands.Count(c => c.Provider == quelle)", StringComparison.Ordinal)
            && !downloadSource.Contains("tvZuerst", StringComparison.Ordinal));
        Check("the ranking leads with the source in the query order, pack and score stay behind it (F-M494/F-M504)",
            downloadSource.Contains(".OrderBy(x => QuellenRang(quellenRang, x.Cand.Provider))", StringComparison.Ordinal)
            && downloadSource.Contains(".ThenByDescending(x => x.Pack)", StringComparison.Ordinal));
        Check("a source change restarts the correction hunt instead of ending it (F-M494)",
            downloadSource.Contains("var offeneAndereQuelle = ranked.Skip(attempts).Where(x => x.Cand.Provider != cand.Provider)", StringComparison.Ordinal)
            && downloadSource.Contains("correctionHuntAbandoned = false;", StringComparison.Ordinal)
            && downloadSource.Contains("refusalsThisRun = 0;", StringComparison.Ordinal));
        Check("the fetch path asks the candidate's own source switch, through ONE helper, before the fetch (F-M494)",
            downloadSource.Contains("private bool SourceAllowed(Configuration.SubtitleProvider provider) => _config.IsDownloadEnabled(provider);", StringComparison.Ordinal)
            && downloadSource.Contains("if (!SourceAllowed(cand.Provider))", StringComparison.Ordinal)
            && downloadSource.Contains("if (!SourceAllowed(hi.Provider))", StringComparison.Ordinal)
            && downloadSource.Contains(".Where(SourceAllowed)", StringComparison.Ordinal)
            && downloadSource.Contains("candidate from {Source} skipped: that source is switched off", StringComparison.Ordinal)
            && !downloadSource.Contains("if (_config.IsDownloadEnabled(Configuration.SubtitleProvider.TvSubtitles))", StringComparison.Ordinal));
        Check("a kept original is a download but not a success while auto-sync is on",
            downloadSource.Contains("if (!_config.QaDownloadAutoSync || alignedForNaming)", StringComparison.Ordinal)
            && downloadSource.Contains("summary.DownloadedWithoutSignificance++;", StringComparison.Ordinal));

        // ---- 14. Quellenabhaengige Vorpruefungen und Herkunft an jeder Ablage (F-M492/F-M493) -----
        var cfgSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"));
        var dispatcherSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/SubdlEventDispatcher.cs"));
        Check("the only demanded credential is the TMDb key — SubDL's pair is a source question (F-M492/F-M497)",
            cfgSource.Contains("if (string.IsNullOrWhiteSpace(TmdbApiKey))", StringComparison.Ordinal)
            && !cfgSource.Contains("missing.Add(\"SubDL API key\")", StringComparison.Ordinal)
            && !cfgSource.Contains("missing.Add(\"SubDL email\")", StringComparison.Ordinal)
            && !cfgSource.Contains("missing.Add(\"SubDL password\")", StringComparison.Ordinal)
            && cfgSource.Contains("public bool IsConfiguredForUpload(SubtitleProvider provider)", StringComparison.Ordinal)
            && cfgSource.Contains("public bool IsConfiguredForDownload(SubtitleProvider provider)", StringComparison.Ordinal));
        Check("a cycle that does not ask SubDL is not held by the SubDL counters",
            dispatcherSource.Contains("if (!config.IsDownloadEnabled(Configuration.SubtitleProvider.Subdl))", StringComparison.Ordinal));
        Check("a spent SubDL quota does not hold a cycle that still has tvsubtitles",
            dispatcherSource.Contains("secondSourceCanRun", StringComparison.Ordinal)
            && dispatcherSource.Contains("this cycle runs on tvsubtitles.net only (F-M492)", StringComparison.Ordinal));
        var markCalls = System.Text.RegularExpressions.Regex.Matches(downloadSource, @"MarkDownloaded\(").Count;
        var originArgs = System.Text.RegularExpressions.Regex.Matches(downloadSource, @"origin: Api\.Endpoints\.Provenance\.Name\(").Count;
        Check($"every MarkDownloaded call carries its origin ({originArgs}/{markCalls})",
            markCalls >= 4 && originArgs == markCalls);
        Check("the run-end line names the downloads that did not pass the significance",
            downloadSource.Contains("{NotSignificant} downloaded without significance", StringComparison.Ordinal));
        var taskSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/SubdlDownloadTask.cs"));
        var offStateSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/WorkerOffState.cs"));
        Check("the download task asks whether ANY source downloads, not whether SubDL does",
            taskSource.Contains("if (!config.AnyDownloadEnabled)", StringComparison.Ordinal)
            && !taskSource.Contains("IsDownloadEnabled(SubtitleProvider.Subdl)", StringComparison.Ordinal));
        var seederSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/SubdlSeeder.cs"));
        Check("the queue fills for the direction, not for SubDL — a spent SubDL allowance does not empty it",
            seederSource.Contains("bool downloadQueueable = config.AnyDownloadEnabled", StringComparison.Ordinal)
            && seederSource.Contains("(downloadAllowed || config.IsDownloadEnabled(SubtitleProvider.TvSubtitles))", StringComparison.Ordinal)
            && seederSource.Contains("config.AnyUploadEnabled && IsUploadTodo", StringComparison.Ordinal));
        Check("the off-state of a direction is asked for the direction, not for SubDL",
            offStateSource.Contains("return !config.AnyDownloadEnabled", StringComparison.Ordinal)
            && offStateSource.Contains("return !config.AnyUploadEnabled", StringComparison.Ordinal));

        // ---- 15. Upload: beide Endpunkte hintereinander (F-M495) --------------------------------
        Check("the upload walks the sources of the configured order — one loop, one result per source (F-M495/F-M509)",
            uploadSource.Contains("var quellenReihenfolge = UploadSourceOrder(isSeries);", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.SourceOrder.ForMedia(_config.DownloadOrder, art)", StringComparison.Ordinal)
            && orderSource.Contains("PluginConfiguration.DownloadSourceOrder.SubdlFirst", StringComparison.Ordinal)
            && uploadSource.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal)
            && uploadSource.Contains("var ergebnisse = new Dictionary<Configuration.SubtitleProvider, UploadResult>();", StringComparison.Ordinal)
            && uploadSource.Contains("ergebnisse[quelle] = await SendToSourceAsync(", StringComparison.Ordinal)
            && uploadSource.Contains("bool subdlGenommen = Genommen(Configuration.SubtitleProvider.Subdl);", StringComparison.Ordinal)
            && uploadSource.Contains("bool zweiteGenommen = Genommen(Configuration.SubtitleProvider.TvSubtitles);", StringComparison.Ordinal)
            && uploadSource.Contains("bool dritteGenommen = Genommen(Configuration.SubtitleProvider.MovieSubtitles);", StringComparison.Ordinal)
            && !uploadSource.Contains("var vonZweiter = false;", StringComparison.Ordinal));
        Check("each endpoint's switch is asked right before its start — through ONE helper, skip instead of break (F-M495)",
            uploadSource.Contains("private bool SourceAllowed(Configuration.SubtitleProvider provider) => _config.IsUploadEnabled(provider);", StringComparison.Ordinal)
            && uploadSource.Contains("if (!SourceAllowed(quelle))", StringComparison.Ordinal)
            && uploadSource.Contains("source {Source} skipped: that source is switched off, nothing sent", StringComparison.Ordinal)
            && !uploadSource.Contains("_config.IsUploadEnabled(Configuration.SubtitleProvider.Subdl)", StringComparison.Ordinal)
            && !uploadSource.Contains("_config.IsUploadEnabled(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal));
        Check("the provenance gate still decides PER SOURCE — a refused source is skipped, the chain runs on (F-M495/F-M508)",
            uploadSource.Contains("if (!Api.Endpoints.UploadGate.May(gate, quelle))", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.Provenance.OriginBlockedReason(herkunft)", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.Provenance.MayUploadTo(herkunft, quelle)", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.UploadGate.Decide(", StringComparison.Ordinal));

        // ---- 16. Trockenlauf: eine Zeile je Quelle, keine Zugangsdaten (F-M496) -----------------
        // Der Trockenlauf stand als EIN Ausstieg vor der Kette: eine Zeile fuer zwei Endpunkte, und
        // MissingCredentials verlangte fuer ihn Benutzername und Passwort, obwohl er keinen einzigen
        // Aufruf macht. Beides ist gemessen (jf-test, 15.11.2026) und beides wird hier festgehalten.
        var trockenOhneLogin = Conf(c =>
        {
            c.DryRun = true;
            c.UploadEnabled = true;
            c.SubdlUploadEnabled = true;
            c.TvSubtitlesUploadEnabled = true;
            c.Username = string.Empty;
            c.Password = string.Empty;
        });
        Check("a dry run is not refused for missing SubDL credentials (F-M496)",
            trockenOhneLogin.MissingCredentials().Count == 0
            && uploadSource.Contains("if (!_config.DryRun && !_config.IsConfiguredForUpload(quelle))", StringComparison.Ordinal),
            "missing=" + string.Join(",", trockenOhneLogin.MissingCredentials()));

        var dryLineTvst = uploadSource.IndexOf("DryRunLine(quelle)", StringComparison.Ordinal);
        var dryBlockStart = uploadSource.IndexOf("if (_config.DryRun)", StringComparison.Ordinal);
        var secondSourceCall = uploadSource.IndexOf("await SendToSourceAsync(", StringComparison.Ordinal);
        Check("the tvst branch has its own dry-run guard BEFORE its call — one line per source (F-M496)",
            dryLineTvst > 0 && dryBlockStart > 0 && secondSourceCall > 0
            && dryLineTvst > dryBlockStart
            && dryLineTvst < secondSourceCall
            && uploadSource.Contains("\"[\" + Api.Endpoints.Provenance.Name(quelle) + \"] DRY-RUN would upload {Name} [{Lang}]\"", StringComparison.Ordinal)
            && uploadSource.Contains("foreach (var quelle in UploadSourceOrder(isSeries))", StringComparison.Ordinal)
            && uploadSource.Contains("if (!SourceAllowed(quelle))", StringComparison.Ordinal)
            && uploadSource.Contains("TvSubtitlesUploadEndpoint(_tvstClient, dryRun: _config.DryRun)", StringComparison.Ordinal),
            "tvst line @" + dryLineTvst + ", dry block @" + dryBlockStart + ", call @" + secondSourceCall);

        // ---- 17. TMDb ist die einzige Pflicht, SubDL faellt ohne Konfiguration aus der Kette (F-M497) --
        var ohneTmdb = Conf(c => { c.TmdbApiKey = string.Empty; });
        var ohneSubdlKonfig = Conf(c =>
        {
            c.UploadEnabled = true;
            c.DownloadEnabled = true;
            c.Username = string.Empty;
            c.Password = string.Empty;
            c.ApiKey = string.Empty;
        });
        Check("missing TMDb blocks the run, missing SubDL configuration does not (F-M497)",
            ohneTmdb.MissingCredentials().Contains("TMDb API key (v3)")
            && ohneSubdlKonfig.MissingCredentials().Count == 0,
            "ohne Tmdb=" + string.Join(",", ohneTmdb.MissingCredentials())
            + " ohne SubDL=" + string.Join(",", ohneSubdlKonfig.MissingCredentials()));

        Check("SubDL without its credentials steps aside instead of blocking — both directions (F-M497)",
            !ohneSubdlKonfig.IsConfiguredForUpload(SubtitleProvider.Subdl)
            && ohneSubdlKonfig.IsConfiguredForUpload(SubtitleProvider.TvSubtitles)
            && !ohneSubdlKonfig.IsConfiguredForDownload(SubtitleProvider.Subdl)
            && ohneSubdlKonfig.IsConfiguredForDownload(SubtitleProvider.TvSubtitles)
            && uploadSource.Contains(
                "\"[SubDL] {Name} [{Lang}] — source {Source} skipped — not configured (required for SubDL only)\"",
                StringComparison.Ordinal)
            && downloadSource.Contains(
                "if (!_config.IsConfiguredForDownload(Configuration.SubtitleProvider.Subdl))", StringComparison.Ordinal)
            && downloadSource.Contains(
                "\"[SubDL-D] {File} — SubDL skipped — not configured (required for SubDL only)\"", StringComparison.Ordinal));

        // --- Abschnitt 16: Rip-Zuordnung fuer den tvst-Upload (F-M498) ---
        // Gemessen 15.11.2026 im scharfen Lauf: die Seite lehnt jede Folge mit "rip must be one of
        // WEB, HDTV, HEVC, DVDRip, DVDSCR, BDRip, BluRay" ab, weil der Release-Name "WEB-DL" sagt.
        var tvClientSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/TvSubtitlesClient.cs"));
        Check("der Release-Name wird auf einen der sieben Rip-Werte gezogen (F-M498)",
            tvClientSource.Contains("public static string RipFromQuality(string? quality)", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"WEB\";", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"HEVC\";", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"BDRip\";", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"BluRay\";", StringComparison.Ordinal));

        Check("der Endpunkt bekommt den Rip durch die Zuordnung, nicht den rohen N amen (F-M498)",
            uploadSource.Contains("TvSubtitlesClient.RipFromQuality(quality)", StringComparison.Ordinal));

        Check("das Hearing-impaired-Kaestchen wird gesendet, wenn der Untertitel es ist (F-M498)",
            tvClientSource.Contains("new KeyValuePair<string, string>(\"hi\", \"1\")", StringComparison.Ordinal)
            && tvClientSource.Contains("if (hearingImpaired)", StringComparison.Ordinal));

        Check("die Fehlermeldung nennt nicht mehr den falschen Parameter (F-M498)",
            !tvClientSource.Contains("nameof(showId));", StringComparison.Ordinal));

        // --- Abschnitt 17: die Upload-Queue fragt je Quelle, nicht quellenblind (F-M499) ---
        // Gemessen 15.11.2026, 16:04: nach 27 SubDL-Uploads reihte der Seeder 0 Folgen ein
        // ("scan finished (upload) — 0 upload queued"), weil "schon hochgeladen" aus Status=Uploaded
        // gelesen wurde — also "irgendwo hochgeladen". Der zweite Endpunkt bekam nie seinen Versuch.
        Check("die Upload-Queue fragt je Quelle statt quellenblind (F-M499)",
            seederSource.Contains("UploadTodoFor(item, mediaPath, config, quelle)", StringComparison.Ordinal)
            && seederSource.Contains("registry.ArePositionsTerminalFor(mediaHash", StringComparison.Ordinal)
            && seederSource.Contains("registry.CountKnownPairsFor(mediaHash, quelle)", StringComparison.Ordinal)
            && seederSource.Contains("registry.IsSidecarUploadedTo(looseHash, quelle)", StringComparison.Ordinal)
            && !seederSource.Contains("registry.ArePositionsTerminal(mediaHash", StringComparison.Ordinal)
            && !seederSource.Contains("registry.CountKnownPairs(mediaHash)", StringComparison.Ordinal)
            && !seederSource.Contains("registry.IsSidecarUploaded(looseHash)", StringComparison.Ordinal));

        Check("der Helfer nimmt nur eingeschaltete UND konfigurierte Quellen, und die Marke liest EINE Regel (F-M499)",
            seederSource.Contains("foreach (var quelle in UploadSources(config, isSeries))", StringComparison.Ordinal)
            && seederSource.Contains(
                "if (config.IsUploadEnabled(quelle) && config.IsConfiguredForUpload(quelle))",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "public bool ArePositionsTerminalFor(string? mediaHash, IEnumerable<int> positions, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "public int CountKnownPairsFor(string? mediaHash, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "public bool IsSidecarUploadedTo(string? contentHash, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "return IsUploadedTo(mediaHash, track.Language, track.HearingImpaired ?? false, provider);",
                StringComparison.Ordinal));

        // --- Abschnitt 18: auch der KOPF des Laufs fragt je Quelle (F-M499) ---------------------
        // Gemessen 15.11.2026, 16:17 im scharfen Lauf: der Seeder hatte nach Abschnitt 17 27 Folgen
        // quellenweise eingereiht, und die Pipeline warf sie alle mit "[SubDL] SKIP {File} — upload
        // file-complete" wieder weg — die Dateimarke stand von den SubDL-Uploads. Ergebnis "0 uploaded".
        Check("die Pipeline ueberspringt nur, wenn ALLE eingeschalteten und konfigurierten Quellen fertig sind (F-M499)",
            uploadSource.Contains("Registry.IsSubtitlesUploadedFor(earlyHash, quelle)", StringComparison.Ordinal)
            && uploadSource.Contains("if (quellen.Count > 0 && offeneQuellen.Count == 0)", StringComparison.Ordinal)
            && uploadSource.Contains(
                "private List<Configuration.SubtitleProvider> UploadSources(bool? isSeries) =>", StringComparison.Ordinal)
            && uploadSource.Contains(
                "_config.DryRun || _config.IsConfiguredForUpload(quelle)", StringComparison.Ordinal)
            // Die blinde Frage darf im Kopf des Laufs nicht mehr stehen — sie war der Defekt.
            && !uploadSource.Contains("IsSubtitlesUploaded(", StringComparison.Ordinal));

        Check("das quellenweise Gegenstueck der Dateimarke existiert und filtert auf die Quelle (F-M499)",
            registrySource.Contains(
                "public bool IsSubtitlesUploadedFor(string? mediaHash, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "tracks.All(track => IsEmbedTerminalFor(mediaHash, track.SubPos, provider))",
                StringComparison.Ordinal)
            // dieselbe Leseregel wie im Upload selbst: eigene Marke der Quelle, Ablehnung quellenneutral
            && registrySource.Contains(
                "return IsUploadedTo(mediaHash, track.Language, track.HearingImpaired ?? false, provider);",
                StringComparison.Ordinal));

        // --- Abschnitt 19: auch das SETTLE-Urteil fragt je Quelle (F-M500) ----------------------
        // Gemessen 15.11.2026, 16:24 im scharfen Lauf: 27 eingebettete FR-Spuren endeten als
        // "identical subtitle already uploaded earlier (F-M298)", weil der Inhalt bei SubDL lag —
        // tvsubtitles bekam nie einen Versuch, der Lauf meldete "0 uploaded | 34 rejected". Das Urteil
        // "schon hochgeladen" las Status=Uploaded und damit "irgendwo hochgeladen".
        Check("das Settle-Urteil nimmt die Quelle mit — vier Stellen, EINE Regel (F-M500)",
            registrySource.Contains("public bool IsContentUploadedFor(string? contentHash, SubtitleProvider provider)", StringComparison.Ordinal)
            && registrySource.Contains("public bool RecordSkippedContent(string? mediaHash, bool isLoose, int subPos, string lang,", StringComparison.Ordinal)
            && registrySource.Contains("IEnumerable<SubtitleProvider> settledSources,", StringComparison.Ordinal)
            && registrySource.Contains("Where(quelle => IsContentUploadedFor(contentHash, quelle))", StringComparison.Ordinal)
            && uploadSource.Contains("private (ContentSettle Verdict, string Open) SettleDuplicateContent(", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(uploadSource, @"SettleDuplicateContent\(").Count >= 5
            // die Zeile wird nur noch aus der EINEN Regel heraus geschrieben (zwei Aufrufe: Ablehnung/Settle)
            && System.Text.RegularExpressions.Regex.Matches(uploadSource, @"Registry\.RecordSkippedContent\(").Count == 2);

        Check("nur der Quellenbeleg schreibt die Marke — tvst zaehlt keine markenlose Zeile (F-M500)",
            registrySource.Contains("settledAsUploaded && quellen.Contains(SubtitleProvider.Subdl) ? DateTime.UtcNow : null", StringComparison.Ordinal)
            && registrySource.Contains("settledAsUploaded && quellen.Contains(SubtitleProvider.TvSubtitles) ? DateTime.UtcNow : null", StringComparison.Ordinal)
            // fuer tvsubtitles NUR seine eigene Marke: die markenlose Alt-/Pack-Zeile zaehlt nur bei SubDL
            && System.Text.RegularExpressions.Regex.Matches(registrySource, @"UploadedWithoutSourceMark\(x\)").Count == 2);

        // Der Umbau wird nicht nur gelesen, sondern gefahren: ein Inhalt bei SubDL darf tvst nicht
        // erledigen, zweimal bei derselben Quelle bleibt blockiert, und die Herkunft bleibt das Tor.
        string settleRoot = Path.Combine(Path.GetTempPath(), "endpoints-settle-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(settleRoot))
            {
                Directory.Delete(settleRoot, true);
            }

            Directory.CreateDirectory(settleRoot);
            using (var settleDb = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(Path.Combine(settleRoot, "db"), null))
            {
                var settleReg = new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(settleDb, null, null);

                const string fileA = "aaaa1111bbbb2222";
                const string fileB = "cccc3333dddd4444";
                const string de = "DE";
                string textDe = "1\n00:00:01,000 --> 00:00:03,000\nEin Satz.\n\n";
                string hashDe = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(textDe);

                // Der Inhalt liegt bei SubDL (Marke der Quelle, F-M490), unter ANDERER Dateiidentitaet.
                settleReg.EnsureMedia(fileB, "tt7654321", "/data/movies2/film.mkv");
                settleReg.MarkEmbed(fileB, 0, de, false, Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Uploaded,
                    contentHash: hashDe, subdlUploadedAt: DateTime.UtcNow);
                settleReg.EnsureMedia(fileA, "tt1234567", "/data/movies2/folge.mkv");

                Check("derselbe Inhalt bei SubDL erledigt tvsubtitles NICHT (F-M500)",
                    settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.Subdl)
                    && !settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.TvSubtitles)
                    && settleReg.IsContentUploadedForAnotherSource(hashDe, SubtitleProvider.TvSubtitles),
                    "subdl=" + settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.Subdl)
                    + " tvst=" + settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.TvSubtitles));

                // Zweimal bei DERSELBEN Quelle bleibt blockiert: der zweite SubDL-Versuch wird wieder
                // als "schon hochgeladen" verbucht und schreibt KEINE Ablehnung.
                bool settleSubdl = settleReg.RecordSkippedContent(fileA, false, 7, de, false, false, hashDe,
                    Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateContent,
                    new[] { SubtitleProvider.Subdl });
                bool settleTwice = settleReg.RecordSkippedContent(fileA, false, 7, de, false, false, hashDe,
                    Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateContent,
                    new[] { SubtitleProvider.Subdl });
                var rowA = settleReg.GetEmbed(fileA, 7);
                Check("zweimal bei derselben Quelle bleibt blockiert — ohne Ablehnung (F-M500)",
                    settleSubdl && settleTwice
                    && rowA?.Status == Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Uploaded
                    && rowA?.Reason is null
                    && settleReg.IsUploadedTo(fileA, de, false, SubtitleProvider.Subdl)
                    // die andere Quelle ist auf DIESER Spur weiterhin offen
                    && !settleReg.IsUploadedTo(fileA, de, false, SubtitleProvider.TvSubtitles),
                    "settled=" + settleSubdl + "/" + settleTwice + " status=" + rowA?.Status + " grund=" + (rowA?.Reason ?? "-"));

                // Ohne Quellenbeleg (nur eine Ablehnung im Bestand) bleibt es bei der Ablehnung, und die
                // ist quellenneutral: sie haelt BEIDE Quellen — wie festgelegt (F-M499).
                string textFr = "1\n00:00:04,000 --> 00:00:06,000\nUne phrase.\n\n";
                string hashFr = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(textFr);
                settleReg.MarkEmbed(fileA, 8, "FR", false, Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Rejected,
                    contentHash: hashFr, reason: Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateRemote);
                bool noEvidence = settleReg.RecordSkippedContent(fileA, false, 8, "FR", false, true, hashFr,
                    Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateContent,
                    Array.Empty<SubtitleProvider>());
                var rowFr = settleReg.GetEmbed(fileA, 8);
                Check("ohne Quellenbeleg bleibt die Ablehnung quellenneutral (F-M499/F-M500)",
                    !noEvidence
                    && rowFr?.Status == Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Rejected
                    && rowFr?.Reason == Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateContent);

                // Das Herkunftstor steht VOR diesem Weg und wird hier nicht weicher. Drei Faelle, wie
                // festgelegt (Operator 15.11.2026: "externe Quellen duerfen zu beiden hochgeladen werden"):
                // unbekannt/extern -> beide, subdl -> nur SubDL, tvsubtitles -> nur tvsubtitles.
                var torUnbekannt = UploadGate.Decide(true, true, origin: null, false, false);
                var markenText = "1\n00:00:20,000 --> 00:00:22,000\nZwei Marken.\n\n";
                string markenHash = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(markenText);
                settleReg.EnsureMedia("eeee5555ffff6666", "tt9999999", "/data/movies2/beide.mkv");
                settleReg.MarkEmbed("eeee5555ffff6666", 0, de, false,
                    Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Uploaded, contentHash: markenHash,
                    subdlUploadId: "sd-1", subdlUploadedAt: DateTime.UtcNow);
                settleReg.MarkEmbed("eeee5555ffff6666", 0, de, false,
                    Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Uploaded, contentHash: markenHash,
                    tvSubtitlesUploadId: "tv-1", tvSubtitlesUploadedAt: DateTime.UtcNow);
                var beideRow = settleReg.GetEmbed("eeee5555ffff6666", 0);
                Check("unbekannte/externe Herkunft: BEIDE Quellen duerfen hochladen, jede mit eigener Marke (F-M487/F-M500)",
                    torUnbekannt.Subdl && torUnbekannt.TvSubtitles && torUnbekannt.SkipReason is null
                    && Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(null, SubtitleProvider.Subdl)
                    && Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(null, SubtitleProvider.TvSubtitles)
                    // beide Marken stehen nebeneinander auf DERSELBEN Zeile — die Kette schreibt je Quelle
                    && beideRow?.SubdlUploadId == "sd-1" && beideRow?.TvSubtitlesUploadId == "tv-1"
                    && settleReg.IsUploadedTo("eeee5555ffff6666", de, false, SubtitleProvider.Subdl)
                    && settleReg.IsUploadedTo("eeee5555ffff6666", de, false, SubtitleProvider.TvSubtitles)
                    && uploadSource.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal),
                    "subdl=" + torUnbekannt.Subdl + " tvst=" + torUnbekannt.TvSubtitles);

                // Gesperrte Herkuenfte: ein Download ist ein Fund, kein eigener Kandidat — er geht NIRGENDS
                // hoch (Operator 15.11.2026: "Herkunft subdl: nie zurueck zu subdl, nie zu tvst").
                string textHer = "1\n00:00:09,000 --> 00:00:11,000\nHerkunft.\n\n";
                string hashHer = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(textHer);
                settleReg.MarkDownloaded(hashHer, fileA, de, false,
                    origin: Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.Name(SubtitleProvider.Subdl),
                    fileName: "folge.de.srt", path: "/data/movies2/folge.de.srt");
                string? herkunft = settleReg.GetOrigin(hashHer).Origin;
                var torSubdl = UploadGate.Decide(true, true, herkunft, false, false);
                Check("Herkunft=subdl: weder zu SubDL (kein Reup) noch zu tvsubtitles (F-M487 verschaerft)",
                    herkunft == "subdl"
                    && !torSubdl.Subdl && !torSubdl.TvSubtitles
                    && torSubdl.SkipReason == "origin:subdl — downloaded there, never uploaded"
                    && !Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(herkunft, SubtitleProvider.Subdl)
                    && !Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(herkunft, SubtitleProvider.TvSubtitles)
                    && !settleReg.IsContentUploadedFor(hashHer, SubtitleProvider.TvSubtitles)
                    // der Settle-Weg fragt genau dieses Tor, bevor er einen Kandidaten weiterlaufen laesst
                    && uploadSource.Contains("Api.Endpoints.Provenance.MayUploadTo(herkunft, quelle)", StringComparison.Ordinal)
                    && uploadSource.Contains("string? herkunft = Registry.GetOrigin(contentHash).Origin;", StringComparison.Ordinal),
                    "herkunft=" + (herkunft ?? "-") + " subdl=" + torSubdl.Subdl + " tvst=" + torSubdl.TvSubtitles);

                string textHer2 = "1\n00:00:12,000 --> 00:00:14,000\nAndere Herkunft.\n\n";
                string hashHer2 = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(textHer2);
                settleReg.MarkDownloaded(hashHer2, fileA, de, false,
                    origin: Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.Name(SubtitleProvider.TvSubtitles),
                    fileName: "folge2.de.srt", path: "/data/movies2/folge2.de.srt");
                string? herkunftTv = settleReg.GetOrigin(hashHer2).Origin;
                var torTv = UploadGate.Decide(true, true, herkunftTv, false, false);
                Check("Herkunft=tvsubtitles: weder zu tvsubtitles (kein Reup) noch zu SubDL (F-M487 verschaerft)",
                    herkunftTv == "tvsubtitles"
                    && !torTv.Subdl && !torTv.TvSubtitles
                    && torTv.SkipReason == "origin:tvsubtitles — downloaded there, never uploaded"
                    && !Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(herkunftTv, SubtitleProvider.Subdl)
                    && !Jellyfin.Plugin.SubdlScribe.Api.Endpoints.Provenance.MayUploadTo(herkunftTv, SubtitleProvider.TvSubtitles)
                    && !settleReg.IsContentUploadedFor(hashHer2, SubtitleProvider.Subdl)
                    && !settleReg.IsContentUploadedFor(hashHer2, SubtitleProvider.TvSubtitles),
                    "herkunft=" + (herkunftTv ?? "-") + " subdl=" + torTv.Subdl + " tvst=" + torTv.TvSubtitles);

                // Ein bekannter Fund ist keine Upload-Arbeit mehr: die Spur ist fuer JEDE Quelle erledigt,
                // sonst reihte der Seeder sie in jedem Lauf erneut ein (Operator: nie zurueck, nie hinueber).
                settleReg.MarkEmbed(fileA, 9, "IT", false,
                    Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Observed, contentHash: hashHer);
                string textFrei = "1\n00:00:30,000 --> 00:00:32,000\nOhne Herkunft.\n\n";
                string hashFrei = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(textFrei);
                settleReg.MarkEmbed(fileA, 10, "IT", false,
                    Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Observed, contentHash: hashFrei);
                Check("ein Download ist fuer BEIDE Quellen erledigt — die Spur bleibt nicht ewig offen (F-M500)",
                    settleReg.IsEmbedTerminalFor(fileA, 9, SubtitleProvider.Subdl)
                    && settleReg.IsEmbedTerminalFor(fileA, 9, SubtitleProvider.TvSubtitles)
                    // Negativkontrolle: ohne bekannte Herkunft ist die Spur NICHT terminal — die Regel greift nur dort
                    && !settleReg.IsEmbedTerminalFor(fileA, 10, SubtitleProvider.Subdl)
                    && !settleReg.IsEmbedTerminalFor(fileA, 10, SubtitleProvider.TvSubtitles)
                    && seederSource.Contains("if (registry.GetOrigin(looseHash).Origin is not null)", StringComparison.Ordinal));

                // Die Queue fragt dieselbe Sache je Quelle (F-M500 im Seeder): ein Upload bei der anderen
                // Quelle haelt die zweite nicht mehr aus der Queue.
                Check("die Queue liest \"bekannt\" nicht mehr quellenblind (F-M500)",
                    seederSource.Contains("registry.IsContentUploadedForAnotherSource(contentHashProbe, quelle)", StringComparison.Ordinal)
                    && !seederSource.Contains("if (registry.IsContentKnown(Registry.ContentHashRegistry.ComputeHash(content)))", StringComparison.Ordinal));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(settleRoot))
                {
                    Directory.Delete(settleRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // --- Abschnitt 20: die tvst-Seriennummer im Store und die Anlage fehlender Serien (F-M501) ---
        // Operator-Anweisung 15.11.2026: "Tmdb titel fuehrt.", "Tmdb titel fuehrt auch fuer
        // Doppeleintraege.", nachgeschaerft "Doch Aehnlichkeitssuche. Aber gross/Kleinschreibung. And =
        // und. Leerzeichen mehr oder weniger. Aehnlichkeit eng halten". Also: Name normalisieren und
        // dann auf GLEICHHEIT vergleichen — kein Teilstring, kein Aehnlichkeitsmass, kein
        // Artikel-Abschneiden.
        var namen = new Dictionary<int, string>
        {
            [512] = "The Middle",
            [422] = "Malcolm in the Middle",
            [701] = "The Middle Man",
            [700] = "Rick & Morty",
        };
        Check("tvst vergleicht eng: Gross/Kleinschreibung und Leerraum zaehlen nicht (F-M501)",
            TvSubtitlesClient.ResolveShow(namen, "the middle") == 512
            && TvSubtitlesClient.ResolveShow(namen, "  The   Middle ") == 512
            && TvSubtitlesClient.NormalizeShowName("The   Middle") == "the middle",
            "the middle=" + TvSubtitlesClient.ResolveShow(namen, "the middle")
            + " doppelraum=" + TvSubtitlesClient.ResolveShow(namen, "  The   Middle "));

        Check("tvst vergleicht eng: & = and = und (F-M501)",
            TvSubtitlesClient.ResolveShow(namen, "Rick and Morty") == 700
            && TvSubtitlesClient.ResolveShow(namen, "Rick und Morty") == 700
            && TvSubtitlesClient.ResolveShow(namen, "rick&morty") == 700
            // "und" nur als Wort: aus "Rundschau" darf kein "Randschau" werden
            && TvSubtitlesClient.NormalizeShowName("Rundschau") == "rundschau",
            "and=" + TvSubtitlesClient.ResolveShow(namen, "Rick and Morty"));

        Check("tvst vergleicht eng: kein Teilstring, kein Artikel-Abschneiden (F-M501)",
            // "The Middle" findet NICHT "The Middle Man" und NICHT "Malcolm in the Middle"
            TvSubtitlesClient.ResolveShow(namen, "The Middle") == 512
            // die fruehere "enthaelt"-Stufe haette hier 701 geliefert
            && TvSubtitlesClient.ResolveShow(namen, "Middle") is null
            && TvSubtitlesClient.ResolveShow(namen, "Middle Man") is null
            && TvSubtitlesClient.ResolveShow(namen, "the middle man") == 701,
            "Middle=" + (TvSubtitlesClient.ResolveShow(namen, "Middle")?.ToString() ?? "-"));

        // Der TMDb-Serien-Datensatz, auf dem die Aufloesung beruht (Titel, Jahr, Staffelzahl).
        var tmdbInfo = await new TmdbImdbResolver(
                new HttpClient(new JsonHandler(
                    "{\"name\":\"The Middle\",\"original_name\":\"The Middle\"," +
                    "\"first_air_date\":\"2009-09-30\",\"number_of_seasons\":9}")),
                "test-key")
            .SeriesInfoAsync("999001", CancellationToken.None).ConfigureAwait(false);
        Check("der TMDb-Serien-Datensatz traegt Titel, Erstausstrahlungsjahr und Staffelzahl (F-M501)",
            tmdbInfo is { Name: "The Middle", FirstAirYear: 2009, Seasons: 9 },
            tmdbInfo is null ? "kein Datensatz" : tmdbInfo.Name + "/" + tmdbInfo.FirstAirYear + "/" + tmdbInfo.Seasons);

        string tvstRoot = Path.Combine(Path.GetTempPath(), "endpoints-tvst-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(tvstRoot))
            {
                Directory.Delete(tvstRoot, true);
            }

            Directory.CreateDirectory(tvstRoot);
            using (var tvstDb = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(Path.Combine(tvstRoot, "db"), null))
            {
                var tvstReg = new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(tvstDb, null, null);
                var mitte = new TmdbSeriesInfo("The Middle", "The Middle", 2009, 9);

                // (a) Die gespeicherte Nummer fuehrt: kein Netz, kein Vergleich, keine Anlage.
                tvstReg.EnsureMedia("aaaa000000000001", patch: m => m.TvstShowId = "512");
                var handlerGespeichert = new TvstSiteHandler(512, "The Middle");
                var aufloeserGespeichert = new TvstShowResolver(
                    new TvSubtitlesClient(new HttpClient(handlerGespeichert)));
                var gespeichert = await aufloeserGespeichert
                    .ResolveForMediaAsync(tvstReg, "aaaa000000000001", mitte, dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("eine gespeicherte TvstShowId verhindert Suche UND Anlage (F-M501)",
                    gespeichert.Resolved && gespeichert.ShowId == 512
                    && gespeichert.Status == TvstShowStatus.Stored
                    && handlerGespeichert.Gets == 0 && handlerGespeichert.Posts == 0,
                    "id=" + gespeichert.ShowId + " status=" + gespeichert.Status
                    + " gets=" + handlerGespeichert.Gets + " posts=" + handlerGespeichert.Posts);

                // (b) Ein Treffer in der Liste: die Nummer wird geschrieben und benutzt, ohne Anlage.
                var handlerVorhanden = new TvstSiteHandler(512, "The Middle");
                var aufloeserVorhanden = new TvstShowResolver(
                    new TvSubtitlesClient(new HttpClient(handlerVorhanden)));
                var vorhanden = await aufloeserVorhanden
                    .ResolveForMediaAsync(tvstReg, "aaaa000000000002", mitte, dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("der TMDb-Titel findet die Serie eng verglichen und schreibt die Nummer ins Medium (F-M501)",
                    vorhanden.Resolved && vorhanden.ShowId == 512 && vorhanden.Status == TvstShowStatus.Found
                    && tvstReg.GetMedia("aaaa000000000002")?.TvstShowId == "512"
                    && handlerVorhanden.Posts == 0 && handlerVorhanden.Gets == 1,
                    "status=" + vorhanden.Status + " store=" + (tvstReg.GetMedia("aaaa000000000002")?.TvstShowId ?? "-"));

                // (c) Kein Treffer: genau EINE Anlage, danach wird erneut gesucht und erst dann geschrieben.
                var neu = new TmdbSeriesInfo("Beispielserie Test", "Beispielserie Test", 2026, 3);
                var handlerNeu = new TvstSiteHandler(512, "The Middle") { AddAfterPost = (9001, "Beispielserie Test") };
                var aufloeserNeu = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerNeu)));
                var angelegt = await aufloeserNeu
                    .ResolveForMediaAsync(tvstReg, "aaaa000000000003", neu, dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("ohne Treffer wird genau einmal angelegt und danach erneut gesucht (F-M501)",
                    angelegt.Resolved && angelegt.ShowId == 9001 && angelegt.Status == TvstShowStatus.Created
                    && handlerNeu.Posts == 1 && handlerNeu.Gets == 2
                    && tvstReg.GetMedia("aaaa000000000003")?.TvstShowId == "9001",
                    "status=" + angelegt.Status + " posts=" + handlerNeu.Posts + " gets=" + handlerNeu.Gets);

                Check("die Anlage traegt Name, Jahr und Staffelzahl aus TMDb (F-M501)",
                    handlerNeu.LastBody is string rumpf
                    && rumpf.Contains("name=\"name\"\r\n\r\nBeispielserie Test", StringComparison.Ordinal)
                    && rumpf.Contains("name=\"year_s\"\r\n\r\n2026", StringComparison.Ordinal)
                    && rumpf.Contains("name=\"seasons\"\r\n\r\n3", StringComparison.Ordinal),
                    handlerNeu.LastBody is null ? "kein Rumpf" : "Rumpf ohne die drei Felder");

                // Staffelzahl: die Seite kennt nur 1..22 — 30 wird auf 22 gezogen, "keine Angabe" auf 1.
                var handlerViele = new TvstSiteHandler(512, "The Middle") { AddAfterPost = (9002, "Viele Staffeln") };
                var aufloeserViele = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerViele)));
                await aufloeserViele.ResolveForMediaAsync(
                        tvstReg, "aaaa000000000004", new TmdbSeriesInfo("Viele Staffeln", null, 2001, 30),
                        dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                var handlerOhne = new TvstSiteHandler(512, "The Middle") { AddAfterPost = (9003, "Ohne Angabe") };
                var aufloeserOhne = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerOhne)));
                await aufloeserOhne.ResolveForMediaAsync(
                        tvstReg, "aaaa000000000005", new TmdbSeriesInfo("Ohne Angabe", null, 2001, null),
                        dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("die Staffelzahl wird auf 1..22 begrenzt, und BuildAddShow weist 23 ab (F-M501)",
                    handlerViele.LastBody!.Contains("name=\"seasons\"\r\n\r\n22", StringComparison.Ordinal)
                    && handlerOhne.LastBody!.Contains("name=\"seasons\"\r\n\r\n1", StringComparison.Ordinal)
                    && TvSubtitlesClient.MaxSeason == 22
                    && Throws(() => TvSubtitlesClient.BuildAddShow("XY", 2026, 23))
                    && !Throws(() => TvSubtitlesClient.BuildAddShow("XY", 2026, 22)),
                    "30->" + (handlerViele.LastBody!.Contains("seasons\"\r\n\r\n22", StringComparison.Ordinal) ? "22" : "?"));

                // (d) Die Anlage scheitert: nichts wird geschrieben, die Kette laeuft weiter (Skipped, kein Wurf).
                var handlerKaputt = new TvstSiteHandler(512, "The Middle") { PostStatus = 500 };
                var aufloeserKaputt = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerKaputt)));
                var kaputt = await aufloeserKaputt
                    .ResolveForMediaAsync(
                        tvstReg, "aaaa000000000006", new TmdbSeriesInfo("Beispielserie Test", null, 2026, 3),
                        dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("eine gescheiterte Anlage blockiert die Kette nicht und schreibt nichts (F-M501/F-M516)",
                    !kaputt.Resolved && kaputt.Status == TvstShowStatus.Refused
                    && tvstReg.GetMedia("aaaa000000000006")?.TvstShowId is null
                    && handlerKaputt.Posts == 1
                    // die Kette antwortet mit Skipped (die andere Quelle bekommt ihren Versuch), nicht mit Failed
                    && uploadSource.Contains("return UploadResult.Skipped(\"second-source-show-\" + show.Status)", StringComparison.Ordinal),
                    "status=" + kaputt.Status + " posts=" + handlerKaputt.Posts);

                // Anlegen hoechstens einmal je Medium und Lauf: derselbe Fehlschlag wird NICHT wiederholt.
                var zweiterVersuch = await aufloeserKaputt
                    .ResolveForMediaAsync(
                        tvstReg, "aaaa000000000006", new TmdbSeriesInfo("Beispielserie Test", null, 2026, 3),
                        dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("ein Fehlschlag legt nicht bei jeder Folge erneut an (F-M501)",
                    !zweiterVersuch.Resolved
                    && zweiterVersuch.Status == TvstShowStatus.CreateAlreadyTried
                    && handlerKaputt.Posts == 1,
                    "status=" + zweiterVersuch.Status + " posts=" + handlerKaputt.Posts);

                // (e) Trockenlauf: nichts anlegen, nichts schreiben.
                var handlerTrocken = new TvstSiteHandler(512, "The Middle");
                var aufloeserTrocken = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerTrocken)));
                var trocken = await aufloeserTrocken
                    .ResolveForMediaAsync(
                        tvstReg, "aaaa000000000007", new TmdbSeriesInfo("Beispielserie Test", null, 2026, 3),
                        dryRun: true, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("der Trockenlauf legt nichts an und schreibt nichts (F-M501)",
                    !trocken.Resolved && trocken.Status == TvstShowStatus.WouldCreate
                    && handlerTrocken.Posts == 0
                    && tvstReg.GetMedia("aaaa000000000007")?.TvstShowId is null,
                    "status=" + trocken.Status + " posts=" + handlerTrocken.Posts);

                // (f) Unklare Seite: einmal nachfragen, dann NICHT schreiben und tvst ueberspringen.
                var handlerLeer = new TvstSiteHandler(512, "The Middle") { EmptyList = true };
                var aufloeserLeer = new TvstShowResolver(new TvSubtitlesClient(new HttpClient(handlerLeer)));
                var leer = await aufloeserLeer
                    .ResolveForMediaAsync(
                        tvstReg, "aaaa000000000008", new TmdbSeriesInfo("Beispielserie Test", null, 2026, 3),
                        dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("eine unklare Serienliste wird einmal nachgefragt, dann wird nicht geschrieben (F-M501)",
                    !leer.Resolved && leer.Status == TvstShowStatus.Unclear
                    && handlerLeer.Gets == 2 && handlerLeer.Posts == 0
                    && tvstReg.GetMedia("aaaa000000000008")?.TvstShowId is null,
                    "status=" + leer.Status + " gets=" + handlerLeer.Gets);

                // (g) Der Endpunkt mit Nummer im Ticket: die Serienliste wird gar nicht erst geholt.
                var handlerEndpunkt = new TvstSiteHandler(512, "The Middle");
                var endpunkt = new TvSubtitlesUploadEndpoint(
                    new TvSubtitlesClient(new HttpClient(handlerEndpunkt)), dryRun: false);
                var antwort = await endpunkt.SendAsync(
                        new UploadTicket(
                            "Ein Name, den die Seite NICHT fuehrt", "tt1442464", "1422", 2, 24, "de",
                            "folge.srt", Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\nSatz.\n"),
                            "WEB", "The.Middle.S02E24", false, false, false, TvstShowId: 512),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                // Die EINE Serienliste danach ist die Ruecklesung der eigenen Upload-Id (F-M488) — sie
                // kommt NACH dem Upload. Vor dem Upload wird nicht gesucht: die Nummer steht im Ticket,
                // obwohl der Name im Ticket eine Schreibweise ist, die die Seite nicht fuehrt.
                Check("mit TvstShowId im Ticket sucht der Endpunkt vor dem Upload nicht (F-M501)",
                    antwort.Ok && handlerEndpunkt.Uploads == 1
                    && handlerEndpunkt.Order.FirstOrDefault() == "UPLOAD"
                    && handlerEndpunkt.Order.Count(x => x == "UPLOAD") == 1
                    && handlerEndpunkt.LastUploadBody!.Contains("tvshow\"\r\n\r\n512", StringComparison.Ordinal),
                    "ok=" + antwort.Ok + " folge=" + string.Join(",", handlerEndpunkt.Order));

                // Die Kette selbst: kein Dateiname mehr als Serienname, die Nummer geht ins Ticket.
                Check("die Upload-Kette loest ueber TMDb und den Store auf, nicht ueber den Dateinamen (F-M501)",
                    uploadSource.Contains("var series = await _tmdb.SeriesInfoAsync(tmdbId!, ct)", StringComparison.Ordinal)
                    && uploadSource.Contains(
                        ".ResolveForMediaAsync(Registry, mediaHash, series, _config.DryRun, ct, mediaPath)", StringComparison.Ordinal)
                    && uploadSource.Contains("TvstShowId: show.ShowId", StringComparison.Ordinal)
                    && uploadSource.Contains("_tvstShows.BeginRun()", StringComparison.Ordinal)
                    // der alte Weg (Serienname aus dem Dateinamen) steht im tvst-Zweig nicht mehr
                    && !uploadSource.Contains(
                        "var parsed = Pipeline.MediaNameParser.Parse(Path.GetFileNameWithoutExtension(releaseName))",
                        StringComparison.Ordinal));

            // (h) Die Migration selbst: eine Datei aus Schema 12 (media ohne TvstShowId) wird beim
            // Oeffnen um die Spalte erweitert, der Bestand bleibt lesbar.
            string legacyDir = Path.Combine(tvstRoot, "legacy");
            Directory.CreateDirectory(legacyDir);
            string legacyFile = Path.Combine(legacyDir, "subdl-scribe.sqlite");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + legacyFile))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE meta (Id TEXT PRIMARY KEY, SchemaVersion INTEGER, PluginVersion TEXT, "
                    + "JellyfinVersion TEXT, Updated TEXT);"
                    + "INSERT INTO meta VALUES ('db', 12, '12.1.12.389', '10.11.0', '2026-11-15');"
                    + "CREATE TABLE media (Id TEXT PRIMARY KEY, Path TEXT, JellyfinItemId TEXT, ImdbId TEXT, "
                    + "TmdbId TEXT, SdId TEXT, IsSeries INTEGER, Season INTEGER, Episode INTEGER, "
                    + "LanguagesAvailable TEXT, SubtitlesUploadedAt TEXT, LastSearchUtc TEXT, "
                    + "LastSearchLanguages TEXT, LastSeen TEXT);"
                    // Jede Spalte, die das Modell NICHT nullen kann, bekommt einen Wert — sonst
                    // scheitert schon das Lesen des Bestands, und die Pruefung misse die Migration.
                    + "INSERT INTO media (Id, Path, JellyfinItemId, ImdbId, TmdbId, SdId, IsSeries, Season, "
                    + "Episode, LanguagesAvailable, SubtitlesUploadedAt, LastSearchUtc, LastSearchLanguages, "
                    + "LastSeen) VALUES ('aaaa000000000009', '/data/serien/folge.mkv', 'jf-1', 'tt1442464', "
                    + "'1422', NULL, 1, 2, 24, 'DE', NULL, NULL, NULL, '2026-11-15T00:00:00');";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var legacyDb = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(legacyDir, null))
            {
                var bestand = legacyDb.Media.FindById("aaaa000000000009");
                // Der Wert wird VOR dem Schreiben festgehalten: EF haelt eine Zeile im Kontext und
                // liefert beim zweiten Lesen DIESELBE Instanz — die nach dem Patch "512" traegt.
                string? vorherTvst = bestand?.TvstShowId;
                legacyDb.EnsureMedia("aaaa000000000009", patch: m => m.TvstShowId = "512");
                var nachher = legacyDb.Media.FindById("aaaa000000000009");
                Check("Schema 12 -> 14: die Spalte wird beim Start nachgezogen, der Bestand bleibt gueltig (F-M501)",
                    bestand is { ImdbId: "tt1442464", TmdbId: "1422" }
                    && vorherTvst is null
                    && nachher?.TvstShowId == "512"
                    // F-M507: der Marker ist inzwischen 14 — die Datei wird auf den Stand DIESES Builds
                    // gehoben, und der Bestand muss das aushalten.
                    && legacyDb.Meta.FindById("db")?.SchemaVersion == 14,
                    "bestand=" + (bestand?.ImdbId ?? "-") + "/" + (bestand?.TmdbId ?? "-")
                    + "/tvst=" + (vorherTvst ?? "<null>")
                    + " nachher=" + (nachher?.TvstShowId ?? "-")
                    + " marker=" + legacyDb.Meta.FindById("db")?.SchemaVersion);
            }
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(tvstRoot))
                {
                    Directory.Delete(tvstRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // ---- 21. F-M504: eine leere erste Quelle beendet den Vorgang nicht mehr ---------------
        // Gemessen 15.11.2026 (Probelauf jf-test, DownloadLanguages=DE): 27 Zeilen
        // "no candidates on SubDL for [DE]" und KEINE tvst-Zeile, obwohl der tvst-Schalter an war —
        // der Ausstieg bei leerer SubDL-Antwort stand VOR der Stelle, die die zweite Quelle fragte.
        // Und die Frage lautet nicht "zwei Quellen", sondern "alle Quellen, deren Schalter an ist"
        // (Nutzerergaenzung 15.11.2026: "Dritte bis n quelle sind ebenfalls denkbar").
        var alleAnbieter = Enum.GetValues<SubtitleProvider>().ToList();

        // (a) F-M509 (Operator-Klarstellung 15.11.2026: „Seperate into subdl and tvst. Tvst is splitted
        // into tvst and moviesub itself"): die Reihenfolge ordnet ZWEI Quellen — SubDL und die tvst-
        // Familie. Die Familie erscheint in jeder Medienart mit der Adresse, die sie fuehrt: Serie
        // tvsubtitles.net, Film moviesubtitles.org. Deshalb steht in jeder Liste genau ZWEI Eintraege,
        // und die Einstellung hat weiterhin zwei Werte (keine dritte Position).
        var tvstZuerstSerie = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: true);
        var tvstZuerstFilm = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false);
        var subdlZuerstSerie = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.SubdlFirst, isSeries: true);
        var subdlZuerstFilm = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.SubdlFirst, isSeries: false);
        Check("the query order names the family's page for the medium plus SubDL — the family leads in BOTH media (F-M509)",
            tvstZuerstSerie.SequenceEqual(new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl })
            && tvstZuerstFilm.SequenceEqual(new[] { SubtitleProvider.MovieSubtitles, SubtitleProvider.Subdl })
            && subdlZuerstSerie.SequenceEqual(new[] { SubtitleProvider.Subdl, SubtitleProvider.TvSubtitles })
            && subdlZuerstFilm.SequenceEqual(new[] { SubtitleProvider.Subdl, SubtitleProvider.MovieSubtitles })
            && Enum.GetValues<PluginConfiguration.DownloadSourceOrder>().Length == 2
            && SourceOrder.FamilyFirst(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst)
            && !SourceOrder.FamilyFirst(PluginConfiguration.DownloadSourceOrder.SubdlFirst)
            && SourceOrder.FamilyPage(true) == SubtitleProvider.TvSubtitles
            && SourceOrder.FamilyPage(false) == SubtitleProvider.MovieSubtitles,
            "serie/tvst=" + string.Join(",", tvstZuerstSerie) + " | film/tvst=" + string.Join(",", tvstZuerstFilm));

        // Die Anzahl der befragten Quellen ist die Anzahl der freigeschalteten — nachgebaut aus genau
        // den Bausteinen, die der Lauf benutzt. Eine fest verdrahtete Zwei faellt hier auf, sobald das
        // Enum eine dritte Quelle bekommt.
        var beideAn = Conf(c => { c.DownloadEnabled = true; c.TvSubtitlesDownloadEnabled = true; });
        var nurSubdl = Conf(c =>
        {
            c.DownloadEnabled = true;
            c.TvSubtitlesDownloadEnabled = false;
            c.MovieSubtitlesDownloadEnabled = false; // F-M507: die dritte Quelle ist im null-Fall an
        });
        // F-M509: gefragt wird je Medienart — die Adresse der Familie, die diese Art fuehrt, und SubDL.
        // Die Zahl der befragten Quellen ist damit ZWEI, unabhaengig davon, wie viele Adressen das Enum
        // fuehrt: die Familie ist eine Quelle.
        var freigeschaltetSerie = SourceOrder.ForMedia(beideAn.DownloadOrder, isSeries: true).Where(beideAn.IsDownloadEnabled).ToList();
        var freigeschaltetFilm = SourceOrder.ForMedia(beideAn.DownloadOrder, isSeries: false).Where(beideAn.IsDownloadEnabled).ToList();
        Check("per medium exactly two sources are asked — SubDL and the family's page for that medium (F-M504/F-M509)",
            freigeschaltetSerie.SequenceEqual(new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl })
            && freigeschaltetFilm.SequenceEqual(new[] { SubtitleProvider.MovieSubtitles, SubtitleProvider.Subdl })
            && SourceOrder.ForMedia(nurSubdl.DownloadOrder, isSeries: true).Where(nurSubdl.IsDownloadEnabled).Single() == SubtitleProvider.Subdl
            && SourceOrder.ForMedia(nurSubdl.DownloadOrder, isSeries: false).Where(nurSubdl.IsDownloadEnabled).Single() == SubtitleProvider.Subdl,
            "serie=" + string.Join(",", freigeschaltetSerie) + " film=" + string.Join(",", freigeschaltetFilm));

        // (a) DIE AUFRUFREIHENFOLGE, nicht nur die Rangfolge (Nutzerkorrektur 15.11.2026: „Suchen
        // engines werden der Reihenfolge nach befragt. Zur zeit sind es 2"): bis hierher lief die
        // SubDL-Suche als fester Block zuerst, und die Einstellung entschied nur ueber den Rang. Der
        // Ablauf wird deshalb aus DENSELBEN Bausteinen nachgebaut, die der Lauf benutzt — die
        // eingestellte Reihenfolge aus SourceOrder.ForOrder und die Frage „ist das die Hauptsuche?"
        // ueber SourceOrder.MainSearch, also genau die Konstante, auf die die Pipeline verzweigt.
        static List<string> Ablauf(PluginConfiguration.DownloadSourceOrder order) =>
            SourceOrder.ForMedia(order, isSeries: true)
                .Select(p => p == SourceOrder.MainSearch ? "main-search" : "endpoint:" + Provenance.Name(p))
                .ToList();

        var ablaufTvst = Ablauf(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst);
        var ablaufSubdl = Ablauf(PluginConfiguration.DownloadSourceOrder.SubdlFirst);
        // F-M509: bei einem FILM fuehrt dieselbe Einstellung die Film-Adresse der Familie an — genau die
        // Luecke, die F-M507 offen liess (tvst war fuer Filme nie zustaendig, also fuehrte immer SubDL).
        var ablaufFilmTvst = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false)
            .Select(p => p == SourceOrder.MainSearch ? "main-search" : "endpoint:" + Provenance.Name(p))
            .ToList();
        Check("tvst-first puts the family's FILM page before the SubDL search too (F-M509)",
            ablaufFilmTvst[0] == "endpoint:moviesubtitles"
            && ablaufFilmTvst.IndexOf("endpoint:moviesubtitles") < ablaufFilmTvst.IndexOf("main-search"),
            string.Join(" > ", ablaufFilmTvst));
        Check("tvst-first asks tvsubtitles BEFORE the SubDL search, subdl-first after it (F-M504)",
            ablaufTvst[0] == "endpoint:tvsubtitles"
            && ablaufTvst.IndexOf("endpoint:tvsubtitles") < ablaufTvst.IndexOf("main-search")
            && ablaufSubdl[0] == "main-search"
            && ablaufSubdl.IndexOf("main-search") < ablaufSubdl.IndexOf("endpoint:tvsubtitles"),
            "tvst-first=" + string.Join(" > ", ablaufTvst) + " | subdl-first=" + string.Join(" > ", ablaufSubdl));

        // Dieselbe Aussage am Quelltext: die SubDL-Suche steht NICHT mehr als eigener Block vor der
        // Schleife, sondern laeuft in der lokalen Funktion, die der Zweig „diese Quelle ist die
        // Hauptsuche" aufruft — also an der Stelle, die die Reihenfolge vorgibt. Der letzte Aufruf
        // von RunV2SearchAsync muss VOR der Schleife liegen, und es darf keinen danach geben.
        var mainSearchKopf = downloadSource.IndexOf("async Task<bool> RunMainSearchAsync()", StringComparison.Ordinal);
        var letzteSubdlSuche = downloadSource.LastIndexOf("await RunV2SearchAsync(", StringComparison.Ordinal);
        var schleife = downloadSource.IndexOf("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal);
        Check("the SubDL search runs where the order puts it — inside the ordered loop, not before it (F-M504)",
            mainSearchKopf > 0 && letzteSubdlSuche > mainSearchKopf && letzteSubdlSuche < schleife
            && downloadSource.Contains("if (quelle == MainSearchSource)", StringComparison.Ordinal)
            && downloadSource.Contains("if (!await RunMainSearchAsync().ConfigureAwait(false))", StringComparison.Ordinal),
            "main-search head @" + mainSearchKopf + ", last call @" + letzteSubdlSuche + ", loop @" + schleife);

        // (b) KEIN vorzeitiger Ausstieg: die Schleife fragt jede Quelle, und der Ausstieg „keine
        // Kandidaten" steht HINTER ihr. Im Rumpf gibt es kein `break` und keine Ausstiegszeile; die
        // beiden `continue` sind die dokumentierten Faelle (SubDL nicht konfiguriert, keine fehlende
        // Sprache) und ueberspringen eine Abfrage, nicht eine Quelle, die noch zu fragen waere.
        var schleifeEnde = downloadSource.IndexOf("// F-M47: search performed", StringComparison.Ordinal);
        var schleifenRumpf = schleife > 0 && schleifeEnde > schleife
            ? downloadSource.Substring(schleife, schleifeEnde - schleife)
            : string.Empty;
        Check("no source is skipped and nothing leaves the loop before the next one is asked (F-M504)",
            schleifenRumpf.Length > 0
            && schleifenRumpf.Contains("if (quelle == MainSearchSource)", StringComparison.Ordinal)
            && schleifenRumpf.Contains("await ForeignCandidatesAsync(quelle, title", StringComparison.Ordinal)
            && schleifenRumpf.Contains("gefragteQuellen.Add(quelle);", StringComparison.Ordinal)
            && !schleifenRumpf.Contains("break;", StringComparison.Ordinal)
            && !schleifenRumpf.Contains("no candidates for [{Langs}]", StringComparison.Ordinal)
            && !downloadSource.Substring(schleifeEnde).Contains("await ForeignCandidatesAsync(", StringComparison.Ordinal),
            "loop body " + schleifenRumpf.Length + " chars");

        // (d) ausgeschaltete Quelle: der Filter steht JE QUELLE in der Liste, die die Schleife abarbeitet.
        Check("a switched-off source is never asked — the loop runs over the switch-filtered order (F-M504)",
            downloadSource.Contains("Api.Endpoints.SourceOrder.ForMedia(_config.DownloadOrder, isSeries)", StringComparison.Ordinal)
            && downloadSource.Contains(".Where(SourceAllowed)", StringComparison.Ordinal)
            && downloadSource.Contains("var quellenReihenfolge = DownloadQueryOrder(isSeries);", StringComparison.Ordinal)
            && downloadSource.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal)
            && !downloadSource.Contains("foreach (var quelle in zusatzQuellen)", StringComparison.Ordinal)
            && !downloadSource.Contains("Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal),
            "the list is built from the order AND the switch of each source");

        // (a) leere erste Antwort: die Fragen stehen VOR dem Ausstieg — beide, nicht nur die zweite.
        var frageStelle = downloadSource.IndexOf("await ForeignCandidatesAsync(", StringComparison.Ordinal);
        var ausstieg = downloadSource.IndexOf("no candidates for [{Langs}]", StringComparison.Ordinal);
        Check("an empty first source no longer ends the item — the next sources are asked first (F-M504)",
            frageStelle > 0 && ausstieg > frageStelle && ausstieg > letzteSubdlSuche
            && downloadSource.Contains("if (candidates.Count == 0 && !fremdProSprache.Values.Any(", StringComparison.Ordinal),
            "asked @" + frageStelle + ", subdl search @" + letzteSubdlSuche + ", exit @" + ausstieg);

        // (b) beide leer: das bisherige Ergebnis bleibt, und die Zeile nennt die gefragten Quellen.
        Check("when every asked source is empty the old verdict stands, and the line names them (F-M504)",
            downloadSource.Contains("summary.NotAvailable += missing.Count;", StringComparison.Ordinal)
            && downloadSource.Contains("summary.SkippedNoCandidates++;", StringComparison.Ordinal)
            && downloadSource.Contains("asked: {Sources}", StringComparison.Ordinal)
            && downloadSource.Contains("Api.Endpoints.Provenance.Name", StringComparison.Ordinal)
            && !downloadSource.Contains("[SubDL-D] {File} — no candidates on ", StringComparison.Ordinal));

        // (c) kein Doppelabruf: EIN Aufruf je Quelle und Sprache, die Schleife liest nur noch.
        var abrufe = System.Text.RegularExpressions.Regex.Matches(downloadSource, @"await ForeignCandidatesAsync\(").Count;
        Check("every source is asked ONCE per language — the language loop only reads the answer (F-M504)",
            abrufe == 1
            && downloadSource.Contains("fremdProSprache[lang] = treffer;", StringComparison.Ordinal)
            && downloadSource.Contains("if (fremdProSprache.TryGetValue(lang, out var fremd) && fremd.Count > 0)", StringComparison.Ordinal),
            abrufe + " call site(s)");

        // Und der Abruf selbst hat die Zwei-Quellen-Annahme verloren: der Endpunkt kommt aus dem
        // Katalog, ueber denselben Helfer fuer Suche UND Abruf.
        Check("search and fetch resolve the endpoint of ANY source over the catalog (F-M504)",
            downloadSource.Contains("private Api.Endpoints.IDownloadEndpoint? EndpointFor(Configuration.SubtitleProvider provider) =>", StringComparison.Ordinal)
            && downloadSource.Contains("var endpoint = EndpointFor(quelle);", StringComparison.Ordinal)
            && downloadSource.Contains("var endpoint = EndpointFor(cand.Provider);", StringComparison.Ordinal)
            && !downloadSource.Contains("_tvDownload", StringComparison.Ordinal));

        // ---- 22. F-M507: die dritte Quelle — Zuordnung nach Medienart, Download-Pfad -------------
        // Nutzerauftrag 15.11.2026: „Nur das series halt nach tv show und movies nach moviesubtitels
        // gehen. Als upload und download" und „Was was ist, Aufloesung per tmdb". Geprueft wird die
        // Zuordnung selbst (nicht ihre Absicht): welche Quelle fuer welche Medienart zustaendig ist, dass
        // die jeweils andere Seite NICHT gefragt wird, dass ein ausgeschalteter Schalter die Quelle aus
        // der Liste nimmt, dass die Reihenfolge erhalten bleibt — und dass der UPLOAD die neue Quelle
        // nicht kennt (Operator-Auflage: „Bevor wir falsche Sprachen hochladen, laden wir nix hoch.").

        // (a) Die Schalter der dritten Quelle: Download default AN, Upload default AUS (F-M503-Regel),
        // ein gespeichertes false bleibt AUS.
        Check("moviesubtitles: Download ist im ungesetzten Fall an, Upload aus — ein gesetztes false bleibt aus (F-M507)",
            Conf(c => { }).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
            && !Conf(c => { }).IsUploadEnabled(SubtitleProvider.MovieSubtitles)
            && !Conf(c => c.MovieSubtitlesDownloadEnabled = false).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
            && Conf(c => c.MovieSubtitlesDownloadEnabled = true).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
            && Conf(c => c.MovieSubtitlesUploadEnabled = true).IsUploadEnabled(SubtitleProvider.MovieSubtitles),
            "download-null=" + Conf(c => { }).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
            + " upload-null=" + Conf(c => { }).IsUploadEnabled(SubtitleProvider.MovieSubtitles));

        // (b) Die Zuordnung steht an EINER Stelle und ist eine Aussage ueber die Quelle: SubDL
        // unterscheidet nicht (beides), tvsubtitles.net nur Serien, moviesubtitles.org nur Filme.
        Check("die Medienart entscheidet je Quelle: SubDL beides, tvst nur Serien, msub nur Filme (F-M507)",
            SourceOrder.AppliesTo(SubtitleProvider.Subdl, isSeries: true)
            && SourceOrder.AppliesTo(SubtitleProvider.Subdl, isSeries: false)
            && SourceOrder.AppliesTo(SubtitleProvider.TvSubtitles, isSeries: true)
            && !SourceOrder.AppliesTo(SubtitleProvider.TvSubtitles, isSeries: false)
            && !SourceOrder.AppliesTo(SubtitleProvider.MovieSubtitles, isSeries: true)
            && SourceOrder.AppliesTo(SubtitleProvider.MovieSubtitles, isSeries: false));

        // (c) Eine SERIE fragt SubDL und tvsubtitles.net — und moviesubtitles.org NICHT, in keiner der
        // beiden Reihenfolgen.
        var serieTvstZuerst = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: true);
        var serieSubdlZuerst = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.SubdlFirst, isSeries: true);
        Check("eine Serie fragt tvst und SubDL, moviesubtitles nie (F-M507)",
            serieTvstZuerst.SequenceEqual(new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl })
            && serieSubdlZuerst.SequenceEqual(new[] { SubtitleProvider.Subdl, SubtitleProvider.TvSubtitles })
            && !serieTvstZuerst.Contains(SubtitleProvider.MovieSubtitles)
            && !serieSubdlZuerst.Contains(SubtitleProvider.MovieSubtitles),
            "tvst-first=" + string.Join(",", serieTvstZuerst) + " subdl-first=" + string.Join(",", serieSubdlZuerst));

        // (d) Ein FILM fragt SubDL und moviesubtitles.org — und tvsubtitles.net NICHT. Die konfigurierte
        // Quelle fuehrt, soweit sie fuer diese Medienart ueberhaupt zustaendig ist: bei „tvst zuerst" ist
        // das fuer einen Film SubDL, weil tvst hier nicht anwendbar ist.
        var filmZuerst = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false);
        var filmSubdlZuerst = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.SubdlFirst, isSeries: false);
        // F-M509 (Korrektur der Luecke aus F-M507): „tvst zuerst" wirkt jetzt AUCH bei Filmen — dann
        // fuehrt moviesubtitles.org. Vorher stand hier, ein Film frage in beiden Reihenfolgen SubDL
        // zuerst, weil tvst fuer Filme nie zustaendig ist; das war die Luecke, nicht die Absicht.
        Check("ein Film fragt moviesubtitles und SubDL — in der eingestellten Reihenfolge, tvsubtitles nie (F-M509)",
            filmZuerst.SequenceEqual(new[] { SubtitleProvider.MovieSubtitles, SubtitleProvider.Subdl })
            && filmSubdlZuerst.SequenceEqual(new[] { SubtitleProvider.Subdl, SubtitleProvider.MovieSubtitles })
            && !filmZuerst.Contains(SubtitleProvider.TvSubtitles)
            && !filmSubdlZuerst.Contains(SubtitleProvider.TvSubtitles),
            "tvst-first=" + string.Join(",", filmZuerst) + " subdl-first=" + string.Join(",", filmSubdlZuerst));

        // (e) Ein ausgeschalteter Schalter nimmt die Quelle aus der Liste — die andere Seite bleibt.
        var tvstAusCfg = Conf(c => c.TvSubtitlesDownloadEnabled = false);
        var msubAusCfg = Conf(c => c.MovieSubtitlesDownloadEnabled = false);
        Check("Schalter aus = nicht gefragt: tvst aus heisst nur SubDL fuer eine Serie, msub aus nur SubDL fuer einen Film (F-M507)",
            SourceOrder.ForMedia(tvstAusCfg.DownloadOrder, isSeries: true).Where(tvstAusCfg.IsDownloadEnabled).Single() == SubtitleProvider.Subdl
            && SourceOrder.ForMedia(msubAusCfg.DownloadOrder, isSeries: false).Where(msubAusCfg.IsDownloadEnabled).Single() == SubtitleProvider.Subdl,
            "serie=" + string.Join(",", SourceOrder.ForMedia(tvstAusCfg.DownloadOrder, true).Where(tvstAusCfg.IsDownloadEnabled))
            + " film=" + string.Join(",", SourceOrder.ForMedia(msubAusCfg.DownloadOrder, false).Where(msubAusCfg.IsDownloadEnabled)));

        // (f) Die Herkunft kennt die dritte Quelle: der Store-Wert laesst sich zuruecklesen, und eine
        // BEKANNTE Herkunft sperrt den Upload bei ALLEN Zielen (F-M487), die neue eingeschlossen.
        Check("moviesubtitles ist eine Herkunft wie die beiden anderen — bekannt sperrt ueberall (F-M507)",
            Provenance.Name(SubtitleProvider.MovieSubtitles) == "moviesubtitles"
            && Provenance.Parse("moviesubtitles") == SubtitleProvider.MovieSubtitles
            && Provenance.Parse("MovieSubtitles") == SubtitleProvider.MovieSubtitles
            && !Provenance.MayUploadTo("moviesubtitles", SubtitleProvider.MovieSubtitles)
            && !Provenance.MayUploadTo("moviesubtitles", SubtitleProvider.Subdl)
            && !Provenance.MayUploadTo("moviesubtitles", SubtitleProvider.TvSubtitles)
            && Provenance.MayUploadTo(null, SubtitleProvider.MovieSubtitles)
            && Provenance.OriginBlockedReason("moviesubtitles")!.Contains("moviesubtitles", StringComparison.Ordinal));

        // (g) Operator-Auflage 15.11.2026 („Bevor wir falsche Sprachen hochladen, laden wir nix hoch."):
        // die neue Quelle steht NICHT in der Quellenliste des Uploads — auch nicht als toter Eintrag,
        // denn die Kette hat fuer „alles ausser SubDL" genau einen Zweig (tvst).
        // F-M508 (Etappe 2b): der Upload-Weg der Film-Adresse EXISTIERT jetzt. Er steht deshalb in der
        // Kette — aber nur fuer Filme, und nur wenn sein Schalter an ist (Vorgabe: aus). Fuer Serien bleibt
        // er draussen: die Medienart schneidet ihn heraus, nicht eine Fallunterscheidung im Code.
        var uploadKetteSerie = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: true)
            .Where(SubtitleEndpointCatalog.HasUploadPath)
            .ToList();
        var uploadKetteFilm = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false)
            .Where(SubtitleEndpointCatalog.HasUploadPath)
            .ToList();
        Check("the film address HAS an upload path and stands in the chain for films — and only there (F-M508)",
            SubtitleEndpointCatalog.HasUploadPath(SubtitleProvider.MovieSubtitles)
            && SubtitleEndpointCatalog.HasUploadPath(SubtitleProvider.Subdl)
            && SubtitleEndpointCatalog.HasUploadPath(SubtitleProvider.TvSubtitles)
            && uploadKetteSerie.SequenceEqual(new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl })
            && uploadKetteFilm.SequenceEqual(new[] { SubtitleProvider.MovieSubtitles, SubtitleProvider.Subdl })
            // Vorgabe bleibt AUS: gebaut heisst nicht scharf
            && !Conf(c => { c.UploadEnabled = true; }).IsUploadEnabled(SubtitleProvider.MovieSubtitles)
            && uploadSource.Contains(
                ".Where(Api.Endpoints.SubtitleEndpointCatalog.HasUploadPath)", StringComparison.Ordinal),
            "serie=" + string.Join(",", uploadKetteSerie) + " film=" + string.Join(",", uploadKetteFilm));

        // (h) Der Lauf selbst: die Frage-Reihenfolge wird ueber die Medienart geschnitten, und der
        // Filmzweig bringt Titel, Jahr und die gespeicherte Filmnummer mit — Staffel und Folge bleiben 0.
        Check("der Lauf schneidet die Liste ueber die Medienart und fragt den Film ueber Titel, Jahr und Filmnummer (F-M507)",
            downloadSource.Contains("private List<Configuration.SubtitleProvider> DownloadQueryOrder(bool isSeries)", StringComparison.Ordinal)
            // F-M509: die Medienart schneidet die Liste, und sie tut es IN SourceOrder.ForMedia — die
            // Pipeline fragt nur noch nach der Reihenfolge dieser Medienart.
            && downloadSource.Contains("Api.Endpoints.SourceOrder.ForMedia(_config.DownloadOrder, isSeries)", StringComparison.Ordinal)
            && downloadSource.Contains("var quellenReihenfolge = DownloadQueryOrder(isSeries);", StringComparison.Ordinal)
            && downloadSource.Contains("if (!Api.Endpoints.SourceOrder.AppliesTo(quelle, isSeries))", StringComparison.Ordinal)
            && downloadSource.Contains("if (!_config.IsConfiguredForDownload(quelle))", StringComparison.Ordinal)
            && downloadSource.Contains("if (isSeries && (season <= 0 || episode <= 0))", StringComparison.Ordinal)
            && downloadSource.Contains(".ResolveForMediaAsync(Registry, mediaHash, movie, _config.DownloadDryRun, ct, mediaPath)", StringComparison.Ordinal)
            && downloadSource.Contains("var movie = await _tmdb.MovieInfoAsync(tmdbId!, ct)", StringComparison.Ordinal)
            && downloadSource.Contains("MovieId: film.MovieId!.Value.ToString(", StringComparison.Ordinal)
            && downloadSource.Contains("Season = isSeries ? season : 0,", StringComparison.Ordinal));

        // (i) Der Endpunkt der dritten Quelle am Stueck: Suche ueber Titel und Jahr, EINE Filmseite je
        // Film und Lauf, die Sprache aus der Flagge des Eintrags.
        var msubHandler = new MsubSiteHandler();
        var msubEndpoint = new MovieSubtitlesDownloadEndpoint(
            new MovieSubtitlesClient(new HttpClient(msubHandler)));
        // Die Nummer kommt aus dem Store (der Lauf uebergibt sie, MsubMovieResolver) — dann kostet eine
        // zweite Sprache KEINE zweite Suche, und die Filmseite wird fuer den Lauf einmal geholt.
        var filmTreffer = await msubEndpoint
            .SearchAsync(
                new SubtitleQuery("Dune: Part One", null, "438631", 0, 0, "EN", 2021, MovieId: "102"),
                CancellationToken.None)
            .ConfigureAwait(false);
        var filmTrefferDe = await msubEndpoint
            .SearchAsync(
                new SubtitleQuery("Dune: Part One", null, "438631", 0, 0, "DE", 2021, MovieId: "102"),
                CancellationToken.None)
            .ConfigureAwait(false);
        Check("der msub-Endpunkt loest ueber Titel und Jahr und holt die Filmseite EINMAL je Film (F-M507)",
            filmTreffer.Count == 2
            && filmTreffer.All(h => h.Provider == SubtitleProvider.MovieSubtitles)
            && filmTreffer.All(h => h.Language == "en")
            && filmTrefferDe.Count == 1
            && msubHandler.Gets == 1
            && msubHandler.Posts == 0
            && msubHandler.LastUrl!.EndsWith("movie-102.html", StringComparison.Ordinal),
            "hits=" + filmTreffer.Count + "/" + filmTrefferDe.Count + " gets=" + msubHandler.Gets
            + " url=" + msubHandler.LastUrl);

        // (j) Was NICHT gefragt wird, kostet keinen Aufruf: eine Serie (Staffel/Folge gesetzt) und eine
        // Sprache, die die Seite nicht fuehrt, antworten leer — ohne Anfrage.
        var msubHandlerLeer = new MsubSiteHandler();
        var msubLeer = new MovieSubtitlesDownloadEndpoint(
            new MovieSubtitlesClient(new HttpClient(msubHandlerLeer)));
        var serieBeiMsub = await msubLeer
            .SearchAsync(new SubtitleQuery("The Middle", null, null, 2, 24, "EN", 2011), CancellationToken.None)
            .ConfigureAwait(false);
        var zhBeiMsub = await msubLeer
            .SearchAsync(new SubtitleQuery("Dune: Part One", null, "438631", 0, 0, "ZH", 2021), CancellationToken.None)
            .ConfigureAwait(false);
        Check("eine Serie und eine Sprache ohne Seitenkuerzel werden bei msub ohne einen einzigen Aufruf verneint (F-M507)",
            serieBeiMsub.Count == 0 && zhBeiMsub.Count == 0 && msubHandlerLeer.Gets == 0,
            "gets=" + msubHandlerLeer.Gets);

        // (k) Der Store: die Filmnummer steht am Medium (MsubMovieId), wird beim zweiten Mal gelesen
        // statt gesucht, und der Marker steht auf 14.
        var msubStoreDir = new TempDir("msub-store");
        try
        {
            using (var msubStore = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(msubStoreDir.Path, null))
            {
                var msubAufloeser = new MsubMovieResolver(
                    new MovieSubtitlesClient(new HttpClient(msubHandler)), null);
                var filmAuf = await msubAufloeser
                    .ResolveForMediaAsync(
                        new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(msubStore, null, null),
                        "bbbb000000000001",
                        new TmdbMovieInfo("Dune: Part One", "Dune", 2021),
                        dryRun: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var gespeichert = msubStore.Media.FindById("bbbb000000000001")?.MsubMovieId;
                var zweiteRunde = await msubAufloeser
                    .ResolveForMediaAsync(
                        new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(msubStore, null, null),
                        "bbbb000000000001",
                        new TmdbMovieInfo("Dune: Part One", "Dune", 2021),
                        dryRun: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                Check("die Filmnummer steht am Medium (MsubMovieId) und wird beim zweiten Mal gelesen statt gesucht (F-M507)",
                    filmAuf.Resolved
                    && filmAuf.Status == MsubMovieStatus.Found
                    && gespeichert == "102"
                    && zweiteRunde.Status == MsubMovieStatus.Stored
                    && msubStore.Meta.FindById("db")?.SchemaVersion == 14,
                    "id=" + (gespeichert ?? "<null>") + " status=" + filmAuf.Status + "/" + zweiteRunde.Status
                    + " marker=" + msubStore.Meta.FindById("db")?.SchemaVersion);
            }
        }
        finally
        {
            msubStoreDir.Dispose();
        }

        // (l) Die Migration selbst: eine Datei aus Schema 13 (media ohne MsubMovieId, sidecars/embeds ohne
        // die drei Spalten) wird beim Oeffnen erweitert; der Bestand bleibt unveraendert lesbar.
        var msubLegacyDir = new TempDir("msub-legacy");
        try
        {
            string legacyFile = Path.Combine(msubLegacyDir.Path, "subdl-scribe.sqlite");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + legacyFile))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE meta (Id TEXT PRIMARY KEY, SchemaVersion INTEGER, PluginVersion TEXT, "
                    + "JellyfinVersion TEXT, Updated TEXT);"
                    + "INSERT INTO meta VALUES ('db', 13, '12.1.12.394', '10.11.0', '2026-11-15');"
                    + "CREATE TABLE media (Id TEXT PRIMARY KEY, Path TEXT, JellyfinItemId TEXT, ImdbId TEXT, "
                    + "TmdbId TEXT, TvstShowId TEXT, SdId TEXT, IsSeries INTEGER, Season INTEGER, Episode INTEGER, "
                    + "LanguagesAvailable TEXT, SubtitlesUploadedAt TEXT, LastSearchUtc TEXT, "
                    + "LastSearchLanguages TEXT, LastSeen TEXT);"
                    + "INSERT INTO media (Id, Path, JellyfinItemId, ImdbId, TmdbId, TvstShowId, SdId, IsSeries, "
                    + "Season, Episode, LanguagesAvailable, SubtitlesUploadedAt, LastSearchUtc, LastSearchLanguages, "
                    + "LastSeen) VALUES ('cccc000000000001', '/data/filme/dune.mkv', 'jf-2', 'tt1160419', "
                    + "'438631', NULL, NULL, 0, 0, 0, 'EN', NULL, NULL, NULL, '2026-11-15T00:00:00');"
                    + "CREATE TABLE sidecars (Id TEXT PRIMARY KEY);"
                    + "CREATE TABLE embeds (Id TEXT PRIMARY KEY);"
                    + "INSERT INTO sidecars (Id) VALUES ('dddd000000000001');"
                    + "INSERT INTO embeds (Id) VALUES ('cccc000000000001|2');";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            string? bestandImdb;
            string? bestandTmdb;
            string? bestandMsub;
            int? marker;
            using (var legacy = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(msubLegacyDir.Path, null))
            {
                var bestand = legacy.Media.FindById("cccc000000000001");
                bestandImdb = bestand?.ImdbId;
                bestandTmdb = bestand?.TmdbId;
                bestandMsub = bestand?.MsubMovieId;
                marker = legacy.Meta.FindById("db")?.SchemaVersion;
            }

            var medienSpalten = Spalten(legacyFile, "media");
            var seitenSpalten = Spalten(legacyFile, "sidecars");
            var spurSpalten = Spalten(legacyFile, "embeds");
            Check("Schema 13 -> 14: die Spalten der dritten Quelle werden beim Start nachgezogen (F-M507)",
                medienSpalten.Contains("MsubMovieId")
                && seitenSpalten.Contains("MoviesubtitlesDownloadId")
                && seitenSpalten.Contains("MoviesubtitlesUploadId")
                && seitenSpalten.Contains("MoviesubtitlesUploadedAt")
                && spurSpalten.Contains("MoviesubtitlesDownloadId")
                && spurSpalten.Contains("MoviesubtitlesUploadId")
                && spurSpalten.Contains("MoviesubtitlesUploadedAt")
                && marker == 14,
                "media=" + string.Join(",", medienSpalten.Where(x => x.StartsWith("Msub", StringComparison.Ordinal)))
                + " sidecars=" + string.Join(",", seitenSpalten.Where(x => x.StartsWith("Moviesub", StringComparison.Ordinal)))
                + " embeds=" + string.Join(",", spurSpalten.Where(x => x.StartsWith("Moviesub", StringComparison.Ordinal)))
                + " marker=" + marker);
            Check("die Bestandszeile bleibt unangetastet und liest die neuen Felder als null (F-M507)",
                bestandImdb == "tt1160419" && bestandTmdb == "438631" && bestandMsub is null,
                "bestand=" + (bestandImdb ?? "-") + "/" + (bestandTmdb ?? "-") + " msub=" + (bestandMsub ?? "<null>"));

            // Und die Quellen-Bindung, damit ein spaeterer Umbau die Zuordnung nicht still entfernt.
            var dbSource = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Data/SubdlDbContext.cs"));
            var provenanceSource = ReadText(
                Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/Provenance.cs"));
            Check("Schema 14 ist der Marker, die Spalten stehen in EnsureReportColumns und EnsureMediaColumns (F-M507)",
                dbSource.Contains("public const int CurrentSchemaVersion = 14;", StringComparison.Ordinal)
                && dbSource.Contains("ALTER TABLE sidecars ADD COLUMN MoviesubtitlesDownloadId", StringComparison.Ordinal)
                && dbSource.Contains("ALTER TABLE embeds ADD COLUMN MoviesubtitlesUploadedAt", StringComparison.Ordinal)
                && dbSource.Contains("(\"MsubMovieId\", \"ALTER TABLE media ADD COLUMN MsubMovieId TEXT NULL;\")", StringComparison.Ordinal)
                && provenanceSource.Contains("public const string MovieSubtitles = \"moviesubtitles\";", StringComparison.Ordinal));
        }
        finally
        {
            msubLegacyDir.Dispose();
        }

        // ---- 23. F-M508/F-M509: der UPLOAD-Weg der Film-Adresse --------------------------------
        // Gebaut, verdrahtet, GESPERRT. Die Pruefungen halten beides fest: dass der Weg da ist (Endpunkt,
        // Kette, Tor, Marke) und dass er ohne ausdrueckliche Entscheidung nicht angetreten wird.
        Check("die Film-Adresse steht mit eigenem Endpunkt, eigenem Trockenlauf und eigener Marke in der Kette (F-M508)",
            uploadSource.Contains("Configuration.SubtitleProvider.MovieSubtitles => SendToMovieSubtitlesAsync(", StringComparison.Ordinal)
            && uploadSource.Contains("new Api.Endpoints.MovieSubtitlesUploadEndpoint(_msubClient, dryRun: _config.DryRun)", StringComparison.Ordinal)
            && uploadSource.Contains("Season: 0,", StringComparison.Ordinal)
            && uploadSource.Contains("Episode: 0,", StringComparison.Ordinal)
            && uploadSource.Contains("MsubMovieId: aufloesung.MovieId,", StringComparison.Ordinal)
            && uploadSource.Contains("MovieYear: movie.ReleaseYear ?? 0);", StringComparison.Ordinal)
            && uploadSource.Contains("if (isSeries)", StringComparison.Ordinal)
            && uploadSource.Contains("UploadResult.Skipped(\"film-source-films-only\")", StringComparison.Ordinal));

        Check("die Filmnummer kommt aus dem Store, gesucht wird nur ohne sie — und ohne Jahr gar nicht (F-M508/F-M513)",
            uploadSource.Contains(".ResolveOrCreateForMediaAsync(Registry, mediaHash, movie, _config.DryRun, ct, mediaPath)", StringComparison.Ordinal)
            && uploadSource.Contains("var movie = await _tmdb.MovieInfoAsync(tmdbId!, ct)", StringComparison.Ordinal)
            && uploadSource.Contains("no film id ({Status}) (F-M508)", StringComparison.Ordinal)
            // ohne Titel/Nummer nennt der Endpunkt den Grund, statt zu raten
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/MovieSubtitlesEndpoints.cs"))
                .Contains("a film title AND its year are needed", StringComparison.Ordinal));

        Check("kein Schalter wird in der Kette direkt gelesen — nur ueber SourceAllowed (F-M495/F-M508)",
            !uploadSource.Contains("MovieSubtitlesUploadEnabled", StringComparison.Ordinal)
            && !uploadSource.Contains("TvSubtitlesUploadEnabled", StringComparison.Ordinal)
            && !uploadSource.Contains("SubdlUploadEnabled", StringComparison.Ordinal)
            && uploadSource.Contains("private bool SourceAllowed(Configuration.SubtitleProvider provider) => _config.IsUploadEnabled(provider);", StringComparison.Ordinal));

        // Die Marke der Film-Adresse haelt nur fuer sie — und sie schreibt ihre eigenen Spalten.
        string msubMarkRoot = Path.Combine(Path.GetTempPath(), "endpoints-msubmark-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(msubMarkRoot))
            {
                Directory.Delete(msubMarkRoot, true);
            }

            Directory.CreateDirectory(msubMarkRoot);
            using (var markDb = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(Path.Combine(msubMarkRoot, "db"), null))
            {
                var markReg = new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(markDb, null, null);
                const string film = "ffff000011112222";
                string text = "1\n00:00:01,000 --> 00:00:02,000\nEin Satz.\n\n";
                string hash = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(text);
                markReg.EnsureMedia(film, null, "/data/movies2/film.mkv");
                markReg.MarkEmbed(film, 0, "DE", false, Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Uploaded,
                    contentHash: hash, moviesubtitlesUploadId: "own:moviesubtitles:20261115T180405Z:abcd1234",
                    moviesubtitlesUploadedAt: DateTime.UtcNow);

                Check("die Marke der Film-Adresse haelt nur fuer die Film-Adresse (F-M508)",
                    markReg.IsUploadedTo(film, "DE", false, SubtitleProvider.MovieSubtitles)
                    && markReg.IsContentUploadedFor(hash, SubtitleProvider.MovieSubtitles)
                    && !markReg.IsUploadedTo(film, "DE", false, SubtitleProvider.TvSubtitles)
                    && !markReg.IsContentUploadedFor(hash, SubtitleProvider.TvSubtitles)
                    && !markReg.IsUploadedTo(film, "DE", false, SubtitleProvider.Subdl),
                    "msub=" + markReg.IsUploadedTo(film, "DE", false, SubtitleProvider.MovieSubtitles)
                    + " tvst=" + markReg.IsUploadedTo(film, "DE", false, SubtitleProvider.TvSubtitles));

                // Und das Settle schreibt dieselbe Marke, wenn NUR die Film-Adresse den Inhalt haelt.
                markReg.EnsureMedia("eeee000033334444", null, "/data/movies2/zweiter.mkv");
                bool gesetzt = markReg.RecordSkippedContent("eeee000033334444", false, 1, "DE", false, false, hash,
                    Jellyfin.Plugin.SubdlScribe.Data.RejectReason.DuplicateContent,
                    new[] { SubtitleProvider.MovieSubtitles });
                var zeile = markReg.GetEmbed("eeee000033334444", 1);
                Check("das Settle setzt den Zeitstempel der Film-Adresse, und nur ihn (F-M508)",
                    gesetzt && zeile?.MoviesubtitlesUploadedAt != null
                    && zeile?.TvSubtitlesUploadedAt is null && zeile?.SubdlUploadedAt is null
                    && zeile?.Status == Jellyfin.Plugin.SubdlScribe.Data.SubtitleStatus.Uploaded,
                    "settled=" + gesetzt + " msub=" + (zeile?.MoviesubtitlesUploadedAt?.ToString() ?? "<null>"));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(msubMarkRoot))
                {
                    Directory.Delete(msubMarkRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // ---- 18. F-M510: das Etikett der Familien-Zeile nennt die Adresse, die gefragt wurde ------
        // Gemessen 15.11.2026 im ersten Film-Lauf auf jf-test (Filmbibliothek "Movies_Test"):
        //   [V] [tvsubtitles-D] Oppenheimer (2023).mkv [EN] — 2 moviesubtitles candidate(s), 9 subdl
        //       candidate(s), order moviesubtitles, subdl
        // Die Zeile zaehlte richtig (moviesubtitles stand da), trug aber das Etikett der SERIEN-Seite.
        // Der Text stand fest im Code; die Adresse kommt jetzt aus SourceOrder.FamilyPage(isSeries) —
        // der einen Stelle, an der die Medienart eine Seite der Familie auswaehlt.
        Check("die Familie hat je Medienart genau eine Adresse (Serie tvsubtitles, Film moviesubtitles) (F-M510)",
            SourceOrder.FamilyPage(isSeries: true) == SubtitleProvider.TvSubtitles
            && SourceOrder.FamilyPage(isSeries: false) == SubtitleProvider.MovieSubtitles
            && Provenance.Name(SourceOrder.FamilyPage(isSeries: false)) == "moviesubtitles"
            && Provenance.Name(SourceOrder.FamilyPage(isSeries: true)) == "tvsubtitles",
            "Film → " + Provenance.Name(SourceOrder.FamilyPage(false))
            + ", Serie → " + Provenance.Name(SourceOrder.FamilyPage(true)));

        Check("das Etikett der Sammelzeile ist kein fester Text mehr (F-M510)",
            !downloadSource.Contains("[tvsubtitles-D]", StringComparison.Ordinal)
            && downloadSource.Contains("\"[{Src}-D] {File} [{Lang}] — {Counts}, order {Order}\"", StringComparison.Ordinal));

        var etikettZeile = downloadSource.IndexOf("[{Src}-D] {File} [{Lang}]", StringComparison.Ordinal);
        var etikettArg = downloadSource.IndexOf(
            "Provenance.Name(Api.Endpoints.SourceOrder.FamilyPage(isSeries))", StringComparison.Ordinal);
        Check("die Zeile bekommt die Adresse als ERSTES Argument — vor Datei, Sprache und Zaehlung (F-M510)",
            etikettZeile > 0 && etikettArg > etikettZeile
            && downloadSource.IndexOf("Path.GetFileName(mediaPath), lang, zaehlung, orderText);", etikettArg,
                StringComparison.Ordinal) > etikettArg,
            "Zeile @" + etikettZeile + ", Argument @" + etikettArg);

        Check("die Aussage der Zeile bleibt: Kandidaten JE gefragter Quelle und die Frage-Reihenfolge (F-M510)",
            downloadSource.Contains("{Counts}, order {Order}", StringComparison.Ordinal)
            && downloadSource.Contains("quellenReihenfolge.Select(quelle =>", StringComparison.Ordinal)
            && downloadSource.Contains(" + Api.Endpoints.Provenance.Name(quelle) + \" candidate(s)\"", StringComparison.Ordinal));

        Check("das Etikett ist die Adresse, die in der Frage-Reihenfolge auch wirklich steht (F-M510)",
            SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false)[0]
                == Provenance.Parse(Provenance.Name(SourceOrder.FamilyPage(false)))
            && SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: true)[0]
                == Provenance.Parse(Provenance.Name(SourceOrder.FamilyPage(true)))
            && SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false)
                .Contains(SourceOrder.MainSearch));

        // Der Trockenlauf nennt jetzt auch, WOHER der gewaehlte Kandidat kommt: bei zwei gefragten
        // Quellen ist "wer hat gewonnen" genau die Frage, die er beantworten soll.
        var dryZeile = downloadSource.IndexOf(
            "\"[SubDL-D] DRY-RUN {File} [{Lang}] → {Release} ({Source}, score {Score}, hi={Hi})\"",
            StringComparison.Ordinal);
        var dryHerkunft = dryZeile < 0
            ? -1
            : downloadSource.IndexOf("Provenance.Name(cand.Provider)", dryZeile, StringComparison.Ordinal);
        Check("die DRY-RUN-Zeile nennt die Herkunft des Gewinners, im selben Aufruf (F-M510)",
            dryZeile > 0 && dryHerkunft > dryZeile && dryHerkunft - dryZeile < 400,
            "Zeile @" + dryZeile + ", Herkunft @" + dryHerkunft);
        Check("die alte Form ohne Herkunft ist verschwunden — score und hi behalten ihren Platz (F-M510)",
            !downloadSource.Contains("{Release} (score {Score}, hi={Hi})", StringComparison.Ordinal)
            && downloadSource.Contains("({Source}, score {Score}, hi={Hi})", StringComparison.Ordinal));

        Check("die geteilten Zeilen nennen die Quelle aus der Liste — feste Etiketten bleiben bei ihrem Endpunkt (F-M510)",
            downloadSource.Contains("Api.Endpoints.Provenance.Name(quelle) + \" candidate(s)\"", StringComparison.Ordinal)
            && downloadSource.Contains("Provenance.Name(cand.Provider)", StringComparison.Ordinal)
            && uploadSource.Contains(
                "\"[\" + Api.Endpoints.Provenance.Name(quelle) + \"] DRY-RUN would upload {Name} [{Lang}]\"",
                StringComparison.Ordinal)
            && downloadSource.Contains("\"[moviesubtitles] {File} [{Lang}] — skipped: no TMDb id", StringComparison.Ordinal)
            && !downloadSource.Contains("\"[tvsubtitles] {File", StringComparison.Ordinal));

        // ---- 19. F-M512: die Medienart des Einreihers kommt aus dem STORE (TMDb-korrigiert) ----------
        // Befund 15.11.2026: SubdlSeeder las `item is Episode ? true : item is Movie ? false : null` —
        // also Jellyfins Typ. Die Pipelines korrigieren die Art aber aus Dateiname und TMDb
        // (UploadPipeline:819-826, :886) und schreiben sie in die Medienzeile; der Einreiher sah das
        // nie. Folge bei Abweichung: JF sagt Film, TMDb sagt Serie → nur die Filmadresse wurde
        // erwogen, eine offene tvst-Quelle bekam das Item NIE; umgekehrt wurde eine nie erfuellbare
        // Adresse in jedem Scan neu eingereiht (No-op-Laeufe). Nutzerentscheidung: "Ja den typ
        // augrund tmdb bestimmen". Der Store wird EINE Abfrage je Item gefragt (Primaerschluessel),
        // ohne Netz und ohne TMDb-Request im Scan.
        string kindRoot = Path.Combine(Path.GetTempPath(), "endpoints-medienart-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(kindRoot))
            {
                Directory.Delete(kindRoot, true);
            }

            Directory.CreateDirectory(kindRoot);
            using (var kindDb = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(Path.Combine(kindRoot, "db"), null))
            {
                var kindReg = new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(kindDb, null, null);

                // Die Zeile, die ein Lauf fuer eine SERIE schreibt: Kennungen, Staffel/Folge, IsSeries.
                const string serieHash = "a1a1a1a1b2b2b2b2";
                kindReg.EnsureMedia(serieHash, patch: media =>
                {
                    media.ImdbId = "tt0903747";
                    media.TmdbId = "1396";
                    media.IsSeries = true;
                    media.Season = 1;
                    media.Episode = 3;
                });

                // Die Zeile eines AUFGELOESTEN Films: Kennungen, keine Serie.
                const string filmHash = "c3c3c3c3d4d4d4d4";
                kindReg.EnsureMedia(filmHash, patch: media =>
                {
                    media.ImdbId = "tt15398776";
                    media.TmdbId = "872585";
                });

                // Die Zeile, die JEDER Lauf anlegt (Beobachtung, MarkSearched) — sie sagt NICHTS ueber die Art.
                const string stummHash = "e5e5e5e5f6f6f6f6";
                kindReg.EnsureMedia(stummHash, "0123456789abcdef0123456789abcdef", "/data/movies2/stumm.mkv");

                bool? artSerie = kindReg.MediaKindIsSeries(serieHash);
                bool? artFilm = kindReg.MediaKindIsSeries(filmHash);
                bool? artStumm = kindReg.MediaKindIsSeries(stummHash);

                Check("der Store nennt die Serie, den aufgeloesten Film — und schweigt, wenn niemand die Art geprueft hat (F-M512)",
                    artSerie == true && artFilm == false && artStumm is null,
                    "serie=" + (artSerie?.ToString() ?? "<null>") + " film=" + (artFilm?.ToString() ?? "<null>")
                    + " stumm=" + (artStumm?.ToString() ?? "<null>"));

                Check("ohne Medienzeile (und ohne Hash) sagt der Store nichts — der Jellyfin-Typ entscheidet (F-M512)",
                    kindReg.MediaKindIsSeries("ffff000011112222") is null
                    && kindReg.MediaKindIsSeries(null) is null
                    && kindReg.MediaKindIsSeries(string.Empty) is null);

                // Die Medienart schneidet die Adressen GENAU so, wie der Einreiher sie schneidet
                // (UploadSources: `isSeries is not { } art || SourceOrder.AppliesTo(quelle, art)`).
                var adressen = new[]
                {
                    SubtitleProvider.Subdl, SubtitleProvider.TvSubtitles, SubtitleProvider.MovieSubtitles,
                };
                List<SubtitleProvider> Erwogen(bool? art) => art is { } a
                    ? adressen.Where(q => SourceOrder.AppliesTo(q, a)).ToList()
                    : adressen.ToList();

                var jfFilmStoreSerie = Erwogen(artSerie);      // Jellyfin sagt Film, der Store sagt Serie
                var jfFolgeStoreFilm = Erwogen(artFilm);       // Jellyfin sagt Folge, der Store sagt Film
                var ohneArt = Erwogen(null);                   // nirgends belegt

                Check("Jellyfin=Movie + Store=Serie: erwogen wird die SERIEN-Adresse, nicht die Film-Adresse (F-M512)",
                    jfFilmStoreSerie.Contains(SubtitleProvider.TvSubtitles)
                    && !jfFilmStoreSerie.Contains(SubtitleProvider.MovieSubtitles)
                    && jfFilmStoreSerie.Contains(SubtitleProvider.Subdl),
                    string.Join(",", jfFilmStoreSerie.Select(Provenance.Name)));

                Check("Jellyfin=Episode + Store=Film: erwogen wird die FILM-Adresse, nicht die Serien-Adresse (F-M512)",
                    jfFolgeStoreFilm.Contains(SubtitleProvider.MovieSubtitles)
                    && !jfFolgeStoreFilm.Contains(SubtitleProvider.TvSubtitles)
                    && jfFolgeStoreFilm.Contains(SubtitleProvider.Subdl),
                    string.Join(",", jfFolgeStoreFilm.Select(Provenance.Name)));

                Check("ohne belegte Art bleiben BEIDE Adressen stehen — 'lieber ein Item zu viel' (F-M499/F-M512)",
                    ohneArt.Count == 3 && ohneArt.Contains(SubtitleProvider.TvSubtitles)
                    && ohneArt.Contains(SubtitleProvider.MovieSubtitles),
                    string.Join(",", ohneArt.Select(Provenance.Name)));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(kindRoot))
                {
                    Directory.Delete(kindRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // Die Reihenfolge ist die Behauptung: STORE zuerst, Item-Typ nur als Rueckfall.
        var artAufruf = seederSource.IndexOf("bool? isSeries = MediaKindFor(item, mediaPath);", StringComparison.Ordinal);
        var artHelfer = seederSource.IndexOf("private static bool? MediaKindFor(BaseItem item, string mediaPath)", StringComparison.Ordinal);
        var artRueckfall = artHelfer < 0
            ? -1
            : seederSource.IndexOf("return item is Episode ? true : item is Movie ? false : null;", artHelfer, StringComparison.Ordinal);
        var artStore = artHelfer < 0 ? -1 : seederSource.IndexOf("StoredMediaKind(mediaPath)", artHelfer, StringComparison.Ordinal);
        var artBlock = artHelfer < 0 ? string.Empty : seederSource.Substring(artHelfer, Math.Min(2600, seederSource.Length - artHelfer));
        Check("der Einreiher fragt den STORE vor dem Item-Typ, und der Rueckfall steht nur dahinter (F-M512)",
            artAufruf > 0 && artHelfer > 0 && artStore > artHelfer && artRueckfall > artStore
            && !seederSource.Contains("isSeries = item is Episode ? true : item is Movie ? false : null;", StringComparison.Ordinal));

        Check("die Store-Frage ist EINE Abfrage ueber den Primaerschluessel — kein Netz, kein TMDb im Scan (F-M512)",
            registrySource.Contains("public bool? MediaKindIsSeries(string? mediaHash)", StringComparison.Ordinal)
            && registrySource.Contains("var row = _db.Media.FindById(mediaHash);", StringComparison.Ordinal)
            // der Einreiher-Block selbst traegt keinen Aufruf, der hinausgeht
            && !artBlock.Contains("Async(", StringComparison.Ordinal)
            && !artBlock.Contains("HttpClient", StringComparison.Ordinal)
            && !artBlock.Contains("_tmdb", StringComparison.Ordinal));

        // ---- F-M514: ein abgewiesenes Anhaengen darf den Store nicht vergiften ---------------------
        // Vorfall 16.11.2026 (jf-test 02:51:48): Der Download schrieb eine Seitentraeger-Zeile fuer
        // eine Datei, deren OSHash noch nicht bekannt war. `EnsureMedia(null)` baute daraus eine
        // Medienzeile OHNE Schluessel; Entity Framework wies das Anhaengen ab ("primary key property
        // 'Id' is null") und liess die Instanz seit EF Core 10 als Detached im Change Tracker stehen.
        // Von da an warf JEDES SaveChanges dieses Kontexts "Unexpected entry.EntityState: Detached",
        // die Aufzeichnung der Worker konnte nichts mehr schreiben, und der Upload-Zyklus endete ohne
        // Lauf ("cycle finished without an upload run", 03:03 und 03:07 wiederholt).
        var ghostRoot = Path.Combine(Path.GetTempPath(), "endpoints-geist-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(ghostRoot))
            {
                Directory.Delete(ghostRoot, true);
            }

            Directory.CreateDirectory(ghostRoot);

            const string geisterHash = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f";
            int medienVorher;
            bool warfSeitentraeger = false;

            using (var ghostDb = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(Path.Combine(ghostRoot, "db"), null))
            {
                var ghostReg = new Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry(ghostDb, null, null);
                var ghostWorker = new Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry(ghostDb, null);
                medienVorher = ghostDb.Media.Count();

                // (a) GENAU der Aufruf des Vorfalls: Seitentraeger ohne bekannten Medien-Hash.
                try
                {
                    ghostReg.MarkDownloaded(geisterHash, null!, "EN",
                        fileName: "Hotel Transylvania 2 (2015).en.srt",
                        path: "/data/movies2/Hotel Transylvania 2 (2015).en.srt");
                }
                catch (Exception)
                {
                    warfSeitentraeger = true;
                }

                var geisterZeile = ghostDb.Sidecars.FindById(geisterHash);
                Check("ein Seitentraeger ohne bekannten Medien-Hash wirft nicht und legt keine Medienzeile ohne Schluessel an (F-M514)",
                    !warfSeitentraeger
                    && ghostDb.Media.Count() == medienVorher
                    && ghostDb.Media.FindAll().All(m => !string.IsNullOrEmpty(m.Id))
                    && geisterZeile != null
                    && string.IsNullOrEmpty(geisterZeile.MediaHash),
                    "warf=" + warfSeitentraeger + " medien=" + ghostDb.Media.Count() + "/" + medienVorher
                    + " randzeile=" + (geisterZeile == null ? "<fehlt>" : "'" + geisterZeile.MediaHash + "'"));

                // (b) Die Aufzeichnung, die im Vorfall zuerst warnte, schreibt weiter eine Zeile.
                ghostWorker.Start(Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.SeederKey, "Seeder", "F-M514");
                ghostWorker.Finish(Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.SeederKey,
                    Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.Outcome.Ok, "F-M514 fertig");

                // (c) Eine Zeile ohne Schluessel wird abgewiesen, nicht angehaengt — in beiden Formen.
                bool warfOhneSchluessel = false;
                try
                {
                    ghostDb.Media.Upsert(new Jellyfin.Plugin.SubdlScribe.Data.MediaEntity
                    {
                        Id = null!,
                        Path = "/data/movies2/ohne-schluessel.mkv",
                    });
                    ghostDb.Media.Upsert(new Jellyfin.Plugin.SubdlScribe.Data.MediaEntity
                    {
                        Id = string.Empty,
                        Path = "/data/movies2/leerer-schluessel.mkv",
                    });
                }
                catch (Exception)
                {
                    warfOhneSchluessel = true;
                }

                Check("eine Zeile ohne Geschaeftsschluessel wird nicht geschrieben und wirft nicht (F-M514)",
                    !warfOhneSchluessel
                    && ghostDb.Media.Count() == medienVorher
                    && ghostDb.Media.FindAll().All(m => !string.IsNullOrEmpty(m.Id)),
                    "warf=" + warfOhneSchluessel + " medien=" + ghostDb.Media.Count());

                // (d) Und der Store bleibt danach schreibfaehig — das ist der Punkt, an dem der
                //     Vorfall den ganzen Upload-Zyklus beendet hat.
                bool warfDanach = false;
                try
                {
                    ghostWorker.Start(Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.UploadWorkerKey, "Upload", "F-M514 danach");
                    ghostWorker.Finish(Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.UploadWorkerKey,
                        Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.Outcome.Ok, "F-M514 danach fertig");
                }
                catch (Exception)
                {
                    warfDanach = true;
                }

                Check("nach einem abgewiesenen Schluessel schreibt der Store weiter (F-M514)",
                    !warfDanach);
            }

            // Aus der DATEI gelesen, nicht aus dem Speicher: beide Worker-Zeilen stehen da.
            using (var ghostRead = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(Path.Combine(ghostRoot, "db"), null))
            {
                var seederZeile = ghostRead.WorkerRuns.FindById(Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.SeederKey);
                var uploadZeile = ghostRead.WorkerRuns.FindById(Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.UploadWorkerKey);
                Check("ein normaler Aufzeichnungslauf schreibt weiter eine Zeile — vor und nach dem abgewiesenen Schluessel (F-M514)",
                    seederZeile?.Outcome == Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.Outcome.Ok
                    && uploadZeile?.Outcome == Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.Outcome.Ok,
                    "seeder=" + (seederZeile?.Outcome ?? "<fehlt>") + " upload=" + (uploadZeile?.Outcome ?? "<fehlt>"));
            }

            // (e) Die Selbstheilung: ein vergifteter Tracker wird wieder benutzbar, und die offene
            //     Aenderung ueberlebt die Reparatur (sonst waere der Fix ein stiller Datenverlust).
            var heilDir = Path.Combine(ghostRoot, "heil");
            Directory.CreateDirectory(heilDir);
            using (var seed = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(heilDir, null))
            {
                seed.WorkerRuns.Upsert(new Jellyfin.Plugin.SubdlScribe.Data.WorkerRunEntity
                {
                    Id = "seeder",
                    Name = "Seeder",
                    Outcome = Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.Outcome.Never,
                });
            }

            bool warfNachGift = false;
            using (var gift = new Jellyfin.Plugin.SubdlScribe.Data.SubdlSqliteContext(
                Path.Combine(heilDir, "subdl-scribe.sqlite")))
            {
                // Der einzige Weg, den Geist heute noch zu erzeugen: am Set vorbei anhaengen.
                try
                {
                    gift.Add(new Jellyfin.Plugin.SubdlScribe.Data.MediaEntity { Id = null!, Path = "/x.mkv" });
                }
                catch (InvalidOperationException)
                {
                    // genau die Abweisung, die den Geist hinterlaesst
                }

                var offen = gift.WorkerRunRows.First(r => r.Id == "seeder");
                offen.Outcome = Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.Outcome.Ok;
                offen.Detail = "ueberlebte die Reparatur";

                try
                {
                    SaveNow(gift);
                }
                catch (Exception)
                {
                    warfNachGift = true;
                }
            }

            using (var heilRead = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(heilDir, null))
            {
                var zeile = heilRead.WorkerRuns.FindById("seeder");
                Check("ein vergifteter Change Tracker heilt beim Speichern und die offene Aenderung geht nicht verloren (F-M514)",
                    !warfNachGift
                    && zeile?.Detail == "ueberlebte die Reparatur"
                    && zeile?.Outcome == Jellyfin.Plugin.SubdlScribe.Registry.WorkerRunRegistry.Outcome.Ok,
                    "warf=" + warfNachGift + " outcome=" + (zeile?.Outcome ?? "<fehlt>")
                    + " detail=" + (zeile?.Detail ?? "<fehlt>"));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(ghostRoot))
                {
                    Directory.Delete(ghostRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // Die Wege, die den Geist gar nicht erst entstehen lassen — am Quelltext, damit ein spaeterer
        // Umbau sie nicht still entfernt.
        var dbQuelle = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Data/SubdlDbContext.cs"));
        var registryQuelle = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        var msubQuelle = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/MsubMovieResolver.cs"));

        Check("EnsureMedia baut ohne Medien-Hash gar keine Zeile — warnen statt anhaengen (F-M514)",
            dbQuelle.Contains("public MediaEntity EnsureMedia(string? mediaHash", StringComparison.Ordinal)
            && dbQuelle.Contains("media row NOT written: no media hash known", StringComparison.Ordinal));

        Check("das gemeinsame Set weist Schluessellose Zeilen ab — fuer JEDE Area und damit jeden Worker (F-M514)",
            dbQuelle.Contains("a row without a business key was not written", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(
                dbQuelle, "string.IsNullOrEmpty\\(key\\)|string.IsNullOrEmpty\\(_keyText\\(e\\)\\)").Count >= 3);

        Check("SaveChanges repariert einen Detached-Geist, statt fuer immer zu scheitern (F-M514)",
            dbQuelle.Contains("public override int SaveChanges()", StringComparison.Ordinal)
            && dbQuelle.Contains("Unexpected entry.EntityState", StringComparison.Ordinal)
            && dbQuelle.Contains("ChangeTracker.Clear();", StringComparison.Ordinal));

        Check("MarkSidecar reicht 'kein Medium bekannt' als leeren Wert durch — die Spalte ist NOT NULL (F-M514)",
            registryQuelle.Contains("public void MarkSidecar(string? contentHash, string? mediaHash", StringComparison.Ordinal)
            && registryQuelle.Contains("mediaHash ??= string.Empty;", StringComparison.Ordinal));

        Check("die Logzeile behauptet kein Speichern, wenn kein Medium die Nummer aufnehmen konnte (F-M514/F-M517)",
            msubQuelle.Contains("ist Film {Id} — {Note}", StringComparison.Ordinal)
            && msubQuelle.Contains("MediaNumberNote.Of(", StringComparison.Ordinal)
            && !msubQuelle.Contains("ist Film {Id} — gespeichert.", StringComparison.Ordinal)
            // F-M517: der Grund nennt BEIDE Adressen, nicht mehr nur den fehlenden Hash
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/MediaNumberNote.cs"))
                .Contains("kein Medien-Hash und keine eindeutige Pfad-Zeile im Store", StringComparison.Ordinal));

        Console.WriteLine();

        // ---- 5xer sidecars never go up (operator order 17.11.2026) -------------------------------
        // The original block (slot 50–99) holds the unsynchronized copies a correction keeps beside
        // its result. They are working files: uploading them would put the raw take next to the
        // corrected one under the same episode. The answer comes from the name alone.
        const string ep = "/media/The Middle.S01E05.MULTI.1080p.WEB-DL.H265-FTMVHD.mkv";
        Check("F-M519: the reserved original block is refused as an upload candidate",
            SidecarNaming.IsOriginalSidecar("/media/The Middle.S01E05.MULTI.1080p.WEB-DL.H265-FTMVHD.en.51.srt", ep)
            && SidecarNaming.IsOriginalSidecar("/media/The Middle.S01E05.MULTI.1080p.WEB-DL.H265-FTMVHD.en.52.srt", ep)
            && SidecarNaming.IsOriginalSidecar("/media/The Middle.S01E05.MULTI.1080p.WEB-DL.H265-FTMVHD.en.50.srt", ep)
            && SidecarNaming.IsOriginalSidecar("/media/The Middle.S01E05.MULTI.1080p.WEB-DL.H265-FTMVHD.de.sdh.99.srt", ep));
        Check("F-M519: the corrected block (slots 1–49) stays an upload candidate",
            !SidecarNaming.IsOriginalSidecar("/media/The Middle.S01E05.MULTI.1080p.WEB-DL.H265-FTMVHD.en.01.srt", ep)
            && !SidecarNaming.IsOriginalSidecar("/media/The Middle.S01E05.MULTI.1080p.WEB-DL.H265-FTMVHD.en.49.srt", ep)
            && !SidecarNaming.IsOriginalSidecar("/media/The Middle.S01E05.MULTI.1080p.WEB-DL.H265-FTMVHD.de.srt", ep));
        Check("F-M519: the upload candidate list drops the originals and keeps the corrected files",
            Jellyfin.Plugin.SubdlScribe.Pipeline.UploadPipeline.FindLooseSrts(ep)
                .All(x => !SidecarNaming.IsOriginalSidecar(x.Path, ep)));


        // ---- F-M521: die tvst-Familie schreibt ihre Anfragen mit (Operator 17.11.2026) -------------
        // Vorher schwieg beide Adressen: „0 tvsubtitles candidate(s)" sah aus wie ein Lauf, der die
        // Seite nie gefragt hat. Zwei Zeilen je Anfrage auf Debug, die Kennung in der Sammelzeile.
        Check("F-M521: the request line names method and path, never the query",
            SiteTrace.Call("GET", "https://www.tvsubtitles.net/search.php?q=Grey%27s+Anatomy")
                == "API call GET https://www.tvsubtitles.net/search.php");
        Check("F-M521: the response line repeats the path and carries the status",
            SiteTrace.Response("POST", "https://www.moviesubtitles.org/add.php?tvid=13392", 302)
                == "API response POST https://www.moviesubtitles.org/add.php → 302");
        Check("F-M521: the summary text says how the site answered",
            SiteTrace.Answer(200, 3) == "HTTP 200, 3 hit(s)"
            && SiteTrace.Answer(0, 0) == "no answer, 0 hit(s)");
        Check("F-M521: both addresses of the family carry a debug channel",
            typeof(TvSubtitlesClient).GetEvent("DebugTrace") is not null
            && typeof(MovieSubtitlesClient).GetEvent("DebugTrace") is not null
            && typeof(MovieSubtitlesClient).GetProperty("LastStatus") is not null);
        string dlSource = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "DownloadPipeline.cs")).ConfigureAwait(false);
        string ulSource = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "UploadPipeline.cs")).ConfigureAwait(false);
        Check("F-M521: both directions subscribe the channel — download and upload",
            dlSource.Contains("_tvClient.DebugTrace += OnApiTrace", StringComparison.Ordinal)
            && dlSource.Contains("_msubClient.DebugTrace += OnApiTrace", StringComparison.Ordinal)
            && ulSource.Contains("_tvstClient.DebugTrace += OnApiTrace", StringComparison.Ordinal)
            && ulSource.Contains("_msubClient.DebugTrace += OnApiTrace", StringComparison.Ordinal));

        // ---- F-M524: ein gescheiterter Zyklus nennt die Stelle -------------------------------------
        string dispatchQuelle = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "ScheduledTasks", "SubdlEventDispatcher.cs"))
            .ConfigureAwait(false);
        Check(
            "F-M524: a failed cycle logs the exception with its stack — the message alone named no site",
            dispatchQuelle.Contains("_logger.LogError(\n", StringComparison.Ordinal)
            && dispatchQuelle.Contains("MarkCycleFailure(ex.Message)", StringComparison.Ordinal)
            && dispatchQuelle.Contains("cycle failed: {Msg} (see the exception below for the site)", StringComparison.Ordinal));

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

    /// <summary>Liest eine Datei in einem nicht-async Helfer — CA1849 prueft nur den async-Pfad.</summary>
    private static string ReadText(string path) => File.ReadAllText(path);

    /// <summary>Speichert synchron aus einem nicht-async Helfer — CA1849 prueft nur den async-Pfad.</summary>
    private static void SaveNow(Jellyfin.Plugin.SubdlScribe.Data.SubdlSqliteContext ctx) => ctx.SaveChanges();

    /// <summary>Oeffnet einen ZIP-Eintrag im nicht-async Helfer (dieselbe CA1849-Regel).</summary>
    private static System.IO.Stream OpenForRead(this System.IO.Compression.ZipArchiveEntry entry) => entry.Open();

    /// <summary>Schreibt in einen Strom im nicht-async Helfer.</summary>
    private static void WriteAll(System.IO.Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

    /// <summary>F-M501: laeuft der Aufruf in eine Ausnahme? (Fuer die Grenzen von BuildAddShow.)</summary>
    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// F-M501: die Seiten von tvsubtitles.net, die die Aufloesung braucht — die Serienliste unter
    /// <c>/add.html</c> und das Anlege-Formular unter <c>/add-tvshow1.php</c>. Die Liste ist
    /// veraenderlich, damit "erst anlegen, dann erneut suchen" wirklich geprueft wird und nicht nur
    /// behauptet. Die Zaehler sind der Beweis fuer "kein Netzzugriff" bzw. "genau einmal angelegt".
    /// </summary>
    private sealed class TvstSiteHandler : HttpMessageHandler
    {
        private readonly List<(int Id, string Name)> _shows = new();

        public TvstSiteHandler(int id, string name) => _shows.Add((id, name));

        /// <summary>GETs auf die Serienliste.</summary>
        public int Gets { get; private set; }

        /// <summary>POSTs auf das Anlege-Formular.</summary>
        public int Posts { get; private set; }

        /// <summary>POSTs auf das Upload-Formular.</summary>
        public int Uploads { get; private set; }

        /// <summary>Der Rumpf der letzten Anlage.</summary>
        public string? LastBody { get; private set; }

        /// <summary>Der Rumpf des letzten Uploads.</summary>
        public string? LastUploadBody { get; private set; }

        /// <summary>Antwortet die Seite auf die Anlage mit diesem Status?</summary>
        public int PostStatus { get; set; } = 200;

        /// <summary>Wird die Serie nach erfolgreicher Anlage in die Liste aufgenommen?</summary>
        public (int Id, string Name)? AddAfterPost { get; set; }

        /// <summary>Antwortet die Seite ohne Auswahlfeld (unparsbare/leere Liste)?</summary>
        public bool EmptyList { get; set; }

        /// <summary>Die Anfragen in ihrer Reihenfolge — "GET", "POST" (Anlage) oder "UPLOAD".</summary>
        public List<string> Order { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _ = body;

            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("add-tvshow1.php", StringComparison.Ordinal))
            {
                Order.Add("POST");
                Posts++;
                LastBody = body;
                if (PostStatus is < 200 or >= 300)
                {
                    return new HttpResponseMessage((HttpStatusCode)PostStatus);
                }

                if (AddAfterPost is { } add)
                {
                    _shows.Add(add);
                }

                // F-M516: so antwortet die Seite wirklich (gemessen 16.11.2026) — 302 auf
                // /add-tvshow.php?done=yes, leerer Rumpf, keine Id. Der Stub muss das spiegeln,
                // sonst prueft die Suite eine Antwort, die es nicht gibt.
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://www.tvsubtitles.net/add-tvshow.php?done=yes");
                return redirect;
            }

            if (request.Method == HttpMethod.Post)
            {
                Order.Add("UPLOAD");
                Uploads++;
                LastUploadBody = body;
                return Html("<html><body>ok</body></html>");
            }

            Order.Add("GET");
            Gets++;
            if (request.RequestUri!.AbsolutePath.EndsWith("search2.php", StringComparison.Ordinal))
            {
                // F-M516: die Seitensuche — Treffer sind Links /tvshow-<id>.html mit Zeitraum daneben.
                // F-M537: hinter qs= steht jetzt noch &client=<tag> — der Wert endet am naechsten &.
                var q = request.RequestUri.Query.Contains("qs=", StringComparison.Ordinal)
                    ? Uri.UnescapeDataString(
                        request.RequestUri.Query[(request.RequestUri.Query.IndexOf("qs=", StringComparison.Ordinal) + 3)..].Split('&')[0])
                    : string.Empty;
                var hits = _shows
                    .Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (hits.Count == 0)
                {
                    return Html("<html><body><p class=\"description\">Search results</p><p>No results found</p></body></html>");
                }

                var sbHits = new StringBuilder("<html><body><p class=\"description\">Search results</p><p><ul>");
                foreach (var (id, name) in hits)
                {
                    sbHits.Append("<li><div style=\"\"><a href=\"/tvshow-").Append(id).Append(".html\">")
                        .Append(name).Append(" (2009-2018)</a></div></li>");
                }

                return Html(sbHits.Append("</ul></p></body></html>").ToString());
            }

            if (EmptyList)
            {
                return Html("<html><body><p>kein Formular</p></body></html>");
            }

            var sb = new StringBuilder("<select name=\"tvshow\" size=\"1\">");
            sb.Append("<option value=\"0\">- Select TV-Show -</option>");
            foreach (var (id, name) in _shows)
            {
                sb.Append("<option value=\"").Append(id).Append("\">").Append(name).Append("</option>");
            }

            sb.Append("</select>");
            return Html("<html><body>" + sb + "</body></html>");
        }

        private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };
    }

    /// <summary>F-M501: eine feste JSON-Antwort fuer den TMDb-Serien-Datensatz.</summary>
    private sealed class JsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public JsonHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>F-M507: liest die Spaltenliste einer Tabelle — die Gegenprobe zur Migration.</summary>
    private static HashSet<string> Spalten(string dbFile, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbFile);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(" + table + ");";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>F-M507: ein Wegwerf-Verzeichnis, das sich selbst wieder wegraeumt.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir(string name)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "subdl-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }
    }

    /// <summary>
    /// F-M507: die Seiten von moviesubtitles.org, die der Endpunkt braucht — die Suche (zwei Filme,
    /// „Dune" 1984 und „Dune: Part One" 2021) und die Filmseite mit drei Eintraegen (zwei englische,
    /// ein deutscher). Der Handler zaehlt jede Anfrage, damit „ein Abruf je Quelle und Sprache" messbar
    /// ist statt behauptet.
    /// </summary>
    private sealed class MsubSiteHandler : HttpMessageHandler
    {
        public int Gets { get; private set; }

        public int Posts { get; private set; }

        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            LastUrl = url;
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><b>ok</b></html>", Encoding.UTF8, "text/html"),
                });
            }

            Gets++;

            if (url.Contains("/search.php", StringComparison.Ordinal))
            {
                // Die Suchliste: das Jahr steht im Linktext, die Seite schreibt den Artikel hinter den
                // Komma ("Dune" hat keinen).
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><div class=\"movies\">"
                        + "<a href=\"/movie-101.html\">Dune (1984)</a>"
                        + "<a href=\"/movie-102.html\">Dune: Part One (2021)</a>"
                        + "</div></html>", Encoding.UTF8, "text/html"),
                });
            }

            if (url.EndsWith("movie-102.html", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(FilmSeite, Encoding.UTF8, "text/html"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static readonly string FilmSeite =
            "<html><body>"
            + "<h2>Dune: Part One (2021)</h2>"
            + "<p class=\"description\"><img src=\"images/flags/en.gif\"> 3 subtitles, 12 downloads</p>"
            + Eintrag("5001", "en", "WEB-DL", "GROUPA")
            + Eintrag("5002", "en", "BluRay", "GROUPB")
            + Eintrag("5003", "de", "WEB-DL", "GROUPC")
            + "<span style=\"color:#666666\"><b>English  subtitles:</b></span>"
            + "<span style=\"color:#666666\"><b>German  subtitles:</b></span>"
            + "</body></html>";

        private static string Eintrag(string id, string lang, string rip, string release) =>
            "<a href=\"/subtitle-" + id + ".html\" title=\"Download Dune: Part One (2021) subtitles\">"
            + "<div class=\"subtitle\"><b>Dune: Part One 2021 " + lang + " subtitles (" + rip + "." + release
            + ")</b><table><tr>"
            + "<td title=\"lang\"><img src=\"images/flags/" + lang + ".gif\"></td>"
            + "<td title=\"Rip\">" + rip + "</td>"
            + "<td title=\"release\">" + release + "</td>"
            + "<td title=\"parts\">1</td>"
            + "<td title=\"uploaded\">2026-01-01</td>"
            + "<td title=\"downloaded\">12</td>"
            + "</tr></table></div></a>";
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
            // F-M537: der Client haengt client=<tag> an jede Anfrage — der Fixture-Name bleibt der Pfad.
            var path = url.Split('?')[0].Replace("https://www.tvsubtitles.net/", string.Empty, StringComparison.Ordinal);

            if (path == "add.html")
            {
                return Html(ReadText(Path.Combine(_dir, "add-tvshow-select.html")));
            }

            if (path.StartsWith("download-", StringComparison.Ordinal))
            {
                // The site answers the download URL with the ZIP in one of its two shapes (F-M482).
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("The Middle - 2x24 - Back To Summer.HDTV.FEVER.en.srt");
                    using var stream = entry.OpenForRead();
                    var body = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhello\n");
                    WriteAll(stream, body);
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

            return Html(ReadText(Path.Combine(_dir, file)));
        }

        private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };
    }
}
