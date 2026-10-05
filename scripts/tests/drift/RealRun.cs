// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M295 end-to-end check against REAL material. The synthetic test proves the
// arithmetic; this proves the whole chain on the files the gate will actually
// meet: decode a real episode, derive islands from the real audio, read the real
// SRT, and print the verdict. It is also the check that the C# port reproduces
// the Python prototype — same file, same answer.
//
// Usage: dotnet run --project drift.csproj -- real <mediaDir> <basename>
//   basename e.g. "Person Of Interest S02e01 The Contingency.en" -> reads
//   <basename>.srt for the clean reference and <basename>.sdh.srt for the
//   hearing-impaired variant, so both can be compared on one episode.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Qa;

namespace DriftTest;

/// <summary>End-to-end driver.</summary>
public static class RealRun
{
    /// <summary>Entry point for the real-material check.</summary>
    /// <param name="args">real, dir, mediaBase, [lang].</param>
    /// <returns>0 on success.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args[1];
        string mediaBase = args[2];
        string lang = args.Length > 3 ? args[3] : "en";

        string vid = new[] { ".mkv", ".mp4", ".avi" }
            .Select(e => Path.Combine(dir, mediaBase + e))
            .FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"no media for {mediaBase} in {dir}");

        Console.WriteLine($"=== F-M295 end-to-end on real material ===");
        Console.WriteLine($"    {Path.GetFileName(vid)}");
        Console.WriteLine();

        string ffmpeg = FindFfmpeg();
        Console.WriteLine($"    ffmpeg: {ffmpeg}");
        Console.WriteLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));

        // The media carries no language suffix; the SRTs do. Variants compared on
        // one episode: the plain subtitle and the hearing-impaired one.
        foreach (string variant in new[] { $"", ".sdh" })
        {
            string srtPath = Path.Combine(dir, $"{mediaBase}.{lang}{variant}.srt");
            if (!File.Exists(srtPath))
            {
                Console.WriteLine($"  {variant,-6} -> file not present, skipped");
                continue;
            }

            string text = await File.ReadAllTextAsync(srtPath, cts.Token).ConfigureAwait(false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DriftVerdict v = await DriftGate.RunAsync(
                vid, text, ffmpeg, new NullLogger(), cts.Token).ConfigureAwait(false);
            sw.Stop();

            (double[] cs, double[] ce) = DriftGate.ParseCues(text);
            string label = variant.Length == 0 ? "plain" : "sdh";
            Console.WriteLine($"  {label,-6} {cs.Length,4} cues  [{sw.Elapsed.TotalSeconds:0.0}s]  {v.Describe()}");
            if (v.Ran && v.Drifts)
            {
                Console.WriteLine($"         span {v.SpanSec:0.00}s, "
                    + $"{v.BoundaryCount} boundary/boundaries "
                    + $"at " + string.Join(", ",
                        v.BoundaryTimesSec.Select(t => $"{t / 60:0.0}min"))
                    + $", max log-BF {v.MaxBayesFactor:0.0}");
            }
        }

        Console.WriteLine();
        return 0;
    }

    private static string FindFfmpeg()
    {
        foreach (string c in new[]
        {
            "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/data/ffmpeg/ffmpeg"
        })
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        return "ffmpeg";
    }

    /// <summary>Minimal logger: the gate logs at Debug only, and this test prints its own lines.</summary>
    private sealed class NullLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
