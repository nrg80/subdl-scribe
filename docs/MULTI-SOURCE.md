# Mehrquellen-Architektur — SubDL und die tvst-Familie

Stand 20.11.2026, Plugin 12.2.0.14. Dieses Blatt zieht zusammen, was in
[`REQUIREMENTS.md`](REQUIREMENTS.md) über viele Nummern verteilt steht: wie sich die Quellen
zueinander verhalten, was „erledigt" heißt, und wie Download und Upload mit mehreren Quellen
umgehen. Es ist die eine Stelle, an der die Quellenfrage als Ganzes nachzulesen ist.

## 1. Zwei Quellen, nicht zwei Wahrheiten

Es gibt **zwei** Upload-Quellen:

- **SubDL** — API, liefert Kennungen zurück, kennt Einzel- und Staffel-Uploads.
- **die tvst-Familie** — `tvsubtitles.net` (Serien) und `moviesubtitles.org` (Filme), derselbe
  Betreiber, kein Konto, keine Kennung in der Antwort. Die Familie ist *in sich* zweigeteilt; welche
  Adresse ein Medium bekommt, entscheidet **TMDb/der Store** (Serie → tvsubtitles, Film →
  moviesubtitles), nie der Titel und nie der Dateiname.

Die Reihenfolge-Einstellung gilt für die **Familie**, nicht für die einzelne Adresse: „tvst zuerst"
heißt beim Film moviesubtitles zuerst, bei der Serie tvsubtitles zuerst. Und die Oberfläche hat
**einen Schalter je Richtung** — die beiden Adressfelder sind Datenfelder, keine Bedienelemente
(F-M482, F-M496, F-M504).

## 2. Der Grundsatz: was bei einer Quelle erledigt ist, ist bei der anderen offen

Das ist die Regel, aus der alles andere folgt, und sie ist mehrfach gebrochen worden:

| Sachverhalt | Regel | Nummer |
|---|---|---|
| Hochgeladen | je Quelle eigenes Kennzeichen (`ScribeUploadId`, `TvSubtitlesUploadId`) — „irgendwo hochgeladen" ist kein Beleg | F-M488, F-M489 |
| Dublette | gilt nur für **dieselbe** Quelle: was SubDL hat, geht trotzdem zu tvst | F-M582 |
| Ablehnung | sperrt nur die Quelle, die sie ausgesprochen hat | F-M584 |
| Staffelpack | ein Pack erledigt das Paar nur bei **seiner** Quelle; die Staffel ist erst erledigt, wenn **jede** Quelle des Slots ihren eigenen Pack hat | **F-M654**, F-M571, F-M573 |
| Status | nennt offene Sprachen je Quelle, nicht als Summe | F-M570 |
| Herkunft | ein Fund wird nie hochgeladen — weder zurück zur eigenen Quelle noch zur anderen | F-M487 |

**Der Pack-Fall ist der teuerste gewesen.** Bis 12.2.0.13 las der Plan die quellenblinde Summe
`PacksBySeason()`; ein SubDL-Pack beendete die Staffel auch für tvsubtitles. Gemessen am 20.11.2026:
**36 Staffeln mit 177 Paaren** endeten früh mit „already up as a pack", und tvsubtitles hatte für
diese Staffeln **0** Packs. Seit 12.2.0.14 bildet der Plan die **Schnittmenge** über die Quellen des
Slots (`PacksOfAllSlotSources`), und die Staffelzeile nennt die Quellen:
`all N known pair(s) already up as a pack for subdl, tvsubtitles`.

Der Pack-Schlüssel trägt die Quelle mit (`<imdb>|S<n>|<lang>|<hi|plain>|<source>`), jede Quelle hält
ihre eigene Kennung (F-M642). tvst-Behälter tragen **keine** Kennung, weil die Seite keine
zurückgibt (F-M612) — der Beleg ist dort die Zeile selbst.

## 3. Upload: ein Slot je Paar, zwei Quellen gleichzeitig

Ein **Paar** ist (Serie, Staffel, Sprache, HI-Variante). Bis 12.2.0.8 liefen zwei getrennte
Pack-Phasen — erst SubDL, dann tvst —, und die Mitglieder wurden zweimal gesammelt und zweimal
beurteilt. Seit 12.2.0.9 gilt (F-M647):

1. **Einmal sammeln, einmal urteilen.** `FillAsync` baut aus den Mitgliedern *einen* Plan mit beiden
   Behältern und beiden Dateinamensformen.
2. **Je Quelle senden, gleichzeitig.** `SendAsync` läuft je Quelle, gestartet über `Task.WhenAll`,
   die zweite Quelle mit **0–5 % Versatz** des Transfertakts (bei 400/h bis 0,45 s) statt Gleichstart
   (F-M587). Gemessen im Betrieb: 270–429 ms Versatz.
3. **Ein Fehler einer Quelle reißt die andere nicht mit** — das Ergebnis wird zurückgegeben, nicht
   geworfen; je Quelle eigenes Erfolgskennzeichen und eigener Zeitstempel.
4. **Eine Zeile je Paar** (F-M648, F-M651), mit beiden Seiten: `"subdl: already up as a pack; tvst
   sent"`. Zwei Formulierungen für denselben Befund sind ein Fehler, nicht eine genauere Auskunft.
5. **Nichts extrahieren, was keine Quelle mehr will** (F-M650): steht eine Ablehnung mit derselben
   Staffel-Signatur im Store, endet das Paar vor dem Sammeln und Entpacken.
