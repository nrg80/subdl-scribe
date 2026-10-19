// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// One fit, one file: runs the shipped auto-sync on a real media file and writes the
// corrected subtitle next to the caller's output path. The harnesses (autosyncrun,
// fitrun) print the decision but never hand out the corrected text, so a drift cannot
// be measured against a second variant after the fit. This runner exists for that
// measurement alone.
using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Pipeline;
using Jellyfin.Plugin.SubdlScribe.Qa;
using Microsoft.Extensions.Logging.Abstractions;

namespace SpeechFitTest
{
    /// <summary>Fits one subtitle against one media file and writes the result.</summary>
    public static class Program
    {
        /// <summary>Entry point.</summary>
        /// <param name="args">media, input srt, output srt.</param>
        /// <returns>0 on a written correction, 2 when the fit refused, 1 on bad usage.</returns>
        public static async Task<int> Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("usage: speechfitrun <media> <srtIn> <srtOut>");
                return 1;
            }

            string media = args[0];
            string srtIn = args[1];
            string srtOut = args[2];

            string text = await File.ReadAllTextAsync(srtIn).ConfigureAwait(false);
            string ffmpeg = FfmpegTools.ResolvePath(
                new Jellyfin.Plugin.SubdlScribe.Configuration.PluginConfiguration(), NullLogger.Instance) ?? "ffmpeg";

            SubtitleSync.Result r = await SubtitleSync.SyncAsync(
                media, text, "0:a:0", ffmpeg, NullLogger.Instance, default).ConfigureAwait(false);

            Console.WriteLine("applied=" + r.Applied + " | " + r.Reason);
            if (r.Applied && !string.IsNullOrEmpty(r.Corrected))
            {
                await File.WriteAllTextAsync(srtOut, r.Corrected).ConfigureAwait(false);
                Console.WriteLine("written " + srtOut);
                return 0;
            }

            await File.WriteAllTextAsync(srtOut, text).ConfigureAwait(false);
            return 2;
        }
    }
}
