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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M441 (operator 04.11.2026, "kein dauerhafter store"): the working files of ONE run.
/// <para>
/// The pack fill used to hold the text of every subtitle track of every file it touched in a
/// dictionary for the whole run — measured on prod 04.11.2026 that was 689 episode files, roughly
/// 21.000 texts and an estimated 900 MB resident. The texts now live on disk while the run works and
/// are gone when it ends: a second pack asking for the same episode reads the file instead of calling
/// ffmpeg again, and nothing survives the run.
/// </para>
/// <para>
/// Nothing here is a source of truth. A missing file is simply a re-extraction; the directory is
/// purged at the end of the run and any directory left behind by a killed run is swept at the start
/// of the next one.
/// </para>
/// </summary>
public sealed class RunWorkFiles
{
    /// <summary>Folder below the system temp folder, shared with the ffmpeg extractions.</summary>
    private const string RootName = "subdl-scribe";

    /// <summary>Prefix that marks a directory as belonging to a run.</summary>
    private const string RunPrefix = "run-";

    private readonly string _root;
    private readonly string _directory;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunWorkFiles"/> class.
    /// </summary>
    /// <param name="root">Explicit root, for tests. Left null the system temp folder is used.</param>
    public RunWorkFiles(string? root = null)
    {
        _root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Path.GetTempPath(), RootName)
            : root!;
        _directory = Path.Combine(
            _root,
            RunPrefix
            + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>Gets the directory of this run.</summary>
    public string Directory => _directory;

    /// <summary>Gets the root the run directories live in.</summary>
    public string Root => _root;

    /// <summary>Number of texts this run currently has on disk.</summary>
    /// <returns>File count.</returns>
    public int Count()
        => System.IO.Directory.Exists(_directory)
            ? System.IO.Directory.EnumerateFiles(_directory, "*.srt").Count()
            : 0;

    /// <summary>Writes the text of one track of one episode.</summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <param name="subPos">Subtitle stream position.</param>
    /// <param name="srt">The text.</param>
    public void Write(string? imdbId, int season, int episode, string? language, bool hearingImpaired, int subPos, string? srt)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(srt))
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            File.WriteAllText(
                Path.Combine(_directory, FileNameFor(imdbId!, season, episode, language!, hearingImpaired, subPos)),
                srt!,
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A working file that cannot be written costs one extraction, not the run.
        }
    }

    /// <summary>Reads back the text of one track of one episode.</summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="language">SubDL language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <param name="subPos">Subtitle stream position.</param>
    /// <param name="srt">The text, or null when this run has not written it.</param>
    /// <returns>True when the text was found.</returns>
    public bool TryRead(string? imdbId, int season, int episode, string? language, bool hearingImpaired, int subPos, out string? srt)
    {
        srt = null;
        if (string.IsNullOrWhiteSpace(imdbId) || string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        string path = Path.Combine(_directory, FileNameFor(imdbId!, season, episode, language!, hearingImpaired, subPos));
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            srt = File.ReadAllText(path, Encoding.UTF8);
            return !string.IsNullOrWhiteSpace(srt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes this run's directory — called when the run ends.</summary>
    /// <returns>Number of files removed.</returns>
    public int Purge()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return 0;
        }

        int removed = 0;
        foreach (string path in System.IO.Directory.EnumerateFiles(_directory).ToList())
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next run's start sweep takes what is left.
            }
        }

        try
        {
            System.IO.Directory.Delete(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same: a leftover directory is swept, not fatal.
        }

        return removed;
    }

    /// <summary>
    /// Sweeps run directories left behind by a killed run, and stale extraction folders of the ffmpeg
    /// one-pass. Called at the start of a run, so a crash costs disk only until the next start.
    /// </summary>
    /// <param name="root">Root folder, null for the system temp folder.</param>
    /// <param name="maxAge">How long a directory may sit before it goes.</param>
    /// <returns>Number of directories removed.</returns>
    public static int PurgeStale(string? root, TimeSpan maxAge)
    {
        string rootPath = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Path.GetTempPath(), RootName)
            : root!;
        if (!System.IO.Directory.Exists(rootPath))
        {
            return 0;
        }

        DateTime cutoff = DateTime.UtcNow - maxAge;
        int removed = 0;
        foreach (string dir in System.IO.Directory.EnumerateDirectories(rootPath).ToList())
        {
            try
            {
                if (System.IO.Directory.GetLastWriteTimeUtc(dir) >= cutoff)
                {
                    continue;
                }

                System.IO.Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Next start tries again.
            }
        }

        return removed;
    }

    private static string FileNameFor(string imdbId, int season, int episode, string language, bool hearingImpaired, int subPos)
    {
        var safe = new StringBuilder(language.Length);
        foreach (char c in language)
        {
            safe.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        return $"{imdbId}_s{season:00}e{episode:00}_{safe}{(hearingImpaired ? "_hi" : string.Empty)}_p{subPos}.srt";
    }
}
