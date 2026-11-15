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

## Herkunft im Store und keine Kreuzkontamination (F-M486/F-M487)

Jeder Untertitel trägt seit Schema 10 zwei Felder im Store (`sidecars` **und** `embeds`):

| Feld | Inhalt |
|---|---|
| `Origin` | `subdl` oder `tvsubtitles` |
| `OriginId` | die **eigene Id der Quelle** — tvsubtitles-Untertitel-Id (z. B. `170678`) oder SubDL-Eintrags-Id |

**Ist ein tvst-Fund eindeutig identifizierbar?** Ja. Die Id benennt den Eintrag auf der Seite, der
Inhalt-Hash benennt den Text, beide zusammen sind eindeutig — die Id wird im Store abgelegt, nicht nur
im Log. `null` heißt „diese Zeile sagt nichts" (Altzeile, Handkopie, eingebettete Spur) und wird nie
geraten; ein Schreiber ohne Kenntnis löscht eine vorhandene Angabe nicht.

**Die Kreuzregel** (`Provenance.MayUploadTo`, eine Stelle, nicht zwei):

- Herkunft **bekannt** → nur die **eigene** Quelle nimmt den Upload. Ein tvst-Fund geht nie zu SubDL,
  ein SubDL-Fund nie zu tvst.
- Herkunft **unbekannt** → jeder Endpunkt erlaubt. Sonst könnte der Betreiber seine eigenen Dateien
  nicht mehr hochladen.
- Die Ablehnung landet im Log: `cross-source: subtitle came from tvsubtitles, not uploading it to subdl`.

Der Upload liest die Herkunft vor der Endpunktwahl über den Inhalt-Hash (`Registry.GetOrigin`), der
Download schreibt sie beim Speichern mit (`Provenance.Name(cand.Provider)` plus die Id der Quelle).

## Vier Kennzeichen, je Quelle und Richtung eines (F-M488)

| Feld | Wann es gefüllt wird | Woher der Wert kommt |
|---|---|---|
| `SubdlDownloadId` | Download von SubDL | die SubDL-Eintrags-Id des Treffers |
| `SubdlUploadId` | Upload zu SubDL | die Id aus der Antwort des Uploads |
| `TvSubtitlesDownloadId` | Download von tvsubtitles.net | die Untertitel-Id der Seite (z. B. `170678`) |
| `TvSubtitlesUploadId` | Upload zu tvsubtitles.net | **zurückgelesen** von der Folgenseite; fehlt sie, eine **eigene** Id (`own:…`) |

Für SubDL war nichts Neues nötig — die Id steht schon in der Antwort, sie wird nur zusätzlich in ihr
eigenes Feld geschrieben. tvsubtitles.net antwortet auf den Upload **ohne** Id, deshalb wird sie
danach von der Folgenseite gelesen (Sprache und Release müssen passen). **Findet der Rückleser nichts,
vergibt das Plugin eine eigene Id:** `own:<quelle>:<Zeitstempel>:<8 Hex>`. Das Präfix macht sie
unverwechselbar mit einer Id der Seite, und der Store kann damit sagen „dieser Upload ist passiert,
sein Ziel ist unser eigene Kennzeichen" statt „unbekannt". Gesetzt wird nur das Feld der Quelle, die
den Upload wirklich genommen hat.

Schema 11, in `sidecars` **und** `embeds`, per `ALTER TABLE` nachgezogen; geschrieben wird nur, was
bekannt ist, und ein leerer Wert löscht keine vorhandene Angabe.

## Reihenfolge und Erfolgsbegriff (F-M491)

Unter Downloads steht die Reihenfolge: **tvsubtitles.net first** (Default) oder **SubDL first**. Steht tvst
vorn, **ersetzen** seine Treffer die SubDL-Kandidaten statt sich hinter sie zu stellen; hat die Seite für
die Sprache nichts, bleibt SubDL der Rückfall. Rangfolge, Tore, Fit, Ablage und Herkunft sind für beide
Reihenfolgen derselbe Code.

**Erfolgreich heruntergeladen** heißt bei eingeschaltetem Auto-Sync: die Fassung hat die **Signifikanz
bestanden** (`alignedForNaming`). Eine behaltene Originalfassung ist **heruntergeladen, nicht erfolgreich**
(`DownloadedWithoutSignificance`) — die Datei liegt, das Paar bleibt offen (F-M452). Ist Auto-Sync aus,
gibt es keinen Fit und damit keinen Unterschied: jeder Abruf zählt.

## „Hochgeladen" je Quelle (F-M490)

Zwei eigene Zeitstempel, `SubdlUploadedAt` und `TvSubtitlesUploadedAt` (Schema 12, in `sidecars` **und**
`embeds`). Ein Zeitpunkt statt eines Schalters — „hochgeladen" ohne „wann" beantwortet die halbe Frage,
und eine Zeile kann **beide** Quellen tragen: im März zu SubDL, im November zu tvsubtitles.

Gesetzt wird nur der Zeitstempel der Quelle, die den Upload wirklich genommen hat; ein Zeitstempel wird
nie mit einem leeren Wert überschrieben. `IsUploadedTo` liest zuerst den Zeitstempel, dann das Kennzeichen
der Quelle (F-M488) — eine Zeile mit Id, aber ohne Zeitstempel (Schema-11-Altdaten) gilt weiter als
hochgeladen. F-M489 hängt damit nicht mehr daran, ob eine Id vorhanden war.

## Kein Reup, je Quelle (F-M489)

„Schon hochgeladen" wird aus dem **eigenen Kennzeichen der Quelle** gelesen, nicht aus `Status=Uploaded`
(das heißt nur „irgendwo hochgeladen"). Damit: kein Reup von SubDL auf SubDL, kein Reup von tvst auf tvst —
und trotzdem blockiert keine Quelle die andere.

Die Entscheidung fällt an einer Stelle, `UploadGate.Decide(Schalter, Herkunft, hat-SubDL-ihn, hat-tvst-ihn)`,
und wird an drei Stellen benutzt: Sammelstufe für eingebettete Spuren, Sammelstufe für lose Dateien, Upload
selbst. Gründe im Log: `already-uploaded:subdl`, `already-uploaded:tvsubtitles`,
`already-uploaded:subdl+tvsubtitles`, `cross-source`, `no-upload-source`.

Altzeilen ohne eigenes Kennzeichen gelten für SubDL als hochgeladen, wenn `SubdlId` **und**
`Status=Uploaded` stehen — sonst ginge nach dem Upgrade jeder alte Upload ein zweites Mal hinaus.

## Der nächste Schritt (nicht Teil dieses Schnitts)

`Wired = true` für tvsubtitles setzen heißt: die Pipelines rufen den Katalog statt des SubDL-Clients.
Das ist die Einbindung in den Lauf — bewusst noch nicht gemacht, weil die Bestellung „den Rest gleich
lassen" lautete.
