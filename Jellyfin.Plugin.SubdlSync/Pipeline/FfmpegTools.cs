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
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// The one place that talks to ffmpeg (F-M261).
/// <para>
/// The binary lookup, the one-pass extraction and the language-tag write lived apart
/// before this class existed: extraction sat in <c>UploadPipeline</c> only, so the download
/// side had no way to read a container — which is exactly why untagged tracks could never be
/// resolved there (F-M261). Two copies of the lookup would drift the way the four name
/// parsers and the three HI readers did, so there is one copy here.
/// </para>
/// <para>
/// Deliberately ffmpeg and NOT <c>mkvpropedit</c>: the latter is faster (measured 0.05 s
/// against 2.4 s on a 550 MB episode) but it is a native, architecture-bound binary
/// (arm64 here, X64 on the prod Docker container) and it is not present on either machine.
/// ffmpeg is already required for extraction, ships for every platform and costs one remux
/// only for the files that actually carry an untagged track.
/// </para>
/// </summary>
public static class FfmpegTools
{
    private static readonly object PathLock = new();
    private static string? _cachedPath;

    /// <summary>Sentinel that no real configuration value can equal (F-M261).</summary>
    private static string _cachedConfig = "\x11NITIAL\x11";

    /// <summary>
    /// Resolves the ffmpeg binary once per configuration value, then caches it.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>Path to ffmpeg, or "ffmpeg" to let the OS report the failure.</returns>
    public static string ResolvePath(PluginConfiguration config, ILogger logger)
    {
        string configKey = config.FfmpegPath ?? string.Empty;

        lock (PathLock)
        {
            if (_cachedPath != null && _cachedConfig == configKey)
            {
                return _cachedPath;
            }

            string resolved = Resolve(config, logger);
            _cachedPath = resolved;
            _cachedConfig = configKey;
            return resolved;
        }
    }

    private static string Resolve(PluginConfiguration config, ILogger logger)
    {
        // 1) explicit user override
        if (!string.IsNullOrWhiteSpace(config.FfmpegPath))
        {
            var configured = config.FfmpegPath.Trim();
            if (File.Exists(configured))
            {
                LogUtil.Detail(config.LogMode, logger, "[SubDL] ffmpeg path from config override: {Path}", configured);
                return configured;
            }

            logger.LogWarning("[SubDL] configured ffmpeg override not found: {Path}, falling back to auto-detection", configured);
        }

        // 2) PATH lookup via "which ffmpeg" / "where ffmpeg"
        var pathHit = FindInPath("ffmpeg");
        if (!string.IsNullOrWhiteSpace(pathHit))
        {
            LogUtil.Detail(config.LogMode, logger, "[SubDL] ffmpeg path from PATH: {Path}", pathHit);
            return pathHit;
        }

        // 3) common static locations (Linux/Docker first, then macOS, then Windows).
        //    /usr/lib/jellyfin-ffmpeg/ffmpeg is the path the official Jellyfin images carry,
        //    so a container without ffmpeg on PATH still resolves.
        var staticCandidates = new[]
        {
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/share/jellyfin/ffmpeg/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
            "/opt/ffmpeg/ffmpeg",
            "/opt/jellyfin-ffmpeg/ffmpeg",
            "/config/ffmpeg",
            "/Applications/Jellyfin.app/Contents/MacOS/ffmpeg",
            "C:\\ProgramData\\Jellyfin\\Server\\ffmpeg.exe",
            "C:\\Program Files\\Jellyfin\\Server\\ffmpeg.exe"
        };

        foreach (var candidate in staticCandidates)
        {
            if (File.Exists(candidate))
            {
                LogUtil.Detail(config.LogMode, logger, "[SubDL] ffmpeg path from static fallback: {Path}", candidate);
                return candidate;
            }
        }

        // 4) last resort: keep "ffmpeg" and let the OS report the failure in the call log
        logger.LogWarning("[SubDL] ffmpeg not found anywhere; extraction will fail until ffmpeg is installed or FfmpegPath is configured");
        return "ffmpeg";
    }

