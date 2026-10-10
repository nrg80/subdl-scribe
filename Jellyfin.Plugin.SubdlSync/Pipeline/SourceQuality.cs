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

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// F-M344 (operator order 10.10.2026): the SOURCE QUALITY an upload reports.
/// <para>
/// Until 10.10.2026 the metadata step sent the literal <c>"web"</c>, with no reason recorded and no
/// requirement asking for it — a leftover of the first upload implementation (F-M8). It was wrong
/// for every file that is not a streaming source: the Dinosaurs episodes in the operator's library
/// are <c>480p DVD x265 Panda</c> rips and went to SubDL as WEB, and so does every BluRay or HDTV
/// file. The field is what SubDL shows as the release's quality tag and what its own filters read,
/// so a wrong value mislabels the upload for everyone who filters by quality.
/// </para>
/// <para>
/// THE VALUE LIST IS THE DOCUMENTED ONE, NOT AN INVENTED ONE. subdl.com/api-doc documents exactly
/// five values for the upload field <c>quality</c>: <c>web</c> (Web release, streaming, WEB-DL),
/// <c>bluray</c>, <c>dvd</c>, <c>hdtv</c>, <c>cam</c>. Measured on the live site: our own uploads
/// (sent as <c>web</c>) are displayed with the tag <c>webdl</c> — the label differs from the value,
/// the value is accepted. Anything this class cannot place stays <c>web</c>, which is exactly the
/// behaviour that shipped before, so an unrecognized name cannot regress.
/// </para>
/// <para>
/// THE NAME IS THE EVIDENCE, and the loose file wins over the media file: for a sidecar the
/// subtitle's own file name describes where THAT subtitle came from, while the media file next to
/// it may be a different release of the same film. For an embedded track there is no own name —
/// the container it was extracted from is the source, so the media file name is read.
/// </para>
/// <para>
/// THE LAST SOURCE WORD IN THE NAME WINS, and the words are matched as WHOLE TOKENS. A release name
/// puts its title first and its source after it — <c>The.DVD.Club.2019.1080p.BluRay.x264</c> is a
/// BluRay, and the disc word belongs to the title. A substring rule would also read the CAM in
/// "Cambridge" as a camera recording, which is why the short markers are never matched as
/// fragments. The compact forms (for spellings whose separators swallow the word, "Blu-Ray") are
/// consulted only when no token matched, and only against distinctive strings.
/// </para>
/// <para>
/// The asymmetry behind both rules: a missed BluRay falls back to <c>web</c>, which is the old
/// behaviour; a false BluRay would mislabel a streaming release for everyone who filters. So the
/// fallback is cheap and the false positive is not.
/// </para>
/// </summary>
public static class SourceQuality
{
    /// <summary>Web release (streaming, WEB-DL). Also the fallback for an unrecognized name.</summary>
    public const string Web = "web";

    /// <summary>Blu-ray source.</summary>
    public const string BluRay = "bluray";

    /// <summary>DVD source.</summary>
    public const string Dvd = "dvd";

    /// <summary>HDTV broadcast source.</summary>
    public const string Hdtv = "hdtv";

    /// <summary>Camera recording.</summary>
    public const string Cam = "cam";

    /// <summary>
    /// Every value the API document lists for the upload field <c>quality</c>. The upload path must
    /// only ever send one of these; the assertion exists so an invented value cannot slip in.
    /// </summary>
    public static IReadOnlyList<string> Allowed { get; } = new[] { Web, BluRay, Dvd, Hdtv, Cam };

    private static readonly char[] Separators =
        { '.', '_', '-', ' ', '(', ')', '[', ']', '+', ',', '\'', '&' };

    // Whole-token tables. They are disjoint, so the order inside Classify only documents intent.
    private static readonly string[] CamTokens =
        { "CAM", "CAMRIP", "HDCAM", "HDTS", "TELESYNC", "TELECINE" };

    private static readonly string[] HdtvTokens =
        { "HDTV", "HDTVRIP", "PDTV", "TVRIP" };

    private static readonly string[] DvdTokens =
        { "DVD", "DVDR", "DVDRIP", "DVD5", "DVD9", "DVDSCR" };

