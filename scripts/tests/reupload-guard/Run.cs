// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version. SubDL Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// T153 (F-M345): the prevented-re-upload counter.
//
// Asserted on the SOURCE for everything a green build cannot show, because every one of these
// failures is silent: a counter that stops being per-sub still renders, one that counts the whole
// library still counts, and a column that never reaches an existing store throws only on the
// installation that already owns one — i.e. on every installation but a fresh test box.
//
// Run: dotnet run -c Release --project scripts/tests/reupload-guard/reupload-guard.csproj

using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

// Section 6 opens a real database, so the plugin's own dependency set (EF Core + its SQLite provider
// + the bundled native e_sqlite3) is loaded from the plugin's build output instead of being copied
// into this test's output — a test that mirrors a dependency list drifts away from the plugin it
// tests, and the drift shows up as a green test on the machine where it was written.
string pluginBin = args.Length > 1
    ? args[1]
    : "/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/bin/Release/net10.0";
string jellyfinBin = args.Length > 2 ? args[2] : "/opt/data/jf-test/jellyfin";
string[] probeDirs = { pluginBin, jellyfinBin };
AppDomain.CurrentDomain.AssemblyResolve += (_, missing) =>
{
    var wanted = new AssemblyName(missing.Name).Name + ".dll";
    foreach (var dir in probeDirs)
    {
        var candidate = Path.Combine(dir, wanted);
        if (File.Exists(candidate))
        {
            return Assembly.LoadFrom(candidate);
        }
    }

    return null;
};

int checks = 0;
int failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    checks++;
    if (ok)
    {
        Console.WriteLine($"  OK   {name}");
        return;
    }

    failures++;
    Console.WriteLine($"  FAIL {name}{(detail is null ? string.Empty : "  — " + detail)}");
}

static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

string repo = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";
string root = Path.Combine(repo, "Jellyfin.Plugin.SubdlSync");
string Read(params string[] parts) => Flat(File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray())));

string seeder = Read("ScheduledTasks", "SubdlSeeder.cs");
string dispatcher = Read("ScheduledTasks", "SubdlEventDispatcher.cs");
string delta = Read("Pipeline", "StatusCounterDelta.cs");
string plugin = Read("Plugin.cs");
string api = Read("Api", "SubdlStatusController.cs");
string entity = Read("Data", "Entities.cs");
string storeRaw = File.ReadAllText(Path.Combine(root, "Data", "SubdlDbContext.cs"));
// The store text is compared with its C# escapes folded away, so a check can name the SQL as SQL
// ("status_stats") instead of as the source spells it (\"status_stats\").
string store = Flat(storeRaw).Replace("\\\"", "\"");
string page = Read("Configuration", "configPage.html");

Console.WriteLine("T153 — the prevented-re-upload counter (F-M345)\n");

// ---- 1. The delta carries the count (runtime, not source) --------------------------------------
var counted = StatusCounterDelta.From(null, null, 0, 0, 3);
Check("the delta carries the count", counted.ReuploadsPrevented == 3, $"got {counted.ReuploadsPrevented}");
Check("a delta that only prevented work is not treated as empty", !counted.IsEmpty);
Check("a delta without the count stays empty", StatusCounterDelta.From(null, null).IsEmpty);

// ---- 2. The grain: one count per SUB — per track and per sidecar --------------------------------
int trackSites = Regex.Matches(seeder, @"CountTerminalTracks\(registry, mediaHash").Count;
Check("the seeder counts the settled TRACKS of the file", trackSites > 0);
Check("both branches that answer \"nothing to do\" count them", trackSites == 2, $"found {trackSites} site(s)");

int helperAt = seeder.IndexOf("private static int CountTerminalTracks", StringComparison.Ordinal);
string helper = helperAt >= 0 ? seeder[helperAt..] : string.Empty;
Check("the per-track count reads the SAME rows the queue decision reads",
    helper.Contains("IsEmbedTerminal(mediaHash, p)"),
    "the counter must not answer per position from a different source than ArePositionsTerminal");

Check("the settled loose files are counted too", seeder.Contains("prevented = settledSidecars"));
int settled = Regex.Matches(seeder, @"settled\+\+").Count;
Check("every settled-sidecar path increments the count", settled == 3, $"found {settled} increment(s)");

// ---- 3. The guard: only what a run NEWLY found counts -------------------------------------------
Check("the count is added exactly once", Regex.Matches(seeder, @"snapshot\.ReuploadsPrevented \+=").Count == 1);
// Asserted as the GUARDED STATEMENT itself, not as a window above it: the predicate call one line up
// also names `onlyItemIds != null`, so a window check stays green with the guard deleted — planted
// and confirmed green, which is why this reads the flattened statement.
Check("and only under the item-scoped (arrival) guard",
    seeder.Contains("if (onlyItemIds != null) { snapshot.ReuploadsPrevented += prevented; }"),
    "a full scan would report the settled subtitles of the whole library");
Check("the predicate is asked with that same flag",
    seeder.Contains("IsUploadTodo(item, mediaPath, config, onlyItemIds != null, out int prevented)"));
