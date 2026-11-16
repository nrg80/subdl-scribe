# tvsubtitles.net — Protokoll und Methoden

Stand 16.11.2026. Gilt für `Jellyfin.Plugin.SubdlSync/Api/TvSubtitlesClient.cs` (F-M480–F-M482, F-M516).

Die Seite hat **keine API**. Geprüft am 15.11.2026: `/api*`, `/xmlrpc.php` und `api.tvsubtitles.net`
antworten mit 404 bzw. lösen nicht auf; `/rss.xml` gibt es (RSS 2.0), es trägt aber keine
Folgendaten. Alles läuft deshalb über die Seiten, die die Navigation der Seite selbst anbietet —
gelesen wird HTML, geschrieben wird ein Formular.

## Der Weg (Download)

```
/search2.php?qs=<name>          Suche: /tvshow-<id>.html samt Zeitraum (F-M516 — weich!)
/add.html                       Auswahlliste aller Serien  → Show-Id
/tvshows.html                   Tabelle aller Serien: tvshow-<id>-<staffeln>.html (nicht immer aktuell)
/tvshow-<show>.html             Staffeln der Serie         → Staffelnummern
/tvshow-<show>-<staffel>.html   Folgenseiten der Staffel   → Seiten-Ids  (KEINE Folgennummern)
/episode-<seite>.html           "2x24" in der Überschrift  → Staffel/Folge
                                + die Einträge (je Eintrag Sprache, Rip, Release, Datum)
/subtitle-<id>.html             Detailseite des Eintrags   (muss zuerst geholt werden)
/download-<id>.html             Zip — oder eine Seite, die den Dateinamen im JavaScript trägt
```

**Was an diesem Weg weh tut, und wie es gelöst ist:**

- **Die Sprache steht nicht im Text.** Verlässlich ist allein das Flaggenbild
  `images/flags/<code>.gif`. Es muss **je Block** gelesen werden (von einem Untertitel-Anker bis zum
  nächsten), sonst erbt ein Eintrag die Flagge des folgenden und wird falsch einsortiert.
  Einträge ohne Flagge werden übersprungen: eine unbekannte Sprache lässt sich keiner Zielsprache
  zuordnen.
- **Die Seiten-Ids sind keine Folgennummern**, und die Staffelliste läuft absteigend. Die Nummer
  kommt aus der Überschrift der Folgenseite (`<h2>The Middle 2x24</h2>`), mit Rückfall auf das erste
  `NxM` der Seite — das Markup hat schon einmal gewechselt.
- **Der Dateiname steht nicht im Link.** Die Download-Seite baut ihn in JavaScript aus nummerierten
  Fragmenten (`var s1='files/The Middle_2x24'; var s2='_HDTV.FEVER.en.zip';`), die in Reihenfolge
  zusammengesetzt werden.
- **Die Download-Adresse antwortet in zwei Formen** (gemessen 15.11.2026): mal mit der
  „einen Moment"-Seite, mal mit dem **Zip direkt**. Der Client entscheidet an den Bytes, die
  tatsächlich kamen (`PK\x03\x04` = Zip, sonst HTML parsen) — nicht an einem Content-Type, den die
  Seite nicht immer setzt.
- **Die Bytes werden unangetastet weitergegeben.** Die Seite liefert je Sprache UTF-8 neben cp1250,
  cp1251, cp1253, cp1254 und cp1256. Genau hier ist am 15.11.2026 der Codepage-Fehler entstanden, der
  Russisch, Griechisch und Arabisch unlesbar machte: `cp1252` scheitert praktisch nie und frisst
  alles. Dekodiert wird deshalb in der Pipeline, nicht im Transport.

## Der Weg (Upload)

`POST /add1.php`, `multipart/form-data`, Referer `/add.html`. Die Felder sind **wörtlich** die des
Formulars auf `/add.html`:

| Feld | Inhalt |
|---|---|
| `tvshow` | Show-Id aus der Auswahlliste; `0` = keine Serie — wird verweigert |
| `season` | 1–22 |
| `episode` | 0–100 |
| `double` | `1` bei Doppelfolge (nur setzen, wenn zutreffend) |
| `lang` | Sprachkürzel **der Seite**, nicht ISO (siehe unten) |
| `rip` | `WEB HDTV HEVC DVDRip DVDSCR BDRip BluRay` oder leer |
| `release` | Freitext (Release-Gruppe, z. B. `FEVER`) |
| `hi` | `1` bei hörgeschädigter Fassung (nur setzen, wenn zutreffend) |
| `upfile` | Datei, `.srt` oder `.zip` |
| `comment` | **trägt hier die Kennungen und den Client**: `TMDB 1422 / IMDb tt1442464 / Sub Scribe 12.1.12.380` |
| `go` | versteckt, `1` |
| `author` | **wird nicht mitgeschickt** — ein fehlender Teil ist der einzige Weg, eine Vorbelegung der Seite nicht zu überschreiben |

