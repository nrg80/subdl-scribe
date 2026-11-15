// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubdlScribe.Configuration;

namespace Jellyfin.Plugin.SubdlScribe.Api.Endpoints;

/// <summary>
/// F-M483: the SubDL download endpoint — a thin wrapper, not a rewrite. It calls exactly the client
/// methods the pipeline called before (search, then file fetch) and translates their types into the
/// neutral ones. Nothing about routes, quotas or caching moves: those stay in the pipeline, which is
/// what "den rest des plugins gleich lassen" means.
/// </summary>
public sealed class SubdlDownloadEndpoint : IDownloadEndpoint
{
    private readonly SubdlApiClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlDownloadEndpoint"/> class.
    /// </summary>
    /// <param name="client">The SubDL API client.</param>
    public SubdlDownloadEndpoint(SubdlApiClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public SubtitleProvider Provider => SubtitleProvider.Subdl;

    /// <inheritdoc />
    public bool Wired => true;

    /// <summary>
    /// F-M483: maps a SubDL candidate onto the neutral hit. Pure, so the mapping is testable without a
    /// network call — and so a changed candidate field is one edit, not a hunt.
    /// </summary>
    /// <param name="candidate">The candidate as the client returned it.</param>
    /// <returns>The neutral hit.</returns>
    public static SubtitleHit Map(SubtitleCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new SubtitleHit(
            SubtitleProvider.Subdl,
            candidate.Url,
            candidate.Language,
            string.Empty,                    // SubDL carries no rip cell; quality is in ReleaseName
            candidate.ReleaseName,
            candidate.FullSeason);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubtitleHit>> SearchAsync(
        SubtitleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var candidates = await _client.SearchSubtitlesAsync(
            query.ImdbId,
            query.TmdbId,
            query.Season,
            query.Episode,
            string.IsNullOrWhiteSpace(query.Language) ? null : query.Language.ToUpperInvariant(),
            1,
            cancellationToken).ConfigureAwait(false);
        return candidates is null ? Array.Empty<SubtitleHit>() : candidates.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task<SubtitlePayload?> FetchAsync(
        SubtitleHit hit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (string.IsNullOrWhiteSpace(hit.Id))
        {
            return null;
        }

        var outcome = await _client.DownloadSubtitleFileAsync(hit.Id, cancellationToken).ConfigureAwait(false);
        if (!outcome.HasContent)
        {
            return null;
        }

        return new SubtitlePayload(
            outcome.Member ?? (hit.Release.Length > 0 ? hit.Release : "subtitle") + ".srt",
            outcome.Content!,
            hit.Language);
    }
}

/// <summary>
/// F-M483: the SubDL upload endpoint. It covers the SINGLE subtitle, which is what the neutral ticket
/// describes; a season pack needs the pipeline's own pack building (F-M372) and is refused with a
/// reason rather than silently sent as a single file.
/// </summary>
public sealed class SubdlUploadEndpoint : IUploadEndpoint
{
    private readonly SubdlApiClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlUploadEndpoint"/> class.
    /// </summary>
    /// <param name="client">The SubDL API client.</param>
    public SubdlUploadEndpoint(SubdlApiClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public SubtitleProvider Provider => SubtitleProvider.Subdl;

    /// <inheritdoc />
    public bool Wired => true;

    /// <inheritdoc />
    public async Task<EndpointAnswer> SendAsync(
        UploadTicket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.SeasonPack)
        {
            return new EndpointAnswer(
                Provider, false,
                "season packs are built by the upload pipeline (F-M372), not by a single-file endpoint",
                0, null, true);
        }

        if (ticket.Data is not { Length: > 0 })
        {
            return new EndpointAnswer(Provider, false, "no subtitle bytes", 0, null, true);
        }

        // SubDL takes the subtitle as TEXT, and the encoding is load-bearing: a cp1250 file decoded as
        // UTF-8 turns Hungarian into replacement characters. So the text is only built from bytes that
        // really are UTF-8; anything else is refused here instead of being sent mangled (F-M482).
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(ticket.Data);
        }
        catch (ArgumentException)
        {
            return new EndpointAnswer(
                Provider, false,
                "the subtitle is not UTF-8 and this endpoint will not transcode it", ticket.Data.Length, null, true);
        }

        if (string.IsNullOrWhiteSpace(ticket.ImdbId))
        {
            return new EndpointAnswer(Provider, false, "no-imdb", 0, null, true);
        }

        try
        {
            var result = await _client.UploadSubtitleAsync(
                text,
                ticket.FileName,
                ticket.Language.ToUpperInvariant(),
                ticket.Release ?? string.Empty,
                ticket.Rip ?? string.Empty,
                ticket.ImdbId,
                ticket.TmdbId,
                isSeries: ticket.Season > 0,
                ticket.Season,
                ticket.Episode > 0 ? ticket.Episode : null,
                cancellationToken,
                ticket.HearingImpaired).ConfigureAwait(false);

            return new EndpointAnswer(
                Provider, result.Ok, result.Reason, ticket.Data.Length, null, false);
        }
        catch (InvalidOperationException ex)
        {
            // The client throws this when no token is held — a caller-readable refusal, not a crash.
            return new EndpointAnswer(Provider, false, ex.Message, ticket.Data.Length, null, false);
        }
    }
}
