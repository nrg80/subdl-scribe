# moviesubtitles.org — Protokoll und Methoden

Stand 15.11.2026. Aufgenommen **ausschließlich lesend**: kein POST an `add.php`, `add-movie.php` oder
`rate.php`, kein Upload, kein Film angelegt, keine Anmeldung versucht. Geschrieben wurde nichts —
auch die Suche nicht, obwohl sie ihr Formular per POST abschickt und dabei nichts anlegt (geprüft,
siehe unten). Alles, was trotzdem unsicher bleibt, steht unter
[Was NICHT belegt ist](#was-nicht-belegt-ist) — nicht als Vermutung getarnt, sondern offen.

Die Seite hat **keine API**: `/api.php`, `/sitemap.xml`, `/rss.xml` antworten mit 302 auf `/`;
`/admin.php` ist ein Betreiber-Login (ukrainisch, cp1251), kein Nutzerkonto. `/login.php`,
`/panel.php`, `/upload.php` → 302 auf `/`. Alles läuft über HTML und die Formulare der Seite.

Layout, CSS-Klassen (`left_articles`, `description`, `stat`, `footer`) und Formularsprache sind die
von tvsubtitles.net, und im Kopf steht der Verweis „TV Show subtitles" auf die Schwester. Es ist
dieselbe Familie — dieselbe Bauart, andere Daten.

## Zuordnung

**Die Regel: TMDb entscheidet, welche Quelle gefragt wird — nicht der Dateiname, nicht der
Ordnername, nicht der Bibliotheksname.**

**Und zwar innerhalb EINER Quelle.** Auf oberster Ebene gibt es **zwei** Quellen: SubDL und die
**tvst-Familie**. Die Familie hat **zwei Adressen** — tvsubtitles.net für Serien, moviesubtitles.org
für Filme —, und diese Seite ist die Film-Adresse davon. Die Reihenfolge-Einstellung ordnet die
Familie gegen SubDL („tvst zuerst" / „SubDL zuerst"), nicht die Adressen gegeneinander; sie bleibt
deshalb zweiwertig, und bei einem Film heißt „tvst zuerst" **moviesubtitles.org zuerst**.

| TMDb-Datensatz | Quelle |
|---|---|
| `movie` | **moviesubtitles.org** — und nur diese; tvsubtitles.net wird für Filme nicht gefragt |
| `tv` | **tvsubtitles.net** — und nur diese; moviesubtitles.org wird für Serien nicht gefragt |
| beides | **SubDL** bleibt für beides zuständig; SubDL unterscheidet nicht |

Der Grund, warum die Auflösung über TMDb laufen muss und nicht über den Namen: **derselbe Name
existiert auf beiden Seiten und meint dort verschiedene Werke.** `Dune` ist auf moviesubtitles.org
der Film von 1984 (`movie-607.html`), im Serienkatalog von tvsubtitles.net die Fernseh-Miniserie von
2000 (`Dune`, daneben `Children of Dune` und `Dune: Prophecy`). Aus „Dune" allein folgt keine Quelle.

**Woran man auf der Filmseite sicher erkennt, dass ein Treffer ein Film ist** (alles gemessen, nicht
angenommen):

- **Es gibt keine Serienadressen.** `/tvshow-512.html` und `/episode-31911.html` — die Adressen, die
  auf tvsubtitles.net eine Serie und eine Folge bezeichnen — antworten hier mit **302 auf `/`**.
- **Keine Serie steht in der Liste.** `add.php?filter=` liefert 0 Treffer für „game of thrones",
  „the office", „the simpsons", „chernobyl", „stranger things", „peaky blinders", „succession",
  „better call saul", „breaking bad s01". „breaking bad" trifft nur `El Camino: A Breaking Bad
  Movie (2019)` — einen Film.
- **Jede Zeile trägt eine vierstellige Jahreszahl und sonst nichts.** Kein Staffel-, kein
  Folgenfeld, nirgends auf der Seite — auch nicht im Upload-Formular (`tvid`, `lang`, `rip`,
  `release`, `author`, `UFile`…, `comments`).
- **Die Seite selbst sagt „movie".** `<title>` einer Filmseite endet auf `… movie`, die Überschrift
  lautet `Download <Titel> (<Jahr>) subtitles`. Auf tvsubtitles.net steht an derselben Stelle
  `… season 9` bzw. `… episode 2x24` — die beiden Schwesterseiten unterscheiden sich also schon im
  Seitentitel.
- **Die Eigenwerbung zählt nicht als Beleg.** „movies and TV films" auf der Startseite meint
  Fernsehfilme; ein Serienangebot ist daraus nicht ableitbar und wurde auch nicht gefunden.

Was die Seite damit **nicht** leistet: sie sagt nicht, *welcher* Film gemeint ist, wenn Titel und
Jahr mehrdeutig sind (siehe „Mehrdeutigkeit" unten). Die Film-Id ist eindeutig, der Name nicht.

## Der Weg (Download)

| Schritt | Adresse | Was daraus kommt |
|---|---|---|
| suchen | `GET /search.php?q=<titel>` | Trefferliste `Titel (Jahr)` → `/movie-<id>.html` — **antwortet mit HTTP 500**, Rumpf trotzdem brauchbar |
| oder filtern | `GET /add.php?filter=<titel>` | dieselbe Auswahl in der Upload-Sicht: `add.php?tvid=<id>` + Jahr |
| oder blättern | `GET /movies-<A…Z\|0>.html` | die Filmliste je Anfangsbuchstabe (487–1.395 Filme, eine Seite, keine Blätterung) |
| Filmseite | `GET /movie-<id>.html` | alle Einträge des Films, nach Sprache in Abschnitte gruppiert |
| Eintrag | `GET /subtitle-<id>.html` | Rip, Release, `filename:`, Größe, Datum, kompatible Releases, Link `download-<id>.html` |
| Datei | `GET /download-<id>.html` | **302** → `Location: files/<…>.zip` (relativ) |
| Bytes | `GET /files/<…>.zip` | `application/zip`, 1–3 `.srt` darin |

Beispiel, vollständig durchgezogen am 15.11.2026:

```
search.php?q=oppenheimer      →  movie-24811.html      „Oppenheimer (2023)"
movie-24811.html              →  subtitle-132989.html  „english … (WEB-DL.MA)"
subtitle-132989.html          →  download-132989.html
download-132989.html          →  302 files/Oppenheimer_2023.WEB-DL.MA.en.zip
files/…zip                    →  92.522 B, ein Eintrag: Oppenheimer.WEB-DL.MA.en.srt (246.829 B, UTF-8 mit BOM, CRLF)
```

**Was an diesem Weg weh tut, und was daraus folgt:**

- **`search.php` antwortet mit 500.** Bei allen sieben geprüften Abrufen (fünf Suchbegriffe, GET
  und POST, mit und ohne Referer), mit und ohne Treffer. Der Rumpf ist trotzdem die richtige
  Trefferliste; er bricht nur hinter der Filmliste ab — dort, wo die Untertitel-Zweitliste anfängt
  (`</ul><br><ul style="margin-left:2em">` und dann nichts). Der Statuscode taugt hier also **nicht**
  als Fehlerzeichen, der Inhalt schon.
- **Titel stehen invertiert.** Die Seite speichert `Matrix, The (1999)`, `Lord of the Rings: The Two
  Towers, The (2002)`, `Old Man and the Sea, The (1999)` — 52 von 492 O-Filmen enden auf `, The/A/An`.
  `filter=The Matrix` liefert deshalb **0 Treffer**, `filter=Matrix, The` genau einen. Ein Auflöser
  muss beide Formen probieren (oder auf den Kern ohne Artikel ausweichen: `filter=Matrix` → 5
  Treffer, dann Jahr und Name prüfen).
- **Der Filter ist reiner Teilstring, irgendwo im Titel.** `filter=LOTR` trifft `Allotria
  (Allegria)`; `filter=dark` trifft 162 Filme; ein angehängtes Jahr hilft nicht (`Oppenheimer 2023`
  → 0 Treffer). Groß-/Kleinschreibung zählt nicht (`oppenheimer`, `dune`, `matrix` trafen jeweils).
  Es gibt **keine** Obergrenze: `filter=the` liefert 6.674 Zeilen in einer Antwort (548 KB).
- **`tvid` und die Film-Id sind dieselbe Zahl.** `movie-<id>.html`, `add.php?tvid=<id>` und der
  Rückweg von der Untertitel-Seite (`Back to <Film>` → `movie-<id>.html`) tragen sie; sie ist global
  und stabil (Oppenheimer 24811, Matrix, The 4, Ocean's Thirteen 239). Es gibt keine zweite
  Kennung und keine Staffel-/Folgenebene.
- **Die Download-Adresse antwortet mit 302, nicht mit Bytes.** `Location` ist **relativ**
  (`files/…zip`) und kann Klammern tragen — gemessen: `files/Matrix_The_1999.ru(2).zip`, weil es den
  Namen schon gab. Ein Client muss die Adresse auflösen und die Klammern mitnehmen.
- **Unbekannte Ids sehen wie Erfolg aus.** `download-999999.html` → 302 auf `files/` (leer, kein
  Dateiname) — das ist die Fehlersignatur. `subtitle-999999.html` → **HTTP 200** mit leerem Titel,
  leerer Sprache und „size: 9140 kb"; das ist keine Antwort, das ist Müll. Beides muss geprüft
  werden, bevor daraus ein Eintrag im Store wird.
- **Das Zip enthält 1 bis 3 Dateien, und `parts` ist nur ein Hinweis.** `parts`=3 (Eintrag 9077)
  → drei Dateien `Matrix The.DVDRip.KiSS.part1/2/3.en.srt`; `parts`=2 (Eintrag 2058, russisch)
  → **eine** Datei `Matrix The (1999).ru.srt`. Ein vollständiger Filmuntertitel ist also nicht
  automatisch „die Datei im Zip": bei mehreren Teilen sind es Segmente, die erst aneinandergehängt
  einen Film ergeben — und jedes Segment trägt seine eigenen Zeitangaben.
- **Der Dateiname im Zip folgt keinem festen Muster.** Beide Formen kamen vor:
  `<Titel>.<Rip>.<Release>.<lang>.srt` (Oppenheimer) und `<Titel> (<Jahr>).<lang>.srt`
  (Matrix, russisch). Der Name im Zip ist **nicht** identisch mit dem Namen des Zips.
- **Das Jahr kann doppelt in der Überschrift stehen.** `movie-16856.html` heißt „O Beautiful (2002)
  (2002)", weil der Titel die Jahreszahl schon enthielt. Wer das Jahr aus der Überschrift liest,
  nimmt die **letzte** Klammer mit vier Ziffern, nicht „alles in der letzten Klammer".
- **Ein Film ohne Untertitel ist keine 404.** `movie-6928.html` (Old Man and the Sea, The, 1999)
  antwortet 200 mit „0 subtitles downloaded  times" — zwei Leerzeichen, wo sonst die Zahl steht.
  Filme ohne Untertitel sind in der Upload-Liste, aber **nicht** in der Filmliste (492 vs. 487 beim
  Buchstaben O, 1.407 vs. 1.395 beim A): `movies-<L>.html` zeigt nur Filme mit mindestens einem
  Eintrag, `add.php?let=<L>` alle.

**Mehrdeutigkeit.** Es gibt keine Suche nach Jahr, keinen Wunsch-Id-Parameter und keine
Disambiguierungsseite. `filter=dune` gibt fünf Filme: drei „Dune"-Verfilmungen (1984, 2021, 2024),
den Dokumentarfilm `Jodorowsky's Dune` (2013) und `Suna no onna (Woman of the Dunes)` (1964). Die
Entscheidung fällt also im Client: Kandidaten sammeln, Jahr aus der Zeile neben dem Link
vergleichen, und wenn zwei Kandidaten dasselbe Jahr tragen, den Titel eng vergleichen (nicht
ähnlich) und sonst **nicht** raten. Genau die Regel, die `TvstShowResolver` für Serien schon
anwendet.

## Der Weg (Upload)

`GET /add.php?tvid=<id>` zeigt das Formular für genau diesen Film; `POST /add.php`,
`enctype="multipart/form-data"`, Ziel ist `add.php` (relativ, also `/add.php`). Die Felder sind
wörtlich die des Formulars:

| Feld | Typ | Inhalt |
|---|---|---|
| `tvid` | hidden | Film-Id; steht schon im Formular (`24811`) |
| `lang` | select | Sprachkürzel der Seite, 13 Werte (siehe Tabelle) |
| `rip` | select | `DVDRip BluRay BrRip BDRip HDRip WEB WEBRip WEB-DL` oder leer |
| `release` | text | Freitext (Release-Gruppe), z. B. `MA`, `YTS`, `KiSS` |
| `author` | text | Freitext — **Pflicht oder nicht: ungeprüft**, siehe unten |
| `UFile` | file | Datei 1 |
| `UFile2` | file | „only if 2 and more CD" |
| `UFile3` | file | „only if 3 CD" |
| `comments` | textarea | Freitext |
| `go` | hidden | `1` |

**Kein `accept=` an den Dateifeldern, kein `MAX_FILE_SIZE`, kein Token, kein Captcha, kein
Referer-Zwang erkennbar.** Das Formular ist ein nacktes Multipart-Formular — anders als bei
tvsubtitles.net fehlt hier sogar das `hi`-Kennzeichen für hörgeschädigte Fassungen.

**Was das Formular über die Annahme verrät: nichts.** Es gibt keinen Hinweis auf erlaubte Endungen,
keine Größenangabe, keinen Moderationstext. Die drei Dateifelder sind als „CD"-Teile deklariert
(„only if 2 and more CD") — die Seite denkt also in Teilen, wie die `parts`-Spalte der Einträge
zeigt. Ob `UFile` allein genügt, ob `.srt` und `.zip` beide gehen und ob `author`/`comments` Pflicht
sind, lässt sich **nur durch einen echten Upload** klären, und der war für diesen Auftrag
ausgeschlossen.

**Film neu anlegen:** `GET /add-movie.php` → Formular nach `/add-movie.php` (POST, multipart) mit
`name`, `year` (`maxlength=4`), `go=1`. **Gebaut seit F-M513** (Etappe 3), **live belegt seit F-M515**
(16.11.2026): der Client baut genau diese Nutzlast, und er liest die Antwort an zwei belegten
Markern — siehe den Abschnitt „Live-Beleg 16.11.2026" weiter unten.
Die Seite warnt selbst: „Before adding new Movie please
check if it already exist" — das ist ein Hinweis, **kein** technischer Doppeleintrag-Schutz; ob die
Seite einen zweiten Eintrag mit gleichem Namen und Jahr ablehnt, ist ungeprüft. Derselbe Vorbehalt
wie bei tvsubtitles.net: erst auflösen, dann anlegen, und nur wenn die Suche sauber geantwortet hat.

**Wie man die `tvid` eines Jellyfin-Films findet** — belegt am Beispiel „Oppenheimer (2023)":

```
GET /add.php?filter=oppenheimer
  → <li><a href="add.php?tvid=24811"><b>Oppenheimer</b></a> 2023</li>      (genau ein Treffer)
GET /add.php?tvid=24811
  → <b>Movie:</b> Oppenheimer (2023)   … Formular
```

Gegenprobe „The Matrix" (1999): `filter=The Matrix` → 0 Treffer; `filter=Matrix` → 5 Treffer, davon
`Matrix, The (1999)` = `tvid=4`. Der Weg führt also über den **Kern** des Titels und dann über den
Jahr-Vergleich, nicht über den vollen TMDb-Titel.

## Tabellen

### Sprache — die 13 Werte von `<select name="lang">`

Wortlaut der Seite, in der Reihenfolge des Formulars. Es sind **genau 13** — die Auswahlliste ist
kürzer als die von tvsubtitles.net, nicht länger, und sie enthält kein `select`-`multiple`: eine
Sprache je Upload.

| Wert | Anzeige | Flagge | Einträge (Site-Statistik) |
|---|---|---|---|
| `en` | english | `images/flags/en.gif` | 46.909 |
| `es` | spanish | `es.gif` | 6.982 |
| `fr` | french | `fr.gif` | 9.148 |
| `de` | german | `de.gif` | 1.102 |
| `br` | portugese(br) | `br.gif` | 7.772 |
| `ru` | russian | `ru.gif` | 2.876 |
| `ua` | ukrainian | `ua.gif` | 174 |
| `it` | italian | `it.gif` | 1.599 |
| `gr` | greek | `gr.gif` | 9.976 |
| `ar` | arabic | `ar.gif` | 5.280 |
| `hu` | hungarian | `hu.gif` | 3.804 |
| `pl` | polish | `pl.gif` | 3.320 |
| `tr` | turkish | `tr.gif` | 7.082 |

**Diese 13 Kürzel stehen in der tvsubtitles-Liste in identischer Schreibweise** (dort sind es 23).
Die Brücke `TvSubtitlesClient.SiteLanguage` (F-M502) deckt sie damit vollständig ab — auch die
Abweichungen, die kein ISO sind: `gr` statt `el`, `ua` statt `uk`, `br` für brasilianisches
Portugiesisch. Was hier fehlt und dort existiert (`nl pt sv da fi ko cn jp bg cz ro`), wird **nicht**
angeboten; ein Zielcode außerhalb dieser 13 hat auf dieser Seite keine Sprache und darf nicht
hochgeladen werden. Die Zahl in der letzten Spalte ist die Selbstauskunft der Seite (`add.php`,
Block „Site statistics"), nicht nachgezählt.

Die Abschnittsüberschriften der Filmseite sind die Anzeigenamen derselben Liste, nur mit zwei
Leerzeichen vor `subtitles:` — `English`, `Portugese(br)` (so geschrieben), `Ukrainian` usw. Sie
erscheinen **nur für Sprachen, in denen der Film Einträge hat**; `movie-4.html` hat 12 Abschnitte
(kein Ukrainisch), `movie-239.html` alle 13.

### Rip — die Werte von `<select name="rip">`

`(leer)` — im Formular **zweimal**, als erster und als letzter Eintrag —, `DVDRip`, `BluRay`,
`BrRip`, `BDRip`, `HDRip`, `WEB`, `WEBRip`, `WEB-DL`. Neun Werte, dazu der leere Fall.

**Das ist nicht die Liste von tvsubtitles.net** (`WEB HDTV HEVC DVDRip DVDSCR BDRip BluRay`).
Gemeinsam sind nur `WEB`, `DVDRip`, `BDRip`, `BluRay`. Eine Rip-Tabelle muss also je Quelle geführt
werden — `HDRip`, `BrRip`, `WEBRip`, `WEB-DL` gibt es dort nicht, `HDTV`, `HEVC`, `DVDSCR` hier
nicht. Und: die Auswahlliste ist **nur eine Empfehlung für neue Uploads**. Bestehende Einträge
tragen daneben freie Werte — auf `movie-4.html` stehen `1080p`, `720p`, `1080p.BlueRay`,
`720p.BlueRay`, `BrRip`, `BDRip`, `DVDRip` und leer. Wer liest, muss also mit allem rechnen; wer
schreibt, hält sich an die neun.

### Staffel/Folge

Gibt es nicht. Die Seite kennt **nur** `tvid`. Kein Feld, keine Spalte, keine Adresse.

### Spalten eines Eintrags auf der Filmseite

Je Eintrag (`<div class="subtitle">`): Flagge (`images/flags/<code>.gif`, `alt` = Kürzel), Link
`/subtitle-<id>.html`, Titel `<Titel> <Jahr> <sprache> subtitles (<Rip>.<Release>)`, Bewertung
(rot/grün), und eine Tabelle mit Rip, Upload-Datum (`07.08.2024 02:48`), Größe (`90.35 kb`),
Release, `parts` (1–3), Downloads. **Der Rip steht also als eigene Spalte in der Liste**, das Release
ebenfalls — aber das Release ist oft leer, während es im Titel in Klammern steht. Verlässlich ist der
Titel: er trägt `(<Rip>.<Release>)` vollständig, notfalls als `()`.

**Die Sprache steht dreifach da** — Abschnittsüberschrift, Flagge, Wort im Titel — und in allen 108
geprüften Einträgen (`movie-4`, `movie-239`, `movie-24811`) stimmten alle drei überein. Anders als
bei tvsubtitles.net, wo allein die Flagge zählt und die Blockgrenze über die Zuordnung entscheidet,
gibt es hier also Redundanz zur Kontrolle.

## Seiten-Eigenheiten

- **Zeichensatz:** `<meta … charset=utf-8>` und der Körper ist auch UTF-8 — **außer** einzelnen
  Fragmenten in **cp1251**: dem LiveInternet-Zähler (ein `escape("Жж"+…)` im JavaScript) und
  `/admin.php` (ukrainisch). Der HTTP-Kopf setzt **kein** `charset` (`Content-Type: text/html`).
  Wer den ganzen Rumpf strikt als UTF-8 dekodiert, scheitert an genau diesen Bytes (Position 10.803
  auf `subtitle-2058.html`); wer cp1252 nimmt, macht dafür alle Titel kaputt. Die Bytes der
  Untertitel-Dateien sind davon unberührt: **UTF-8, CRLF** — mit BOM nur bei der neueren Datei
  (Oppenheimer 2024), ohne BOM bei den drei älteren (Matrix 2007/2008).
- **Weiterleitungen:** `http://` → 301 → `https://www.`; `moviesubtitles.org` ohne `www` → 301 →
  `https://www.moviesubtitles.org/`. `/movies.html` ist **kein** Redirect, sondern die Filmliste
  (569 KB für A) — die frühere Notiz „leitet auf die Startseite um" ließ sich am 15.11.2026 nicht
  reproduzieren, mit Browser-Kennung kamen 200 und 569.197 Bytes Inhalt. `/tvshow-…`, `/episode-…`,
  `/login.php`, `/panel.php`, `/upload.php`, `/sitemap.xml`, `/rss.xml`, `/api.php` → 302 auf `/`.
- **`robots.txt`: HTTP 200, 0 Bytes.** Keine Regel, kein Verbot — und damit auch keine Erlaubnis,
  mehr zu ziehen als nötig.
- **Drosselung:** nicht beobachtet, aber auch nicht ausgeschlossen. Sechs Anfragen auf dieselbe
  Filmseite in Folge: alle 200, 0,57–0,88 s. Die Seite ist langsam und liefert unkomprimiert
  (eine Buchstabenliste 200–570 KB), das ist der eigentliche Kostenfaktor. Für den Client gilt die
  allgemeine Pause des Plugins (`MinCallPauseSec`).
- **Kein Login, kein Konto.** Es gibt nichts anzumelden und folglich auch keine „meine Uploads"-
  Seite. Was hochgeladen wurde, liest man **von der Filmseite zurück** (`movie-<id>.html`) oder
  allenfalls auf `/new.html` (letzte Untertitel) — mehr Rücklesemöglichkeit gibt es nicht.
- **Ohne Browser-Kennung** lieferten Startseite und Filmseite **dieselben Bytes** wie mit
  (`19401` zu `19401`, `104870` zu `104870`; der einzige Unterschied war ein zweistelliger Zähler
  tief in einem Skript). Die Seite prüft den `User-Agent` also nicht. Der Auftrag verlangte trotzdem
  die Browser-Kennung, und die wurde bei jedem Abruf gesendet.
- **Werbung:** mehrere Fremdskripte (`cognatesyringe.com`, `acint.net`, LiveInternet) mitten im
  Inhalt. Ein Parser darf sich auf **keine** Position im Dokument verlassen, nur auf Klassen und
  die Tabellenstruktur.

## Was NICHT belegt ist

Alles hier ist **ungeprüft**, weil dafür ein Schreibzugriff nötig gewesen wäre — und der war für
diesen Auftrag ausgeschlossen.

- **Ob `add.php` überhaupt annimmt.** Es gibt keinen Hinweis auf Moderation, keinen Dankestext, kein
  Captcha — aber auch keinen Beleg, dass ein Upload ohne Anmeldung durchgeht. Bei tvsubtitles.net
  lautet der Verdacht „nimmt an, moderiert vielleicht"; hier ist noch nicht einmal das geprüft.
- **Wie die Antwort auf einen Upload aussieht.** Ob HTTP 200 mit Text, ob eine Weiterleitung, ob
  eine neue Untertitel-Id zurückkommt (bei tvst kommt keine), ob es einen Bestätigungstext wie
  „Thank you. Your subtitles will be placed on site very soon." gibt — **unbekannt**.
- **Ob `UFile` allein reicht**, ob `UFile2`/`UFile3` nur bei Mehrteiler-Filmen gefüllt werden dürfen,
  und ob ein Upload mit drei Dateien als ein Eintrag mit `parts`=3 erscheint. Die Spalte `parts`
  lässt es vermuten; belegt ist es nicht.
- **Welche Endungen die Seite nimmt.** Das Formular sagt nichts (kein `accept=`). `.srt` und `.zip`
  sind bei den *herunterladbaren* Dateien belegt, nicht beim Hochladen.
- **Ob `author` und `comments` Pflicht sind** und was passiert, wenn sie fehlen. Kein `required`,
  kein Sternchen — mehr weiß man erst nach einem POST.
- **Ob der Dateiname des Uploads übernommen wird.** Die Dateien im Zip heißen
  `<Titel>.<Rip>.<Release>.<lang>.srt` bzw. `<Titel> (<Jahr>).<lang>.srt`, also **serverseitig aus
  Titel, Rip, Release und Sprache gebaut**. Es sieht so aus, als würde der eigene Dateiname
  verworfen — bewiesen ist das nicht.
- **Ob die Seite Doppeleinträge verhindert** (gleicher Film, gleiche Sprache, gleicher Rip, gleiches
  Release). `add-movie.php` warnt nur vor dem Anlegen eines doppelten *Films*; für Untertitel gibt
  es keinen sichtbaren Hinweis.
- **Ob der 500 auf `search.php` dauerhaft ist** oder eine kaputte Installation, die sich mit einem
  Update ändert. Er war am 15.11.2026 bei jeder Abfrage da.
- **Wie die Seite auf Umlaute und diakritische Zeichen reagiert** (`Amélie` gegen `Amelie`) — nur
  Groß-/Kleinschreibung ist als unabhängig belegt, nicht Akzentfaltung.
- **Ob es ein Rate-Limit über längere Zeit gibt.** Sechs Anfragen sind keine Messung.

## Umgesetzt in Etappe 1

**`Jellyfin.Plugin.SubdlSync/Api/MovieSubtitlesClient.cs`** — der Client der dritten Quelle, Bauform wie
`TvSubtitlesClient`: alle Parser `static` und ohne Netz, dazu die async-Abrufe. Geprüft von der Suite
**`scripts/tests/moviesubtitles/`** (`MovieSubtitlesRun.cs`, inzwischen 84 Prüfungen gegen die Fixtures in
`fixtures/`, `--live` liest nur), gebunden als **T228** und beschrieben als **F-M506** in
`docs/REQUIREMENTS.md`. **Nachtrag F-M510/T232:** dieselbe Suite prüft seither auch den TMDb-Titel-Jahr-
Vergleich (Klammer-Jahr abgetrennt, nur für den Vergleich) — 9 Prüfungen mehr, zusammen **93**.

Enthalten sind: die beiden eigenen Tabellen (13 Sprachen, neun Rip-Werte), die Sprachbrücke
`SiteLanguage` (verweigert, was die 13 nicht kennen — inklusive `pt`/`por`, denn die Seite führt nur die
brasilianische Fassung), `RipFromQuality` (Quelle vor Codec, **kein** Codec-Rückfall), `ParseMovieList`
für alle drei Listensichten, `ResolveMovie`/`TitleKey` (Artikel-Inversion, Jahr, kein Raten),
`ParseMoviePage` (Sprache aus der Flagge), `ParseSubtitlePage`, `ParseDownloadTarget`/`DownloadUrl`,
`DownloadAsync` (302, Bytes entscheiden, Mehrteiler gekennzeichnet) sowie `BuildUpload`,
`BuildUploadBody` und `UploadAsync` (Trockenlauf als Vorgabe, `dryRun` kommt vom Aufrufer).

**Nicht enthalten und ausdrücklich Etappe 2:** jede Verdrahtung — kein Konfigurationsfeld, keine
Store-Spalte, kein Endpunkt, kein Eintrag im Quellenkatalog. `BuildAddMovie` und die Anlage kamen erst
mit F-M513 (Etappe 3). Auf die Seite wurde **nicht schreibend** zugegriffen.

**Zwei Befunde aus dem Bau, die das Protokoll oben präzisieren:**

- **Das Rip-Auswahlfeld führt acht benannte Werte, nicht neun** — der leere Eintrag steht zweimal
  (erster und letzter Option), also ergeben sich neun *verschiedene* Werte. Die Tabelle im Client und
  die Prüfung in der Suite zählen den leeren mit; die Zeile „Neun Werte, dazu der leere Fall" oben
  zählt ihn doppelt.
- **`x265` ergibt hier den leeren Rip, nicht `HEVC`.** `HEVC` ist ein Wert von tvsubtitles.net; dieses
  Auswahlfeld kennt keinen Codec. Die Quelle im selben Namen greift weiter (`BluRay.x265` → `BluRay`),
  ein Codec allein bleibt leer — die Seite speichert dann keinen Rip, und das ist ein erlaubter Wert.

## Umgesetzt in Etappe 2b — der Upload-Weg (gesperrt)

**Gebaut ist der ganze Weg, scharf ist er nicht.** Nutzerauflage 15.11.2026: „Bevor wir falsche
Sprachen hochladen, laden wir nix hoch." Der Weg wird deshalb gebaut, geprüft und **gesperrt**
ausgeliefert; freigegeben wird er **erst nach belegter Sprachabbildung**.

### Was gebaut ist

| Stück | Wo | Was es tut |
|---|---|---|
| Endpunkt | `Api/Endpoints/MovieSubtitlesEndpoints.cs`, `MovieSubtitlesUploadEndpoint` | `IUploadEndpoint`, `Provider = MovieSubtitles`, `Wired = true`. Nimmt das Ticket, lehnt Staffel/Folge und Staffelpack ab, prüft die Sprache gegen die 13 Kürzel, nimmt `MsubMovieId` aus dem Ticket oder löst über Titel **und** Jahr auf |
| Nutzlast | `MovieSubtitlesClient.BuildUpload` (schon in Etappe 1) | Felder in der Reihenfolge des Formulars: `tvid lang rip release`, `author` **nur wenn gesetzt**, `UFile` (`UFile2`/`UFile3` nur bei Mehrteiler), `comments`, `go=1`. `release` ist die Release-**Gruppe** aus dem Release-Namen, nie der Dateiname |
| Rumpf | `MovieSubtitlesClient.BuildUploadBody` | Multipart mit eigenem Boundary; die Bytes der Datei stehen unverändert darin |
| Senden | `MovieSubtitlesClient.UploadAsync(upload, dryRun)` | Trockenlauf ist der **Vorgabewert**: derselbe Rumpf, kein Byte auf die Leitung |
| Auflösung | `MsubMovieResolver` (Etappe 2a) | gespeicherte Nummer führt; sonst enge Suche über TMDb-Titel und TMDb-Jahr; die Nummer landet in `MediaEntity.MsubMovieId` |
| Kette | `Pipeline/UploadPipeline.cs` | **eine** Schleife über die zuständigen Quellen (Serie: tvsubtitles.net, Film: moviesubtitles.org — je neben SubDL), je Quelle eigener Schalter, eigenes Tor, eigener Versand, eigenes Ergebnis, **eigene Marke** und **eigener Zeitstempel**. Die alte Bauform „SubDL, sonst alles andere" ist weg — sie hätte einen Film zur Serien-Adresse geschickt |
| Tor | `Api/Endpoints/UploadGate.cs` | `Decide(...)` kennt die Film-Adresse (`MovieSubtitles`, `AllowedCount`), `May(provider)` ist die eine Lesestelle der Kette |
| Store | `Registry/ContentHashRegistry.cs` | `IsUploadedTo`/`IsContentUploadedFor` lesen die Spalten der Film-Adresse, `RecordSkippedContent` schreibt ihren Zeitstempel, `MarkEmbed`/`MarkSidecar` nehmen `moviesubtitlesUploadId`/`moviesubtitlesUploadedAt` |
| Schalter | `Configuration/PluginConfiguration.cs` | `MovieSubtitlesDownloadEnabled` (null → **an**), `MovieSubtitlesUploadEnabled` (null → **aus**) |
| GUI | `Configuration/configPage.html` | Die tvst-Familie steht als **eine Quelle mit zwei Adressen**: eine gemeinsame Überschrift, darunter je Seite ein Schalterpaar (Download/Upload). Das Auswahlfeld der Reihenfolge bleibt **zweiwertig** (SubDL zuerst / tvst zuerst) |

### Was der Endpunkt bei einer Antwort ohne Id tut

Was die Seite bei Annahme sagt, ist **nicht belegt** (siehe den Abschnitt „Was NICHT belegt ist"). Der
Endpunkt erfindet deshalb weder eine Id noch eine Moderation:

- **HTTP 2xx** → `Ok = true`, `Id = null`, der Text der Antwort geht **unverändert** durch
  (nur Tags/Whitespace zusammengezogen). Es gilt „**angenommen, Id unbekannt**". Die Kette vergibt
  daraufhin die eigene Kennung `own:moviesubtitles:<Zeit>:<hex>` (F-M488) — „hochgeladen, Id unbekannt"
  ist belegbar, „Id 12345" wäre eine Behauptung.
- **kein 2xx** → `Ok = false`, `Reason = "HTTP <Status>"`, der Text der Seite geht mit. Eine Ablehnung,
  die wir erfinden, wäre so falsch wie eine Annahme, die wir erfinden.
- **Trockenlauf** → `Ok = true`, `DryRun = true`, `Id = null`, `Answer = null`, aber `Bytes` = die Größe
  des Rumpfs, der gesendet **würde**.

### Die Sperre

- `MovieSubtitlesUploadEnabled` ist `bool?` und im ungesetzten Fall **aus**; `IsUploadEnabled` liest das
  als einzige Stelle.
- Ist der Schalter aus, steht eine Zeile im Lauf: `source moviesubtitles skipped: that source is
  switched off, nothing sent` (im Trockenlauf: `[SubDL] DRY-RUN … — source moviesubtitles not sent:
  that source is switched off`). Die Quelle fehlt also nicht still.
- **Kein POST gegen die Seite, auch nicht als Test.** Der Endpunkt wird ausschließlich gegen Fixtures
  und einen Zähl-Handler gefahren; angelegt wurde kein Film, angemeldet wurde sich nirgends.

### Was auch jetzt nicht belegt ist

Die Sperre bleibt, bis **die Sprachabbildung** gegen die echte Seite belegt ist — nicht nur die
Tabelle, sondern die Wirkung: dass ein Hochladen mit dem Kürzel der Seite als diese Sprache erscheint.
**Beleg 16.11.2026:** der Film-Upload (Paddington 2, Film 23716, Eintrag 133347) steht öffentlich auf
der Seite, mit `Rip WEB` und dem Kommentar `IMDb tt4468740 / Sub Scribe 12.1.12.397` — für EN ist die
Wirkung damit gezeigt. Die Sperre selbst bleibt der Schalter: er ist im ungesetzten Fall weiter aus.
Unbelegt bleiben weiterhin: ob `add.php` überhaupt ohne Anmeldung annimmt, wie der Antworttext bei
Annahme lautet, ob moderiert wird, ob `author`/`comments` Pflicht sind, welche Endungen die Seite
nimmt, ob der Dateiname übernommen wird und ob Doppeleinträge verhindert werden. **Erst nach belegter
Sprachabbildung freigeben.**

## Umgesetzt in Etappe 3 — Film anlegen (F-M513)

**Nutzerauftrag 16.11.2026 (wörtlich): „Dann einmal mit neu anlegen testen".** Ein Film, den die Seite
nicht führt, wird beim Upload angelegt und der Untertitel dazu hochgeladen. Gebaut und offline geprüft
ist der Weg; **ein POST gegen die Seite ist in diesem Auftrag nicht gefallen** — den fährt der
Live-Test.

### Was gebaut ist

| Stück | Wo | Was es tut |
|---|---|---|
| Nutzlast | `MovieSubtitlesClient.BuildAddMovie(name, year)` | Felder `name`, `year`, `go=1` in der Reihenfolge des Formulars; **Prüfung vor dem Senden**: leerer Name oder Jahr außerhalb 1888..laufendes Jahr+3 → `ArgumentException`, keine Anfrage |
| Rumpf | `MovieSubtitlesClient.BuildAddMovieBody` | multipart mit eigenem Boundary — Trockenlauf und Ernstfall bauen **denselben** Rumpf |
| Senden + Rücklesen | `MovieSubtitlesClient.AddMovieAsync(payload, dryRun)` | `POST /add-movie.php` (Referer: das Formular); bei 2xx wird die Id **nachgesehen**, nicht geraten |
| Doppeleintrag-Schutz | `MovieSubtitlesClient.CheckForAddAsync` / `ResolveForAdd` | **eine** Suchrunde (Titel, Titel ohne Artikel, invertierte Form) entscheidet, ob angelegt werden darf |
| Anlage im Upload-Pfad | `MsubMovieResolver.ResolveOrCreateForMediaAsync` | die einzige Fassung, die anlegen darf; die Download-Fassung (`ResolveForMediaAsync`) legt nie an |
| Kette | `Pipeline/UploadPipeline.cs` | ruft die anlegende Fassung **nur** im Film-Zweig, also nur bei eingeschaltetem Upload-Schalter der Quelle; der Trockenlauf baut und meldet, sendet nichts |
| Store | `MsubMovieId` | die neue `tvid` geht **sofort** über dieselbe Methode (`Persist`) hinein wie eine gefundene Nummer |

**Prüfungen:** die Suite `scripts/tests/moviesubtitles` zählt jetzt **123** — 84 aus Etappe 1, 9 aus
F-M510 (T232), 5 aus F-M511 (T233), 15 aus F-M513 (T235), 6 aus F-M515 (T237) und **4 aus F-M517
(T239)**. Sie fährt ausschließlich gegen Fixtures, Zähl-Handler und seit F-M517 einen Wegwerf-Store im
Temp-Verzeichnis; `--live` liest weiterhin nur.

### Wie der Doppeleintrag-Schutz entscheidet

Drei Fälle, und nur diese — geprüft wird **einmal**, vor dem Anlegen:

1. **Titel mit gleichem Jahr vorhanden** (genau eine Zeile) → diese Nummer nehmen, **nichts anlegen**.
2. **Titel vorhanden, aber anderes Jahr** (oder Jahr unbekannt, oder mehr als eine Zeile) → **nicht**
   anlegen und **nicht** hochladen; die Logzeile nennt das gefundene Jahr
   (`… die Seite führt den Titel schon — gefundenes Jahr 2019, 1 Treffer; nichts angelegt, kein
   Upload`). Der Nutzer will keine Doppel — also lieber kein Upload als ein zweiter Eintrag.
3. **Kein Treffer, aber mindestens eine lesbare Antwort** → anlegen.

Antwortet die Suche **gar nicht lesbar**, gilt „gibt es nicht" nicht: dann wird ebenfalls nichts
angelegt (`unclear`). Die Entscheidung (`take`/`refuse`/`create`/`unclear`, gefundenes Jahr,
Trefferzahl) steht im Ergebnisobjekt, nicht nur im Log.

### Wie die neue Id gelesen wird — und was „Id nicht lesbar" heißt

Was die Seite auf den POST **antwortet, ist unbelegt**. Deshalb wird die Antwort nicht gedeutet: nach
einem 2xx läuft dieselbe enge Suche noch einmal, und die Nummer kommt aus ihrer Trefferliste. Nennt
die Nachsuche den Film nicht, lautet das Ergebnis **`angelegt, Id nicht lesbar`** — mit dem Text der
Antwort daneben, ohne Nummer und ohne Upload (ohne `tvid` gibt es kein Formular). Ein HTTP-Fehler ist
`Ok = false` mit dem Status als Grund. Die Logzeilen:

```
[moviesubtitles] 'Paddington in Peru' (2026) neu angelegt — Id 4711
[moviesubtitles] 'Dune' (2026): die Seite führt den Titel schon — gefundenes Jahr 2021, 1 Treffer; nichts angelegt, kein Upload (F-M513)
[moviesubtitles] 'X' (2026) wurde angelegt, aber die Nachsuche nannte keine Id — nichts gespeichert, kein Upload (F-M513)
[moviesubtitles] DRY-RUN: 'X' (2026) würde angelegt — 341 Bytes Formular-Rumpf, nichts gesendet (F-M513)
```

### Was weiterhin unbelegt bleibt — und die Annahme im Code

- **Die Antwort auf `POST /add-movie.php`** — **seit F-M515 belegt** (16.11.2026, siehe den
  Live-Beleg unten): `Movie has been added. Thank you.` bzw. `This Movie already exist`. Ein dritter
  Satz (z. B. „Jahr ungültig") ist **nicht** belegt; der Client behandelt ihn deshalb als Ablehnung.
- **Die Annahme, auf der die Rücklesung steht:** dass die Seite einen angenommenen Film **sofort** und
  unter **demselben Titel und Jahr** in ihrer Suche führt. **Live widerlegt** (16.11.2026): die Seite
  legte den Film an und führte ihn nirgends — Suche, Buchstabenliste und `add.php?filter=` blieben
  leer. Der Client meldet deshalb „angelegt, Id nicht lesbar" und lädt nichts hoch.
- **Ob die Seite ein Doppel technisch ablehnt** — **belegt**: sie antwortet `This Movie already exist`
  und legt kein zweites Mal an. Der Satz kommt aber auch für einen Film, den sie sonst nirgends führt.
- **Ob ein angelegter Film sofort Einträge/Seiten hat** (also `movie-<id>.html` antwortet) — die Seite
  antwortet auf `movie-26230.html` mit dem Jahr `(1994)` und **leerem Namen**; ob das der angelegte
  Film ist, ist **nicht titelbelegt**.

## Live-Beleg 16.11.2026 — die Antwort auf `add-movie.php` (F-M515)

Aufgenommen mit `curl`, Browser-UA, `Referer: /add-movie.php`, kein `-L`. Die Antworten liegen
unverändert als Fixtures: `add-movie-added.html` (Erfolg), `add-movie-exists.html` (verweigert);
`add-movie.html` (8399 Bytes) ist das Formular selbst.

| Fall | Request | Antwort |
|---|---|---|
| Formular | `GET /add-movie.php` | **200, 8399 Bytes**; `<form action="/add-movie.php" method="post" enctype="multipart/form-data">`, Felder `name`, `year` (`maxlength=4`), `go=1`, Submit `Send` |
| falscher Weg | `POST`, `application/x-www-form-urlencoded`, `go=Add movie` | 200, **dasselbe Formular**, keine Meldung, kein Eintrag — **keine** Anlage |
| Erfolg | `POST`, multipart, `name=The Little Rascals`, `year=1994`, `go=1` | **200, 8467 Bytes**, `<div class="subtitle2"><b>Movie has been added. Thank you.</b></div>`, **kein `Location`**, keine Id |
| verweigert | derselbe POST ein zweites Mal | **200, 8459 Bytes**, `<div class="subtitle2"><b>This Movie already exist</b></div>` |

**Der gewählte Film** war `The Little Rascals (1994)` — vorher belegt als fehlend: `POST search.php`
mit `q=Little Rascals` und `q=Rascals` → 0 Treffer, `movies-L.html` und `movies-R.html` ohne
„Rascal", `add.php?filter=Rascals` → 0 Zeilen; auf der Maschine liegt er als
`/data/movies/The.Little.Rascals.1994/The.Little.Rascals.1994.1080p.BluRay.x265-RARBG.mp4`
(1379629713 Bytes).

**Und danach?** Die Nachsuche fand ihn **nicht** (`q=Little Rascals` → 0), `movies-L.html` blieb
byte-gleich (478353 Bytes, nur die „Page generation"-Zeit änderte sich), `add.php?filter=Rascals`
blieb bei 0 Zeilen, die Statistik bei `Movies: 23368`, und `movie-26231.html` und folgende
existieren nicht — die neueste Seite `movie-26230.html` trägt `(1994)` mit **leerem** Namen. Der
Doppeleintrag-Schutz der Seite sieht den Film also (zweiter POST → „already exist"), die öffentlichen
Listen nicht. **Eine Id ist damit nicht titelbelegt** — der Client behauptet keine und lädt nichts
hoch. Dasselbe Bild beim Film `Project Hail Mary (2026)`: „This Movie already exist", aber weder in
`search.php` noch in `movies-P.html` (443 KB) noch in `add.php?filter=`.

**Was das für die Kette heißt:** Anlegen gelingt (Meldung belegt), aber der Untertitel kann
**nicht** folgen, solange keine `tvid` lesbar ist. Der Client sagt das und tut nichts weiter; ein
zweiter Anlage-Versuch fällt nie.

## F-M517 — die Nummer überlebt auch ohne Medien-Hash

**Beleg (echter Lauf 16.11.2026, 04:13, jf-test, `jf-run.log`):**

```
UploadPipeline: [moviesubtitles] ?: 'Hotel Transylvania 2' (2015) ist Film 13392 — nicht gespeichert:
kein Medien-Hash bekannt — die Nummer gilt nur für diesen Lauf.
```

Der Upload lief danach durch — nur die **Nummer** war weg.

**Warum.** Der Medien-Hash kommt aus `ContentHashRegistry.GetMediaHash(mediaPath)`; die Methode
antwortet `null`, sobald die Datei nicht lesbar ist. Genau das war hier der Fall: der Pfad
`media/movies-test/Hotel Transylvania 2 (2015)/Hotel Transylvania 2 (2015).mkv` ist ein **Symlink ohne
Ziel** (`readlink -f` → `/data/movies/…ShAaNiG.mkv`, `test -e` → nicht vorhanden) — also kein Hash,
obwohl die Zeile über `Path` adressierbar wäre. `MsubMovieResolver.Persist` kehrte bei fehlendem Hash
**stumm** zurück (die Spalte `MsubMovieId` wurde nie erreicht), und die Logzeile sagte das als
„nicht gespeichert". Derselbe Weg schrieb auch die Seriennummer (`TvstShowId`) — der Serienfall hatte
dasselbe Loch.

**Was jetzt gilt.**

| Stück | Wo | Was es tut |
|---|---|---|
| Schreibweg | `ContentHashRegistry.SetMsubMovieId` / `SetTvstShowId` (ein gemeinsamer `SetMediaNumber`) | Hash zuerst (Zeile per `Id`); sonst die Zeile über ihren **Pfad** (`MediaEntity.Path`) — dieselbe Spalte, nur eine andere Adresse |
| Eindeutigkeit | dieselbe Methode | kein Hash **und** keine Pfad-Zeile → `None` (nichts geschrieben); **mehrere** Zeilen mit dem Pfad → ebenfalls `None` (kein Raten, keine Wahl) |
| Leseweg | `MsubMovieIdAtPath` / `TvstShowIdAtPath` | ohne Hash wird die gespeicherte Nummer über den Pfad **gelesen** — sonst wäre sie für jeden Folgelauf unsichtbar |
| Auflöser | `MsubMovieResolver` / `TvstShowResolver` (neuer Parameter `mediaPath`) | reicht den Pfad durch; `Persist` gibt zurück, **wie** geschrieben wurde |
| Logzeile | `Api/Endpoints/MediaNumberNote` | `gespeichert (über den Medien-Hash).` / `gespeichert über den Pfad (kein Medien-Hash bekannt).` / `nicht gespeichert: kein Medien-Hash und keine eindeutige Pfad-Zeile im Store — die Nummer gilt nur für diesen Lauf.` |
| Kette | `UploadPipeline` (Film- und Serien-Adresse), `DownloadPipeline` | geben den Pfad der Mediendatei mit |

**Beleg am Store-Abbild** (Kopie des Live-Stores, `/opt/data/cache/scratch/fm517-proof/store`, nur die
Kopie beschrieben):

| Schritt | Ergebnis |
|---|---|
| Kopie unverändert | **31** Medienzeilen, **0** Zeilen mit dem Pfad des Films — diese Datei hat dort gar keine Zeile (ohne Hash wurde nie eine angelegt), der Schreibversuch ergibt `None`, 31 → **31** |
| Zeile vorhanden, Hash nicht (der beschriebene Fall) | `ByPath`, die Zeile trägt danach `MsubMovieId = 13392`, Zeilen **32 → 32** (die +1 kam vom Anlegen der Zeile für den Beleg, nicht vom Schreiben) |
| derselbe Schreibvorgang noch einmal | `ByPath`, Zeilen **32 → 32** (idempotent) |
| Lesen | über den Pfad **13392**, über den Hash **13392** |
| Auflöser echt gefahren (ohne Hash, Zeile vorhanden) | Status `found`, Id 13392, Zeilen 32 → 32, Logzeile im Wortlaut: `[moviesubtitles] ?: 'Hotel Transylvania 2' (2015) ist Film 13392 — gespeichert über den Pfad (kein Medien-Hash bekannt).` |

**Ehrlich dazu:** für *diesen* Film bleibt es auf der Testinstanz beim „nicht gespeichert", solange
keine Zeile mit seinem Pfad existiert — der Symlink zeigt ins Leere, also entsteht auch kein Hash. Der
Fix wirkt dort, wo die Zeile da ist (aus einem früheren Lauf, aus dem Einreiher, aus einer anderen
Quelle) und nur der Hash fehlt; ohne beides bleibt die Logzeile wahr.

## Was ein Client hier brauchen wird (Naht: Etappe 2)

Damit die Naht `IDownloadEndpoint`/`IUploadEndpoint` hier genauso passt wie bei
`TvSubtitlesEndpoints`, sind das die Stücke. **Nach Etappe 2a/2b/3 gilt: der Download ist verdrahtet
(F-M507), der Upload gebaut (F-M508), die Anlage gebaut (F-M513) — siehe die Abschnitte oben.
Etappe 1 hat die Methoden gebaut, Etappe 2 die Naht — Endpunkt, Konfigurationsfelder, Store-Spalte,
Quellenkatalog —, Etappe 3 die Anlage:**

| Stück | Zweck |
|---|---|
| `SearchAsync(query)` | `search.php?q=` **und** `add.php?filter=`; 500 nicht als Fehler werten, Rumpf parsen |
| `ResolveMovie(candidates, title, year)` | Kern-Titel + Jahr, Artikel-Inversion (`Matrix, The`) berücksichtigen, eng vergleichen, sonst `null` |
| `MoviesAsync(letter)` | `movies-<L>.html` → `id, Name, Jahr, Sprachen` (nur Filme mit Einträgen) |
| `EntriesAsync(movieId)` | `movie-<id>.html` → `SubtitleId, Language, Rip, Release, Parts, Age`, abschnittsweise nach Sprache |
| `DownloadAsync(subtitleId)` | `download-<id>.html` → 302 auswerten, `files/…zip` holen, `PK\x03\x04` prüfen, alle `.srt` zurückgeben |
| `BuildUpload(...)` | Felder `tvid lang rip release author UFile UFile2 UFile3 comments go` |
| `BuildAddMovie(name, year)` | `add-movie.php`; erst auflösen, dann anlegen — gebaut mit F-M513 |
| Sprachbrücke | die 13 Kürzel; wiederverwenden, was `SiteLanguage` schon kennt (Teilmenge) |
| Rip-Tabelle | eigene, neun Werte, **nicht** die von tvst |

Der Client muss außerdem wissen, dass **ein Film mit `parts`>1 mehrere Dateien** liefert: das ist
kein Pack, das ist ein geschnittener Film, und die Segmente sind nicht austauschbar.