Beispiel der Seite selbst: `Supernatural.S04E09.HDTV.XviD-NoTV.avi` → Serie Supernatural,
Staffel 4, Folge 9, Rip HDTV, Release NoTV.

**Sprachkürzel sind die der Seite:** `en es fr de br ru ua it gr ar hu pl tr nl pt sv da fi ko cn jp
bg cz ro` — vier davon sind **kein ISO** (`gr`, `cz`, `cn`, `jp`). Ein Kürzel, das die Auswahlliste
nicht kennt, lässt das Formular auf seinen ersten Eintrag zurückfallen — der Upload landet dann als
Englisch. Der Client prüft deshalb gegen die Tabelle und schickt kein unbekanntes Kürzel.

**ISO wird abgebildet, und zwar nur hier (F-M502):** `TvSubtitlesClient.SiteLanguage` zieht einen
ankommenden Code auf das Kürzel der Seite — `el`/`ell`/`gre` → `gr`, `cs`/`ces`/`cze` → `cz`,
`zh`/`zho`/`chi` → `cn`, `ja`/`jpn` → `jp` (die vier Abweichungen in beiden Schreibweisen), dazu die
ISO-639-2-Formen der übrigen Seiten-Sprachen (`deu`/`ger` → `de`, `fra`/`fre` → `fr`, `spa` → `es`,
`pob` → `br`, `ukr` → `ua`, `nld`/`dut` → `nl`, `ron`/`rum` → `ro` …). Groß-/Kleinschreibung zählt
nicht, Region und Skript fallen weg (`zh-Hans` → `cn`, `ja-JP` → `jp`) — **außer** `pt-BR`/`pt_BR`:
das ist brasilianisches Portugiesisch (`br`), während `pt` und `pt-PT` bei `pt` bleiben. Unbekannt
heißt `null` (kein Raten) und wird mit einer Zeile abgelehnt. Dieselbe Brücke benutzen die Prüfung in
`BuildUpload`, das Feld `lang` und die Rücklesung `FindUploadedIdAsync`; der Dateiname behält seine
ISO3-Endung aus der Tabelle (`gre`, `cze`, `chi`, `jpn`, `pob`, `rum`). Eine gemeinsame Tabelle mit
SubDL gibt es **nicht** — SubDL bildet in `SubdlApiClient` ab.

**Serie neu anlegen (F-M516, belegt am 16.11.2026):** `GET /add-tvshow.php` liefert das Formular —
und es steht, wie das Upload-Formular, in einer kaputten Verschachtelung (`<table><form …>`), die
Feldnamen gelten trotzdem:

```html
<form action="/add-tvshow1.php" method="post" enctype="multipart/form-data">
  <input name="name"    type="text"  style="width:230px">
  <input name="year_s"  type="text"  maxlength=4 size=4>
  <input name="seasons" type="text"  maxlength=2 size=2>
  <input type="submit"  value="Send">          <!-- ohne name: geht NICHT mit -->
  <input name="go"      type="hidden" value="1"> <!-- hinter der Absende-Zeile, noch im Formular -->
</form>
```

| Feld | Art | Inhalt |
|---|---|---|
| `name` | Text | Serienname, wie er in der Liste stehen soll |
| `year_s` | Text, `maxlength=4` | Jahr der Erstausstrahlung |
| `seasons` | Text, `maxlength=2` | Staffelzahl — zwei Ziffern, also 1..22 (dieselbe Grenze wie `MaxSeason`) |
| `go` | versteckt | `1` |
| (Absende-Knopf) | submit | **kein `name`** → kein Feld im Rumpf |

Kein Captcha, kein Token, kein Konto, kein Kommentarfeld, keine externe Kennung — der Name ist der
einzige Schlüssel, deshalb der Doppeleintrags-Schutz über den engen Namensvergleich. `enctype` ist
**`multipart/form-data`**; ein URL-kodierter Rumpf wäre hier derselbe Fehler, den die Filmseite
(`add-movie.php`) gerade gezeigt hat, und wird deshalb gar nicht erst gebaut.