    private static readonly string[] BluRayTokens =
        { "BLURAY", "BDRIP", "BRRIP", "BDR", "BDREMUX", "REMUX", "BD25", "BD50", "UHDBD" };

    private static readonly string[] WebTokens =
        { "WEB", "WEBDL", "WEBRIP", "AMZN", "DSNP", "ATVP", "HMAX", "PCOK", "CRAV", "STAN", "HULU", "ROKU" };

    // Compact forms, for spellings whose separators swallow the word ("Blu-Ray" → BLU + RAY).
    // Deliberately short and distinctive — a fragment a film title could contain is not in here.
    private static readonly string[] HdtvCompact = { "HDTV", "PDTV" };
    private static readonly string[] DvdCompact = { "DVDRIP", "DVDR", "DVDREMUX", "DVD5", "DVD9", "DVDSCR" };
    private static readonly string[] BluRayCompact = { "BLURAY", "BDRIP", "BRRIP", "BDREMUX", "REMUX", "BD25", "BD50", "UHDBD" };
    private static readonly string[] WebCompact = { "WEBDL", "WEBRIP" };

    /// <summary>
    /// Reads the source quality out of a file name (a path is accepted and reduced to its name).
    /// </summary>
    /// <param name="fileNameOrPath">Media or sidecar file name. Null, empty or unplaceable ⇒ <see cref="Web"/>.</param>
    /// <returns>One of <see cref="Allowed"/>.</returns>
    public static string Detect(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return Web;
        }

        string name;
        try
        {
            name = Path.GetFileNameWithoutExtension(fileNameOrPath);
        }
        catch (ArgumentException)
        {
            name = fileNameOrPath;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = fileNameOrPath;
        }

        string upper = name.ToUpperInvariant();
        string[] tokens = upper.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        // The LAST source word wins — see the class remarks for why the order decides.
        string? placed = null;
        foreach (string token in tokens)
        {
            placed = Classify(token) ?? placed;
        }

        if (placed != null)
        {
            return placed;
        }

        string compact = Compact(upper);
        if (CompactHas(compact, HdtvCompact))
        {
            return Hdtv;
        }

        if (CompactHas(compact, DvdCompact))
        {
            return Dvd;
        }

        if (CompactHas(compact, BluRayCompact))
        {
            return BluRay;
        }

        if (CompactHas(compact, WebCompact))
        {
            return Web;
        }

        return Web;
    }

    /// <summary>
    /// Reads the source quality out of the SUBTITLE's own name, and out of the media file name only
    /// when the first says nothing — the rule the upload path uses: the sidecar states where that
    /// subtitle came from, the container is the fallback.
    /// </summary>
    /// <param name="primary">The subtitle's own file name (null for an embedded track).</param>
    /// <param name="secondary">The media file name, read when <paramref name="primary"/> is unplaceable.</param>
    /// <returns>One of <see cref="Allowed"/>.</returns>
    public static string Detect(string? primary, string? secondary)
    {
        string first = Detect(primary);
        return first != Web ? first : Detect(secondary);
    }

    /// <summary>Classifies ONE token; null when the token says nothing about the source.</summary>
    private static string? Classify(string token)
    {
        if (InTable(CamTokens, token))
        {
            return Cam;
        }

        if (InTable(HdtvTokens, token))
        {
            return Hdtv;
        }

        if (InTable(DvdTokens, token))
        {
            return Dvd;
        }

        if (InTable(BluRayTokens, token))
        {
            return BluRay;
        }

        if (InTable(WebTokens, token))
        {
            return Web;
        }

        return null;
    }

    private static bool InTable(string[] table, string token)
    {
        foreach (string entry in table)
        {
            if (string.Equals(entry, token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CompactHas(string compact, string[] patterns)
    {
        foreach (string pattern in patterns)
        {
            if (compact.Contains(pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Letters and digits only, so "Blu-Ray" and "BluRay" answer alike.</summary>
    private static string Compact(string upper)
    {
        Span<char> buffer = upper.Length <= 512 ? stackalloc char[upper.Length] : new char[upper.Length];
        int n = 0;
        foreach (char c in upper)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[n++] = c;
            }
        }

        return new string(buffer[..n]);
    }
}
