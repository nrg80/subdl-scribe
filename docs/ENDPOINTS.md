# Endpunkte — zwei Quellen, ein Weg

Stand 15.11.2026. Gilt für `Jellyfin.Plugin.SubdlSync/Api/Endpoints/` (F-M483, F-M484).

**Die Bestellung:** „2 download endpoints und 2 upload endpoints einführen und den Rest des Plugins
gleich lassen."

Also gibt es vier Endpunkte, je einen pro Quelle und Richtung:

| Richtung | SubDL | tvsubtitles.net |
|---|---|---|
| Download | `SubdlDownloadEndpoint` — **eingebunden** | `TvSubtitlesDownloadEndpoint` — steht, **nicht eingebunden** |
| Upload | `SubdlUploadEndpoint` — **eingebunden** | `TvSubtitlesUploadEndpoint` — steht, **nicht eingebunden** |

## Die Naht

```csharp
public interface IDownloadEndpoint
{
    SubtitleProvider Provider { get; }
    bool Wired { get; }
    Task<IReadOnlyList<SubtitleHit>> SearchAsync(SubtitleQuery query, CancellationToken ct);
    Task<SubtitlePayload?> FetchAsync(SubtitleHit hit, CancellationToken ct);
}

public interface IUploadEndpoint
{
    SubtitleProvider Provider { get; }
    bool Wired { get; }
    Task<EndpointAnswer> SendAsync(UploadTicket ticket, CancellationToken ct);
}
```

`SubtitleQuery`, `SubtitleHit`, `SubtitlePayload`, `UploadTicket` und `EndpointAnswer` sind neutral:
keine SubDL-Felder, keine tvsubtitles-Felder. Was eine Quelle nicht kann, sagt **ihr** Endpunkt mit
einem Grund — nicht der Aufrufer durch Raten.

**Der Rest des Plugins bleibt, wie er ist.** `DownloadPipeline`, `UploadPipeline`, Store, QA-Tore,
Scheduler und GUI-Aufbau sind unangetastet; der SubDL-Endpunkt ist eine dünne Übersetzung um genau
die Aufrufe, die die Pipeline vorher selbst gemacht hat. Die Naht behauptet keine Einbindung, die es
nicht gibt — siehe `Wired`.

## Der Katalog

```csharp
var catalog = new SubtitleEndpointCatalog(downloads, uploads);
catalog.AllDownloads            // alle vier, egal ob geschaltet
catalog.EnabledDownloads(config) // Schalter AN und Wired
catalog.Refused(config, SubtitleDirection.Download) // was nicht läuft, mit Grund
```

`Enabled…` verlangt **beides**: den Schalter aus F-M477 **und** `Wired`. Deshalb bleibt
tvsubtitles.net auch bei eingeschaltetem Schalter draußen, solange der Lauf ihn nicht aufruft.

`Refused` liefert den Satz, den auch die Konfigurationsseite unter dem Schalter zeigt — eine Aussage,
nicht drei. Steht ein Endpunkt auf `Wired = false`, lautet er:

> tvsubtitles.net — Endpunkte stehen (Abruf und Upload), sind aber noch nicht in den Lauf
> eingebunden: der Schalter hat noch keine Wirkung.

## Was die Endpunkte tun

**`SubdlDownloadEndpoint`** ruft `SearchSubtitlesAsync` (eine Seite) und `DownloadSubtitleFileAsync`;
`Map(SubtitleCandidate)` ist eine reine Funktion und wird von der Suite ohne Netz geprüft. SubDL hat
keine Rip-Zelle — der Treffer erfindet keine, der Rip steht im Release-Namen.

**`SubdlUploadEndpoint`** deckt den **Einzelupload**. Er verweigert mit Grund statt halb zu senden:
Staffelpack (das baut die Pipeline, F-M372), fehlende IMDb-Kennung (`no-imdb`), **Bytes, die nicht
UTF-8 sind** — SubDL nimmt Text, und ein cp1250-File als UTF-8 zu lesen macht ungarisch kaputt
(F-M482). Ohne Sitzung kommt eine lesbare Ablehnung, keine Ausnahme.

**`TvSubtitlesDownloadEndpoint`** löst die Serie über den **Namen** auf (die Quelle hat keinen
Id-Index), läuft die Folgenseiten der Staffel ab, bis eine `Staffel x Folge` sagt, und filtert die
Einträge auf die gewünschte Sprache. Die Sprachbrücke (`SiteLanguage`) ist die einzige Stelle, die
beide Vokabulare kennt (`de → de`, `pt → pt`, `xx → null`).

**`TvSubtitlesUploadEndpoint`** baut das Formular (F-M481) und sendet multipart. Trockenlauf ist die
Vorgabe und wird von außen gesetzt (das `DryRun` des Plugins), nicht intern gelesen — so bleibt der
Endpunkt prüfbar. Staffelpack, unbekannte Sprache, fehlender Serienname und eine Serie, die die Seite
nicht führt: je ein Grund, kein Sendeversuch.

## Prüfungen

Suite `scripts/tests/endpoints` (T204, **28 Prüfungen**, kein Netz):

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 /opt/data/.dotnet/dotnet run \
  --project scripts/tests/endpoints/endpoints.csproj -c Release -- /opt/data/subdl-scribe
```

Sie fährt die tvsubtitles-Seite über einen gefälschten HTTP-Handler gegen die echten Seiten aus
`scripts/tests/tvsubtitles/fixtures` — von der Serienliste bis zum Zip — und prüft die SubDL-Seite
über ihre reine Abbildung und ihre Ablehnungen.

## Der nächste Schritt (nicht Teil dieses Schnitts)

`Wired = true` für tvsubtitles setzen heißt: die Pipelines rufen den Katalog statt des SubDL-Clients.
Das ist die Einbindung in den Lauf — bewusst noch nicht gemacht, weil die Bestellung „den Rest gleich
lassen" lautete.
