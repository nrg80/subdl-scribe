// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System.IO;

namespace Jellyfin.Plugin.SubdlScribe.Data;

/// <summary>
/// The one place that knows how the plugin's data files are spelled.
/// <para>
/// The file is a path, not a label — every read and write resolves it here, so a rename is one edit
/// instead of a hunt for string literals.
/// </para>
/// <para>
/// Renamed twice, and both times for a reason worth keeping. It was <c>subdl-sync.db</c> until
/// 28.09.2026 (the name kept the plugin's working title), and <c>subdl-scribe.db</c> until
/// 08.10.2026, when the store became SQLite and the extension stopped being decorative: the file is
/// now a real SQLite database, so it says so — <c>subdl-scribe.sqlite</c>. The old name is not
/// reused as a second live file: it is the IMPORT source, read once and renamed aside (F-M327).
/// </para>
/// </summary>
public static class DbFiles
{
    /// <summary>The data file's name.</summary>
    public const string Name = "subdl-scribe.sqlite";

    /// <summary>Prefix of a timestamped backup, as the reset and restore routes expect it.</summary>
    public const string BackupPrefix = Name + ".bak-";

    /// <summary>
    /// The name the data file carried before it became SQLite. Kept ONLY as the import source
    /// (F-M327) — nothing reads or writes it as a live store any more.
    /// </summary>
    public const string LegacyName = "subdl-scribe.db";

    /// <summary>
    /// Suffixes SQLite gives the files sitting NEXT TO a database. Built from the FULL file name,
    /// not its stem: <c>subdl-scribe.sqlite</c> → <c>subdl-scribe.sqlite-wal</c>. This is the
    /// opposite of the convention the previous engine used, which is why <see cref="SidePath"/>
    /// still exists as a separate helper rather than a shared string.
    /// </summary>
    public static readonly string[] SideSuffixes = { "-wal", "-shm", "-journal" };

    /// <summary>Full path of the data file inside a plugin data directory.</summary>
    /// <param name="dataDir">Plugin data directory.</param>
    /// <returns>Absolute path of the data file.</returns>
    public static string PathIn(string dataDir) => Path.Combine(dataDir, Name);

    /// <summary>
    /// Full path of the PREVIOUS data file, the one-time import source. Never the live store.
    /// </summary>
    /// <param name="dataDir">Plugin data directory.</param>
    /// <returns>Absolute path of the old document store.</returns>
    public static string LegacyPathIn(string dataDir) => Path.Combine(dataDir, LegacyName);

    /// <summary>
    /// Path of a side file belonging to a data file. SQLite appends the suffix to the FULL name:
    /// <c>subdl-scribe.sqlite</c> → <c>subdl-scribe.sqlite-wal</c>. Appending to the stem
    /// (<c>subdl-scribe-wal</c>) matches nothing SQLite ever writes.
    /// <para>
    /// The previous engine did the opposite — it built side names from the stem — so this helper
    /// carries a comment on each side rather than an assumption on either.
    /// </para>
    /// </summary>
    /// <param name="dbPath">Path of the data file.</param>
    /// <param name="suffix">Suffix including the leading dash, e.g. "-wal".</param>
    /// <returns>Absolute path of the side file.</returns>
    public static string SidePath(string dbPath, string suffix)
    {
        return Path.Combine(
            Path.GetDirectoryName(dbPath) ?? ".",
            Path.GetFileName(dbPath) + suffix);
    }
}
