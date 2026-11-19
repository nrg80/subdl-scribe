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
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// F-M533: the same robustness for tvsubtitles.net that the search answer of SubDL gets — a field
/// that contradicts the site's own vocabulary is named, the entry is dropped, the run continues.
/// <para>
/// tvsubtitles.net answers in HTML, so there is no JSON kind to compare. What the site does state is
/// its own value lists, and those are the yardstick: a language must be one of the 24 codes the
/// upload form offers, a rip must be one of the seven (or empty), and a season/episode label must lie
/// in the ranges the form accepts (season 1–22, episode 0–100). A value outside that is not "a rare
/// case we should tolerate" — it is an entry we cannot place, and it is reported through the site's
/// own "Report bad" form.
/// </para>
/// </summary>
public static class TvstMetadataChecks
{
    /// <summary>
    /// F-M533: names the finding, drops the entry — and reports it through the site's own "Report bad"
    /// form when the operator's report switch stands. Nothing here throws and nothing aborts the run.
    /// </summary>
    /// <param name="client">The site's client (its reporting channel).</param>
    /// <param name="findings">What contradicts the site's vocabulary.</param>
    /// <param name="subtitleId">Affected entry id, or null when the finding is about the page itself.</param>
    /// <param name="release">Release name of the affected entry, for the report.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when there was something to report (the caller drops the entry).</returns>
    public static async System.Threading.Tasks.Task<bool> EmitAsync(
        TvSubtitlesClient client,
        IReadOnlyList<FieldShapeFinding> findings,
        string? subtitleId,
        string? release,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (findings.Count == 0)
        {
            return false;
        }

        string version = typeof(TvstMetadataChecks).Assembly.GetName().Version?.ToString() ?? string.Empty;
        bool melden = Jellyfin.Plugin.SubScribe.Plugin.Instance?.Configuration.ReportBrokenSubtitles == true;

        foreach (var f in findings)
        {
            string text = string.IsNullOrWhiteSpace(subtitleId)
                ? Jellyfin.Plugin.SubScribe.Pipeline.MetadataMismatchReports.BuildSite(f, "tvsubtitles.net", version).Comment
                : Jellyfin.Plugin.SubScribe.Pipeline.MetadataMismatchReports
                    .Build(f, subtitleId, release, string.Empty, version, "tvsubtitles.net")?.Comment ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            OnMessage?.Invoke(text);

            if (melden && !string.IsNullOrWhiteSpace(subtitleId) && client != null)
            {
                try
                {
                    var outcome = await client.ReportBadAsync(subtitleId!, text, cancellationToken).ConfigureAwait(false);
                    OnMessage?.Invoke($"[tvsubtitles] Report bad {subtitleId}: {outcome}");
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException)
                {
                    OnMessage?.Invoke($"[tvsubtitles] Report bad {subtitleId} failed: {ex.GetType().Name}");
                }
            }
        }

        return true;
    }

    /// <summary>Where the findings are logged. The plugin sets this once.</summary>
    public static Action<string>? OnMessage { get; set; }

    /// <summary>
    /// Checks one entry of an episode page against the site's vocabulary.
    /// </summary>
    /// <param name="entry">The parsed entry.</param>
    /// <returns>Findings, empty when the entry says nothing the site does not allow.</returns>
    public static IReadOnlyList<FieldShapeFinding> CheckEntry(TvSubtitlesEntry? entry)
    {
        var findings = new List<FieldShapeFinding>();
        if (entry == null)
        {
            return findings;
        }

        string language = (entry.Language ?? string.Empty).Trim();
        if (language.Length == 0 || !TvSubtitlesClient.Languages.ContainsKey(language))
        {
            findings.Add(new FieldShapeFinding(
                "language", "language", language.Length == 0 ? "(empty)" : language, "String", "one of the site's 24 codes"));
        }

        string rip = entry.Rip ?? string.Empty;
        bool ripOk = false;
        foreach (string allowed in TvSubtitlesClient.Rips)
        {
            if (string.Equals(rip, allowed, StringComparison.Ordinal))
            {
                ripOk = true;
                break;
            }
        }

        if (!ripOk)
        {
            findings.Add(new FieldShapeFinding(
                "rip", "rip", rip, "String", "one of the site's seven rip values, or empty"));
        }

        return findings;
    }

    /// <summary>
    /// Checks the season/episode label of an episode page against the ranges the upload form accepts.
    /// </summary>
    /// <param name="season">Season as parsed from the page.</param>
    /// <param name="episode">Episode as parsed from the page.</param>
    /// <returns>Findings, empty when both are inside the site's ranges.</returns>
    public static IReadOnlyList<FieldShapeFinding> CheckEpisodeLabel(int season, int episode)
    {
        var findings = new List<FieldShapeFinding>();
        if (season < 1 || season > 22)
        {
            findings.Add(new FieldShapeFinding(
                "season", "season", season.ToString(CultureInfo.InvariantCulture), "Number", "1–22"));
        }

        if (episode < 0 || episode > 100)
        {
            findings.Add(new FieldShapeFinding(
                "episode", "episode", episode.ToString(CultureInfo.InvariantCulture), "Number", "0–100"));
        }

        return findings;
    }
}
