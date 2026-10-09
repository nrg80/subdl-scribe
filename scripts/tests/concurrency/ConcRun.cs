// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Concurrency harness: proves the shared context survives the load the plugin actually produces.
//
// WHY THIS EXISTS
//
//   Entity Framework's DbContext is single-threaded by contract. The plugin hands ONE context to
//   every registry, every scheduled task and every API endpoint, and the configuration page polls
//   /Plugins/SubdlSync/Stats every 10 seconds while a worker writes. Without the gate in
//   SubdlSet, that combination throws "A second operation was started on this context instance
//   before a previous operation completed" — and it throws it under load, on a user's server, not
//   in a quiet test.
//
//   So this harness runs the REAL mix: one writer and several readers, on the shared context, from
//   many threads at once. It is not a demonstration that a lock exists; it is a check that the
//   plugin's own call pattern does not fall over.
//
// Usage: concrun <workdir> [seconds]

using System.Diagnostics;
using Jellyfin.Plugin.SubdlScribe.Data;
using Jellyfin.Plugin.SubdlScribe.Registry;

var workdir = args[0];
int seconds = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 6;

Directory.CreateDirectory(workdir);
foreach (var f in Directory.GetFiles(workdir))
{
    File.Delete(f);
}

using var db = new SubdlDbContext(workdir, null);
var registry = new ContentHashRegistry(db, null, null);
var oshash = new OshashCache(db, null);

var stop = false;
var errors = new List<string>();
var gate = new object();

void Guard(Action a)
{
    try
    {
        a();
    }
    catch (Exception ex)
    {
        lock (gate)
        {
            var stack = ex.StackTrace ?? "";
            var frames = string.Join(" <- ", stack.Split('\n').Take(4).Select(l => l.Trim()));
            errors.Add(ex.GetType().Name + " :: " + frames);
        }
    }
}

long writerOps = 0, readerOps = 0;

// THREE writers, not one. The plugin has one worker at a time, but the reset endpoint, the
// statistics reset, the queue writer and the worker can all write to the shared context in the same
// tick — and a single-writer harness does not reach the race, which was measured: with one writer
// and the gate removed this harness still passed. Three writers reach it.
var writers = new List<Thread>();
for (int w = 0; w < 3; w++)
{
    int wid = w;
    var th = new Thread(() =>
    {
        int i = 0;
        while (!stop)
        {
            int n = i++;
            Guard(() =>
            {
                db.Media.Upsert(new MediaEntity { Id = "m" + ((n + wid * 13) % 40), Path = "/x/" + ((n + wid * 13) % 40) + ".mkv", LastSeen = DateTime.UtcNow });
                oshash.Store("/x/" + ((n + wid * 13) % 40) + ".mkv", "hash" + (n % 40), 1000 + (n % 40), 0);
                db.WorkerRuns.Upsert(new WorkerRunEntity { Id = "w" + wid, Name = "W" + wid, Started = DateTime.UtcNow, Outcome = "ok", Detail = "n" + n });
            });
            Interlocked.Increment(ref writerOps);
        }
    });
    writers.Add(th);
}

// Reader 1: what the configuration page's Stats poll does.
var statsReader = new Thread(() =>
{
    while (!stop)
    {
        Guard(() => { _ = db.StatusStats.FindAll().ToList(); });
        Interlocked.Increment(ref readerOps);
    }
});

// Reader 2: the diagnostics endpoint, which walks several areas.
var diagReader = new Thread(() =>
{
    while (!stop)
    {
        Guard(() =>
        {
            _ = db.Counters.FindAll().ToList();
            _ = db.Embeds.FindAll().ToList();
            _ = db.Media.FindAll().ToList();
            _ = db.Oshashes.Count();
        });
        Interlocked.Increment(ref readerOps);
    }
});

// Reader 3: the registry's read path, as the seeder and the gates use it.
var registryReader = new Thread(() =>
{
    int i = 0;
    while (!stop)
    {
        int n = i++;
        Guard(() =>
        {
            _ = registry.GetEmbed("m" + (n % 40), 0);
            _ = oshash.Lookup("/x/" + (n % 40) + ".mkv", 1000 + (n % 40), 0, TimeSpan.Zero, out _);
        });
        Interlocked.Increment(ref readerOps);
    }
});

var sw = Stopwatch.StartNew();
foreach (var w in writers)
{
    w.Start();
}
statsReader.Start();
diagReader.Start();
registryReader.Start();
Thread.Sleep(seconds * 1000);
stop = true;
foreach (var th in writers.Concat(new[] { statsReader, diagReader, registryReader }))
{
    th.Join(TimeSpan.FromSeconds(10));
}

sw.Stop();
Console.WriteLine("elapsed        : " + sw.ElapsedMilliseconds + " ms");
Console.WriteLine("writer ops     : " + writerOps);
Console.WriteLine("reader ops     : " + readerOps);
Console.WriteLine("errors         : " + errors.Count);
foreach (var e in errors.Distinct().Take(10))
{
    Console.WriteLine("   " + e);
}

// The store must still answer, and hold what the writer put in it.
var media = db.Media.Count();
Console.WriteLine("media rows     : " + media);
Console.WriteLine("meta reachable : " + (db.Meta.FindById("db") != null));

if (errors.Count > 0)
{
    Console.WriteLine("=== CONCURRENCY FAILED ===");
    return 1;
}

if (media == 0)
{
    Console.WriteLine("=== CONCURRENCY FAILED (writer wrote nothing) ===");
    return 1;
}

Console.WriteLine("=== CONCURRENCY OK ===");
return 0;