**Was die Seite antwortet — an einer echten Anlage gemessen** (`Weissensee`, 2010, 4 Staffeln,
16.11.2026):

```
POST /add-tvshow1.php            → HTTP 302, Rumpf leer
   Location: https://www.tvsubtitles.net/add-tvshow.php?done=yes
GET  /add-tvshow.php?done=yes    → HTTP 200, im Artikel:
   <div class="subtitle2"><b>TV-Show has been added. Thank you.</b></div>
```

- **Erfolg** ist die 302 auf `?done=yes` (bzw. der Satz, wenn der Aufrufer der Weiterleitung folgt).
  `TvSubtitlesClient.ClassifyAddShow` erkennt **nur** dieses Kennzeichen — Ziel-URL oder Text.
- **Die Antwort trägt keine Id**, in keiner Form.
- **„Gibt es schon“ und eine Ablehnung sind NICHT belegt.** Ein zweiter POST hätte entweder einen
  Doppeleintrag erzeugt (eine Anlage ist auf der Seite nicht löschbar) oder nichts bewiesen. Deshalb
  behauptet der Client nur den Erfolg; alles andere ist `unclear` (kein Kennzeichen) oder `refused`
  (Fehlerstatus), und die Entscheidung fällt am Bestand, nicht am Wortlaut.
- **Der Zähler „TV Shows“ in der Seitenleiste bewegt sich nicht** (vor und nach der Anlage 2768) —
  eine Anlage am Zähler abzulesen ist kein Beleg.

**Wie man danach an die Seriennummer kommt:**

| Weg | Aufruf | Was drin steht |
|---|---|---|
| Suche | `GET /search2.php?qs=<name>` | `<a href="/tvshow-4242.html">Weissensee (2010-2026)</a>` — Id **und** Name |
| Auswahlliste | `GET /add.html`, `<select name="tvshow">` | `4242` → `Weissensee` (der Client: `ShowsAsync`) |
| Alle Serien | `GET /tvshows.html` | Rang · `tvshow-<id>-<staffeln>.html` · Name · Staffeln · Folgen · Untertitel · Jahre |

Die Suche ist der kürzeste Weg und zeigt die frische Serie sofort — sie ist aber **weich**
(`Pagan Peak` → *Twin Peaks*, *Peaky Blinders*), ihr Ergebnis läuft deshalb durch denselben engen
Vergleich wie die Liste, und „kein Treffer“ ist kein Beweis. Das Auswahlfeld von `/add.html` enthielt
die neue Serie unmittelbar danach ebenfalls (4242) — es ist der Rückfall. `/tvshows.html` führte sie
**noch nicht** (2.772 Zeilen vor und nach der Anlage): dieser Katalog wird nicht bei jeder Anlage neu
geschrieben und taugt nicht als Beleg. Die Staffelzahl wurde übernommen: `/tvshow-4242.html`
antwortet 302 auf `tvshow-4242-4.html`, die Seite heißt „… season 4“ und verlinkt die Staffeln 1–4.

**Wer anlegt, steht fest (F-M501):** `TvstShowResolver` — und zwar **nur** mit dem TMDb-Titel, dem
TMDb-Erstausstrahlungsjahr und der TMDb-Staffelzahl (auf 1..22 begrenzt). Die Nummer der Serie steht
danach in `MediaEntity.TvstShowId` auf derselben Ebene wie `TmdbId`/`ImdbId` und wird beim nächsten Mal
gelesen statt gesucht. Angelegt wird **höchstens einmal je Medium und Lauf**, und nur, wenn die Seite
vorher sauber geantwortet hat; im Trockenlauf nie. Nach der Anlage gilt (F-M516): Nummer suchen
(Suche, dann Liste) und **nur bei einem Treffer speichern**; bestätigt die Seite die Anlage, steht die
Serie aber nicht in der Liste, lautet das Ergebnis ehrlich `created-id-unknown` und es wird **nichts**
gespeichert; bestätigt sie nicht und die Serie steht trotzdem da, war sie schon vorhanden
(`existed`) — die Nummer wird gespeichert, ein zweiter Eintrag entsteht nicht.

## Grenzen und offene Punkte

- **Keine Quote, keine dokumentierte Drosselung.** Die Seite nennt kein Limit; der Client hält sich
  an die allgemeine Pause des Plugins (`MinCallPauseSec`), wenn er eingebunden wird.
