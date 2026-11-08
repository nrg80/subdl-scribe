// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even
// the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// F-M450: what one report to SubDL says, and which of its four categories it lands in.
/// <para>
/// The text is DELIBERATELY bare. SubDL's report form offers four categories for a subtitle —
/// "not a subtitle" (spam, fake, incomplete), "wrong release info", "stolen", "other" — and the
/// operator asked for the naked finding, not for a justification: the package, what is wrong with
/// it, and who reports. No request, no apology, no measurement history. Everything that is not the
/// finding belongs in the attribution line, which is the same for every report.
/// </para>
/// <para>
/// The category follows the finding, never the caller: an empty package is an incomplete subtitle,
/// a wrong runtime is "other" (nothing in the catalogue covers a mismatch of cut), and a wrong
/// language is wrong metadata. A finding nobody can classify returns null and is not reported.
/// </para>
/// </summary>
public static class BrokenSubtitleReports
{
    /// <summary>"Not a subtitle" — spam, fake or incomplete (F-M450).</summary>
    public const string CategoryIncomplete = "not_a_subtitle";

    /// <summary>"Wrong release info" — the listing says something the file does not hold (F-M450).</summary>
    public const string CategoryMetadata = "wrong_release_info";

    /// <summary>"Other" — nothing in the catalogue covers it (F-M450).</summary>
    public const string CategoryOther = "other";

    /// <summary>The naked finding for a package that delivered no usable payload.</summary>
    /// <param name="bytes">Bytes that arrived.</param>
    /// <returns>Finding text.</returns>
    public static string EmptyPayload(int bytes) => "downloads with " + bytes + " bytes";

    /// <summary>The naked finding for an SRT that cannot be used.</summary>
    public const string NoCues = "no usable cues";

    /// <summary>The naked finding for timestamps that run backwards.</summary>
    public const string NonMonotonic = "non-monotonic timestamps";

    /// <summary>The naked finding for a single cue that is far too long or too short.</summary>
    /// <param name="seconds">Measured cue length in seconds.</param>
    /// <returns>Finding text.</returns>
    public static string CueDuration(int seconds) => "cue duration " + seconds + "s";

    /// <summary>The naked finding for a valid but stub-sized subtitle.</summary>
    /// <param name="cues">Cue count.</param>
    /// <returns>Finding text.</returns>
    public static string FewCues(int cues) => cues + " cues";

    /// <summary>The naked finding for a subtitle that does not cover the runtime it is listed for.</summary>
    /// <param name="spanSeconds">Cue span in seconds.</param>
    /// <param name="runtimeSeconds">Item runtime in seconds.</param>
    /// <returns>Finding text.</returns>
    public static string RuntimeMismatch(int spanSeconds, int runtimeSeconds)
        => "cue span " + spanSeconds + "s vs. runtime " + runtimeSeconds + "s";

    /// <summary>The naked finding for a subtitle filed under the wrong language.</summary>
    /// <param name="detected">Language detected in the file.</param>
    /// <returns>Finding text.</returns>
    public static string WrongLanguage(string detected) => "contains " + detected;

    /// <summary>
    /// Translates the gate's own wording into a naked finding, so what a report says comes from the
    /// measurement the gate refused on — not from a second opinion.
    /// </summary>
    /// <param name="gateFailure">Gate verdict ("language EN", "structure (...)", "min-cues (12)",
    /// "runtime (span 2433s vs runtime 1290s (tolerance ±600s))").</param>
    /// <returns>Naked finding, or null when the verdict is not a reportable defect.</returns>
    public static string? FindingFromGateFailure(string? gateFailure)
    {
        if (string.IsNullOrWhiteSpace(gateFailure))
        {
            return null;
        }

        string text = gateFailure.Trim();
        if (text.StartsWith("language ", StringComparison.Ordinal))
        {
            string detected = text["language ".Length..].Trim();
            return detected.Length == 0 ? null : WrongLanguage(detected);
        }

        if (text.StartsWith("structure", StringComparison.Ordinal))
        {
            return NoCues;
        }

        if (text.StartsWith("min-cues", StringComparison.Ordinal))
        {
            string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                string digits = token.Trim('(', ')');
                if (digits.Length > 0 && digits.Length != token.Length
                    && int.TryParse(digits, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int cues))
                {
                    return FewCues(cues);
                }
            }

            return null;
        }

