# moviesubtitles.org — Testsuite

Hier liegt das Protokoll der dritten Quelle: `docs/MOVIESUBTITLES.md` (URL-Kette, Formulare,
Wertelisten, offene Punkte) und die Seiten, die die Seite am 15.11.2026 wirklich geliefert hat.

**Der Code dazu ist `Jellyfin.Plugin.SubdlSync/Api/MovieSubtitlesClient.cs` (F-M506), gefahren von
`MovieSubtitlesRun.cs` in diesem Verzeichnis: 93 Prüfungen offline gegen `fixtures/`, `--live` als
Lese-Probe.** Aufruf:

```
dotnet run --project scripts/tests/moviesubtitles/moviesubtitles.csproj -c Release -- <repo>
```

**Stand: 15.11.2026, ausschließlich lesend aufgenommen.** Es wurde kein Upload gemacht, kein Film
angelegt, kein POST an `add.php` oder `add-movie.php` abgesetzt. Alles, was in
`docs/MOVIESUBTITLES.md` unter „Was NICHT belegt ist" steht, ist deshalb ungeprüft — nicht
„wahrscheinlich so".

## `fixtures/` — echte Seiten, unverändert

Rohbytes, wie sie kamen: nicht umgeschrieben, nicht repariert, nicht neu kodiert. Deshalb sind in
den Dateien die cp1251-Fragmente (LiveInternet-Zähler, `admin.php`) weiterhin kaputt, genau wie auf
der Seite. Abgerufen am 15.11.2026 zwischen 20:18 und 20:26 UTC mit
`User-Agent: Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36`.
`fixtures/MANIFEST.sha256` hält die Prüfsummen (`sha256sum -c MANIFEST.sha256` muss durchlaufen).

| Datei | Quelle (GET) | HTTP | Bytes | Was daran hängt |
|---|---|---|---|---|
| `home.html` | `/` | 200 | 19.401 | Startseite; „Latest subtitles" mit Flagge + Rip im Link; charset-Angabe im `<meta>` |
| `movies-O.html` | `/movies-O.html` | 200 | 200.017 | **Filmliste**, Buchstabe O, 487 Filme, je Zeile Name/Jahr/Downloads/Flaggen |
| — *(nur gemessen)* | `/movies-0.html` | 200 | 177.831 | Buchstabe „#"; Größe gemessen, Inhalt nicht als Fixture behalten |
| `add-let-O.html` | `/add.php?let=O` | 200 | 45.377 | dieselbe Liste in der **Upload**-Sicht: 492 Filme, `add.php?tvid=<id>` + Jahr |
| `add-filter-oppenheimer.html` | `/add.php?filter=oppenheimer` | 200 | 9.640 | Filtersuche (GET!) — genau ein Treffer, Feld bleibt gefüllt |
| — *(nur gemessen)* | `/add.html` | 200 | 113.143 | Buchstabe A derselben Upload-Liste, 1.407 Filme; ohne `let`/`filter` ist A die Vorgabe |
| `add-tvid-24811.html` | `/add.php?tvid=24811` | 200 | 10.191 | **Upload-Formular** für Oppenheimer (2023): `lang`-, `rip`-Auswahl, `UFile`/`UFile2`/`UFile3` |
| `add-tvid-unknown.html` | `/add.php?tvid=99999999` | 200 | 10.188 | dieselbe Form für eine Id, die es nicht gibt: „Movie:  ()" — keine Fehlermeldung |
| `add-movie.html` | `/add-movie.php` | 200 | 8.399 | Formular „Add Movie": `name`, `year`, `go` |
| `movie-4.html` | `/movie-4.html` | 200 | 104.870 | **Filmseite** „Matrix, The (1999)": 54 Einträge, 12 Sprachabschnitte, `parts` 1/2/3, Rip frei (`1080p`, `720p.BluRay` …) |
| `movie-239.html` | `/movie-239.html` | 200 | 100.247 | „Ocean's Thirteen (2007)": 51 Einträge, **alle 13** Sprachabschnitte inkl. Ukrainian |
| `movie-24811.html` | `/movie-24811.html` | 200 | 14.149 | kleine Filmseite, Oppenheimer (2023), 3 Einträge, 2 Abschnitte (en, Portugese(br)) |
| `movie-16856.html` | `/movie-16856.html` | 200 | 10.430 | Jahr doppelt in der Überschrift: „O Beautiful (2002) (2002)" |
| `movie-6928.html` | `/movie-6928.html` | 200 | 8.458 | Film **ohne** Untertitel: „0 subtitles downloaded  times" (zwei Leerzeichen) |
| `subtitle-132989.html` | `/subtitle-132989.html` | 200 | 11.461 | **Download-Seite**: Rip, Release, `filename:`, Größe, Datum, kompatible Releases, Link `download-132989.html` |
| `subtitle-2058.html` | `/subtitle-2058.html` | 200 | 11.254 | russischer Eintrag, `parts`=2 in der Liste, `filename: Matrix The.part1.ru.srt` |
| `subtitle-unknown.html` | `/subtitle-999999.html` | 200 | 10.848 | Id gibt es nicht: **HTTP 200** mit leerem Titel, leerer Sprache und „size: 9140 kb" — kein Fehler |
| `search-dune.html` | `/search.php?q=dune` | **500** | 3.311 | Suchtrefferliste (5 Filme mit Jahr); Rumpf endet nach der Filmliste mitten im Markup |
| `download-132989.headers.txt` | `/download-132989.html` | 302 | 374 | die Kopfzeilen mit `Location: files/Oppenheimer_2023.WEB-DL.MA.en.zip` |

Nicht abgelegt: die Zips selbst. Die Bytes sind Beleg, aber kein Seiteninhalt; die Messwerte stehen
in `docs/MOVIESUBTITLES.md` (Oppenheimer-Zip 92.522 B, ein `.srt`; Matrix-`KiSS`-Zip 36.385 B, drei
`.partN`-Dateien). Wer sie braucht, lädt sie mit `download-<id>.html` neu — das ist ein GET.

## Was diese Seiten belegen sollen

- **Filme, nicht Serien.** Die Filmseite hat keine `/tvshow-…`/`/episode-…`-Adressen (beide 302 auf
  `/`), keinen Staffel-/Folgenbegriff und keine Serie in der Liste (`add.php?filter=…` → 0 Treffer
  für „game of thrones", „the office", „chernobyl", „stranger things", „better call saul").
  Siehe Abschnitt „Zuordnung" im Protokoll.
- **`tvid` ist die Film-Id.** `movie-<id>.html`, `add.php?tvid=<id>` und der Rückweg von der
  Untertitel-Seite (`Back to <Film>` → `movie-<id>.html`) tragen dieselbe Zahl; eine Nummer, global.
- **Der Download läuft über eine Weiterleitung**, nicht über JavaScript wie bei tvst:
  `download-<id>.html` → 302 → `files/…zip`.
- **Die Sprache steht dreifach da** (Abschnittsüberschrift, Flagge, Wort im Titel) und stimmt in
  allen 108 geprüften Einträgen überein — anders als bei tvst, wo allein die Flagge zählt.