Check("the scan still queues exactly what the predicate answers",
    seeder.Contains("bool uploadTodo = IsUploadTodo(") && seeder.Contains("if (uploadTodo) { snapshot.Upload.Add(Clone(qi)); }"));

// ---- 4. The chain: snapshot, dispatcher, delta, writer, reset, API, page ------------------------
Check("the snapshot carries the counter", seeder.Contains("public int ReuploadsPrevented { get; set; }"));
Check("the statistics row has the field", entity.Contains("public long ReuploadsPrevented { get; set; }"));
Check("the dispatcher accumulates across scans", dispatcher.Contains("_pendingReuploadsPrevented += snapshot.ReuploadsPrevented;"));
Check("and consumes it exactly once", dispatcher.Contains("long reuploadsPrevented = _pendingReuploadsPrevented;")
    && dispatcher.Contains("_pendingReuploadsPrevented = 0;"));
Check("the delta is built with it", dispatcher.Contains("StatusCounterDelta.From(upSummary, downSummary, langAllocated, looseRenamed, reuploadsPrevented)"));
Check("the writer passes it on", dispatcher.Contains("delta.ReuploadsPrevented);"));
Check("the struct takes and keeps it", delta.Contains("long reuploadsPrevented = 0")
    && delta.Contains("ReuploadsPrevented = reuploadsPrevented;")
    && delta.Contains("&& ReuploadsPrevented == 0;"));
Check("the writer accepts it", plugin.Contains("long reuploadsPrevented = 0)")
    && plugin.Contains("&& reuploadsPrevented == 0)")
    && plugin.Contains("row.ReuploadsPrevented += reuploadsPrevented;"));
Check("the reset zeroes it", plugin.Contains("row.ReuploadsPrevented = 0;"));
int published = Regex.Matches(api, @"ReuploadsPrevented = row\.ReuploadsPrevented,").Count;
Check("the API publishes it in BOTH of its responses", published == 2, $"found {published}");
Check("the table row reads the published field", page.Contains("s.ReuploadsPrevented || 0"));
Check("and exists exactly once", Regex.Matches(page, @"s\.ReuploadsPrevented \|\| 0").Count == 1);

// ---- 5. The upgrade path: an EXISTING store receives the column ---------------------------------
int callAt = store.IndexOf("EnsureAddedColumns();", StringComparison.Ordinal);
int createdAt = store.IndexOf("EnsureCreated();", StringComparison.Ordinal);
Check("the column check runs", callAt > 0);
Check("and runs after EnsureCreated, which never alters an existing table",
    callAt > createdAt && createdAt > 0,
    "EnsureCreated creates a schema only for a new file");
Check("it probes the real table", store.Contains("PRAGMA table_info(\"status_stats\")"));
Check("it adds the column only when it is missing",
    store.Contains("!present.Contains(\"ReuploadsPrevented\")")
    && store.Contains("ALTER TABLE \"status_stats\" ADD COLUMN \"ReuploadsPrevented\""));
Check("and a failure there cannot stop the plugin from starting",
    store.Contains("[SubDL] column check failed (continuing)"));

// ---- 6. The upgrade path, against a REAL store written by the previous build --------------------
// The source checks above prove the ALTER exists; only this proves it WORKS. The failure it guards
// against is the worst kind: an existing installation upgrades, EF asks for a column the file does
// not have, and the whole statistics path throws — on every installation but a fresh one.
string? fixture = Directory.EnumerateFiles("/tmp", "subdl-scribe.sqlite", SearchOption.AllDirectories)
    .OrderByDescending(File.GetLastWriteTimeUtc)
    .FirstOrDefault();
Check("a fixture store written by an earlier build is available", fixture is not null,
    "no /tmp/*/subdl-scribe.sqlite to copy — the upgrade path would go unproven");

if (fixture is not null)
{
    string sandbox = Path.Combine(Path.GetTempPath(), "reupload-guard-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(sandbox);
    File.Copy(fixture, Path.Combine(sandbox, "subdl-scribe.sqlite"), overwrite: true);

    bool opened = true;
    try
    {
        // Opening runs EnsureCreated + the column check; the write then needs the column to exist.
        var ctx = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(sandbox, logger: null);
        ctx.StatusStats.Upsert(new Jellyfin.Plugin.SubdlScribe.Data.StatusStatsEntity
        {
            Id = "status",
            ReuploadsPrevented = 7,
        });
    }
    catch (Exception ex)
    {
        opened = false;
        Check("an EXISTING store accepts the new column", false, ex.GetType().Name + ": " + ex.Message);
    }

    if (opened)
    {
        var again = new Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext(sandbox, logger: null);
        long stored = again.StatusStats.FindById("status")?.ReuploadsPrevented ?? -1;
        Check("an EXISTING store accepts the new column", true);
        Check("and reads the value back", stored == 7, $"read {stored}");
    }

    try
    {
        Directory.Delete(sandbox, recursive: true);
    }
    catch
    {
        // a leftover sandbox is not a test failure
    }
}

Console.WriteLine($"\n{checks} Prüfungen, {failures} Fehler");
Console.WriteLine(failures == 0 ? "ALL GREEN" : ">>> FEHLER");
return failures == 0 ? 0 : 1;
