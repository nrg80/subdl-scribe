# Log und Text — der Standard für dieses Plugin

Stand 18.11.2026. Gilt für jede Zeile, die das Plugin schreibt, und für jeden Text, den der Betreiber
in der GUI liest.

**Die Bestellung:** „mit dem log und den texten tue ich mich eh hart. Gibt es kein konzept oder ein
best practice papier das man darauf anwenden könnte?"

Dieses Papier ist die Antwort. Es lehnt sich an drei Dinge an, die es außerhalb dieses Repos schon
gibt, statt eine eigene Schule zu erfinden:

- **Serilog Message Templates** (<https://messagetemplates.org>) — fester Textrahmen mit benannten
  Feldern, damit dieselbe Zeile bei jedem Lauf gleich aussieht und maschinell zählbar bleibt.
- **OpenTelemetry Semantic Conventions für Logs** (<https://opentelemetry.io/docs/specs/semconv/>) —
  ein Log-Eintrag braucht Zeit, Schweregrad, Ereignisnamen und Betreff, in dieser Reihenfolge.
- **Google SRE Book, „Monitoring Distributed Systems"** — schreib für den, der nachts geweckt wird:
  der Schweregrad sagt, was der Leser TUN soll, nicht wie interessant der Autor die Stelle fand.

Der Maßstab ist eine Frage: **Kann der Betreiber aus einer einzigen Zeile entscheiden, ob er etwas
tun muss?** Wenn nein, ist die Zeile falsch — nicht zu lang, nicht zu kurz, sondern falsch gebaut.

## 1. Regeln

Jede Regel nennt Grund und Beispiel. Die Beispiele sind echt, aus dem Prod-Lauf vom 18.11.2026.

### 1.1 Eine Zeile = ein Ereignis

Ein Ereignis pro Zeile, kein Zwischenschritt, keine Absicht, kein Nachtrag über eine Arbeit, die
später passiert.

- Falsch: `upload saved for "X.mkv" [UK] — the SubDL id is read back once at the end of the run (F-M588).`
  Hier stehen zwei Dinge in einer Zeile, und das zweite ist eine Ankündigung, kein Ereignis.
- Richtig: `UPLOAD SAVED "X.mkv" [UK] → subdl`, und wenn die Id wirklich zugeteilt ist:
  `UPLOAD ID "X.mkv" [UK] = 421844 (mySubtitles)`.

### 1.2 Fester Rahmen: Ereignis, Betreff, Zahlen, Ergebnis

Reihenfolge im Satz ist immer dieselbe. Ein Ereigniswort in Großbuchstaben zuerst (`UPLOADED`,
`REJECTED`, `SKIP`, `SEED`, `PACK`, `DEFER`), dann der Betreff, dann Zahlen, dann das Ergebnis.

- `UPLOADED "Doctor.Who.2023.S02E01.1080p.x265-ELiTE.mkv" [EN] → tvsubtitles` ✓
- `SKIP "War.2026.S01E05…mkv" — item settled before extraction (every text stream has its end state)` ✓

### 1.3 Die Quelle, die geantwortet hat, gehört in die Zeile

Das Präfix `[Sub Scribe]` ist der Name des Plugins, nicht der Quelle. Wer `UPLOADED` liest, muss ohne
Vorwissen sehen, wohin es ging.

- Falsch: `[Sub Scribe] UPLOADED "X.mkv" ["EN"]`
- Richtig: `UPLOADED "X.mkv" [EN] → tvsubtitles`
- Hat keine Quelle geantwortet, steht das da: `no source answered` — nicht eine Lücke, die wie SubDL
  aussieht.

### 1.4 Zahlen statt Adjektive

Jede Zusammenfassung nennt die Zahl und die Verteilung, nicht eine Wertung.

- Falsch: `upload: finished — everything rejected`
- Richtig: `RUN END upload — 0 uploaded | 295 rejected (275 duplicate, 20 duplicate-HI) | 97 skipped | 14 min`

### 1.5 Der Schweregrad sagt, was zu TUN ist

| Level | Bedeutung | Beispiel |
|---|---|---|
| Normal (`[N]`) | Der Betreiber muss es wissen. Ein Ereignis pro Datei und Richtung, plus je eine Lauf-Zeile. | `UPLOADED …`, `RUN START`, `RUN END` |
| Verbose (`[V]`) | Wo genau es passiert ist. Nur wenn jemand nachfragt. | `SKIP "…" — duplicate-self-echo (HI=False)` |
| Detail/Debug (`[D]`) | Interne Schritte, Zeitmessung, Datenbank. | `LAUF upload — item 429/1417` |

- Ein **Erfolg** braucht eine Normal-Zeile. Ein Fehler, der loggt, ersetzt sie nicht.
- Eine Zeile, die im Normal-Level nur die Position nennt („item 429/1417"), gehört auf Debug.

### 1.6 Kein Abbruch ohne Betreff

Ein Wort wie `cancelled` muss sagen, WAS abgebrochen wurde. Der Lauf, die Extraktion, ein Versuch?

- Falsch: `ffmpeg for "Ali G….mkv" was stopped — the run was cancelled (F-M596)`
  (im Lauf vom 18.11.2026 stand das viermal da, und der Lauf lief danach noch 300 Positionen weiter)
- Richtig: `EXTRACT CANCELLED "Ali G….mkv" — the item settled first`, und ein echter Lauf-Abbruch nur
  an der Stelle, an der der Lauf wirklich endet.

### 1.7 Ein Zustand, ein Wort

`ok`, `fail`, `skip`, `cancel`, `defer` — dieselben Wörter im Log wie in der GUI. Kein Synonym, keine
zweite Schreibweise, keine Umschreibung. Ein Zustand hat genau eine Farbe und genau ein Wort.

### 1.8 Wiederholungen zusammenfassen

Gleiche Zeilen nicht N-mal auf Normal. Eine Zeile mit der Zahl, die Einzelfälle auf Verbose.

- Normal: `SECOND SOURCE moviesubtitles — 66 title(s) closed, 18 id(s) unusable, 5 language(s) missing`
- Verbose: je Titel eine Zeile mit dem Grund.

### 1.9 Nur was passiert ist

Keine Absicht („will", „soll", „read back once at the end"), keine Vermutung, kein Passiv ohne Täter,
wenn der Täter bekannt ist. Eine Zeile, die eine spätere Handlung ankündigt, ist ein Versprechen im
Log — und Versprechen im Log veralten.

### 1.10 Der Ereignis-Code steht am Ende, genau einer

`F-M###` ist eine stabile Kennung für die Fundstelle, damit Tests sie festnageln können. Ein Code pro
Zeile, in Klammern, am Ende. Zwei Codes in einer Zeile heißen: es sind zwei Ereignisse.

### 1.11 Keine kaputten Zeichen

Der Log-Schreiber setzt um jeden **String-Wert** selbst Anführungszeichen und verdoppelt vorhandene.
Deshalb gilt: **keine eigenen Anführungszeichen um einen Platzhalter** und **kein leeres
String-Argument als Suffix**.

- Falsch (echt): `LAUF upload — item 1/1417 ""Back to the Suture""` — das Template schreibt
  `\"{Name}\"`, der Schreiber quotet zusätzlich.
  Richtig: `LAUF upload — item 1/1417 {Name}` (der Wert kommt gequotet an).
- Falsch (echt): `upload: started""` — das Suffix `{Dry}` ist ein **leerer** String und wird als `""`
  gedruckt. Richtig: das Suffix nur anhängen, wenn es Inhalt hat, oder eine eigene Zeile dafür.
- Falsch (echt): `"sent"""` — derselbe Fehler auf der Erfolgsmeldung eines Versands.

Gemessen am Prod-Lauf 18.11.2026: **1.424 von 7.021 Zeilen** trugen solche Artefakte. Das ist kein
Kosmetikproblem — ein Leser hält die Zeile für kaputt und sucht den Fehler an der falschen Stelle.

### 1.12 Kleinschreibung der bestehenden Schlüsselwörter bleibt

`upload:`, `download:`, `season pack:`, `LAUF`, `TAKT` sind gewachsene Marker, an denen Filter und
Tests hängen. Neue Zeilen folgen 1.2, alte Marker werden nicht umbenannt, ohne dass Tests mitziehen.

## 2. Der Lauf-Bericht (Gerüst)

Ziel: **höchstens zehn Normal-Zeilen pro Lauf und Richtung**, aus denen man den Lauf nacherzählen
kann. Alles andere ist Verbose.

```
[N] RUN START upload — 1417 item(s), sources: tvsubtitles, subdl, moviesubtitles
[N] SEED upload — 192 open (tvsubtitles 173, subdl 20, moviesubtitles 18)
[N] PACK upload — 125 season(s) on disk, 12 examined, 3 uploaded, 9 skipped (2 incomplete, 7 one source)
[N] DECIDE upload — 155 settled, 275 duplicate, 20 duplicate-HI, 97 stream(s) skipped
[N] UPLOADED "<file>" [EN] → tvsubtitles            (eine Zeile je wirklich hochgeladener Datei)
[N] REJECTED "<file>" [AR] → subdl: duplicate (upload #385190)   (eine Zeile je Quelle und Datei)
[N] SOURCE moviesubtitles — 66 title(s) closed, 18 id(s) unusable, 5 language(s) missing
[N] RUN END upload — 0 uploaded | 295 rejected | 0 refused | 0 without answer | 97 skipped | 14 min 27 s
[N] POSTPROCESS — armed for 21:13 UTC ("upload run ended (9 uploaded)") / not armed (nothing uploaded)
```

Regeln für dieses Gerüst:

- Jede Zeile beginnt mit demselben Schlüsselwort wie im Beispiel. Die Reihenfolge ist fest.
- `RUN END` ist Pflicht und trägt **alle** Zähler, auch die Nullen.
- `POSTPROCESS` ist Pflicht, weil „nichts hochgeladen" und „nichts geplant" zwei verschiedene
  Zustände sind, die sonst gleich aussehen.
- Ein abgebrochener Lauf schreibt dasselbe Gerüst, `RUN END` mit `cancelled` und dem, was er noch
  geschafft hat.

## 3. Prüf-Checkliste (vor jedem Release, für jede neue Zeile)

1. Sagt die Zeile, **was** passiert ist, **an welchem Gegenstand**, **mit welcher Zahl**, **wohin**?
2. Hat sie genau **ein** Ereignis, **einen** Code, **eine** Quelle?
3. Ist sie auf dem Level, auf dem der Leser sie braucht — und fehlt sie nicht auf Normal, wenn sie
   einen Erfolg beschreibt?
4. Benutzt sie das Wort, das die GUI für denselben Zustand benutzt?
5. Steht eine Zahl drin, wo ein Adjektiv stand?
6. Würde ein Fremder sie ohne Codekenntnis richtig lesen?
7. Steht sie im Lauf-Bericht an ihrem Platz — oder macht sie den Bericht länger als zehn Zeilen?

Wer eine Zeile ändert, hängt den Test mit, der sie festnagelt (`scripts/tests/`), sonst ist die
nächste Aufräumrunde die alte Zeile wieder los.

## 4. Anhang: Messung am Prod-Lauf 18.11.2026, 22:08–22:22

Gezählt an `log_20261118.log` (Upload-Richtung, 1.417 Positionen):

- **7.021 Zeilen** insgesamt, davon **125 Normal**, 1.301 Verbose, 5.254 Debug. Lesbar ist also
  **1,8 %** des Laufs — das ist der ganze Grund für „mit dem Log tue ich mich hart".
- Die 125 Normal-Zeilen sind **34 verschiedene Vorlagen**, davon **rund 90 Einzelfälle** aus dem
  Season-Pack-Pfad (`season pack: "<id>" S<n> — …`). Nach Regel 1.8 gehören die auf Verbose; der
  Bericht schrumpft damit auf etwa 30 Zeilen.
- **1.424 Zeilen** mit Anführungszeichen-Artefakten (Regel 1.11), **94 Zeilen** mit zwei `F-M`-Codes
  (Regel 1.10), **4 Zeilen** mit falschem Abbruch-Betreff (Regel 1.6: „the run was cancelled", während
  der Lauf weiterlief).

Diese vier Punkte sind der Arbeitsvorrat für die nächste Aufräumrunde — in dieser Reihenfolge, weil
jeder folgende Punkt ohne den vorigen nur wieder Zeilen umsortiert.
