// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Jellyfin.Plugin.SubdlScribe.Language;
using Jellyfin.Plugin.SubdlScribe.Registry;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// Result of one language-resolution pass over a container.
/// </summary>
/// <param name="Tracks">Every text track with its resolved language, ready for the registry.</param>
/// <param name="Wanted">Subtitle position to ISO 639-2 code, for the tracks that were detected.</param>
/// <param name="UntaggedSeen">How many text tracks arrived without a usable language.</param>
/// <param name="Written">How many tags were actually written into the container.</param>
public sealed record LanguageResolution(
    List<(int SubPos, string Lang, bool HearingImpaired)> Tracks,
    Dictionary<int, string> Wanted,
    int UntaggedSeen,
    int Written)
{
    /// <summary>
    /// Hash the file had BEFORE the tag write, null when nothing was written.
    /// <para>
    /// Returned rather than consumed here because the caller owns the registry: moving the file's
    /// rows from this hash to <see cref="NewHash"/> is a database operation, and the gate stays a
    /// file-level component. The pair is the contract — <see cref="NewHash"/> without
    /// <see cref="OldHash"/> must never be used to move anything.
    /// </para>
    /// </summary>
    public string? OldHash { get; init; }

    /// <summary>Hash the file has AFTER the tag write, null when nothing was written.</summary>
    public string? NewHash { get; init; }
}

/// <summary>
/// F-M261: the download quality gate that resolves untagged and 'und' subtitle tracks and —
/// when enabled — writes the found language back into the container.
/// <para>
/// Both halves answer the SAME gap, which is why they are one gate rather than two features.
/// A track whose tag is missing or <c>und</c> cannot be mapped: <see cref="LanguageMapper"/>
/// has no entry for <c>und</c>, so the two-letter fallback turns it into the invented language
/// <c>UN</c>, and a null tag maps to nothing at all and gets no registry row. Either way the
/// area reads "language absent" for a track that is sitting right there, and the item is queued
/// and searched for a language it already carries.
/// </para>
/// <para>
/// Measured on <c>Slow.Horses.S06E03</c> (30.09.2026): 44 text tracks, every one with
/// <c>lang=None</c>. Extraction showed real content — English 48 KB, an SDH variant, Arabic,
/// Portuguese, Bulgarian — so the file was fully subtitled while the registry read zero
/// languages as present.
/// </para>
/// </summary>
public sealed class LanguageTagGate
{
    private readonly ILogger _logger;
    private readonly PluginConfiguration _config;
    private readonly ContentHashRegistry? _registry;

    /// <summary>Initializes a new instance of the <see cref="LanguageTagGate"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="registry">Registry used to read the file's hash before and after a write. Null disables the hash pair.</param>
    public LanguageTagGate(ILogger logger, PluginConfiguration config, ContentHashRegistry? registry = null)
    {
        _logger = logger;
        _config = config;
        _registry = registry;
    }

    /// <summary>
    /// True when the operator enabled the resolution half.
    /// </summary>
    public bool ResolveEnabled => _config.AllocateMissingLanguageCodes;

    /// <summary>
    /// True when the operator enabled writing the tag back into the container.
    /// </summary>
    public bool WriteEnabled => _config.AllocateMissingLanguageCodes;

    /// <summary>
    /// True when a dry run is armed for EITHER direction, and the write half must therefore stand
    /// down (F-M22).
    /// </summary>
    /// <remarks>
    /// F-M263 (user decision 30.09.2026): both dry-run switches suppress the container rewrite, not
    /// just the one for their own direction. The reason is that this pass is not part of either
    /// pipeline's search work — it edits media FILES, and it runs from the seeder, which belongs to
    /// no direction at all: the seeder scans before both pipelines and is reached by every trigger.
    /// Tying the write to one switch would leave the other dry run rewriting the user's library,
    /// which is exactly what F-M22 forbids. Detection still runs in a dry run — the run's value is
    /// reporting what it WOULD do — so this gates the WRITE only, never the resolution.
    /// </remarks>
    public bool DryRunActive => _config.DryRun || _config.DownloadDryRun;

    /// <summary>
    /// True when a usable language tag exists — i.e. the track needs no resolution.
    /// </summary>
    /// <remarks>
    /// The definition of "untagged" lives here and nowhere else. It matches what the upload
    /// pipeline has treated as undecided since F-M74: a NULL/empty tag means <c>und</c>, and
    /// so does the literal <c>und</c>/<c>undefined</c>. Keeping one copy is deliberate —
    /// the four name parsers and the three HI readers that existed in parallel all drifted.
    /// </remarks>
    /// <param name="language">Raw tag from the stream.</param>
    /// <returns>True when the tag is missing or explicitly undefined.</returns>
    public static bool IsUntagged(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return true;
        }