        if (text.StartsWith("runtime", StringComparison.Ordinal))
        {
            int? span = NumberAfter(text, "span ");
            int? runtime = NumberAfter(text, "runtime ", after: "span");
            if (span != null && runtime != null)
            {
                return RuntimeMismatch(span.Value, runtime.Value);
            }

            return null;
        }

        return null;
    }

    private static int? NumberAfter(string text, string marker, string? after = null)
    {
        int from = after == null ? 0 : text.IndexOf(after, StringComparison.Ordinal);
        if (from < 0)
        {
            return null;
        }

        int at = text.IndexOf(marker, from, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        int i = at + marker.Length;
        int start = i;
        while (i < text.Length && char.IsDigit(text[i]))
        {
            i++;
        }

        if (i == start)
        {
            return null;
        }

        return int.TryParse(text[start..i], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    /// <summary>
    /// Cuts a finding back to the part the category is decided on.
    /// <para>
    /// A finding may name the pack member it belongs to — "cue span 2508s vs. runtime 1254s
    /// ('The.Middle.S03E01.srt')". That suffix makes the report unambiguous for the reader, but it
    /// must not change how the finding is classified, so the category is read from the head only.
    /// </para>
    /// </summary>
    /// <param name="finding">Finding, possibly with a member name.</param>
    /// <returns>Finding without the member name.</returns>
    private static string Head(string finding)
    {
        int at = finding.IndexOf(" ('", StringComparison.Ordinal);
        return (at > 0 ? finding[..at] : finding).Trim();
    }

    /// <summary>
    /// Builds the report for one finding, or null when the finding is not reportable.
    /// </summary>
    /// <param name="subdlId">SubDL entry id ("3767750-8689923").</param>
    /// <param name="releaseName">Release name as SubDL lists it.</param>
    /// <param name="language">Language the candidate was requested for.</param>
    /// <param name="finding">Naked finding from one of the helpers above.</param>
    /// <param name="version">Plugin version, taken from the assembly.</param>
    /// <param name="account">SubDL account that files the report.</param>
    /// <returns>Category and comment, or null.</returns>
    public static ReportText? Build(
        string? subdlId, string? releaseName, string? language, string? finding, string? version, string? account)
    {
        if (string.IsNullOrWhiteSpace(finding) || string.IsNullOrWhiteSpace(subdlId))
        {
            return null;
        }

        string id = subdlId.Trim();
        string release = (releaseName ?? string.Empty).Trim();
        string lang = (language ?? string.Empty).Trim().ToUpperInvariant();
        string what = finding.Trim();

        string head = Head(what);
        bool listedUnderLanguage = head.StartsWith("contains ", StringComparison.Ordinal);
        if (listedUnderLanguage && lang.Length == 0)
        {
            return null;
        }

        string subject = listedUnderLanguage
            ? "Package " + id + " ('" + release + "')"
            : "Package " + id + " ('" + release + "', " + lang + ")";

        string category;
        string sentence;
        if (head.StartsWith("downloads with ", StringComparison.Ordinal))
        {
            category = CategoryIncomplete;
            sentence = subject + " " + what + ".";
        }
        else if (head.EndsWith(" cues", StringComparison.Ordinal))
        {
            category = CategoryIncomplete;
            sentence = subject + ": " + what + ".";
        }
        else if (head == NoCues || head == NonMonotonic
                 || head.StartsWith("cue duration ", StringComparison.Ordinal))
        {
            category = CategoryIncomplete;
            sentence = subject + ": " + what + ".";
        }
        else if (head.StartsWith("cue span ", StringComparison.Ordinal))
        {
            category = CategoryOther;
            sentence = subject + ": " + what + ".";
        }
        else if (listedUnderLanguage)
        {
            category = CategoryMetadata;
            sentence = subject + ": listed as " + lang + ", " + what + ".";
        }
        else
        {
            return null;
        }

        string attribution = "Automated report by SubDL Scribe (Jellyfin plugin) v"
            + (string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim())
            + ", account " + (string.IsNullOrWhiteSpace(account) ? "unknown" : account.Trim()) + ".";

        return new ReportText(category, sentence + "\n\n" + attribution);
    }
}

/// <summary>F-M450: one prepared report — category for SubDL and the comment that goes with it.</summary>
/// <param name="Category">One of the four SubDL categories.</param>
/// <param name="Comment">The comment text, finding plus attribution.</param>
public sealed record ReportText(string Category, string Comment);
