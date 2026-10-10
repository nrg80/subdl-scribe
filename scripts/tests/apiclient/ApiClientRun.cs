// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// F-M343: what does the BUILT SubdlApiClient actually put on the wire?
//
// WHY THIS IS AN INTERCEPTION AND NOT A LIVE CALL. Two facts have to hold: the search URL carries
// the integration name, and every request carries the product agent with a REAL version. A live
// call can prove neither — SubDL echoes back no header it received, and a URL the server accepted
// says nothing about the parameters the client stopped sending. A handler that records the
// outgoing request and answers a minimal, parseable body measures the exact URI and User-Agent,
// with no network, no quota and no fixture that can drift.
//
// The live half was measured separately, before the change went in (10.10.2026): the same search
// with and without client=other returned an IDENTICAL payload (HTTP 200, status true, 10
// candidates, equal content signature), and an unknown value was accepted just as silently — so
// the parameter cannot break a call, and the documented value list is a convention rather than a
// server-side check.
//
// Usage: apiclientrun <subdl-api-key>

using System.Net;
using System.Net.Http;
using System.Text;
using Jellyfin.Plugin.SubdlScribe.Api;

int failures = 0;

void Check(bool ok, string what, string detail = "")
{
    Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what + (detail.Length > 0 ? "  [" + detail + "]" : ""));
    if (!ok)
    {
        failures++;
    }
}

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.WriteLine("usage: apiclientrun <subdl-api-key>");
    return 2;
}

string? seenUri = null;
string? seenAgent = null;

using var http = new HttpClient(new CaptureHandler(
    req =>
    {
        seenUri = req.RequestUri?.ToString();
        seenAgent = req.Headers.UserAgent.ToString();
    },
    "{\"status\":true,\"subtitles\":[],\"results\":[]}"));

// Exactly what PluginServiceRegistrator.BuildApiClient sets on its own client, which is what the
// login call relies on (it builds its own request and adds no header of its own).
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SubdlApiClient.DefaultUserAgent);

var api = new SubdlApiClient(http) { ApiKey = args[0] };

Console.WriteLine("--- the client's own identity ---");
Console.WriteLine("   DefaultUserAgent: " + SubdlApiClient.DefaultUserAgent);
Console.WriteLine("   IntegrationClient: " + api.IntegrationClient);
Check(api.IntegrationClient == "other", "the integration name is other", api.IntegrationClient);
Check(SubdlApiClient.DefaultUserAgent.StartsWith("SubDL-Scribe/", StringComparison.Ordinal),
    "the agent names the product, not the assembly", SubdlApiClient.DefaultUserAgent);
Check(!SubdlApiClient.DefaultUserAgent.EndsWith("/1.0", StringComparison.Ordinal),
    "the agent no longer reports the frozen version 1.0", SubdlApiClient.DefaultUserAgent);
Check(SubdlApiClient.DefaultUserAgent.Contains("12.1.", StringComparison.Ordinal),
    "the agent carries a real assembly version", SubdlApiClient.DefaultUserAgent);

Console.WriteLine();
Console.WriteLine("--- the search request ---");
var res = await api.SearchSubtitlesAsync("tt0137523", null, 0, 0, "en", 1, CancellationToken.None).ConfigureAwait(false);
Console.WriteLine("   candidates parsed: " + (res?.Count ?? -1));
Console.WriteLine("   uri: " + seenUri);
Console.WriteLine("   agent on the wire: " + seenAgent);

Check(seenUri != null, "a request was made");
Check(seenUri!.Contains("client=other", StringComparison.Ordinal), "the search carries client=other");

// The parameter must be ADDITIVE. Every part the search sent before is still required, so an
// edit that replaced a parameter instead of appending one fails here rather than at SubDL.
foreach (var part in new[] { "imdb_id=tt0137523", "type=movie", "languages=en", "unpack=1", "page=1" })
{
    Check(seenUri.Contains(part, StringComparison.Ordinal), "the search still carries " + part);
}

Check(string.Equals(seenAgent, SubdlApiClient.DefaultUserAgent, StringComparison.Ordinal),
    "the request carries the plugin agent", seenAgent ?? "none");

Console.WriteLine();
Console.WriteLine("=== " + (failures == 0 ? "API-CLIENT OK" : failures + " FAILURE(S)") + " ===");
return failures == 0 ? 0 : 1;

internal sealed class CaptureHandler : HttpMessageHandler
{
    private readonly Action<HttpRequestMessage> _capture;
    private readonly string _body;

    public CaptureHandler(Action<HttpRequestMessage> capture, string body)
    {
        _capture = capture;
        _body = body;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _capture(request);
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(resp);
    }
}
