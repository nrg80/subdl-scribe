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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.SubdlScribe.Data;
using LiteDB;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubdlScribe.Pipeline;

namespace Jellyfin.Plugin.SubdlScribe.Registry;

/// <summary>
/// Persistent state of the subtitle pipeline, split by the kind of thing being described:
/// <list type="bullet">
/// <item><description>video files — area 2, keyed by media hash</description></item>
/// <item><description>embedded tracks — area 2 beneath the file, keyed by media hash + position</description></item>
/// <item><description>sidecar files — area 3, keyed by content hash</description></item>
/// <item><description>burned download candidates — area 4, keyed by item + language + SubDL id</description></item>
/// </list>
/// <para>
/// Two rules hold everywhere in this class:
/// </para>
/// <para>
/// F-M194b: every area is keyed by a computed business key stored in the record's own id, never
/// by a database-assigned auto id.
/// <b>1. A row is written under a computed business key, never the database's auto id.</b>
/// The previous implementation upserted <c>new SubtitleEntity { Id = 0 }</c>; LiteDB resolves an
/// upsert by <c>_id</c>, so zero never matched and every write inserted. That produced 1277 rows
/// for 782 facts (F-M194) and is structurally impossible now.
/// </para>
/// <para>
/// <b>2. The stored state is the outcome, not the process.</b> A subtitle is uploaded, rejected or
/// downloaded — there is no separate "settled" row beside it saying the same thing twice, because
/// every reader then has to know both layers and keep them consistent. "Is this position finished?"
/// is derived by asking whether any of the three outcome states is present.
/// </para>
/// </summary>
public sealed class ContentHashRegistry : IDisposable
{
    private readonly SubdlDbContext _db;
    private readonly OshashCache _cache;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentHashRegistry"/> class.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="cache">OSHash cache, created from the context when omitted.</param>
    /// <param name="logger">Optional logger.</param>
    public ContentHashRegistry(SubdlDbContext db, OshashCache? cache = null, ILogger? logger = null)
    {
        _db = db;
        _cache = cache ?? new OshashCache(db, logger);
        _logger = logger;
    }

    // ---------------------------------------------------------------- hashing

    /// <summary>
    /// Canonical form of SRT content: BOM stripped, line endings normalised to LF, trailing space
    /// trimmed. Two files that differ only in those ways must hash identically.
    /// </summary>
    /// <param name="srtContent">Raw content.</param>
    /// <returns>Canonical content.</returns>
    public static string NormalizeSrt(string srtContent)
    {
        if (srtContent.Length > 0 && srtContent[0] == '\uFEFF')
        {
            srtContent = srtContent[1..];
        }

        // CRLF first, then any remaining lone CR (old Mac endings).
        string normalized = srtContent
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return normalized.TrimEnd();
    }