6. **Ein Kontingent-Stopp ist kein Urteil** (F-M649): nichts wird gespeichert, das Paar bleibt offen.
7. **Die Quellensteuerung bleibt Pflicht.** Wer „schon gepackt" fragt, fragt je Quelle; wer eine
   Zeile schreibt, nennt die Quelle **ausdrücklich** — `RecordPack(..., source)` hat keinen
   brauchbaren Vorgabewert für neue Aufrufe, ein vergessenes Argument schreibt sonst stillschweigend
   `subdl`.

Für Einzel-Uploads gilt dasselbe Muster: ein Transfer, zwei Quellen, ein Versatz (F-M587); die
Prüfungen je Quelle (Schalter, Zugangsdaten, Tor) laufen **vor** dem Versand in Kettenreihenfolge.

## 4. Download: nacheinander in Reihenfolge — der Gegensatz zum Upload

**Der Download fragt die Quellen NACHEINANDER, nicht gleichzeitig.** Die eingestellte Reihenfolge ist
Daten, kein Sonderfall: `DownloadPipeline` läuft `foreach (var quelle in quellenReihenfolge)`, und
innerhalb einer Quelle wird gesucht, bevor die nächste gefragt wird. Es gibt dort **kein**
`Task.WhenAll` und kein `Parallel.ForEach` — anders als beim Upload, wo die beiden Versandwege
gleichzeitig laufen. Wer „paralleler Download" erwartet, findet ihn heute nicht; die Reihenfolge ist
die einzige Steuerung.

Warum das so ist und was daran hängt:

- Die Reihenfolge bestimmt die **Abfrage-Reihenfolge**, nicht nur den Rang der Treffer. Eine Quelle
  mit ausgeschaltetem Schalter wird gar nicht erst gesucht (F-M477, F-M495).
- Eine Quelle, die in diesem Lauf schon stumm geblieben ist, wird **nicht erneut** gefragt (F-M592):
  sonst kostet jeder weitere Artikel den Timeout von 90 s ohne Aussicht auf Antwort.
- Fällt die Hauptsuche (SubDL) in den Timeout, wird die nächste Quelle gefragt statt den Lauf zu
  beenden (F-M591).
- Das Ergebnis je Quelle wird **zusammengeführt**, nicht ersetzt: Rangfolge beginnt mit der
  Quellenreihenfolge, dann Pack und Score.
- Die Kandidaten werden **zusammengeführt**: tvst verdrängt SubDL nicht mehr, die Rangfolge beginnt
  mit der Quellenreihenfolge, dann Pack und Score.
- **Ein Ablehnungswort einer Quelle beendet die Suche der anderen nicht** — sonst käme die zweite
  Quelle nie zum Zug.
- Ein vorzeitiger Ausstieg nach „erste Quelle leer" ist der klassische Fehler: dann steht im Log
  `no candidates on SubDL for [DE]`, obwohl tvst nie gefragt wurde. Beleg ist die per-Quelle-Zeile
  (`[tvsubtitles-D] … — N candidate(s)`), nicht die Sammelzeile.
- Der Kopf einer Datei entscheidet die Medienart **aus dem Store** (TMDb-korrigiert), nicht aus dem
  Item-Typ; nur der Dateikopf des Upload-Laufs nimmt den Item-Typ, weil dort noch nichts aufgelöst ist.

**Offen, nicht gebaut:** ob der Download die Quellen gleichzeitig fragen soll. Das wäre eine Änderung
an der Abfrage-Reihenfolge und am Timeout-Verhalten (eine stumme Quelle dürfte die andere nicht
bremsen), keine Beschreibung des Ist-Stands — und sie ist nicht entschieden.

## 5. Was das im Betrieb bedeutet

Ein Lauf, zwei Etappen — die Pack-Phase zuerst, dann die Einzelphase (F-M429):

- **Pack-Phase**: vollständige Staffeln, ein Zip je (Sprache, Variante), an **jede** Quelle des Slots.
- **Einzelphase**: alles Übrige, je Datei und Sprache ein Urteil, wieder je Quelle.
- Ein Ankunftslauf packt nicht; die Bibliothek gehört dem vollen Lauf (F-M424). tvst ist im Slot nur,
  wenn der volle Lauf läuft **und** ihr Upload-Schalter an ist (F-M495).

Messwerte vom 20.11.2026 als Maßstab: 1.331 Videodateien, 113 lückenlose Staffeln, 811 Paare im
Dateisystem, 540 für tvst tauglich — davon 397 in Staffeln, die schon vollständig gepackt waren,
25 verschickt, 143 offen (unvollständige Staffeln oder gemischte Releases). Von den 25 gingen alle an
tvst, keiner an SubDL (SubDL lehnte jeden Behälter als `Duplicate upload: identical file already
published` ab).

## 6. Prüfungen, die diese Architektur festhalten

| Prüfung | Datei |
|---|---|
| Quellen-Trennung im Plan, Schnittmenge statt Summe | `scripts/tests/seasonpack/SeasonPackRun.cs` (10b) |
| „schon gepackt" je Quelle, Quellenliste in der Zeile | `scripts/tests/packretry/PackRetryRun.cs` |
| ein Slot je Paar, beide Quellen, eine Zeile | `scripts/tests/seasonpack/SeasonPackRun.cs` (10) |
| Kennzeichen je Quelle, kein Reup, kein Cross-Block | `scripts/tests/endpoints/EndpointsRun.cs` |
| Kommentar und Felder der tvst-Nutzlast | `scripts/tests/tvsubtitles/` |
