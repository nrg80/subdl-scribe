// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Converts existing subtitle files to the F-M307 fit.
//
// THE ORDER, AND WHY IT IS THAT ORDER
//
//   1. RESTORE: if a file carries the one-entry archive from an EARLIER algorithm
//      (`.srt.unsynced.zip`), the archive's payload is the file as downloaded.
//      It is put back first. Without this step the new fit would measure an
//      already-shifted timeline and then shift it a second time — the old
//      algorithm's error would be inherited instead of removed, and the old
//      algorithm is exactly what is being retired here.
//   2. FIT: the restored text goes through the SAME SubtitleSync.SyncAsync the
//      plugin calls, on the same audio track the plugin would pick.
//   3. WRITE, only when the fit proves a gain: the corrected `.srt` (canonical form)
//      plus the archive holding the RESTORED bytes — i.e. still the file as
//      downloaded, never the shifted one.
//
// Nothing else is touched: no media file, no registry entry, no log. The dry run is
// the default; `--write` is the operator's switch.
//
// Usage: convrun <manifest.json> [--write]
//   manifest.json: [{"srt": "...", "media": "...", "lang": "en.sdh"}, ...]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Qa;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ConvertRun
{
    private sealed class Job
    {
        public string Srt { get; set; } = string.Empty;

        public string Media { get; set; } = string.Empty;

        public string Lang { get; set; } = string.Empty;

        /// <summary>Gets or sets the audio stream map, e.g. <c>0:a:0</c>.</summary>
        public string AudioMap { get; set; } = "0:a:0";
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("usage: convrun <manifest.json> [--write]");
            return 2;
        }

        bool write = Array.IndexOf(args, "--write") >= 0;
        string outDir = "/opt/data/drift-lab/conv-out";
        Directory.CreateDirectory(outDir);

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var jobs = JsonSerializer.Deserialize<List<Job>>(
            await File.ReadAllTextAsync(args[0]), jsonOpts) ?? new List<Job>();

        ILogger logger = NullLogger.Instance;
        const string Ffmpeg = "/usr/bin/ffmpeg";

        int applied = 0, refused = 0, restored = 0, movedTotal = 0;
        var csv = new StringBuilder("srt,lang,media,applied,shift,cues,moved,reason\n");

        foreach (Job j in jobs)
        {
            byte[] original = await File.ReadAllBytesAsync(j.Srt);
            byte[] working = original;
            bool wasRestored = false;

            // STEP 1 — restore what an EARLIER algorithm changed, if it left its archive.
            // The legacy name `.srt.unsynchronized.zip` is still read: archives written
            // before the rename are the ONLY copy of the download state for those files,
            // and ignoring them would make the old algorithm's shift look like the
            // download state and get inherited instead of removed. New archives are
            // written under the current name (F-M306).
            string archive = Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.UnsyncZipPathFor(j.Srt);
            string legacyArchive = j.Srt + ".unsynchronized.zip";
            if (!File.Exists(archive) && File.Exists(legacyArchive)) archive = legacyArchive;
            if (File.Exists(archive))
            {
                try
                {
                    using var zf = ZipFile.OpenRead(archive);
                    if (zf.Entries.Count > 0)
                    {
                        using Stream es = zf.Entries[0].Open();
                        using var ms = new MemoryStream();
                        await es.CopyToAsync(ms);
                        working = ms.ToArray();
                        wasRestored = true;
                        restored++;
                    }
                }
                catch (Exception ex)
                {
                    await Console.Out.WriteLineAsync(
                        $"  RESTORE FAILED {Path.GetFileName(j.Srt)}: {ex.Message} — job skipped");
                    refused++;
                    continue;
                }
            }

            string text = Decode(working);

            // STEP 2 — the fit, exactly as the plugin runs it.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.Result r =
                await Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.SyncAsync(
                    j.Media, text, j.AudioMap, Ffmpeg, logger, CancellationToken.None).ConfigureAwait(false);
            sw.Stop();

            int cues = CountCues(text);
            int moved = r.Applied ? CountMoved(text, r.Corrected) : 0;

            await Console.Out.WriteLineAsync(string.Format(
                CultureInfo.InvariantCulture,
                "{0,-7} {1,-50} restored={2,-5} {3,-5} shift {4,7}  cues {5,5} moved {6,5}  {7,5:0.0}s  {8}",
                j.Lang, Path.GetFileName(j.Srt), wasRestored, r.Applied,
                r.ShiftSec.ToString("+0.00;-0.00", CultureInfo.InvariantCulture),
                cues, moved, sw.Elapsed.TotalSeconds, r.Reason));
            csv.Append(CultureInfo.InvariantCulture,
                $"{Path.GetFileName(j.Srt)},{j.Lang},{Path.GetFileName(j.Media)},{r.Applied},"
                + $"{r.ShiftSec:0.00},{cues},{moved},\"{r.Reason}\"\n");

            if (r.Applied)
            {
                applied++;
                movedTotal += moved;
                if (write)
                {
                    // STEP 3a — the corrected file, canonical (UTF-8, no BOM, LF).
                    WriteCanonical(j.Srt, r.Corrected);

                    // STEP 3b — the archive holds the RESTORED bytes, i.e. the file as
                    // downloaded. Where a restore happened this is a NEW archive over the
                    // original payload; the old archive is replaced by the identical bytes.
                    string entry = Path.GetFileName(Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.UnsyncPathFor(j.Srt));
                    await File.WriteAllBytesAsync(
                        Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.UnsyncZipPathFor(j.Srt),
                        Jellyfin.Plugin.SubdlScribe.Qa.SubtitleSync.BuildUnsyncArchive(entry, working));
                }
            }
            else
            {
                refused++;

                // The fit proved no gain. If an earlier algorithm had shifted this file,
                // its shift must NOT survive — that is the whole point of retiring it.
                // The download state is restored and the stale correction is gone.
                if (wasRestored && write && working.Length > 0)
                {
                    WriteCanonical(j.Srt, Decode(working));
                    await Console.Out.WriteLineAsync(
                        $"        no gain — reverted to the download state (old algorithm's shift removed)");
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "convert-report.csv"), csv.ToString());
        await Console.Out.WriteLineAsync(string.Format(
            CultureInfo.InvariantCulture,
            "\nSUMMARY applied={0} refused={1} of {2} | restored from an earlier algorithm={3} | "
            + "cues moved={4} | mode={5}",
            applied, refused, jobs.Count, restored, movedTotal, write ? "WRITE" : "DRY RUN"));
        return 0;
    }

    /// <summary>Decodes subtitle bytes the way the plugin's DecodeSrt does: BOM-aware.</summary>
    private static string Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    private static void WriteCanonical(string path, string text)
    {
        string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(normalized));
    }

    private static int CountCues(string srt)
    {
        int n = 0;
        foreach (string line in srt.Split('\n'))
        {
            if (line.Contains("-->", StringComparison.Ordinal))
            {
                n++;
            }
        }

        return n;
    }

    private static int CountMoved(string before, string after)
    {
        string[] a = Starts(before), b = Starts(after);
        int n = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                n++;
            }
        }

        return n;
    }

    private static string[] Starts(string srt)
    {
        var list = new List<string>();
        foreach (string line in srt.Split('\n'))
        {
            int i = line.IndexOf("-->", StringComparison.Ordinal);
            if (i > 0)
            {
                list.Add(line.Substring(0, i).Trim());
            }
        }

        return list.ToArray();
    }
}