        var t = language.Trim();
        return t.Equals("und", StringComparison.OrdinalIgnoreCase)
            || t.Equals("undefined", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the language of every text track of one container, optionally writing the
    /// found languages back into the file.
    /// </summary>
    /// <param name="mediaPath">Container to read, and to edit when the write half is on.</param>
    /// <param name="streams">All subtitle streams of the item, UNFILTERED (as Jellyfin returns them).</param>
    /// <param name="ffmpegPath">Resolved ffmpeg binary.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolution outcome. Never throws for a per-file failure.</returns>
    /// <remarks>
    /// F-M264 (user order 30.09.2026, "bitte nur einmal"): a file whose tags this install already
    /// wrote is not written a second time. Measured on prod 30.09.2026 — Paris.Has.Fallen.S02E04
    /// was rewritten twice inside one cycle, 8 s apart: the DOWNLOAD-seed pass wrote the codes,
    /// and the UPLOAD-seed pass that follows in the same cycle found the SAME tracks untagged and
    /// wrote them again. The second pass was not reading a stale value of its own: it asked
    /// Jellyfin, and Jellyfin caches its stream list — the container was corrected at 13:30:59,
    /// Jellyfin noticed the change only at 13:32:07 ("File changed, pruning extracted data"), after
    /// the cycle had ended. Every rewrite is a full remux of the media file (ffmpeg cannot edit a
    /// container in place), so the duplicate cost a second 540 MB copy, a second identity change
    /// and a second round of registry bookkeeping for five tags that were already in the file.
    /// <para>
    /// The skip is per TRACK and keyed on the tag the container was just verified to carry, which
    /// is the one fact the cached stream list cannot contradict. An untagged track stays work in
    /// any later run, so this cannot mask a genuine failure to write: the temp file is verified and
    /// only then moved over the original, so a track seen as tagged here is tagged on disk.
    /// </para>
    /// </remarks>
    public async Task<LanguageResolution> RunAsync(
        string mediaPath,
        IEnumerable<MediaStream>? streams,
        string ffmpegPath,
        CancellationToken ct)
    {
        var tracks = new List<(int SubPos, string Lang, bool HearingImpaired)>();
        var wanted = new Dictionary<int, string>();
        int untagged = 0;
        int written = 0;

        var all = new List<MediaStream>();
        if (streams != null)
        {
            foreach (var s in streams)
            {
                // ONLY subtitle streams, before any position is computed. The incoming list is the
                // item's WHOLE stream list (video, audio, subtitles); ffmpeg's `0:s:N` counts
                // SUBTITLE streams only, so an index taken from the full list is off by the number
                // of video and audio tracks in front of it. Measured on Slow.Horses.S06E03: the
                // file has 46 streams — 1 video, 1 audio, then the subtitles — so the first subtitle
                // is global index 2 but `0:s:0`. Passing the full list through would have resolved
                // every track against the language of a DIFFERENT track, silently, and written those
                // wrong codes into the file.
                if (s.Type == MediaStreamType.Subtitle && !s.IsExternal)
                {
                    all.Add(s);
                }
            }
        }

        // Positions are taken against the UNFILTERED subtitle list: ffmpeg's 0:s:N counts
        // bitmap and forced tracks as well, so a position from a filtered list names a
        // different stream (the invariant F-M246/F-M257 already document).
        var pending = new List<(int Pos, MediaStream Stream)>();
        for (int pos = 0; pos < all.Count; pos++)
        {
            var s = all[pos];
            if (s.Type != MediaStreamType.Subtitle || s.IsExternal)
            {
                continue;
            }

            if (SidecarNaming.IsForcedStream(s) || !s.IsTextSubtitleStream)
            {
                continue;
            }

            if (IsUntagged(s.Language))
            {
                untagged++;
                if (ResolveEnabled)
                {
                    pending.Add((pos, s));
                }

                continue; // resolvable below, or left without a row so the search still runs
            }

            string? mapped = LanguageMapper.MapToSubdl(s.Language);
            if (mapped != null)
            {
                tracks.Add((pos, mapped, SidecarNaming.IsHearingImpairedStream(s)));
            }
        }

        if (pending.Count == 0)
        {
            return new LanguageResolution(tracks, wanted, untagged, written);
        }

        // The per-track attempt record, consulted BEFORE the container is touched at all. A
        // position whose detection already ran is dropped here, and when every pending position
        // was already tried the file is not opened even for the metadata probe below (F-M266).
        // The expensive half is extraction plus classification, but skipping the probe too is what
        // makes "already tried" cost nothing on a file that keeps refusing to resolve.
        //
        // The seeder's own pre-check cannot catch this case: it only asks whether an untagged track
        // EXISTS, and a track the detector cannot classify answers that question identically on
        // every scan — so the file was re-opened on each one.
        string? gateHash = _registry?.GetMediaHash(mediaPath);
        var alreadyAttempted = _registry?.DetectedPositions(gateHash) ?? new HashSet<int>();
        if (alreadyAttempted.Count > 0)
        {
            int skipped = 0;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var (pos, stream) = pending[i];
                if (!alreadyAttempted.Contains(pos))
                {
                    continue;
                }

                pending.RemoveAt(i);
                skipped++;

                // A resolved position carries its language on the row, so the registry and the
                // queue decision still see what this file really has. An unresolved one contributes
                // no track and stays counted as untagged — the language is unknown, and saying
                // otherwise would claim coverage the container does not have.
                var row = _registry?.GetEmbed(gateHash, pos);
                if (row != null && !string.IsNullOrEmpty(row.Language))
                {
                    tracks.Add((pos, row.Language, SidecarNaming.IsHearingImpairedStream(stream)));
                    untagged--;
                }
            }

            if (skipped > 0)
            {
                // Normal, not PerItem: "this file was NOT opened again" is the whole point of the
                // record, and the sibling F-M264 line is at Normal for the same reason. At PerItem
                // the operator could never tell a skipped file from an untouched one.
                LogUtil.Normal(_logger,
                    "[SubDL-D] {File} — {Count} track(s) already ran through language detection; container not re-opened (F-M266).",
                    System.IO.Path.GetFileName(mediaPath), skipped);
            }
        }

        if (pending.Count == 0)
        {
            return new LanguageResolution(tracks, wanted, untagged, written);
        }

        // F-M264 (30.09.2026): the stream list above is Jellyfin's CACHED snapshot, and it does not
        // know about a tag this install wrote minutes ago. Ask the CONTAINER instead — one ffprobe
        // call, no remux — and drop every position that already carries a usable tag. This is what
        // makes the gate run once per file instead of once per seed: the download-seed pass writes
        // the codes, the upload-seed pass in the same cycle sees them here and writes nothing.
        var onDisk = await FfmpegTools.ReadSubtitleTagsAsync(ffmpegPath, mediaPath, _logger, ct).ConfigureAwait(false);
        if (onDisk != null && onDisk.Count > 0)
        {
            int alreadyTagged = 0;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var (pos, stream) = pending[i];
                if (!onDisk.TryGetValue(pos, out string? tag) || string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                string? real = LanguageMapper.MapToSubdl(tag);
                if (real == null)
                {
                    continue;
                }

                // The container carries a real language at this position: nothing to resolve and
                // nothing to write. Record it like any other tagged track so the registry — and
                // with it the queue decision — sees the language the file actually has.
                tracks.Add((pos, real, SidecarNaming.IsHearingImpairedStream(stream)));
                pending.RemoveAt(i);
                untagged--;
                alreadyTagged++;
            }

            if (alreadyTagged > 0)
            {
                LogUtil.Normal(_logger,
                    "[SubDL-D] {File} — {Count} track(s) already carry a language tag in the container; nothing to write (F-M264).",
                    System.IO.Path.GetFileName(mediaPath), alreadyTagged);
            }
        }

        if (pending.Count == 0)
        {
            return new LanguageResolution(tracks, wanted, untagged, written);
        }
        // One ffmpeg pass for every unresolved stream of this file (F-M5): ffmpeg re-reads the
        // whole container per call, and this file has 44 of them.
        var positions = new List<int>();
        foreach (var (pos, _) in pending)
        {
            positions.Add(pos);
        }

        var (texts, exit) = await FfmpegTools.ExtractAllAsync(ffmpegPath, mediaPath, positions, _logger, _config, ct)
            .ConfigureAwait(false);

        // A non-zero exit means the pass went wrong as a whole. Individual texts may still be
        // usable, but a null is then a suspicion rather than a verdict, so nothing is claimed.
        if (exit != 0 && texts.Values.All(v => v == null))
        {
            LogUtil.PerItem(_config.LogMode, _logger,
                "[SubDL-D] language resolution of {File} skipped: extraction exited {Exit}",
                System.IO.Path.GetFileName(mediaPath), exit);
            return new LanguageResolution(tracks, wanted, untagged, written);
        }

        foreach (var (pos, stream) in pending)
        {
            ct.ThrowIfCancellationRequested();
            texts.TryGetValue(pos, out string? text);
            if (string.IsNullOrWhiteSpace(text))
            {
                // No text — nothing to detect. The attempt is recorded with its reason so the next
                // scan does not open the container for this position again.
                _registry?.MarkDetectionAttempt(gateHash, pos, Data.DetectionOutcome.NoText);
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[SubDL-D] {File} s{Pos} — untagged track carries no text, left unresolved",
                    System.IO.Path.GetFileName(mediaPath), pos);
                continue;
            }

            // The 2 KB floor runs BEFORE detection, in the same order the uploader has used
            // since F-M74: a tiny track yields a garbage verdict, and a wrong language is
            // worse than none — it would claim coverage the file does not have.
            if (text.Length < 2048)
            {
                _registry?.MarkDetectionAttempt(gateHash, pos, Data.DetectionOutcome.TooSmall);
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[SubDL-D] {File} s{Pos} — untagged track too small to detect a language ({Bytes} bytes), left unresolved",
                    System.IO.Path.GetFileName(mediaPath), pos, text.Length);
                continue;
            }