    /// <summary>Locate an executable in PATH. Uses "where" on Windows, "which" elsewhere.</summary>
    /// <param name="name">Executable name.</param>
    /// <returns>Full path, or null.</returns>
    public static string? FindInPath(string name)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "where" : "which",
                Arguments = isWindows ? $"\"{name}\"" : name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                return null;
            }

            foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed) && File.Exists(trimmed))
                {
                    return trimmed;
                }
            }
        }
        catch
        {
            // ignore — the caller falls back to the static list
        }

        return null;
    }

    /// <summary>
    /// F-M5: extract EVERY given subtitle stream of one file in a SINGLE ffmpeg call.
    /// </summary>
    /// <remarks>
    /// Why one call and not one per stream: ffmpeg must open and (over CIFS/NAS)
    /// re-read the container for every invocation. On a 1 GB episode with 83
    /// subtitle streams that is 83 reads of the same gigabyte — measured on prod
    /// 28.09.2026 as the only multi-minute stalls in an otherwise 11 s-median run.
    /// One call with repeated <c>-map 0:s:N</c> + <c>-f srt</c> output pairs reads the
    /// file once and writes one file per stream, so the container is read a single
    /// time however many streams it carries.
    /// <para>
    /// The mapping uses the SUBTITLE-relative index (<c>0:s:N</c>), never the container
    /// index: mixing the two lands on video/audio streams (exit 8 / no-stream errors).
    /// Callers pass positions computed against the unfiltered subtitle list.
    /// </para>
    /// </remarks>
    /// <param name="ffmpegPath">Resolved ffmpeg binary.</param>
    /// <param name="mediaPath">Container to read.</param>
    /// <param name="subIndices">Subtitle-relative stream positions to extract.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="config">Configuration, for the log mode.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The extracted texts plus ffmpeg's exit code. A stream that produced no
    /// text maps to null. The exit code travels with the result because it is
    /// the difference between "this stream has no text" (0) and "the pass went
    /// wrong, retry it per stream" (non-zero) — the caller needs that to decide
    /// whether a null is a verdict or a suspicion.
    /// </returns>
    public static async Task<(Dictionary<int, string?> Texts, int ExitCode)> ExtractAllAsync(
        string ffmpegPath,
        string mediaPath,
        IReadOnlyList<int> subIndices,
        ILogger logger,
        PluginConfiguration config,
        CancellationToken ct)
    {
        var result = new Dictionary<int, string?>();
        if (subIndices.Count == 0)
        {
            return (result, 0);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "subdl-scribe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(mediaPath);

            var outputs = new List<(int Index, string Path)>();
            foreach (int idx in subIndices)
            {
                string outFile = Path.Combine(tempDir, $"stream{idx}.srt");
                psi.ArgumentList.Add("-map");
                // NO trailing '?'. It looks like the safe choice — "if this index does not
                // exist, skip it" — but measured 28.09.2026 it is the opposite:
                // `-map 0:s:9?` on a file with 3 streams writes the FIRST subtitle stream
                // (the English one) into the output slot meant for index 9, exits 0 and
                // says nothing. That is a subtitle of the wrong language, silently
                // duplicated, exactly what F-M71/F-M85 exist to prevent. Without '?', an
                // unknown index fails the whole call (exit 234, no output). That is the
                // honest outcome: the caller sees a non-zero exit, retries the empty
                // streams one by one and records a real failure instead of uploading a
                // mislabelled file.
                psi.ArgumentList.Add($"0:s:{idx}");
                psi.ArgumentList.Add("-c:s");
                psi.ArgumentList.Add("srt");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("srt");
                psi.ArgumentList.Add(outFile);
                outputs.Add((idx, outFile));
                result[idx] = null;
            }

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                logger.LogError("[SubDL] ffmpeg failed to start for {File}", Path.GetFileName(mediaPath));
                return (result, -1);
            }

            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            // ffmpeg keeps going after a bad stream with -y where it can, so a non-zero
            // exit does NOT mean every output is unusable. Read whatever landed and let
            // the exit code tell the caller whether the nulls are verdicts or suspicions.
            if (proc.ExitCode != 0)
            {
                LogUtil.PerItem(config.LogMode, logger,
                    "[SubDL] one-pass extraction of {File} exited {Exit}: {Err}",
                    Path.GetFileName(mediaPath), proc.ExitCode,
                    stderr.Length <= 300 ? stderr : stderr[..300]);
            }

            foreach (var (idx, outFile) in outputs)
            {
                try
                {
                    if (File.Exists(outFile))
                    {
                        string text = await File.ReadAllTextAsync(outFile, ct).ConfigureAwait(false);
                        result[idx] = string.IsNullOrWhiteSpace(text) ? null : text;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("[SubDL] reading extracted stream {Idx} failed for {File}: {Msg}",
                        idx, Path.GetFileName(mediaPath), ex.Message);
                }
            }

            return (result, proc.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("[SubDL] one-pass extraction error for {File}: {Msg}",
                Path.GetFileName(mediaPath), ex.Message);
            foreach (int idx in subIndices)
            {
                result.TryAdd(idx, null);
            }

            return (result, -1);
        }
        finally
        {
            DeleteDirectoryBestEffort(tempDir);
        }
    }

    /// <summary>
    /// F-M261: writes a corrected language tag into the container for each given stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The remux copies every stream (<c>-c copy</c>), so no audio or video is re-encoded and
    /// the call costs one read of the file. Measured on the 550 MB
    /// <c>Slow.Horses.S06E03</c>: 2.4 s, byte-identical stream data.
    /// </para>
    /// <para>
    /// WHY THIS MUST RUN BEFORE THE MEDIA HASH: the plugin's OSHash is size plus the first and
    /// last 64 KB of the file, and a Matroska segment header carries its own size — so writing
    /// a tag changes the hash. Measured on that episode: 550277727 bytes / <c>32d41dc3a0359d27</c>
    /// before, 550277733 bytes / <c>09eb61d402fba20b</c> after. Changing the file after the hash
    /// was taken leaves every registry row hanging under an identity the file no longer has.
    /// </para>
    /// <para>
    /// The write is atomic from the caller's point of view: ffmpeg writes a sibling temp file,
    /// the result is verified to carry the wanted tag, and only then is the original replaced.
    /// A failure at any step leaves the original untouched and returns false.
    /// </para>
    /// </remarks>
    /// <param name="ffmpegPath">Resolved ffmpeg binary.</param>
    /// <param name="mediaPath">Container to edit, replaced in place.</param>
    /// <param name="iso639ByPosition">Subtitle position to ISO 639-2 code.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="config">Configuration, for the log mode.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the container now carries every wanted tag.</returns>
    public static async Task<bool> WriteLanguageTagsAsync(
        string ffmpegPath,
        string mediaPath,
        IReadOnlyDictionary<int, string> iso639ByPosition,
        ILogger logger,
        PluginConfiguration config,
        CancellationToken ct)
    {
        if (iso639ByPosition.Count == 0)
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return false;
        }

        string fileName = Path.GetFileName(mediaPath);
        string tempPath = Path.Combine(dir, "." + fileName + ".subdl-tag-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(mediaPath));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(mediaPath);
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            foreach (var kv in iso639ByPosition)
            {
                psi.ArgumentList.Add("-metadata:s:s:" + kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("language=" + kv.Value);
            }

            psi.ArgumentList.Add(tempPath);

            using (var proc = Process.Start(psi))
            {
                if (proc == null)
                {
                    logger.LogError("[SubDL] ffmpeg failed to start for tag write on {File}", fileName);
                    return false;
                }

                string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                if (proc.ExitCode != 0 || !File.Exists(tempPath))
                {
                    LogUtil.PerItem(config.LogMode, logger,
                        "[SubDL-D] tag write failed {File}: exit={Exit} {Err}",
                        fileName, proc.ExitCode, stderr.Length <= 300 ? stderr : stderr[..300]);
                    return false;
                }
            }

            // Verify the temp file really carries the tags BEFORE the original is touched.
            // A silent ffmpeg success on an unset metadata key would otherwise replace a good
            // file with one that is no better than before — and cost a new hash for nothing.
            var verified = await ReadSubtitleTagsAsync(ffmpegPath, tempPath, logger, ct).ConfigureAwait(false);
            if (verified == null)
            {
                LogUtil.PerItem(config.LogMode, logger, "[SubDL-D] tag write not verifiable, keeping original {File}", fileName);
                DeleteFileBestEffort(tempPath);
                return false;
            }

            foreach (var kv in iso639ByPosition)
            {
                if (!verified.TryGetValue(kv.Key, out string? got) || !LanguageTagMatches(got, kv.Value))
                {
                    LogUtil.PerItem(config.LogMode, logger,
                        "[SubDL-D] tag write did not take on {File} s{Pos} (wanted {Want}, got {Got}) — original kept",
                        fileName, kv.Key, kv.Value, got ?? "none");
                    DeleteFileBestEffort(tempPath);
                    return false;
                }
            }

            // Replace the original. File.Move(overwrite) is atomic on the same filesystem,
            // and the temp file is written next to the media precisely so it is.
            File.Move(tempPath, mediaPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            DeleteFileBestEffort(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("[SubDL-D] tag write error for {File}: {Msg}", fileName, ex.Message);
            DeleteFileBestEffort(tempPath);
            return false;
        }
    }

    /// <summary>
    /// Reads the container's per-subtitle language tags through ffprobe.
    /// </summary>
    /// <remarks>
    /// The returned keys are SUBTITLE-RELATIVE positions, not container indices, because that is
    /// what <c>-metadata:s:s:N</c> writes and what callers hold. ffprobe reports the global
    /// <c>index</c>, so the subtitle streams are sorted by it and renumbered — comparing the two
    /// numberings directly would verify every position against the wrong stream and reject a
    /// write that actually succeeded.
    /// </remarks>
    /// <param name="ffmpegPath">Resolved ffmpeg binary; ffprobe is looked up next to it.</param>
    /// <param name="mediaPath">Container to read.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Subtitle position to tag, or null when the list could not be read.</returns>
    public static async Task<Dictionary<int, string>?> ReadSubtitleTagsAsync(
        string ffmpegPath,
        string mediaPath,
        ILogger logger,
        CancellationToken ct)
    {
        string probe = ffmpegPath;
        int cut = ffmpegPath.LastIndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase);
        if (cut >= 0)
        {
            probe = ffmpegPath[..cut] + "ffprobe" + ffmpegPath[(cut + "ffmpeg".Length)..];
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = probe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add("s");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=index:stream_tags=language");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=noprint_wrappers=1");
            psi.ArgumentList.Add(mediaPath);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                return null;
            }

            // Collect (global index, tag) pairs, then renumber by order of the global index.
            var raw = new List<(int ContainerIndex, string? Tag)>();
            int current = -1;
            string? currentTag = null;
            foreach (var lineRaw in stdout.Split('\n'))
            {
                var line = lineRaw.Trim();
                if (line.StartsWith("index=", StringComparison.Ordinal))
                {
                    if (current >= 0)
                    {
                        raw.Add((current, currentTag));
                    }

                    current = int.TryParse(line[6..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : -1;
                    currentTag = null;
                }
                else if (line.StartsWith("TAG:language=", StringComparison.Ordinal) && current >= 0)
                {
                    currentTag = line[13..].Trim();
                }
            }

            if (current >= 0)
            {
                raw.Add((current, currentTag));
            }

            raw.Sort((a, b) => a.ContainerIndex.CompareTo(b.ContainerIndex));
            var tags = new Dictionary<int, string>();
            for (int pos = 0; pos < raw.Count; pos++)
            {
                if (!string.IsNullOrEmpty(raw[pos].Tag))
                {
                    tags[pos] = raw[pos].Tag!;
                }
            }

            return tags;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug("[SubDL] reading subtitle tags of {File} failed: {Msg}", Path.GetFileName(mediaPath), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// True when a container tag matches the wanted ISO 639-2 code.
    /// </summary>
    /// <remarks>
    /// Matroska tags are free text, so a file may carry <c>eng</c>, <c>en</c> or <c>English</c>.
    /// The comparison accepts the two- and three-letter spelling of the same language rather
    /// than demanding byte equality, so a tag ffmpeg normalised is not mistaken for a failed
    /// write — which would throw away a good remux and redo it on every run.
    /// </remarks>
    /// <param name="actual">Tag found in the container.</param>
    /// <param name="wanted">Wanted ISO 639-2 code.</param>
    /// <returns>True when both name the same language.</returns>
    public static bool LanguageTagMatches(string? actual, string wanted)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(wanted))
        {
            return false;
        }

        string a = actual.Trim();
        string w = wanted.Trim();
        if (string.Equals(a, w, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "eng" vs "en", "deu"/"ger" vs "de" — compare the first two letters when both are
        // plain alphabetic codes, but never for a long name ("English" starts with "En" too,
        // so names are compared by prefix instead of by truncation).
        bool aShort = a.Length is 2 or 3 && IsAlpha(a);
        bool wShort = w.Length is 2 or 3 && IsAlpha(w);
        if (aShort && wShort)
        {
            return string.Equals(a[..2], w[..2], StringComparison.OrdinalIgnoreCase);
        }

        return false;

        static bool IsAlpha(string s)
        {
            foreach (char c in s)
            {
                if (!char.IsLetter(c))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort — a leftover temp file is not worth failing the run over
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        // F-M7: always clean temp files (success AND failure)
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // cleanup best effort
        }
    }
}
