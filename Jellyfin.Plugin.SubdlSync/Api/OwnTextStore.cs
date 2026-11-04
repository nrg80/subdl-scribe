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
/// F-M440: what a stored text belongs to — series, episode, language and HI variant.
/// </summary>
/// <param name="ImdbId">Series IMDb id.</param>
/// <param name="Season">Season number.</param>
/// <param name="Episode">Episode number.</param>
/// <param name="Language">SubDL language code.</param>
/// <param name="HearingImpaired">HI/SDH variant.</param>
public readonly record struct OwnTextKey(
    string ImdbId, int Season, int Episode, string Language, bool HearingImpaired);

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
    /// <summary>
    /// Runaway guard only, not the working budget: measured on real files a canonical text is ~16 KB
    /// gzipped, so 6.266 uploaded texts are ~100 MB and 20.000 are ~320 MB. The default leaves that
    /// room; the value is a setting, and 0 means no ceiling at all.
    /// </summary>
    private const long DefaultMaxTotalBytes = 2048L * 1024 * 1024;

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

    /// <summary>Ceiling in bytes from the plugin configuration; 0 means unlimited.</summary>
    /// <returns>Ceiling in bytes.</returns>
    private long Ceiling()
    {
        int megabytes = Plugin.Instance?.Configuration?.OwnTextsMaxMegabytes ?? 0;
        return megabytes <= 0 ? DefaultMaxTotalBytes : megabytes * 1024L * 1024L;
    }

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
                string target = Path.Combine(dir, FileNameFor(imdbId!, season, episode, language!, hearingImpaired));
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

        string path = Path.Combine(dir, FileNameFor(imdbId!, season, episode, language!, hearingImpaired));
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

            // A fill that found the text here is the proof it is still needed — the sweep reads the
            // last-write time as "last used" and leaves a text alone while packs keep taking it.
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not fatal: the sweep then sees the older time and may drop a text that is in use,
                // which costs one extraction later.
            }

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

    /// <summary>
    /// A stored text is named after what it holds — <c>tt1234567_s01e02_de_hi.srt.gz</c> — so the sweep
    /// can read the key off the file without an index. A hash would have been smaller and unreadable.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <returns>The file name.</returns>
    /// <summary>Drops one stored text — called where its published entry is deleted or absorbed.</summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <returns>True when a text was removed.</returns>
    public bool Remove(string? imdbId, int season, int episode, string? language, bool hearingImpaired)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        string? dir = Directory;
        if (dir == null)
        {
            return false;
        }

        string path = Path.Combine(dir, FileNameFor(imdbId!, season, episode, language!, hearingImpaired));
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Täglicher Aufräumlauf: a stored text goes when its episode no longer has an uploaded entry for
    /// that language, or when nothing has read it for <paramref name="maxUnusedAge"/>. Nothing else
    /// removes it — the reset takes everything, the size cap is only a runaway guard.
    /// </summary>
    /// <param name="stillUploaded">Answers whether the episode still carries an uploaded entry for this language.</param>
    /// <param name="maxUnusedAge">How long a text may sit unread before it goes.</param>
    /// <returns>Number of texts removed.</returns>
    public int Sweep(Func<OwnTextKey, bool> stillUploaded, TimeSpan maxUnusedAge)
    {
        string? dir = Directory;
        if (dir == null || !System.IO.Directory.Exists(dir))
        {
            return 0;
        }

        DateTime cutoff = DateTime.UtcNow - maxUnusedAge;
        int removed = 0;

        // A half-written Put leaves a .tmp behind; a day of grace and it goes.
        foreach (string tmp in System.IO.Directory.EnumerateFiles(dir, "*.tmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(tmp) < DateTime.UtcNow.AddDays(-1))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Next sweep tries again.
            }
        }

        foreach (string path in System.IO.Directory.EnumerateFiles(dir, "*.srt.gz").ToList())
        {
            string name = Path.GetFileName(path);
            bool drop;
            if (!TryParseName(name, out var key))
            {
                // Not one of ours (stray temp file): remove it.
                drop = true;
            }
            else
            {
                DateTime lastUsed = File.GetLastWriteTimeUtc(path);
                drop = lastUsed < cutoff || !stillUploaded(key);
            }

            if (!drop)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Next sweep tries again.
            }
        }

        return removed;
    }

    private static string FileNameFor(string imdbId, int season, int episode, string language, bool hearingImpaired)
    {
        var safe = new StringBuilder(language.Length);
        foreach (char c in language)
        {
            safe.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        return $"{imdbId}_s{season:00}e{episode:00}_{safe}{(hearingImpaired ? "_hi" : string.Empty)}.srt.gz";
    }

    /// <summary>Reads a key back out of a store file name.</summary>
    /// <param name="fileName">File name only.</param>
    /// <param name="key">The parsed key.</param>
    /// <returns>True when the name follows the store's pattern.</returns>
    private static bool TryParseName(string fileName, out OwnTextKey key)
    {
        key = default;
        const string Suffix = ".srt.gz";
        if (!fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = fileName[..^Suffix.Length];
        bool hi = stem.EndsWith("_hi", StringComparison.Ordinal);
        if (hi)
        {
            stem = stem[..^3];
        }

        int langAt = stem.LastIndexOf('_');
        if (langAt <= 0 || langAt + 1 >= stem.Length)
        {
            return false;
        }

        string language = stem[(langAt + 1)..];
        string head = stem[..langAt];
        int epAt = head.LastIndexOf('e');
        int sAt = head.LastIndexOf('s');
        if (sAt <= 0 || epAt <= sAt + 1 || epAt + 1 >= head.Length)
        {
            return false;
        }

        string imdbId = head[..sAt];
        if (!int.TryParse(head[(sAt + 1)..epAt], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int season)
            || !int.TryParse(head[(epAt + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int episode))
        {
            return false;
        }

        key = new OwnTextKey(imdbId, season, episode, language, hi);
        return true;
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

        long ceiling = Ceiling();
        if (ceiling <= 0 || total <= ceiling)
        {
            return;
        }

        foreach (var info in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= ceiling)
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