            string? detected = Qa.QaGates.DetectLanguage(text);
            if (detected == null)
            {
                _registry?.MarkDetectionAttempt(gateHash, pos, Data.DetectionOutcome.Failed);
                LogUtil.PerItem(_config.LogMode, _logger,
                    "[SubDL-D] {File} s{Pos} — language could not be detected ({Bytes} bytes), left unresolved",
                    System.IO.Path.GetFileName(mediaPath), pos, text.Length);
                continue;
            }

            bool hi = SidecarNaming.IsHearingImpairedStream(stream);
            tracks.Add((pos, detected, hi));
            wanted[pos] = detected;
            _registry?.MarkDetectionAttempt(gateHash, pos, Data.DetectionOutcome.Resolved, detected, hi);
            LogUtil.PerItem(_config.LogMode, _logger,
                "[SubDL-D] {File} s{Pos} — untagged track resolved to {Lang} ({Bytes} bytes)",
                System.IO.Path.GetFileName(mediaPath), pos, detected, text.Length);
        }

        if (wanted.Count == 0 || !WriteEnabled || DryRunActive)
        {
            return new LanguageResolution(tracks, wanted, untagged, written);
        }

        // Write the tags back into the container. Callers run this BEFORE the media hash:
        // the hash covers the first and last 64 KB and a Matroska segment header carries its
        // own size, so a tag write changes the identity of the file (F-M261).
        var iso639 = new Dictionary<int, string>();
        foreach (var kv in wanted)
        {
            iso639[kv.Key] = LanguageMapper.ToIso6392(kv.Value);
        }

        // The hash pair. Read BEFORE the write, read again AFTER it, and hand both back: the
        // caller moves the file's registry rows from the old key to the new one. Without the pair
        // the rewrite would leave the file with two identities — the rows under the old hash stay,
        // because nothing prunes a media row whose Jellyfin item is still there (F-M261).
        string? oldHash = _registry?.GetMediaHash(mediaPath);

        written = await FfmpegTools.WriteLanguageTagsAsync(ffmpegPath, mediaPath, iso639, _logger, _config, ct)
            .ConfigureAwait(false) ? iso639.Count : 0;

        string? newHash = written > 0 ? _registry?.GetMediaHash(mediaPath) : null;

        // A write that produced no new hash is not a rewrite: the file was left as it was, so there
        // is nothing to move and the caller must not be handed half a pair.
        if (oldHash != null && newHash != null && string.Equals(oldHash, newHash, StringComparison.OrdinalIgnoreCase))
        {
            newHash = null;
            oldHash = null;
        }

        // F-M262: one line per FILE at Normal, not only at Verbose. This pass edits the user's media
        // files, and until now a successful write left NO trace at Normal — every line of this gate
        // is a PerItem line, and the write itself logged nothing on success, so the only way to learn
        // that a container had been rewritten was to raise the plugin to Verbose and read the
        // per-track detail. The summary line therefore carries the three facts a Normal reader needs
        // — which file, how many tracks, WHICH languages — and the per-track positions stay at
        // Verbose where they belong.
        if (written > 0)
        {
            var writtenLangs = new List<string>();
            foreach (var kv in wanted.OrderBy(kv => kv.Key))
            {
                string code = iso639.TryGetValue(kv.Key, out string? iso) && !string.IsNullOrWhiteSpace(iso)
                    ? iso
                    : kv.Value;
                if (!string.IsNullOrWhiteSpace(code) && !writtenLangs.Contains(code, StringComparer.OrdinalIgnoreCase))
                {
                    writtenLangs.Add(code);
                }
            }

            string identity = oldHash != null && newHash != null ? $"  {oldHash} -> {newHash}" : string.Empty;
            LogUtil.Normal(_logger,
                "[SubDL-D] {File} — {Count} language code(s) written into the container: {Langs}{Identity}",
                System.IO.Path.GetFileName(mediaPath), iso639.Count, string.Join(", ", writtenLangs), identity);
        }

        return new LanguageResolution(tracks, wanted, untagged, written)
        {
            OldHash = newHash == null ? null : oldHash,
            NewHash = newHash
        };
    }
}
