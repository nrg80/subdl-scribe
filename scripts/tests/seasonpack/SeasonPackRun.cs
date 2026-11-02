// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR
// PURPOSE. See the GNU General Public License for more details.
//
// T163 / F-M372: a season goes up as ONE pack — but only when the season is complete and
// every episode passed the gates the single path applies.
//
// THE RULES THE OPERATOR SET (01.11.2026):
//
//   completeness  "ist die season vollzählig" — asked at IMDb, because the claim
//                 is_full_season=true is about the season that AIRED, not about the folder.
//                 Eight of ten episodes is a complete folder and an incomplete season.
//   one bad cue   a season whose episode failed qa-invalid-srt stays single, as does an
//                 incomplete season. The pack never absorbs a refusal.
//   one source    lang, hi, quality, framerate and releases are single fields on SubDL's
//                 side, so mixed sources cannot share a container.
//   the name      {imdb}.S{ss}.{md5 of the zip}.{LANG}.zip — the container's own hash, built
//                 byte-stable, otherwise the same season hashes differently every run.
//
// Usage: dotnet build Jellyfin.Plugin.SubdlSync -c Release
//        dotnet run --project scripts/tests/seasonpack/seasonpack.csproj -- [repo-root]

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Microsoft.Extensions.Logging;

namespace SeasonPackRun;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok)
        {
            Failures.Add(what);
            Console.WriteLine("  FAIL  " + what);
        }
    }

    private static string Srt(int episode) =>
        string.Join("\n\n", Enumerable.Range(1, 30).Select(i =>
            $"{i}\n00:00:{i:00},000 --> 00:00:{i:00},800\nEpisode {episode} line {i}\n"));

    /// <summary>One pack member, built without a library: the coordinator only needs name and numbers.</summary>
    private static SeasonPackCoordinator.Candidate Candidate(int episode) => new(
        new MediaBrowser.Controller.Entities.TV.Episode { Name = $"Episode {episode}" },
        $"item-{episode}",
        "0123456789abcdef0123456789abcdef",
        "tt1",
        null,
        1,
        episode,
        "EN",
        false,
        false,
        0,
        Srt(episode),
        $"Show.S01E{episode:00}.1080p.WEB.x264-NTb",
        "web",
        "23.976",
        1920,
        1080,
        "h264");

    /// <summary>
    /// The pipeline's filler, replaced: it answers with a candidate or refuses, and it keeps the order
    /// it was asked in so the harness can assert that only the missing episode is asked for.
    /// </summary>
    private sealed class FakeFiller : IPackFiller
    {
        private readonly HashSet<int> _refuse;
        private readonly int _answer;

        public FakeFiller(IEnumerable<int> refuse, int answer)
        {
            _refuse = refuse.ToHashSet();
            _answer = answer;
        }

        public List<int> Calls { get; } = new();

        public Task<SeasonPackCoordinator.Candidate?> FillAsync(
            string imdbId, int season, int episode, string lang, bool hearingImpaired, CancellationToken ct)
        {
            Calls.Add(episode);
            if (_refuse.Contains(episode))
            {
                return Task.FromResult<SeasonPackCoordinator.Candidate?>(null);
            }

            return Task.FromResult<SeasonPackCoordinator.Candidate?>(Candidate(_answer > 0 ? _answer : episode));
        }
    }

    /// <summary>Captures the Normal lines the coordinator writes, so the harness can read them.</summary>
    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _lines;

        public ListLogger(List<string> lines) => _lines = lines;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Add(formatter(state, exception));
    }

    /// <summary>
    /// A coordinator in dry-run mode: it walks the whole pack decision and stops before the transfer, so
    /// no HTTP client is used and the registry is never asked to write.
    /// </summary>
    private static SeasonPackCoordinator NewCoordinator(
        List<string> log,
        IReadOnlyDictionary<(string ImdbId, int Season), IReadOnlyList<int>> complete,
        ISet<(string ImdbId, int Season)> packable,
        IPackFiller filler,
        Jellyfin.Plugin.SubdlScribe.Configuration.PluginConfiguration config)
        => new(
            new Jellyfin.Plugin.SubdlScribe.Api.SubdlApiClient(new System.Net.Http.HttpClient()),
            null!,
            new ListLogger(log),
            config,
            complete,
            packable,
            (_, _) => { },
            _ => { },
            _ => { },
            filler);

    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";

        // ── 1. What counts as aired ─────────────────────────────────────────────
        Console.WriteLine("1. Aired episode numbers");
        var got = SeasonPack.AiredNumbers(new[] { 0, 3, 1, 2, 10, 2 });
        Check(got.SequenceEqual(new[] { 1, 2, 3, 10 }), "zero is dropped, rest sorted and deduplicated");
        Check(SeasonPack.AiredNumbers(Array.Empty<int>()).Count == 0, "no numbers, no season");
        // Measured shape at IMDb: Game of Thrones S01 reports eleven entries — ten episodes
        // plus the unaired original pilot at E0.
        var thrones = Enumerable.Range(0, 11).ToList();
        Check(SeasonPack.AiredNumbers(thrones).Count == 10, "eleven IMDb entries with an E0 are ten episodes");

        // ── 2. Is the season complete ───────────────────────────────────────────
        Console.WriteLine("2. Completeness");
        var aired8 = Enumerable.Range(1, 8).ToList();
        Check(SeasonPack.IsComplete(aired8, aired8), "eight of eight is complete");
        Check(!SeasonPack.IsComplete(aired8, Enumerable.Range(1, 7)), "seven of eight is not");
        Check(!SeasonPack.IsComplete(aired8, Enumerable.Range(1, 9)), "nine of eight is not");
        Check(!SeasonPack.IsComplete(aired8, new[] { 1, 2, 3, 4, 5, 6, 7, 9 }), "a gap at the end is not");
        Check(!SeasonPack.IsComplete(aired8, new[] { 1, 2, 3, 4, 5, 6, 7 }), "a missing episode is not");
        Check(!SeasonPack.IsComplete(Array.Empty<int>(), Array.Empty<int>()), "an unanswered season is never complete");
        Check(SeasonPack.IsComplete(new[] { 0, 1, 2, 3 }, new[] { 1, 2, 3 }), "an E0 on the aired side does not demand an episode");
        Check(SeasonPack.IsComplete(new[] { 1, 2, 3 }, new[] { 0, 1, 2, 3 }), "an E0 in the library does not block the pack");
        Check(SeasonPack.IsComplete(new[] { 5 }, new[] { 5 }), "a one-episode season can be complete");

        // ── 3. The name carries the pack's identity ─────────────────────────────
        Console.WriteLine("3. Pack name");
        var hashes8 = Enumerable.Range(1, 8)
            .Select(e => Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(Srt(e))).ToList();
        string packHash = SeasonPack.PackHash(hashes8);
        Check(packHash.Length == 32, "the pack hash is an MD5");
        Check(SeasonPack.PackHash(hashes8) == packHash, "the same season hashes the same");
        Check(SeasonPack.PackHash(hashes8.AsEnumerable().Reverse()) != packHash, "the episode order is part of the identity");
        var corrected = hashes8.ToList();
        corrected[2] = Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(Srt(3) + "\n99\n00:01:00,000 --> 00:01:01,000\nchanged\n");
        Check(SeasonPack.PackHash(corrected) != packHash, "a corrected episode is a different pack");

        string name = SeasonPack.Name("tt0944947", 1, packHash, "DE");
        Check(name == $"tt0944947.S01.{packHash}.DE", "the name is {imdb}.S{ss}.{pack hash}.{LANG}");
        Check(!name.EndsWith(".zip", StringComparison.Ordinal), "the .zip is SubDL's to append — the name field becomes the zip name");
        Check(SeasonPack.Name("tt0944947", 12, "abc", "EN") == "tt0944947.S12.abc.EN", "the season is two digits, the language keeps its case");

        // ── 4. What SubDL documents about a packed upload ───────────────────────
        Console.WriteLine("4. Documented upload shape");
        var api = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSsync/Api/SubdlApiClient.cs".Replace("Ssync", "Sync")));
        Check(api.Contains("JsonSerializer.Serialize(fileNIds)", StringComparison.Ordinal), "step 3 sends the ARRAY of file ids");
        Check(api.Contains("[\"is_full_season\"] = \"true\"", StringComparison.Ordinal), "the pack claims a full season");
        Check(api.Contains("[\"is_full_season\"] = \"false\"", StringComparison.Ordinal), "a single upload still claims nothing");
        Check(api.Contains("[\"name\"] = packName", StringComparison.Ordinal), "the name field carries the pack name SubDL zips under");
        // F-M377 (measured 01.11.2026): ef/ee are the reason our packs were filed as episode 1 with
        // full_season=false. The file set is the range — the fields must NOT travel.
        Check(api.IndexOf("[\"ef\"] = episodeFrom", StringComparison.Ordinal) < 0
              && api.IndexOf("[\"ee\"] = episodeTo", StringComparison.Ordinal) < 0,
            "a pack carries no ef/ee — SubDL files it as episode 1 when they are present");
        Check(api.Contains("F-M377"), "… and the measurement is recorded at the call site");
        Check(api.Contains("UploadSingleFileAsync", StringComparison.Ordinal), "one file per uploadSingleSubtitle call");
        Check(api.Contains("pack-file-refused", StringComparison.Ordinal), "a refused file is recognised as such");
        Check(api.Contains("JsonSerializer.Serialize(releaseNames)", StringComparison.Ordinal),
            "the pack carries every episode's release name — the field is an array");

        // ── 5. The inner names are exactly the single upload's ──────────────────
        Console.WriteLine("5. Inner file names");
        string hash = "0123456789abcdef0123456789abcdef";
        Check(SeasonPack.NeutralFileName(true, "tt0944947", 1, 3, "DE", hash) == $"tt0944947.S01E03.{hash}.DE.srt", "series form carries SxxEyy");
        Check(SeasonPack.NeutralFileName(false, "tt0944947", 0, 0, "DE", hash) == $"tt0944947.{hash}.DE.srt", "movie form carries no episode");
        // F-M379 (operator decision 01.11.2026): the SDH variant has to be readable in the NAME. The
        // `hi` field SubDL carries ends where the name begins — the zip, the member file and the entry
        // title show nothing but the name, so two English packs were indistinguishable but for the hash.
        Check(SeasonPack.Name("tt0944947", 1, packHash, "EN", true) == $"tt0944947.S01.{packHash}.EN.SDH",
            "an SDH pack states the variant in its name");
        Check(SeasonPack.NeutralFileName(true, "tt0944947", 1, 3, "EN", hash, true) == $"tt0944947.S01E03.{hash}.EN.SDH.srt",
            "an SDH single upload states it as well");
        Check(SeasonPack.MemberFileName(1, 1, "The Emperor's Peace", "EN", true) == "S01E01 - The Emperor's Peace.en.SDH.srt",
            "a pack member names the variant too");
        Check(SeasonPack.NeutralFileName(false, "tt0944947", 0, 0, "EN", hash, true) == $"tt0944947.{hash}.EN.SDH.srt",
            "the movie form states it as well");
        Check(SeasonPack.HiMarker(false) == string.Empty
              && SeasonPack.Name("tt0944947", 1, packHash, "EN") == $"tt0944947.S01.{packHash}.EN"
              && SeasonPack.MemberFileName(1, 1, "The Emperor's Peace", "EN") == "S01E01 - The Emperor's Peace.en.srt",
            "the regular track keeps its old name unchanged");

        // ── 6. The rules in the code, not only in the arithmetic ────────────────
        Console.WriteLine("6. Rules wired");
        var config = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"));
        Check(config.Contains("public bool UploadSeasonPacks { get; set; } = true;", StringComparison.Ordinal),
            "the switch exists and is on by default");

        var page = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"));
        Check(page.Contains("id=\"UploadSeasonPacks\"", StringComparison.Ordinal), "the switch is a field in the upload section");
        Check(page.Contains("config.UploadSeasonPacks = document.querySelector", StringComparison.Ordinal)
              && page.Contains("document.querySelector('#UploadSeasonPacks').checked = config.UploadSeasonPacks", StringComparison.Ordinal),
            "the config page reads and writes it");

        Check(api.Contains("public async Task<UploadResult> UploadSeasonPackAsync", StringComparison.Ordinal), "the pack has its own upload call");

        var coordinator = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
        Check(coordinator.Contains("do not share one source", StringComparison.Ordinal), "mixed sources never share a container");
        Check(coordinator.Contains("season pack skipped — {Imdb} S{Season} [{Lang}]: {Have} of {Want} episode(s)", StringComparison.Ordinal),
            "an incomplete buffer says so and falls back to singles");
        Check(coordinator.Contains("pack-file-refused", StringComparison.Ordinal), "a refused container falls back to singles, not to loss");
        Check(coordinator.Contains("subdlId: packId", StringComparison.Ordinal), "every episode of a pack carries the pack's id");
        Check(!coordinator.Contains("c.ReleaseName).Distinct().Count() == 1", StringComparison.Ordinal),
            "differing release names do not block a pack — only quality and frame rate do");
        Check(coordinator.Contains("PackReleases(ordered, key)", StringComparison.Ordinal),
            "a pack builds its release list through the season label");
        Check(coordinator.Contains("SeasonPack.PackRelease(", StringComparison.Ordinal),
            "the pack's one release entry is built from the season form of the episode releases");
        Check(coordinator.Contains("return new List<string>\n        {\n            SeasonPack.PackRelease(")
              || coordinator.Contains("SeasonPack.PackRelease("),
            "exactly one release entry goes to SubDL");
        Check(!coordinator.Contains("ordered.Select(c => c.ReleaseName).Distinct().ToList()", StringComparison.Ordinal),
            "the episode release names are never handed to SubDL as the pack's release list");

        // F-M374: SubDL titles a full-season upload after releases[0] and ignores `name`
        // (measured 01.11.2026 against /user/mySubtitles), so the first entry has to name the season.
        Check(SeasonPack.ReleaseLabel("Lanterns", "tt26545992", 1, "EN", false) == "Lanterns S01 - complete season [EN]",
            "the label names series, season and language");
        Check(SeasonPack.ReleaseLabel(null, "tt26545992", 3, "EN", true) == "tt26545992 S03 - complete season (SDH) [EN]",
            "without a series name the IMDb id carries the label and SDH is marked");
        Check(SeasonPack.ReleaseLabel("  ", "tt26545992", 2, "FR", false) == "tt26545992 S02 - complete season [FR]",
            "a blank series name falls back to the IMDb id");

        // F-M374: one release entry per pack — other uploaders name a season release there, and
        // this plugin mixes nothing: differing groups name no release at all.
        var foundation = SeasonPack.PackRelease(
            new[]
            {
                "Foundation.S01E01.1080p.WEB.H264-NHTFS",
                "Foundation.S01E02.1080p.WEB.H264-NHTFS",
                "Foundation.S01E03.1080p.WEB.H264-NHTFS",
            },
            "Foundation", "tt0804484", 1, "EN", false);
        Check(foundation == "Foundation.S01.1080p.WEB.H264-NHTFS",
            "episodes from one release give one season release name");
        Check(!foundation.Contains("E01", StringComparison.Ordinal), "the episode marker never survives");
        var mixed = SeasonPack.PackRelease(
            new[]
            {
                "Foundation.S02E01.1080p.WEB.H264-NHTFS",
                "Foundation.S02E02.1080p.WEB.H264-SuccessfulCrab",
                "Foundation.S02E03.1080p.WEB.H264-NHTFS",
            },
            "Foundation", "tt0804484", 2, "EN", false);
        Check(mixed == "Foundation S02 - complete season [EN]",
            "two groups never get merged into one release name — the label stands in");
        var dashed = SeasonPack.PackRelease(
            new[] { "Lanterns - S01E01 - Test", "Lanterns - S01E08 - Test" },
            "Lanterns", "tt26545992", 1, "EN", false);
        Check(dashed == "Lanterns - S01 - Test", "the marker is cut out, not replaced by junk");
        // F-M380 (measured 01.11.2026, Dexter: Resurrection): the WIDTH decides the resolution token.
        // 1920x800 is a cropped 1080p encode and was named 720p by the height-only rule.
        Check(SeasonPack.MediaRelease("Dexter: Resurrection", "tt33043892", 1, "web", 1920, 800, "hevc")
              == "Dexter.Resurrection.S01.1080p.WEB.x265",
            "a cropped 1080p encode is 1080p, and the colon of the series name does not survive");
        Check(SeasonPack.MediaRelease("Foundation", "tt0804484", 1, "web", null, 800, "h264")
              == "Foundation.S01.720p.WEB.x264", "without a width the height stays the fallback");
        Check(SeasonPack.MediaRelease("Dexter: Resurrection", "tt33043892", 1, "web", 1920, 800, "hevc", "Rapta")
              == "Dexter.Resurrection.S01.1080p.WEB.x265-Rapta",
            "a group the file name states is appended — never invented");

        // The group is read off the file name, and only when that name states one.
        Check(SeasonPack.ReleaseGroup("Dexter.Resurrection.S01.1080p.WEBRip.AAC5.1.10bits.x265-Rapta") == "Rapta",
            "the last dash segment of a release name is the group");
        Check(SeasonPack.ReleaseGroup("Dexter - Resurrection - S01E01 - A Beating Heart...") == null,
            "an episode title after the last dash is no group");
        Check(SeasonPack.ReleaseGroup("Show.S01.1080p.WEB.x265-AAC5.1") == null,
            "a technical token with a dot is no group");
        Check(SeasonPack.ReleaseGroup(null) == null && SeasonPack.ReleaseGroup("") == null,
            "no name, no group");

        Check(SeasonPack.PackRelease(new[] { "Reacher.3x04.1080p.WEB" }, "Reacher", "tt9288030", 3, "EN", false)
              == "Reacher.S03.1080p.WEB", "1x05 style markers reduce to the season too");
        Check(SeasonPack.PackRelease(new[] { "Folge eins", "Folge zwei" }, "Lanterns", "tt26545992", 1, "EN", false)
              == "Lanterns S01 - complete season [EN]",
            "a release name without a marker cannot be reduced and the label stands in");
        Check(SeasonPack.PackRelease(Array.Empty<string>(), "Lanterns", "tt26545992", 1, "EN", true)
              == "Lanterns S01 - complete season (SDH) [EN]", "an empty list still yields a title");

        // F-M376: when no name carries a release, the file's own facts name the season.
        // F-M378: SubDL reads s/e from each member's own name — a season in the member name turns the
        // pack into an episode range.
        Check(SeasonPack.MemberFileName(1, 1, "The Emperor's Peace", "EN") == "S01E01 - The Emperor's Peace.en.srt",
            "a member names its season and episode — SubDL reads both from that name");
        Check(SeasonPack.MemberFileName(2, 7, "Mysteries and Martyrs", "DE") == "S02E07 - Mysteries and Martyrs.de.srt",
            "season and episode follow the pack's own season");
        Check(SeasonPack.MemberFileName(3, 7, null, "FR") == "S03E07.fr.srt", "a missing title leaves number and episode");
        Check(SeasonPack.MemberFileName(1, 2, "Bad/Name: Here?", "ES") == "S01E02 - Bad Name Here.es.srt",
            "file-name characters are stripped, the language is lower case");

        Check(SeasonPack.MediaRelease("Foundation", "tt0804484", 1, "web", 1920, 1080, "h264")
              == "Foundation.S01.1080p.WEB.x264", "resolution, source and codec from the file name the season");
        Check(SeasonPack.MediaRelease("Dark Matter", "tt1", 2, "bluray", 3840, 2160, "hevc")
              == "Dark.Matter.S02.2160p.BluRay.x265", "spaces and codec names are release-shaped");
        Check(SeasonPack.MediaRelease("Foundation", "tt0804484", 1, "web", null, null, null) == "Foundation.S01.WEB",
            "a file without a video stream still names its source");
        Check(SeasonPack.MediaRelease("Foundation", "tt0804484", 1, null, null, null, null) == null,
            "no facts at all is no release");
        Check(SeasonPack.MediaRelease(null, "tt0804484", 3, "hdtv", 1280, 720, "av1") == "tt0804484.S03.720p.HDTV.AV1",
            "an unknown series name is replaced by the IMDb id");
        Check(SeasonPack.PackRelease(
                new[] { "S01E01 - The Emperor's Peace", "S01E02 - Preparing to Live" },
                "Foundation", "tt0804484", 1, "EN", false,
                SeasonPack.MediaRelease("Foundation", "tt0804484", 1, "web", 1920, 1080, "h264"))
              == "Foundation.S01.1080p.WEB.x264",
            "names without a release fall through to the built release");
        Check(SeasonPack.PackRelease(
                new[] { "Foundation.S01E01.1080p.WEB.x264-NTb", "Foundation.S01E02.1080p.WEB.x264-NTb" },
                "Foundation", "tt0804484", 1, "EN", false,
                "Foundation.S01.1080p.WEB.x264")
              == "Foundation.S01.1080p.WEB.x264-NTb",
            "a release the names carry still wins over the built one");
        Check(SeasonPack.PackRelease(
                new[] { "S01E01 - One", "S01E02 - Two" },
                "Foundation", "tt0804484", 1, "FR", false, null)
              == "Foundation S01 - complete season [FR]",
            "no names, no facts — the neutral label stands in");
        Check(coordinator.Contains("DRY-RUN would upload season pack", StringComparison.Ordinal), "a dry run names the pack it would send and sends nothing");
        Check(coordinator.Contains("ReadPackId(result.Response) ?? name", StringComparison.Ordinal),
            "with no id in the response the pack name is the identity the rows carry");

        var pipeline = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check(pipeline.Contains("&& !cand.IsLoose && !cand.Forced", StringComparison.Ordinal), "no pack for a loose sidecar or a forced track");
        Check(pipeline.Contains("await packCoordinator.FlushAsync(runCt)", StringComparison.Ordinal), "the run closes its buffers before the summary");
        Check(pipeline.Contains("SeasonPack.IsComplete(aired, entry.Value)", StringComparison.Ordinal), "the pipeline asks the completeness rule");
        Check(pipeline.Contains("SeasonEpisodeIndex.AiredAsync", StringComparison.Ordinal), "and gets the answered season from IMDb");

        // F-M373: a pack needs the WHOLE season in this run. Episodes that are already up
        // individually cannot join, so a season that is only partly queued is worked as singles
        // right away — and the seasons that can still pack are closed in one go, one after another.
        var queuedItems = new List<SeasonPack.RunItem>
        {
            new("e5", "tt1", 1, 5),
            new("e1", "tt1", 1, 1),
            new("m1", null, -1, -1),
            new("e9", "tt2", 2, 3),
            new("e3", "tt1", 1, 3),
        };
        var order = SeasonPack.ClosureOrder(queuedItems, new HashSet<(string, int)> { ("tt1", 1) });
        Check(order.Take(3).SequenceEqual(new[] { "e1", "e3", "e5" }),
            "a packable season runs first and in episode order, so it closes in one go");
        Check(order.Skip(3).SequenceEqual(new[] { "m1", "e9" }),
            "everything outside the plan keeps the order the queue gave it");
        Check(SeasonPack.ClosureOrder(queuedItems, new HashSet<(string, int)>())
                .SequenceEqual(queuedItems.Select(i => i.Id)),
            "with nothing packable the run order is untouched");
        Check(!SeasonPack.IsComplete(new[] { 1, 2, 3 }, new[] { 1, 3 }),
            "a season that is only partly queued is not the season that aired");
        Check(coordinator.Contains("&& _packable.Contains((imdbId!, season))", StringComparison.Ordinal),
            "buffering still asks the plan, which now holds every complete season");
        Check(pipeline.Contains("var packable = PlanSeasons(completeSeasons, items);", StringComparison.Ordinal),
            "the plan is computed before the loop, from the queue");
        // F-M394: the plan no longer asks the QUEUE whether a season may be packed — it takes every
        // season that is complete on disk and lets the fill close the gap. The old rule left a dead
        // zone between "all queued" and "nothing queued".
        Check(pipeline.Contains("packable.Add(entry.Key);", StringComparison.Ordinal)
              && !pipeline.Contains("if (SeasonPack.IsComplete(entry.Value, have))", StringComparison.Ordinal),
            "the plan makes every complete season packable, queued or not");
        Check(pipeline.Contains("items = OrderForClosure(items, packable);", StringComparison.Ordinal),
            "the run works the plan's seasons first");
        Check(pipeline.Contains("the rest is filled from what is already uploaded", StringComparison.Ordinal),
            "a partly queued season says that the gap is filled, not that it stays single");
        Check(pipeline.Split("await packCoordinator.AddAsync(").Length - 1 >= 2,
            "the dry run hands its candidates to the same buffer as the real run");
        Check(pipeline.Contains("a dry run shows the PACK it would send", StringComparison.Ordinal),
            "a dry run names the pack instead of eight singles");


        var index = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/SeasonEpisodeIndex.cs"));
        Check(index.Contains("caching.graphql.imdb.com", StringComparison.Ordinal), "IMDb is asked first");
        Check(index.Contains("/tv/{tmdbId}/season/{season}", StringComparison.Ordinal), "TMDB is the fallback");

        // ── 7. F-M389: a pack completed from what is already uploaded ───────────
        // Operator order 02.11.2026, revised the same day: a season may be FILLED with episodes whose
        // subtitle is already up — and a season whose episodes are ALL up is a pack of its own.
        Console.WriteLine("7. Filling a pack");
        var airedEight = Enumerable.Range(1, 8).ToList();
        var sevenOfEight = Enumerable.Range(1, 7).ToList();
        Check(SeasonPack.Uncovered(airedEight, sevenOfEight).SequenceEqual(new[] { 8 }),
            "seven of eight leaves exactly the eighth to fill");
        Check(SeasonPack.Uncovered(airedEight, Array.Empty<int>()).Count == 8,
            "with nothing queued the whole season comes from the filler");
        Check(SeasonPack.Uncovered(airedEight, airedEight).Count == 0, "a full buffer needs nothing filled");
        Check(SeasonPack.Uncovered(airedEight, new[] { 1, 2, 3, 5, 6, 7, 9 }).SequenceEqual(new[] { 4, 8 }),
            "a gap in the middle is filled per episode, and an episode that never aired is no gap");

        var filledSeason = sevenOfEight.Concat(SeasonPack.Uncovered(airedEight, sevenOfEight)).ToList();
        Check(SeasonPack.IsComplete(airedEight, filledSeason), "seven plus the filled eighth is the season");
        Check(!SeasonPack.IsComplete(airedEight, sevenOfEight), "without the fill the season stays single");
        Check(SeasonPack.PackHash(filledSeason.Select(e => Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(Srt(e))))
              == SeasonPack.PackHash(Enumerable.Range(1, 8).Select(e => Jellyfin.Plugin.SubdlScribe.Registry.ContentHashRegistry.ComputeHash(Srt(e)))),
            "a filled pack is the same container as a fully collected one — same members, same hash");

        // The rules in the code: a member may only come from a subtitle that passed the gates AND is
        // known to the registry; nothing is uploaded when one episode cannot join.
        int fillFrom = pipeline.IndexOf("public async Task<SeasonPackCoordinator.Candidate?> FillAsync", StringComparison.Ordinal);
        int fillTo = pipeline.IndexOf("private BaseItem? FindSeasonEpisode", StringComparison.Ordinal);
        string fill = fillFrom >= 0 && fillTo > fillFrom ? pipeline[fillFrom..fillTo] : string.Empty;
        Check(fill.Length > 0, "the pipeline is the pack filler");
        Check(fill.Contains("ContentHashRegistry.NormalizeSrt(extracted)", StringComparison.Ordinal),
            "the candidate is canonicalised before it is gated and hashed");
        Check(fill.Contains("GateReject(srt, lang, runtimeMs", StringComparison.Ordinal),
            "the same gates as the single path run on the filler");
        Check(fill.Contains("Registry.IsContentKnown(contentHash)", StringComparison.Ordinal),
            "… and only content the registry knows is accepted — that is the 'already uploaded' proof");
        Check(fill.IndexOf("GateReject(srt", StringComparison.Ordinal) < fill.IndexOf("Registry.IsContentKnown(contentHash)", StringComparison.Ordinal),
            "the gates run BEFORE the content is trusted");
        Check(fill.Split("return null;").Length - 1 >= 5, "every refusal leaves the pack without that member");
        Check(fill.Contains("the text speaks {Detected}", StringComparison.Ordinal),
            "a lying language tag cannot drag a second language into the container");

        Check(coordinator.Contains("IPackFiller? filler = null", StringComparison.Ordinal),
            "the filler is optional — without it the old behaviour is untouched");
        Check(coordinator.Contains("SeasonPack.Uncovered(aired, buffer.Select(c => c.Episode))", StringComparison.Ordinal),
            "an incomplete buffer asks which episode it lacks");
        Check(coordinator.Contains("public async Task FillOnlyAsync(", StringComparison.Ordinal),
            "a season with nothing new in the queue is packed too");
        Check(coordinator.Contains("uploadSinglesOnRefusal: false", StringComparison.Ordinal),
            "a fill-only season re-sends nothing when the container is refused");
        Check(pipeline.Contains("fillOnlySeasons = PlanFillOnlySeasons(completeSeasons, items);", StringComparison.Ordinal),
            "the run plans those seasons before the loop");
        Check(pipeline.Contains("nothing new in the queue, the pack is built from what is already uploaded", StringComparison.Ordinal),
            "and says why it does so");
        Check(pipeline.Contains("await packCoordinator.FillOnlyAsync(fillImdb, fillSeason, fillLang, fillHi, runCt)", StringComparison.Ordinal),
            "the fill-only packs go up before the buffers are flushed");

        // ── 8. The coordinator itself, on fakes ─────────────────────────────────
        // No network, no Jellyfin, no database: the dry run of a pack stops before the upload, and the
        // registry is never touched on that path.
        Console.WriteLine("8. Coordinator on fakes");
        var dryConfig = new Jellyfin.Plugin.SubdlScribe.Configuration.PluginConfiguration { DryRun = true };
        var complete = new Dictionary<(string ImdbId, int Season), IReadOnlyList<int>> { [("tt1", 1)] = airedEight };
        var packable = new HashSet<(string ImdbId, int Season)> { ("tt1", 1) };

        var fillerOne = new FakeFiller(refuse: Array.Empty<int>(), answer: 8);
        var logOne = new List<string>();
        var coordinatorOne = NewCoordinator(logOne, complete, packable, fillerOne, dryConfig);
        foreach (int e in sevenOfEight)
        {
            coordinatorOne.AddAsync(Candidate(e), CancellationToken.None).GetAwaiter().GetResult();
        }

        coordinatorOne.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check(fillerOne.Calls.SequenceEqual(new[] { 8 }), "only the missing episode is asked for, in episode order");
        Check(logOne.Any(l => l.Contains("season pack filled", StringComparison.Ordinal)
                              && l.Contains("1 episode(s) from subtitles already uploaded, 7 from this run", StringComparison.Ordinal)),
            "one line says how many members came from the filler");
        Check(logOne.Any(l => l.Contains("DRY-RUN would upload season pack", StringComparison.Ordinal)
                              && l.Contains("8 episode(s)", StringComparison.Ordinal)
                              && l.Contains("Ef/Ee 1-8", StringComparison.Ordinal)),
            "seven collected plus one filled is a pack of eight");
        Check(!logOne.Any(l => l.Contains("season pack skipped", StringComparison.Ordinal)),
            "the skip line is not reached when the fill succeeds");

        var fillerAll = new FakeFiller(refuse: Array.Empty<int>(), answer: -1);
        var logAll = new List<string>();
        var coordinatorAll = NewCoordinator(logAll, complete, packable, fillerAll, dryConfig);
        coordinatorAll.FillOnlyAsync("tt1", 1, "EN", false, CancellationToken.None).GetAwaiter().GetResult();
        Check(fillerAll.Calls.Count == 8, "a season with nothing new asks the filler for every aired episode");
        Check(logAll.Any(l => l.Contains("season pack filled", StringComparison.Ordinal)
                              && l.Contains("8 episode(s) from subtitles already uploaded, 0 from this run", StringComparison.Ordinal)),
            "the fill-only pack is made of uploaded content only");
        Check(logAll.Any(l => l.Contains("DRY-RUN would upload season pack", StringComparison.Ordinal)
                              && l.Contains("8 episode(s)", StringComparison.Ordinal)),
            "and it is sent as one pack of eight");

        var fillerHole = new FakeFiller(refuse: new[] { 5 }, answer: -1);
        var logHole = new List<string>();
        var coordinatorHole = NewCoordinator(logHole, complete, packable, fillerHole, dryConfig);
        coordinatorHole.FillOnlyAsync("tt1", 1, "EN", false, CancellationToken.None).GetAwaiter().GetResult();
        Check(logHole.Any(l => l.Contains("episode 5 cannot join from what is already uploaded, nothing is sent", StringComparison.Ordinal)),
            "one episode that cannot join stops the pack and names itself");
        Check(!logHole.Any(l => l.Contains("DRY-RUN would upload season pack", StringComparison.Ordinal)),
            "no partial pack is ever sent");

        // ── F-M407: TMDb zuerst ─────────────────────────────────────────────────
        var seasonIndexSrc = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/SeasonEpisodeIndex.cs"));
        Check(seasonIndexSrc.IndexOf("FromTmdbAsync(tmdbId, tmdbApiKey, season, logger, ct)", StringComparison.Ordinal)
              < seasonIndexSrc.IndexOf("FromImdbAsync(imdbId, season, logger, ct)", StringComparison.Ordinal),
              "F-M407: TMDb is asked before IMDb");

        // ── F-M408: Vollstaendigkeit je Release-Gruppe ──────────────────────────
        var uploadSrc = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check(uploadSrc.Contains("SeasonPack.ReleaseGroup(") && uploadSrc.Contains("completeGroup"),
              "F-M408: the season is completed per release group");
        Check(uploadSrc.Contains("no release group covers the season"),
              "F-M408: a season split across groups says so and stays single");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"seasonpack: {_checks} checks, all green."
            : $"seasonpack: {_checks} checks, {Failures.Count} FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
