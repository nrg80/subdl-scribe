// Diagnostic: the plugin's decode path against one file, with the failure surfaced.
using System;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.SubdlScribe.Qa;

internal static class DecDiag
{
    private static int Main(string[] args)
    {
        string media = args[0];
        Console.WriteLine("media: " + media);
        Console.WriteLine("exists: " + File.Exists(media));
        var t0 = DateTime.UtcNow;
        try
        {
            double[] levels = DriftGate.DecodeFrameLevelsAsync("/usr/bin/ffmpeg", media, CancellationToken.None, "0:a:0")
                .GetAwaiter().GetResult();
            Console.WriteLine($"levels: {levels.Length}  ({(DateTime.UtcNow - t0).TotalSeconds:0.0}s)");
            if (levels.Length > 0)
            {
                Console.WriteLine($"frames->min: {levels.Length * OffsetFit.FrameSec / 60:0.00}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
        }

        return 0;
    }
}
