using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M370: archives kept on disk between runs, keyed by <see cref="ArchiveKey"/>.
/// <para>
/// The run cache (F-M369) only helps inside one run — but a season pack serves one episode per
/// run, and with the free daily limit at 50 downloads a season needs several days. Measured on
/// 01.11.2026: 51 archive fetches for 45 files, 26 of them on three packs; the run stopped at
/// 98 of 1300 queued items because the quota was gone, so the next run asks for the same packs
/// again. Every avoided fetch on disk is one more file per day.
/// </para>
/// <para>
/// Two switches, both in the Download tab: <c>days</c> (0 = off) and <c>megabytes</c>
/// (0 = off). Either at 0 disables the whole thing. The ceiling is enforced oldest-first, and
/// only <c>.zip</c> bodies under <see cref="RunArchiveCache.DefaultMaxEntryBytes"/> are stored,
/// so a runaway upstream cannot fill the volume.
/// </para>
/// </summary>
public sealed class ArchiveDiskCache
{
    private readonly object _sync = new();
    private string? _directory;
    private int _ttlDays;
    private long _maxBytes;

    /// <summary>Gets the number of lookups answered from disk since <see cref="Reset"/>.</summary>
    public int Hits { get; private set; }

    /// <summary>Gets the number of lookups that found nothing usable.</summary>
    public int Misses { get; private set; }

    /// <summary>Gets the number of archives written to disk.</summary>
    public int Stores { get; private set; }

    /// <summary>Gets the number of files deleted by expiry or by the size ceiling.</summary>
    public int Removed { get; private set; }

    /// <summary>Gets the last error the cache swallowed, for the diagnostics endpoint.</summary>
    public string? LastError { get; private set; }

    /// <summary>Gets a value indicating whether the cache currently reads and writes.</summary>
    public bool Enabled => _directory != null && _ttlDays > 0 && _maxBytes > 0;

    /// <summary>Gets the directory the cache lives in (null when never configured).</summary>
    public string? DirectoryPath => _directory;

    /// <summary>
    /// Points the cache at a directory and applies the two switches. Safe to call on every run.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder; the cache uses <c>archive-cache</c> inside it.</param>
    /// <param name="days">Retention in days; 0 disables the cache.</param>
    /// <param name="megabytes">Size ceiling in MB; 0 disables the cache.</param>
    public void Configure(string? dataFolder, int days, int megabytes)
    {
        lock (_sync)
        {
            _ttlDays = days < 0 ? 0 : days;
            _maxBytes = megabytes <= 0 ? 0 : (long)megabytes * 1024L * 1024L;

            if (string.IsNullOrWhiteSpace(dataFolder) || _ttlDays == 0 || _maxBytes == 0)
            {
                _directory = null;
                return;
            }

            string dir = Path.Combine(dataFolder, "archive-cache");
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                _directory = dir;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _directory = null;
            }
        }
    }

    /// <summary>Clears the counters (not the files) at the start of a run.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            Hits = 0;
            Misses = 0;
            Stores = 0;
            Removed = 0;
        }
    }

    /// <summary>
    /// Looks an archive up by key.
    /// </summary>
    /// <param name="key">Archive key from <see cref="ArchiveKey.FromUrl"/>.</param>
    /// <param name="bytes">The archive body when found.</param>
    /// <returns>Whether a live, non-expired archive was found.</returns>
    public bool TryGet(string? key, out byte[]? bytes)
    {
        bytes = null;
        if (!Enabled || string.IsNullOrEmpty(key))
        {
            return false;
        }

        lock (_sync)
        {
            try
            {
                string path = PathFor(key);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    Misses++;
                    return false;
                }

                if (IsExpired(info))
                {
                    Delete(path);
                    Misses++;
                    return false;
                }

                bytes = File.ReadAllBytes(path);

                // Touch: retention counts from the last USE, so a pack that keeps serving
                // episodes does not expire in the middle of a season.
                try
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (Exception)
                {
                    // A read-only volume must not turn a hit into a miss.
                }

                Hits++;
                return bytes.Length > 0;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Misses++;
                return false;
            }
        }
    }

    /// <summary>
    /// Stores an archive. Bodies that are not ZIPs, or that exceed the run cache's per-archive
    /// ceiling, are not written — a disk cache must not become a dump.
    /// </summary>
    /// <param name="key">Archive key.</param>
    /// <param name="bytes">Archive body.</param>
    public void Put(string? key, byte[]? bytes)
    {
        if (!Enabled || string.IsNullOrEmpty(key) || bytes == null || bytes.Length == 0)
        {
            return;
        }

        if (bytes.Length > RunArchiveCache.DefaultMaxEntryBytes)
        {
            return;
        }

        // Packs only, for the same reason the client refuses plain bodies: a page segment is
        // shared by every file of that page, so a single file stored under it would be served to
        // the wrong caller. A ZIP carries the whole page (see ArchiveKey and F-M370).
        if (bytes.Length <= 4 || bytes[0] != 'P' || bytes[1] != 'K')
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                string path = PathFor(key);
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
                Stores++;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }

    /// <summary>
    /// Drops expired archives and then the oldest ones until the ceiling is met. Called at run
    /// start, so a run never begins on a full volume.
    /// </summary>
    public void Trim()
    {
        if (!Enabled || _directory == null)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var files = new List<FileInfo>();
                foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*.zip"))
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    if (IsExpired(info))
                    {
                        Delete(path);
                        continue;
                    }

                    files.Add(info);
                }

                long total = files.Sum(f => f.Length);
                if (total <= _maxBytes)
                {
                    return;
                }

                foreach (var info in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= _maxBytes)
                    {
                        break;
                    }

                    total -= info.Length;
                    Delete(info.FullName);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }

    /// <summary>Total bytes currently held on disk, for the diagnostics endpoint.</summary>
    /// <returns>Size in bytes; 0 when disabled or unreadable.</returns>
    public long SizeBytes()
    {
        if (_directory == null)
        {
            return 0;
        }

        try
        {
            return System.IO.Directory.EnumerateFiles(_directory, "*.zip")
                .Select(p => new FileInfo(p))
                .Where(f => f.Exists)
                .Sum(f => f.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Number of archives currently on disk.</summary>
    /// <returns>File count.</returns>
    public int Count()
    {
        if (_directory == null)
        {
            return 0;
        }

        try
        {
            return System.IO.Directory.EnumerateFiles(_directory, "*.zip").Count();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private string PathFor(string key) => Path.Combine(_directory!, key + ".zip");

    private bool IsExpired(FileInfo info) =>
        _ttlDays > 0 && info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-_ttlDays);

    private void Delete(string path)
    {
        try
        {
            File.Delete(path);
            Removed++;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }
}
