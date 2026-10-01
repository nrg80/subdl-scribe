// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;

namespace Jellyfin.Plugin.SubdlScribe.Data;

/// <summary>
/// The one place that knows how the plugin's data files are spelled.
/// <para>
/// The file is a path, not a label — every read and write resolves it here, so a rename is one edit
/// instead of a hunt for string literals. It was renamed from <c>subdl-sync.db</c> to
/// <c>subdl-scribe.db</c> (28.09.2026, the name kept the plugin's working title).
/// </para>
/// <para>
/// No migration code is kept for the old name: the rename shipped in 12.1.12.116 and every known
/// installation carried its files across on the first start after it, so nothing is left to
/// migrate. An installation that somehow still only has the old file starts with an empty registry
/// — the accepted trade for not carrying dead code forever.
/// </para>
/// </summary>
public static class DbFiles
{
    /// <summary>The data file's name.</summary>
    public const string Name = "subdl-scribe.db";

    /// <summary>Prefix of a timestamped backup, as the reset and restore routes expect it.</summary>
    public const string BackupPrefix = Name + ".bak-";

    /// <summary>
    /// Suffixes LiteDB gives the files sitting NEXT TO a data file. They are built from the data
    /// file's stem, not its full name: <c>subdl-scribe.db</c> → <c>subdl-scribe-log.db</c>.
    /// </summary>
    public static readonly string[] SideSuffixes = { "-log.db", "-temp.db", "-temp-log.db" };

    /// <summary>Full path of the data file inside a plugin data directory.</summary>
    /// <param name="dataDir">Plugin data directory.</param>
    /// <returns>Absolute path of the data file.</returns>
    public static string PathIn(string dataDir) => Path.Combine(dataDir, Name);

    /// <summary>
    /// Path of a side file (rollback journal, rebuild temporary) belonging to a data file. LiteDB
    /// appends the suffix to the file's STEM: <c>subdl-scribe.db</c> → <c>subdl-scribe-log.db</c>.
    /// Appending to the full name (<c>subdl-scribe.db-log.db</c>) matches nothing that exists.
    /// </summary>
    /// <param name="dbPath">Path of the data file.</param>
    /// <param name="suffix">Suffix including the leading dash, e.g. "-log.db".</param>
    /// <returns>Absolute path of the side file.</returns>
    public static string SidePath(string dbPath, string suffix)
    {
        return Path.Combine(
            Path.GetDirectoryName(dbPath) ?? ".",
            Path.GetFileNameWithoutExtension(dbPath) + suffix);
    }
}
