// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M440 (operator 04.11.2026, "wir waren mit dem pack fill eigentlich anders verblieben"): the text
/// this account has ALREADY uploaded, kept on disk so a pack fill does not read it out of the media
/// file again.
/// <para>
/// F-M389 says a pack is completed "from the subtitle that is already uploaded". The implementation
/// extracted every subtitle track of the container with ffmpeg instead and held all of them for the
/// whole run: measured on prod 04.11.2026, 689 episode files across 55 seasons — around 21.000 texts
/// and an estimated 900 MB resident, plus one ffmpeg pass per file. The text that goes up is known at
/// upload time, so it is written here once, gzipped, a few KB per episode; the fill reads it back.
/// </para>
/// <para>
/// The store is a cache, never a source of truth: a miss falls back to the file. It is bounded by
/// <see cref="MaxTotalBytes"/> (oldest first) so a large library cannot fill the volume, and
/// <see cref="PurgeDirectory"/> removes it together with the rest of the plugin state on a reset.
/// </para>
/// </summary>
public sealed class OwnTextStore
{
    /// <summary>Ceiling for the whole store; beyond it the oldest entries go.</summary>
    private const long MaxTotalBytes = 256L * 1024 * 1024;

    /// <summary>Folder below the plugin data folder.</summary>
    private const string FolderName = "own-texts";

    private readonly object _sync = new();
    private readonly string? _directory;

    /// <summary>
    /// Initializes a new instance of the <see cref="OwnTextStore"/> class.
    /// </summary>
    /// <param name="directory">
    /// Explicit directory, for tests. Left null the store resolves
    /// <c>&lt;plugin data folder&gt;/own-texts</c> on every call.
    /// </param>
    public OwnTextStore(string? directory = null) => _directory = directory;

    private string? Directory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_directory))
            {
                return _directory;
            }

            string? data = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrWhiteSpace(data) ? null : Path.Combine(data, FolderName);
        }
    }

    /// <summary>Files the store holds right now — for diagnostics and tests.</summary>
    /// <returns>Number of stored texts.</returns>
    public int Count()
    {
        string? dir = Directory;
        return dir != null && System.IO.Directory.Exists(dir)
            ? System.IO.Directory.EnumerateFiles(dir, "*.srt.gz").Count()
            : 0;
    }

    /// <summary>Writes the text of an uploaded subtitle.</summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <param name="srt">The canonical text that went up.</param>
    public void Put(string? imdbId, int season, int episode, string? language, bool hearingImpaired, string? srt)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(srt))
        {
            return;
        }

        string? dir = Directory;
        if (dir == null)
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                System.IO.Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, FileNameFor(Key(imdbId!, season, episode, language!, hearingImpaired)));
                string tmp = target + ".tmp";
                using (var file = File.Create(tmp))
                using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(srt!);
                    gzip.Write(bytes, 0, bytes.Length);
                }

                File.Move(tmp, target, overwrite: true);
                EnforceCap(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot write is a miss, not a failed run.
        }
    }

    /// <summary>Reads back the text of an uploaded subtitle.</summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <param name="srt">The stored text, or null when it is not here.</param>
    /// <returns>True when the text was found.</returns>
    public bool TryGet(string? imdbId, int season, int episode, string? language, bool hearingImpaired, out string? srt)
    {
        srt = null;
        if (string.IsNullOrWhiteSpace(imdbId) || string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        string? dir = Directory;
        if (dir == null)
        {
            return false;
        }

        string path = Path.Combine(dir, FileNameFor(Key(imdbId!, season, episode, language!, hearingImpaired)));
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            srt = reader.ReadToEnd();
            return !string.IsNullOrWhiteSpace(srt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the whole store. Called by the reset alongside the archive cache (F-M427), because the
    /// registry rows that make a stored text reachable are gone with it.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    /// <returns>Number of texts removed.</returns>
    public static int PurgeDirectory(string dataFolder)
    {
        string dir = Path.Combine(dataFolder, FolderName);
        if (!System.IO.Directory.Exists(dir))
        {
            return 0;
        }

        int removed = 0;
        foreach (string path in System.IO.Directory.EnumerateFiles(dir).ToList())
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort — a locked file stays for the next purge.
            }
        }

        return removed;
    }

    private static string Key(string imdbId, int season, int episode, string language, bool hearingImpaired)
        => $"{imdbId}|{season}|{episode}|{language}|{(hearingImpaired ? "hi" : "plain")}";

    private static string FileNameFor(string key)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(digest, 0, 16).ToLowerInvariant() + ".srt.gz";
    }

    /// <summary>Oldest-first eviction once the store is over its ceiling.</summary>
    /// <param name="dir">Store directory.</param>
    private void EnforceCap(string dir)
    {
        var files = new List<FileInfo>();
        long total = 0;
        foreach (string path in System.IO.Directory.EnumerateFiles(dir, "*.srt.gz"))
        {
            var info = new FileInfo(path);
            files.Add(info);
            total += info.Length;
        }

        if (total <= MaxTotalBytes)
        {
            return;
        }

        foreach (var info in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= MaxTotalBytes)
            {
                break;
            }

            try
            {
                total -= info.Length;
                info.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep going; the next Put tries again.
            }
        }
    }
}
