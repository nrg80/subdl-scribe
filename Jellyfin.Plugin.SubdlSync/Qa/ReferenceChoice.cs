// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// F-M297: which reference a correction may be anchored to.
//
// THE QUESTION THIS ANSWERS
//
// A drifting subtitle cannot be repaired from audio alone in a way that is both
// safe and exact — that was measured (33 of 36 files improved, 2 were made worse,
// and no threshold on any number read off the run separated the two — see
// docs/REQUIREMENTS.md F-M296 and the skill note). The one thing that CAN repair a
// drifting file exactly is a same-language reference subtitle: cues carrying
// identical text are the same line, so `target - reference` is that line's true
// error to the centisecond, and a jump becomes a step between two anchors instead
// of a value that has to be averaged.
//
// So the correction needs a reference. WHERE DOES ONE COME FROM?
//
//   1. A SIDECAR beside the media file, same language, not hearing-impaired. This
//      is free: the file is already on disk.
//   2. An EMBEDDED track in the container, same language, not hearing-impaired.
//      Costs one ffmpeg extraction per candidate, so it is only worth trying when
//      the sidecar route found nothing.
//   3. Nothing. Then the file is reported and left alone — the audio path is not
//      a substitute, because it cannot tell a repair from a spoilage on the file
//      it is handed.
//
// THE LANGUAGE RULE IS THE WHOLE POINT, AND IT IS NOT "SAME LANGUAGE" IN THE LOOSE SENSE
//
// Anchoring is by identical TEXT. A German reference and a German target share no
// text with an English SDH file, so a reference in the wrong language produces zero
// anchors and the correction silently does nothing. Measured on a real episode: the
// container carried a plain English track and an English SDH track whose 137 anchors
// sat at 0.00 s across the whole episode — and its 42-track sibling carries a dozen
// languages that would each have yielded nothing. Requiring the reference to carry
// the target's language is therefore not a nicety; without it the feature degrades to
// "no correction" while looking like it ran.
//
// HEARING-IMPAIRED IS DISQUALIFYING, AND THAT IS MEASURED TOO
//
// The HI variant of the same episode is the file that DRIFTS — 37 of 40 ranked files
// are HI. Using an HI track as the reference would anchor a drifting file to another
// drifting file, which is the circularity the whole exercise is trying to avoid. The
// reference must be the plain track.
//
// PURE LOGIC, SO A TEST CAN DRIVE IT
//
// This file takes plain data in and returns a decision. No Jellyfin types, no disk, no
// ffmpeg — the caller extracts, this decides. That is what makes the rule testable
// without a media library.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubdlScribe.Qa;

/// <summary>
/// F-M297: chooses the subtitle a drift correction may be anchored to.
/// </summary>
public static class ReferenceChoice
{
    /// <summary>Where the chosen reference came from.</summary>
    public enum Origin
    {
        /// <summary>No usable reference — the caller must not correct.</summary>
        None = 0,

        /// <summary>A sidecar file beside the media.</summary>
        Sidecar = 1,

        /// <summary>A plain track embedded in the container.</summary>
        Embedded = 2
    }

    /// <summary>One candidate reference.</summary>
    /// <param name="Language">Subtitle language code as the plugin stores it (SubDL form).</param>
    /// <param name="HearingImpaired">True when the track is the HI variant.</param>
    /// <param name="Path">Sidecar path, when the candidate is a file.</param>
    /// <param name="SubPos">Subtitle-relative stream position, when the candidate is embedded.</param>
    public readonly record struct Candidate(
        string? Language,
        bool HearingImpaired,
        string? Path,
        int? SubPos);

    /// <summary>The decision.</summary>
    /// <param name="Origin">Where the reference is.</param>
    /// <param name="Path">Sidecar path when <see cref="Origin"/> is Sidecar, else null.</param>
    /// <param name="SubPos">Stream position when <see cref="Origin"/> is Embedded, else null.</param>
    /// <param name="Reason">Human-readable outcome, for the log.</param>
    public readonly record struct Decision(
        Origin Origin,
        string? Path,
        int? SubPos,
        string Reason);

    /// <summary>
    /// Picks the reference for a target subtitle.
    /// </summary>
    /// <param name="candidates">Every sidecar and embedded track the item has, in any order.</param>
    /// <param name="targetLanguage">Language of the subtitle to be corrected.</param>
    /// <param name="selfPath">
    /// Path of the subtitle being corrected, so it is never chosen as its own reference.
    /// Null when the target is not yet on disk.
    /// </param>
    /// <returns>
    /// The decision. Sidecars are preferred over embedded tracks: no extraction is
    /// needed, and a sidecar the user placed is a better witness than a container track.
    /// </returns>
    public static Decision Choose(
        IEnumerable<Candidate>? candidates,
        string? targetLanguage,
        string? selfPath = null)
    {
        var all = (candidates ?? Enumerable.Empty<Candidate>())
            .Where(c => c.Language != null)
            .ToList();

        if (all.Count == 0)
        {
            return new Decision(Origin.None, null, null, "no subtitle tracks at all");
        }

        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return new Decision(Origin.None, null, null, "target language unknown — a reference in the wrong language yields no anchors");
        }

        // Same language, and NOT the hearing-impaired variant: the HI file is the one
        // that drifts, so anchoring to it would anchor a drifting file to another.
        var usable = all
            .Where(c => string.Equals(c.Language, targetLanguage, StringComparison.OrdinalIgnoreCase))
            .Where(c => !c.HearingImpaired)
            .Where(c => c.Path == null || selfPath == null
                        || !string.Equals(c.Path, selfPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (usable.Count == 0)
        {
            bool sameLang = all.Any(c => string.Equals(c.Language, targetLanguage, StringComparison.OrdinalIgnoreCase));
            string why = sameLang
                ? "only a hearing-impaired track in the target language — the drifting variant cannot anchor itself"
                : $"no {targetLanguage} track to anchor to";
            return new Decision(Origin.None, null, null, why);
        }

        // A sidecar is preferred: no extraction needed, and a file the user placed is a
        // better witness than a container track.
        //
        // NOT FirstOrDefault on the struct — `default(Candidate)` has Path == null, and
        // `Candidate? x = list.FirstOrDefault() is { } sc` matches ALWAYS because the
        // nullable wrapper has HasValue set. The test caught exactly that: an embedded-only
        // candidate list came back as "Sidecar" with an empty path.
        foreach (Candidate c in usable)
        {
            if (c.Path != null)
            {
                return new Decision(Origin.Sidecar, c.Path, null, $"sidecar ({targetLanguage})");
            }
        }

        Candidate embedded = usable[0];
        return new Decision(Origin.Embedded, null, embedded.SubPos, $"embedded track {embedded.SubPos} ({targetLanguage})");
    }
}