    /// <summary>
    /// MD5 of the normalized content, in SubDL's own format: the hash of exactly the bytes that get
    /// uploaded. Deliberately MD5 — SubDL stores the md5 of the file as received, so any other
    /// algorithm would make the two sides incomparable by construction.
    /// </summary>
    /// <param name="srtContent">Raw SRT content.</param>
    /// <returns>Lowercase 32-character hex hash.</returns>
    /// F-M17c2: remote duplicate detection needs BOTH the canonical payload (F-M185) and this
    /// function (F-M186); either one alone makes the comparison meaningless.
    public static string ComputeHash(string srtContent)
    {
        string normalized = NormalizeSrt(srtContent);
        byte[] bytes = Encoding.UTF8.GetBytes(normalized);
        return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the OSHash of a video file: size plus the first and last 64 KB, summed in 8-byte
    /// words. Cheap, and any change to the file changes the hash.
    /// </summary>
    /// <param name="mediaPath">Path to the video file.</param>
    /// <returns>16-character hex hash, or null when unreadable.</returns>
    public static string? ComputeMediaHash(string mediaPath)
    {
        try
        {
            using var fs = File.OpenRead(mediaPath);
            const int maxChunk = 65_536;
            int chunk = (int)Math.Min(maxChunk, fs.Length);
            if (chunk <= 0)
            {
                return null;
            }

            var buf = new byte[chunk];
            ulong hash = (ulong)fs.Length;
            if (fs.Read(buf, 0, chunk) != chunk)
            {
                return null;
            }

            for (int i = 0; i + 8 <= chunk; i += 8)
            {
                hash += BitConverter.ToUInt64(buf, i);
            }

            fs.Seek(-chunk, SeekOrigin.End);
            if (fs.Read(buf, 0, chunk) != chunk)
            {
                return null;
            }

            for (int i = 0; i + 8 <= chunk; i += 8)
            {
                hash += BitConverter.ToUInt64(buf, i);
            }

            return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Media hash for a file: cache first (validated against size and mtime), computed and stored
    /// on a miss.
    /// </summary>
    /// <param name="mediaPath">Path to the video file.</param>
    /// <returns>Hash, or null.</returns>
    public string? GetMediaHash(string mediaPath)
    {
        try
        {
            var fi = new FileInfo(mediaPath);
            if (!fi.Exists)
            {
                return null;
            }

            long mtimeUnix = (long)(fi.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var cached = _cache.Lookup(mediaPath, fi.Length, mtimeUnix, TimeSpan.Zero, out _);
            if (cached != null)
            {
                return cached;
            }

            var computed = ComputeMediaHash(mediaPath);
            if (computed != null)
            {
                _cache.Store(mediaPath, computed, fi.Length, mtimeUnix);
            }

            return computed;
        }
        catch (Exception ex)
        {
            LogUtil.Detail(_logger, "[SubDL-DB] media hash failed for {Path}: {Msg}", mediaPath, ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------ media (area 2)

    /// <summary>Ensures a media record exists and applies an optional patch.</summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <param name="jellyfinItemId">Optional item id.</param>
    /// <param name="path">Optional path.</param>
    /// <param name="patch">Optional mutation.</param>
    /// <returns>The stored record.</returns>
    public MediaEntity EnsureMedia(string mediaHash, string? jellyfinItemId = null, string? path = null, Action<MediaEntity>? patch = null)
        => _db.EnsureMedia(mediaHash, jellyfinItemId, path, patch);

    /// <summary>Reads a media record.</summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <returns>Record or null.</returns>
    public MediaEntity? GetMedia(string mediaHash) => _db.Media.FindById(mediaHash);

    /// <summary>Removes a media record with its embedded tracks and sidecars.</summary>
    /// <param name="mediaHash">Media hash.</param>
    public void DeleteMediaAndSubtitles(string mediaHash)
    {
        _db.Embeds.DeleteMany(x => x.MediaHash == mediaHash);
        _db.Sidecars.DeleteMany(x => x.MediaHash == mediaHash);
        _db.Media.Delete(mediaHash);
    }

    /// <summary>
    /// F-M261: moves a media file's whole registry state from its old hash to its new one.
    /// <para>
    /// The OSHash covers size plus the first and last 64 KB, and a Matroska segment header carries
    /// its own size — so writing a language tag into a container changes the file's identity. Every
    /// row of that file is keyed by the hash (<c>media</c>, <c>embeds</c>, <c>sidecars</c>), and
    /// without this move the file would keep TWO identities: the rows under the old hash stay,
    /// because nothing prunes them — <see cref="PruneDeadMediaAndSubtitles"/> only removes a media
    /// row whose JELLYFIN ITEM is gone and the item is still there, and the path-based prune is
    /// never called. Measured on a real rewrite: two media rows for one file, same item id, same
    /// path, with the download marks and every embed row left on the dead one.
    /// </para>
    /// <para>
    /// The move is the union of both sides where they collide, and the OLD row wins: what stands
    /// under the old hash was earned by work on this same file, while a row under the new hash can
    /// only come from a run that already touched the rewritten file. Verdicts and observations are
    /// carried over unchanged — this is a rename, not a re-decision.
    /// </para>
    /// <para>
    /// Same hash on both sides is a no-op, and an empty hash is refused: a hash that could not be
    /// computed must never act as if it were one.
    /// </para>
    /// </summary>
    /// <param name="oldHash">Hash the file had before it was rewritten.</param>
    /// <param name="newHash">Hash the file has now.</param>
    /// <returns>Number of rows moved or merged.</returns>
    public int ReplaceMediaIdentity(string? oldHash, string? newHash)
    {
        if (string.IsNullOrEmpty(oldHash) || string.IsNullOrEmpty(newHash)
            || string.Equals(oldHash, newHash, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        int moved = 0;

        // 1. media — carry the whole state (marks, ids, aggregate language list) to the new key.
        var oldMedia = _db.Media.FindById(oldHash);
        if (oldMedia != null)
        {
            var target = _db.EnsureMedia(newHash);

            // The new row keeps whatever it already established only where the old row has nothing
            // to say; everything the old row carries is the more informed value.
            target.JellyfinItemId = oldMedia.JellyfinItemId ?? target.JellyfinItemId;
            target.Path = oldMedia.Path ?? target.Path;
            target.ImdbId = oldMedia.ImdbId ?? target.ImdbId;
            target.TmdbId = oldMedia.TmdbId ?? target.TmdbId;
            target.SdId = oldMedia.SdId ?? target.SdId;
            target.LanguagesAvailable = oldMedia.LanguagesAvailable ?? target.LanguagesAvailable;
            target.IsSeries = oldMedia.IsSeries || target.IsSeries;
            target.Season ??= oldMedia.Season;
            target.Episode ??= oldMedia.Episode;

            // Completion marks: a file whose rewrite was triggered by its own language gate has
            // already been worked on, and that verdict belongs to the file, not to a byte count.
            target.SubtitlesUploadedAt ??= oldMedia.SubtitlesUploadedAt;
            target.SubtitlesDownloadedAt ??= oldMedia.SubtitlesDownloadedAt;
            target.SubtitlesDownloadedLanguages ??= oldMedia.SubtitlesDownloadedLanguages;
            target.LastSearchUtc ??= oldMedia.LastSearchUtc;
            target.LastSearchLanguages ??= oldMedia.LastSearchLanguages;
            target.LastSeen = DateTime.UtcNow;

            _db.Media.Upsert(target);
            _db.Media.Delete(oldHash);
            moved++;

            LogUtil.Detail(_logger, "[SubDL-DB] media identity {Old} -> {New} ({Path})",
                oldHash, newHash, target.Path ?? "?");
        }

        // 2. embeds — keyed by "<hash>|<pos>", so the row is rebuilt under the new key. The new
        // position set is the same, a tag write does not add or remove streams.
        foreach (var row in _db.Embeds.FindAll().Where(x => x.MediaHash == oldHash).ToList())
        {
            string newId = SubdlDbContext.EmbedKey(newHash, row.SubPos);
            var existing = _db.Embeds.FindById(newId);

            // An existing row under the new hash is left alone: it was written by a run that
            // already saw the rewritten file, and it therefore knows the post-write truth.
            if (existing != null)
            {
                _db.Embeds.Delete(row.Id);
                moved++;
                continue;
            }

            _db.Embeds.Delete(row.Id);
            row.Id = newId;
            row.MediaHash = newHash;
            _db.Embeds.Upsert(row);
            moved++;
        }

        // 3. sidecars — the sidecar's own key is its CONTENT hash and does not change; only the
        // parent pointer moves. A .srt beside the file is not touched by a tag write.
        foreach (var row in _db.Sidecars.FindAll().Where(x => x.MediaHash == oldHash).ToList())
        {
            row.MediaHash = newHash;
            _db.Sidecars.Update(row);
            moved++;
        }

        if (moved > 0)
        {
            RefreshMediaAggregates(newHash);
            _db.Embeds.DeleteMany(x => x.MediaHash == oldHash);
            _db.Sidecars.DeleteMany(x => x.MediaHash == oldHash);
            _db.Media.Delete(oldHash);
        }

        return moved;
    }

    /// <summary>Removes media records whose Jellyfin items no longer exist, with their subtitles.</summary>
    /// <param name="aliveItemIds">Ids still present in Jellyfin.</param>
    /// <returns>Counts of removed rows.</returns>
    public (int subtitles, int media) PruneDeadMediaAndSubtitles(IEnumerable<string> aliveItemIds)
    {
        var alive = aliveItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dead = _db.Media.FindAll()
            .Where(m => !string.IsNullOrEmpty(m.JellyfinItemId) && !alive.Contains(m.JellyfinItemId!))
            .Select(m => m.Id)
            .ToList();

        int embeds = 0, sidecars = 0;
        foreach (var hash in dead)
        {
            embeds += _db.Embeds.DeleteMany(x => x.MediaHash == hash);
            sidecars += _db.Sidecars.DeleteMany(x => x.MediaHash == hash);
        }

        int media = dead.Count == 0 ? 0 : _db.Media.DeleteMany(x => dead.Contains(x.Id));
        return (embeds + sidecars, media);
    }

    /// <summary>Removes media records whose file is gone from disk, with their subtitles.</summary>
    /// <returns>Counts of removed rows.</returns>
    public (int subtitles, int media) PruneMissingPathMediaAndSubtitles()
    {
        var gone = _db.Media.FindAll()
            .Where(m => !string.IsNullOrEmpty(m.Path) && !File.Exists(m.Path!))
            .Select(m => m.Id)
            .ToList();

        int embeds = 0, sidecars = 0;
        foreach (var hash in gone)
        {
            embeds += _db.Embeds.DeleteMany(x => x.MediaHash == hash);
            sidecars += _db.Sidecars.DeleteMany(x => x.MediaHash == hash);
        }

        int media = gone.Count == 0 ? 0 : _db.Media.DeleteMany(x => gone.Contains(x.Id));
        return (embeds + sidecars, media);
    }

    // --------------------------------------------------- embedded tracks (area 2)

    /// <summary>
    /// Records an embedded track verdict. The key is (media hash, position), so re-marking the same
    /// stream always updates its own row.
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="subPos">ffmpeg stream position.</param>
    /// <param name="language">Language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <param name="status">One of <see cref="SubtitleStatus"/>.</param>
    /// <param name="contentHash">Content hash when the content was read.</param>
    /// <param name="reason">Rejection reason when rejected.</param>
    /// <param name="subdlId">SubDL id when known.</param>
    public void MarkEmbed(string? mediaHash, int subPos, string language, bool hearingImpaired,
                          string status, string? contentHash = null, string? reason = null, string? subdlId = null)
    {
        if (string.IsNullOrEmpty(mediaHash) || subPos < 0)
        {
            return;
        }

        _db.EnsureMedia(mediaHash);
        var id = SubdlDbContext.EmbedKey(mediaHash, subPos);
        var existing = _db.Embeds.FindById(id) ?? new EmbedTrackEntity
        {
            Id = id,
            MediaHash = mediaHash,
            SubPos = subPos
        };

        existing.Language = language;
        existing.HearingImpaired = hearingImpaired;
        existing.Status = status;
        existing.Reason = reason;
        existing.StatusAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(contentHash))
        {
            existing.ContentHash = contentHash;
        }

        if (!string.IsNullOrEmpty(subdlId))
        {
            existing.SubdlId = subdlId;
        }

        _db.Embeds.Upsert(existing);
        RefreshMediaAggregates(mediaHash);
        LogUtil.Detail(_logger, "[SubDL-DB] embed {Media}|{Pos} {Status} {Lang}", mediaHash, subPos, status, language);
    }

    /// <summary>
    /// F-M257: records what a track IS (language, HI at this position) without touching a verdict.
    /// <para>
    /// The embedded area's only writer used to be <see cref="MarkEmbed"/>, called exclusively by the
    /// upload pipeline — so on a default install (<c>UploadEnabled=false</c>) the area stayed empty
    /// and every registry reader of it answered from nothing. The seeder scans every item anyway and
    /// holds the streams in its hand; this method lets that fact be recorded where the readers look.
    /// </para>
    /// <para>
    /// A verdict, once written, is never overwritten here: <see cref="MarkEmbed"/> sets
    /// <c>Status</c>/<c>Reason</c> unconditionally, so calling it from an observer would erase an
    /// uploader's "uploaded"/"rejected" verdict with an observation. A fresh row is created as
    /// <see cref="SubtitleStatus.Observed"/>, which no terminality check counts.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="subPos">ffmpeg stream position.</param>
    /// <param name="language">Language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <returns>True when a row was created or its facts changed.</returns>
    public bool ObserveEmbed(string? mediaHash, int subPos, string language, bool hearingImpaired)
    {
        if (string.IsNullOrEmpty(mediaHash) || subPos < 0)
        {
            return false;
        }

        _db.EnsureMedia(mediaHash);
        var id = SubdlDbContext.EmbedKey(mediaHash, subPos);
        var existing = _db.Embeds.FindById(id);

        // A verdict stands. The observer does not argue with it — that is the difference between
        // recording a fact and re-deciding one.
        if (existing != null && existing.Status != SubtitleStatus.Observed)
        {
            return false;
        }

        if (existing != null
            && string.Equals(existing.Language, language, StringComparison.OrdinalIgnoreCase)
            && existing.HearingImpaired == hearingImpaired)
        {
            return false; // already recorded, unchanged
        }

        var row = existing ?? new EmbedTrackEntity
        {
            Id = id,
            MediaHash = mediaHash,
            SubPos = subPos
        };

        row.Language = language;
        row.HearingImpaired = hearingImpaired;
        row.Status = SubtitleStatus.Observed;
        row.Reason = null;
        row.StatusAt = DateTime.UtcNow;

        _db.Embeds.Upsert(row);
        RefreshMediaAggregates(mediaHash);
        LogUtil.Detail(_logger, "[SubDL-DB] embed {Media}|{Pos} observed {Lang} (HI={Hi})",
            mediaHash, subPos, language, hearingImpaired);
        return true;
    }

    /// <summary>Reads one embedded track.</summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="subPos">ffmpeg stream position.</param>
    /// <returns>Track or null.</returns>
    public EmbedTrackEntity? GetEmbed(string? mediaHash, int subPos)
        => string.IsNullOrEmpty(mediaHash) ? null : _db.Embeds.FindById(SubdlDbContext.EmbedKey(mediaHash, subPos));

    /// <summary>
    /// Records that language detection ran for this track and how it ended.
    /// <para>
    /// Deliberately NOT a <c>MarkEmbed</c> call: that one sets <c>Status</c>/<c>Reason</c>
    /// unconditionally, and a detection attempt is a fact about the WORK, not a verdict about the
    /// subtitle. Writing it through <c>MarkEmbed</c> would stamp <c>rejected</c> on the row, which
    /// the file-completeness arithmetic reads as terminal and would count as a settled track.
    /// Only the two detection fields are touched, everything else on the row is left as it is.
    /// </para>
    /// <para>
    /// A row that does not exist yet is created as <see cref="SubtitleStatus.Observed"/> with an
    /// empty language, exactly like an observation: the language is unknown, which is the truth.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="subPos">ffmpeg stream position.</param>
    /// <param name="outcome">One of <see cref="DetectionOutcome"/>.</param>
    /// <param name="language">Detected language when one was found, else null.</param>
    /// <param name="hearingImpaired">HI/SDH verdict for the track, when the caller knows it.</param>
    /// <returns>True when a row was created or changed.</returns>
    public bool MarkDetectionAttempt(string? mediaHash, int subPos, string outcome, string? language = null, bool hearingImpaired = false)
    {
        if (string.IsNullOrEmpty(mediaHash) || subPos < 0)
        {
            return false;
        }

        string id = SubdlDbContext.EmbedKey(mediaHash, subPos);
        var row = _db.Embeds.FindById(id);
        if (row == null)
        {
            _db.EnsureMedia(mediaHash, m => { });
            row = new EmbedTrackEntity
            {
                Id = id,
                MediaHash = mediaHash,
                SubPos = subPos,
                Status = SubtitleStatus.Observed,
                StatusAt = DateTime.UtcNow
            };
        }

        bool changed = row.DetectionAttemptedAt == null
                       || !string.Equals(row.DetectionOutcome, outcome, StringComparison.OrdinalIgnoreCase)
                       || (language != null && !string.Equals(row.Language, language, StringComparison.OrdinalIgnoreCase));

        row.DetectionAttemptedAt = DateTime.UtcNow;
        row.DetectionOutcome = outcome;

        // A verdict already on the row outranks this observation: "uploaded" and "rejected" are
        // decisions about the subtitle, and a detection pass must not overwrite one.
        if (language != null && string.Equals(row.Status, SubtitleStatus.Observed, StringComparison.OrdinalIgnoreCase))
        {
            row.Language = language;
            row.HearingImpaired = hearingImpaired;
        }

        _db.Embeds.Upsert(row);
        if (changed)
        {
            LogUtil.Detail(_logger, "[SubDL-DB] embed {Media}|{Pos} detection {Outcome} {Lang}",
                mediaHash, subPos, outcome, language ?? "-");
        }

        return changed;
    }

    /// <summary>
    /// The stream positions of this file whose language detection has already run.
    /// <para>
    /// The gate asks this BEFORE touching the container: extraction plus classification is the
    /// expensive half, and a track the detector could not classify would otherwise be re-opened on
    /// every single scan. Keyed on the presence of the attempt, not on its outcome — a track that
    /// resolved successfully needs no second look either.
    /// </para>
    /// <para>
    /// The set is only as good as the identity it hangs from. A tag write rewrites the container,
    /// the hash changes, and the new hash has no rows yet, so the next scan legitimately retries.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <returns>Attempted positions.</returns>
    public HashSet<int> DetectedPositions(string? mediaHash)
    {
        var found = new HashSet<int>();
        if (string.IsNullOrEmpty(mediaHash))
        {
            return found;
        }

        foreach (var row in _db.Embeds.Find(x => x.MediaHash == mediaHash))
        {
            if (row.DetectionAttemptedAt != null)
            {
                found.Add(row.SubPos);
            }
        }

        return found;
    }

    /// <summary>All embedded tracks of a file, ordered by position.</summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <returns>Tracks.</returns>
    public List<EmbedTrackEntity> GetEmbeds(string? mediaHash)
        => string.IsNullOrEmpty(mediaHash)
            ? new List<EmbedTrackEntity>()
            : _db.Embeds.Find(x => x.MediaHash == mediaHash).OrderBy(x => x.SubPos).ToList();

    /// <summary>
    /// F-M254: the languages whose hearing-impaired variant the registry already holds — the ONE
    /// answer the HI question is read from.
    /// <para>
    /// The HI switch used to be answered live, from Jellyfin's stream titles and from the sidecar
    /// names next to the media. Both are snapshots the registry already took: the uploader stores the
    /// stream verdict through <c>MarkEmbed</c>, the downloader stores the fetched variant through
    /// <c>MarkDownloaded</c>. Asking the sources again meant a stored verdict could be ignored — a
    /// captioned EN track whose title Jellyfin does not surface read as "HI absent" while its own row
    /// said otherwise, so the language was fetched again for a variant the container already carried.
    /// </para>
    /// <para>
    /// Both areas are consulted because the variant arrives either way: an embedded track, or a
    /// sidecar fetched from SubDL.
    /// </para>
    /// <para>
    /// F-M256: the parameter is the media PATH, not the hash. Every caller holds a path, and this
    /// method resolved nothing: handing it a path where a hash was expected made every lookup miss,
    /// so it returned an empty set for a file whose rows existed and every language read as "HI
    /// absent". Measured on prod 30.09.2026: 14 items whose container already carried an embedded
    /// <c>SDH</c> track were searched again. The hash is resolved HERE, once, so no caller can pass
    /// the wrong kind of value again — the mistake is not repairable by a caller, it is impossible.
    /// </para>
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <returns>Language codes with a recorded hearing-impaired variant.</returns>
    public HashSet<string> HearingImpairedLanguages(string? mediaPath)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(mediaPath))
        {
            return found;
        }

        string? mediaHash = GetMediaHash(mediaPath);
        if (string.IsNullOrEmpty(mediaHash))
        {
            return found;
        }

        foreach (var language in GetEmbeds(mediaHash).Where(x => x.HearingImpaired).Select(x => x.Language))
        {
            if (!string.IsNullOrEmpty(language))
            {
                found.Add(language);
            }
        }

        foreach (var language in GetSidecars(mediaHash).Where(x => x.HearingImpaired).Select(x => x.Language))
        {
            if (!string.IsNullOrEmpty(language))
            {
                found.Add(language);
            }
        }

        return found;
    }

    /// <summary>
    /// F-M263: the languages recorded for a file's EMBEDDED tracks — read from the registry, not
    /// from the stream list.
    /// </summary>
    /// <remarks>
    /// The download pipeline's coverage check used to ask Jellyfin for the item's streams through
    /// `SidecarNaming.EmbeddedPresentLanguages`, and that answer is a CACHED snapshot: a container
    /// the seeder corrected in the same cycle still reports its OLD tag, so the language read as
    /// absent and the file was searched for a subtitle it demonstrably carries. The gate call in
    /// the pipeline existed only to paper over that with its own in-memory `_resolvedLanguages`,
    /// and it brought two defects with it (no hash pair, so no identity move; and a rewrite that no
    /// dry run stopped). With the gate gone from the pipeline, the record the SEEDER wrote is the
    /// authority — and unlike the stream list it is current, because the seeder wrote it from the
    /// gate's own verdict after the file was rewritten.
    /// <para>
    /// The hash is resolved from the path HERE, once — the same rule F-M256 established for
    /// <see cref="HearingImpairedLanguages"/>: every caller holds a path, and a method that expects
    /// a hash while being handed a path misses every lookup without a compiler error.
    /// </para>
    /// <para>
    /// Forced and bitmap tracks are excluded by the writers, not here: a forced track is not the
    /// film's subtitle (F-M246) and a bitmap track carries pixels (F-M257). Only rows that exist are
    /// read, and no row is written for a track the gate could not decide.
    /// </para>
    /// </remarks>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <returns>Language codes recorded for embedded tracks.</returns>
    public HashSet<string> EmbeddedLanguages(string? mediaPath)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(mediaPath))
        {
            return found;
        }

        string? mediaHash = GetMediaHash(mediaPath);
        if (string.IsNullOrEmpty(mediaHash))
        {
            return found;
        }

        foreach (var language in GetEmbeds(mediaHash).Select(x => x.Language))
        {
            if (!string.IsNullOrEmpty(language))
            {
                found.Add(language);
            }
        }

        return found;
    }

    /// <summary>
    /// True when this exact stream position already reached a terminal state. Asked per position —
    /// never per language — because two streams of one file can share a language, and treating the
    /// language as the unit was what let one rejected stream block its siblings.
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="subPos">Stream position.</param>
    /// <returns>True when terminal.</returns>
    public bool IsEmbedTerminal(string? mediaHash, int subPos)
    {
        var track = GetEmbed(mediaHash, subPos);
        return track != null && (track.Status == SubtitleStatus.Uploaded || track.Status == SubtitleStatus.Rejected);
    }

    /// <summary>
    /// True when EVERY given position is terminal. Replaces the former ArePositionsSettled: the
    /// answer is derived from the outcome rows instead of a parallel set of "settled" marker rows.
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="positions">Stream positions to check.</param>
    /// <returns>True when all positions are terminal.</returns>
    public bool ArePositionsTerminal(string? mediaHash, IEnumerable<int> positions)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return false;
        }

        var need = positions.Where(p => p >= 0).Distinct().ToList();
        if (need.Count == 0)
        {
            return false;
        }

        var terminal = _db.Embeds
            .Find(x => x.MediaHash == mediaHash
                    && (x.Status == SubtitleStatus.Uploaded || x.Status == SubtitleStatus.Rejected))
            .Select(x => x.SubPos)
            .ToHashSet();

        return need.All(terminal.Contains);
    }

    /// <summary>
    /// True when this (file, language, HI) pair already went up. Pair-level by design: it answers
    /// "do I still need to upload this language for this file", which is what the self-echo guard
    /// and the collector ask. Individual stream verdicts are read per position instead.
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="language">Language code.</param>
    /// <param name="hearingImpaired">HI variant.</param>
    /// <returns>True when an uploaded stream of that pair exists.</returns>
    public bool IsUploaded(string? mediaHash, string language, bool hearingImpaired = false)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return false;
        }

        return _db.Embeds.Exists(x => x.MediaHash == mediaHash
            && x.Language == language
            && x.HearingImpaired == hearingImpaired
            && x.Status == SubtitleStatus.Uploaded);
    }

