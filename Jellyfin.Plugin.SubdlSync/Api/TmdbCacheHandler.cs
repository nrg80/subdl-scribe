#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// F-M555 (Befund des Operators 16.11.2026, „Die tmdb requests sind komisch. Elle ist längst
/// durchgelaufen"): jede TMDb-Antwort wird an EINER Stelle abgelegt und von dort beantwortet.
/// <para>
/// DER BEFUND: der Resolver fragt vor 9 seiner 13 Sendestellen einen Speicher und an 4 Stellen nicht
/// (u. a. <c>/tv/{id}/external_ids</c> und die Suche). Gemessen ueber den Tag: **1221** TMDb-Anfragen,
/// **305** verschiedene Adressen, **916** Wiederholungen — dieselbe <c>/tv/1416</c> 21×, dieselbe
/// <c>/movie/1423191</c> 19×, teils zweimal in derselben Sekunde. Und der „id test" lief **2173**×
/// fuer 183 verschiedene Titel (The Middle 495×). Der Speicher half also nur dort, wo jemand daran
/// gedacht hatte — genau die Bauform, die der Operator nicht will („Logiken die robust fuer alles
/// sind, nicht casebycase").
/// </para>
/// <para>
/// DIE REGEL: dieser Handler sitzt in der HTTP-Kette und beantwortet JEDE GET-Anfrage an
/// <c>api.themoviedb.org</c> aus dem Speicher; nur ein Fehltreffer geht ins Netz, und eine brauchbare
/// Antwort wird abgelegt. Damit kann keine kuenftige Aufrufstelle den Speicher vergessen. Andere
/// Adressen (SubDL, tvsubtitles, moviesubtitles) sieht der Handler nicht an — ihr Verkehr geht
/// unveraendert durch.
/// </para>
/// <para>
/// Der Schluessel ist die Adresse OHNE <c>api_key</c>: der Schluessel wechselt, die Frage nicht.
/// Nur Status 200 wird abgelegt; ein Fehler ist keine Antwort und muss erneut gefragt werden duerfen.
/// </para>
/// </summary>
public sealed class TmdbCacheHandler : DelegatingHandler
{
    /// <summary>The only host this handler answers from the store.</summary>
    private const string TmdbHost = "api.themoviedb.org";

    /// <summary>Namespace prefix, so an address key can never collide with a resolver question key.</summary>
    private const string KeyPrefix = "url:";

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbCacheHandler"/> class.
    /// </summary>
    /// <param name="inner">The next handler in the chain.</param>
    public TmdbCacheHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    /// <summary>Builds the store key of a TMDb address.</summary>
    /// <param name="uri">The request address.</param>
    /// <returns>The key handed to the answer store.</returns>
    public static string KeyOf(Uri uri)
    {
        if (uri == null)
        {
            return KeyPrefix;
        }

        string query = uri.Query ?? string.Empty;
        if (query.Length > 0)
        {
            var kept = new StringBuilder();
            foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (kept.Length > 0)
                {
                    kept.Append('&');
                }

                kept.Append(part);
            }

            query = kept.Length > 0 ? "?" + kept : string.Empty;
        }

        return KeyPrefix + uri.GetLeftPart(UriPartial.Path) + query;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request == null || request.Method != HttpMethod.Get || request.RequestUri == null
            || !string.Equals(request.RequestUri.Host, TmdbHost, StringComparison.OrdinalIgnoreCase))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        string key = KeyOf(request.RequestUri);
        if (Registry.TmdbAnswerStore.TryGet((key, true), out string? stored) && !string.IsNullOrEmpty(stored))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(stored, Encoding.UTF8, "application/json"),
            };
        }

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return response;
        }

        // Der Rumpf wird EINMAL gelesen: ablegen und dem Aufrufer dasselbe wiedergeben. Ohne das
        // Puffern waere der Inhalt nach dem Lesen fuer den Aufrufer verbraucht.
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            Registry.TmdbAnswerStore.Put((key, true), body);
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return response;
    }
}