- **Kein Login sichtbar.** `/login.html`, `/upload.html`, `/panel.html` → 404. **Serien anlegen geht
  ohne Konto** (16.11.2026 belegt: der POST wurde angenommen, die Serie stand sofort da); für den
  Upload ist es weiterhin ungeprüft: ein leerer POST antwortet mit HTTP 200 und leerem Rumpf, ein
  Captcha war nicht sichtbar.
- **Der Client ist inzwischen eingebunden** (F-M485): die zwei Schalter steuern Abruf und Upload
  wirklich, und die Herkunft jedes Funds steht mit der Id der Quelle im Store (F-M486), damit ein
  tvst-Fund nie zu SubDL hochgeladen wird (F-M487). Grenzen bleiben: **Staffelpacks und der HI-Pool
  sind SubDL-Sache**, diese Quelle kann nur Einzelfolgen.

## Methoden

Alle Parser sind `static` und nehmen HTML als Zeichenkette — die Suite `scripts/tests/tvsubtitles`
fährt sie mit den Seiten, die die Seite am 15.11.2026 wirklich geliefert hat (`fixtures/`), nicht
mit der lebenden Seite.

| Methode | Zweck |
|---|---|
| `ShowsAsync()` | `id → Name` aus der Auswahlkiste von `/add.html` (~2.770 Serien) |
| `SearchShowsAsync(query)` / `ParseShowSearch(html)` | Seitensuche: `id → Name` (Jahreszeitraum abgeschnitten) — **weich**, Ergebnis eng vergleichen (F-M516) |
| `ResolveShow(shows, name)` | Id zu einem Namen, **eng verglichen** (F-M501): Groß-/Kleinschreibung, `&` = `and` = `und`, Leerraum — sonst Gleichheit. Kein Teilstring, kein Ähnlichkeitsmaß |
| `NormalizeShowName(name)` | der Vergleichsschlüssel selbst (F-M501) — die drei Angleichungen, nachprüfbar |
| `SeasonsAsync(showId)` | Staffelnummern, die eine Seite haben |
| `EpisodePagesAsync(showId, season)` | Seiten-Ids einer Staffel, ohne Dubletten |
| `EpisodeLabelAsync(pageId)` | `(Staffel, Folge)` aus der Überschrift der Folgenseite |
| `EntriesAsync(pageId)` | Einträge: `SubtitleId`, `Language`, `Rip`, `Release`, `Age` |
| `DownloadAsync(subtitleId)` | `(Dateiname, Bytes)` — erstes `.srt` aus dem Zip |
| `Comment(tmdbId, imdbId, extra)` | Kommentarzeile `TMDB … / IMDb … / Sub Scribe <Version>` |
| `BuildUpload(...)` | Feldliste prüfen und bauen; sendet nichts |
| `BuildUploadBody(upload)` | Multipart-Rumpf und Boundary, byte-genau |
| `UploadAsync(upload, dryRun)` | senden; **ohne `dryRun: false` verlässt nichts den Prozess** |
| `BuildAddShow(name, year, seasons)` | die vier Formularfelder (`name`, `year_s`, `seasons`, `go`), `seasons` 1–22 (F-M501/F-M516) |
| `AddShowAsync(addShow, dryRun)` | senden (multipart, Referer `/add-tvshow.php`); Trockenlauf ist die Vorgabe; liefert Status, `Location`, Ziel-URL, Text und den **klassifizierten** Ausgang (F-M516) |
| `ClassifyAddShow(status, location, url, text)` | `created` nur bei `done=yes`; `refused` bei Fehlerstatus; sonst `unclear` — mehr ist nicht belegt (F-M516) |
| `AddShowConfirmed(html)` | steht „TV-Show has been added.“ in der Seite? (F-M516) |
| `IsZip(bytes)` | Zip oder HTML — die Seite antwortet in beiden Formen |

Live-Probe (nur lesend, nicht Teil der Prüfungen):

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 /opt/data/.dotnet/dotnet run \
  --project scripts/tests/tvsubtitles/tvsubtitles.csproj -c Release -- /opt/data/subdl-scribe --live
```

Sie zeigt: die Serienliste (am 16.11.2026: 2.774 Einträge im Auswahlfeld, einschließlich des
Platzhalters `0`), The Middle = 512, das Ergebnis der Seitensuche für „The Middle", Staffeln 1–9,
24 Folgenseiten in Staffel 2, Seite 31911 = 2x24 mit 3 Einträgen und den Download eines englischen
Eintrags. Sie **legt nichts an** — die Anlage ist ausdrücklich kein Teil der Probe.
