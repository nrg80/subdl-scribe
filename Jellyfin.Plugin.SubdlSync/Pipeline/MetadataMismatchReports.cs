// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.

using System;
using Jellyfin.Plugin.SubdlScribe.Api;

namespace Jellyfin.Plugin.SubdlScribe.Pipeline;

/// <summary>
/// F-M531: the report about a metadata format mismatch — the attribute, the subtitle, nothing else.
/// <para>
/// Operator wording (17.11.2026): "Nur was verkehrt ist: Metadata format mismatch. Das betroffene
/// attribut und betroffene sub. Automated report by…. Keine Geschichten erzählen." So the text is a
/// heading, one attribute line, one subtitle line, the attribution. No cause, no history, no
/// suggestion — the reader at SubDL sees what is wrong and where, and that is the whole report.
/// </para>
/// </summary>
public static class MetadataMismatchReports
{
    /// <summary>
    /// Builds the report for one wrong field. The category is SubDL's "wrong release info": the
    /// listing carries a value that does not match its own documentation.
    /// </summary>
    /// <param name="finding">The wrong field.</param>
    /// <param name="subId">SubDL entry id ("3676577-8593549").</param>
    /// <param name="releaseName">Release name as listed.</param>
    /// <param name="language">Language of the entry.</param>
    /// <param name="version">Plugin version, taken from the assembly.</param>
    /// <param name="account">SubDL account filing the report.</param>
    /// <returns>Category and comment, or null when the report cannot name its subject.</returns>
    public static ReportText? Build(
        FieldShapeFinding? finding,
        string? subId,
        string? releaseName,
        string? language,
        string? version,
        string? account)
    {
        if (finding == null || string.IsNullOrWhiteSpace(subId))
        {
            return null;
        }

        string lang = string.IsNullOrWhiteSpace(language) ? "—" : language.Trim().ToUpperInvariant();
        string release = (releaseName ?? string.Empty).Trim();

        string comment =
            "Metadata format mismatch.\n"
            + "Attribute: " + finding.Path + " (documented: " + finding.Documented
            + ", received: " + finding.Received + ")\n"
            + "Subtitle: " + subId.Trim() + " ('" + release + "', " + lang + ")\n\n"
            + "Automated report by Sub Scribe (Jellyfin plugin) v"
            + (string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim())
            + ", account " + (string.IsNullOrWhiteSpace(account) ? "unknown" : account.Trim()) + ".";

        return new ReportText(BrokenSubtitleReports.CategoryMetadata, comment);
    }
}