    /// <summary>
    /// Recorded rejection reason for a specific embedded stream position, else null.
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="subPos">Stream position.</param>
    /// <returns>Reason or null.</returns>
    public string? EmbedRejectedReason(string? mediaHash, int subPos)
    {
        var track = GetEmbed(mediaHash, subPos);
        return track?.Status == SubtitleStatus.Rejected ? track.Reason : null;
    }

    /// <summary>
    /// Number of distinct (language, HI) pairs a VERDICT was recorded for.
    /// <para>
    /// F-M257: observations (<see cref="SubtitleStatus.Observed"/>) are deliberately NOT counted.
    /// The seeder uses this number against the pairs a file needs; counting observation rows would
    /// make the set look complete on the strength of "we looked at the stream" and the item would
    /// never be queued for upload again — the observer would have silently cancelled the uploader's
    /// work.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <returns>Count of distinct pairs with a verdict.</returns>
    public int CountKnownPairs(string? mediaHash)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return 0;
        }

        return _db.Embeds.Find(x => x.MediaHash == mediaHash
                && (x.Status == SubtitleStatus.Uploaded || x.Status == SubtitleStatus.Rejected))
            .Select(x => x.Language + "|" + x.HearingImpaired)
            .Distinct()
            .Count();
    }

    /// <summary>
    /// Lists uploaded embedded tracks, newest verdict first.
    /// </summary>
    /// <returns>Uploaded tracks.</returns>
    public List<EmbedTrackEntity> GetUploadedEmbeds()
        => _db.Embeds.Find(x => x.Status == SubtitleStatus.Uploaded).ToList();

    // -------------------------------------------------------- sidecars (area 3)

    /// <summary>
    /// Records a sidecar verdict, keyed by content hash.
    /// <para>
    /// Content is the identity because that is what makes a sidecar an independent thing: the same
    /// subtitle stays known after a rename, and two sidecars sharing a language but differing in
    /// text stay two rows. The file name is stored for diagnosis and plays no part in the key.
    /// </para>
    /// </summary>
    /// <param name="contentHash">MD5 of the normalized content.</param>
    /// <param name="mediaHash">Media hash of the file it sits beside.</param>
    /// <param name="language">Language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <param name="status">One of <see cref="SubtitleStatus"/>.</param>
    /// <param name="reason">Rejection reason when rejected.</param>
    /// <param name="subdlId">SubDL id when known.</param>
    /// <param name="fileName">File name for diagnostics.</param>
    /// <param name="path">Full path for diagnostics.</param>
    public void MarkSidecar(string? contentHash, string mediaHash, string language, bool hearingImpaired,
                            string status, string? reason = null, string? subdlId = null,
                            string? fileName = null, string? path = null)
    {
        if (string.IsNullOrEmpty(contentHash))
        {
            return;
        }

        var media = _db.EnsureMedia(mediaHash);
        var existing = _db.Sidecars.FindById(contentHash) ?? new SidecarEntity { Id = contentHash };

        existing.MediaHash = mediaHash;
        existing.Language = language;
        existing.HearingImpaired = hearingImpaired;
        existing.ContentHash = contentHash;
        existing.Status = status;
        existing.Reason = reason;
        existing.StatusAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(subdlId))
        {
            existing.SubdlId = subdlId;
        }

        if (!string.IsNullOrEmpty(fileName))
        {
            existing.FileName = fileName;
        }

        if (!string.IsNullOrEmpty(path))
        {
            existing.Path = path;
        }

        // Copies of the parent metadata so a sidecar can be judged without loading the file record.
        existing.ImdbId = media.ImdbId;
        existing.TmdbId = media.TmdbId;
        existing.IsSeries = media.IsSeries;
        existing.Season = media.Season;
        existing.Episode = media.Episode;

        _db.Sidecars.Upsert(existing);
        LogUtil.Detail(_logger, "[SubDL-DB] sidecar {Hash} {Status} {Lang}", contentHash, status, language);
    }

    /// <summary>
    /// F-M259: records what a loose subtitle FILE is, without touching a verdict.
    /// <para>
    /// The sidecar half of <see cref="ObserveEmbed"/>, and the same gap: <see cref="MarkSidecar"/> is
    /// the area's only writer and only the upload and download pipelines call it, both behind their
    /// own switch (upload is off by default). A <c>.srt</c> beside the media that no plugin run ever
    /// moved — written by another tool, kept from before the registry existed, or simply never
    /// touched because upload is off — therefore had no row at all. The download side reads the disk
    /// for "is the language there?" but asks the REGISTRY for the HI question (F-M254), so such a
    /// file's language looked present and its hearing-impaired variant looked absent, run after run.
    /// </para>
    /// <para>
    /// A verdict is never overwritten here, for the same reason as on the embedded side:
    /// <see cref="MarkSidecar"/> sets <c>Status</c> unconditionally, so observing through it would
    /// erase an uploader's or downloader's verdict. A fresh row is created as
    /// <see cref="SubtitleStatus.Observed"/>.
    /// </para>
    /// </summary>
    /// <param name="contentHash">MD5 of the normalized content (the sidecar's identity).</param>
    /// <param name="mediaHash">Media hash of the file it sits beside.</param>
    /// <param name="language">Language code.</param>
    /// <param name="hearingImpaired">HI/SDH variant.</param>
    /// <param name="fileName">File name for diagnostics.</param>
    /// <param name="path">Full path for diagnostics.</param>
    /// <returns>True when a row was created or its facts changed.</returns>
    public bool ObserveSidecar(string? contentHash, string mediaHash, string language, bool hearingImpaired,
                               string? fileName = null, string? path = null)
    {
        if (string.IsNullOrEmpty(contentHash) || string.IsNullOrEmpty(mediaHash))
        {
            return false;
        }

        _db.EnsureMedia(mediaHash);
        var existing = _db.Sidecars.FindById(contentHash);

        // A verdict stands — recording a fact is not re-deciding one.
        if (existing != null && existing.Status != SubtitleStatus.Observed)
        {
            return false;
        }

        if (existing != null
            && string.Equals(existing.Language, language, StringComparison.OrdinalIgnoreCase)
            && existing.HearingImpaired == hearingImpaired)
        {
            return false; // already recorded, unchanged
        }

        var row = existing ?? new SidecarEntity { Id = contentHash };

        row.MediaHash = mediaHash;
        row.Language = language;
        row.HearingImpaired = hearingImpaired;
        row.ContentHash = contentHash;
        row.Status = SubtitleStatus.Observed;
        row.Reason = null;
        row.StatusAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(fileName))
        {
            row.FileName = fileName;
        }

        if (!string.IsNullOrEmpty(path))
        {
            row.Path = path;
        }

        _db.Sidecars.Upsert(row);
        LogUtil.Detail(_logger, "[SubDL-DB] sidecar {Hash} observed {Lang} (HI={Hi})",
            contentHash, language, hearingImpaired);
        return true;
    }

    /// <summary>Reads a sidecar by content hash.</summary>
    /// <param name="contentHash">Content hash.</param>
    /// <returns>Record or null.</returns>
    public SidecarEntity? GetSidecar(string? contentHash)
        => string.IsNullOrEmpty(contentHash) ? null : _db.Sidecars.FindById(contentHash);

    /// <summary>True when this exact content was uploaded.</summary>
    /// <param name="contentHash">Content hash.</param>
    /// <returns>True when uploaded.</returns>
    public bool IsSidecarUploaded(string? contentHash)
        => GetSidecar(contentHash)?.Status == SubtitleStatus.Uploaded;

    /// <summary>Recorded rejection reason for this content, else null.</summary>
    /// <param name="contentHash">Content hash.</param>
    /// <returns>Reason or null.</returns>
    public string? SidecarRejectedReason(string? contentHash)
    {
        var sidecar = GetSidecar(contentHash);
        return sidecar?.Status == SubtitleStatus.Rejected ? sidecar.Reason : null;
    }

    /// <summary>All sidecars belonging to a media file.</summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <returns>Sidecars.</returns>
    public List<SidecarEntity> GetSidecars(string? mediaHash)
        => string.IsNullOrEmpty(mediaHash)
            ? new List<SidecarEntity>()
            : _db.Sidecars.Find(x => x.MediaHash == mediaHash).ToList();

    // ------------------------------------------------------------- content dedupe

    /// <summary>
    /// True when this content is known anywhere — uploaded or rejected, as an embedded track, a
    /// sidecar or a previous download. Global by design: it answers "has SubDL seen this text from
    /// us before", regardless of which file it came from.
    /// </summary>
    /// <param name="contentHash">Content hash.</param>
    /// <returns>True when known.</returns>
    /// F-M17z: any recorded outcome counts as known; a rejected row is as final as an uploaded one,
    /// so a new rejection reason never needs a new state name.
    public bool IsContentKnown(string? contentHash)
    {
        if (string.IsNullOrEmpty(contentHash))
        {
            return false;
        }

        // F-M257/F-M259: observations are NOT knowledge. This answers "do we already HAVE this
        // content?" — asked by the download path before writing a fetched subtitle and by the
        // uploader before sending one. An observation only says "a file with this content sits on
        // disk"; counting it would make the downloader skip a write it has not performed, and the
        // run would report a save that never happened.
        return _db.Embeds.Exists(x => x.ContentHash == contentHash && x.Status != SubtitleStatus.Observed)
            || _db.Sidecars.Exists(x => x.ContentHash == contentHash && x.Status != SubtitleStatus.Observed);
    }

    /// <summary>True when this content was rejected by SubDL itself as already held remotely.</summary>
    /// <param name="contentHash">Content hash.</param>
    /// <returns>True when a remote duplicate is recorded.</returns>
    public bool IsRemoteDuplicate(string? contentHash)
    {
        if (string.IsNullOrEmpty(contentHash))
        {
            return false;
        }

        return _db.Embeds.Exists(x => x.ContentHash == contentHash
                && x.Status == SubtitleStatus.Rejected
                && x.Reason == RejectReason.DuplicateRemote)
            || _db.Sidecars.Exists(x => x.ContentHash == contentHash
                && x.Status == SubtitleStatus.Rejected
                && x.Reason == RejectReason.DuplicateRemote);
    }

    // -------------------------------------------------------- downloaded (area 3)

    /// <summary>
    /// Records a subtitle fetched from SubDL. Stored as a sidecar: once downloaded the file IS a
    /// sidecar on disk, so it belongs in the same area rather than a parallel one.
    /// </summary>
    /// <param name="contentHash">Content hash of the downloaded subtitle.</param>
    /// <param name="mediaHash">Media hash it was downloaded for.</param>
    /// <param name="language">Language code.</param>
    /// <param name="hearingImpaired">HI variant.</param>
    /// <param name="subdlId">SubDL release id.</param>
    /// <param name="fileName">Written file name.</param>
    /// <param name="path">Written path.</param>
    public void MarkDownloaded(string contentHash, string mediaHash, string language, bool hearingImpaired = false,
                               string? subdlId = null, string? fileName = null, string? path = null)
        => MarkSidecar(contentHash, mediaHash, language, hearingImpaired,
                       SubtitleStatus.Downloaded, reason: null, subdlId: subdlId, fileName: fileName, path: path);

    // ------------------------------------------- rejected download candidates (area 4)

    /// <summary>
    /// Remembers a fetched-and-discarded download candidate so it is never fetched and screened
    /// twice. Keyed by item + language + SubDL id: the verdict belongs to that remote release, not
    /// to a file on disk.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    /// <param name="subdlId">SubDL release id.</param>
    /// <param name="reason">Why it was discarded.</param>
    public void RecordRejectedCandidate(string itemId, string language, string subdlId, string reason)
    {
        if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(subdlId))
        {
            return;
        }

        var id = SubdlDbContext.CandidateKey(itemId, language, subdlId);
        _db.RejectedCandidates.Upsert(new RejectedCandidateEntity
        {
            Id = id,
            ItemId = itemId,
            Language = language.ToUpperInvariant(),
            SubdlId = subdlId,
            Reason = reason,
            Updated = DateTime.UtcNow
        });
    }

    /// <summary>SubDL ids already discarded for this item and language.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="language">Language code.</param>
    /// <returns>Discarded release ids.</returns>
    public List<string> GetRejectedCandidates(string itemId, string language)
    {
        // Compared in LINQ-to-objects rather than in the query: the stored value is normalised on
        // write, and the caller may pass either case.
        return _db.RejectedCandidates
            .Find(x => x.ItemId == itemId)
            .Where(x => string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.SubdlId)
            .Distinct()
            .ToList();
    }

    // ------------------------------------------------------------ file completion

    /// <summary>
    /// Marks the embedded subtitle side of a file as complete.
    /// <para>
    /// Named for subtitles, not for the file: the video file is never uploaded anywhere. Only
    /// subtitle data travels to SubDL.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    public void MarkSubtitlesUploaded(string? mediaHash)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return;
        }

        var now = DateTime.UtcNow;
        _db.EnsureMedia(mediaHash, m =>
        {
            m.SubtitlesUploadedAt = now;
        });
        LogUtil.Detail(_logger, "[SubDL-DB] subtitles uploaded-complete {Media}", mediaHash);
    }

    /// <summary>True when the embedded subtitle side of this file is complete.</summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <returns>True when complete.</returns>
    public bool IsSubtitlesUploaded(string? mediaHash)
        => !string.IsNullOrEmpty(mediaHash) && _db.Media.FindById(mediaHash)?.SubtitlesUploadedAt != null;

    /// <summary>
    /// Marks the download side complete for a specific language set.
    /// <para>
    /// The language set is part of the state, not a note: the mark is only valid for exactly those
    /// languages. Change the configured set and the comparison fails, so the file is revisited for
    /// whatever is now missing instead of being skipped as done.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <param name="languages">Languages that were covered.</param>
    public void MarkSubtitlesDownloaded(string? mediaHash, IReadOnlyList<string> languages)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return;
        }

        var normalized = string.Join(",", languages.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var now = DateTime.UtcNow;
        _db.EnsureMedia(mediaHash, m =>
        {
            m.SubtitlesDownloadedAt = now;
            m.SubtitlesDownloadedLanguages = normalized;
        });
        LogUtil.Detail(_logger, "[SubDL-DB] subtitles downloaded-complete {Media} langs={Langs}", mediaHash, normalized);
    }

    /// <summary>
    /// F-M234 (D): true when the stored language set covers the requested one.
    /// <para>
    /// Extracted so the rule is testable without a database: a stored SUPERSET is complete, a
    /// missing language is not. Removing a language from the configuration must not invalidate the
    /// mark (equality and supersets count), while adding one must.
    /// </para>
    /// </summary>
    /// <param name="storedLanguages">Languages the mark was written for.</param>
    /// <param name="requestedLanguages">Languages wanted now.</param>
    /// <returns>True when nothing requested is missing from the stored set.</returns>
    public static bool CoversLanguages(IEnumerable<string> storedLanguages, IReadOnlyList<string> requestedLanguages)
    {
        var stored = storedLanguages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requestedLanguages.All(stored.Contains);
    }

    /// <summary>
    /// F-M234 (D): true when the download side is complete for at least this language set.
    /// <para>
    /// A SUPERSET stored set still covers a smaller current set: removing a language from the
    /// configuration must not invalidate the mark and send the whole library through a pointless
    /// refetch. Equality (and any stored superset) counts as complete; only languages the stored
    /// mark does NOT cover make the file un-done.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <param name="languages">Languages to compare against.</param>
    /// <returns>True when complete and the stored set covers the requested one.</returns>
    public bool IsSubtitlesDownloaded(string? mediaHash, IReadOnlyList<string> languages)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return false;
        }

        var media = _db.Media.FindById(mediaHash);
        if (media?.SubtitlesDownloadedAt == null)
        {
            return false;
        }

        var stored = (media.SubtitlesDownloadedLanguages ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        return CoversLanguages(stored, languages);
    }

    /// <summary>
    /// F-M234 (A/B): the stored download mark for this file, or null when unmarked. Returns the
    /// language set the mark was written for, so a caller can tell WHICH languages went stale.
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <returns>Stored languages, or null when there is no mark.</returns>
    public List<string>? GetDownloadedLanguages(string? mediaHash)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return null;
        }

        var media = _db.Media.FindById(mediaHash);
        if (media?.SubtitlesDownloadedAt == null)
        {
            return null;
        }

        return (media.SubtitlesDownloadedLanguages ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    /// <summary>
    /// F-M234 (B): which languages of the stored mark no longer have file evidence.
    /// <para>
    /// The mark means "every language settled" — settled either by SAVING a subtitle or by SubDL
    /// answering that it does not have it (QA-exhausted). Only the first kind leaves a file behind,
    /// so a language that is gone from disk AND not settled-as-unavailable proves the mark was
    /// overtaken by a deletion: the stored verdict is a lie and the file must be revisited.
    /// </para>
    /// </summary>
    /// <param name="storedLanguages">Languages the mark was written for.</param>
    /// <param name="missingOnDisk">Languages currently absent from disk.</param>
    /// <param name="settledUnavailable">Languages SubDL has settled as unavailable.</param>
    /// <returns>Languages that lost their evidence (empty = the mark still holds).</returns>
    public static List<string> FindStaleDownloadedLanguages(
        IEnumerable<string> storedLanguages,
        IReadOnlyCollection<string> missingOnDisk,
        IReadOnlyCollection<string> settledUnavailable)
    {
        var missing = missingOnDisk.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settled = settledUnavailable.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return storedLanguages
            .Where(l => missing.Contains(l) && !settled.Contains(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// F-M234 (B): drops a download mark whose file evidence has disappeared, so the next run
    /// reworks the item instead of trusting "file complete" forever.
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <returns>True when a mark was present and removed.</returns>
    public bool InvalidateDownloadedMark(string? mediaHash)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return false;
        }

        var media = _db.Media.FindById(mediaHash);
        if (media?.SubtitlesDownloadedAt == null)
        {
            return false;
        }

        media.SubtitlesDownloadedAt = null;
        media.SubtitlesDownloadedLanguages = null;
        _db.Media.Update(media);
        LogUtil.Detail(_logger, "[SubDL-DB] download mark invalidated {Media} — file evidence gone", mediaHash);
        return true;
    }

    /// <summary>
    /// F-M234 (A): sidecar rows whose stored file path no longer exists.
    /// <para>
    /// Callers MUST have verified the roots first (see the refresh task): an unmounted volume
    /// reports every path as missing, and forgetting valid verdicts would be far worse than keeping
    /// a stale one. Rows without a stored path are never returned — "unknown" is not "deleted".
    /// </para>
    /// </summary>
    /// <returns>Content hashes of the vanished sidecars.</returns>
    public List<string> GetSidecarsMissingFromDisk()
    {
        return _db.Sidecars.FindAll()
            .Where(s => !string.IsNullOrEmpty(s.Path) && !System.IO.File.Exists(s.Path!))
            .Select(s => s.Id)
            .ToList();
    }

    /// <summary>
    /// F-M234 (A): forgets sidecar verdicts (uploaded/rejected/downloaded) whose file is gone.
    /// <para>
    /// The content hash is the key, so the row is only meaningful while the content exists on
    /// disk. A forgotten row means the item is judged again if the subtitle ever comes back.
    /// </para>
    /// </summary>
    /// <param name="contentHashes">Content hashes to forget.</param>
    /// <returns>Number of rows removed.</returns>
    public int ForgetSidecars(IEnumerable<string> contentHashes)
    {
        int removed = 0;
        foreach (var hash in contentHashes)
        {
            if (!string.IsNullOrEmpty(hash) && _db.Sidecars.Delete(hash))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            LogUtil.Detail(_logger, "[SubDL-DB] forgot {N} vanished sidecar verdict(s)", removed);
        }

        return removed;
    }

    /// <summary>
    /// F-M258: embedded rows of a file that no longer match any track of that file.
    /// <para>
    /// A sidecar has a FILE beside the media, so its evidence can be probed with
    /// <see cref="File.Exists"/> (<see cref="GetSidecarsMissingFromDisk"/>). An embedded track has
    /// no file — its evidence is the stream list, which only a caller holding the item can read.
    /// </para>
    /// <para>
    /// F-M266 (30.09.2026): this sweep is for OBSERVATIONS, and a VERDICT is out of its reach.
    /// The comparison below is only meaningful for a row whose language the caller's stream list
    /// can name. An untagged track is invisible to that list BY CONSTRUCTION — the shared reader
    /// (<c>SidecarNaming.EmbeddedTracks</c>) drops every position whose tag does not map — so a row
    /// for such a track looked "stale" on every single run and was deleted. That destroyed two
    /// kinds of work: the uploader's own verdict (<c>rejected / und-too-small</c>, stored with
    /// Language="UN") and the detection attempt record, both of which exist precisely for the
    /// tracks that cannot be resolved. Losing the first re-opens a settled question; losing the
    /// second re-opens the media file. Neither is this method's business.
    /// </para>
    /// <para>
    /// Hence: a terminal row (uploaded/rejected) and a row carrying a detection attempt are kept
    /// unconditionally. Only plain observations are judged on their facts. The cost is that a
    /// verdict for a stream that has genuinely disappeared stays on disk; that is the cheap side of
    /// the trade, and the hash changes when a file is really rewritten, which retires the row
    /// through the identity move instead.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="currentTracks">The tracks the file has NOW, as (position, language, HI).</param>
    /// <returns>Removed rows.</returns>
    public int ForgetStaleEmbeds(string? mediaHash, IEnumerable<(int SubPos, string Lang, bool HearingImpaired)> currentTracks, IReadOnlyCollection<int>? livePositions = null)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return 0;
        }

        var current = currentTracks.ToList();
        var present = current.ToDictionary(t => t.SubPos, t => (t.Lang, t.HearingImpaired));

        // F-M258: which positions still EXIST, as opposed to which ones answered with a language.
        // A track whose tag the caller could not read is not evidence of a deletion — Jellyfin
        // caches its stream list, so a container corrected earlier in the cycle still reports no
        // language, and a row for such a track must survive. Null keeps the older behaviour and
        // infers existence from the readable tracks, which is what the callers that already know
        // the list expect.
        var live = livePositions != null
            ? new HashSet<int>(livePositions)
            : new HashSet<int>(present.Keys);

        var stored = GetEmbeds(mediaHash);
        int removed = 0;

        foreach (var row in stored)
        {
            // A verdict is not the caller's business (see the summary). This row records a
            // decision about the subtitle or the work already spent on it, and the caller's stream
            // list cannot contradict either — it cannot even SEE the positions this concerns.
            bool terminal = string.Equals(row.Status, SubtitleStatus.Uploaded, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(row.Status, SubtitleStatus.Rejected, StringComparison.OrdinalIgnoreCase);
            if (terminal || row.DetectionAttemptedAt != null)
            {
                continue;
            }

            // The position is gone: the row describes a stream the file no longer has.
            if (!live.Contains(row.SubPos))
            {
                _db.Embeds.Delete(row.Id);
                removed++;
                LogUtil.Detail(_logger, "[SubDL-DB] forgot stale embed {Media}|{Pos} {Lang} (HI={Hi}, position gone)",
                    mediaHash, row.SubPos, row.Language, row.HearingImpaired);
                continue;
            }

            // The position exists but the caller could not read its language — no verdict.
            if (!present.TryGetValue(row.SubPos, out var now))
            {
                continue;
            }

            // The position exists AND answered: only a real disagreement is evidence.
            bool keep = string.Equals(row.Language, now.Lang, StringComparison.OrdinalIgnoreCase)
                        && row.HearingImpaired == now.HearingImpaired;
            if (keep)
            {
                continue;
            }

            _db.Embeds.Delete(row.Id);
            removed++;
            LogUtil.Detail(_logger, "[SubDL-DB] forgot stale embed {Media}|{Pos} {Lang} (HI={Hi})",
                mediaHash, row.SubPos, row.Language, row.HearingImpaired);
        }

        if (removed > 0)
        {
            RefreshMediaAggregates(mediaHash);
        }

        return removed;
    }

    /// <summary>
    /// F-M234 (C): the distinct roots the sidecar paths live under. Same two-level derivation as
    /// the oshash cache: the refresh verifies each root exists AND lists cleanly before forgetting
    /// anything, so an offline mount cannot wipe verdicts.
    /// </summary>
    /// <returns>Candidate roots.</returns>
    public List<string> GetSidecarRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _db.Sidecars.FindAll())
        {
            if (string.IsNullOrEmpty(s.Path))
            {
                continue;
            }

            var parts = s.Path!.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                roots.Add("/" + string.Join('/', parts[0], parts[1]));
            }
            else if (parts.Length == 1)
            {
                roots.Add("/" + parts[0]);
            }
        }

        return roots.ToList();
    }

    /// <summary>Records when a search last ran for this file and for which languages.</summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <param name="languages">Target languages of the search.</param>
    public void MarkSearched(string? mediaHash, IReadOnlyList<string> languages)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return;
        }

        var normalized = string.Join(",", languages.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var now = DateTime.UtcNow;
        _db.EnsureMedia(mediaHash, m =>
        {
            m.LastSearchUtc = now;
            m.LastSearchLanguages = normalized;
        });
    }

    /// <summary>
    /// Patches the media record's identifying metadata (ids, season/episode, SubDL release id).
    /// <para>
    /// Kept apart from the verdict writers: this describes the FILE, while uploading describes a
    /// subtitle. The two used to be written by one call, which is why every upload wrote a media
    /// patch and a pair row and a position row for a single event.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <param name="imdbId">IMDb id of the movie, or of the SERIES for an episode.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="sdId">SubDL release id.</param>
    /// <param name="tmdbId">TMDb id of the movie, or of the SERIES for an episode.</param>
    public void PatchMediaMetadata(string? mediaHash, string? imdbId, int season, int? episode,
                                   string? sdId = null, string? tmdbId = null)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            return;
        }

        _db.EnsureMedia(mediaHash, m =>
        {
            if (!string.IsNullOrEmpty(imdbId))
            {
                m.ImdbId = imdbId;
            }

            if (!string.IsNullOrEmpty(tmdbId))
            {
                m.TmdbId = tmdbId;
            }

            if (!string.IsNullOrEmpty(sdId))
            {
                m.SdId = sdId;
            }

            if (season > 0)
            {
                m.Season = season;
                m.IsSeries = true;
            }

            if (episode.HasValue && episode.Value > 0)
            {
                m.Episode = episode;
                m.IsSeries = true;
            }
        });
    }

    // ------------------------------------------------------------- aggregates

    /// <summary>
    /// Recomputes the derived per-file aggregates from the embedded tracks: which languages are
    /// present and whether an HI variant exists. Derived values are written here rather than
    /// maintained by every caller, so they cannot drift.
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    private void RefreshMediaAggregates(string mediaHash)
    {
        var tracks = GetEmbeds(mediaHash);
        if (tracks.Count == 0)
        {
            return;
        }

        var languages = string.Join(",", tracks.Select(t => t.Language)
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase));
        bool hi = tracks.Any(t => t.HearingImpaired);

        _db.EnsureMedia(mediaHash, m =>
        {
            m.LanguagesAvailable = languages;
            m.HearingImpairedAvailable = hi;
        });
    }

    // --------------------------------------------------------------- plumbing

    /// <summary>No-op flush: LiteDB persists at the point of the write.</summary>
    public void Flush()
    {
    }

    /// <summary>Runs a write and flushes. Kept so call sites read as a unit of work.</summary>
    /// <param name="mark">The write to perform.</param>
    public void MarkAndFlush(Action mark)
    {
        mark();
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
