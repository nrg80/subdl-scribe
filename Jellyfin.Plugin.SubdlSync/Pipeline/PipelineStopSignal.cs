// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Threading;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// (20.09.2026): file-based stop signal. A marker file tells the running
/// pipeline to stop gracefully. The pipeline checks before each item and, when a
/// marker is found, deletes it and returns true so the caller can finish the run
/// normally (post-run cleanup still executes).
/// </summary>
public static class PipelineStopSignal
{
    private static readonly object Lock = new();

    /// <summary>
    /// Clears any stale stop markers before a run starts.
    /// </summary>
    public static void ClearStaleMarkers(string dataDir)
    {
        if (string.IsNullOrEmpty(dataDir))
        {
            return;
        }

        lock (Lock)
        {
            foreach (var name in new[] { ".stop-upload", ".stop-download" })
            {
                var path = Path.Combine(dataDir, name);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch
                    {
                        // best-effort delete
                    }
                }
            }
        }
    }

    /// <summary>
    /// Creates a stop marker for the given direction.
    /// </summary>
    public static void WriteMarker(string dataDir, bool upload)
    {
        if (string.IsNullOrEmpty(dataDir))
        {
            return;
        }

        var fileName = upload ? ".stop-upload" : ".stop-download";
        var path = Path.Combine(dataDir, fileName);
        lock (Lock)
        {
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
        }
    }

    /// <summary>
    /// Returns true if a stop marker exists for this direction. Deletes the marker
    /// so the next run is not affected. Does NOT throw — the caller should break out
    /// of its loop and let normal post-run cleanup run.
    /// </summary>
    public static bool IsStopped(string dataDir, bool upload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fileName = upload ? ".stop-upload" : ".stop-download";
        var path = Path.Combine(dataDir, fileName);
        lock (Lock)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // best-effort delete
                }

                return true;
            }
        }

        return false;
    }
}
