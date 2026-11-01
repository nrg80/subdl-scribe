using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M369: cache for downloaded subtitle archives, valid for ONE download run only.
/// The same ZIP is fetched more than once inside a single run whenever an episode needs a
/// second variant out of it (measured on 31.10.2026: three archives downloaded twice within
/// fifteen minutes, and the run spent 50 quota units to save 46 files). Every hit saves one
/// unit of the free daily limit.
/// <para>
/// Deliberately NOT persisted and NOT shared: the pipeline clears it at run start and in the
/// run's finally block, so it never grows across runs, never survives a restart, and cannot
/// serve stale bytes after an archive was replaced upstream. Both ceilings exist for the Pi's
/// RAM — a whole-season pack can be far bigger than a single episode's subtitle.
/// </para>
/// </summary>
public sealed class RunArchiveCache
{
    /// <summary>Default number of archives kept at once.</summary>
    public const int DefaultMaxEntries = 32;

    /// <summary>Default ceiling for one archive (16 MB); bigger packs are not cached.</summary>
    public const int DefaultMaxEntryBytes = 16 * 1024 * 1024;

    /// <summary>Default ceiling for the whole cache (64 MB).</summary>
    public const long DefaultMaxTotalBytes = 64L * 1024 * 1024;

    private readonly int _maxEntries;
    private readonly int _maxEntryBytes;
    private readonly long _maxTotalBytes;
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RunArchiveCache"/> class.
    /// </summary>
    /// <param name="maxEntries">Maximum number of archives kept; the oldest is dropped first.</param>
    /// <param name="maxEntryBytes">Archives larger than this are not cached at all.</param>
    /// <param name="maxTotalBytes">Total budget for all cached archives.</param>
    public RunArchiveCache(
        int maxEntries = DefaultMaxEntries,
        int maxEntryBytes = DefaultMaxEntryBytes,
        long maxTotalBytes = DefaultMaxTotalBytes)
    {
        _maxEntries = maxEntries < 1 ? 1 : maxEntries;
        _maxEntryBytes = maxEntryBytes < 0 ? 0 : maxEntryBytes;
        _maxTotalBytes = maxTotalBytes < 0 ? 0 : maxTotalBytes;
    }

    /// <summary>Gets the number of successful lookups since the last reset.</summary>
    public int Hits { get; private set; }

    /// <summary>Gets the number of lookups that had to go to the network.</summary>
    public int Misses { get; private set; }

    /// <summary>Gets the number of archives taken into the cache.</summary>
    public int Stores { get; private set; }

    /// <summary>Gets the number of archives dropped because of a ceiling.</summary>
    public int Evictions { get; private set; }

    /// <summary>Gets the number of archives refused because they exceed the per-entry ceiling.</summary>
    public int Refused { get; private set; }

    /// <summary>Gets the number of archives currently held.</summary>
    public int Count => _entries.Count;

    /// <summary>Gets the number of bytes currently held.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>
    /// Looks an archive up by its download URL.
    /// </summary>
    /// <param name="url">Absolute download URL, used verbatim as the key.</param>
    /// <param name="bytes">The cached archive when the lookup succeeds, otherwise null.</param>
    /// <returns>True when the archive was already in the cache.</returns>
    public bool TryGet(string url, out byte[]? bytes)
    {
        if (!string.IsNullOrEmpty(url) && _entries.TryGetValue(url, out byte[]? found))
        {
            Hits++;
            bytes = found;
            return true;
        }

        Misses++;
        bytes = null;
        return false;
    }

    /// <summary>
    /// Stores an archive for the rest of the run. Re-storing the same URL replaces the entry.
    /// </summary>
    /// <param name="url">Absolute download URL, used verbatim as the key.</param>
    /// <param name="bytes">The raw archive bytes as received (before any extraction).</param>
    public void Put(string url, byte[] bytes)
    {
        if (string.IsNullOrEmpty(url) || bytes == null || bytes.Length == 0)
        {
            return;
        }

        if (bytes.Length > _maxEntryBytes)
        {
            Refused++;
            return;
        }

        if (_entries.TryGetValue(url, out byte[]? previous))
        {
            TotalBytes -= previous.Length;
            _entries.Remove(url);
        }

        while (_order.Count >= _maxEntries || TotalBytes + bytes.Length > _maxTotalBytes)
        {
            if (!_order.TryDequeue(out string? oldest) || oldest == null)
            {
                break;
            }

            if (_entries.TryGetValue(oldest, out byte[]? dropped))
            {
                TotalBytes -= dropped.Length;
                _entries.Remove(oldest);
                Evictions++;
            }
        }

        _entries[url] = bytes;
        _order.Enqueue(url);
        TotalBytes += bytes.Length;
        Stores++;
    }

    /// <summary>
    /// Drops every archive and resets the counters. Called at run start and in the run's
    /// finally block, so the cache is empty outside a download run.
    /// </summary>
    public void Reset()
    {
        _entries.Clear();
        _order.Clear();
        TotalBytes = 0;
        Hits = 0;
        Misses = 0;
        Stores = 0;
        Evictions = 0;
        Refused = 0;
    }
}
