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
//
// F-M295: cue-vs-speech drift gate for the download pipeline.
//
// WHAT IT DECIDES, AND WHAT IT DELIBERATELY DOES NOT
//
// The gate answers ONE question: does the subtitle hold ONE constant offset to
// the spoken audio, or does the offset change partway through the file? A file
// whose offset moves has no valid single offset — any correction derived from
// its average shifts one part right and spoils another part. Measured on a
// drifting set (24 hearing-impaired files), such files exist in the wild and are
// not rare there.
//
// It does NOT report WHERE the change sits, to the second. Measured limit of the
// method (control test on a known-clean file: 0 false finds; synthetic test with
// jumps of known size and position: 6/6 recovered; real drifting files: 77 %
// recall, 36 % precision, positions scatter ±1–2 min). The scatter is set by the
// material, not by the search: an SDH cue does not start on the speech onset but
// leads it by a per-cue varying amount (~1.5 s MAD). So the verdict is a
// DIRECTION plus a SPAN, never a correction value — a number here would be read
// as an instruction and would be wrong.
//
// WHY THERE IS NO "FULL ANALYSIS" MODE
//
// A full-surface correlation was measured against the anchored method on the
// same drifting files: mean error 2.37 s vs. 2.49 s, regional scatter 11.5 s vs.
// 12.2 s. Both are equally poor at the absolute value, and both track a shift
// exactly (an added +2 s/+5 s came back within 0.05 s). The reason is not the
// method: a file drifting from −2 s to +10 s has no valid single offset, so
// every number is an average over the drift. The heavier method buys nothing.
//
// THE MODEL, IN ONE PARAGRAPH
//
// For a cue starting at t the anchor is the last speech-island start BEFORE t
// (from the decoded audio envelope). tau = t − anchor is small when the cue
// begins with the speech and large when it does not. The asymmetry matters: an
// island start AFTER the cue is implausible (a subtitle does not predict the
// voice), so it is heavily penalised. The distance to the NEAREST island start
// would be wrong — in dense dialogue the nearest start is always ~0 s away, the
// likelihood flattens, and the offset lands anywhere (measured +10.70 s against a
// truth of +4.65 s). The per-cue likelihood is then summed over a discrete
// offset grid, and two offsets (split at a candidate cue) are compared against
// one offset by their marginal likelihood — the Bayes factor. Recursion left and
// right finds further boundaries.
//
// HONEST LIMITS, ALL MEASURED
//
// - A control run on a known-clean file must find NOTHING. Two earlier
//   approaches failed exactly here and were discarded: binärmask cross-
//   correlation (found ±25 s jumps in the clean file, scatter 12.7 s) and raw
//   window confidence (high everywhere, including where the value was wrong).
// - A joint optimisation over k windows was measured against the recursive
//   binary split and is WORSE (synthetic 2/6 vs. 6/6, recall 22 % vs. 77 %).
//   The reason is structural: with a free offset per segment, each extra
//   boundary pays for itself, so k pins to its upper limit whatever the data.
//   The two-window restriction is therefore not the error bound.
// - ffmpeg is required for the audio envelope. Absent or unreadable audio means
//   the gate fail-opens (no verdict, file passes) — the same posture as every
//   other gate in this plugin.
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SubdlScribe.Qa;

/// <summary>
/// F-M295: the cue-vs-speech drift verdict. Answers "constant offset or moving
/// offset", never "correct it by N seconds".
/// </summary>
public sealed class DriftVerdict
{
    /// <summary>Gets a value indicating whether the gate ran at all (audio readable).</summary>
    public bool Ran { get; init; }

    /// <summary>Gets a value indicating whether a moving offset was found.</summary>
    public bool Drifts { get; init; }

    /// <summary>Gets the median offset in seconds (informational only, never a correction).</summary>
    public double MedianOffsetSec { get; init; }

    /// <summary>Gets the span in seconds between the first and last segment offset.</summary>
    public double SpanSec { get; init; }

    /// <summary>Gets the number of boundaries whose Bayes factor cleared the threshold.</summary>
    public int BoundaryCount { get; init; }

    /// <summary>Gets the boundary times in seconds, ordered.</summary>
    public IReadOnlyList<double> BoundaryTimesSec { get; init; } = [];

    /// <summary>Gets the largest Bayes factor seen (evidence strength).</summary>
    public double MaxBayesFactor { get; init; }

    /// <summary>Gets why the gate did not run, when it did not.</summary>
    public string? SkipReason { get; init; }

    /// <summary>
    /// Gets the human-readable one-line verdict for the log. Deliberately states the
    /// span and never a correction value (see the file header).
    /// </summary>
    public string Describe()
    {
        if (!Ran)
        {
            return "drift gate skipped: " + (SkipReason ?? "not run");
        }

        if (!Drifts)
        {
            return $"offset steady at {MedianOffsetSec:+0.00;-0.00}s";
        }

        return $"offset DRIFTS {MedianOffsetSec:+0.00;-0.00}s overall, span {SpanSec:0.0}s "
            + $"over {BoundaryCount} boundary/boundaries — no single offset is valid";
    }
}
