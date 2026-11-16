// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubdlScribe.Api;

/// <summary>
/// F-M547: zieht <see cref="ExternalApiPacer"/> nach JEDER externen Anfrage — eine Stelle fuer alle
/// Quellen.
/// <para>
/// Der Handler sitzt INNEN, direkt ueber dem <c>SocketsHttpHandler</c> und unter
/// <c>TransientRetryHandler</c>. Damit wird jeder einzelne Versuch bezahlt: ein Wiederholungsversuch
/// ist eine echte Anfrage mehr und darf nicht kostenlos sein. Ein DelegatingHandler wurde bewusst
/// gewaehlt statt Pausen an den Aufrufstellen — die Aufrufer sind SubDL (15 Stellen), TMDb (13),
/// tvsubtitles und moviesubtitles; eine vergessene Stelle waere eine stille Ausnahme, und neue Quellen
/// erben die Regel hier ohne Zutun.
/// </para>
/// </summary>
public sealed class PacingHandler : DelegatingHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PacingHandler"/> class.
    /// </summary>
    /// <param name="inner">The handler that performs the actual request.</param>
    public PacingHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Immer danach — auch bei einem Fehlerstatus, denn die Anfrage hat stattgefunden.
        await ExternalApiPacer.AfterCallAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }
}
