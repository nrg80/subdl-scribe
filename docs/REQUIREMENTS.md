# Requirements Specification — Jellyfin Plugin "Sub Scribe" (Upload + Download)
**Project:** Native Jellyfin plugin: automatic upload of embedded subtitles to SubDL.com + download pipeline for missing external subtitles — both in ONE plugin
**Version:** 2.142
**Status:** Implementation — v12.1.12.405.

**Die Begründungen (warum eine Regel gilt, Messungen, Vorfälle) stehen nicht hier, sondern lokal in der Methodik-Ablage, nach Kapiteln sortiert und mit der Requirement-Nummer
markiert. Dieses Dokument nennt die Regel, die Konstante, den Grund und den Test — sonst nichts.**

**Zählkonvention der Contents-Zähler.** Ein Abschnittszähler ist die **Anzahl der Definitionszeilen in seinem Block, Unterüberschriften eingeschlossen, zurückgezogene Einträge eingeschlossen** — ein schlichter Zählwert, der keine Aussage darüber trifft, was in Kraft ist. Ein Abschnittszähler, der seine Unterabschnitte ausnimmt, ist nicht die Konvention. Geprüft von T115.

## Contents

- [1. Objective and Scope](#1-objective-and-scope) — 2 requirements
- [2. Seeder — what enters the queue](#2-seeder-what-enters-the-queue) — 13 requirements
- [3. Upload Pipeline](#3-upload-pipeline) — 95 requirements
 - [3.1 Quality Gates — Upload](#31-quality-gates-upload) — 9 requirements
 - [3.2 Dry Run — Upload](#32-dry-run-upload) — 3 requirements
 - [3.3 Season Packs — Upload](#33-season-packs-upload) — Querschnitt, zitiert F-M379–F-M448 und F-M475, F-M476
- [4. Download Pipeline](#4-download-pipeline) — 84 requirements
 - [4.1 Quality Gates — Download](#41-quality-gates-download) — 32 requirements
 - [4.2 Dry Run — Download](#42-dry-run-download) — 1 requirement
 - [4.3 Auto-Sync — Download](#43-auto-sync-download) — 20 requirements
 - [4.4 Season Packs — Download](#44-season-packs-download) — Querschnitt, zitiert F-M334, F-M384, F-M397, F-M454
- [5. Upload Postprocessing](#5-upload-postprocessing) — 14 requirements
- [6. Database Refresh](#6-database-refresh) — 9 requirements
- [7. OSHash Refresh](#7-oshash-refresh) — 3 requirements
- [8. Rules Shared by Both Directions](#8-rules-shared-by-both-directions) — 5 requirements

- [9. SubDL/TMDb API, IDs and Credentials](#9-subdltmdb-api-ids-and-credentials) — 66 requirements
- [10. Scheduler, Quota and Timing](#10-scheduler-quota-and-timing) — 22 requirements
- [11. Content Registry and Identity](#11-content-registry-and-identity) — 27 requirements
- [12. Library Scope and Skip Filters](#12-library-scope-and-skip-filters) — 8 requirements
- [13. Configuration and Settings Page](#13-configuration-and-settings-page) — 21 requirements
- [14. Data Model and Persistence](#14-data-model-and-persistence) — 19 requirements
- [15. Logging, Status and Transparency](#15-logging-status-and-transparency) — 48 requirements
- [16. Non-Goals](#16-non-goals)
- [17. Non-Functional Requirements](#17-non-functional-requirements)
- [18. Acceptance Criteria](#18-acceptance-criteria)
- [19. Test Library](#19-test-library)
- [20. References](#20-references)

## 1. Objective and Scope

Automatic upload of all embedded text subtitles from the Jellyfin library to SubDL.com.
**Core idea:** Jellyfin already knows every file and every stream. The plugin handles discovery, extraction, quality assurance and upload entirely within the media server — no external polling cron, no separate state management for file discovery.
### Context — current state
**Before the plugin: manual/scripted extraction and upload outside Jellyfin** — ffmpeg extraction (one call per file, all streams at once), upload to `https://api.subdl.com` with one account, a queue and uploaded-state JSON, lock files, a not-found retry, and a repeating two-hour job with a random delay.
**Architecture decision: one plugin, two pipelines.** Download and upload are integrated in ONE plugin; the downloader is NOT a standalone app or a second plugin.
- **Upload pipeline:** extracts embedded subs and uploads them.
- **Download pipeline:** searches for missing external subtitles and downloads them.
- Both share the same services and process, with separate switches, separate scheduled tasks and separate status counters.
- **Shared components:** one SubDL API client (login, rate limit, backoff, retry), one content-hash registry (every subtitle uploaded OR downloaded is registered under a normalized content hash, consulted before either direction acts), one language mapping / IMDB resolver, one config and status infrastructure.
**Known problems the plugin must solve:** container language tags are often wrong (ISO 639-2/B inconsistent), so language verification is needed; and files in subfolders can produce path resolution errors.
**F-M37 [B1]:** **Modular layering:** strict separation into (a) SubDL API client, (b) content-hash registry, (c) language mapping/IMDB resolver, (d) upload pipeline, (e) config/status. Only (d) is upload-specific; (a)–(c) and (e) are reusable.

**F-M40 [B1]:** **DI registration via IPluginServiceRegistrator:** the shared services are registered with the container. The download pipeline is built outside the container and takes the registry from the plugin instance.

## 2. Seeder — what enters the queue

**F-M1 [B1]:** Automatic upload of new media files via scheduled task, interval configurable (per F-M21). The task is a trigger only and starts a dispatch cycle; "on new file" is the arrival path (F-M1a).

**F-M4a [B1]:** Only process libraries selected in the config (F-M18)

**F-M189 [B1]:** **Library selection resolves to PATHS, so a selection keeps working when libraries are NESTED.** An item is accepted when its path lies under a selected root on a **directory boundary** (a selected path prefix must not match, and the comparison follows the filesystem's case semantics), or when the resolved collection-folder name is selected. The containment check also marks libraries that CONTAIN or are CONTAINED BY a selected path. The gate applies at the seeder's hard gate and its event-path gate, the collection step in both pipelines, the dispatcher's event-library resolution and the status controller's directory check. The selected names are resolved per call site, not cached across a run.

**The scope must handle subdirectories of a selected library:** an inner library created over an already-covered path stays empty.

**F-M1a [B3]:** ItemAdded real-time trigger — the item-added event subscription in the event dispatcher (the handler); there is no separate watcher class. Each arrival starts a non-resetting debounce timer (the arrival debounce window, default 5 min). On fire the dispatcher runs one cycle: seed → download → upload (download first, F-M149). Events during an active cycle collect in the pending ids. A follow-up reseed runs at cycle end only if that list is non-empty; the follow-up settings decide which directions take part. Arrivals that are still pending when the cycle ends start one further full cycle after a 90-second delay.

**F-M233:** **An on-arrival run works ONLY the arrivals; a full-coverage pass happens on the schedule or the manual button.**

The arrival path must be scoped to the items that arrived. Unscoped, one arrival window swept every gap in the library into the queue and the run died on the daily limit before reaching its own items.

The debounce window collects EVERY event in it, not merely the first, and arrivals during an active run form the follow-up window. An arrival cycle seeds and works exactly those ids.

Only `event` and `arrival-followup` are scoped. `scheduled-upload`, `scheduled-download`, recovery fires and the manual button carry no item scope and keep full coverage for the selected libraries.

"Kein treffer, kein lauf": an arrival-scoped round whose seed queued nothing for that direction ends; it does not work leftover queue entries.

An arrival cycle with an empty collector ends without a scan. Falling back to a full scan on the arrival path is a defect, not a graceful default.

**F-M244 [B1/D]:** **The queue item is a complete work order; the pipelines only carry it out.** The seeder decides WHAT is to be done and hands it over in the item; the downloader and the uploader execute that order.

The download pipeline reads its languages from the item and does not answer the HI question again — that decision is the HI wish plus the languages the order names. The upload pipeline likewise consumes the positions it was handed.

The pipelines keep ONE non-derivation check — the media file's existence. It is the loop's precondition (F-M60), not a give-up, and a file that vanishes between seeding and the run is reported as a failure. The **Seeder** refuses such an item before it can enter a queue (`Scan` skips on `!File.Exists`), and a file already gone from disk is the **Database Refresh's** phase 1b.

**F-M263:** **A dry run suppresses the container rewrite — for BOTH directions — and the seeder is the only caller of the language gate.**

With a dry run (upload) OR a dry run (download) armed, the gate still resolves untagged tracks and reports them but writes nothing into the media file. Detection is not suppressed: a dry run's value is answering what it WOULD do (F-M22).

**F-M259 [D]:** **Loose.srt files are observed by the seeder's scan.**

The scan records every loose subtitle file beside the media whose NAME carries a language as an observation row, keyed by the normalized content hash (the sidecar's identity) with its language and HI flag. A sidecar whose name carries no language token is resolved by detection and renamed first (F-M278); if its language cannot be established it gets no observation row. `Status = observed`, no verdict; an existing verdict is never overwritten.

Content is the key, not the path, and the hash is the uploader's own function. Two sidecars with the same language but different text stay two rows.

**F-M278 [D]:** **An unlabelled sidecar is detected and then RENAMED to the shape this plugin writes — `<container>.<lang>[.sdh].srt`. Only a file whose name carries no language token is touched.** The run order is detect, then rename, then record. Detection reads the file's own text (no ffmpeg pass). The gates apply unchanged and in this order: switch **`Allocate missing language codes` off** (F-M314 — the same switch as the container write, NOT das Auflösen unbestimmter Sprachcodes), text below the 2 KB floor (F-M16), or no language found all mean **the file is left exactly as it is** — no rename, no row; only a detection that names a language renames. A file that already carries a language token is never renamed. A taken combination takes the next free slot (`<container>.<lang>.01.srt`, F-M316; then `.02.srt`, `.03.srt`, …), never an overwrite, and the slot is per combination. A rename moves the stored row's location with it: the content hash — the row key — is unchanged, so the row keeps its verdict. A refused rename is not a failed observation: a file that cannot be renamed is recorded under its current name.

**F-M55:** **Download-on-arrival as its own switch** + UI restructure: "Download subtitles on arrival" checkbox (Download tab), independent of the upload arrival setting. Both directions share the same diced anchors/jitter.

**F-M57:** **Symmetric per-direction config, one shared rhythm:** each direction carries its own "… on arrival" checkbox — "Upload subtitles on arrival" and "Download subtitles on arrival". The scheduled rhythm is **not** per direction: the single refetch dropdown governs both (F-M111). Both directions share the same diced anchors; `Manual` suppresses the scheduled pipeline fires for both.

**F-M111:** **Queue-driven operation:** both pipelines run only over items the seeder placed in the in-memory queue. Runs are atomic per direction and serialized by a global run lock.

**F-M188:** **Ohne gewählte Bibliothek startet der Seeder nicht.** Die Prüfung liegt vor dem Lauf-Lock, vor dem Durchgang über die Änderungsstempel und vor jedem Datenbankzugriff; dieselbe Bedingung steht zusätzlich im Scan selbst.
## 3. Upload Pipeline

**F-M2 [B1]:** Manually triggerable scheduled task (dashboard "Scheduled Tasks") for a full library rebuild

**F-M3 [B1]:** **A media file is upload material when it carries at least one text subtitle stream, or at least one loose subtitle file next to it.** Bitmap streams (PGS/VobSub) do not qualify: they carry no text (F-M6). A stream Jellyfin reports as external is a loose file already, not a container stream. A forced track carries only the lines of foreign-language scenes and does not qualify either (F-M246).

**F-M4 [B1]:** Never upload an (item, language) pair twice — persistent state file in the plugin data dir

**F-M6 [B1]:** Text subtitle streams only (SRT/ASS/SSA, converted to SRT); bitmap subs (PGS/VobSub) explicitly excluded

**F-M7 [B1]:** **Extraction writes nothing into the media library, and its working files are removed on every exit.** Each extraction runs in its own directory outside the library, one per job. That directory is removed when the job ends — after a success, after a failure and after a cancellation. The removal is best effort and stays silent: a leftover working file must never fail a run, and it is never worth a warning.

**F-M8 [B1]:** SubDL API (`https://api.subdl.com`): login (user/pass → token) and the upload endpoint. The call rate is configured, not fixed (F-M20)

**F-M9 [B1]:** Metadata per upload: series IMDB (**not** episode IMDB) + season + episode for TV; movie IMDB for movies — from Jellyfin provider IDs. Without IMDB → skip (F-M28).

**F-M10 [B1]:** Language mapping to SubDL's two-letter codes (`nob`→`NO` etc.)

**F-M48 [D2]:** **Loose SRT files as an additional upload source:** external `.srt` files next to the media file are uploaded in addition to embedded streams. The language comes from the file name; a name that carries **no** language token goes through language detection, at the same switch as an `und` stream (F-M74) and with the same outcomes — switch off, text below the 2 KB floor (F-M16), or no language found all mean **skip**, never a guess. The marker is **`sdh` only**, and such a file is uploaded with `hi=true` (F-M71). `hi` is NOT a marker — it is the ISO 639-1 code for **Hindi** (ISO 639-1 `hin` = Hindi), so a `.hi.srt` name is a Hindi subtitle. Nothing in this plugin writes `.hi.srt`; the downloader writes `.sdh.srt` (F-M239). The HI flag is part of the identity in both areas (F-M197, F-M199).

**F-M88c [B3]:** **Upload-side short-circuit (the upload-side completion mark):** a file whose embedded subtitle side is complete is skipped before any ffprobe, directory walk or API call. Complete means every stream position reached a terminal state (uploaded or rejected). The field is named after the SUBTITLES, not the file: the video file itself is never uploaded.

**F-M21 [B1]:** Update interval: **manual / daily / twice daily / twice weekly / weekly / monthly**. A scheduled interval fires at the per-installation diced random anchor (F-M51); **manual** leaves no scheduled fire at all and keeps the dashboard and config-page triggers; "on new file" = F-M1a. **Default: Weekly**, shared by both directions (F-M111).

**F-M26b [D]:** **A deferred direction waits the job spacing — one knob, no hidden clock.** Whenever a stop defers a direction, the one-shot recovery fire lands **JobSpacingMinutes after the stop** (both directions), clamped 5–120, default 15. The user configures it through the existing **Job spacing (minutes)** field — one knob for all spacing, no separate control to discover; A deferral is recorded with the direction's own stored wording, so a run-lock push is not reported as a quota problem (F-M288). Anti-herd spreading is not needed here: the offset is per installation; the daily-limit reset keeps its randomised 30–300 min offset (F-M152, F-M182). The coordinator must not add a second offset on top — this path uses the recovery-fire scheduler, der den Versatz des Aufrufers unangetastet lässt — and the offset must not affect the daily-limit reset.

**F-M249:** **The file-name parser recognises a bare episode marker without a season, and a broadcast date in the middle of the name with the title after it.** Every other shape keeps its result.

**(a) Bare `Exx`.** A name with a bare `Exx` carries no `S<d>E<d>`. The rule sets a series flag and the episode number, then falls through to the shared head/year/quality cleanup.

**(b) Mid-name broadcast date.** The shape is `<strand>.<YYYY>.<MM>.<DD>.<title>.<tags>`. The rule takes the text after the date, cuts at the first release/language token and drops trailing bare numbers.

**Scope of the date rule:** it applies only when (1) no `SxxExx`/long-form marker is present, (2) the date is a **separated** `YYYY<sep>MM<sep>DD` (the compact `YYYYMMDD` stamp belongs to the TV-stamp rule), (3) month and day are in range, and (4) **text follows the date**. A name with text BEFORE the date keeps its title and year.

**Scope:** every other file parses byte-identically in title, year, type, season and episode, and no file loses its title.

**Rules:** the prefix of (b) is dropped, not combined; a bare `Exx` never invents a season; the compact TV-stamp path is untouched; and a bare `Exx` marker is accepted in either case, upper or lower.

**F-M252:** **A bracketed year is a year, and its opening bracket is not part of the title.** The tail-year rule accepts `(YYYY)` and `[YYYY]` at the tail.

**Rules:** (a) the tail-year rule closes the bracket; (b) the "year anywhere" branch cuts at the **start of the match** (the separator), never at the year; (c) the title cleanup drops an unclosed trailing `(`/`[`/`{`.

**Scope:** every changed file must be a bracket name.

**F-M250:** **The parser reads two series markers: `NxNN`, and `SxxExx` at the end of the name.**

**(a) `NxNN` (`2x01`).** Read as a season/episode marker.

**The `NxNN` rule cannot misfire on a resolution.** The season is capped at two digits, must not start with `0`, and may not be preceded by a digit. `1920x1080`, `2160x1080` and `0x01` yield nothing; `1x01` → S1E1, `10x05` → S10E5.

**(b) `SxxExx` at the end of the name.** The separator after the episode number is optional at the end of the name.

**F-M202 [B1]:** **An episode TMDB id is PROVEN against a candidate show — never guessed.**

Rule: title + season + episode → `search/multi` → candidate show → `tv/{show}/season/{s}/episode/{e}` → accept the show **only when the episode id returned there equals Jellyfin's id**. A confirmed match yields the SHOW ids (imdb via `/external_ids`; the detail endpoint reports `imdb_id: null` for series).

**Fail-closed:** no confirmed equality ⇒ `(null, null)` ⇒ the item is skipped like any other unresolvable series.

Season and episode come from Jellyfin's own metadata where it reports them; the parsed file name is the fallback and leads only when the name itself marked the item as a series.

**F-M29 [B1]:** **No user-identifying metadata in uploads:** exclusively IMDB ID + season/episode + language + subtitle file. No username, no library/path information, no source media filenames, no server/installation identifiers, no version telemetry. A content hash of the uploaded bytes is allowed in the filename: it is a property of the subtitle, not of the sender (F-M30).

**F-M30 [B1]:** Neutral upload filename: derived generically from the IMDB/language/release info — never internal JF paths or user names. **Order („die volle md5 macht sinn"):** the name carries the **full MD5** of the normalized upload bytes directly in front of the language — `tt1234567.S01E02.<32 hex>.EN.srt` for an episode, `tt1234567.<32 hex>.EN.srt` for a movie — and is built in exactly ONE helper that the dry run and the real upload both call, so the advertised name and the sent name cannot drift. The hash is content-derived and recomputable from the delivered file: it names the bytes, never the sender (F-M29 stands).

**F-M264:** **A media file is rewritten at most once — a track that already carries a tag in the container is never written again.**

Within ONE cycle both seed passes write the codes: the DOWNLOAD-seed pass, and the UPLOAD-seed pass that follows. The second pass reads a stale stream list and finds the same tracks untagged.

Jellyfin caches its stream list, so a corrected container keeps reporting its old tag for the rest of the cycle. No registry read and no self-observation of a freshly written tag changes that.

Before the extraction pass, read the container's ACTUAL per-subtitle tags (a single ffprobe read, one ffprobe call, no remux) and drop every pending position that already carries a mappable tag. Those positions are recorded as ordinary tracks instead, so the registry and the queue decision still see the real language. When nothing remains pending, the run returns with `Written == 0` and logs `N track(s) already carry a language tag in the container; nothing to write` at Normal.

The switch the missing-language switch sits on the **General** tab under `Media files`, directly above the statistics section.

**F-M239 [B1/D2]:** **One reader for sidecar file names.** The name→(language, hearing-impaired) rule exists ONCE (the sidecar reader); the uploader, the pipeline's missing-language check and the database refresh all call it. Marker is `sdh` only — never `hi` (F-M48). Recognized shapes: `<base>.srt` (no language — it is detected before upload, F-M48), `<base>.<lang>.srt`, `<base>.<lang>.sdh.srt`, and the numbered slots `<base>.<lang>.<n>.srt` the downloader writes (F-M316). `.part` is never a subtitle.

A marker is consumed as a marker and the language token is read from the position BEFORE it; a marker must never reach the language mapper. A name whose marker has no resolvable language token yields nothing.

**F-M266:** **The registry sweep removes OBSERVATIONS only — a VERDICT and a detection-attempt record are out of its reach.**

**F-M191 [B1]:** **A SERIES item must carry SHOW-level ids** — TMDB and SubDL expect the tvshow id together with season and episode, **never an episode id**. Jellyfin's provider ids for a mis-typed episode are EPISODE ids in **both** fields.

**Episode → show resolution:** die Suche über die IMDb-Id liefert die Serien-Id IS the series TMDB id; `tv_results[0].id` is the series itself when the id was already show-level. The series IMDb then comes from `/tv/{showId}/external_ids`.

**An episode TMDB id cannot be walked up directly:** `/find/{episodeId}?external_source=tmdb_id` returns EMPTY and `/tv/{episodeId}` is 404. Such a value is first PROVEN against a candidate show (F-M202); only when that proof fails is it dropped, and the caller's type-free title search then resolves the show by name.

**F-M391 [U]:** **Ein Einzel-Upload, der in einen Pack aufgeht, wird vorher gelöscht — Dublette zuerst, Pack danach.** Ein Pack, der gegen Einträge mit demselben Inhalt antritt, ist genau der Fall, den man nicht auf SubDL's Seite klären will; die Reihenfolge macht den Pack zum einzigen Träger. Gelöscht wird über der Löschaufruf mit der Eintrags-Id aus der Registry — der Löschaufruf über die Upload-Id ist dafür die falsche Route: er antwortet mit Erfolg und löscht nichts. Die IDs wandern in die `packs`-Zeile (F-M390); scheitert der Pack danach, nennt das Log die gelöschten IDs als Warnung, und der nächste Lauf baut den Pack aus der Bibliothek neu.

**F-M403 [P]:** **Eine Schreibweise für den Modus: inline klein `dry`, auf Ereigniszeilen als Präfix `DRY-RUN`.** Die Formen sind: `upload: started, dry`, `download: started, dry, refetch=…`, `DRY-RUN would have uploaded …`, `DRY-RUN — N item(s) left Queued`, `cycle start (…, dry)`.

**F-M402 [P]:** **Auch das Log sagt es: die Zykluszeilen tragen den Trockenlauf-Vermerk.** `cycle start (…, dir UploadOnly, dry)` und die beiden `cycle end`-Zeilen ebenso, gesetzt über einen Helfer aus der Trockenlauf bzw. der Trockenlauf-Schalter des Downloads — damit beantwortet das Log die Frage „war das ein Trockenlauf?" genauso wie die Worker-Zeile.

**F-M401 [P]:** **Läuft ein Worker im Trockenlauf, steht das schon während des Laufs in der Worker-Zeile — „run dry".** Das Flag wird beim Start und beim Abschluss geschrieben, gefüllt aus der Trockenlauf-Einstellung bzw. dem Trockenlauf-Schalter des Downloads. Der Marker ist das Wort **dry**, grau, direkt neben dem Ergebniswort.

**F-M400 [P]:** **Die Sprachzeile im Log stellt den Befund fest, statt den Tag einen Lügner zu nennen.** Die Zeile lautet „LANGUAGE … — tagged X, detected Y; uploaded under Y". Der Inhalt ist derselbe, der Ton ist neutral — der Tag ist eine Angabe, die nicht stimmt, kein Akteur.

**F-M399 [P]:** **Ein Pack gilt nur dann als vorhanden, wenn die `packs`-Zeile ihn belegt — eine Id auf den Mitgliedern belegt nichts.** Die alte zweite Beweisregel („jede Folge trägt eine SubdlId") war falsch: auch ein **Einzel**-Upload schreibt seine eigene Id dorthin, also sah eine Staffel, deren Folgen alle einzeln hochgegangen waren, „schon gepackt" aus und wurde nie gepackt — im Trockenlauf 98 Staffeln, für die es nie einen Pack gab. Der Zähler und die Heuristik sind entfernt; die Pack-Prüfung ist der einzige Nachweis.

**F-M397 [U]:** **Stammt auch nur eine `.srt` des Packs ursprünglich von SubDL — heruntergeladen oder aus einem heruntergeladenen Pack entpackt —, wird für diese Staffel kein Pack gebildet.** Nicht das Mitglied fällt raus, der ganze Pack fällt aus: ein Container um solchen Text wäre von Bauart wegen eine Wiederveröffentlichung dessen, was SubDL selbst herausgegeben hat. Die Prüfung ist inhaltsbasiert (über die `downloaded`-Sidecar-Zeile zum Content-Hash), damit Umbenennen, Verschieben oder Neu-Zuordnen sie nicht aushebelt, und sie sitzt **vor** der ersten Löschung. Logzeile: „season pack refused — … came from SubDL itself".

**F-M396 [P]:** **Ein Pack, der mit genau diesen Mitgliedern schon oben ist, geht nicht noch einmal raus — und eine Pack-Zeile, die niemand mehr trägt, wird beim Database Refresh entfernt.** Der Reupp-Schutz saß nur im Fill-only-Plan; der Puffer-Weg lud unbesehen, sobald etwas für die Staffel in der Queue lag. Jetzt prüft der Koordinator vor dem Löschen die `packs`-Zeile: gleiche Folgenmenge und gleiche Release heißt „nichts Neues im Container"; eine neue Folge ändert die Menge, dann geht der Pack berechtigt raus. Zweitens kannte der Refresh die `packs`-Tabelle nicht: verschwanden die Dateien einer Staffel, blieb die Zeile stehen und legte die Staffel für immer still. Die Aufräumprüfung entfernt jede Zeile, deren Pack-Kennung auf keiner Untertitelzeile mehr steht — und zählt im Refresh mit.

**F-M395 [U]:** **Aus heruntergeladenen Untertiteln entsteht nie ein Pack.** Füllmaterial muss von diesem Konto **hochgeladen** worden sein; der Beleg ist die Zeile genau dieser Spur mit Status `uploaded`, nicht die Inhaltsprüfung — das zählt einen Download und einen Reject genauso als bekannt. Ein heruntergeladener Untertitel wieder in einen Pack zu stecken hieße, das, was bei SubDL schon liegt, unter eigenem Namen als neuen Container erneut zu veröffentlichen. Verweigert wird mit eigener Logzeile („only known from a download or a rejection").

**F-M394 [U]:** **Jede auf Platte vollständige Staffel ist packbar — unabhängig davon, wie viel davon in diesem Lauf in der Queue steht; was fehlt, wird aus dem aufgefüllt, was schon hochgeladen ist.** Die alte Regel („Pack nur, wenn alle Folgen queued sind, sonst Einzel-Uploads") ließ eine Sackgasse: eine Staffel mit 7 von 8 queued war weder packbar noch wurde sie aufgefüllt, und die Queue lief als Einzel-Uploads ab — 39 Sprachen einer Folge statt eines Packs. Jetzt entscheidet genau eine Stelle, ob der Pack hochgehen kann: der Koordinator prüft seinen Puffer, füllt die Lücken über den Filler und lädt entweder den vollständigen Pack oder fällt auf Einzel-Uploads zurück. Die Queue bestimmt nur noch die **Reihenfolge** der Auswahl (frischer Inhalt vor Füllmaterial), nicht die Machbarkeit.

**F-M389 [U]:** **A season pack may be completed from subtitles that are already uploaded.** An episode missing from the run's queue because its subtitle went up earlier is filled in from the library instead of dissolving the season into single uploads — and a season whose episodes are ALL up already is a pack of its own, with nothing new in the queue. Every member passes the same gates as a single upload and must be known to the registry as uploaded; a never-uploaded or gate-failed text never enters a pack. One release, one quality, one frame rate still hold, and when one episode cannot be filled, nothing is sent.

**F-M68:** **Der Ablehnungsspeicher ist dauerhaft und je (Datei-Inhalts-Hash, Spur) geschlüsselt.** Eine Spur, die die Qualitätsprüfung abgelehnt hat oder als Dublette ausschied, wird nicht erneut extrahiert; Einzel- und Pack-Weg lesen denselben Speicher.

**F-M69:** **Ein Medium kostet eine Suche, nicht eine Suche je Spur.** Der Upload arbeitet dreistufig: erst werden alle Spuren und losen Untertitel lokal geprüft (Extraktion, Qualität, Dubletten), dann fragt eine Suche mit kommagetrennter Sprachliste alle offenen Sprachen, dann geht hoch, was auf SubDL fehlt.

**F-M71:** **Eine SDH-Variante geht mit gesetztem SDH-Kennzeichen hoch.** Das Kennzeichen ist `sdh`; `hi` ist keine Marke, sondern der Sprachcode für Hindi. Beide Richtungen lesen die SDH-Angabe aus denselben zwei Quellen: dem Sprachen-Flag zuerst, dem Spurtitel als Rückfall.

**F-M85:** **Die Spurenauswahl trägt den Optional-Marker.** Ein Spuren-Index, der nicht existiert, bricht die Extraktion damit nicht ab und liefert keine doppelte Spur; der Aufrufer wiederholt die leeren Spuren einzeln und verbucht eine, die leer bleibt, als echten Fehlschlag.
### 3.1 Quality Gates — Upload

The upload chain, in the order the code applies it. Each switchable gate names its own switch and default.

**F-M16 [B2]:** **Minimum size and minimum cue count.** The 2 KB size floor applies to UPLOADS only and is always on. The cue count (30) applies in BOTH directions and is switchable per direction, default on. Below either, the stream is skipped as "too small".

**F-M13 [B2]:** **SRT parser validation (switchable, default on):** ≥1 cue, monotonically increasing timestamps, plausible cue duration (>0.1 s, <10 min — long music cues stay legal)

**F-M14 [B2]:** **Sync plausibility (switchable, default on):** cue span vs. media runtime (+5 min tolerance; all cues concentrated in <10 % of runtime = reject)

**F-M15 [B2]:** **Language verification (switchable in each direction, default on):** detected language of the content (bundled n-gram classifier) vs. stream tag; undetectable content passes (fail-open).

**F-M375:** **On upload a lying tag is corrected, not rejected.** A track whose text speaks another language than its tag claims is uploaded under the detected language — the tag is what is wrong, not the subtitle — and the correction is logged with the tag that lied. A tag that names a relative of the detected language (the Norwegian written forms, Czech vs. Slovak, the Serbo-Croatian block) is no lie and is left alone. In the download direction the same check keeps rejecting: there the file is saved under its tag, so a corrected language would leave a mislabelled file on disk.

**F-M142:** **A pair already uploaded in this run is recorded as rejected, never as uploaded.** A stream whose (media content hash, language, hearing-impaired) pair is already up is skipped, and its position is recorded with the reason `duplicate-self-echo`. It is not marked `uploaded`. The hearing-impaired variant has its own key space, so a normal upload never blocks an SDH variant.

**F-M17c [B2]:** **Own upload dedup:** (item, language) pair + content hash in persistent state — never upload an already-uploaded or in-session-processed content hash twice. Depends on the canonical SRT form (F-M185): the hash is only stable across extraction runs when the payload is normalized first.

**F-M17x [B2]:** **Remote duplicate learning:** when SubDL rejects an upload with "Duplicate upload: identical file already published", the normalized content hash is stored as `duplicate-remote`. Future runs skip that content before any API call.

**F-M74:** **An `und` stream is resolved by detection before upload. Switchable, default on.** With the switch on, the detected language replaces the tag and the stream uploads normally; a detection that fails skips the stream. With the switch off, every `und` stream is removed from the upload set and logged. Detection needs text to work on, so a stream below the 2 KB floor (F-M16) is skipped as too little text.

### 3.2 Dry Run — Upload

**F-M22 [B1]:** **Dry-run switch, one per direction (both default off): the run walks its decision path, reports what it WOULD do, and writes nothing.** No file is transferred in either direction and no stored verdict is written. The two directions do not cost the same: the download dry run searches (F-M277), the upload dry run does not (F-M276).

**No stored verdict is written or cleared by a dry run** — a mark a dry run sets takes the work away instead of describing it, because the next real run reads the mark and skips the file.

**The rule covers every write that sits BEFORE the mode's own exit, not only the writes after it** — a dry run exits per item, so a write in the per-item preamble runs unless it is
guarded at its own site. What that covers, and the two sanctioned exceptions, are stated once in **F-M287**.

**F-M276:** **The upload dry run walks the upload decision path up to the transfer and spends no API call.**

It runs: the extraction and the quality gates of 3.1, the id resolution, and the per-file candidate list.

It does not run: the login (F-M8), the duplicate pre-check — which a real run does not make either (F-M184) — and the upload transfer. No search call is made, so no search quota is spent.

The report is one line per stream: `DRY-RUN would upload <neutral name>`, the neutral name being the one a real upload would send (F-M29, F-M30). **Seit F-M496 eine Zeile je Stream UND je Quelle.**

A dry run writes no completion mark (F-M88c). It has no retry counter to leave stale any more — that state went with F-M60.

**F-M496 [D]:** **Der Trockenlauf spielt die Kette durch, nicht ein einzelnes Ziel — und er verlangt keine Zugangsdaten.** Zwei Löcher, beide am 15.11.2026 im Testbench gemessen: (1) **Der Trockenlauf kannte die zweite Quelle nicht.** Er stand als **ein** Ausstieg vor der Kette (`F-M22`) und schrieb **eine** Zeile `[SubDL] DRY-RUN would upload …` — seit F-M495 fragen aber **beide** Endpunkte hintereinander, und genau das verschwieg der Bericht: der zweite Schalter, die zweite Zeile, die Reihenfolge. Jetzt bekommt **jede Quelle ihre eigene Zeile**, in **derselben Reihenfolge**, in der die Kette sie fragen würde (`UploadSourceOrder`, **eine** Aufzählung für Kette und Trockenlauf statt zweier Wahrheiten), und erst **nachdem** ihr Toggle über `SourceAllowed` gefragt wurde; ein abgeschalteter Endpunkt erscheint nicht, der andere schon. Die Zeilen lauten `[SubDL] DRY-RUN would upload <neutral name> [<lang>]` und `[tvsubtitles] DRY-RUN would upload <neutral name> [<lang>]`. Die Tore gelten im Trockenlauf **lesend** mit: ein Stream, dessen Inhalt schon bei einer Quelle liegt (F-M489) oder der zu einer anderen Quelle gehört (F-M487), wird mit Grund genannt statt als „würde hochladen" ausgegeben — der Trockenlauf zeigt, was der echte Lauf täte, nicht, was er könnte. **Kein API-Aufruf, kein Login, kein Schreibvorgang aus diesen Zeilen** (die QA-Stufe oberhalb schreibt ihre Ablehnungen weiterhin auch im Trockenlauf — eigene, ältere Stelle) — der tvst-Zweig hatte bis hierher **keinen eigenen** Schutz (der Ausstieg deckte die Kette nur als Ganzes ab, und der Endpunkt selbst holt im Trockenlauf noch die Show-Liste); die Zeile steht deshalb **vor** jedem Aufruf, und der Endpunkt bekommt zusätzlich den Schalter mitgegeben (`TvSubtitlesUploadEndpoint(…, dryRun: _config.DryRun)`) statt eines festen `false`. (2) **Der Trockenlauf wurde an den Zugangsdaten abgewiesen.** `MissingCredentials()` verlangte für einen SubDL-Upload Benutzername und Passwort, obwohl ein Trockenlauf **keinen einzigen** Aufruf macht — der Lauf starb also an etwas, das er nie benutzt hätte. Diese Forderung ist weg: was eine Quelle zum **Anfangen** braucht, fragt `IsConfiguredForUpload` (F-M497) **live**; im Trockenlauf wird sie nicht gestellt. Die beiden Regeln widersprechen sich nicht: *nötig* ist im Trockenlauf nichts, *anwesend* sein muss eine Quelle nur, wenn sie wirklich antritt. Geprüft von T215.

### 3.3 Season Packs — Upload

Dieses Unterkapitel fasst zusammen, was ein Pack ist, wann er beim Upload entstehen darf und was er nicht enthalten darf. Es führt **keine neuen Anforderungen** ein; die normativen Sätze stehen unter den zitierten IDs.

**Was ein Pack ist.** Ein Container für genau **eine Staffel × eine Sprache × eine Variante** (SDH oder Nicht-SDH), gefüllt mit **allen ausgestrahlten Folgen**. 26 Sprachen bedeuten 26 Packs, nicht ein Sammelarchiv. SDH wird nur mit SDH gemischt; Forced-Spuren gehören zur Folge und nie in einen Staffelpack; alle Mitglieder tragen dieselbe Release (F-M389, F-M390).

**Wer packbar ist.** Jede Staffel, die auf Platte vollständig ist. Seit F-M420 entscheidet der Einzelpfad über die Reihenfolge im Lauf; die Queue bestimmt nur noch, was überhaupt anfällt. Eine Staffel mit 7 von 8 queued wird aufgefüllt und gepackt, statt in Einzel-Uploads zu zerfallen (F-M394).

**Reihenfolge im Lauf.** Ein **Arrival**-Lauf lädt ausschließlich einzeln hoch und baut keinen Container (F-M418). Ein **Refetch**- oder **manuell** gestarteter Lauf lädt erst jede Position einzeln (mit Id, F-M413) und schließt danach die Staffel mit dem Container ab, der die Einzelnen ablöst (F-M420).

**Woher die Mitglieder kommen dürfen.** Füllmaterial muss von diesem Konto **hochgeladen** worden sein: Beleg ist die `uploaded`-Zeile genau dieser Spur, nicht „Inhalt bekannt" (F-M395). Stammt auch nur eine `.srt` aus SubDL — heruntergeladen oder aus einem geladenen Pack entpackt —, fällt der **ganze** Pack aus (F-M397). Was auf der Download-Seite als `downloaded` gebucht wurde, ist damit dauerhaft kein Pack-Material (§4.4).

**Gates und Rückfall.** Es gelten dieselben Gates wie beim Einzel-Upload (Struktur, Laufzeit, bekannte Inhalte, Mindestumfang, Sprachprüfung). Fällt eine Folge durch, bleibt die Staffel für diese Sprache einzeln; die Folge vergiftet den Pack nicht (F-M387, F-M388).

**Dublettenschutz.** Ein Pack, der mit denselben Mitgliedern und derselben Release schon oben ist, wird nicht erneut gesendet. Nachweis ist **ausschließlich** die `packs`-Zeile; eine Id an den Mitgliedern belegt nichts, weil auch ein Einzel-Upload seine eigene Id schreibt (F-M396, F-M399).

**Aufsaugen und Löschen.** Einzel-Uploads, die in einen späteren Pack aufgehen, werden **vor** dem Pack bei SubDL gelöscht; ihre IDs bleiben in der `packs`-Zeile. Scheitert der Pack danach, steht das mit den IDs im Log (F-M391). Gelöscht wird nur, was der State als **eigener** Upload mit Id führt — der Einzelpfad holt die Id nach jedem Upload aus `/user/mySubtitles` und schreibt sie in dieselbe Embed- bzw. Sidecar-Zeile (F-M412, F-M413, F-M414).

**Trockenlauf und Transparenz.** Im Trockenlauf wird weder gelöscht noch hochgeladen noch gebucht; die Zeile sagt `DRY-RUN would upload season pack`. Die Worker-Zeile trägt den Vermerk **dry** neben dem Ergebniswort, die Zykluszeilen im Log ebenfalls `, dry` (F-M401, F-M402, F-M403).

**Aufräumen.** Pack-Zeilen, deren Pack-Kennung auf keiner Untertitelzeile mehr steht, entfernt der Database Refresh und zählt sie in seiner Summe — sonst legte eine verschwundene Staffel sich selbst für immer still (F-M396).

**F-M405 [P]:** **Der Pack-Filler liest eine Datei einmal je Lauf aus, nicht einmal je Sprache.** Für eine Staffel mit 37 Sprachen waren das 37 volle ffmpeg-Durchläufe über denselben Container — bei acht Folgen 296 Zugriffe, wo acht genügen. Der Filler nutzt denselben Ein-Pass-Aufruf wie der Einzel-Upload seit F-M5 und hält das Ergebnis je Medienpfad für die Dauer des Laufs.
**F-M406 [P]:** **Die Pack-Schleife meldet Fortschritt, damit der Lauf nicht als Stillstand gilt und abgebrochen wird.** Die Laufüberwachung bricht einen Lauf nach sechs Minuten ohne Lebenszeichen ab; die Pack-Schleife baute stumm und wurde deshalb nach elf bis zwölf Minuten mitten im Plan gekillt — bei 659 angekündigten Paaren blieben 28 Packs, was wie Unfähigkeit aussah und ein Abbruch war. Sie meldet jede Folge, die sie ausliest.

**F-M407 [P]:** **Die ausgestrahlte Folgenmenge kommt von TMDb, und künftige Folgen zählen nicht mit.** IMDb antwortet auf die Staffel-Abfrage mit der ganzen Serie — für `tt33270224` S1 mit `total: 20` und den Nummern 1 bis 20, wo die Staffel 7 Folgen hat und 4 gelaufen sind —, und dieses eine Zahl entschied über Vollständigkeit: Die Staffel konnte nie vollständig werden und blieb dauerhaft einzeln. TMDb wird zuerst gefragt (der Staffel-Endpunkt ist je Staffel richtig), IMDb bleibt Rückfall für Titel, die TMDb nicht kennt. Auf Ausstrahlungsdaten kommt es dabei nicht an: eine Staffel ist vollständig, wenn ihre Folgen auf Platte liegen.

**F-M408 [P]:** **Eine Staffel ist vollständig, wenn alle Folgen aus derselben Release-Gruppe vorliegen.** Ein Pack trägt eine Release, eine Qualität und eine Bildrate; eine Staffel aus zwei Gruppen ist kein Pack. Es gewinnt die Gruppe, die alle ausgestrahlten Folgen stellt, und nur ihre Dateien dürfen mit — bei Cupertino S01 (`-MeGusta` E01/E02, `-ELiTE` E03/E04) deckt keine Gruppe die Staffel, sie bleibt einzeln. Die Folgenmenge selbst kommt von TMDb (F-M407).

**F-M410 [P]:** **Der Pack-Filler fragt den Medien-Hash einmal je Folge, nicht zweimal.** Beleg-Hash (F-M395) und Kandidaten-Hash waren zwei Aufrufe derselben Abfrage; jede schrieb ihre eigene oshash-Zeile.

**F-M411 [P]:** **Ein Cache-Treffer wird einmal je Datei protokolliert, nicht einmal je Spur.** Die Trefferzeile entsteht einmal je Datei und nennt die Zahl der Treffer, nicht eine Zeile je Spur.

**F-M412 [P]:** **Der State beantwortet zwei Fragen je Einzelfolge: ob wir sie hochgeladen haben, und ob wir sie löschen können.** Die erste steht im Status der Embed-Zeile (`uploaded`), die zweite in ihrer SubDL-Id — ohne Id gibt es nichts zu löschen. Der Pack nennt deshalb vor dem Absenden jede Mitgliedsfolge ohne Id: `… carries no SubDL id in the store — uploaded by us, but not deletable, they stay beside the pack.`

**F-M413 [P]:** **Die Upload-Id einer Einzelfolge wird beim Hochladen nachgeholt, wenn die Antwort keine enthält.** SubDL antwortet auf einen Upload mit „subtitle sent for review" und ohne Id; die Id steht aber in die Liste der eigenen Untertitel. Sie wird direkt nach dem Upload dort abgeholt — erst über den Dateinamen, sonst über IMDb-Id, Staffel, Folge und Sprache, neueste zuerst — und in dieselbe Zeile geschrieben. Ohne sie kann ein Pack die Einzelfolge, die es ersetzt, nie löschen.

**F-M414 [P]:** **Die Upload-Id wird für Einzelfolgen und für Staffelpacks auf demselben Weg geholt — aus `/user/mySubtitles`, direkt nach dem Upload.** Der Probelauf hat es belegt: Einzel-Upload → `id 376477`, Staffelpack (zwei Folgen, `is_full_season=true`) → `id 376481`, jeweils Sekunden nach der Antwort `"subtitle sent for review"`. Der Abgleich läuft über den **Release-Namen**, weil die Liste den Eintrag so benennt (`…S01…` beim Pack, `…S01E08…` bei der Einzelfolge) und nicht nach dem Dateinamen; die Sprache kommt dort ausgeschrieben („Turkish") und wird über die Sprachzuordnung verglichen. Ohne Treffer bleibt beim Pack der Name als Kennung, bei der Einzelfolge die Spalte leer.

**F-M415 [P]:** **Die Staffelpack-Prüfung läuft vor der Extraktion.** Liegt die Staffel in dieser Sprache schon als Pack im Store liegt, wird kein einziges Mal extrahiert: `season pack skipped — …: already up as a pack, nothing is extracted.` Zuvor zog der Lauf erst jede Folge aus jeder Datei und stellte danach fest, dass nichts zu tun war — der teuerste Teil des Zyklus. Die übrigen Vorprüfungen (Mixed Releases, unvollständige Staffel, QA-Durchfall) verhindern weiterhin nur den Pack, nicht die Einzel-Uploads.

**F-M416 [P]:** **Vollständigkeit wird je Sprache aus dem State entschieden, vor der Extraktion.** Eine Staffel kann für EN vollständig und für DE unvollständig sein, weil zwei Folgen keine deutsche Spur tragen. Die Folgen-Abfrage je Sprache liest aus dem Store, welche Folgen diese Sprache kennen; fehlt eine, entsteht kein Pack für diese Sprache („… no pack for it, this pass sends nothing"); die Folgen, die da sind, gehen über den Einzelpfad hoch (F-M420). Zusammen mit der Pack-Prüfung (F-M415) und der Release-Gruppe (F-M408) sind damit alle Pack-Entscheidungen vor der Extraktion möglich; nur der QA-Durchfall bleibt beim Untertiteltext.

**F-M417 [P]:** **Die Prüfreihenfolge ist: Store, dann Dateinamen, dann Extraktion.** Je Staffel fragt die Planung zuerst die Sprachen der Staffel ab, dann für jede bekannte Sprache die Pack-Prüfung und die Folgen-Abfrage; erst wenn mindestens eine Sprache offen und vollständig ist, wird die Release-Gruppe gelesen und die Kandidatenzeile geschrieben. Bleibt nichts übrig, endet die Staffel vor jeder Extraktion („already up as a pack, nothing is extracted" bzw. „not every aired episode carries this language in the store") — vor jeder Extraktion. Damit steht die teuerste Arbeit ganz hinten.

**F-M418 [P]:** **Ein Arrival-Lauf lädt nur einzeln hoch — dort entsteht kein Staffelpack.** Die Frage, welche Staffel vollständig ist, wird im Arrival gar nicht erst gestellt (ohne Komplettlauf), jede Position geht über den Einzelpfad; das Log sagt es mit `no season pack this run — an arrival run uploads single only`. Grund: der Arrival-Lauf arbeitet den Zugang, der ihn ausgelöst hat, nicht die Staffel. Technisch reicht der Dispatcher den Cycle-Grund bis in die Pipeline (Richtungslauf → Pipeline → deren Füllschalter = !IsArrivalTrigger(reason)`), wahr für alles außer `event` und `arrival-followup`.

**F-M420 [P]:** **In Refetch- und manuellem Lauf gilt: erst Einzelupload, dann Staffelpack.** Für einen Container wird nichts mehr zurückgehalten — `packable` bleibt leer, kein Kandidat landet in einem Puffer, die Schließreihenfolge fällt weg; jede Position geht sofort einzeln hoch und schreibt dabei ihre SubDL-Id (F-M413). Der Pack-Plan wird deshalb **nach** der Folgeschleife aus dem Store gerechnet: packbar ist eine Staffel je Sprache, wenn jede ausgestrahlte Folge eine `uploaded`-Zeile trägt und keine `packs`-Zeile existiert (F-M399). Der Container entsteht dann aus denselben Texten und löscht die Einzel-Uploads, deren Ids er kennt, vorher bei SubDL (F-M391). Vorher gerechnet sah der Plan die Uploads desselben Laufs nicht und die Staffel wartete einen ganzen Zyklus auf ihren Container. Konsequenz: eine komplette Staffel kostet je Lauf Einzel-Uploads **und** Container — gewollt.

**F-M421 [P]:** **Die SubDL-Upload-Id jeder Einzelfolge steht im Log — immer, und mit ihrer Herkunft.** Bisher schwieg der Einzelpfad, wenn die Upload-Antwort die Id schon enthielt oder wenn sie gar nicht zu ermitteln war; geschrieben wurde die Zeile nur nach einem Nachholen aus `/user/mySubtitles`. Jetzt gibt es je Upload genau eine Zeile: `upload id {Id} from the upload answer for {File} [{Lang}]` (Id kam in der Antwort), `upload id {Id} taken from mySubtitles for …` (nachgeholt, F-M413) oder — wenn keine zu bekommen ist — eine Warnung `no upload id for {File} [{Lang}] — this single stays beside a pack that replaces it, because there is no handle to delete it.` Der Pack nennt bei fehlender Id `no pack id for {Imdb} S{Season} [{Lang}] — the pack row carries its name`. Grund: die Id ist der einzige Griff, mit dem ein späterer Pack die Einzelfolge löschen kann; eine stille Lücke ist ein unerklärbarer Rest auf SubDL.

**F-M422 [P]:** **Die Ablehnung einer Staffel nennt ihre Belege.** Bisher sagte die Zeile nur „no language is uploaded for every episode, no pack is built" — ein Urteil ohne Beweis, aus dem nicht hervorging, ob eine Folge fehlt, ob das SDH-Kennzeichen die Sprache in zwei Paare spaltet oder ob ein Medien-Hash nicht mehr passt. Das Log folgt: `Closest: [EN] 9/10, missing 4 | [DE/SDH] 3/10, missing 4,5,6` (bis zu drei nächstbeste Paare mit Zählung und fehlenden Folgen) und `Episodes with no uploaded row at all: 4,7` (Folgen, zu denen es gar keine `uploaded`-Zeile gibt — das ist ein Hash-/Waisenproblem, kein Abdeckungsproblem). Grund: die Prüfung ist der Flaschenhals zwischen 7.958 hochgeladenen Zeilen und den tatsächlich gebauten Packs; ohne diese Zahlen ist jede Erklärung dafür eine Vermutung.

**F-M423 [P]:** **Bereits veröffentlichte Uploads lassen sich in die Registry übernehmen** (der Übernahme-Endpunkt). Die Vollständigkeitsprüfung einer Staffel liest ausschließlich `uploaded`-Zeilen dieser Registry — Zeilen, die nur dieses Plugin schreibt. Alles, was ein früherer Seeder unter demselben Konto veröffentlicht hat, ist damit unsichtbar und ergibt weder Vollständigkeit noch Pack. Der Endpunkt nimmt je Eintrag `{path, subPos, language, subdlId, hearingImpaired}` und schreibt genau die Zeile, die der Uploader geschrieben hätte: `Status = uploaded` mit der Id, die die Einzelfolge später löschbar macht (F-M412). Er lädt nichts hoch, löscht nichts und spricht SubDL nicht an — er trägt nur nach, was dieses Konto ohnehin veröffentlicht hat. Die Antwort zählt übernommen, Datei fehlt, unlesbar, Id fehlt, Position falsch; die Log-Zeile nennt dieselben Zahlen mit der Dateizahl.

**F-M424 [P]:** **Ein Arrival-Lauf prüft, ob seine eigenen Items eine Staffel schließen, und baut dann den Pack ausschließlich aus ihnen.** F-M418 hatte den Arrival-Lauf vollständig aus der Pack-Bildung genommen; diese Anweisung ersetzt das. Ablauf: nach der Item-Schleife wird je (Serie, Staffel) verglichen, ob die **in diesem Lauf eingereihten** Folgen die ausgestrahlte Folgenmenge abdecken. Ja → `season pack closing` aus genau diesen Folgen (die Pack-Planung bekommt die erlaubte Folgenmenge als Schnitt, der Füll-Lauf läuft über dieselbe Menge). Nein → `arrival: … brought {Have} of {Aired} episode(s)`, kein Pack, die Folgen gehen einzeln hoch. Der Schnitt ist die eigentliche Zusage: die Bibliothek hält die Staffel meist vollständig, ohne ihn würde der Pack Mitglieder enthalten, die dieser Lauf nie angefasst hat — also Zeilen aus dem Store statt aus der Ankunft. Nicht-Arrival-Läufe (Refetch, Manual, Scheduled) sind unverändert: dort gilt weiter F-M420 (erst Einzelupload, dann Staffelpack aus dem Store).

**F-M425 [P]:** **Die Pack-Regel gilt für jeden Trigger gleich.** Ein Lauf — Ankunft, Refetch, Manual, geplant — stellt dieselbe eine Frage: Sind die in **diesem** Lauf eingereihten Folgen die ganze Staffel? Ja → Pack aus genau diesen Items. Nein → kein Pack, die Folgen gehen einzeln hoch. Damit fällt der Fill-only-Weg weg (F-M418): er baute Container aus Zeilen, die ein früherer Lauf im Store hinterlassen hatte, ohne dass dieser Lauf eine dieser Folgen angefasst hätte. Die Property wird nicht gesetzt, und der Schnitt (die erlaubte Folgenmenge) wird für jeden Lauf an Plan und Füller übergeben. Die Log-Zeilen heißen `queue: … brought {Have} of {Aired} episode(s)` bzw. `queue: … brought all {Aired} episode(s)` — der Trigger ist für die Entscheidung ohne Belang.

**F-M426 [P]:** **Eine Staffel, deren Sprachen alle schon in einem Pack liegen, wird nicht untersucht.** Die Frage „sind die eingereihten Folgen die ganze Staffel?" (F-M424/F-M425) wird deshalb erst gestellt, wenn die Offene-Sprache-Prüfung sie bejaht: für die eingereihten Folgen wird die Sprache ihrer `uploaded`-Zeilen gegen die Pack-Prüfung gehalten; ist jede Sprache bereits in einem Pack, endet die Staffel mit `queue: … every language is already inside a pack, nothing is investigated` — keine Extraktion, keine Plan-Zeile. Eine Staffel ohne jede `uploaded`-Zeile gilt als offen (es ist auch nichts gepackt). Damit entfallen die Zeilen, die nur meldeten, dass ein Pack schon existiert.

**F-M427 [P]:** **Der DB-Reset löscht die Archiv-Ablage mit.** `scope=all` entfernt zusätzlich `<Datenordner>/archive-cache` über `ArchiveDiskCache.PurgeDirectory` (alle `*.zip` und liegengebliebene `*.tmp`); die Log-Zeile nennt die Zahl (`status counters zeroed with the database, N cached archive(s) purged`). Grund: ein Reset sagt „alles vergessen" — Archive weiterzuverwahren hieße, dem nächsten Lauf Dateien aus einem Zustand zu servieren, der nirgends mehr existiert. Die Ablage ist plugin-eigen und nicht Teil der Datenbank (F-M370), deshalb der eigene Aufräumschritt; `Trim` bleibt unverändert für Alter und Größe zuständig. Andere Scopes (`upload`, `download`) lassen den Cache unberührt.

**F-M428 [P]:** **Die Vorgaben der Archiv-Ablage sind 7 Tage und 256 MB**. Geändert in der Konfiguration (Archiv-Ablage 7 Tage und 256 MB) und in der Konfigurationsseite, die dieselben Zahlen als Rückfall benutzt, wenn ein Feld leer ankommt. Eine Woche deckt den Fall ab, für den die Ablage gebaut wurde — ein Staffelarchiv, das die kostenlose Tagesgrenze über mehrere Läufe verteilt (F-M370) — und die Ablage bleibt klein. **Bestehende Installationen behalten ihre gespeicherten Werte**, weil die Konfiguration die Zahlen ausschreibt; die neuen Vorgaben greifen bei einer frischen Installation oder nach dem Zurücksetzen der Felder.

**F-M429 [P]:** **Der Pack läuft vor den Einzeluploads.** Nach der Staffelplanung und dem Login, aber vor der Item-Schleife, wird jeder geplante Pack gefüllt und hochgeladen; erst danach gehen die Einzel hoch. Was ein Pack übernommen hat, merkt sich der Lauf in `_packAbsorbed` (Serie, Staffel, Sprache, Variante); eine Position daraus wird in übersprungen (`carried by the pack built in this run, no single beside it`). Packs, die nicht hochgehen, hinterlassen keine Spur in dieser Menge — ihre Folgen gehen im Einzel-Lauf hoch, also geht nichts verloren. Zwei Folgen dieser Umstellung: (a) Die Pack-Planung nimmt die Sprachen aus den **eingebetteten Spuren** der Dateien statt aus Store-Zeilen, weil ein neuer Lauf noch keine Zeilen hat (Store bleibt Rückfall, wenn die Streamliste nicht lesbar ist); (b) die Beweisregel F-M395 ist umgeschrieben — ein Pack darf die **erste Veröffentlichung** eines Textes sein (`first publication: the pack carries this text up`), Text, der nur aus einem Download oder einer Ablehnung bekannt ist, bleibt weiter ausgeschlossen.

**F-M430 [P]:** **Die Release-Gruppe wird in allen Schreibweisen gelesen, die der Dateiname hergibt.** Die alte Regel nahm nur das letzte `-`-Segment; sie übersah damit drei Formen, die dieselbe Angabe tragen: punktgetrennt (`…x264.YIFY`), leerzeichengetrennt (`…HEVC x265 BONE`, auch in Klammern: `…(1080p AMZN WEB-DL x265 Ghost)`) und mit einem Site-Tag dahinter (`…x265-MeGusta[EZTVx.to]`, `…x264.[MPup]`, `…x264-[YTS.LT]`). Die Regel: eine abschließende Klammer oder Klammergruppe wird abgetrennt und der Rest zuerst gefragt — dadurch bleibt die Gruppe vor dem Site-Tag sichtbar —, erst dann wird der Klammerinhalt gelesen (Marke hinter einer Domain nur bei kurzen Namen, ein langes Wasserzeichen wie `THENKIRI.COM` ist keine Gruppe). Danach zählt das letzte Token nach `-`, `.` oder Leerzeichen, aber **nur wenn das Token davor ein technisches ist** (Codec, Quelle, Audio, Auflösung, Bit-Tiefe, HDR, Edition); ein Segment mit Leerzeichen ist ein Titel. Zusätzlich werden ein Unterstrich-Paar (Gruppenname mit Unterstrich-Paar) und eine zweite Endung (`.mp4`) aufgelöst. **Nichts wird erfunden:** ein Name ohne Release-Angabe („For All Mankind S03E04") liefert weiter `null`, und ein technisches Token wie `-AAC5.1` bleibt `null`.

**F-M431 [P]:** **Der Pack-Füller versucht jede Spur der gesuchten Sprache, nicht nur die erste.** Zuvor nahm die Füllsuche die erste eingebettete Dialogspur, die zu Sprache und HI-Variante passt; fiel sie durch — Extraktion, Qualitätstor, eine falsche Sprachangabe im Tag, oder Text, der nur aus einem Download bekannt ist —, galt die Folge als nicht packbar, obwohl eine zweite Spur derselben Sprache in derselben Datei lag. Jetzt sammelt die Füllsuche alle passenden Positionen (in Dateireihenfolge, aus derselben Aufzählung, aus der auch die Extraktion liest, damit eine Position in beiden Stellen dasselbe bedeutet) und die Positionsprüfung prüft sie einzeln. Der erste Track, der alle Tore besteht, wird das Mitglied; jeder verworfene nennt seinen Grund und reicht weiter (`trying the next track of this language`), ein gelungener Nachrücker wird benannt (`track {Nth} of {Count} joins the pack`). Erst wenn alle Spuren der Sprache scheitern, bleibt die Folge mit `none of the {Count} track(s) of this language qualifies` draußen.

**F-M432 [P]:** **Die Upload-Id wird auch aus der Veröffentlichungsliste geholt.** Die Id ist der einzige Griff, mit dem ein eigener Eintrag wieder gelöscht werden kann (ersetzt ein Pack eine Einzelfolge), und sie steht in der Liste der eigenen Untertitel — einer Liste, die nur nicht veröffentlichte Einsendungen führt und deshalb leer ist, sobald SubDL alles freigegeben hat. Ein Film hat zusätzlich keine Staffel und keine Folgennummer, über die sich ein Eintrag wiederfinden ließe, hatte also gar keine Id. Neu liest die Abfrage der Veröffentlichungsliste (neueste zuerst, seitenweise, wie die API sie liefert); die Auflösung der eigenen Upload-Id und der eigenen Pack-Id fragen sie, wenn die Einreichungsliste nichts hergibt, und gleichen über den **Release-Namen** ab — denselben Griff, den Pack und Einzelfolge schon benutzen. Die Log-Zeile nennt die Quelle (`… taken from myPublishedSubtitles for …`). Der Eintragsparser versteht dafür auch die Schreibweisen der Veröffentlichungsliste (Anlagezeitpunkt, Staffel als `s`).

**F-M433 [P]:** **Der Store entscheidet nicht allein — die Dateien sind der zweite Zeuge.** Die Vollständigkeitsprüfung je Sprache fragt den Store, also das, was schon hochgeladen oder beobachtet wurde. Eine Staffel, deren Texte zum **ersten** Mal hochgehen, kennt der Store nicht. Deshalb fragt der Füller auch die Dateien: Die Sprachabfrage nennt die Sprachpaare, die in den Spuren einer Staffel liegen (Spurlisten, keine Extraktion), die Träger-Prüfung beantwortet die Frage je Folge. Der Pack-Plan nimmt seine Sprachen aus **Store und Dateien**, und die Vollständigkeit gilt als gegeben, wenn **entweder** der Store **oder** die Dateien die Sprache in jeder ausgestrahlten Folge zeigen; nur wenn beide sie nicht finden, bleibt es beim Skip (Log nennt dann beide Zahlen). Der Fall ohne jede Spur in dieser Sprache spart weiterhin die Extraktion, weil eine Spurliste nichts kostet (F-M416).

**F-M434 [P]:** **Die Dateien werden zur Pack-Zeit auch gefunden.** F-M433 fragte die Dateien, bekam aber für jede Sprache „nein": die Zuordnung Folge → Datei lief über `episode.Series?.ProviderIds`, und dieses Serienobjekt ist nicht geladen, wenn die Pack-Phase frägt — sie läuft seit F-M429 **vor** der Item-Schleife, die die Staffel-Anker füllt. Belegt an „For All Mankind" S1: die Dateien tragen `eng` (E4 zusätzlich 35 Sprachen), der Lauf meldete trotzdem für **jede** Sprache „weder Store noch Dateien". Zwei Korrekturen: der Vollständigkeits-Scan übergibt seine Episoden-Items an den Füller (`_seasonEpisodes`), und die Zuordnung läuft über die Rohauflösung (IMDb, Staffel, Folgennummer) statt über das Serienobjekt — in der Träger-Prüfung wie in der Sprachabfrage. Der Store bleibt der erste Zeuge; die Dateien sind der zweite, der tatsächlich antwortet.

**F-M435 [P]:** **Gespeichert wird die Eintrags-Id, nicht die Upload-Id.** Die Veröffentlichungsliste führt je Eintrag zwei Nummern: `id` ist die Id des veröffentlichten Eintrags, `subtitles_uploaded_id` die interne Upload-Id. Der Löschaufruf `/user/deletePublishedSubtitle` nimmt die Eintrags-Id; beide Nummern liegen in verschiedenen Bereichen, eine Zeile mit der Upload-Id benennt nach der Freigabe also nichts mehr. Der Eintragsparser trennt sie (Eintrags-Id, Upload-Id) und bietet den zu speichernden Griff an, der zu speichern ist: die Eintrags-Id, sobald ein veröffentlichter Eintrag sie nennt, sonst die Upload-Id einer noch offenen Einsendung. Staffelpack, Einzelfolge und Film speichern über diesen Griff; die Log-Zeile nennt beide Nummern (`entry id … (upload id …)`). Weil eine Einsendung zum Upload-Zeitpunkt meist noch offen ist, zieht die Nachbereitung die Eintrags-Id nach: sie liest die Veröffentlichungsliste, bildet Upload-Id → Eintrags-Id und schreibt die gespeicherten Griffe in den drei Tabellen um, gemeldet als `stored handle(s) moved to their published entry id`.

**F-M436 [P]:** **Ein offener Fire ist der letzte Zustand — Ampel und Text sagen dasselbe.** Steht für einen Worker ein Fire aus, dann zeigt seine Zeile `defer` in Gelb, unabhängig vom Ergebnis des abgelaufenen Laufs; nur ein laufender (`run`) bleibt unangetastet. War der letzte Lauf **fehlgeschlagen**, nennt die Zeile unter der Liste zuerst den Fehlschlag samt Ursache (`last run failed: <Detail>`) und danach den nächsten Versuch (`— next attempt <Tag Zeit>`); „deferred" ist nur der Rückfall ohne eigene Ursache. Rot „fail" wäre an dieser Stelle falsch: der Worker ist nicht kaputt, er wartet auf seinen nächsten Anlauf. **Die Fehlschlag-Zeile bleibt stehen, bis der nächste Lauf erfolgreich war** — sie hängt nicht am offenen Fire, denn die Zeile der Workers-Liste nennt nur das Wort, nie die Ursache. Ein gesunder Worker ohne Fire sagt nichts.

**F-M437 [P]:** **Der Nachlauf blättert bis zum Wasserzeichen, nicht bis zu einer festen Seitenzahl — und er läuft täglich.** Die Veröffentlichungsliste kommt neueste zuerst; der Lauf merkt sich in das Wasserzeichen des Nachlaufs den jüngsten Anlagezeitpunkt, den er gesehen hat, und endet, sobald eine Seite ältere Einträge erreicht. Im Normalbetrieb sind das wenige Seiten; nach einer Pause holt derselbe Lauf auf, bis er das Wasserzeichen erreicht. der Seitendeckel des Nachlaufs (Default 20000) ist nur noch der Deckel für so einen Aufholauf, nicht die Tiefe des Laufs — Standard **20.000** Seiten, damit auch ein Rückstand von hunderttausenden Einträgen in einem Lauf erreichbar bleibt; gebremst wird so ein Aufholauf durch das Laufzeitlimit, nicht durch die Seitenzahl. Der Takt steht auf **täglich**, weil eine Eintrags-Id erst mit der Freigabe existiert und ein frischer Upload sie sonst bis zu einer Woche nicht bekommt. Das Wasserzeichen wird über den partiellen Schreiber abgelegt, damit ein leerer Speicher keine Benutzereinstellungen überschreiben kann.

**F-M438 [P]:** **Ein plugin-eigenes Feld, das in der Konfigurationsdatei noch nicht gesetzt ist, wird so geschrieben, dass Jellyfin es beim nächsten Laden auch liest.** Der partielle Schreiber (F-M201) setzt nur den Elementtext. Zwei Fälle gehen dabei verloren: ein `Nullable<DateTime>` steht in der Datei nicht etwa gar nicht da, sondern als `<PostprocessWatermarkUtc xsi:nil="true" />` — der Text wurde gesetzt, das `xsi:nil` blieb, und der nächste Ladevorgang las wieder `null`; ein Feld, das die Datei überhaupt nicht kennt (Konfiguration einer älteren Version), wurde ans Dokumentende gehängt, und Jellyfins Serializer liest Elemente in **Deklarationsreihenfolge** — alles hinter einem später deklarierten Element wird ignoriert. Beides sah wie ein erfolgreiches Speichern aus und war nach genau einem Neustart weg. Der Schreiber entfernt deshalb `xsi:nil` beim Setzen und fügt ein unbekanntes Feld vor dem ersten Element ein, das die Konfiguration **nach** ihm deklariert. Beide Fälle sind festgehalten, die alte Schreibweise als Gegenprobe.

**F-M440 [P]:** **Ein Pack-Fill nimmt den Untertitel, der bereits hochgeladen ist — er extrahiert die Datei nicht erneut für jede Sprache.** Der Text einer Folge, die schon als Einzelupload draußen ist, wird beim Pack-Bau aus der Datei gelesen (der einzige Ort, an dem er noch liegt, denn der Store hält nur Hashes, Status und SubDL-Ids) und dann im **Arbeitsordner des Laufs** abgelegt. Ein zweiter Pack, der dieselbe Folge braucht, liest sie von dort statt ffmpeg erneut zu rufen — und **nur die tatsächlich gefragten Spuren**, nie alle Spuren des Containers.

**F-M441 [P]:** **Die Untertitel liegen nur für die Dauer eines Laufs auf Platte.** Der Arbeitsordner ist `<Temp>/subdl-scribe/run-<Zeitstempel>-<Id>`, eine Datei je Folge/Sprache/Variante/Spur (`tt1234567_s01e02_de_hi_p2.srt`). Am Laufende wird er gelöscht; was ein abgebrochener Lauf hinterlässt, räumt der Start des nächsten Laufs weg (älter als zwei Tage). Es gibt **keinen dauerhaften Text-Store**, keinen Deckel und keinen Aufräum-Task. Reihenfolge je Staffel: **erst Pack bauen und hochladen, dann die aufgenommenen Einzeluploads per Eintrags-Id bei SubDL löschen** — die Löschung bleibt wie beauftragt **vor** dem Pack-Upload (F-M391) —, zuletzt die Folgen, die im Pack nicht unterkamen, einzeln hochladen.

**F-M442 [P]:** **Gleichheit der Quelle wird VOR der ersten Extraktion geprüft, und die Skip-Zeile sagt, was wirklich passiert.** Qualität steht in Pfad und Dateiname, die Framerate in der Stream-Liste — beides ohne ffmpeg lesbar. Weichen Qualitätsstufen oder Framerates über die ausgestrahlten Folgen ab, wird die Staffel abgelehnt, ohne eine einzige Datei zu extrahieren; die Zeile nennt die abweichenden Folgen (`S01E04 (ATVP.WEB-DL/23,809)`). Statt „nothing is re-sent — it is already up" steht der tatsächliche Zustand: sind alle Folgen oben, wird nichts erneut gesendet; sonst gehen die fehlenden danach als Einzeluploads hoch.

**F-M443 [P]:** **Ein bereits abgelehnter Track wird nicht erneut extrahiert.** Der Fill fragt vor dem ffmpeg-Aufruf den Ablehnungsspeicher je (Datei-Hash, Spur) — denselben, den der Einzelupload-Pfad seit F-M68 benutzt. Sind alle Spuren einer Sprache abgelehnt, wird nicht extrahiert.

**F-M444 [P]:** **Liegt eine Folge in der Bibliothek mehrfach (zweite Fassung), bekommt jede Datei einen eigenen Versuch.** Zuvor entschied die erste Fundstelle; fiel diese Datei durch ein Gate, starb das Pack, obwohl eine zweite Fassung derselben Folge die Sprache ebenfalls trug. Die Dateien werden in Bibliotheksreihenfolge probiert; die Zeile nennt, welche Fassung gewonnen hat bzw. warum die vorige durchfiel.

**F-M445 [P]:** **Es gibt keinen festen Anker für den Upload-Nachlauf: jeder beendete Upload-Lauf terminiert ihn neu, und der letzte Auftrag gewinnt.** Es gibt kein Intervall, keinen gewürfelten Tag-plus-Uhrzeit-Anker und keinen Mindestabstand mehr; ein noch offener Termin wird **storniert und ersetzt**, nicht addiert. Endet Upload 1 um 12:15, steht der Nachlauf auf 12:45; endet Upload 2 um 12:30, wird auf 13:00 umterminiert. **Der Termin wird beim Start eines Upload-Laufs storniert** — ein Auftrag aus einem früheren Lauf würde gegen einen Zustand feuern, den dieser Lauf gerade ändert — und am Laufende neu gesetzt. **Ein Nutzerstopp storniert den terminierten Postprocessing-Lauf** — Stop heißt Stop; der nächste reguläre Upload-Lauf terminiert wieder. Vorlaufzeit einstellbar (die Vorlaufzeit des Upload-Nachlaufs (Default 30 min, 0–1440), **0–1440**, Default 30), **0 = inaktiv**: kein Termin, und ein offener wird storniert. Der Termin überlebt einen Neustart; bei ausgeschaltetem Upload wird nichts terminiert (F-M291). **Die Statusseite nennt ihn in einer eigenen Zeile unter den Workern** („Postprocessing due …"); die Zeile verschwindet, sobald der Lauf startet. Bewusst nicht an der Worker-Ampel: da jeder Upload-Lauf terminiert, stünde sie dauerhaft auf Gelb und sagte nichts mehr. Grund für alles: der Handle-Remap Upload-Id → Eintrags-Id und das Wegräumen abgelehnter Uploads hingen am Tagesanker und liefen erst am nächsten Morgen — ein am selben Tag gebautes Pack löschte seine Einzeluploads noch mit der Upload-Id, die der Löschaufruf über die Eintrags-Id nicht kennt (F-M435).

**F-M446 [P]:** **Ein Arrival-Lauf ist im Pipeline-Kern als solcher erkennbar.** Das Arrival-Flag stand seit der Umstellung auf den Queue-Scope als Literal `false` in beiden Richtungen — der Scope (nur die frisch eingetroffenen Ids, F-M233) trug die Einschränkung, das Flag sagte keiner Richtung mehr, dass sie ein Arrival-Lauf ist. Damit galten die beiden Arrival-Ausnahmen nie: ein Arrival-Lauf, der auf das Tageslimit (429) trifft, wartet **nicht** aus, sondern stoppt sauber und überlässt das Warten dem geplanten Lauf — sonst blockiert er den Run-Lock für Stunden (F-M65, Nutzerentscheidung Option B). Das Flag wird aus dem Scope abgeleitet (nicht-nulliger Scope), in beiden Richtungen gleich. Die Scoping-Regel selbst bleibt: **Arrival lädt nur die Arrivals, Manual und Refetch die ganze Warteschlange.**

**F-M447 [P]:** **Der terminierte Nachlauf-Auftrag steht in der Worker-Statuszeile als `schedule`, nicht als `deferred`.** Der erste Lauf dieses Auftrags ist planmäßig; `deferred` bleibt den Vertagungen vorbehalten. Das Wort gilt nur für diesen Auftrag, alle übrigen Worker behalten ihre echten Vertagungen. Eine gescheiterte Zeile behält ihren Fehlertext (F-M436).


**F-M448 [P]:** **Ein Seed-Lauf, der auf einen laufenden Bibliotheks-Scan trifft, wird vertagt statt ausgeführt.** Maß der Vertagung ist der Job-Abstand (Default **15 min**, einstellbar **5–120 min**). Wartelisten, Scan-Zeitstempel und die Merkliste der während des Laufs eingetroffenen Items (F-M233) bleiben unverändert; nach Ablauf der Wartezeit wird der Lauf erneut angestoßen. Die Statuszeile führt die Wartezeit als `deferred` mit „library scan running — retry in <N> min". Als laufend gelten Bibliotheks-Scan und Medien-Aktualisierung.

**F-M475 [D]:** **Ein Aufschub ist ein Termin der Richtung, kein Timer — und er nennt seinen Grund.** Trifft ein Seed-Lauf auf einen laufenden Bibliotheks-Scan (F-M448), wird die Wartezeit als geplanter Lauf der betroffenen Richtung eingetragen: Zeitpunkt = jetzt + Job-Abstand, ohne Zufallszuschlag. Eingetragen wird nur die Richtung, die der Zyklus angefordert hat; ein Lauf über beide Richtungen trägt beide ein. Ein bereits eingetragener **späterer** Termin derselben Richtung wird durch den früheren ersetzt — der frühere Versuch gewinnt; ein bereits früherer Termin bleibt unangetastet, es wird nichts nach hinten geschoben. Der Termin überlebt einen Neustart, weil er zusammen mit seinem Grund in der Termindatei liegt; der Grund wird als Kurzzeichen geführt (`quota`, `respaced`, `overload`, `scan<Minuten>`), weil die Termindatei an Komma und Doppelpunkt trennt. Die Statuszeile nennt neben dem nächsten Versuch den Grund **dieses Termins**, nicht die Ursache des letzten Laufs; ein abgelaufener Termin ist kein offener Termin. Das gilt für **jeden** der sieben Arbeiter: die beiden Richtungen (Download, Upload) führen ihren Grund als Kurzzeichen, die drei Wartungs-Termine (Database, OSHash, Nachlauf) führen `lockbusy`, weil sie nur aus einem belegten Lauf-Lock warten; Auto-Sync und Seeder haben keinen eigenen Termin. Ein Termin ohne Kurzzeichen zeigt den Text des letzten Laufs.

**F-M476 [D]:** **Der HI-Pool wird abgelaufen, und er trägt die Hauptspur, wenn die reguläre Quelle erschöpft ist.** Der Pool der hörgeschädigten Fassungen wird nicht mit einem Griff gelesen, sondern in der Rangfolge aus F-M474 abgelaufen: je Kandidat die verbrannte Liste, der Abruf, die Tore und die Anpassung; der erste gespeicherte Kandidat beendet den Ablauf, eine Ablehnung nennt ihren Kandidaten und der Ablauf geht weiter. Vorzeitig endet er nur an der Tagesgrenze des Anbieters, die als Aufschub gilt. Endet der reguläre Ablauf einer Sprache ohne Speichern — Pack erschöpft, Pack verworfen, keine Bytes —, wird derselbe Pool als Quelle der **Hauptspur** abgelaufen; die Datei behält ihre SDH-Zeilen, trägt das Kennzeichen der hörgeschädigten Fassung im Namen und `hi = true` im Store, und ihr Speichern gilt als Speichern der Sprache, die damit nicht als nicht verfügbar gilt. Beide Wege sind eine Umsetzung. Den Pool gibt es nur bei eingeschaltetem HI-Schalter; ist er aus, wird er nicht gesucht und es gibt nichts abzulaufen.

**F-M379:** **Der Pack-Name trägt die SDH-Variante.** Ein SDH-Pack nennt sie im Namen, weil nur der Name in Archiv, Mitgliedsdatei und Eintragstitel sichtbar ist.

**F-M380:** **Die Breite entscheidet das Auflösungs-Token, nicht die Höhe.** Ein beschnittenes Bild bleibt 1080p, auch wenn die Höhe unter der Schwelle der nächsten Stufe liegt.

**F-M381:** **Ein Serienname ist noch kein Release-Name.** Die im Dateisystem unzulässigen Zeichen werden aus dem Seriennamen entfernt, damit keines in den Archivnamen wandert.
## 4. Download Pipeline

**F-M151a [B3]:** **Download-side short-circuit — replaced by the derived open-pair question.**

There is no download-side completion mark any more. Die Pipeline fragt die Untertitel-Deckung, welche geforderten PAARE have no evidence (14.2b) and returns when the answer is empty; that answer is derived fresh on every run, so it cannot be overtaken by a deletion and needs no stored language set and no validation pass. See F-M283 for why the stored mark had to go.

**F-M187:** **Downloaded bytes have exactly one decode path.** Every conversion of downloaded subtitle bytes to text goes through one path, which honours a UTF-16 byte-order mark (LE/BE, stripped) and falls back to UTF-8. The download side hashes the decode path, and the upload side later reads the written file as UTF-8/UTF-16 text, so the two must agree. An **uncorrected** file on disk stays **byte-identical** to SubDL's payload — normalization applies in memory for hashing and upload, never to the stored file. The one exception is a file the correction CHANGED: that goes out canonical (F-M296), because the bytes on disk must be the bytes the stored hash describes. The archived original is always the fetched bytes verbatim (F-M306).

**F-M251:** **One name parser, not two.** Both directions read names through the same parser; the download side holds no copy of the name logic.

**Scope is arithmetic:** the files this change may touch fall into six named groups (`NxNN`, bare `Exx`, `SxxExx`-at-end, date names, long form, bracket titles), and the groups must add up to the diff total. If they do not, an unintended change hides in it.

**F-M41 [D, parallel]:** **Download pipeline in the same plugin:** search for missing external subtitles per item (missing languages against a configurable target-language list), download via the same API client, registration in the registry, storage as an external stream next to the media file. Own scheduled task, own switch, own status block.

**F-M42 [D]:** **Preferred languages:** target-language list (multi-select, no priority). Default `AR, EN, ES, FR, HI, ZH`; empty = download off. `DE` is absent.

**F-M42b [D]:** **Hearing-impaired version additionally** (checkbox, default off): when on, the best hearing-impaired candidate per (item, language) is downloaded in addition to the regular version and stored as `<basename>.<lang>.sdh.srt`. It comes from the second search (F-M241), and the branch must sit before the best-per-language cut break, or with the default one per language it is unreachable.

**F-M333 [D]:** **"Only missing languages" has TWO real modes. OFF means: once per language, whatever the file already carries.**„Wenn on: embedded gilt als present, sidecar gilt als present. Wenn off: einmaliger download der Sprache egal ob sidecar oder embedded da."* **ON (unchanged):** sidecar and embedded track both count as present. **OFF:** only **our own recorded download** closes a pair — library sidecars, embedded tracks and the stream list stop counting, so every configured language is fetched once even when the file carries it. Reason: *"Ein embedded Titel verschwindet mit seinem Container"* — a language never fetched has no copy this plugin can re-derive. **"Once" is our own row, not the disk:** the reader RETURNS after the `downloaded` rows and never reads the file's own evidence, which would close the pair again. Intended: the second download lands beside the existing file (numbered cleanly) and a deleted subtitle heals, because the refresh forgets the row whose file is gone (F-M234). **No overwrite:** the slot is planned against the directory (F-M315). **One reader, three callers** — pipeline, seeder and refresh pass the mode in.

**F-M334 [D]:** **A DOWNLOAD dry run escapes the download brake, but not the search one.**„Naja, das klingt als müsste diese bremse vom dryrun test ausgenommen werden"*, and *"Also weiter wenn download dry run test ein"*. **The defect it fixes:** with the account at 50/50 downloads, F-M331 held back the very run whose whole purpose is to report what WOULD happen — the dry-run test could not be started at all (measured on the test server: `download held back this cycle — "download limit spent (50/50)"`, then `cycle end`, not one dry-run line). **The exception is HALF, and that is the point:** only the DOWNLOAD brake is skipped, because a dry run fetches no file. The SEARCH allowance keeps holding it in both modes — a dry run does search every item, spends search quota and draws every line it prints from those answers, so lifting that brake too would let it walk a library with the allowance spent and print nothing, which is the same wall F-M331 was written against. A real run reads both counters exactly as before.

**F-M335 [D]:** **The dry run consumes no work, and every worker row opens itself.** **(1) The download dry run marked its items `Done`:** the download outcome handler guarded on `_upInFlightDryRun` — the **upload** flag — so on a download run the guard never fired. Ein Trockenlauf schreibt **7 `Done`-Zeilen, genau die 7 Items, für die er eine DRY-RUN-line for**, and the next real run skipped them. Same F-M22 defect the upload branch was already fixed for; only one half was repaired, and an asymmetry reads as correct on either side alone. The dead half is the tell: `_downInFlightDryRun` was **written and never read**. Each direction now consults its **own** flag, asserted as write **and** read. **(2) The Auto-Sync row was only ever FINISHED, never STARTED:** every writer called `Finish` for the alignment while `Start` ran for the seeder, both directions and the three refresh tasks. `Finish` falls back to "now" only when `Started` is null, so a stale stamp survived — prod showed **started 08.10. 18:05, ended 09.10. 03:23**, nine hours for *"5 aligned, 5 already in sync (274.9s)"*. The row now opens from the download summary that holds the work, **before every outcome branch**.

**F-M340 [D]:** **The Auto-Sync row must be reachable while it runs.** Operator: *"Sollte der auto sync nicht auf run springen?"* F-M335 setzt den Startzeilen-Eintrag im selben Block wie, the same **synchronous block** as its five `Finish` calls — so the row opened and closed in one instant and the 10 s poll never saw it blue (prod: the stamp read 99 s LATE). It now opens at the **run’s edge**, before `await ExecutePipelineAsync`, and is closed by the summary path or, when a run ends without one, by the `finally` guard. **Not in the worker** (static, no `Plugin.Instance`, called PER CANDIDATE — the row would flicker) and **not around the sync measurement** (the candidate, not the run; never runs with the switch off).

**F-M336 [D]:** **A worker row may not claim work the run did not do.** The row's verb follows the run's own log words (**"would have saved"** / **"would have uploaded"**), so row and log can be compared word for word; the counters are untouched, because a dry run's counts are honest about what it FOUND and only the verb is wrong. The mode is read at the call site from the summary of the direction being written — each direction consults its **own** summary (the F-M335(1) asymmetry) — and passed in; a method that takes no configuration cannot derive it. The log line carries the same label (F-M247).

**F-M337 [D]:** **The General tab carries everything a run needs.**„Target languages (download, required) bitte unter library in general gui. Das ist nicht ganz sauber aber dann haben wir alles was wichtig ist auf der ersten Seite"*, and on the shape: *"Das download fehlt in der Überschrift und die Überschrift ist keine überschrift"*. **Target languages moves from the Download tab to the General tab, directly under the library selection** — libraries, languages and the schedule in that order: the first page answers the three questions a run depends on. The operator accepted the impurity himself (*"nicht ganz sauber"*: a download setting on a general page). **Moved, not copied:** a second control would render on both tabs, both would be bound at save and the last one read would win. **It is a real `<h3>` and it names the direction** — it was a field caption (a caption in a field container, the form this page uses for fields) reading *"Target languages"* alone, while its siblings name theirs. Only the control moves: the picker button, the modal and the four JS bindings stay and keep working.

**F-M260 [D]:** **The downloader reads the hearing-impaired flag of the file it actually fetched.** When a candidate resolves to one file inside a season or range pack (the pack member), that file's own `hi` flag governs naming and registration; for a plain single-file release the candidate's flag does, since the candidate is the file.

The HI block of the download loop is guarded by the *effective* flag, not the candidate's: when the file just saved already was the HI variant, no second download follows.

**F-M319 [D]:** **A language receives exactly ONE corrected file. The "best subtitles to keep per language" setting is REMOVED.**

**F-M242:** Zurückgezogen — die Regel steht in F-M319.

**The rule.** The candidate walk ends at the first candidate whose correction proves itself against the audio. An **unprocessed** file does not end it: it is a fallback, kept so the repeat costs no content (F-M317/F-M318).

**Why the setting went.** It existed to save X numbered files per language. Once the slot encodes WHICH KIND a file is (F-M317) and the walk repeats until a correction is proven (F-M318), the count had one meaningful value left — `1`, its own default — and every other value asked for the same language twice. The setting, its GUI field, the slot preview, the second parameter of the download budget 

**A removed setting must be removed at every layer** (config type, page, pipeline): a field left on the config type is a knob the next reader wires back up. Asserted in T131.

**F-M95:** **The search's early stop is the one correction-attempts setting, derived — never a literal.** The page walk stops once every requested language has at least N candidates, where N is that setting (F-M50); `0` disables the early stop. Until the threshold was hard-coded to 3, so raising the setting widened the download side but never the search: the walk still stopped after three candidates per language and the loop never saw a fourth. Until there were two settings feeding it; **one setting, one number** — the helper takes that value alone.

**F-M241 [D]:** **Two searches, one per side of the hearing-impaired split.** The regular slot is filled from a `&hi=0` search and the HI slot from a `&hi=1` search; the HI search runs only while the switch is on, so a user who does not want HI pays one search exactly as before.

SubDL filters HI server-side and the two pools do not overlap — a release appears in one or the other, never both. An unfiltered response is a MIXTURE, and the regular ranking would silently depend on how many HI releases it happened to contain.

A failed HI search keeps the regular candidates and logs it — the HI variant is a bonus (F-M42b), the regular subtitle is the target.

A dry run reports the HI pick from the HI pool; the real selection happens after the file download, which a dry run never reaches.

**F-M47 [D]:** **Configurable refetch interval:** manual / daily / weekly / monthly. **Default: Weekly.** The per-file last-search stamp (the last-search stamp and the stored language list) protects already-downloaded languages and controls re-search. The unified cycle interval governs both directions; the per-direction refetch property is carried for XML compatibility only and read by no code path.

**F-M215 [D]:** **A season or range pack is resolved to the episode it belongs to — never saved whole.** A pack must yield exactly one subtitle file per episode, chosen from the pack's own listing:

**The ZIP fallback matches the entry NAME** (`S01E06`, `s01.e06`, `1x06`, SubDL's `S0106`). A multi-file archive whose entry cannot be resolved returns **null**.

A pack with no entry for this episode is **skipped**, not saved wrong. Non-pack paths keep their existing behaviour.

**F-M383 [D]:** **A pack that lists at least two files is fetched as the whole archive; the episode is then taken from that archive's own listing.** Ein Mitgliedsdatei kostet **eine** Download-Einheit, and the whole archive costs the same **one** unit — ten episodes of a season therefore cost one unit instead of ten.

The archive body stays in the archive cache on disk (14 days, F-M370/F-M386), so the remaining episodes of the same season are served without another request; the run summary reports them as archive hits. The episode is still chosen by name from `unpack_files` (F-M215), never as "the first.srt": an archive that lacks this episode, and a pack that lists only one file, fall back to the single-file download.

**F-M388 [D]:** **A cue too short to be seen is dropped on the download side too — and the state hash is taken afterwards.** The rule existed only on the upload side (F-M382): a downloaded file carrying a 1-ms cue was convicted by the structure gate, so the very defect that had once blocked a season upload still threw away downloads. The fetched payload is now corrected before the gates run: cues of 100 ms or less fall away, the remainder is renumbered, and the file goes to disk in the canonical form. Because that happens first, gated text, written bytes and the stored hash are one artefact — the hash of a corrected file is never the hash of the payload it was cut from. A clean file keeps every cue; only the canonical byte style is applied (UTF-8 without BOM, LF, no trailing blank line), the form the content hash has always described. The retried sibling of an archive (F-M387) and the hearing-impaired file are corrected the same way.

**F-M387 [D]:** **A quality gate judges a FILE, not a candidate.** A pack can carry several files for the same episode, and a verdict on the first says nothing about its sibling: before the candidate is given up, the archive that is already on disk is asked for its next file — no request, no download unit — and the gates run again. Only when every matching file failed is the candidate counted as `rejected after fetch` and burned, and the reject line then says that the archive was exhausted rather than that one file failed.

**SDH is matched with SDH only.** A plain request never falls back to an SDH file and an SDH request never to a plain one: the filter compares the file's OWN `hearing_impaired` flag, never the pack header (which is often the opposite of its contents, F-M260). The two are different tracks, and swapping them silently changes what the viewer gets. 

**F-M384 [D]:** **The ranking prefers a season pack over an equally good single file, and a cached pack over one it would still have to fetch.** Two sort keys sit in front of the text score, which itself is unchanged: `2` for a pack that carries this episode and whose archive already sits in the run or disk cache (free), `1` for a pack that carries it but must be fetched, `0` for everything else — a pack without the episode never wins the preference.

Between two packs the text similarity still decides, and a matching release group still beats a foreign pack, because the preference is a rank and not a score bonus.

**F-M156:** **Missed refetch anchors:** an unhandled anchor is taken up by the next regular fire; the marker counts the claim, not the outcome. Missed slots do not stack.

**F-M58:** **Transient-overload 429 classification.** A `service_busy` 429 is server overload, not the daily allowance: the download side retries in place, up to 3 attempts, waiting the server's retry hint (default 5 s) and continuing the run. The upload side has no in-run retry — it ends the run and schedules the overload fire. A `rate_limit` 429 gets the same treatment (default 30 s); the status code cannot tell the two apart (F-M238). A login is classified rather than propagated: 404 and 403 are auth verdicts and fail closed at once, 429 retries, and a 5xx or a non-JSON body after 3 attempts ends the run as a transient overload and schedules the overload fire.

**F-M64:** **Target-language change needs no reset pass:** each file stores the language list it was last searched under plus the timestamp (the stored language list). the due check compares that stored list against the current configuration, so a changed list simply makes affected files due again — no global reset run and no second bookkeeping row. Items already holding all new languages still skip without API calls.

**F-M183:** **Legible download run reporting:** the download run counts **queued** items as the denominator of its abort line; the progress counter runs over every item of the selected libraries. At run end one compact aggregate line (saved, no candidates, language not available, skipped with reasons, failed, processed/queued), so a run that searched and saved nothing is distinguishable from a run that did nothing without raising the log mode.

**F-M52:** **Eine unvollständige Suche gilt nicht als erledigt.** Liefert die Suche kein Ergebnis (Rate-Limit, Serverfehler, verweigerter Zugriff), bricht der Lauf ab; das Item und alle folgenden bleiben fällig, statt mit leeren Ergebnissen als erledigt zu gelten.

**F-M89:** **Ein nicht beschreibbares Zielverzeichnis überspringt die Datei und bricht den Lauf nicht ab.** Der Grund steht je Datei im Log.

**F-M93d:** **Für die Übereinstimmung zählt nur die Kennung, die tatsächlich gesendet wurde.** Filtert SubDL serverseitig auf eine Kennung, ist dessen eigene Zweitkennung bedeutungslos und darf keinen passenden Kandidaten aussortieren.

**F-M382:** **Eine Untertitelzeile unter der Sichtbarkeitsschwelle wird verworfen, nicht als Beweis gegen die Datei verwendet.** Das gilt in beiden Richtungen und geschieht vor den Prüfungen und vor dem Schreiben, damit der Zustands-Hash die tatsächlich geschriebene Datei beschreibt.
### 4.1 Quality Gates — Download

The download chain runs against each candidate in score order, before the file is saved. F-M15 and F-M16 are the two gates both directions share.

**F-M44 [D]:** **Release match:** candidate release name scored against the local filename: release-group match > token overlap > download-count tie-breaker. Weights configurable (expert mode).

**F-M43 [D]:** **Runtime/FPS match with tolerance:**
**Stage 1 — pre-download (FPS):** SubDL provides `framerate`/`fps` per candidate. If set: difference > ±1 % vs. item FPS → candidate rejected. Missing → criterion skipped, no hard fail.
**Stage 2 — post-download (structure + runtime):** corruption check on the downloaded bytes (cue timings present, monotonically increasing, plausible durations), then runtime check of SRT cue span vs. item the runtime, default tolerance ±600 s. Outside → discard. Missing runtime metadata → criterion deactivated.

**F-M45 [D]:** **IMDB/TMDB match — HARD, with no switch.** Candidates are matched against item IDs, and an item with no resolvable id does not search SubDL at all: no id, no download.

**Es gibt keinen solchen Schalter.** It was `Require IMDb/TMDb match` (default on, F-M45 until) and its OFF position ran a `film_name` search on a title **parsed from the release filename**. That is the defect it looks like: a filename-parsed title is a guess, SubDL matches it against its own catalogue, and a wrong-year release with the same title passes the search and is fetched. The hard gate is the only position that stays correct on its own — so the option went, not the gate. The `film_name` branch in the search call is removed with it, and `_searchFnTitle`/`_searchFnYear` (the parsed-title state) went too.

**Two gates carry this, and both must stay.** The ladder's id-less branch returns before any search, and the item-level gate does the same for an item that arrives id-less *after* the ladder — the TMDb fetch needs a tmdb id to begin with, and a directed fire never runs the ladder.

**F-M50 [D]:** **ONE setting, TWO effects: how many candidates per language are searched, and how many the downloader may pull before it gives up** (page label: *Auto-Sync attempts and max. download/search limit*). Es gibt keine zweite Einstellung ("Max candidates per language") — the two knobs described one intent and drifted: the walk's cap was checked before the correction budget and counted the same attempts, so at equal defaults the cap always fired first and the budget could never trigger. With the Auto-Sync **on** the limit counts only **fits** the walk could not resolve, never gate rejections (F-M320). With the Auto-Sync **off** there is no fit and no verdict, so the walk pulls N candidates through the active quality gates and keeps them at 50, 51, 52 … `0` = no limit, in both modes. Fixed with it: the "correction hunt abandoned" break fired on the **first** candidate of every language whenever the Auto-Sync was off.

**The same language verification runs here verbatim** (F-M15), on the downloaded bytes, with its own per-direction switch.

**F-M295 [D]:** **Drift gate — cue vs. speech: does the subtitle hold ONE offset, or does that offset MOVE?**
Decodes the audio, derives speech islands from the frame envelope, anchors each cue to the last island start before it, and compares the model "one offset" against "two offsets split at a candidate cue" by their marginal likelihood (Bayes factor). Recursion left and right finds further boundaries. Two switches: **analyse** (report only) and **reject**; both default off.

**What it decides — and what it refuses to:** a direction plus a span. A drifting file has NO valid single offset, so a correction value printed here would be read as an instruction and would be wrong. The gate therefore never emits a shift.

**Failure posture:** no ffmpeg, unreadable audio, no speech, or too few cues → the gate reports "did not run" and the file passes, like every other gate.

**F-M331 [D]:** **A spent allowance acts like the download switch.**„Api limit oder download limit voll, downloader startet erst garnicht."*

**Rule.** A direction whose allowance is spent is switched off for that cycle exactly as der Download-Schalter off does it: the queue is **not filled** and the **run does not start**. No queue entry, no run, items stay due, and the direction still writes its own row. Both allowances are checked: the file-download counter (50/day) and the search allowance the search phase needs before any candidate exists.

**The verdict is the dispatcher's.** The seeder fills queues, reads a flag and owns no quota logic; the dispatcher probes **once per cycle, before the download seed**, and only when the cycle covers the download direction. The read is die Kontingent-Abfrage, which consumes no allowance.

**An unreadable counter is NOT a verdict.** A failed quota read returns null and the cycle proceeds as before — the usual fail-closed posture is deliberately not taken here.

**F-M46 [D]:** **Overall selection:** one best candidate per (item, language) by combined score from F-M43–F-M45 — the best CORRECTED one where a correction can be proven (F-M318/F-M319). No candidate passing → the language counts as "not available".

**F-M449 [D]:** **Maschinenübersetzte Untertitel sind standardmäßig aus.** Ein Untertitel des Übersetzungs-Kontos (SUBDL AI) wird nur geladen, wenn der Schalter `Allow AI translated subs (SUBDL AI)` gesetzt ist. Der Ausschluss greift am Suchergebnis: ein solcher Kandidat kommt weder in die Rangfolge noch kostet er eine Download-Einheit, und die SDH-Variante folgt derselben Regel. Erkannt wird das Konto am Uploader-Namen; Abstände, Punkte, Bindestriche und Unterstriche entfallen vor dem Vergleich, andere Namen bleiben unberührt. Die Laufsumme nennt die verworfene Zahl.

**F-M450 [D]:** **Ein beobachteter Defekt wird an SubDL gemeldet.** Liefert ein Paket keine Daten oder fällt eine Datei durch eine Inhaltsprüfung, wird der Befund im selben Lauf gemeldet; auch eine Antwort ohne Inhalt ist ein Befund. Ein gemeldeter Befund wird nie wiederholt, und ein gemeldeter Kandidat ist verbraucht — er wird nicht erneut geholt, denn jede weitere Anfrage kostet eine Kontingent-Einheit und kann am Befund nichts ändern. Gemeldet wird über das Meldeformular des Portals, mit dem nackten Befund (Paket-Kennung, Release-Name, Sprache, Messwert), der Zuordnung zu einer von vier Kategorien (kein Untertitel, falsche Release-Angaben, kopiert, sonstiges) — bei einem Paket mit dem Namen der betroffenen Datei, genommen aus der Auflösung des Archivs und nicht aus einer Vermutung, in beiden Spuren (Haupt und SDH), dazu einer Absenderzeile aus Produktname, Version und Kontoname — nie die Anmelde-Adresse. Ein Paket ohne Melde-Kennung wird nicht gemeldet. Nimmt das Portal eine Meldung nicht an, bleibt sie offen und wird im nächsten Lauf erneut versucht; eine angenommene Meldung wird nie wiederholt. Die Laufsumme nennt Meldungen und Vertagungen. Der Schalter `Report broken subtitles to SubDL` ist standardmäßig an.

**F-M451 [D]:** **Eine Absage des Abrufs ist kein Defekt des Uploads.** Angefordert wird das Archiv — die einzige Form, die für jeden Untertitel antwortet; die gesuchte Folge wird daraus entpackt. Ein Archiv trägt einen festen Zwischenspeicher-Schlüssel aus seiner Kennung, sodass ein Staffelpaket je Lauf einmal geholt wird. Antwortet das Archiv mit `200` und trägt nichts, ist das der Befund: es folgt keine zweite Anfrage in einer anderen Form. Die Einzeldatei wird nur verlangt, wenn das Archiv selbst abgelehnt wurde. Ablehnzeilen nennen den HTTP-Status und den Fehlernamen des Portals statt einer Vermutung; gemeldet wird ein Untertitel nur, wenn die Antwort tatsächlich mit `200` kam und nichts enthielt — Status und Inhaltslänge werden dafür festgehalten, und ein Archiv ohne passende Datei zählt nicht als leere Antwort. Eine Sperre des Tageskontingents beendet den Lauf, sie wird nie gemeldet.

**F-M452 [D]:** **Nur eine Fassung, die die Signifikanz bestanden hat, schließt ein Sprachpaar.** Ein Untertitel im reservierten Originalblock (51–99) ist der unkorrigierte Abruf, den eine abgelehnte Ausrichtung behalten hat — er liegt da, *weil* die Datei sich als falsch erwies. Er zählt deshalb nicht als Beleg: das Paar bleibt offen, der Lauf behält seine N Versuche, und ein späterer Kandidat hat weiter die Chance, als `01` zu landen. Dateinamen ohne Slot (vor F-M316 geschrieben) gehören keinem Block an und zählen wie bisher. Die Regel gilt nur bei eingeschalteter Ausrichtung: ist Auto-Sync aus, wird nie etwas ausgerichtet, jede Datei landet im Originalblock — dann schließt der erste Abruf das Paar, sonst schlösse nie eines. Damit das offene Paar nicht jede Runde dasselbe schlechte Release erneut zieht, merkt sich der Lauf das Release beim Behalten des Originals; der Merker überspringt es vor jedem Abruf und kostet kein Kontingent.

**F-M454 [D]:** **Ein Pack wird über die Folge aufgelöst, und die Folge darf auch als `Staffel.Folge` im Namen stehen.** Die Suche läuft mit Dateiliste, und die Datei, die *unsere* Folge nennt, wird gezogen — nie die erste im Archiv. Neben `S01E06`, `s01.e06`, `1x06` und SubDLs `S0106` gilt die bloße Form `6.10 …`; die API meldet für solche Mitglieder Staffel 0 und Folge 0, die Folge steht nur im Namen. Diese Form wird zuletzt geprüft und nur am Namensanfang gelesen, damit `DDP5.1` nicht als Staffel 5 Folge 1 gilt und eine Auflösung keine Folge wird; die im Namen genannte Staffel entscheidet, eine abweichende Staffel ergibt keine Folge. Nennt keine Datei die Folge, wird nichts geschrieben — lieber kein Untertitel als der einer fremden Folge.

**F-M455 [D]:** **Ein leerer Abruf ist kein Urteil — er wird einmal nach zehn Sekunden wiederholt, und erst der zweite leere Abruf ist ein Befund.** Deshalb fragt der Lauf denselben Kandidaten genau **einmal** erneut (zehn Sekunden, kein zweiter Versuch, kein Kontingent-Sonderweg), und nur wenn auch dieser Versuch leer bleibt, wird der Befund gemeldet — die einzige Stelle, an der die Zwei-Fehlschläge-Regel noch gilt. Ein Befund wird damit weiterhin im selben Lauf gemeldet. Ein Pack bleibt ausgenommen: mehr als ein Mitglied heißt Auswahl-Ergebnis, nicht defekter Upload; **ein** Mitglied ist die Datei selbst und damit meldbar. Die Statistik führt die angenommenen Meldungen als eigene Zeile `Downloads: Broken subtitles reported` direkt unter der Auto-Sync-Zeile.

**F-M456 [D]:** **Was kein Abruf war, ist keine leere Antwort.** Jeder Abrufversuch beginnt mit gelöschtem Urteil; bleibt ein Kandidat vor der ersten Anfrage liegen, steht im Log `no bytes (no request made)` statt eines Status, der von einem anderen Kandidaten stammt. Ein Kandidat, der vor der ersten Anfrage liegt, trägt keinen fremden Status. Eine leere Antwort ist nur dann eine, wenn sie mit **200** kam; Status 0 heißt: wir haben nicht gefragt.

**F-M457 [D]:** **Zierbuchstaben im Namen werden gelesen, bevor die Folge gelesen wird.** Veröffentlichungen nennen ihre Folgenmarke in stilisierten Zeichen (`𝐒𝟎𝐄𝟏𝟎`, vollbreite Formen); die mathematischen Alphanumerica und Vollbreiten werden auf ASCII zurückgefaltet, dann greifen die vier Namensformen. Die Faltung ist arithmetisch und braucht kein ICU — eine Unicode-Normalisierung wirft unter invarianter Globalisierung, die ein Jellyfin-Wirt durchaus fahren kann. Eine **einstellige** Null in der Staffel (`S0E10`) gilt als verlorene Ziffer, die geschriebene `S00` bleibt Staffel 0.

**F-M458 [D]:** **Eine Statistikzeile wird so benannt, dass die Zahl in ihrer Spalte bleibt.** Kein Zeilenname ist länger als `Downloads: rejected after being fetched` — die längste Zeile, die die Tabelle bereits trägt. Gekürzt wird am Namen, nie an der Zahl.

**F-M459 [D]:** **Die Zahlen der Statistik enden auf derselben Senkrechten wie die Statusspalten.** Rechtsbündig in der Wertespalte allein genügt nicht: solange die Tabelle nur so breit ist wie Beschriftung plus Zahl, liegt ihre rechte Kante dort, wo die breiteste Beschriftung endet. Die Tabelle spannt deshalb das Feld, die Beschriftungsspalte bleibt `max-content` und die Wertespalte wird an den rechten Rand geschoben — ein schmales Gerät scrollt waagerecht wie zuvor, statt die Zahl zu quetschen.

**F-M460 [D]:** **Der SDH-Pfad meldet, was er festgestellt hat.** Der SDH-Abruf prüft dieselben Dinge wie der Hauptpfad — Struktur, Laufzeit, Bytes — und meldet mit denselben Kategorien, derselben Wortwahl und demselben Schalter. Der Befund wird **an Ort und Stelle** eingestellt, nicht am Ende des Sprachlaufs. Nichts Geliefertes bleibt kein Befund (F-M456), und ein leerer SDH-Abruf wird ebenfalls einmal nach zehn Sekunden wiederholt (F-M455).

**F-M461 [D]:** **Jeder Abrufweg schreibt sein eigenes Urteil.** Der v1-Weg (`dl.subdl.com/subtitle/…`, Einzeldatei oder Archiv) und der v2-Weg halten beide Status, Fehlername und Länge der **eigenen** Antwort fest. Ein Ablehnungsgrund nennt damit den Grund dieser Anfrage, nie den einer anderen.

**F-M462 [D]:** **Ein gemessenes Urteil wird eingestellt, wo es gemessen wurde — nicht am Ende des Sprachlaufs.** Am Ende des Sprachlaufs wird nur aufgeräumt, wenn für die Sprache **nichts** gespeichert wurde; ein späterer Treffer lässt den Befund des vorigen Kandidaten unberührt. Der Befund geht an Ort und Stelle in die Warteschlange, und die Marke `ReportedUtc` verhindert, dass der Laufende-Durchgang ihn ein zweites Mal einstellt.

**F-M463 [D]:** **Der gewählte Dateiname geht mit in den Abruf.** Nennt eine Pack-Datei ihre Folge nicht im Namen (`16 - Relevance.en.srt`), findet die Auflösung über Staffel und Folge sie nicht. Der Abruf bekommt deshalb den bereits gewählten Dateinamen und nimmt genau diesen Eintrag; fehlt er im Archiv, folgt die Folgen-Suche auf **denselben** Bytes, und erst danach wird neu geladen. Ein Archiv, das schon auf der Platte liegt, wird nie ein zweites Mal bezahlt.

**F-M464 [D]:** **Ein Abruf trägt sein eigenes Urteil; eine Auflösung geschieht einmal.** Jeder Abruf gibt Status, Bytes, Fehlername und Nutzlast als ein Ergebnis zurück; kein Verbraucher liest Zustand, den ein späterer Abruf überschreiben kann. Ein einmal aufgelöster Eintrag (Name, Staffel, Folge) wird als Identität weitergereicht und nicht in einer tieferen Stufe erneut aus dem Dateinamen gelesen. Ein Archiv wird über den Weg geholt, aus dem seine Dateiliste stammt; der Cache-Schlüssel ist die Identität des Archivs, nicht die Route.

**F-M465 [D]:** **Der Upload-Nachlauf hängt am Upload.** Ein Upload-Lauf, der nichts hochgeladen hat, stellt keinen Nachlauf-Termin; ein Termin, der von anderer Stelle steht, bleibt unangetastet. Ein Nutzerstopp storniert weiterhin.

**F-M466 [D]:** **Staffel-Memo mit Fingerabdruck.** Je Serie, Staffel, Sprache und Variante wird der gebaute Pack mit einem Fingerabdruck über seine Mitglieder gemerkt. Gleicher Fingerabdruck bedeutet derselben Pack: nichts wird erneut abgeleitet oder gesendet. Ein anderer Fingerabdruck bedeutet einen neuen Pack. Eine Zeile ohne Fingerabdruck fällt auf den Mitgliedervergleich zurück.

**F-M467 [D]:** **Die Serie wird über ihre Id aufgelöst, nicht über die Bequemlichkeit des Objekts.** Trägt eine Folge ihre Serie nicht (die Pack-Phase liest eine Staffel als Ordnerkinder), wird sie über `SeriesId` geholt; ein Zugriff, der nicht antworten kann, kostet die Pack-Phase nicht. Jede Sprachprüfung je Datei vergleicht gegen die Ids der Serie, nie gegen die der Folge.

**F-M468 [D]:** **Spuren werden über die Id des Mediums gelesen.** Die Sprachprüfung je Datei fragt den Media-Source-Manager mit der Id des Eintrags; die eigene Spurliste des Objekts ist nur der Rückfall, wenn dieser nichts liefert. Eine Staffel, die als Ordnerkinder gelesen wird, trägt keine Mediensourcen — ihre eigene Liste ist leer und würde jede Sprache verneinen.

**F-M469 [D]:** **Ein Pack trägt eine Release — auch wenn die Staffel vollständig auf der Platte liegt.** Gepackt wird nur die Release-Gruppe, die alle ausgestrahlten Folgen stellt; stellt keine Gruppe die Staffel, entsteht kein Pack, die Einzeluploads bleiben. Dateien ohne erkennbare Release bilden eine eigene Gruppe und gewinnen nie gegen eine benannte.

**F-M470 [D]:** **Ein Umstand, eine Zeile.** Der gestellte Nachlauf-Auftrag wird genau einmal angezeigt — von seiner eigenen Zeile. Die Statuszeile der Workers führt ihn nicht mehr mit; sie nennt ihn nur noch, wenn sein letzter Lauf fehlgeschlagen ist, weil der Grund sonst nirgends stünde.

**F-M471 [D]:** **Das Archiv ist der eine Weg.** Ein Abruf holt das Archiv vollständig, legt es in die Ablage und entpackt daraus das gesuchte Mitglied — über die Eintrags-Id, den einzigen Adressweg. Es gibt keinen Einzeldatei-Abruf, keinen Mitglieder-Link, keine zweite Adresse und keine zweite Gestalt hinter dem Archiv: Ein abgelehntes Archiv ist der Befund, und ein Eintrag ohne Kennung wird übersprungen und benannt.

**F-M472 [D]:** **Ein Staffelpack bedient sofort die übrigen Folgen desselben Releases.** Kommt eine Folge aus einem Staffelpack, werden die restlichen Folgen dieses Releases, die im selben Lauf bereits in der Warteschlange stehen, unmittelbar danach eingereiht und im selben Lauf bedient — das Archiv liegt bereits in der Ablage, sie kosten also weder einen Abruf noch eine Wartezeit auf ihre eigene Runde. Maßgeblich ist allein der Eintrag, der die Folge bedient hat: seine Mitglieder sind das Release, ein Namensvergleich findet nicht statt. Nur die Warteschlange wird umsortiert — eine Folge, die niemand eingereiht hat, bleibt unberührt; eine Vollständigkeit der Staffel wird nicht verlangt.

**F-M473 [D]:** **Nach dem Lauf erfährt Jellyfin von den neuen Untertiteln.** Endet ein Lauf, in dem Untertitel gespeichert wurden, wird die Bibliothek mit dem Modus **nach neuen und aktualisierten Dateien suchen** eingereiht — dieselbe Funktion, die das Dashboard als „Bibliothek scannen" anbietet (`MetadataRefreshMode` und `ImageRefreshMode` = `Default`, kein Ersetzen, kein ForceSave). Die Meldung wird eingereiht, nicht abgewartet; der Lauf endet, der Server arbeitet danach. Ein vollständiger Datenbank-Scan wird nicht ausgelöst; ein Trockenlauf meldet nichts, weil er nichts schreibt. Der Schalter **Refresh Jellyfin library after download run** im Download-Reiter vor den Quality Gates schaltet die Meldung ab. Schalter aus: keine Meldung. Default: on.

**F-M474 [D]:** **Was nichts kostet, kommt zuerst — und ein Staffelpack vor der Einzelfassung.** Die Rangfolge der Kandidaten richtet sich nach dem Preis, den der Lauf zahlt, und nach der Reichweite: <c>4</c> Staffelpack mit dieser Folge, Archiv bereits in der Ablage (kostet nichts und bedient die ganze Staffel); <c>3</c> Einzelfassung, Archiv bereits in der Ablage (kostet nichts, bedient diese Folge); <c>2</c> Staffelpack mit dieser Folge, muss geladen werden (ein Abruf, ganze Staffel); <c>1</c> Einzelfassung, muss geladen werden (ein Abruf, eine Folge); <c>0</c> Pack ohne diese Folge. Der Abruf ist die knappe Einheit, deshalb wird nichts bezahlt, solange etwas Gratis vorliegt, und bei gleichem Preis deckt das Pack die Staffel ab. Die Rangfolge gilt für die erste Wahl, für das Nachfassen und für die HI-Variante gleichermaßen; der Textscore entscheidet erst innerhalb einer Preisstufe. Ob ein Archiv in der Ablage liegt, wird über die Eintrags-Id geprüft — über die Adresse, unter der auch geladen wird — nicht über die alte Einzel-URL.

### 4.2 Dry Run — Download

**F-M277:** **The download dry run runs the search and the selection, and stops before the fetch — the search quota IS spent.**

It runs: the id quality gate (F-M151b), the searches (F-M241), the release scoring and ranking (F-M44), the FPS tolerance filter (F-M43), the skip of candidates QA-rejected in earlier runs (F-M50), and the choice of the best candidate per language.

It does not run: the file fetch, the quality gates of 4.1 (language verify, minimum cue count, runtime match), the file write, the download mark and the registry write.

The report names, per language, the chosen release with its score and its hearing-impaired flag; while the hearing-impaired switch is on, the candidate from the hearing-impaired pool is also named (F-M241). It stops at the candidate, not the file: a dry run fetches nothing, so it cannot know the byte size and never builds the F-M260 name's hearing-impaired marker.

The searches DO cost API quota, one per language set (two while the HI switch is on, F-M241); the GUI text must name both "without saving files" and "without API calls". Statistics stay untouched (F-M247).

### 4.3 Auto-Sync — Download

**F-M296 [D]:** **A fetched subtitle is shifted to the audio: by the ONE constant offset
the detector measures while that offset is constant, and by a STAIRCASE — one offset per segment —
when it moves (F-M300).**

The feature has two halves that must not be confused: **which** track the audio is read from, and
**whether** the file is then moved. Both were measured against this library before either was built.

**F-M297:** **Als Referenz für die Korrektur wird eine Tonspur derselben Sprache bevorzugt.** Das unveränderte Original bleibt in beiden Fällen erhalten.
#### 4.3.1 Method — the audio track is chosen by LANGUAGE, not by stream order

**Rule.** Priority: **(1)** a track whose language equals the subtitle's language, **(2)** an English
track, **(3)** the first track without a language tag, else the first track at all.

**Why a rule was needed at all.** Both audio-reading gates decoded `0:a:0` — the first audio stream —
with a comment defending the hard index, and the reasoning behind that comment is sound about
*what to do when a stream is missing* while being wrong about *which stream to read*. **304 files of this library that carry sidecar subtitles**, the rule above picks a different track than
`0:a:0` in **34 of 387 (file, subtitle-language) cases (~9 %)**; 353 cases are unchanged. The recurring
shape is an **Italian release whose first track is the Italian dub** with the English original on
track 1 — a German subtitle then wants English (prio 2) and an English one wants English too (prio 1),
while the hard index reads the dub. **76 files carry no language tag on their first track at all.**
Priority distribution: 158 / 122 / 107.

**The 2-vs-3-letter trap, and why it is written down.** A subtitle file name carries `en`/`de`
(2 letters); a container tag carries `eng`/`deu` (3). Comparing the two sets directly **never matches**.
A first attempt at counting this reported *"subtitle language in NO audio track: 252 of 304"* and
*"wrong first track: 0"* — both pure artefacts of the mismatch, one of which reads as a finding. The
implementation therefore carries an **explicit ISO map**, and the rule that **a zero or a perfect value
is root-caused rather than reported** applies.

**What it does not claim.** On the one multi-track file measured closely (Italian + English audio)
both tracks returned the **same verdict** — span 17.4 s vs. 18.1 s, median 8.4 s vs. 7.8 s, deviation
0.6–0.7 s — because the envelope reads sound, not language. The rule's value is that it removes the
assumption *"the first track is the right one"*, which is measurably false on ~9 % of these files. **Das ist keine Einstellung**, weil es keine Wahl ist: choosing by language is the right answer, die Seite bietet dafür keinen Schalter (F-M303, F-M304). The configuration entry die Prüfung der Tonspur je Sprache remains in the file with its default `true`; forced off, the old hard `0:a:0` is read again.

#### 4.3.2 Method — a constant offset is corrected, a moving one is corrected as a staircase

**What is measured.** The same arithmetic that the drift gate (F-M295) runs also yields the single
best offset. The two questions were validated together: planted constant shifts of
**−3.0 / +2.0 / +4.0 / +8.0 / +12.0 s** came back as **−3.20 / +1.80 / +3.80 / +7.80 / +11.80 s** — an
error of about **0.2 s** in every case (the residual is the ~0.3 s per-cue SDH lead moving the median).
At every one of those values the detector reported `drifts=False, boundaries=0`: **a large constant
shift is not mistaken for drift**. That separation is what makes the feature possible at all.

**The sign is MEASURED, and getting it wrong doubles the error.** The correction is **minus** the
detector's value (`srt_time − measured = synced`). Established on a real episode with a planted +5.0 s:
the detector read **+4.50 s**, applying **−4.50 s** landed the file **0.50 s** from its plain subtitle,
while applying **+4.50 s** landed it at **+9.50 s**. The first version of the pipeline added the value
and every corrected file came out **exactly twice as far off as it went in**. A unit test cannot catch
this — the shift function is correct in isolation; only the sign of what is handed to it was wrong.
The end-to-end test (T111) therefore applies **both** directions and prints both outcomes, so a future
sign flip fails loudly instead of silently doubling.

**The floor is measured, not chosen — 0.20 s was too low.** The plain subtitle of a real episode, the
file nothing is wrong with, measures **−0.50 s** against its own audio: that is this material's
measurement floor (a cue leads the speech onset by a per-cue varying amount), and it is the same order
as a small real offset. With a floor of 0.20 s the feature therefore **moved a file that was already in
sync** — caught by the end-to-end test, not by reasoning. Die Mindestverschiebung war **1,0 s**, at which a finding
is at least twice the floor. **Schwelle: 0,7 s.** He weighed both ends ("1 Sekunde ist zuviel, 0.2 Sekunden zuwenig"): 0.2 s is the value that already moved a correct file, 1.0 s leaves a real half-cue finding untouched, and 0.7 s keeps a 0.2 s margin over the ~0.5 s floor while reaching into the band the operator sees on screen. The number is fixed in code, not a setting.

**The refusal names its cause (order).** The old text was composed from the values the revert had already zeroed, so a perfectly in-sync file and a file whose fit had measured 887 moved cues at t = 1.71 on a better score printed the identical line — `no proven gain (0 cue(s) moved, t = +0.00)`. The operator read a measured +1.40 s drift in the gate next to "no gain" and asked how both can be true. The deploy rule now records which of its four conditions refused (keine Bewegung, nicht signifikant, schlechterer Wert, unter der Schwelle) and the measurement itself survives the revert, so the line carries the numbers the decision was made on: `no proven gain (not significant: t = +1.71 < 2.0, score 0.5591 -> 0.5667 (+0.0076), 5 segment(s) [+2.00/+1.25/+0.50/-0.50/+2.50], 887/887 cues, largest 2.50 s) — left as downloaded`. **Significance settles the class, not the score (operator).** "Signifikanz über 2 ist Pflicht für verbesserte subs" — no correction is deployed without `t > DeployZ`, and there is no such thing as a MATERIAL score improvement without significance: `+0.0131` at `t = +1.54`, `+0.0076` at `t = +1.71`, `+0.0036` at `t = +0.57` are all under 2 and prove nothing, so they are "not sufficiently evidenced" and hunt the next candidate. A correction that is worse but NOT significantly worse (`t = -1.59`, score `0.5032 -> 0.4914`) is equally unevidenced — it hunts too. The score keeps exactly one vote, where it decides: a correction at `t > 2` that would LOWER the score is refused as "worse". Nowhere else is the score consulted; it is reported so the operator can read it.

**Two classes, and the difference decides whether another candidate is fetched**: a correction that improves the score but cannot prove it is `gain not statistically proven (..) — original kept, next candidate` and is CANDIDATE-SPECIFIC — the reserved `9x` original stays and the pipeline pulls the next candidate (F-M318). `no proven gain` stays for the three shapes where the file really is the best there is — nothing had to move, the shift is below the floor, or the untreated file is SIGNIFICANTLY better (`t <= -2`) — and only those END the walk. Die Ablehnungsprüfung matches the `no proven gain` prefix exactly, which is what keeps the two apart.

**The refusal list, and what replaced one of its entries.** A drifting file has **no valid single
offset** (see F-M295): every number is an average over the drift and describes no real state, so
shifting by *it* moves one part right and spoils another. That is correct about the average. The
conclusion once drawn from it — "so nothing can be done" — was wrong, and F-M300 corrects it. The
remaining refusals stand:

- gate did not run (no ffmpeg, unreadable audio, no speech, too few cues) ⇒ **nothing is written**. An
 unavailable analysis tool is not a licence to guess.
- `Drifts = true` **with no segment offsets reported** ⇒ nothing is written; the segment list is what
 the staircase needs, and without it there is no correction to apply.
- a **single staircase step** above 20 s ⇒ nothing is written; beyond that the figure is implausible.
- `|offset| < 1.0 s` on a **constant** offset ⇒ nothing is written; that is within this material's
 measurement floor.
- `|offset| > 20 s` on a constant offset ⇒ nothing is written.
- a shift that would push a cue **below zero** ⇒ the fitted shift is **applied as measured** and the
 cue is **clamped to 0**. **Supersedes the former whole-file refusal**. **Rule.** The fit decides
 the offset; the clamp only stops a cue running off the front of the file. A refusal may not replace the
 fitted value — it discarded the whole correction over one leading cue and reported the file as clean
 (`no proven gain`) while it was **9.00 s** out of sync. The clamp changes one cue's relation to its
 neighbour; that is the smaller price, and the fitted shift stands.

**Applied to times only.** Text is carried through byte-for-byte, the cue count and cue order are
unchanged, and the corrected file is written **canonical** (UTF-8, no BOM, LF, trailing whitespace
trimmed) — the same form F-M185 establishes and the content hash describes. The fetched original is
untouched by this: it keeps its own byte style inside the archive (F-M306), because that artefact is
the uncorrected file. **`Ich wollte wenn ich Dateien ändere sie dann auch
gleich normalisiert schreiben.` The former rule wrote the corrected file in the fetched payload's own
byte style, which made the stored hash describe a byte sequence that existed nowhere on disk; the two
agreed only because the normaliser happened to strip exactly what the style put back. Normalizing on the
way out makes written bytes and hashed bytes identical **by construction**, so the correction and the
hash cannot disagree.

#### 4.3.3 STAIRCASE — a moving offset is repaired segment by segment (F-M300)

**Rule.** When the detector reports `Drifts = true`, each cue is shifted by the offset of **its own
segment**: the boundaries the detector already found divide the cue list, and segment *k* covers the
cues from its start time up to the next boundary. The applied shift is **minus** that offset — the same
sign the constant path proved by measurement (§4.3.2).

**Why the segments ARE the repair, and why the structure is not overfitting.** A staircase model has
one parameter per segment, so it fits *anything* better than a single offset; that is not evidence. The
evidence is a permutation test on the same files: the same staircase fit applied to a **shuffled**
ordering gives **4.13 s** residual against **0.30 s** on the real ordering — **14× better**, which a
model fitting noise cannot achieve. The structure itself was measured:

- **4–5 steps per episode** (median 4), **all positive** in the 22 episodes that were examined for it.
- Step height **median +2.84 s**, range **+1.07 to +3.77 s**.
- Step spacing **median 8.5 min** — S02E01 at **10.5 / 18.1 / 27.1 / 35.5 min**, i.e. act breaks.
- The steps **sum to the total drift** (**11.98 s** against a measured **10.92 s**), so they explain it
 completely rather than approximately.
- Each step lands **between two adjacent dialogue lines** — a cut, not a smooth ramp.

The last point is what rules out a framerate error: a framerate error makes every interval equally
steep, whereas a stepwise-constant curve with jumps is a re-cut. **The grid is not fixed**: boundaries
on 5-minute multiples matched only 48 % against 40 % chance (not significant, spacing scatter 2.55 min),
so the steps are **measured, never computed**.

**The measurement, on the material it was built for.** Over the **36 drifting episodes** of this
library, the worst single-cue residual against the same-language plain subtitle went from a **10.74 s
median to 4.51 s**, with **33 of 36 improved**.

**Where the REMAINING error comes from: the boundary, not the step height.** The applied offsets come
out close to right; what is uncertain is **where the step starts**, because a boundary carries the
±1–2 min positional uncertainty stated in §4.3.1. The cues inside that window receive the far side's
offset, and that difference is what the residual measures. The same uncertainty is why an episode can
come out worse: a boundary drawn through a region that was already correct moves that region off it.
The result is therefore read **per segment**, never as a file-wide total, because a total hides exactly
that.

**The limit, stated because it is measured.** **Two of the 36 come out worse** — S01E04
(**8.00 → 21.30 s**) and S01E06 (**2.47 → 11.91 s**). **No reference-free signal separated them from the
33 successes.** Six candidates were tried, all computed from the run itself: monotone distortion
(4.55 s good vs. 3.42 s bad), split-half disagreement (22.80 vs. 7.80), remaining drift after the
correction (22.10 vs. 10.90), run-back against the main direction (26.8 vs. 11.7), outlier offset
(14.35 vs. 11.75), raw drift span (0.90 vs. 13.10). **Every one of them overlaps.** With 2 failures in
36, any threshold computed from that same distribution is a circle — so no gate is built on them, and
the staircase is applied with those two accepted as the price of a ~6 s average gain.

**The staircase is the ONE correction for a moving offset.** There is no second route: the audio is the
only reference, and the segments the detector found are the repair.

**Order guard.** At a step the two neighbours move by different amounts. Where that would push a cue
back across its neighbour's end — the case a player renders as stacked text — the previous cue's shift
is carried forward. The guard is measured against the **previous cue's END**, and because the staircase
**adds** its shift the bound is a **lower** one (`eff[k] ≥ eff[k−1] − gap + MinGapSec`). A gap already
below the minimum gap must not be shrunk further: measured on
a real episode, **560 of 724 gaps are below 0.04 s** with a median of **0.002 s**, so demanding the floor
everywhere makes the guard fire on nearly every cue and carry one shift through the whole file.
**Getting this direction wrong is not cosmetic**: the synthetic test measured a **1.92 s** residual and
**22** guarded cues with the sign reversed, against **one misplaced step** and **6** guarded cues with
it right.

**F-M305 [D]:** **A real-material score is taken against the AUDIO, never against a
subtitle.** The synthetic episode T114 runs on puts the boundaries where the test put them, so it cannot
fail on boundary placement — the decider on real audio. A real-material score is therefore required, and
its yardstick must be independent of every subtitle: **the audio envelope itself**, the same input the
correction reads. The detector runs on it and the result is read as the **per-segment offset curve** — in
sync yields offsets near zero and a small span, drifting yields several segments far apart. **A subtitle
must not be the yardstick, because that makes the measurement circular:** a reference that itself drifts
makes the result look better than it is; it may be used to **find** a defect, never as the number a
requirement is accepted on. **The control run comes first:** the untouched material is measured before
anything is corrected, and the **order-guard count is read with the result** (a guard firing on hundreds
of cues flattens the staircase into one constant shift while the step heights still look right).

**F-M300 [D]:** **A subtitle whose offset MOVES is repaired segment by segment — one
offset per segment, the boundaries the drift gate already found — instead of being left as downloaded.**

This supersedes the "a drifting file is only reported" clause of F-M296. The average describes no real
state, so it must not be applied; the **segments** are not an average, they are the steps themselves.
Method, measurements and the two measured failures: §4.3.3.

**F-M302:** **A user-facing text must not contradict the code, and length plus a ban list cannot carry that.** A short sentence stating the opposite of the code satisfies a length cap AND a ban list, so T113 also matches the **refuted claims** — `cannot correct` / `can not correct`, `has no valid single correction`, `is only reported`, `never shifted`, `cannot touch` — across **every user-visible text node**: the section intros, the per-switch description blocks **and the switch labels**, since a description-only scope misses a label. The list stays narrow on purpose: it names the refuted phrasings, not every mention of drift, because a check that fires on correct prose gets disabled.
**F-M303:** **The correction sections are ONE section, and it carries ONE switch.** One block, one switch, die automatische Synchronprüfung, governing the ONE correction — and the block carries **no intermediate heading**: the switch is the entry point, not a heading above it. What the switch does sits in its **own** description block: the audio path of §4.3, constant as one shift and moving as a staircase. The audio track is chosen by language (§4.3.1) — not a choice the operator makes, so it carries no switch. **The behaviour stays.** The removed entries keep their defaults in the configuration file (die Prüfung der Tonspur je Sprache `true`; die Drift-Prüfung und ihr Ablehnungsschalter `false`), so an existing installation behaves as the page shows.
**F-M304 [D]:** **A switch switches the whole of what it names, and the page offers no switch for something that is simply right.**

**Rule — the switch governs every route that moves the file.** One visible switch, die automatische Synchronprüfung, governs the constant shift (§4.3.2) **and** the staircase (§4.3.3) — every path that moves a file. A switch that does not switch what it names is worse than no switch.

**Rule — the absence of a removed control is asserted, not assumed.** A check that asserts only PRESENCE stays green over a stale checkbox left in the markup. The failure mode that matters is the other one: ein zurückgebliebener Elementzugriff auf ein Element, das es nicht gibt returns `null`, the next property read throws, and the throw aborts the whole load handler — every binding on the page dies although the surviving switch's own markup is present and correct. The check therefore asserts the absent ids (Drift-Prüfung, Drift-Ablehnung, die Prüfung der Tonspur je Sprache, die Anker-Synchronprüfung) are absent from the markup **and** from the inline script, and counts the section's checkboxes and fails unless there is exactly **one**.

#### 4.3.4 Order of operations, and what the database is told

**Order (user specification):**

1. fetch the candidate bytes,
2. **sync** — measure and, if the offset is constant, shift,
3. **normalize** (idempotent, F-M185) — the correction output is written canonical, so written bytes
 and hashed bytes are identical by construction,
4. **write two files**: the corrected `<base>.<lang>.srt` **and** the untouched original as the
 one-entry archive `<base>.<lang>.srt.unsynced.zip` (F-M306),
5. **register the hash of the CORRECTED subtitle.**

The hash describes the file that lies on disk and that later goes up to SubDL, so the duplicate guards
(Inhaltsprüfung, Sidecar-Upload-Prüfung, Ablehnungsgrund) keep seeing the truth. **A correction
must never lift a duplicate guard** — had the hash been registered over the pre-shift bytes, every
corrected file would have looked new and been re-downloaded and re-uploaded.

**Why the name sits AFTER `.srt`.** The sidecar listing is
`Directory.EnumerateFiles(dir, baseName + "*.srt")` (F-M251). Gegen dieses Muster: ein Name
ending in `.srt.unsynced.zip` is **ignored** (correct), while the swapped
`<base>.<lang>.unsynced.srt` **matches** — and the name parser then reads `unsynced` as a
language code, inventing a language. Jellyfin does not index such a name as an external subtitle track
either, so exactly one new track appears per corrected file. The archive keeps the `.unsynced`
part in its name for the same reason the name sits where it does: it is the artefact, and it stays
recognisable.

**The original is kept on purpose, as an archive.** It makes the correction reversible without
spending download quota again — and re-downloading may return the same drifting file. It is kept as the
one-entry zip of F-M306 and nothing else; unpacking it is the operator's step. A failure to write it does
not undo the corrected file; it is logged as a warning.

**F-M306 [D]:** **The untouched original is kept as a selectable sidecar in the reserved slot block, and is locked against upload.**

**Rule.** After a correction the pipeline writes the original beside the corrected file as a **loose sidecar** `<base>.<lang>.51.srt`, **numbered from 51 upward** to the first free number up to **99**. It carries the corrected file's language token and `.sdh` marker, and **no marker of its own**. The reserved block is **51–99**; the writer of *corrected* files stops at slot **49** (F-M315). With every reserved slot taken, **no original is written** and the pipeline refuses.

**The lock.** The original is registered like every other downloaded subtitle — eine Zeile als heruntergeladen gebucht, geschlüsselt auf den Inhalts-Hash of the bytes on disk. Die Inhaltsprüfung zählt jede Zeile **außer `observed`** as known content and the uploader does too, so the seeder drops the file and the uploader returns `duplicate-content`. **Canonical** bytes (F-M296); both tracks share one helper under die automatische Synchronprüfung (F-M304).

**F-M315 [D]:** **The slots 51–99 are reserved for kept originals; corrected files stop at 49.**

**Rule.** The naming rule carries the reservation as a pure property of the number: die Reservierungsprüfung ist wahr für 51–99 and for nothing else. Der Zielname — the name a *corrected* sidecar is moved to, and the name the downloader composes — never returns a reserved slot: it walks 1…49 and, when every one is taken, returns slot 1 so the caller's own existence check refuses the move. Die Zielnamensuche für Originale läuft **50…99** and returns **null** when all fifty are taken. Both blocks count upward on purpose — corrected from 1, originals from 50 — so a viewer reads the reserved number in the track list and tells the two kinds apart without opening anything (F-M316).

**Why the cap is not cosmetic.** Without it, the next corrected file of a language whose slots 1–49 are taken would be handed a name an original already occupies — the correction would overwrite the very file it exists to keep. The corrected writer therefore stops at 49 and returns slot 1 when the block is full, never a reserved number (F-M316).

**F-M316 [D]:** **The slot is always written, always two digits; it is the track order.**

**Rule.** `SidecarNaming.Build` composes every sidecar name as `<base>.<lang>[.sdh][.forced].<NN>.srt` with `NN` = the slot as `D2` (`01`…`99`). Slot 1 trägt den nackten Namen nicht. The number stays the LAST name token, so the name parser reads it exactly as before — they accept one and two digits, so names written before F-M316 keep parsing and no migration shim exists. The track list therefore runs languages alphabetically and, within a language, `01`…`49` (corrected) before `50`…`99` (originals) — the order F-M315 reserves them for.

**Cost.** Names written before this rule keep their old form until they are renamed; the pipeline only writes new names, and nothing renames on disk by itself.

**F-M317 [D]:** **The slot encodes WHICH KIND the file is: aligned takes 01 upward, unprocessed 51 upward.**

**Rule.** A downloaded subtitle goes into one of two ranges: was it ALIGNED?

- **Aligned** (the fit applied a correction, or the fit is off and the file is the corrected artifact) → `<base>.<lang>.<NN>.srt`, `NN` = `01`, `02`, `03` … **upward**, its own counter.
- **Unprocessed** (the fit found the file **WRONG** — shift beyond the limit, first cue negative, too few cues — or the fit is off) → the **reserved block**, `50`, `51`, `52` … **upward**.
- **`no proven gain` is NEITHER** (F-M321): `01` **and** a byte-identical copy at `51`.

**"Regardless of whether a corrected version exists"**: an unprocessed file takes a reserved slot **even with no corrected sibling beside it** (F-M321).

die Zähler korrigierter Plätze steigen on the aligned path only; the unprocessed path writes once. Every reserved slot taken → the write is **refused** and counted. F-M315's reservation (51–99, corrected stops at 49) stands; GUI text must match the code (F-M302).

**F-M318 [D]:** **The download REPEATS until a candidate's correction proves itself, capped at the QA retry limit.**

**The rule.** A fetched subtitle whose correction the fit REFUSES does not end the walk. The pipeline keeps fetching lower-ranked candidates — up to **die Zahl der Download-QA-Wiederholungen (Default 3)** candidates refused by the fit (default 3, the same knob the QA-reject give-up already uses; `0` = no cap) — and stops at the first candidate whose correction proves itself against the audio.

Only a verdict on the FILE justifies another download; a verdict on the AUDIO or the tooling ends the hunt (T131). Each refused candidate is still kept, landing in the reserved block as an unprocessed file (`50`, `51`, `52` … F-M317).

The repeat sits AFTER every other download gate (FPS, no-bytes, language verification, structure, min-cues, runtime). The walk's candidate limit still caps the total fetches, counting only fits the worker could not resolve, never gate rejections (F-M50); each budget has its own log line.

**F-M320 [D]:** **The QA retry limit bounds the FIT. No other gate gives up, and nothing closes a pair.**

**The rule.** die Zahl der Download-QA-Wiederholungen (Default 3) (default 3) is the fit's budget — how many candidates may be fetched hoping for a provable correction (F-M318). Es zählt nichts anderes. A gate rejection (language, structure, min-cues, runtime, FPS, no-bytes) is **not** a budget: the candidate is discarded, counted among the rejected candidates, memorized by release id (F-M200, so it is never fetched twice), and the walk moves on.

Zähler fehlgeschlagener Läufe (Fehlerzähler und Erschöpfungsprüfung) und die zwei Statuszähler gibt es nicht (Übersprungen-Zähler und Aufgebens-Zähler); nothing closes a (item, language) pair. Der QA-Zähler ist der Datensatz verbrannter Kandidaten and nothing else. What bounds the work is the download budget per run (F-M50) plus the burned-release memory (F-M200).

**F-M321 [D]:** **A file the fit finds nothing wrong with is a GOOD file: it is written at 01 upward, its original at 51 upward, and the walk ends.**

**The rule.** `no proven gain` is not a failure — the deploy rule DECLINES to move the file, so the file as downloaded is the best version of that language. It is written as a **good file at `01` upward** (`<base>.<lang>.01.srt`, then `02` … F-M316), advances the **corrected counter** (the reserved block stays free for wrong files), and its **original is written too**, into the reserved block (`51` upward) — "beide original": BOTH carry the original subtitle, identical by construction because the reserved write REUSES the array sent to `01`. The walk **ENDS** there: no candidate can beat a file the fit found nothing wrong with (F-M318).

Only the deploy rule's OWN refusal counts: a refusal that says the file is **wrong** keeps the reserved range and the F-M318 hunt alive. `SubtitleSync.RefusalMeansAlreadyGood` tells the two apart on the exact `no proven gain` prefix. Both tracks carry the rule (F-M317).

**F-M322 [D]:** **The auto-sync has its own status row, the same words as every worker; and the statistics count only the alignments that really happened.**

The alignment's status is its own readout, in ONE place: a **row of its own** in the Workers list — `Auto-Sync`, kept as `WorkerRunRegistry.AutoSyncWorkerKey` — separate from Download, which cannot say whether anything was aligned. A second readout (a light under the download switch) is asserted ABSENT (T134). **The words and colours are the existing ones** (F-M268): `ok` green when it aligned something or was already in sync, `skipped` grey when switched off or nothing measured, `failed` red when the run failed and aligned nothing; a DRY RUN reports `skipped`.

**The statistics count successful auto-syncs only.** The row is `Downloads: Auto-Sync`, reading the fitted-to-audio figure, advanced ONLY where a correction was really applied; an already-good file (F-M321) does NOT reach it and keeps the already-good mark. The setting's label reads *"Automatically synchronize subtitle to spoken track"*; the postprocessing row is `Upl. Postproc.`.

**F-M323 [D]:** **The auto-sync is a WORKER of its own — it is handed the subtitle and hands back a status and its output.**

ein Einstiegspunkt für beide Spuren (F-M307 kept). It returns the corrected text and a status; the **download run keeps writing the files and counting**. **Input:** subtitle as **decoded TEXT**; audio as a **file reference plus a stream map** (Dateipfad plus Tonspur-Karte, z. B. `0:a:2`). **Output:** `Outcome(Status, Applied, Corrected, Reason, ElapsedMs, AlreadyGood, CandidateSpecific)`; `Corrected` is text — the worker writes NO file, files no slot, keeps NO counter.

**Six per-file statuses** (vs F-M268's per-RUN vocabulary): `ok` (moved), `already-good` (declined — F-M321), `disabled` (switch off), `refused-audio` (nothing about the file — F-M318), `refused-file` (about this file), `failed` (could not measure).

**Measurement (F-M310):** the stopwatch wraps the fit and reports its duration. **The row goes red for its OWN breakdown only** (`DownloadRunSummary.AutoSyncFailed`), never a refusal.

 See F-M307, F-M310, F-M318, F-M321, F-M322.

**F-M324 [D]:** **The configuration page carries its JavaScript ONCE. There is no second page script, no second script route, and no page-script file.**

**The rule.** The page carries its JavaScript exactly once, inline in der Konfigurationsseite. There is no second copy: no second page script, no second page-info entry, the resource registration and the API route `Plugins/SubdlSync/ConfigJs` are all removed. Five assertions of ABSENCE hold the state — no file beside the page, no controller, no registration, no resource, and the renderer present exactly once.

**What stands unchanged:** the renderer's FIELD must still be `s.FittedToAudio`, and the statistics endpoints must still declare the JSON data type (F-M216).

**F-M328 [D]:** **Every worker row reports only ITS OWN result; no row borrows a neighbour's sentence.**

**The rule.** A worker's light and its detail text are both derived from that worker's own evidence, and from nothing else:

- The cycle's **green** fallback states the DIRECTION's own detail, never the seeder's string.
- This holds at the level the rows are WRITTEN: the check reads the live endpoint and requires that no two rows carry the same detail, not merely that each field is non-empty.
- A direction that never reached the pipeline reports that.
- The direction's own detail is set **before** the cleanup may record a quota stop; a quota stop outranks a green one-line result.
- A **skipped leg does not overwrite a scan that ran this cycle.**

**A gated scan names only the directions it covered** — a hard `0` is never printed for a direction that was not asked. **The seeder row SUMS the cycle's legs**, so the download leg's numbers do not vanish.

 The download tab's switch-block order and the row-level rule are asserted against source and the live endpoint.

**F-M329 [D]:** **The download tab reads in the order the cycle works: "Continue after daily limit", then "Follow-up rounds for downloads", then "Automatically synchronize" — all three above the quality gates.**

**The reason is the operator's own:**„automatically synchronized goes in download gui up right below follow-up rounds and both go below continue after daily limit."* The three controls render correctly in any sequence, so the rule is asserted as an **ORDER**, never as presence — a presence check passes on exactly the layout this rule exists to correct.

 See F-M328.

 See F-M216, F-M218, F-M323.

**F-M307 [D]:** **The offset is a piecewise-constant function of time, fitted by exact dynamic programming. Supersedes the recursive Bayes-factor gate (F-M295) and the staircase applied from gate boundaries (F-M300).**

**Speech** (§4.3.1): mono 16 kHz, band-pass **300–3400 Hz**, **20 ms** RMS frames; levels clipped at the file's **10th** and **90th percentiles** → der Perzentilwert liegt in **0–1**; dilate **0.30 s** (`SLACK`).

**Score:** a cue at shift `d` scores the mean `p(t)` over its shifted window; off-audio → **0**, excluded (T121a). **161** shifts (**−20…+20 s**, step **0.25 s**) per cue.

**Fit:** maximise `sum(score) − Z·sigma·sqrt(cues)` exactly by DP over **`BLOCK_CUES = 40`** blocks; `Z = 1.0`; `MIN_SEG_FRAC = 0.12` of the file's cues, floor **40 cues**; a shift-**0** segment competes.

**Deploy (all must hold):** moved cues gain over `DeployZ = 2.0` standard errors (paired, moved cues only); file not worse; largest shift ≥ der Mindestverschiebung (§4.3.2).

**Scope:** language-neutral, times only, text byte-for-byte; below-zero clamped to **0**; no audio or speech ⇒ "did not run".
### 4.4 Season Packs — Download

Dieses Unterkapitel hält fest, was die Download-Seite mit Packs tut und warum das die Upload-Seite bindet. Es führt **keine neuen Anforderungen** ein; die normativen Sätze stehen unter den zitierten IDs.

**Ein Pack ist die günstigere Quelle.** Ein Pack-Kandidat wird einem gleich guten Einzel-Kandidaten vorgezogen: ein Archiv-Body bedient **alle** Folgen des Packs und kostet eine Quota-Einheit; ein bereits im Platten-Cache liegender Pack steht ganz vorn (F-M384). Beim Entpacken gilt das `hearing_impaired`-Flag **der einzelnen Datei**, nicht das des Pack-Headers (F-M384), und welche Datei die gesuchte Folge ist, entscheidet der Mitgliedsname (F-M454), und jede entpackte Datei wird als `downloaded` gebucht — genau die Herkunft, die sie später als Pack-Material disqualifiziert.

**Was ein Dry Run auf der Download-Seite nie erreicht.** Ein Trockenlauf holt keinen Archiv-Body, entpackt also nichts und schreibt keine Datei — die Zahlen, die er nennt, sind hypothetisch, und er hinterlässt keine `downloaded`-Zeile (F-M334).

**Die eine Richtung, die daraus folgt.** Jede entpackte Datei wird als `downloaded` gebucht, und genau diese Herkunft disqualifiziert sie später als Pack-Material beim Upload: stammt nur eine `.srt` eines Packs aus SubDL, fällt der ganze Pack aus (F-M397).

## 5. Upload Postprocessing

**F-M287 [B1]:** **Everything a run writes that is reachable from a dry run sits behind the dry-run flag — the guard belongs at the WRITE, not at the mode's exit.**

In beiden Richtungen: der Refetch-Stempel, the (now removed) file-retry counter, the (now removed) id-resolution budget, the media-row creation, the track observation and the file-missing deletion all executed in a dry run, and the dispatcher marked every reported item `Done` and deleted it from the cycle queue — one dry run consumed the work it described. The guard rule: **a write is dry-run-guarded at its own site**, so moving an exit cannot silently expose it. Sanctioned exceptions: a write that makes state stale-free rather than claiming a fact (recording a success, clearing a stamp), and the worker-run record, which reports that the run happened.

**F-M184:** **Duplicate handling delegated to postprocessing:** the upload path does not search SubDL for duplicates before uploading. A duplicate upload is accepted by the API ("sent for review"), resolves to `rejected` on the SubDL dashboard, and the postprocessing job deletes that entry and marks the local row `remote-duplicate`. This removes one search call per item from the daily search quota; the cost is one upload per duplicate. A local self-echo guard (registry, per media/language pair) still prevents uploading the same pair twice.

**F-M227:** **Every plugin task appears under one heading in the dashboard.** Only `SubDL Postprocessing` reported the category `Sub Scribe`; the other four reported the default category, so the dashboard split the plugin's work across two groups. Rule: every task this plugin schedules reports `Category => "Sub Scribe"`; a new task must not inherit a Jellyfin category.

**F-M223:** **Every name the user sees says Sub Scribe — the ASSEMBLY name is the one thing that must NOT follow.** Jellyfin's logger derives its category from the type, so the namespace says Sub Scribe. Die Namen der eingebetteten Ressourcen wandern mit dem Namespace (Seite und ihr Skript sowie das Literal, das sie liest), und alle drei müssen gemeinsam wechseln, sonst lädt die Konfigurationsseite nicht. **the assembly name stays the assembly name** — Jellyfin derives the plugin data folder and the configuration file from it, so renaming would orphan the credentials, the state database and the run history. The assembly name is a persistence key, not branding. Free text follows (postprocessing the category, console tags, reset dialog, GPL headers); API routes stay the plugin's own routes.

**F-M210:** **Every scheduled job is driven by its OWN setting.** Database refresh, OSHash refresh and upload postprocessing each carry their own cadence setting and their own diced anchor, and must fire on them regardless of any other job's setting. **One exception, and it is not a return to the old coupling:** postprocessing additionally requires the upload direction to be ON (F-M291) — not another *job's* setting but the direction it works for, since with upload off it has no subject at all. the cycle interval governs **only** the automatic pipeline cycles; `Manual` and `OnArrival` there suppress those cycles and nothing else. A job must never sit behind an early return belonging to a different job's configuration.

**F-M210a:** **Every scheduled job carries a manual start button under its own description, and its disabling option is labelled `Manual` — the bare word.** Alle drei Aufgaben tragen einen Knopf und bieten deshalb `Manual` statt `Never`; the rule is mutual — **`Manual` is offered exactly where a button exists**. The option text is **exactly `Manual`**, nothing appended: the button sits right under the description it belongs to, so naming it again in the menu was noise. A button touches NO cadence field — the interval stays the only thing that schedules the job — and it reaches the work through the job's own TASK, which writes the durable status row (F-M295). The stored VALUE stayed `Never` until, when the operator resolved the two spellings into one — *"Never / manual für alle 3 jobs zu manual auflösen"* — so the menu now stores the value it shows, and the page offers `Never` nowhere at all. That rename is safe only because of three things, each asserted by T87: the legacy member survives in every enum (a dropped name makes an old configuration file fail to deserialise, or fall back to a cadence nobody chose), the load path reads a stored `Never` as `Manual` in memory (`Plugin.DiceSchedulerConfig`), and the disabling member is named explicitly where it is consumed — the OSHash switch falls back to Monthly, so an unnamed disabling value would start rehashing a library whose owner had switched it off. Nothing is written back: these are USER fields and the plugin persists only its own (F-M201), so a file keeps `Never` until its owner saves the page — and the two spellings mean the same thing, which is what makes leaving the file alone safe.

**F-M212:** **No fire may be consumed before its task is registered.** Jellyfin's task queue drops the fire (logging `Unable to find scheduled task of type "X"`) when the target task is not yet registered. Every fire path tests the registration **before** it consumes its slot (no marker set, no reschedule counter reset), so the next 30-s tick retries. This applies to **all six** paths: database refresh, OSHash refresh, postprocessing, the refetch cycle, the F-M156 catch-up and the F-M65 recovery fires.

**F-M131:** **Manual stop via marker files:** the Stop button creates `.stop-upload` and/or `.stop-download` in the plugin data directory. Each pipeline checks its marker before processing the next item (and, for uploads, between streams of the same item), cancels the direction, deletes the marker and reports the stop marker. The dispatcher ends the cycle without starting further directions. Stop markers do **not** affect the delayed postprocessing task. The canonical endpoint for programmatic stops is der Stop-Endpunkt; das Abbrechen des laufenden Tasks}` cancels the Jellyfin task wrapper, but a running pipeline item may finish first.

**F-M17y:** **Delayed upload postprocessing:** after an upload run (normal finish or stop), on the postprocessing schedule (F-M176; not tied to the run and not to a fixed delay — SubDL review latency varies), the plugin queries `/user/mySubtitles` and resolves every locally pending-review entry whose status is `rejected`: an entry with "Duplicate upload" is deleted on SubDL and marked duplicate-remote; any other rejected entry is deleted without a mark. Accepted entries are not touched. All steps logged at Debug. The task runs on its own cadence and diced anchor (F-M210), independent of any run.

**F-M291:** **The upload binding applies to the SCHEDULED run only — a manual run always works.**„Nur die automatischen runs vom Postprocessing an den upload binden, manuell darf immer."* With the upload direction off the scheduled run has no subject (an entry can only be pending review if something was uploaded), so **the anchor is not armed**. Enforced at that ONE point: the two downstream gates are removed, because both refused a MANUAL run — the dashboard button reaches the work through the postprocessing task, and the job's route is der Nachlauf-Endpunkt. A fire queued before the toggle went off still runs (idempotent work, accepted). The page names the BOUND half and only that: *"Scheduled run is inhibited if upload is disabled."* Der Zeiger auf den Knopf entfällt; der Knopf sitzt direkt unter der Zeile, . The page therefore documents the rule that needs documenting (the schedule is held back) and leaves the manual route to the control the operator can see.

**F-M332 [U]:** **The page's postprocessing button starts the TASK, not the job's endpoint.** Both routes run the same work, but only the postprocessing task writes the durable status row ("Upl. Postproc."); the endpoint writes none. Der Endpunkt lief korrekt (zwei abgelehnte Uploads aufgeräumt) while the list stayed empty — the operator saw no entry and reported the run as not having happened. The button therefore uses the task starter like the two refresh jobs. The name in the page is matched against the class's `Name` at runtime, so the check ties both literals together: a rename on either side fails only in the browser, as "Task not found", with every build green.

**F-M176:** **Postprocessing reschedule spacing:** if postprocessing cannot start because the global run lock is busy (F-M94h: immediate `false`, no waiting), it schedules a one-shot re-fire in the job spacing (5–120 min). Ein lokales Rate-Limit vertagt ihn nicht (F-M20). The re-fire still respects the diced anchor and does not move the next regular run. A busy lock is first checked for staleness; only a living previous run causes a deferral. **Cadence default: `Weekly`**, changed from `Daily` on: rejected-upload rows arrive as a slow trickle and every run costs a read of the own-subtitles list, so a daily sweep mostly re-read the same pages to find nothing, while a weekly one still resolves a rejection within days of it happening. The diced anchor is a day+time either way, so a stored value needs no reinterpretation — and an existing installation keeps the cadence it chose.

**F-M94h:** **ONE global run lock for all six state-mutating components** (seeder, downloader, uploader, postprocessing, database refresh, OSHash refresh) — they all touch the same state, so one mutual exclusion is what the design needs. **Overlap is never waited out:** a caller that cannot acquire is refused immediately (a 20-s in-process hand-off grace) and reschedules itself by the job spacing.

**Stale detection:** dead holder PID → take over immediately; holder alive but stamp older than the 6-h run ceiling → wedged run, take over; file older than 24 h → take over. the startup cleanup deletes a leftover block file at plugin start. Fail-open: a malformed block file never wedges the plugin.

**F-M205:** **Every reschedule is counted; after 16 the rescheduling stops.** One counter per slot (upload, download, postprocess, database refresh, OSHash refresh, refetch); once a slot has been rescheduled 16 times it is refused. The database refresh carries two slot keys, one for its anchor and one for its reschedule fire. **The limit is 16** — a single constant; every log line renders `{Limit}`, so raising it touches no message text.

Giving up is logged as a **warning** exactly once; further refusals are logged at debug.

The counter is cleared by the reschedule reset on a real fire and by the per-day budget reset (F-M208). It lives in RAM only — a restart resets it, which is the intended fail-open.

**Not counted:** daily-limit quota-reset fires (a quota-reset fire) — they are driven by the reset, and dropping them would skip the direction outright.

## 6. Database Refresh

**F-M94:** **Database refresh as scheduler task:** a dedicated task reconciles the stored state with reality — it removes tracker state for Jellyfin items that are gone and verifies the file side of stored verdicts (F-M234). The task, its dashboard name and its log prefix read "database refresh" (`[SubDL-Refresh]`). It uses the global run lock and operates on the shared database.

**F-M274:** **The refresh runs its steps in one fixed order, in a single run, under the global run lock, and reports what it changed.**

**0 — probe:** resolve the full item id set; an unqueryable or empty library skips the run. **1 — dead rows:** rows whose Jellyfin item is gone are removed and counted. **1b — gone files (F-M60 removal,):** media rows whose FILE is gone while the item survives are removed with their subtitles, by PATH. **2 — guid-keyed trackers:** the search and QA-fail trackers lose gone-item rows. **3 — OSHash cache paths:** cached paths under a non-listable root are dropped. **4 — vanished subtitle files:** the sidecar verdicts of F-M234 and the open required FILES of F-M283 (reported, never repaired). **4b — embedded rows:** F-M258. **5 — compaction:** the fold and rebuild of F-M214.

Steps 1b, 3, 4 share one fail-safe: every stored media root must exist and list cleanly, else the step is skipped, never judged ("unknown ≠ deleted"). The run's line reports removed rows per area and forgotten state; a no-change run reports the database matches reality; a lock-less run is `deferred`, never failed.

**F-M234:** **The state prune is a database refresh: it verifies the FILE side of a stored verdict, not only whether the item still exists.**

A subtitle file deleted while its item stays leaves a verdict ("uploaded"/"rejected"/"downloaded") and a download mark both permanently wrong, the item reported complete forever. A mark is stale only when the language has lost its evidence EVERYWHERE the configuration counts it: no sidecar file AND no embedded track, or a track settled as unavailable. The refresh reads "settled as unavailable" from the whole stored set, through the same reader the download pipeline uses.

Fail-safe: every stored-path root must exist and list cleanly, else the file side is skipped; rows without a stored path are never judged ("unknown ≠ deleted": a missed refresh costs nothing, a false one destroys valid verdicts). A missing probe is not a deletion: when the embedded half cannot be read, the check falls back to the files alone.

Subset rule: a stored language set that COVERS the configured one counts as complete, so removing a language does not invalidate every mark; adding one still does.

**F-M258 [D]:** **The database refresh checks the embedded side too, not only the sidecar side.**

For every media row whose Jellyfin item still exists and which has stored embedded rows, the refresh reads the item's current streams and forgets every stored row that disagrees — on the key (position) and on the recorded facts (language, hearing-impaired). A row whose position is gone, or whose language/HI no longer matches the stream at that position, is dropped.

The check is structural for observations only: a row carrying a verdict (`uploaded`/`rejected`) or a detection attempt is kept unconditionally; only a plain observation is dropped on a disagreement. The comparison is against positions, not the tracks whose language resolved: a position that exists but answered nothing keeps its row; only a position that exists and answers differently is a disagreement.

Fail-safe, as the rest of the refresh ("unknown ≠ deleted"): an unresolvable item, an unreadable stream list and an EMPTY stream list are skipped, not judged; only a NON-empty list that disagrees is evidence. The check runs only for files that HAVE stored rows.

**F-M214:** **A database refresh compacts the database in the same run.** The prune is followed, in the same run and while it holds the global run lock, by the compaction: fold the journal in (the journal fold), then rebuild the file (the rebuild) to release the free pages.

**Compaction is measured as the whole footprint** — data file plus journal, before and after.

**It must never fail the task:** an exception is logged as a warning and swallowed. It is housekeeping, and the refresh's own work has already succeeded.

**F-M237:** **A rebuild that cannot release the pages is replaced by a rebuild from the file's own rows.**

Rule: when the library's own the rebuild fails, rows are read out, written into a fresh file, counted against the original, and only a matching count is swapped in. Rows are never traded for a smaller file.

The swap is a rename, not a copy: a running Jellyfin holds the file open, and a copy over it is refused with "being used by another process".

The previous file survives as exactly one timestamped backup, reachable through the database restore function. Nothing is deleted until the row count is proven equal.

The rebuild's side files are addressed by their STEM (`subdl-scribe-log.db`, `subdl-scribe-temp.db`), never by appending to the full name, which matches nothing on disk.

**F-M236:** **A failed compaction must not leave the shared database dead.**

The compaction is verified, not trusted: after a failed rebuild the database is probed with a real query (the compatibility row), and only a failing probe replaces the engine.

The repair preserves the data: the failed swap leaves the original file on disk, so rows survive.

**F-M342 [D]:** **A database refresh refreshes the query statistics in the same pass as the compaction.** After the rebuild the run executes `ANALYZE`, which fills `sqlite_stat1` — the row count and the rows-per-key average of every index — because that is what the query planner reads to pick an index; without it the plan is a guess. Auf diesem Schema: `embeds WHERE MediaHash = ? AND Status = ?` searched one index before the statistics existed and another after, and MediaHash is the selective one (13 rows per key against 29).

The cost is one database page.

The statistics run as their OWN guarded step. A failure is logged as a warning and does **not** turn a shrunken file into a failed compaction — the file WAS rebuilt at that point, and the failure wording belongs to the rebuild alone.

The rewrite is folded in before the footprint is measured (the journal fold), so the reported "after" describes the settled file instead of a journal holding the rebuilt database. See F-M214.

**F-M404 [P]:** **Der Database Refresh räumt auch die Pack-Seite auf: eine `packs`-Zeile, deren Pack-Kennung auf keiner Untertitelzeile mehr steht, wird entfernt und in der Refresh-Summe mitgezählt.** Eine solche Zeile hätte keinen Träger mehr — die Medien wurden neu gehasht, die Dateien weggeräumt oder der Pack ersetzt —, behauptete aber weiter „diese Staffel ist gepackt" und legte sie damit für immer still.3.

## 7. OSHash Refresh

**F-M119:** **OSHash refresh as scheduler task:** a dedicated task recomputes expired or fingerprint-changed OSHash cache entries on **its own diced WEEKLY anchor** ("D HH:mm", drawn once at install and never re-rolled). It fires **every week**. It holds the global run lock while mutating the shared cache. The OSHash cadence bounds how long a cached fingerprint is trusted (`Never` = fingerprint mismatch only, zero media reads in the steady state), while the fire date comes from the anchor alone. Setting it to `Manual` (stored value `Never`) does **not** disable the job.

**F-M275:** **The refresh works on a snapshot of the cache and decides per entry from the file's fingerprint.**

For every entry the file is stat-ed for **size** and **modification time**. A file that is missing, or that cannot be stat-ed, leaves its entry untouched and is counted as **missing** — removing entries is the state prune's job (F-M94), not this task's.

A fingerprint mismatch (size or modification time differs) recomputes the media hash. An unchanged fingerprint recomputes only when the entry's trust window has expired; with the cadence set to `Manual` (stored `Never`) the window never expires, so only a mismatch triggers a recomputation.

A recomputed hash is stored together with size and modification time. A file that cannot be hashed keeps its old entry and is counted as **skipped**.

The cache is flushed at the end of the run. The run's line reports the entry count and the changed, missing and skipped counts. The run ends `ok`, `cancelled` or `failed`; a failed run keeps the entries already flushed and leaves the remainder untouched. A run that cannot take the global run lock is recorded as `deferred` and re-fires after the job spacing (default 15 minutes, clamped to 5–120).

**F-M279 [D]:** **The OSHash VALUE is verified against an independent oracle, not merely against itself. Switchable: not applicable — this is a test obligation, not runtime behaviour.**

**Die Referenz:** ein eigenständiges Prüfskript implementiert the published OpenSubtitles algorithm (size + first/last 64 KiB summed as little-endian 64-bit words, 16 lowercase hex, unsigned 64-bit wraparound), NOT from the media hash; `scripts/seed-db-test` section J asserts the shipped code against its literals. Der Vertrag ist: Blockgröße = min(64 KiB, Dateigröße).

**Pinned:** the values at 8 B, 64 KiB, 128 KiB and 200 KB, the window edges at 64 KiB ±1 and 128 KiB ±1, that a change in the tail or the last window changes the value, and a missing path yields `null` (empty file too), never a throw.

**Negative-controlled:** reverting the algorithm in a COPY of the source must turn the assertion red. The cache on an unreadable host is audited via ein eigenständiges Forensik-Skript; its verdicts separate `MATCH`, `MISSING FILE` (F-M119), `SIZE DRIFT` (F-M61b) and `MISMATCH` (wrong value).

## 8. Rules Shared by Both Directions

**F-M5:** **One ffmpeg call per file, however many subtitle streams it carries.** All text subtitle streams are extracted in a SINGLE invocation into a temp folder: repeated `-map 0:s:N -c:s srt -f srt <out>` output pairs on one input.
**The index rule:** stream positions are SUBTITLE-relative (`0:s:N`), never container indices. Mixing the two numbering schemes lands on video or audio and fails with exit 8 or a no-stream error.
**No optional mapping — `0:s:N` without `?`.** On an index that does not exist a trailing `?` makes ffmpeg write the FIRST subtitle stream into that slot, exit 0 and report nothing. An unknown index must fail the call; the caller reads that as a whole-pass failure and retries the empty streams one at a time.
**The fallback rule:** ffmpeg's exit code decides what an empty result means. Exit 0 → the stream carries no text; that is a verdict and must NOT be retried. Non-zero → the pass failed as a whole, and each empty stream is retried once with the per-stream call.

**F-M261 [D]:** **An untagged or `und` subtitle track is resolved, and the found language is written back into the container. Switchable, default off — it governs both the resolution and the write.**

A text track whose tag is absent, empty, `und` or `undefined` is not a fact about its language: the gate extracts it, detects offline, records an observation and — when enabled — writes the tag into the container. It belongs to the SEEDER, before the queue decision.

**Sequence:** untagged text tracks from the stream list → one ffmpeg pass for all (F-M5) → offline detect with the 2 KB floor (F-M74) → write the codes → move the registry state to the new hash → record the tracks.

**Registry move:** the rewrite changes the file's IDENTITY, so die Identitätsverschiebung schreibt den State um. **Position rule:** position is `0:s:N` over TEXT-eligible subtitle streams only (not external, not forced, not bitmap); bitmap and forced tracks occupy and keep a position, external streams are skipped. **The write is a stream copy (`-c copy`)**, read back and verified, and counts as PRESENT for the same run's coverage check.

**F-M66 [D]:** **Id resolution has NO give-up — an item without a resolvable id is retried by the next run.**„Id resolution retrys bitte auch löschen."* Zähler und Grenze kommen in Konfiguration, Seite, beiden Pipelines und im Refresh nicht vor; `0`-means-never-give-up was the only surviving meaning, so the rule is now unconditional. An id-less item is skipped for the run and re-enters the ladder next time, which costs one TMDb attempt per run at worst.

**F-M59:** **LIFO queue order:** both pipelines process items by the creation stamp descending — newest first. The id-order partition keeps id-resolvable items before the id-less backlog.

**F-M60:** **Entfernt — es gibt keine Datei-Fehler-Wiederholung vor dem endgültigen Überspringen.** Der Zähler je Item, the setting the retry limit), its page field and both pipelines' give-up branches are removed, and the tracker type is deleted. **The gone-file case has one owner now and it is the Database Refresh (phase 1b, F-M274):** it sweeps media rows by PATH behind the same root fail-safe as its neighbours, so it also reaches rows whose item is fine. A second, slower owner that only fired after three failed runs was what the order removes.

## 9. SubDL/TMDb API, IDs and Credentials

**F-M11 [B1]:** Upload queue with retry: a failed item is retried across runs up to the configured maximum, without backoff; at the limit it is purged. Timeouts are retried in-transport three times with a doubling 1 s backoff.

**F-M12 [B1]:** Configurable account (SubDL user/pass) in plugin config, password never in plaintext in logs

**F-M17c2:** **Remote duplicate detection requires a canonical payload AND a matching hash function.** The registry hash is comparable with SubDL's stored raw-file MD5 only when both describe the same bytes and use the same function (F-M185, F-M186).

**F-M19 [B1]:** **All four credentials are required — they are not alternatives.** The SubDL **email/password** pair authenticates the UPLOAD (its three steps carry a Bearer token from `/login`); the SubDL **API key** authenticates search, file download and the quota read; the **TMDb key** resolves and corrects the ids in both directions. A missing one is not a degraded mode: the run is refused up front, naming the field (F-M203).

**F-M208:** **The reschedule budget is per UTC day and resets at the day roll-over** — the same 00:00 UTC boundary the SubDL quota reset uses, so the budget returns when the quota does. the daily reset clears every slot at the roll-over. It is a no-op when the day has not changed.

A slot at the limit has no other path back: the refusal drops its pending fire, so it can never fire, so it can never clear its counter. With `RefetchInterval = Manual`/`OnArrival` no anchor exists that could.

Both refusal messages name the day roll-over as the recovery path, and do not promise a regular anchor — that anchor does not exist in those modes.

A manual run and a successful run do not clear the counter. Only a real fire or the day roll-over does.

**F-M219:** **The type-neutral TMDb search is the FIRST rung wherever an id is resolved, and the year is checked on its RESULTS.**

The year is not sent to `search/multi`; the typed endpoints honour it, and the typed search is the second rung.

The year is applied when the hits are read: a hit whose own date (`first_air_date`, `release_date`) equals the year wins over the first hit. With no year-equal hit the first film/series hit is used; a Jellyfin year is often the import year (F-M217).

Both id-less paths start type-neutral: the download pipeline's title rung and the id resolution. A typed search inherits Jellyfin's guess, so a name-detected series typed as a film asks `search/movie` and can never match.

The seeder parses the file name before it asks TMDb (the same parser, F-M217), and returns the type TMDb reports.

**F-M231:** **Jellyfin's ids are ALWAYS tested against TMDb by TITLE and YEAR; the year comes from the FILE NAME when it states one.**

Year source: the file name's year first, Jellyfin's only as fallback (Jellyfin's film year, else Jellyfin's series year) — Jellyfin's year comes from the metadata that may be pinned to the wrong title.

Correction only on an EXACT year match (the strict year match, the strict twin of the multi-search). No exact hit ⇒ Jellyfin's ids stand, so a missing year or a differently spelled title costs nothing (F-M190).

**F-M232:** **A login that fails on a server error is CLASSIFIED, never propagated raw.**

An unclassified failure propagates as a raw parse exception: the cycle logs a generic failure, the recoverable overload is never recognised, and the F-M67 overload fire is never scheduled.

Every login attempt is classified: 404 a not-found verdict and 403 are auth verdicts (fail closed, an auth verdict); 429 keeps its retry/backoff; a 5xx or a non-JSON body after the retries exhaust is a **transient overload** — sets a transient overload, ends the run cleanly, schedules the F-M67 fire.

A JSON body behind a 5xx is still transient, never a credentials verdict: the status code decides.

The client's the log channel (login retries, raw 429 bodies, rate headers) is subscribed per run by both pipelines, so the retry line is reachable.

**F-M235:** **A transport timeout is retried three times; a user stop never is.**

A timeout is transient, like the 5xx login answer of F-M232. An expired request threw a timeout, which the API clients do not catch, so it reached the run loop's `catch (OperationCanceledException)` and was reported as "cancelled — keeping the partial result": a slow server looked like the stop button.

The two cancellations are told apart by the caller's token. Already cancelled → the request is not repeated. Otherwise the cancel came from the handler's own per-attempt timeout and the request is repeated.

Budget: 3 attempts per request (as in F-M232), 1 s base backoff, doubling. Transient means a timeout, a cancellation or a transport error; a malformed answer is an answer and is never retried.

Per-request timeout, not one global value: 60 s per attempt for API calls, overridable via the per-request timeout; a subtitle FILE download asks 5 minutes. The client-wide timeout is disabled.

The request body is buffered once and the request rebuilt per attempt.

The give-up line names the server as not answering in time, never as a caller cancel, and the retry lines are routed into the run's log channel.

**F-M221:** **The plugin card states the defaults and the two required keys in the same sentence as what the plugin does.** Card text (all carriers, F-M220): *"Sub Scribe brings SubDL.com to Jellyfin: it downloads missing subtitles for the languages and libraries you pick and uploads your own. Download is on by default; upload is off — enable at your choice. Requires a SubDL login and API Key plus a TMDb API Key."* Order: what it does, both things the user picks (languages AND libraries — F-M42 pre-fills a list, it is not a commitment), download on by default, upload off by default and enabled by choice (F-M41), then the keys. Do not write non-ASCII characters as `\uXXXX` escapes in the plugin description literal: the F-M220 guard compares source text, and an escape is six characters where the YAML carries one.

**F-M24b [B1]:** **Credentials never in logs:** password/API key/token are never written at any log level (redaction before writing, also in Debug)

**F-M25 [NOT IMPLEMENTED]:** A per-run start offset (random wait before the first upload) is not implemented — no code path delays a run, no config property backs it. The anti-herd function is carried by the per-installation diced fire times (F-M51), the per-stop deferral offset (the job spacing, F-M26b) and the daily-limit recovery jitter (F-M182, 30–300 min). Manually triggered runs start immediately.

**F-M26a:** **Which pause applies where (single source of truth).** the bare-call pause — bare API calls, deterministic. the transfer pause — real transfers, ±30 %.

**F-M27 [B2]:** **Auto-backoff:** on HTTP 429/rate-limit or 5xx a file transfer is retried up to 3 attempts with exponential backoff (2 s, then 4 s). The server's retry hint with a 5 s floor applies on the login and transient paths. On exhaustion the request fails cleanly and the run-level reaction matrix (F-M54) applies.

**F-M28 [B1]:** **IMDB ID mandatory — correct per media type.** For series strictly the series IMDB (tvshow, not episode) plus season and episode; for movies the movie IMDB. Resolution order: (1) Jellyfin metadata as-is; (2) TMDB id → IMDB via the TMDB REST API (key required, F-M203); (3) TMDB title search; (4) skip "no-imdb". Episode IMDB is never used.

**F-M190 [B1]:** **ID resolution is type-free and TMDB-authoritative, and it is the standard path for every upload.**

**Type detection comes from the FILE NAME and from TMDB, never from the Jellyfin library type.** The parser reads the name (`S01E05`, `Season 1 Episode 5`, TV stamps); when it reports a series while Jellyfin reports none, the item IS resolved as a series; a later `search/multi` answer overrides the type — **TMDB decides** film or series.

**ID arbitration (JF vs TMDB) with a report:** both IDs present → cross-validate; on disagreement a **Normal-level** ID-conflict line is emitted and **TMDB wins**.

**Fallback chain:** TMDB returns no IMDb → the Jellyfin ids are kept; Jellyfin carries only a TMDB id → the IMDb id is fetched from TMDB; **no IMDb resolvable at all → NO upload** (fail-closed). **Upload payload:** IMDb + TMDB when both are known, IMDb alone when TMDB is missing, neither → no upload. **Series IMDb needs `/external_ids`:** the detail endpoint reports `imdb_id: null` for series, while `<kind>/<id>/external_ids` returns the real `tt…`; the fallback uses it when the detail response leaves the id empty.

**F-M28a:** Before every SubDL search/upload the plugin calls the id validation to cross-check and correct Jellyfin's IDs via TMDB when a key is configured. Mismatches are corrected, missing IDs backfilled. Automatic whenever a TMDB key is present; no separate UI switch.

**F-M151 [B1]:** **The upload ID quality gate — no switch, it always runs.** Before an upload Jellyfin's IMDb/TMDb ids are validated against TMDb: missing ids are filled, conflicting ids are corrected towards TMDb, and the type is re-decided from the answer. A quality-gate setting existed and was read by no code path — the gate is not an option but the thing that makes the TMDb key load-bearing (F-M203).

**F-M151b [D]:** **The download ID quality gate — same, and no switch either.** Before a search the ids are validated and corrected via TMDb so the SubDL search points at the right title. the download-side gate was equally unread.

**F-M203 [B1]:** **The TMDb API key is required — without it no run starts, in either direction.**

Rationale: id resolution is TMDB-authoritative (F-M190) and the SHOW id a series upload must carry (F-M191) is only obtainable through the key. With Jellyfin's own ids a mis-typed episode would be uploaded with an **episode id in the series slot**.

**No partial operation.** Id resolution runs in both directions and feeds the search and the upload payload alike. The plugin refuses to start and names the missing field (F-M19).

Upload and download side alike, from one check (`MissingCredentials`): the run stops before the first item, so nothing is ever uploaded or searched with unverified ids.

GUI: the field label reads **"TMDb API key (v3) — required"**, the input carries `required`, and the save handler **refuses to save an empty key**.

Status: a missing key reports **red**; the settings page refuses to save it and the run refuses to start.

**F-M63:** **Official API endpoint:** `https://api.subdl.com` for all API calls (login, search, upload, /me).

**F-M217 [D]:** **For a download the FILE NAME decides type, title and episode — not the Jellyfin library.** Jellyfin types an episode living in a library declared as *movies* as a film, and its item NAME is the raw file name, which resolves against nothing. Three consequences:

**The seeder prefilter must not drop such an item:** the id gate may not depend on a lookup that resolves the item's name. An item that cannot be resolved by name still has to reach the queue.

**The search query is the PARSED title** (the file-name parser) — a raw file name returns 0 hits against the type-neutral `search/multi`, its parsed title returns the show.

**Season and episode come from the file name too.** For a film-typed item Jellyfin reports 0/0, so the F-M215 pack guard (`episode > 0 || season > 0`) could never fire.

**A year-filtered search retries once without the year.** Jellyfin's year is frequently the IMPORT year (a 2022 show carrying 2025): the filtered query returns 0 hits where the unfiltered one finds the title.

**F-M62:** **429 classification fix:** a 429 carrying no rate headers and naming neither `service_busy` nor `rate_limit` is treated as the conservative fallback (server-level 429, next-midnight anchor), not as the daily allowance — a 429 whose kind cannot be named must never be guessed in the direction that discards a day of work. The real daily allowance carries a reset signal.

**F-M206:** **A respacing log line must not claim a quota reset.** A fire carrying an exact, caller-computed time (lock-busy, rate-limit respacing) applies **no jitter** and must log `respaced by JobSpacingMinutes after lock-busy or rate-limit (no jitter)`. The quota-reset wording (reset anchor + 30–300 min jitter) is confined to the branch that actually dices it.

**F-M191b [D]:** **Unresolvable IMDb id ⇒ DROP it, never keep it.** When `/find` returns every result array empty, TMDB does not index that IMDb id at all — for a series item it is an episode id TMDB has no record of. Keeping it "as the best available" uploads an episode IMDb id in the SERIES slot, exactly what this rule forbids. Both ids are dropped so the caller's type-free title search resolves the show by name.

**F-M343 [D]:** **Every SubDL call names the integration, and the agent carries the real version — the TMDb calls carry that agent too.** The search appends `client=other`: the published value list is bazarr, stremio, kodi, subdl_player, custom_integration, other (subdl.com/api-doc, read), this plugin has no entry of its own, and `other` is the honest value rather than claiming `custom_integration` — a category the operator did not choose. A dedicated value is SubDL's call, not ours. The parameter is documented for the SEARCH endpoint only, so no other call carries it.

Dieselbe Suche mit und ohne Parameter ergabned an IDENTICAL payload (HTTP 200, status true, 10 candidates, equal content signature), and an unknown value (`client=bogus_wert`) was accepted just as silently. The parameter cannot break a call, and the published list is a convention rather than a server-side check.

The User-Agent is built from the assembly version — `SubDL-Scribe/<version> (+https://github.com/nrg80/sub-scribe)`. Until it was the fixed literal `Jellyfin.Plugin.SubdlSync/1.0`, so every release since 1.0 introduced itself to SubDL as version 1.0 and no two versions could be told apart. Read from the assembly, a version bump needs no second edit and cannot fall out of step with it.

`SubdlApiClient.NewHttpClient` is the one factory every SubDL client is built with, and the client factory sets the same agent as its own client's default. Both exist for the same reason: the LOGIN call builds its own request and adds no header of its own, and the plugin builds SubDL clients at more than one place — the main API client, the status probe, the quota read, the upload postprocessing controller and the upload pipeline's own pass. Those ad-hoc clients carried NO agent at all, so their calls (login in particular) went out with no User-Agent whatsoever. Verified that login succeeds with the plugin agent, with the old one and with none, so the agent is free to set. The status probe's said `SubDL-Sync-Status/1.0` — the pre-rename product name, frozen at the same version — and carries the shared agent now.

**Who sees the agent (traced through the tree):** three counterparties — `api.subdl.com` (search, login, quota read, status probe), `dl.subdl.com` (the subtitle file download) and `api.themoviedb.org`. The third is not an oversight and the operator confirmed it on: the same client factory hands its HttpClient to the resolver, and the status controller's probe client serves the TMDb configuration probe as well, so an id lookup identifies its caller for the same reason a search does. Both services sit behind a TLS-terminating front, so the operator and its edge read the header; the plugin's own log, Jellyfin itself, the LAN and the ISP do not. The `client=other` parameter reaches less far than the header: it is appended to `api.subdl.com` search URLs only, so TMDb never sees it.

**F-M344 [D]:** **The upload reports the SOURCE quality of the subtitle, read from the file name; das feste Literal `web` wird nicht gesendet.** subdl.com/api-doc lists exactly five values for the upload field `quality` — `web` (Web release, streaming, WEB-DL), `bluray`, `dvd`, `hdtv`, `cam` — and the metadata step sent `web` for every file since the first upload implementation (F-M8), with no requirement behind it. It was wrong for every non-streaming file: the Dinosaurs `480p DVD x265 Panda` episodes went to SubDL as WEB, and so does every BluRay or HDTV file. The field is the release's quality tag in SubDL's lists and what its own filters read, so a wrong value mislabels the upload for everyone who filters by quality. Auf der Live-Seite: unsere eigenen Uploads, als `web` gesendet, tragen das Tag `webdl` — the label differs from the value, the value is accepted.

The value is read from the NAME, and the subtitle's OWN name wins over the container's: a sidecar states where THAT subtitle came from, while the file next to it may be a different release of the same film; an embedded track has no own name, so the media file is read. The LAST source word in the name decides, and words are matched as WHOLE TOKENS — a release name puts its title first and its source after it, so `The.DVD.Club.2019.1080p.BluRay.x264` is a BluRay and the disc word belongs to the title, and a substring rule would read the CAM inside an ordinary word as a camera recording. Compact forms are consulted only when no token matched and only against distinctive strings (`BLURAY`, `BDRIP`, `DVDRIP`, `HDTV` …), for spellings whose separators swallow the word (`Blu-Ray`). **The asymmetry decides the tables:** a missed BluRay falls back to `web`, which is the behaviour that shipped before, while a false BluRay mislabels a streaming release — so the fallback is cheap and the false positive is not. The value is a REQUIRED parameter of the upload call, with no default, so no call site can silently keep the literal; `SourceQuality.Allowed` carries the documented five and the assertion reads it.

**F-M350 [D]:** **The upload reports the FRAME RATE CODE of the item's video; das feste Literal `0` wird nicht gesendet.** The metadata step sent `framerate=0` — SubDL's "default/unknown" — although the rate is already read for the download-side FPS gate (F-M43 stage 1). The codes are SubDL's, not invented: measured on the live search API across 120 entries, code `2` always arrives with `fps: 23.976` and code `0` always with `fps: null`, and the numeric table is the one the download side already parses (`0` unknown, `2` = 23.976, `3` = 25, `4` = 29.97, `5` = 24, `7` = 30). The rate comes from the item's primary video stream (average first, real second); the **nearest** table entry wins, because 23.976 sits only 0.024 from 24 — a fixed epsilon would have to be smaller than that and would then reject the float noise of real container values. Anything further than 0.05 away, and every rate outside the table (48, 50, 59.94, 60), stays `0`, which is what shipped before, so an unusual rate cannot become a wrong claim. **Fallback:** neither field is trustworthy alone — in einer Bibliothek tragen 27 Dateien einen falschen Durchschnitt (`1000`, which a container reports for one frame in one millisecond) with a correct real rate, 18 a good average with a real rate 0.05+ off (`29.9166`), and 11 map to a DIFFERENT code from both. The average is taken first and the real rate only when the average does not map: **98.0 % of the library then reports a code, against 96.8 % on the average alone and 97.3 % on the real rate alone.** The average goes first because it is the playback rate (frames divided by duration — a file with every second frame dropped reports 12.6 against a real 25) and because the download gate (F-M43 stage 1) reads the same order, so a file gets the same rate in the gate and in the upload; on the 11 conflicts that is a consistency choice, not a truth claim. The lookup runs **once per ITEM**, before the candidate loop — the rate is a property of the video, so an item with four uploadable tracks must not read it four times. **`production_type` deliberately stays `0`:** SubDL documents no value list for it and the search API does not return the field at all, so a guessed enum would be a claim without evidence. See F-M8, F-M325.

**F-M17a:** **Die Suche ist auf die eine Folge eingegrenzt.** Staffel und Folgennummer gehen mit; ohne sie füllt ein Staffelpack die Seitengrenze und verdeckt passende Releases auf späteren Seiten.

**F-M17d:** **Ein Suchfehler blockiert das Item nicht.** Was die Suche liefert, gilt; ein Fehler führt zum offenen Ergebnis, nicht zum Ausschluss.

**F-M17f:** **Zurückgezogen — es gibt keine Abfrage der eigenen Veröffentlichungsliste vor der Suche.**

**F-M190b:** **Fehlt die IMDb-Kennung in der Detailantwort einer Serie, wird sie über die externen Kennungen derselben Id geholt.** Die Detailantwort führt sie für Serien leer; ohne den zweiten Aufruf hätte jede Serienfolge als „ohne IMDb" gegolten und wäre nie hochgegangen.
**F-M480 [D]:** **tvsubtitles.net wird über seine Seiten erschlossen, nicht über eine API — und der Weg ist der der Seite selbst.** Die Seite hat keine API (gemessen 15.11.2026: `/api*`, `/xmlrpc.php` und `api.tvsubtitles.net` → 404 bzw. kein DNS; nur `/rss.xml` ohne Folgendaten). Der Abruf läuft deshalb `/add.html` → `/tvshow-<id>.html` → `/tvshow-<id>-<staffel>.html` → `/episode-<seite>.html` → `/subtitle-<id>.html` → `/download-<id>.html`. **Die Sprache steht nicht im Text** — verlässlich ist allein das Flaggenbild, und es wird **je Block** gelesen (von einem Untertitel-Anker bis zum nächsten), weil eine Suche über die ganze Seite die Flagge des folgenden Eintrags erwischt; Einträge ohne Flagge werden übersprungen. **Seiten-Ids sind keine Folgennummern** und die Staffelliste läuft absteigend — Staffel und Folge kommen aus der Überschrift der Folgenseite, mit Rückfall auf das erste `NxM`. **Der Dateiname des Downloads steht nicht im Link**, sondern als nummerierte JavaScript-Fragmente auf der Download-Seite, die in Reihenfolge zusammengesetzt werden. Jeder Parser ist eine eigene, seiteneffektfreie Funktion und wird von der Suite `tvsubtitles` mit den am 15.11.2026 tatsächlich gelieferten Seiten gefahren, nicht mit der lebenden Seite. Geprüft von T203.

**F-M481 [D]:** **Der Upload ist das Formular der Seite, und das Kommentarfeld trägt die Kennungen.** Gesendet wird `multipart/form-data` an `/add1.php` mit Referer `/add.html`; die Felder gehen in der Reihenfolge der Seite: `tvshow`, `season` (1–22), `episode` (0–100), `lang`, `rip`, `release`, `comment`, `go`, dazu `double` und `hi` **nur wenn zutreffend**. `comment` trägt **nur** TMDb- und IMDb-Kennung (`TMDB 1422 / IMDb tt1442464`), weil die Seite kein Feld dafür hat; **ohne beide Kennungen bricht der Bau ab**. Der Client-Vermerk steht seit der Anweisung vom 17.11.2026 („Bitte nur noch imdb tmdb") **nicht mehr** im Feld — was vorher dort stand, bleibt auf der Seite stehen, weil sie kein Bearbeiten kennt. Das Feld `author` wird **nicht mitgeschickt** — ein fehlender Teil ist der einzige Weg, eine Vorbelegung der Seite nicht zu überschreiben. Die Sprachkürzel sind die der Seite, nicht ISO (`br` = brasilianisches Portugiesisch, `cn` = Chinesisch, `jp` = Japanisch, `gr` = Griechisch, `cz` = Tschechisch, `ro` = Rumänisch); ein Kürzel, das die Auswahlliste nicht kennt, lässt das Formular auf seinen ersten Eintrag zurückfallen und der Upload landet als Englisch — deshalb wird gegen die Tabelle geprüft. Eine Serie wird über `/add-tvshow1.php` angelegt (`name`, `year_s`, `seasons`, `go`), **nachdem** der Name in der Serienliste gesucht wurde. Ein Trockenlauf baut den Rumpf vollständig und sendet nichts; gesendet wird nur bei ausdrücklichem `dryRun: false`. Geprüft von T203.

**F-M482 [P]:** **Der Transport dekodiert nicht, und er entscheidet an den Bytes, die kamen.** Die Download-Adresse antwortet in zwei Formen — mal mit der Warte-Seite samt Dateiname im Skript, mal mit dem Zip direkt (gemessen 15.11.2026); der Client prüft den Zip-Kopf (`PK\u0003\u0004`) und parst nur, wenn kein Archiv kam, statt sich auf einen Content-Type zu verlassen, den die Seite nicht immer setzt. Die Untertitel-Bytes werden **unverändert** weitergegeben: die Seite liefert je Sprache UTF-8 neben cp1250, cp1251, cp1253, cp1254 und cp1256, und `cp1252` scheitert praktisch nie — genau daraus entstand am 15.11.2026 der Codepage-Fehler, der Russisch, Griechisch und Arabisch unlesbar machte. Dekodiert wird in der Pipeline, nicht im Transport. Geprüft von T203.

**F-M483 [D]:** **Es gibt zwei Download-Endpunkte und zwei Upload-Endpunkte, und der Rest des Plugins bleibt, wie er ist.** Je Quelle einer pro Richtung: `SubdlDownloadEndpoint`/`SubdlUploadEndpoint` und `TvSubtitlesDownloadEndpoint`/`TvSubtitlesUploadEndpoint`, hinter einem gemeinsamen Paar Schnittstellen (`IDownloadEndpoint`, `IUploadEndpoint`) mit einem neutralen Modell (`SubtitleQuery`, `SubtitleHit`, `SubtitlePayload`, `UploadTicket`, `EndpointAnswer`). Der SubDL-Endpunkt ist eine **Übersetzung, keine Neufassung**: er ruft genau die Methoden, die die Pipeline vorher selbst gerufen hat, und Routen, Quota, Cache und Tore bleiben, wo sie waren. `DownloadPipeline`, `UploadPipeline`, Store, QA-Tore, Scheduler und GUI-Aufbau werden nicht angefasst. Was eine Quelle nicht kann, sagt **ihr** Endpunkt mit einem Grund statt halb zu senden: Staffelpack (baut die Pipeline, F-M372), fehlende IMDb-Kennung, Bytes, die nicht UTF-8 sind (SubDL nimmt Text, F-M482), unbekannte Sprache, fehlender Serienname, Serie nicht auf der Seite — je eine Ablehnung, keine Ausnahme für etwas, das der Aufrufer hätte wissen können. Geprüft von T204.

**F-M484 [D]:** **Ein Endpunkt, der nicht in den Lauf eingebunden ist, sagt das — der Katalog liefert nur, was laufen darf.** `SubtitleEndpointCatalog.Enabled…` verlangt **beides**: den Schalter aus F-M477 **und** `Wired`. Solange die Pipelines den tvsubtitles-Endpunkt nicht aufrufen, steht er auf `Wired = false` und bleibt auch bei eingeschaltetem Schalter draußen; `Refused(...)` nennt ihn samt Grund, und dieser Satz ist **wörtlich derselbe**, den die Konfigurationsseite unter dem Schalter und `MissingProviderReason` führen — eine Aussage, nicht drei (Linie von F-M366). Ein eingebundener Endpunkt, dessen Schalter aus ist, meldet `switched off`. Geprüft von T204.

**F-M485 [D]:** **Beide Richtungen sind einzeln schaltbar, und die Schalter wirken im Lauf.** Mit F-M483/F-M484 sind die vier Endpunkte eingebunden, nicht nur vorhanden: **Download** — hat SubDL für eine Sprache keinen Kandidaten, bekommt die zweite Quelle ihre Gelegenheit, sofern ihr Download-Schalter an ist; ihre Treffer laufen anschließend durch **dieselbe** Rangfolge, dieselben Tore, denselben Fit und dieselbe Ablage wie ein SubDL-Treffer, sie tragen nur ihren Anbieter im Kandidaten und werden bei ihrem eigenen Endpunkt geholt (die SubDL-Routen Archiv/Cache/v2 gelten nur für SubDL). **Upload** — SubDL wird genau wie bisher gerufen, solange sein Schalter an ist (der Antwortkörper wird vom Aufrufer gelesen und bleibt erhalten); ist er aus oder nimmt er den Untertitel nicht, geht derselbe Untertitel an die zweite Quelle, sofern deren Upload-Schalter an ist. Der Serienname für die zweite Quelle kommt aus dem Mediendateinamen über denselben Parser, den der Download benutzt — die Quelle hat keinen Id-Index. **Jeder Schalter wirkt für sich:** SubDL-Download an und Upload aus, tvsubtitles nur Upload, alles aus — der Katalog (F-M484) gibt genau das heraus, was der Schalter sagt. **Grenzen, die bleiben:** Staffelpacks und der HI-Pool sind SubDL-Sache, die zweite Quelle kann nur Einzelfolgen; ein Fehler der zweiten Quelle kippt den Lauf nicht, er wird als Warnung protokolliert. Geprüft von T205 und T202.

**F-M486 [D]:** **Ein heruntergeladener Untertitel trägt seine Herkunft samt der Id der Quelle im Store.** Je Untertitel werden zwei Felder geführt: `Origin` (`subdl` oder `tvsubtitles`) und `OriginId` — die **eigene Id der Quelle**, also die tvsubtitles-Untertitel-Id (z. B. `170678`) oder die SubDL-Eintrags-Id. Damit ist ein Fund eindeutig identifizierbar: die Id benennt den Eintrag, der Inhalt-Hash benennt den Text, und zusammen sagen sie, was woher kam. `null` heißt **„diese Zeile sagt nichts"** (Altzeile, oder eine Datei, die das Plugin nicht geholt hat) und wird nie geraten; ein späterer Schreiber ohne Kenntnis darf eine vorhandene Angabe nicht löschen. Die Felder liegen in `sidecars` und `embeds` (Schema 10, Migration legt sie nach). Geprüft von T206.

**F-M487 [D]:** **Keine Kreuzkontamination: ein Fund geht nur zu seiner eigenen Quelle zurück.** Beim Upload wird die Herkunft über den Inhalt-Hash aus dem Store gelesen; ist sie **bekannt**, darf der Untertitel ausschließlich bei **derselben** Quelle hochgeladen werden — ein tvsubtitles-Fund nie zu SubDL, ein SubDL-Fund nie zu tvsubtitles. Ist sie **unbekannt** (`null`: Handkopie, eingebettete Spur, Altzeile), bleibt jeder Endpunkt erlaubt, sonst könnte der Betreiber seine eigenen Dateien nicht mehr hochladen. Die Ablehnung wird protokolliert und nennt beide Seiten. Die Regel ist bewusst kurz und liegt an einer Stelle (`Provenance.MayUploadTo`), damit sie nicht in zwei Pipelines zwei Fassungen bekommt. Geprüft von T206. **Nachtrag 15.11.2026 (Operator-Anweisung, verschärft): „Herkunft subdl: nie zurück zu subdl, nie zu tvst."** Eine bekannte Herkunft wird **gar nicht** mehr hochgeladen — nicht zurück zur eigenen Quelle (Reup) und nicht zur anderen (Kreuzkontamination); ein Download ist ein Fund, kein eigener Kandidat. Unbekannte bzw. externe Herkunft (eingebettete Spuren, lose `.srt` ohne Herkunft) bleibt zu **beiden** Endpunkten erlaubt, in Kettenreihenfolge und je Quelle mit eigener Marke. Der Grund steht im Log als `origin:subdl — downloaded there, never uploaded` bzw. `origin:tvsubtitles — downloaded there, never uploaded` (`Provenance.OriginBlockedReason`).

**F-M488 [D]:** **Vier eigene Kennzeichen im Store, je Quelle und Richtung eines** — `SubdlDownloadId`, `SubdlUploadId`, `TvSubtitlesDownloadId`, `TvSubtitlesUploadId` (Schema 11, in `sidecars` **und** `embeds`, per `ALTER TABLE` nachgezogen). Eine Zeile sagt damit von selbst, was wo passiert ist; niemand muss `Status` interpretieren. Der Download schreibt das Feld **seiner** Quelle (`SubdlId` bzw. die tvsubtitles-Untertitel-Id), der Upload schreibt die Id, die die Quelle für **unseren** Upload zurückgibt. **SubDL** liefert seine Id in der Antwort des Uploads — dafür war nichts Neues nötig, sie wird nur zusätzlich in ihr eigenes Feld geschrieben. **tvsubtitles.net gibt keine Id zurück**, deshalb wird sie nach dem Senden von der Folgenseite **zurückgelesen** (Sprache + Release müssen passen; `FindUploadedIdAsync`, 60-Sekunden-Leine). **Jeder Upload bekommt ein Kennzeichen:** liefert die Quelle keine Id (Rückleser findet nichts, oder SubDL antwortet ohne), **vergibt das Plugin eine eigene** — Format `own:<quelle>:<JJJJMMTTTHHMMSSZ>:<8 Hex>`, immer mit Präfix `own:`, damit sie nie für eine Id der Seite gehalten wird (`Provenance.OwnUploadId`, `IsOwnUploadId`). Ein Upload ohne Kennzeichen wäre von einem, der nie stattfand, nicht zu unterscheiden. Gesetzt wird nur das Feld der Quelle, die den Upload **wirklich genommen hat**. Geschrieben wird nur, was bekannt ist; ein leerer Wert löscht keine vorhandene Angabe. Der Upgrade-Pfad 9 → 11 wurde an einer echten Datenbank geprüft. Geprüft von T207.

**F-M489 [D]:** **Kein Reup, und zwar je Quelle.** „Schon hochgeladen" wird nicht mehr aus `Status=Uploaded` gelesen (das heißt „irgendwo hochgeladen"), sondern aus dem **eigenen Kennzeichen der Quelle** (F-M488): `SubdlUploadId` für SubDL, `TvSubtitlesUploadId` für tvsubtitles — in `embeds` **und** `sidecars`. Damit gilt: **kein Reup von SubDL auf SubDL**, **kein Reup von tvst auf tvst**, und trotzdem **kein gegenseitiges Blockieren** — ein tvst-Upload hält einen SubDL-Upload nicht auf und umgekehrt. Die Entscheidung für beide Quellen fällt an **einer** Stelle (`UploadGate.Decide`: Schalter × Herkunft × „hat die Quelle ihn schon") und wird an drei Stellen benutzt: Sammelstufe (Embed), Sammelstufe (lose Datei), Upload selbst. Altzeilen ohne eigenes Kennzeichen gelten für SubDL als hochgeladen, wenn `SubdlId` **und** `Status=Uploaded` stehen — sonst ginge nach dem Upgrade jeder alte Upload ein zweites Mal hinaus; `SubdlId` allein genügt nicht, weil der Download es auch schreibt. Der Grund wird protokolliert (`already-uploaded:subdl+tvsubtitles`, `cross-source`, `no-upload-source`). Geprüft von T208.

**F-M490 [D]:** **„Hochgeladen" steht je Quelle als eigenes Feld im Store** — `SubdlUploadedAt` und `TvSubtitlesUploadedAt` (Schema 12, `sidecars` **und** `embeds`, per `ALTER TABLE` nachgezogen). Ein Zeitpunkt statt eines Schalters: „hochgeladen" ohne „wann" beantwortet die halbe Frage, und eine Zeile kann **beide** Quellen tragen — im März zu SubDL, im November zu tvsubtitles. Gesetzt wird nur der Zeitstempel der Quelle, die den Upload wirklich genommen hat; ein Zeitstempel wird nie mit einem leeren Wert überschrieben. `Registry.IsUploadedTo` liest zuerst diesen Zeitstempel und fällt dann auf das Kennzeichen der Quelle (F-M488) zurück — eine Zeile mit Id, aber ohne Zeitstempel (Altdaten aus Schema 11) gilt weiter als hochgeladen. Damit hängt F-M489 nicht mehr daran, ob eine Id vorhanden war, sondern an einer eigenen, lesbaren Angabe. Der Upgrade-Pfad 11 → 12 wurde an einer echten Datenbank geprüft. Geprüft von T209.

**F-M491 [D]:** **Die Reihenfolge der Download-Quellen ist einstellbar, und „erfolgreich heruntergeladen" verlangt die Signifikanz.** Unter Downloads steht die Auswahl `SubDL first, tvsubtitles.net as fallback` / `tvsubtitles.net first, SubDL as fallback` (`DownloadOrder`, Default **tvsubtitles.net zuerst** — Operator-Entscheidung 15.11.2026: „Tvst ist zuerst"). Steht tvst vorn, wird es **zuerst gefragt**; die SubDL-Kandidaten derselben Sprache werden danach **zusammengeführt**, nicht ersetzt (F-M494). Der Rest des Laufs — Rangfolge, Tore, Fit, Ablage, Herkunft (F-M487) — ist für beide Reihenfolgen derselbe. **Erfolgsbegriff:** bei eingeschaltetem Auto-Sync zählt nur eine Fassung, die die **Signifikanz bestanden** hat (`alignedForNaming`), als erfolgreich heruntergeladen; eine behaltene Originalfassung ist **heruntergeladen, nicht erfolgreich** (`summary.DownloadedWithoutSignificance`, das Paar bleibt über die Abdeckung offen — F-M452). Ist Auto-Sync **aus**, gibt es keinen Fit und damit keinen Unterschied: jeder Abruf zählt. Gilt für beide Quellen gleich. Geprüft von T210.

**F-M492 [D]:** **Zugangsdaten und Quota richten sich nach den Quellen, die wirklich dran sind.** Gefunden am 15.11.2026 im Testbench-Lauf: ein tvsubtitles-first-Lauf starb zweimal an Prüfungen, die nur SubDL betreffen — erst an fehlenden SubDL-Zugangsdaten, dann an „download limit spent (50/50)". Beides gehört zum SubDL-Konto, nicht zum Lauf. Deshalb: der **SubDL-API-Schlüssel** wird nur verlangt, wenn SubDL-Download an ist (er authentifiziert Suche, Dateiabruf und Quota-Lesen); das **Login-Paar** nur, wenn SubDL-Upload an ist (es authentifiziert den Upload); der **TMDb-Schlüssel** bleibt in beiden Richtungen und für beide Quellen Pflicht, weil er die Ids auflöst, auf denen Store, Rangfolge und der tvst-Upload-Kommentar beruhen. Die **Download-Quota** hält einen Zyklus nur noch, wenn SubDL die einzige Quelle ist: ist SubDL-Download aus, wird sie gar nicht gelesen; ist SubDL ausgeschöpft und tvst verfügbar, läuft der Zyklus auf tvst und der Grund steht einmal im Log. Auch die Richtungsgates fragen die **Richtung**, nicht SubDL: der Download-Task, der Upload-Task und die Ampel-Aus-Zustände benutzen `AnyDownloadEnabled`/`AnyUploadEnabled` — sonst sähe „SubDL aus, tvst an" wie eine abgeschaltete Richtung aus und der Lauf würde mit „Download pipeline disabled" übersprungen (im Testbench-Lauf genau so passiert). Das **Einreihen** im Seeder fragt ebenfalls die Richtung (`AnyDownloadEnabled`/`AnyUploadEnabled`); die SubDL-Download-Quota darf die Queue nur leeren, wenn keine andere Quelle da ist — sonst füllte ein Lauf mit SubDL aus und tvst an eine leere Queue (gemessen 15.11.2026: „0 download queued", obwohl 27 Folgen ohne ES-Untertitel dastanden). Geprüft von T211. **Weitergeführt von F-M497:** die *Forderung* selbst ist inzwischen schmaler geworden — Pflicht ist allein der TMDb-Schlüssel, und eine unkonfigurierte Quelle fällt aus der Kette, statt den Lauf zu blockieren.

**F-M497 [D]:** **Pflicht ist allein der TMDb-Schlüssel; SubDL ist optional und wird ohne Konfiguration übergangen — in beiden Richtungen.** Operator-Anweisung 15.11.2026: „Pflichtkonfiguration ist tmdb key. Wenn subdl nich konfiguriert -> skip subdl upload/download. Kennzeichnung als required for subdl." Bis hierher standen vier Zugangsdaten in EINER Pflichtliste (F-M19/F-M203/F-M492): fehlte eines, startete der Lauf gar nicht. Damit starb ein Lauf an einem Konto, das er nie angesprochen hätte — auf jf-test zweimal gemessen. Jetzt gilt: (1) **Der TMDb-Schlüssel ist die einzige Pflicht.** Er löst und korrigiert die Ids, auf denen Store, Rangfolge, Fit und der tvst-Upload-Kommentar in **beiden** Richtungen und für **beide** Quellen beruhen; ohne ihn kann keine Richtung ihre Arbeit tun, also wird der Lauf wie bisher mit genannter Meldung **nicht gestartet**. `MissingCredentials()` liefert genau diesen einen Eintrag. (2) **Die SubDL-Zugangsdaten sind eine Quellenfrage, kein Lauf-Fehler.** Fehlt der API-Schlüssel (Download) oder das Login-Paar (Upload), ist das **kein Blocker**: die Quelle tritt nicht an und wird mit **einer** Zeile übergangen (`[SubDL] … — skipped — not configured (required for SubDL only)`, Download: `[SubDL-D] … — SubDL skipped — not configured …`), die andere Quelle läuft weiter. Die Frage steht als **eigener Helfer** (`IsConfiguredForDownload` / `IsConfiguredForUpload`) und wird **neben** dem Schalter (`SourceAllowed`) unmittelbar vor dem Start jeder Quelle gestellt — Toggle **und** Konfiguration entscheiden zusammen, ob eine Quelle antritt; im Download fällt ohne API-Schlüssel schon die Suche aus (sonst beendete ihr 403 den Lauf), und der Abruf selbst fragt dieselbe Tür ein zweites Mal. (3) **Die Seite sagt es.** Die drei SubDL-Felder (API-Key, Benutzername, Passwort) tragen die Kennzeichnung **„Required for SubDL only"**, der TMDb-Schlüssel steht als **Required** und einzige Pflicht da — und zwar **vor** dem SubDL-Block (Nutzung: „Tmdb confi dann auch vor subdl konfi in der gui"): wer die Seite liest, sieht zuerst, was er haben muss. Getauscht sind nur die beiden Blöcke; geladen und gespeichert wird über die Feld-IDs, die von ihrer Position nichts wissen. Feld-IDs und Schalter bleiben unverändert, es kommt keine neue Option dazu. (4) **Wechselwirkung mit dem Trockenlauf (F-M496):** *nötig* ist im Trockenlauf nichts — er macht keinen Aufruf und wird nie an Zugangsdaten abgewiesen; *anwesend* sein muss eine Quelle nur, wenn sie wirklich antritt, also live. Die beiden Sätze beschreiben zwei verschiedene Fragen und widersprechen sich nicht. Geprüft von T216, T217 und T218.

**F-M493 [D]:** **Jede Ablage trägt ihre Herkunft — an allen vier Schreibstellen.** Messung jf-test 15.11.2026: 5 von 24 behaltenen Originalen standen ohne `Origin` im Store, weil der Pfad „Original behalten" seine eigene Zeile ohne Herkunft schrieb. Das behaltene Original ist eine eigene Zeile, also braucht SIE die Marke — sonst weiß ein späterer Upload nicht, wohin die Datei gehört (F-M487), und „kein Reup je Quelle" (F-M489) sieht sie nicht. Gilt für die Hauptablage, das behaltene Original, die HI-Ablage und das behaltene HI-Original. Der Laufabschluss nennt zusätzlich `DownloadedWithoutSignificance`. Geprüft von T212.

**F-M494 [D]:** **Beide Download-Endpunkte werden gefragt, der zweite bekommt seinen eigenen Versuch.** Messung 15.11.2026 (jt-test, 27 Folgen, tvsubtitles-first): jede Folge hatte genau einen tvst-Treffer, und weil F-M491 die tvst-Liste an die Stelle der SubDL-Liste setzte, stand **keine einzige SubDL-Zeile** im Log — von 27 Folgen wurden 6 am Ton ausgerichtet, 19 blieben als Original liegen, 1 wurde verworfen. Zwei Folgen daraus: (1) die Kandidatenlisten beider Quellen werden **zusammengeführt** statt einander zu ersetzen — tvst-Treffer und die SubDL-Treffer **derselben Sprache**, jede Quelle nur, wenn ihr Download-Schalter an ist; die Logzeile nennt **beide** Zahlen (`{Tv} tvsubtitles candidate(s), {Sd} SubDL candidate(s), order …`). (2) Die Rangfolge bekommt eine **führende Stufe nach Quelle**, die der eingestellten Reihenfolge folgt (`TvSubtitlesFirst`: tvst vor SubDL; `SubdlFirst`: umgekehrt); Pack und Textscore bleiben dahinter unverändert — der zweite Endpunkt wird gefragt, verdrängt den ersten aber nicht. (3) Der Abbruch der Korrektur-Suche gilt je Quelle: ein Ablehnungswort stammt von **einer** Quelle und sagt nichts über die andere. Steht unter den noch nicht versuchten Kandidaten (`ranked.Skip(attempts)`) mindestens einer der anderen Quelle, wird der Abbruch zurückgenommen und der Ablehnungszähler dieser Quelle auf 0 gesetzt — die andere Quelle bekommt ihren eigenen Versuch; erst wenn kein Kandidat der anderen Quelle mehr übrig ist, endet die Suche wie bisher (F-M318). (4) **Vor dem Start jedes Endpunkts** wird geprüft, ob genau diese Quelle per Schalter freigeschaltet ist — über **einen** Helfer (`SourceAllowed(provider)`), den der Start der tvst-Suche, der Hauptdurchlauf, die SDH-Variante und der Abruf selbst benutzen; eine abgeschaltete Quelle heißt „diesen Kandidaten überspringen", nie „den Lauf beenden", und die Zeile `candidate from {Source} skipped: that source is switched off` steht im Log (Operator-Anweisung 15.11.2026: „Vor Start des jeweiligen Endpunkts prüfen ob er per Toggle freigeschaltet ist"). Geprüft von T213.

**F-M504 [D]:** **Die Quellen werden in der eingestellten Reihenfolge befragt — eine leere erste Quelle beendet den Vorgang nicht mehr, und die Quellen sind eine Liste, kein Paar.** Gemessen 15.11.2026 im Probelauf auf jf-test (Trockenlauf mit Produktionskonfiguration, `DownloadLanguages` DE): für jede Folge stand nur `[SubDL-D] … — no candidates on SubDL for [DE]` im Log — **27 Zeilen, keine einzige `[tvsubtitles-D]`-Zeile**, obwohl der tvst-Download-Schalter die ganze Zeit an war. Ursache war die Bauform, nicht ein Schalter: nach der SubDL-Suche endete der Vorgang bei `candidates.Count == 0`, und die Stelle, die die zweite Quelle fragte, lag **dahinter** in der Sprachschleife. Die zweite Quelle war damit genau in dem Fall stumm, für den sie da ist (F-M485) — bei der ersten Quelle mit Treffern lief alles wie erwartet. Jetzt gilt: (1) **Die Quellen werden in der eingestellten Reihenfolge befragt, nicht nur in ihr sortiert** (Nutzerkorrektur 15.11.2026: „Suchen engines werden der Reihenfolge nach befragt. Zur zeit sind es 2"). Bis dahin lief die SubDL-Suche als fester Block **immer zuerst**, und `DownloadOrder` entschied nur über Rang und Nachfrage — bei `TvSubtitlesFirst` wurde tvsubtitles also **nach** SubDL gefragt. Jetzt ist die Frage-Reihenfolge eine **Schleife über dieselbe Liste**: steht tvsubtitles.net vorn (Default), wird **zuerst** für alle fehlenden Sprachen tvst gefragt und **danach** die SubDL-Suche gefahren; steht SubDL vorn, ist es umgekehrt. Die Stellung der SubDL-Suche in dieser Liste (`SourceOrder.MainSearch`) ist die eine Aussage, die den Ablauf bestimmt; sie bleibt eine **eigene Abfrage** (Id-Prüfung mit Backfill, HI-Pool, Quota-Pfade) und ist nur nicht mehr fest die erste. Gefragt wird **vor** jeder Ausstiegsentscheidung, für **alle** fehlenden Sprachen, **jede Quelle genau einmal je Sprache** — und die Schleife hat **keinen vorzeitigen Ausstieg**: eine leere Antwort der ersten Quelle beendet nichts und überspringt keine Quelle (beides bleibt der Anlass von F-M504). Die **Rangfolge der Treffer folgt derselben Reihenfolge** — Rang 0 ist die zuerst gefragte Quelle —, Pack und Textscore bleiben dahinter unverändert. (2) **Genau einmal je Quelle und Sprache:** die Antwort liegt in einem Wörterbuch (Sprache → Trefferliste) und wird in der Sprachschleife nur noch **gelesen**; es gibt genau **eine** Aufrufstelle von `ForeignCandidatesAsync`, ein Doppelabruf (gleiche Frage, zweiter Netzzugriff, potenziell andere Antwort) ist damit ausgeschlossen — die Regel gilt für **jede** Quelle, nicht nur für die zweite. (3) **Erst wenn alle gefragten Quellen leer sind**, bleibt es beim bisherigen Ergebnis (`NotAvailable`, `SkippedNoCandidates`, Rückkehr ohne Abruf); die Zeile nennt die Quellen, die **tatsächlich** gefragt wurden (`no candidates for [DE] — asked: subdl, tvsubtitles`), statt eine SubDL-Suche zu behaupten, an der tvst nicht beteiligt war. (4) **Keine Zwei-Quellen-Annahme** (Nutzerergänzung 15.11.2026: „Dritte bis n quelle sind ebenfalls denkbar"): die Liste kommt aus `Enum.GetValues<SubtitleProvider>()` in der Reihenfolge aus **`SourceOrder.ForOrder(DownloadOrder)`** — die konfigurierte Quelle zuerst, alle übrigen in Enum-Reihenfolge —, und **je Quelle** entscheidet `SourceAllowed`, ob sie gefragt wird. Eine dritte Quelle braucht deshalb nur einen Enum-Eintrag und einen Endpunkt im Katalog; die Schleifen, die Suche (`ForeignCandidatesAsync(quelle, …)`) und der Abruf (`EndpointFor(provider)`) ändern sich nicht. (5) **Die Tore bleiben unverändert:** `DownloadOrder` entscheidet, **wer zuerst gefragt wird** (und damit, wer die Rangfolge führt) — nicht, **ob** gefragt wird; die führende Rangstufe ist ein **Rang** in der Frage-Reihenfolge (`QuellenRang`, 0 = zuerst), bei zwei Quellen dieselbe Aussage wie vorher, bei drei ohne Umbau richtig. Der Signifikanzbegriff (F-M491) ist **nicht** berührt. Filme sind unberührt: `ForeignCandidatesAsync` steigt ohne Serie/Staffel/Folge selbst aus, bevor es das Netz anfasst (F-M480/F-M503). **Der Upload hat diesen Fehler nicht** (seine Kette fragt jede Quelle je Kandidat, ein Fehlschlag wird als Warnung protokolliert und die nächste Quelle läuft weiter, F-M495); dort war nur die Reihenfolge-Ableitung zweiwertig und benutzt jetzt denselben `SourceOrder`-Helfer. **Verbliebene Ein-Quellen-Annahme:** die Hauptsuche ist SubDLs Id-Suche (`RunV2SearchAsync` über `SubdlApiClient`, mit Id-Prüfung, HI-Pool und Quota-Pfaden) und liefert die Kandidaten der führenden SubDL-Quelle selbst; sie auf den Endpunkt-Katalog umzustellen wäre ein eigener Umbau und ist als `MainSearchSource` benannt (ein Alias auf `SourceOrder.MainSearch` — Frage-Reihenfolge und Ablauf lesen damit dieselbe Aussage). Geprüft von T226.

**F-M495 [D]:** **Auch der Upload fragt beide Endpunkte hintereinander, in der eingestellten Reihenfolge.** Bis hierher wählte der Lauf genau **ein** Ziel (`vonZweiter`) und nahm die zweite Quelle nur, wenn die erste abgelehnt hatte; in den Feldern stand danach genau eine Quelle. Jetzt läuft eine **Kette**: Quelle A wird versucht, danach Quelle B — in der konfigurierten Download-Reihenfolge (keine eigene Einstellung, keine neue GUI-Option) —, und jede Quelle bekommt ihren eigenen Versuch. Vor dem Start **jedes** Endpunkts wird dessen **Toggle** über **einen** Helfer (`SourceAllowed`, derselbe Prüfpunkt wie Gate und Sammelstufe) gefragt; aus heißt „diese Quelle überspringen" mit Logzeile (`source {Source} skipped: that source is switched off`), nicht „Kette beenden". Die bestehenden Tore bleiben und werden **je Quelle** gefragt statt abgeschafft: (a) „diese Quelle hat ihn schon" über ihre Zeitstempel- und Id-Felder (F-M488/F-M490), (b) Herkunft über `Provenance.MayUploadTo` — bekannt heißt **nur** zur eigenen Quelle, unbekannt überallhin (F-M487); ein verbotenes Ziel wird übersprungen und genannt (`cross-source`, `already-uploaded:<Quelle>`). Ein **Fehler** bei Quelle A verhindert den Versuch bei Quelle B nicht (Grundsatz: der Priorität geht immer; kann die zweite nicht, ist das hinnehmbar) — die Ausnahme wird als Warnung protokolliert, die Kette läuft weiter. Nach der Kette sind **beide** Id-/Zeitstempel-Felder gesetzt, soweit die jeweilige Quelle genommen hat; die SubDL-spezifische Id-Nachfrage (`mySubtitles`) läuft nur, wenn SubDL wirklich genommen hat. Geprüft von T214.

**F-M499 [D]:** **Auch die Upload-Queue fragt je Quelle — der zweite Endpunkt bekommt seinen Versuch bis in die Queue.** Gemessen 15.11.2026, 16:04 im Testbench: ein Lauf hatte 27 eingebettete FR-Spuren über SubDL hochgeladen; danach reihte der Seeder **0 Folgen** ein (`scan finished (upload) — 0 upload queued`), also startete kein weiterer Lauf, und der zweite Endpunkt bekam seinen eigenen Versuch nie. Ursache war die Frage „schon hochgeladen" in der Queue: sie lief über `ArePositionsTerminal`, `CountKnownPairs` und `IsSidecarUploaded`, und die lesen `Status=Uploaded` — das heißt „irgendwo hochgeladen" (F-M489 hat genau diese Lesart im Upload selbst schon abgeschafft). Jetzt ist die Bedingung quellenweise: `IsUploadTodo` läuft über die Quellen, die **Schalter und Konfiguration** gemeinsam zulassen (`IsUploadEnabled` **und** `IsConfiguredForUpload`, F-M477/F-M497), und das Item ist todo, sobald **eine** dieser Quellen ihre eigene Marke noch nicht hat (`IsUploadedTo(…, provider)`, F-M488/F-M490 — in `embeds` **und** `sidecars`). Die blinden Leser bleiben bestehen, werden von der Queue aber nicht mehr benutzt: `ArePositionsTerminalFor`, `CountKnownPairsFor` und `IsSidecarUploadedTo` sind ihre quellenweisen Gegenstücke, und „diese Quelle hat ihn" wird an allen dreien über dieselbe Regel gelesen. Eine **Ablehnung** bleibt quellenneutral: sie ist ein Urteil über den Untertitel (Struktur, Laufzeit, Sprachprüfung), wird vor jeder Quellenwahl geschrieben, und sie je Quelle zu lesen hielte die Spur für die andere Quelle ewig offen. Die Sprache bleibt Sache der Quelle — hier wird **keine** gemeinsame Sprach- oder Codec-Zuordnung eingeführt (Operator-Anweisung 15.11.2026: „Aber halt tvst spezifisch. Das andere mapping ist subdl spezifisch."): tvst bildet in `TvSubtitlesClient` ab, SubDL in `SubdlApiClient`, und der Pair-Zähler benutzt weiter SubDLs eigene Abbildung, ohne damit eine Sprachfrage zu entscheiden. **Zweiter Teil, 15.11.2026 um 16:17 im scharfen Lauf gemessen:** der Seeder reihte 27 Folgen ein, und die Pipeline warf sie **alle** wieder weg (`[SubDL] SKIP {File} — upload file-complete`; der Lauf endete mit `0 uploaded`) — dieselbe quellenblinde Frage stand noch im Kopf des Laufs. Die Dateimarke `SubtitlesUploadedAt` sagt „diese Datei ist fertig", nicht „wer sie fertig gemacht hat", und tvsubtitles hatte auf diesen Dateien noch keine Spur, also kam es nie zum Zug. Die Marke bleibt quellenblind — sie beschreibt die Datei, nicht den Endpunkt —, und ihr quellenweises Gegenstück `IsSubtitlesUploadedFor(mediaHash, provider)` filtert sie an der Quelle: sie gilt nur, wenn sie steht **und** jede eingebettete Spur für **diese** Quelle erledigt ist (`IsEmbedTerminalFor`, also eigene Marke der Quelle oder quellenneutrale Ablehnung — dieselbe Leseregel wie im Upload selbst). Der Lauf überspringt ein Medium nur noch, wenn **keine** der eingeschalteten **und** konfigurierten Quellen mehr offen ist (`UploadSources()`; im Trockenlauf ohne die Konfigurationsfrage, F-M496) und nennt sie im Skip-Log; ist eine Quelle offen, steht sie im Detail-Log. Ohne eingeschaltete Quelle und ohne Spurbeleg wird **nicht** übersprungen: dann arbeitet der Lauf das Item, statt es auf Verdacht für alle wegzulegen. Geprüft von T220 und T221.

**F-M500 [D]:** **Auch das SETTLE-Urteil („ist dieser Untertitel schon hochgeladen") fragt je Quelle — ein Upload bei SubDL erledigt tvsubtitles nicht.** Gemessen 15.11.2026, 16:24 im scharfen Lauf: der Lauf verarbeitete die 27 eingebetteten FR-Spuren, endete aber mit `0 uploaded | 34 rejected, 61 stream(s) skipped`, und **jede** Zeile lautete „identical subtitle already uploaded earlier (F-M298)". Die Stelle ist `Registry.RecordSkippedContent`, gerufen aus vier Punkten des Upload-Pfads (Reject-Replay eingebettet und Sidecar, QA-Dedupe, Skip der Kette); sie entschied den Grund `duplicate-content` über `IsContentUploaded` — also über „irgendwo hochgeladen". Lag der Text bei SubDL, galt tvsubtitles als erledigt und bekam nie einen Versuch. Jetzt nimmt das Urteil die Quelle mit: `IsContentUploadedFor(contentHash, provider)` antwortet je Quelle aus deren **eigener** Marke (Zeitstempel, eigene Id, für SubDL die Altzeile `Uploaded`+`SubdlId`, F-M488/F-M490), und `RecordSkippedContent(…, settledSources)` schreibt die `Uploaded`-Zeile **nur** mit den Marken der Quellen, die den Inhalt wirklich halten — eine Quelle ohne den Inhalt bleibt offen. Die Regel steht EINMAL in `UploadPipeline.SettleDuplicateContent`: liegt der Inhalt bei einer Quelle und ist eine **andere** noch offen (eingeschaltet, konfiguriert, ohne den Inhalt), gilt der Kandidat als **nicht** erledigt, die Ablehnung wird nicht geschrieben, und der Kandidat geht weiter an die Kette — dort fragt `UploadGate` je Quelle, und SubDL erkennt seinen eigenen Text als `already-uploaded:subdl`. **Reine Inhaltsdedupe gegen Doppeleinträge bei DERSELBEN Quelle bleibt bestehen:** derselbe Inhalt darf bei tvst hochgehen, obwohl er bei SubDL liegt, aber nicht zweimal bei tvst. Eine **Ablehnung** bleibt quellenneutral, und `duplicate-remote` (das Urteil des Servers) ist ebenfalls kein Quellenbeleg. **Das Herkunftstor gilt unverändert streng und wird nur gelesen** (Operator-Anweisung 15.11.2026: „Nur halt nicht die downloads von subdl bei tvst hochladen und umgekehrt.", nachgeschärft zu „Herkunft subdl: nie zurück zu subdl, nie zu tvst."): der Settle-Weg fragt `Provenance.MayUploadTo`, bevor er einen Kandidaten weiterlaufen lässt; ein Untertitel mit **bekannter** Herkunft bekommt auch auf diesem Weg keinen Versuch, und `IsEmbedTerminalFor` liest dieselbe Herkunft, damit eine solche Spur nicht in jedem Lauf erneut eingereiht wird. **Unbekannte bzw. externe** Herkunft (eingebettete Spuren, lose `.srt` ohne Herkunft) darf zu beiden Endpunkten, in Kettenreihenfolge und je Quelle mit eigener Marke. Die Queue liest dieselbe Frage (`SubdlSeeder.SidecarsNeedWork`: bekannt ist kein Skip mehr, wenn die **andere** Quelle den Inhalt hält). Eine `Uploaded`-Zeile **ohne** Quellenmarke (Settle von vor F-M500, Pack-Zeile) zählt weiterhin als SubDL-Beleg, damit dieser Text nicht ein zweites Mal zu SubDL geht; für tvsubtitles zählt sie nicht — sonst schriebe der Umbau den gemessenen Defekt fort. Geprüft von T222.

**F-M501 [D]:** **Die tvst-Seriennummer steht im Store, und eine fehlende Serie wird mit dem TMDb-Titel angelegt.** Operator-Anweisung 15.11.2026: „Tvst Seriennummer in den Store, gleiche Ebene wie tvshow id von tmdb und tmdb“, „Tmdb titel führt.“, „Tmdb titel führt auch für Doppeleinträge.“, nachgeschärft zu „Doch Ähnlichkeitssuche. Aber groß/Kleinschreibung. And = und. Leerzeichen mehr oder weniger. Ähnlichkeit eng halten.“ Bis hierher löste der Endpunkt die Serie bei JEDEM Aufruf aus dem **Dateinamen** (`MediaNameParser`) und verglich mit „enthält“ — eine Vermutung über eine Schreibweise, und die Nummer stand nirgends, weshalb der Store sie nie lernen konnte; eine Serie, die die Seite nicht führt, wurde nie angelegt. Jetzt gilt: (1) **Das Feld `MediaEntity.TvstShowId`** (`string?`) sitzt direkt neben `TmdbId`/`ImdbId` — keine eigene Tabelle, weil es dieselbe Frage beantwortet: welche Serie IST diese Datei. Schema **12 → 13** mit Migration beim Start (`EnsureMediaColumns`: `ALTER TABLE media ADD COLUMN TvstShowId TEXT NULL`, an die Spaltenliste gebunden, Fehlschlag protokolliert und geschluckt); ein Bestand bleibt gültig, alle Zeilen lesen das Feld als `null` = „noch nicht ermittelt“. (2) **Die Reihenfolge der Auflösung** (`TvstShowResolver.ResolveForMediaAsync`) ist fest: **a)** steht die Nummer am Medium, wird SIE benutzt — **kein Netzzugriff zur Suche, kein Vergleich, keine Anlage**; **b)** sonst wird der TMDb-Titel in der Serienliste der Seite gesucht, **eng verglichen** (`NormalizeShowName`: Groß-/Kleinschreibung egal, `&`/`and`/`und` als dasselbe **Wort**, Leerraum zu einem Leerzeichen, sonst **Gleichheit** — kein Teilstring, kein Levenshtein, keine Token-Quote, kein Abschneiden führender Artikel, keine Jahres-Toleranz); ein Treffer wird ins Medium geschrieben und benutzt; **c)** kein Treffer: anlegen mit `name` = TMDb-Titel, `year_s` = TMDb-Erstausstrahlungsjahr, `seasons` = TMDb-Staffelzahl **auf 1..22 begrenzt** (die eigene Prüfung `BuildAddShow` erlaubt jetzt ebenfalls nur 1..22 statt 1..100, weil das Auswahlfeld der Seite nicht weiter reicht und ein größerer Wert still auf 22 gezogen würde), danach die Serienliste **erneut** holen und erneut eng suchen — **erst wenn die Nummer dort steht**, wird sie geschrieben; eine unklare Antwort (Timeout, Ausnahme, Seite ohne Auswahlfeld) wird **einmal** nachgefragt, bleibt es unklar, wird **nichts** geschrieben, eine Zeile protokolliert und tvst für dieses Medium übersprungen; **d)** angelegt wird **höchstens einmal je Medium und Lauf** (`BeginRun` leert die Sperre am Laufanfang), damit ein Fehlschlag nicht bei jeder Folge erneut versucht wird. (3) **Nichts davon blockiert die Kette:** jeder Ausgang ist ein Ergebnis, kein Wurf — fehlt die Nummer oder scheitert die Anlage, wird tvst übersprungen (`Skipped`) und SubDL bekommt seinen Versuch wie zuvor; ohne TMDb-Kennung oder ohne TMDb-Seriendatensatz wird ebenfalls übersprungen statt geraten. Der TMDb-Datensatz (Name, Originaltitel, Erstausstrahlungsjahr, Staffelzahl) kommt aus `TmdbImdbResolver.SeriesInfoAsync` (`/tv/{id}`, gecacht). (4) **Der Trockenlauf legt nichts an und schreibt nichts** (`would-create`, keine POSTs). Der Endpunkt benutzt die Nummer aus dem Ticket (`UploadTicket.TvstShowId`) und holt die Serienliste dann **gar nicht**; nur ohne Nummer sucht er den Namen, mit demselben engen Vergleich. Geprüft von T223.

**F-M502 [D]:** **Die Sprachprüfung des tvst-Uploads läuft über eine tvst-eigene Brücke von ISO- auf die Kürzel der Seite — und nur dort.** Operator-Anweisung 15.11.2026: „Aber halt tvst spezifisch. Das andere mapping ist subdl spezifisch." Gemessen am 15.11.2026: `TvSubtitlesClient.Languages` ist **vollständig** — die 24 Kürzel der Seite, deckungsgleich mit dem echten Formular `/add.html` —, aber vier davon sind **kein ISO**: `gr` statt `el`/`ell`/`gre`, `cz` statt `cs`/`ces`/`cze`, `cn` statt `zh`/`zho`/`chi`, `jp` statt `ja`/`jpn`. Jellyfin liefert Sprachen als **Drei-Buchstaben-Codes** (`gre`, `cze`, `chi`, `jpn`), Konfiguration und Zielsprachen teils als **Zwei-Buchstaben-Codes** (`el`, `cs`, `zh`, `ja`); kam ein solcher Code an, fand die Prüfung von `BuildUpload` nichts und der Upload wurde mit „language must be one of the site's codes" abgelehnt. Jetzt gilt: (1) **`TvSubtitlesClient.SiteLanguage(code)`** ist die einzige Abbildungsstelle. Sie kennt die vier Abweichungen in jeder gängigen Schreibweise (`el`/`ell`/`gre` → `gr`, `cs`/`ces`/`cze` → `cz`, `zh`/`zho`/`chi` → `cn`, `ja`/`jpn` → `jp`) und die übrigen Seiten-Sprachen in ihrer ISO-639-2-Form (/B und /T): `eng`→`en`, `spa`→`es`, `fra`/`fre`→`fr`, `deu`/`ger`→`de`, `pob`→`br`, `por`→`pt`, `rus`→`ru`, `ukr`→`ua`, `ita`→`it`, `ara`→`ar`, `hun`→`hu`, `pol`→`pl`, `tur`→`tr`, `nld`/`dut`→`nl`, `swe`→`sv`, `dan`→`da`, `fin`→`fi`, `kor`→`ko`, `bul`→`bg`, `ron`/`rum`→`ro`. Die Kürzel der Seite selbst bleiben unverändert gültig. (2) **Groß-/Kleinschreibung zählt nicht**, und eine Angabe mit Region oder Skript fällt auf den Grundcode: `zh-Hans`/`zh_CN` → `cn`, `ja-JP` → `jp`, `es-419` → `es`. Die **eine** Ausnahme ist die Region, die die Sprache IST: `pt-BR` (auch `pt_BR`) → `br` (brasilianisches Portugiesisch), während `pt` und `pt-PT` europäisches Portugiesisch (`pt`) bleiben — die Seite führt beide getrennt, deshalb steht diese Regel **vor** der Grundcode-Reduktion. (3) **Unbekannt heißt `null`, nicht geraten:** was weder in der Brücke noch in der Tabelle steht, wird wie bisher abgelehnt, mit **einer** Zeile, die die Kürzel der Seite nennt (`language must be one of the site's codes (ISO codes are mapped): …`). (4) **Die Brücke gilt an jeder Sprachstelle des Clients:** die Prüfung von `BuildUpload` (und damit das Feld `lang`, das jetzt immer das **Seitenkürzel** trägt), die Sprachzuordnung `TvSubtitlesDownloadEndpoint.SiteLanguage` (die nur noch durchreicht) und die Rücklesung `FindUploadedIdAsync`, die den gesuchten Code und die Flaggen-Sprache des Eintrags durch dieselbe Brücke schickt — sonst fände sie einen Eintrag nicht, den der Upload gerade angelegt hat. **Der Dateiname des Uploads wird nicht umgeschrieben:** er behält seine Endung, und die drei Buchstaben für den Namen kommen weiter aus der Tabelle der Seite (`gre`, `cze`, `chi`, `jpn`, `pob`, `rum`). **Keine gemeinsame Tabelle:** SubDL bildet in `SubdlApiClient` ab und bleibt unangetastet (F-M499). Geprüft von T224.

**F-M505 [D]:** **Das Release-Feld des tvst-Uploads trägt die Release-GRUPPE, nicht den Dateinamen — und bleibt leer, wenn der Name keine Gruppe nennt.** Befund 15.11.2026 an der Seite: in der Release-Spalte von tvsubtitles.net steht die Gruppe. 193 Einträge der ersten Staffel von *The Middle* ausgelesen — 2HD (28×), REWARD (24×), FQM (14×), FEVER, POW4, CTU, LOL, DIMENSION, **91 Einträge leer**, und **kein einziger** mit Punkten. Unser Upload trug dagegen den Mediendateinamen: auf `episode-26403.html` stand wörtlich `The Middle.S01E21.MULTI.1080p.WEB-DL` — der Rest `.H265-FTMVHD` fehlte, weil die Seite den Wert serverseitig am Bindestrich abschneidet, und die Gruppe (FTMVHD) kam im Feld gar nicht vor. Regel: das Feld wird aus dem Release-Namen über `SeasonPack.ReleaseGroup` gefüllt (dieselbe Regel wie für den Download-Rang, F-M430); nennt der Name keine Gruppe (`null`), wird ein **leeres Feld** gesendet — das ist ein erlaubter Wert und die Mehrheit der Seite, also weder ein Grund, den Upload zu verweigern, noch eine Erfindung. Der Dateiname darf im tvst-Ticket nicht mehr auftreten. Geprüft von T227.

**F-M506 [D]:** **Die dritte Quelle ist ein Client für Filme allein — mit eigenen Tabellen, eigenem Auflöser, und ohne jede Verdrahtung.** Befund 15.11.2026, **ausschließlich lesend** aufgenommen (kein POST an `add.php`/`add-movie.php`, kein Upload, kein Film angelegt): moviesubtitles.org hat **keine API** (`/api.php`, `/sitemap.xml`, `/rss.xml`, `/login.php`, `/panel.php`, `/upload.php` → 302 auf `/`), alles läuft über HTML — `movies-<A..Z|0>.html` **oder** `search.php?q=` **oder** `add.php?filter=` → `movie-<id>.html` → `subtitle-<id>.html` → `download-<id>.html` → **302 mit RELATIVEM `Location`** → `files/<…>.zip`. Drei Messungen verbieten jede Wiederverwendung der tvsubtitles-Tabellen: die Sprachliste hat **13** statt 24 Kürzel (kein `pt`/europäisches Portugiesisch, kein `cz`, `cn`, `jp`, `nl`, `sv`, `da`, `fi`, `ko`, `bg`, `ro`) und die Dateiendung ist das **Zwei-Buchstaben-Kürzel der Seite** (`Oppenheimer_2023.WEB-DL.MA.en.zip`), nicht ISO3; das Rip-Feld kennt **acht benannte Werte** (`DVDRip BluRay BrRip BDRip HDRip WEB WEBRip WEB-DL`) plus den leeren Wert, den das Auswahlfeld **zweimal** führt (erster und letzter Eintrag) — also neun verschiedene Werte —, während `HDTV`, `HEVC` und `DVDSCR` dort fehlen und `WEB-DL`, `WEBRip`, `BrRip`, `HDRip` nur hier existieren; `search.php` antwortet **HTTP 500 mit gültiger Trefferliste im Rumpf**; Titel stehen **invertiert** (`Matrix, The`); eine unbekannte Id sieht wie Erfolg aus (`subtitle-…` HTTP 200 mit leerem Titel und `size: 9140 kb`, `download-…` 302 auf `files/`). Regel: (1) **Zwei eigene Tabellen je Quelle** — `MovieSubtitlesClient.Languages` (13 Werte mit Anzeigename, `portugese(br)` in der Schreibweise der Seite, und dem Dateikürzel) und `MovieSubtitlesClient.Rips` (die neun Werte in Formularreihenfolge); keine gemeinsame Tabelle mit tvst und keine mit SubDL. (2) **Die Sprachbrücke kennt nur, was die 13 hergeben:** `eng spa fra/fre deu/ger pob rus uk/ukr ita el/ell/gre ara hun pol tur`, Region nur dort, wo sie die Sprache IST (`pt-BR`/`pt_BR` → `br`); `pt`/`por` (europäisches Portugiesisch), `nl`/`dut`, `sv`, `da`, `fi`, `ko`, `zh`/`zho`/`chi`, `ja`/`jpn`, `bg`, `cs`/`ces`/`cze`, `ro`/`rum` und alles Unbekannte liefern **`null`** — **kein Rückfall auf Englisch**, keine Vermutung. (3) **Rip: Quelle vor Codec, und KEIN Codec-Rückfall.** `WEB-DL` bleibt `WEB-DL` (nicht `WEB`), `BluRay.x265` wird `BluRay` — aber `x265`, `HEVC` oder `H.265` **allein** ergeben den **leeren** Wert, nicht `HEVC`: tvst zieht einen Codec auf `HEVC`, weil sein Auswahlfeld `HEVC` führt; dieses führt keinen Codec, und ein Codec als Rip wäre eine Behauptung über die Quelle, die die Seite nicht macht. Aus demselben Grund bleibt `HDTV` leer. (4) **Auflösung eng und ohne Raten:** der Vergleichsschlüssel (`TitleKey`) hebt die Artikel-Inversion auf (`Matrix, The` = `The Matrix`) und wendet sonst genau die drei Angleichungen aus `TvSubtitlesClient.NormalizeShowName` an (Groß-/Kleinschreibung, `&`/`and`/`und` als Wort, Leerraum); das **Jahr muss stimmen** (ein falsches Jahr ist kein Treffer, auch nicht „ungefähr"), und bleiben **mehr als ein** Kandidat, wird **nichts** geliefert — die Seite hat keine Jahressuche und keine Wunsch-Id (`filter=dune` → fünf Filme). (5) **Alle Parser sind `static`** und nehmen das HTML als Zeichenkette; die Sprache eines Eintrags kommt aus dem **Flaggenbild seines eigenen Blocks**, und freie Rip-Werte alter Einträge (`1080p`, `720p.BlueRay`) bleiben stehen — die neun Werte sind eine **Schreib**liste. (6) **Download:** die 302 wird ausgewertet (Ziel **relativ**, Klammern wie `ru(2).zip` bleiben Teil des Namens), an den **Bytes** entschieden, ob ein Zip (`PK\x03\x04`) oder eine nackte `.srt` kam, und aus dem Zip die Datei mit dem Sprachkürzel genommen, sonst die erste; `parts`>1 wird im Ergebnis **gekennzeichnet** (`Members`, `IsMultiPart`) statt stillschweigend zusammengeklebt — das ist ein geschnittener Film, kein Pack. Ein Ziel `files/` ist die **Fehlersignatur** und wird verweigert. (7) **Upload:** die Felder wörtlich wie das Formular (`tvid lang rip release author UFile UFile2 UFile3 comments go`), `go=1`; **`author` fehlt ganz**, wenn er leer ist (ein fehlender Teil überschreibt keine Vorbelegung); **`release` trägt die Release-GRUPPE** über `SeasonPack.ReleaseGroup` (dieselbe Regel wie F-M505), **nie den Dateinamen**, und bleibt leer, wenn der Name keine Gruppe nennt; `comments` trägt die Kennungen und den Client (TMDb/IMDb + Assembly-Version); `UFile2`/`UFile3` gehen nur mit, wenn sie gefüllt sind. Geprüft wird **vor** dem Bauen: `tvid` > 0, Sprache in der Tabelle (ISO wird abgebildet), Rip in der Tabelle **oder leer**, mindestens eine Id, Datei nicht leer, Endung `.srt`/`.zip` — sonst `ArgumentException` mit **einer** Zeile und **kein Netzaufruf**; `dryRun` kommt vom Aufrufer und wird nie aus der Konfiguration gelesen. (8) **Nichts davon ist verdrahtet:** kein Konfigurationsfeld, keine Store-Spalte, kein Endpunkt, kein Eintrag im Quellenkatalog — das ist Etappe 2, und der Client steht allein, damit das Protokoll gegen die aufgenommenen Seiten bewiesen werden kann, bevor etwas davon abhängt. Geprüft von T228.

**F-M507 [D]:** **Die dritte Quelle ist ein Anbieter — und sie ist NUR für Filme zuständig; die Medienart entscheidet TMDb.** Nutzerauftrag 15.11.2026: „Dritte quelle. Nur das series halt nach tv show und movies nach moviesubtitels gehen. Als upload und download", dazu „Was was ist, Auflösung per tmdb" und „Subdl differenziert nicht -> beides". Etappe 2a verdrahtet davon den **Download**; Upload-Weg, GUI-Felder und Doku-Kapitel der Quelle sind Etappe 2b. Regel: (1) **Die Zuordnung steht an EINER Stelle** (`SourceOrder.AppliesTo(provider, isSeries)`): SubDL unterscheidet nicht und bleibt für **beides** zuständig; tvsubtitles.net führt Serien (`isSeries`), moviesubtitles.org führt Filme (`!isSeries`) — einander ausschließend. Die Medienart kommt aus der **TMDb-Auflösung** (`isSeries` nach `VerifyIdsAgainstTmdbAsync`/`ResolveByMultiSearchAsync`), **nie** aus dem Dateinamen und nie aus dem Bibliotheksnamen. Bei einem Medium ist damit immer nur EINE der beiden Seiten anwendbar; die Reihenfolge-Einstellung (`DownloadOrder`) entscheidet deshalb faktisch nur zwischen SubDL und der zuständigen Seite, und `DownloadSourceOrder` braucht keinen dritten Wert. (2) **Die zwei Schalter der Quelle** (`MovieSubtitlesDownloadEnabled`, `MovieSubtitlesUploadEnabled`, beide `bool?`) fallen wie bei tvst gegenläufig aus (F-M503): `null` → Download **an**, Upload **aus**; ein ausdrückliches `false` bleibt aus, ein ausdrückliches `true` gilt. Der Alt-Schalter (`DownloadEnabled`) antwortet für diese Quelle **nicht** (F-M477). `HasProvider` ist für sie wahr, `IsConfiguredForDownload`/`IsConfiguredForUpload` auch — die Seite nimmt keinen Zugang. (3) **Die Herkunft kennt sie** (`Provenance.Name`/`Parse` = `moviesubtitles`), und die eine Regel bleibt: eine **bekannte** Herkunft wird bei **allen** Zielen nicht hochgeladen (F-M487). (4) **Store, Schema 14:** `sidecars` und `embeds` bekommen `MoviesubtitlesDownloadId`, `MoviesubtitlesUploadId`, `MoviesubtitlesUploadedAt`; das Medium bekommt `MsubMovieId` (`string?`) auf derselben Ebene wie `TvstShowId`/`TmdbId`. Nachgezogen beim Start (`EnsureReportColumns` für die beiden Untertitel-Tabellen, `EnsureMediaColumns` als Liste für das Medium), jedes `ALTER` an `PRAGMA table_info` gebunden, ein Fehlschlag protokolliert und geschluckt; Bestandszeilen bleiben unangetastet und lesen die neuen Felder als `null`. Der Upload-Zeitstempel und die Upload-Id werden erst in Etappe 2b geschrieben. (5) **Der Download-Pfad** fragt je Medium nur die zuständigen Quellen (`DownloadQueryOrder(isSeries)`), jede anwendbare Quelle **genau einmal je Sprache**, ohne vorzeitigen Ausstieg; die Tore (`SourceAllowed`, verdrahteter Endpunkt) stehen vor der Abfrage. Für die Filmseite führt die Auflösung über die **TMDb**-Angaben: `TmdbImdbResolver.MovieInfoAsync` liefert Titel und Jahr, `MsubMovieResolver` liest `MsubMovieId` aus dem Medium oder sucht sie (Titel **und** Jahr, eng verglichen, invertierte Schreibweise, bei Mehrdeutigkeit nichts) und schreibt sie fest; `MovieSubtitlesDownloadEndpoint` holt die EINE Filmseite (15 Minuten gecacht) und filtert die Einträge über die Sprache **ihrer Flagge**. Filme ohne Jahr, ohne TMDb-Datensatz oder ohne eindeutigen Treffer bekommen **eine Logzeile und keinen Fehler** — die anderen Quellen laufen weiter. Die Rangfolge der Treffer bleibt unverändert (Frage-Reihenfolge → Pack → Textscore); der Signifikanzbegriff (F-M491) ist nicht berührt. (6) **Der Upload-Pfad kennt die Quelle nicht** (Operator-Auflage 15.11.2026: „Bevor wir falsche Sprachen hochladen, laden wir nix hoch."): `SubtitleEndpointCatalog.HasUploadPath` ist für sie in Etappe 2a falsch, und die Kettenreihenfolge wird darauf geschnitten — sie steht dort **nicht einmal als toter Eintrag**, weil die Kette für „alles ausser SubDL" genau einen Zweig hat (tvst) und ein toter Eintrag dessen Adresse bekäme. **Weitergeführt von F-M508:** der Upload-Weg existiert seit Etappe 2b, `HasUploadPath` ist für sie wahr, und die Kette hat keinen „alles-andere"-Zweig mehr; gesperrt bleibt er über den Schalter (Vorgabe: aus). Geprüft von T229/T230.


**F-M508 [D]:** **Der Upload-Weg der Film-Adresse ist gebaut, verdrahtet und GESPERRT — freigegeben wird er erst nach belegter Sprachabbildung.** Operator-Auflagen 15.11.2026: „Nur das series halt nach tv show und movies nach moviesubtitels gehen. Als upload und download" und „Bevor wir falsche Sprachen hochladen, laden wir nix hoch." Etappe 2b baut den Weg und schaltet ihn **nicht** scharf. Regel: (1) **Ein Endpunkt, ein Ticket, ein Film** (`MovieSubtitlesUploadEndpoint`, `IUploadEndpoint`, `Provider = MovieSubtitles`, `Wired = true`): das Ticket trägt Titel, Jahr, `MsubMovieId` und den Release-Namen; Staffel oder Folge im Ticket werden **abgelehnt** („a film has no season and no episode"), ein Staffelpack ebenso — ein Film hat beides nicht. (2) **Die Nummer führt aus dem Store:** `MsubMovieId` wird benutzt, wenn sie gesetzt ist (dann fragt der Endpunkt die Suche der Seite gar nicht); fehlt sie, löst er über Titel **und** Jahr auf, und ohne Jahr wird nichts geraten. (3) **Die Nutzlast kommt aus `MovieSubtitlesClient.BuildUpload`**, unverändert: `tvid lang rip release`, dann `author` **nur wenn gesetzt** (das Plugin führt keinen Autor; ein fehlender Teil überschreibt keine Vorbelegung), dann `UFile`/`UFile2`/`UFile3`, zuletzt `comments` und `go`. `release` ist die Release-**Gruppe** aus dem Release-Namen, nie der Dateiname (F-M505). (4) **Keine erfundene Id, keine erfundene Moderation:** was die Seite bei Annahme sagt, ist **nicht belegt**; der Endpunkt reicht den Text der Antwort durch, meldet Erfolg nur bei HTTP 2xx und gibt `Id = null` zurück („angenommen, Id unbekannt"). Die Kennung vergibt die Kette als `own:moviesubtitles:<Zeit>:<hex>` (F-M488). Ein Nicht-2xx ist eine Ablehnung, die den Status nennt und den Text der Seite mitführt. (5) **Die Kette ist eine Schleife über die zuständigen Quellen** (`UploadSourceOrder(isSeries)` → `SourceOrder.ForMedia`), keine Fallunterscheidung „SubDL oder alles andere" mehr: bei einer Serie stehen SubDL und tvsubtitles.net darin, bei einem Film SubDL und moviesubtitles.org. Je Quelle: eigener Schalter (`SourceAllowed`), eigene Konfiguration, eigenes Tor (`UploadGate.May`), eigener Versand (`SendToSourceAsync`), **eigenes Ergebnis** in einer Ablage je Quelle, **eigene Marke** und **eigener Zeitstempel** (`MarkOf`), und ein Fehler bei einer Quelle beendet die Kette nicht. `UploadGate.Decide` kennt die dritte Adresse (`MovieSubtitles`, `AllowedCount`), `IsUploadedTo`/`IsContentUploadedFor` lesen ihre Spalten, `RecordSkippedContent` schreibt ihren Zeitstempel, die markenlose Zeile zählt auch ihre Marke. (6) **Die Sperre ist hart:** `MovieSubtitlesUploadEnabled` ist `bool?` und im ungesetzten Fall **aus** (F-M503-Regel); dass eine Quelle deshalb nicht sendet, steht als Zeile im Lauf („skipped: that source is switched off, nothing sent", im Trockenlauf „DRY-RUN … not sent"). **In dieser Etappe wird der Endpunkt ausschließlich gegen Fixtures geprüft — kein POST gegen die Seite, auch nicht als Test.** Geprüft von T230.

**F-M509 [D]:** **Zwei Quellen, nicht drei: SubDL und die tvst-Familie — und die Familie löst nach Medienart auf ihre Adresse auf.** Operator-Klarstellung 15.11.2026: „Seperate into subdl and tvst. Tvst is splitted into tvst and moviesub itself." Regel: (1) **Auf oberster Ebene gibt es zwei Quellen** — SubDL und die tvst-Familie. Die Familie ist **in sich aufgeteilt** in tvsubtitles.net (Serien) und moviesubtitles.org (Filme); welche der beiden gefragt wird, entscheidet **allein** die Medienart aus TMDb (`SourceOrder.FamilyPage(isSeries)`). (2) **Die Reihenfolge gilt auf Familien-Ebene**, nicht auf Seiten-Ebene: `DownloadSourceOrder` bleibt **zweiwertig** (SubDL zuerst / tvst zuerst) — es gibt **keinen dritten Wert**. „tvst zuerst" heißt bei einer Serie tvsubtitles.net zuerst, bei einem Film moviesubtitles.org zuerst, danach SubDL; „SubDL zuerst" ist die Umkehrung. Damit wirkt die Einstellung in **beiden** Medienarten; die Lücke aus F-M507 („tvsubtitles zuerst" hatte bei Filmen keine Wirkung, weil SubDL immer führte) ist geschlossen. (3) **`SourceOrder.ForMedia(order, isSeries)`** liefert in jeder Medienart genau **zwei** Einträge: die Adresse der Familie für diese Art und SubDL, in der eingestellten Reihenfolge; `FamilyFirst(order)` und `InFamily(provider)` sagen dasselbe eine Ebene tiefer. (4) **Der Upload folgt derselben Reihenfolge** (es gibt weiterhin keine eigene Upload-Einstellung, F-M495), und auch dort steht in jeder Medienart die Adresse der Familie, die diese Art führt. (5) **Die GUI zeigt die Familie als eine Quelle mit zwei Adressen:** eine gemeinsame Überschrift über den beiden Schalterpaaren (Download/Upload je Seite), danach das zweiwertige Auswahlfeld der Reihenfolge. Es ist **eine Quelle mit zwei Adressen**, nicht drei Quellen. Geprüft von T230.


**F-M510 [D]:** **Der erste Film-Lauf auf einer Filmbibliothek sagt, WELCHE Adresse gefragt wurde, WELCHE Quelle den Gewinner stellt — und der Titel-Vergleich gegen TMDb trennt ein Klammer-Jahr ab.** Gemessen 15.11.2026, 22:12 auf jf-test (erste Film-Bibliothek `Movies_Test`, `Oppenheimer (2023)`, TMDb 872585, IMDb tt15398776, `DownloadLanguages` EN, Trockenlauf, `DownloadOrder` tvst-zuerst):

```
[V] [tvsubtitles-D] Oppenheimer (2023).mkv [EN] — 2 moviesubtitles candidate(s), 9 subdl candidate(s), order moviesubtitles, subdl
[V] [SubDL-D] DRY-RUN Oppenheimer (2023).mkv [EN] → MA (score 0, hi=no)
[V] [SubDL-D] [TMDB] strict title+year test found no hit for "Oppenheimer (2023)" (2023) — not confirming
[V] [SubDL-D] Oppenheimer (2023).mkv — 1 language(s) closed by the QA budget this run; nothing left to search
```

Drei Befunde, drei Regeln: (1) **Das Etikett der Sammelzeile ist die Adresse, die gefragt wurde, kein fester Text.** Die Zeile zählte richtig (`moviesubtitles` stand in der Zählung), ihr Etikett behauptete aber die **Serien**-Seite der Familie — der Text stand fest im Code. Es kommt jetzt aus `SourceOrder.FamilyPage(isSeries)`, der einen Stelle, an der die Medienart eine Seite auswählt: Film → `[moviesubtitles-D]`, Serie → `[tvsubtitles-D]`. Die **Aussage** der Zeile bleibt unverändert — Kandidaten **je gefragter Quelle** und die Frage-Reihenfolge; die Zählung läuft weiter über die Liste der Quellen, nicht über einen festen Namen. Dasselbe gilt für die geteilten Zeilen beider Richtungen: der Upload-Trockenlauf und die Zählung lesen `Provenance.Name(quelle)`; ein festes Etikett bleibt nur dort erlaubt, wo es im **eigenen** Endpunkt-Zweig steht (die Film-Adresse nennt sich in ihrem Film-Zweig `[moviesubtitles]`). (2) **Der Trockenlauf nennt die Herkunft des Gewinners.** Die Zeile lautet jetzt `DRY-RUN <Datei> [<Sprache>] → <Release> (<Quelle>, score <n>, hi=<ja|nein>)` — bei zwei gefragten Quellen ist „wer hat gewonnen" genau die Frage, die ein Trockenlauf beantworten soll. Die Quelle kommt aus dem **Kandidaten** (`cand.Provider`), nicht aus der Frage-Reihenfolge: nur der Kandidat weiß, woher er stammt; `score` und `hi` behalten Platz und Reihenfolge, die Zeilenform der übrigen Suiten ist unberührt. (3) **Ein abschließendes Klammer-Jahr gehört vor den TMDb-Titel-Vergleich abgetrennt — nur dort, und nur ein abschließendes.** `Oppenheimer (2023)` + Jahr 2023 fand nichts, weil **sowohl** die Suchanfrage als auch der Jahr-Treffer den Namen mit der Jahresangabe trugen; TMDb führt den Film unter `Oppenheimer` (`release_date` 2023-07-19). `TmdbImdbResolver.ComparisonTitle` entfernt **ein** abschließendes `(Jahr)`, `(Jahr–Jahr)` oder `[Jahr]` (auch mit `-`/`–`/`—` als Spanne) und wird an **beiden** Titel-Suchen benutzt (typneutraler `search/multi` und typisierte `search/movie|tv`), samt Cache-Schlüssel, damit die zweite Frage nicht erneut kostet; ein Abtrennen wird im Trockenlauf-Trace als `title for the compare: "<roh>" → "<rein>"` protokolliert. **Nur für den Vergleich:** der Anzeigename des Items, der Dateiname, der Sidecar-Name, die Pack-Bildung und `MatchesRelease` lesen den rohen Namen unverändert — es wird nichts umbenannt. **(Ghost) Riders In the Sky (American Outlaws_ Live …)** bleibt deshalb, wie es ist, ebenso **Blade Runner 2049** (Jahr im Titel, ohne Klammer) und die Verdopplung `O Beautiful (2002) (2002)` (nur EIN abschließendes Jahr geht). (4) **Befund ohne Änderung — „closed by the QA budget" ist im Trockenlauf falsch etikettiert** (Entscheidung offen): `closedLangs` hat genau zwei Quellen, „not available" und den Zweig `if (savedAny)`; der Trockenlauf setzt `savedAny = true` (F-M22, „would have saved") und schließt damit dieselbe Sprache, ohne dass **irgendein** QA-Tor befragt wurde. Beleg: derselbe Lauf meldet „1 would have saved … 0 lang not available", und der Store enthält für dieses Medium **keine** `rejected_candidates`-Zeile (die drei vorhandenen gehören zu einer anderen Folge). Es ist also **kein** verbrauchtes Budget und kein Budget der zweiten Quelle — es ist die would-save-Marke. Der Kommentar an der Logzeile hält das fest; der Text selbst ist **nicht** geändert. Geprüft von T231/T232.

**F-M511 [D]:** **Die Qualität aus dem Release-Namen wird für den Film-Upload auf einen Rip-Wert der Filmseite gezogen — einmal im Endpunkt, vor dem Formular.** Befund 15.11.2026 auf jf-test (erster Film-Upload nach F-M508): die Kette reichte die Qualität **roh** ins Ticket (gemessen `"web"`), `Api/Endpoints/MovieSubtitlesEndpoints.cs` gab `ticket.Rip` roh an `MovieSubtitlesClient.BuildUpload`, und der Client akzeptiert nur die neun Werte seines Auswahlfelds — Antwort `ArgumentException` mit dem Wortlaut „rip must be empty or one of: DVDRip, BluRay, BrRip, BDRip, HDRip, WEB, WEBRip, WEB-DL (Parameter 'upload')", daraus `Ok=false`. Nur die **leere** Angabe kam durch: also scheiterte **jeder** Film-Upload. Regel: (1) **Die Abbildung ist Pflicht auf dem Weg ins Formular** — `MovieSubtitlesClient.RipFromQuality(ticket.Rip)`; die Tabelle ist die der Seite (F-M506): `web`→`WEB`, `1080p BluRay x264`→`BluRay`, `WEB-DL`→`WEB-DL`, `HDTV`→leer (kennt diese Seite nicht), `x265`→leer (kein Codec-Rückfall), leer→leer. (2) **Sie sitzt im ENDPUNKT, nicht an den Aufrufstellen:** der Endpunkt ist die eine Naht jedes Film-Uploads (Kette wie direkter Aufruf); die Kette reicht weiter die rohe Qualität herein — so, wie sie es für die Serien-Adresse tut, wo `TvSubtitlesClient.RipFromQuality` die Abbildung macht (F-M498). (3) **Trockenlauf und Ernst sind derselbe Rumpf:** die Nutzlast wird **einmal vor** der Trockenlauf-Weiche gebaut, deshalb zeigt der Trockenlauf byte-gleich, was der Ernstfall sendet. (4) **Die Tabellen bleiben getrennt** (F-M506): die Filmseite behält `WEB-DL`, wo die Serien-Seite `WEB` schreibt — die fremde Zuordnung zu borgen hiesse, einen Wert zu senden, den das Auswahlfeld dieser Seite anders meint. Geprüft von T233.

**F-M512 [D]:** **Der Einreiher bestimmt die Medienart aus dem STORE (TMDb-korrigiert) — Jellyfins Typ ist nur der Rückfall.** Nutzerentscheidung 15.11.2026 (wörtlich): „Ja den typ augrund tmdb bestimmen". Befund: `SubdlSeeder.IsUploadTodo` las `item is Episode ? true : item is Movie ? false : null`, also **Jellyfins** Typ; die Pipelines korrigieren die Art aber aus dem Dateinamen und aus TMDb (`UploadPipeline`: Dateiname, dann `verified.IsSeries`) und schreiben sie über `ContentHashRegistry.PatchMediaMetadata` in die Medienzeile — der Einreiher sah die Korrektur **nie**. Folge bei Abweichung Jellyfin↔TMDb: Jellyfin sagt Film, TMDb sagt Serie → die Queue erwog nur die **Filmadresse**, eine offene tvst-Quelle bekam das Item **nie**; umgekehrt wurde eine nie erfüllbare Adresse in **jedem** Scan neu eingereiht (No-op-Läufe). Regel: (1) **Erste Quelle ist der Store:** `ContentHashRegistry.MediaKindIsSeries(mediaHash)` antwortet `true` (Serie) bei `media.IsSeries`, `false` (Film) nur für eine **aufgelöste** Zeile (sie trägt die IMDb-/TMDb-Kennung derselben Patch) und `null`, wenn die Zeile nichts belegt — `false` allein ist kein Beleg, weil jede beliebige Zeile (`EnsureMedia`, `MarkSearched`) auf dem Vorgabewert steht und eine Serie damit fälschlich zum Film würde. Begründung im Code: die Medienzeile ist der **einzige** Ort, an dem ein Lauf die TMDb-korrigierte Art hinterlegt. (2) **Der Zugriff bleibt billig:** EINE Abfrage über den Primärschlüssel je Item, kein Netzaufruf, kein TMDb-Request im Scan; der Medien-Hash kommt aus demselben OSHash-Cache, den der Einreiher ohnehin fragt. (3) **Sonst Jellyfins Typ, sonst beide Adressen:** ohne Store-Auskunft entscheidet der Item-Typ (Episode/Film); ist auch der unbekannt, bleibt `null`, und **beide** Adressen der Familie bleiben stehen — „lieber ein Item zu viel" (F-M499/F-M509), keine Verengung, die ein Item verlöre. (4) **Die Auswahl selbst bleibt bei `SourceOrder.AppliesTo`** (F-M507/F-M509): besser belegt wird die Medienart, nicht die Regel. Geprüft von T234.


**F-M513 [D]:** **Ein fehlender Film wird beim Upload angelegt — mit einem engen Doppeleintrag-Schutz, und die neue Nummer wird nachgesehen statt geraten.** Nutzerauftrag 16.11.2026 (wörtlich): „Dann einmal mit neu anlegen testen" — ein Film, den moviesubtitles.org nicht führt, soll beim Upload angelegt und der Untertitel dazu hochgeladen werden. Befund: `MovieSubtitlesClient.BuildAddMovie`/`AddMovieAsync` waren bis dahin **nicht gebaut** („Filme werden erst angelegt, wenn die Auflösung sauber antwortet und die Sprachabbildung belegt ist"); beides ist jetzt belegt (Live-Beleg: Film-Download 5819, Film-Upload 23716/Eintrag 133347). Regel: (1) **Nutzlast:** `BuildAddMovie(name, year)` baut `name`, `year`, `go=1` in der Reihenfolge des Formulars und prüft VOR dem Senden — leerer Name und ein Jahr außerhalb 1888..laufendes Jahr+3 sind ein `ArgumentException` ohne Anfrage; ein einbuchstabiger Titel ist erlaubt. `BuildAddMovieBody` macht daraus den multipart-Rumpf samt Boundary, damit Trockenlauf und Ernstfall **byte-gleich** sind. (2) **Die Antwort der Seite ist unbelegt** (kein POST wurde je gemacht): `AddMovieAsync` deutet sie nicht. Bei 2xx wird **nachgesehen** — dieselbe enge Suche wie vorher (`CheckForAddAsync`, Titel + Jahr, alle Schreibweisen) — und die Nummer aus der Trefferliste genommen; nennt die Nachsuche nichts, lautet das Ergebnis `created-id-unknown` („angelegt, Id nicht lesbar") **mit dem Text der Antwort** und ohne Id. Ein Nicht-2xx ist `http-error` mit `Ok=false` und dem Status als Grund; eine Ablehnung erfindet nichts und sucht auch nicht nach. (3) **Der Doppeleintrag-Schutz ist eng und entscheidet in EINER Suchrunde** (`CheckForAddAsync` → `ResolveForAdd`, `TitleMatches`/`TitleKey`): *gleiches Jahr und genau eine Zeile* → diese Nummer nehmen, nichts anlegen (`take`); *Titel existiert mit anderem (oder unbekanntem) Jahr oder in mehr als einer Zeile* → **nicht** anlegen (`refuse`, das gefundene Jahr und die Trefferzahl stehen im Ergebnis, damit die Logzeile sie nennen kann — der Nutzer will keine Doppel); *kein Titeltreffer, aber mindestens eine lesbare Antwort* → anlegen (`create`); *keine lesbare Antwort* → `unclear`, ebenfalls **nicht** anlegen („gibt es nicht" ist unbelegt). Die Entscheidung steht im Ergebnisobjekt (`MovieSubtitlesAddCheck`), nicht nur im Log. (4) **Die Anlage sitzt im Upload-Pfad, nirgends sonst:** `MsubMovieResolver.ResolveOrCreateForMediaAsync` ist die einzige Fassung, die anlegen darf; `ResolveForMediaAsync` (Download-Pfad) bleibt unverändert und legt nie an. Aufgerufen wird sie nur aus `UploadPipeline.SendToMovieSubtitlesAsync`, also nur, wenn der Upload-Schalter der Film-Adresse an ist (Gate/`SourceAllowed`). Die Zeile lautet fest `[moviesubtitles] '<Titel>' (<Jahr>) neu angelegt — Id <n>`; die neue `tvid` geht **sofort** über dieselbe `Persist`-Methode wie die Auflösung in `MediaEntity.MsubMovieId`. Der Trockenlauf baut dieselbe Nutzlast, meldet ihre Größe (`would-create`, `AddMovieBytes`) und sendet nichts. (5) **Der Endpunkt bleibt Transport und legt nichts an** — fehlt ihm die Nummer, sucht er sie und verweigert mit 'film not resolvable on moviesubtitles.org by title and year'; ein zweiter Anlage-Ort wäre ein zweiter Weg zu einer Nummer, die niemand geprüft hat. `ReportBrokenSubtitles` ist unberührt. Geprüft von T235.

**F-M515 [D]:** **Die Antwort auf `add-movie.php` wird an zwei live belegten Markern gelesen — „angelegt" sagt nur, wer den Satz der Seite gelesen hat, und eine Id kommt nie aus der Antwort.** Live gemessen am 16.11.2026 gegen moviesubtitles.org (Ergänzung zu F-M513, dessen Punkt (2) „die Antwort ist unbelegt" damit überholt ist): (a) **Das Formular.** `GET /add-movie.php` antwortet 200 mit **8399 Bytes**, `<form action="/add-movie.php" method="post" enctype="multipart/form-data">` und genau den Feldern `name` (text), `year` (text, `maxlength=4`), `go` (hidden, `1`), Submit `Send`. (b) **Der falsche Weg.** Ein POST mit `application/x-www-form-urlencoded` und `go=Add movie` bekommt 200 und **dasselbe Formular** zurück — keine Meldung, kein Eintrag; das ist **keine** Anlage, sah für das Plugin aber wie eine aus. (c) **Der Erfolg.** Ein POST als multipart mit `go=1`, `name=The Little Rascals`, `year=1994` antwortet **200, 8467 Bytes**, im Rumpf `<div class="subtitle2"><b>Movie has been added. Thank you.</b></div>` — **kein `Location`, keine Film-Id, kein Link**. (d) **Der Doppelfall.** Derselbe POST ein zweites Mal antwortet **200, 8459 Bytes** mit `<div class="subtitle2"><b>This Movie already exist</b></div>` (die Seite schreibt „exist"); derselbe Satz kam für `Project Hail Mary` (2026), obwohl Suche, `movies-P.html` und `add.php?filter=` den Film **nicht** führen — der Satz belegt also nur die Verweigerung, nicht die Auffindbarkeit. (e) **Nach der Anlage.** Die Suche (`POST search.php`, `q=Little Rascals`) fand **0** Treffer, `movies-L.html` blieb byte-gleich (478353 Bytes, kein „Rascal"), `add.php?filter=Rascals` lieferte **0** Zeilen, die Statistik blieb bei `Movies: 23368`; die neueste Filmseite `movie-26230.html` trägt das Jahr `(1994)` mit **leerem Namen**, `movie-26231.html` und folgende existieren nicht — die Id ist damit **nicht titelbelegt**, und es wird keine behauptet. Regel: (1) **Der Request ist multipart/form-data mit `go=1`** (`BuildAddMovieBody`), byte-gleich im Trockenlauf. (2) **Klassifiziert wird an genau zwei Markern** (`ClassifyAddMovieAnswer`): `Movie has been added` → angelegt, `This Movie already exist` → verweigert; **alles andere** — das unveränderte Formular eingeschlossen — ist `rejected` mit `Ok=false` und dem Textauszug als Grund. Es gibt keinen dritten belegten Satz, also wird auch keiner erfunden. (3) **`Ok=true` nur mit dem Erfolgs-Marker**; ein 2xx allein ist keine Anlage mehr. (4) **„existiert schon" führt zu genau EINER Nachsuche** (`CheckForAddAsync`, Titel + Jahr, alle Schreibweisen), **nie zu einem zweiten POST und nie zu einem Fehler**: nennt die Suche den Film, ist seine Nummer das Ergebnis (`exists`), nennt sie ihn nicht, lautet das Ergebnis `exists-id-not-readable` — kein `tvid`, kein Upload. (5) **Die Id kommt immer aus der Nachsuche**, nie aus der Antwort (die nennt keine): `created` bzw. `created-id-unknown`. Im Auflöser sind die Zweige `MsubMovieStatus.ExistsFound` (Nummer gespeichert, nichts angelegt) und `ExistsWithoutId` (nichts gespeichert, kein Upload) sichtbar, und die Logzeilen nennen den Satz der Seite im Wortlaut. (6) **Der Befund bleibt offen gemeldet:** dass ein angelegter Film über Suche/Buchstabenliste/filter nicht auffindbar ist, ist eine Eigenschaft der Seite, keine des Clients — der Client sagt deshalb „angelegt, Id nicht lesbar" und lädt nichts hoch. Geprüft von T237.

**F-M516 [D]:** **Eine fehlende Serie wird mit dem Formular der Seite angelegt, die Antwort wird an EINEM belegten Kennzeichen gelesen, und die Seriennummer kommt aus der Suche der Seite — nicht aus der Antwort und nicht aus „höchste Id + 1".** Auftrag 16.11.2026: die Anlage einer fehlenden Serie im Plugin zum Laufen bringen, „mit belegter Antwort-Auswertung und ohne geratene Nummer". Ausgangslage: der Weg war gebaut (`TvstShowResolver.cs` → `TvSubtitlesClient.BuildAddShow`/`AddShowAsync`), aber **nie gelaufen** — kein Anlage-Eintrag im Log der Testinstanz oder in Prod, und die drei bekannten Seriennummern (4238 *Babylon Berlin*, 4239 *The Thundermans*, 4240 *Cunk on Earth*) stammten aus einer Suche, nicht aus einer Anlage. Befund, ausschließlich lesend plus **genau eine** echte Anlage: (a) **Das Formular** (`GET /add-tvshow.php`, 13767 Bytes) lautet `<form action="/add-tvshow1.php" method="post" enctype="multipart/form-data">` und steht — wie das Upload-Formular — in einer kaputten Verschachtelung (`<table><form …>`); es hat **genau vier Felder**: `name` (Text), `year_s` (Text, `maxlength=4`), `seasons` (Text, `maxlength=2`), `go` (versteckt, `1`, hinter der Absende-Zeile, noch im Formular). Der Absende-Knopf `Send` trägt **keinen `name`** und geht deshalb nicht mit. Kein Captcha, kein Token, kein Konto, kein Kommentarfeld, keine externe Kennung — der Name ist der einzige Schlüssel. (b) **Der Rumpf unseres Clients war in der Form bereits richtig** (multipart, vier Felder in Formularreihenfolge, Referer `/add-tvshow.php`), aber **ungeprüft**: `AddShowAsync` gab nur `Status`/`Text` zurück, der Auflöser verlangte `Status is 200..299` und hätte die echte Antwort als Fehler gelesen. (c) **Die Antwort, gemessen an einer echten Anlage** (`Weissensee`, 2010, 4 Staffeln; Abwesenheit vorher durch Suche **und** durch die vollständige Serienliste belegt — 0 Treffer bei 2.772 Einträgen, keine Schreibvariante): `POST /add-tvshow1.php` → **HTTP 302**, Rumpf leer, `Location: https://www.tvsubtitles.net/add-tvshow.php?done=yes`; diese Seite druckt `<div class="subtitle2"><b>TV-Show has been added. Thank you.</b></div>`. **Keine Id, in keiner Form.** Der Zähler „TV Shows" der Seitenleiste blieb bei **2768** (vor und nach der Anlage) — eine Anlage am Zähler abzulesen ist kein Beleg. **„Gibt es schon" und eine Ablehnung sind NICHT belegt:** ein zweiter POST hätte entweder einen Doppeleintrag erzeugt (eine Anlage ist auf der Seite nicht löschbar) oder nichts bewiesen. (d) **Die Id** stand unmittelbar danach in der Seitensuche (`GET /search2.php?qs=Weissensee` → `<a href="/tvshow-4242.html">Weissensee (2010-2026)</a>`) und im Auswahlfeld von `/add.html` (`4242` → `Weissensee`); `/tvshows.html` führte sie **noch nicht** (2.772 Zeilen vor und nach der Anlage) und taugt nicht als Beleg. Die Staffelzahl wurde übernommen: `/tvshow-4242.html` antwortet 302 auf `tvshow-4242-4.html`, Titel „… season 4", Staffellinks 1–4. Regel: (1) **`BuildAddShow`** baut exakt die vier Felder des Formulars in dessen Reihenfolge (getrimmt), `seasons` 1..22; ein Feld, das die Seite nicht hat, wird nicht erfunden. **`AddShowAsync`** sendet sie als `multipart/form-data` an `/add-tvshow1.php` mit Referer `/add-tvshow.php` — nicht URL-kodiert (der Fehler, den die Filmseite gerade gezeigt hat). Der **Trockenlauf** baut denselben Rumpf, meldet dessen Größe und sendet nichts (Prüfung: gleiche Bytezahl wie der Ernstfall). (2) **Klassifiziert wird an genau einem Kennzeichen** (`ClassifyAddShow`): `created` nur, wenn die Ziel-URL oder der Text `done=yes` bzw. „TV-Show has been added." nennt; ein Fehlerstatus (≥400) ist `refused`; **alles andere — auch ein 2xx mit dem unveränderten Formular — ist `unclear`** und wird nie als Anlage ausgegeben. Es gibt keinen belegten Satz für „gibt es schon", also wird auch keiner erfunden oder geraten. (3) **Die Nummer kommt aus der Suche, dann aus der Liste** (`TvSubtitlesClient.SearchShowsAsync`/`ParseShowSearch`, Rückfall `ShowsAsync`), und beides läuft durch denselben **engen** Vergleich (`ResolveShow`, F-M501). Die Seitensuche ist **weich** — gemessen: `Pagan Peak` liefert *Twin Peaks* und *Peaky Blinders* —, ihr Ergebnis allein ist kein Beweis, und „kein Treffer" heißt dort nichts. Der Jahreszeitraum (`… (2010-2026)`) wird beim Parsen abgeschnitten, sonst fände der enge Vergleich den Namen nie wieder. (4) **Der Auflöser entscheidet am Bestand, nicht am Wortlaut** (`TvstShowResolver.SettleStatus`): Serie gefunden **und** bestätigt → `created`; gefunden **ohne** Bestätigung → `existed` (sie stand schon da, die Nummer wird gespeichert, **kein** zweiter Eintrag); nicht gefunden **und** bestätigt → `created-id-unknown` (**nichts** wird gespeichert, ehrliche Logzeile); nicht gefunden **und** abgelehnt → `refused`; sonst `unclear`. Gespeichert wird **nur** eine wirklich gesehene Nummer (`MediaEntity.TvstShowId`), höchstens ein Anlegeversuch je Medium und Lauf, im Trockenlauf keiner. (5) **Nichts davon blockiert die Kette:** jeder Ausgang ist ein Ergebnis, kein Wurf; ohne Nummer wird tvst für dieses Medium übersprungen und die andere Quelle bekommt ihren Versuch. Geprüft von T238.

**F-M517 [D]:** **Eine gefundene Gegenseiten-Nummer geht auch dann in den Store, wenn kein Medien-Hash bekannt ist — geschrieben wird dieselbe Spalte, nur die Zeile wird über ihren Pfad gefunden.** Beleg (echter Lauf 16.11.2026, 04:13, jf-test, `jf-run.log`): `[moviesubtitles] ?: 'Hotel Transylvania 2' (2015) ist Film 13392 — nicht gespeichert: kein Medien-Hash bekannt — die Nummer gilt nur für diesen Lauf.` Der Upload lief danach durch, die Nummer war weg. Ursache, in dieser Reihenfolge: (1) der Medien-Hash kommt aus `ContentHashRegistry.GetMediaHash` (Registry/ContentHashRegistry.cs, „Media hash for a file: cache first … computed and stored on a miss"), und die Methode antwortet **`null`**, sobald die Datei nicht lesbar ist — gemessen ist der Pfad `media/movies-test/Hotel Transylvania 2 (2015)/Hotel Transylvania 2 (2015).mkv` ein **Symlink ohne Ziel** (`readlink -f` → `/data/movies/…ShAaNiG.mkv`, `test -e` → nicht vorhanden), es gibt also weder einen OSHash-Eintrag noch einen frisch berechneten Hash; (2) `MsubMovieResolver.Persist` kehrte bei leerem `mediaHash` **stumm** zurück („ohne Medium gibt es keine Zeile"), obwohl `MediaEntity` über `Id` **und** `Path` identifiziert ist und die Zeile über ihren Pfad eindeutig adressierbar war; (3) die Logzeile las nur den Hash und meldete deshalb „nicht gespeichert". Dieselbe Lücke traf die **Seriennummer**: `TvstShowResolver.Persist` hatte denselben frühen Ausstieg und schrieb `TvstShowId` im selben Fall nicht. Regel: (1) **Ein Schreibweg, zwei Adressen.** `ContentHashRegistry.SetMsubMovieId`/`SetTvstShowId` (gemeinsam über `SetMediaNumber`) schreiben bei vorhandenem Hash wie bisher (Zeile per `Id`, Pfad wird mitgeschrieben), sonst in **genau eine** Zeile, die diesen `Path` trägt — **dieselbe Spalte**, kein neues Feld, kein zweiter Speicherweg. (2) **Kein Raten.** Kein Hash **und** keine Pfad-Zeile, oder **mehrere** Zeilen mit demselben Pfad → `MediaNumberWrite.None`: es wird nichts geschrieben und keine Zeile gewählt; die Logzeile sagt das dann auch. (3) **Die Nummer muss auch zurücklesbar sein.** Ohne Hash lesen `MsubMovieIdAtPath`/`TvstShowIdAtPath` dieselbe Zeile (nur bei Eindeutigkeit) — sonst wäre eine über den Pfad gespeicherte Nummer für jeden Folgelauf unsichtbar und würde neu gesucht; der Merker des Laufs ist entsprechend der Pfad, nicht mehr nur der Titel. (4) **Beide Auflöser und beide Pfade.** `ResolveForMediaAsync`/`ResolveOrCreateForMediaAsync` (Film) und `TvstShowResolver.ResolveForMediaAsync` (Serie) nehmen den Pfad der Mediendatei als Parameter; `UploadPipeline` (Film- und Serien-Adresse) und `DownloadPipeline` geben ihn mit. (5) **Die Logzeile sagt WIE** — ein Text für beide Adressen (`Api/Endpoints/MediaNumberNote`): „gespeichert (über den Medien-Hash).", „gespeichert über den Pfad (kein Medien-Hash bekannt)." oder „nicht gespeichert: kein Medien-Hash und keine eindeutige Pfad-Zeile im Store — die Nummer gilt nur für diesen Lauf." Beleg am Abbild des Live-Stores (Kopie, nur die Kopie beschrieben): 31 Zeilen, **0** Zeilen mit dem Pfad des Films → `None`, 31 → 31; Zeile vorhanden, Hash nicht → `ByPath`, `MsubMovieId = 13392`, 32 → 32 (die +1 kam vom Anlegen der Zeile für den Beleg); derselbe Schreibvorgang erneut → 32 → 32; gelesen über Pfad und Hash je 13392. Ehrlich dazu: für *diesen* Film bleibt es auf der Testinstanz beim „nicht gespeichert", solange keine Zeile mit seinem Pfad existiert — der Symlink zeigt ins Leere, also entsteht auch kein Hash; der Fix wirkt dort, wo die Zeile da ist und nur der Hash fehlt. Geprüft von T239 und T240.


## 10. Scheduler, Quota and Timing

**F-M20 [B1]:** Configurable pacing — a transfer rate (**default 400/h**) and a pause between API calls (**0.1–10 s, default 0.5 s**) — **and no local call limit of any kind**. A configured pause replaces the rate-derived one; the two are not compared. The range is the server's own fastest allowed rate at the floor (SubDL allows 600 req/min = 0.1 s) and a slow, polite pace at the top; it never permits an unpaused burst. The GUI range and the limiter clamp are identical (0.1–10), so a value entered in the page is the value the limiter uses. The rate is clamped to **1–2000**. **The plugin enforces no quota of its own.** No code path counts API calls and no run is stopped for making "too many" calls. **Only SubDL stops a run**: on a real HTTP 429 the server's own counters are read and the quota decision decides between a short respacing and a day-long stop (F-M238). The limiter now only spaces calls; it cannot refuse one.

**F-M288:** **A LOCAL deferral is reported under the Workers list, in plain text — the quota bars stay a pure server reading.**

The line sits under the Workers list, in the page's normal text colour, and is not part of the quota box; the bars keep the 75 %/95 % rule unchanged, and the two facts stand side by side instead of over each other.

**The cause is passed through, never guessed.** `deferred` covers three unrelated situations: the run lock being busy, our own quota/rate cap, and a user stop. The line carries the direction's own stored wording ("download quota/rate limit — rescheduled", "cycle already active", "run lock busy — rescheduled") instead of a label invented in the page.

**The line answers "why is this direction idle", so it is gated on the direction being STOPPED — a pending fire alone is not enough** (reworked). **A deferral whose fire is gone is not shown either**, because when the scheduler holds no fire the task has already moved on. The time the line names is that fire, in the viewer's zone.

**F-M272:** **The page has THREE refresh cadences, and none of them is "10 s for everything".**

**The SubDL/TMDb lights, the login light and the library read/write rows** have **NO interval at all** — they run at page init, after Save, and on the Refresh button only.

**Every refreshed endpoint carries a cache-buster** (a cache-buster), so no layer can serve a stale body.

**F-M238:** **A 429 is not a verdict — the COUNTERS decide whether the allowance is spent.**

Every 429 is classified: rate headers → the daily allowance, exact reset from the header; body `service_busy` → transient overload; body `rate_limit` with a retry hint → a short-term trip, waited out like the overload. Only when the counters say the allowance is spent does the run stop for the day.

Counters come from the settings page's source (die Kontingent-Abfrage (Suche/Downloads)), read only after a 429; each direction names its own. An unreadable quota is not free: a failed or limit-less read leaves the 429 a stop.

A next-day fire anchors on the server's own `reset_at`, never a local midnight guess; `Continue after daily limit` gates that path per direction. The respacing offset is the job spacing (5–120 min, default 15; clamped 5–120). Every day-long stop ends in a fire at the reset plus the 30–300 min jitter; only the toggle off, or an arrival run holding the slot, stops without a fire.

A dry run reports the hearing-impaired choice (`DRY-RUN HI … (n/m candidates are HI)`).

**F-M26 [B2]:** **Random jitter in the rate limit — on the transfer rhythm, not on bare calls.**

The pause between two real transfers (upload→upload, download→download) is the base interval ±30 % (the transfer pause).

The pause between two API calls carrying no transfer (empty search, skip, metadata lookup) is **deterministic** (the bare-call pause): exactly the configured minimum pause when configured, otherwise `3600/rate`.

The band is base ±30 %, floor 1 s. With the default rate of 400/h that is 6.3–11.7 s.

A failed or skipped candidate gets **no** transfer pause — nothing was transferred — and stays on the bare-call pause (F-M26a).

The "derive from the rate" sentinel `-1` is **not** clamped; only a real value is clamped.

**F-M49 [D]:** **Daily-limit resume per direction:** two independent checkboxes, "Continue after daily limit" (Download) and "Continue after daily limit" (Upload). **Both default ON.** ON → wait once until reset (max 24 h) and retry; OFF → clean stop.

**F-M338 [D]:** **The two directions read the same sentence.**„In upload gui: continue after daily limit (ohne api)"*. The upload control carried a word the download control does not — *"Continue after daily **API** limit"* against *"Continue after daily limit"* — and that difference is misleading rather than merely redundant: the two switches do the same thing in their own direction (F-M49), so different wording reads as different behaviour. The same fault was corrected on the page texts before (*"the three maintenance menus read just Manual"*), which is why the parity is asserted as a **pair of labels** and not as the absence of one word.

**F-M51:** **Per-installation random schedule anchors:** all scheduled fires come from a background coordinator (30-s tick). Daily, weekly and monthly anchors are diced once per installation and persisted in config, and every maintenance job carries its own weekly anchor. No default fixed-time triggers. `Manual` disables the scheduled pipeline fires.

**F-M65:** **One-shot night recovery fire per direction:** a run stopped on the daily limit schedules one re-fire after the quota reset plus a random 30–300 min jitter (away from the 00:00 UTC herd), diced freshly per fire by cryptographic RNG — see F-M182.

**F-M351 [D]:**a stop cancels the pending fire of the direction it names.** The stop call now calls `SubdlSchedulerCoordinator.CancelPendingFire(direction)` in addition to writing the marker and aborting the running cycle: that direction's one-shot fire is nulled, its reschedule counter is reset (der Termin wurde von Hand entfernt, nicht durch das Budget erschöpft), und die Terminliste wird neu geschrieben so a restart cannot resurrect it (F-M116). The other direction keeps its fire; `all` cancels both — the page's single button ("\u25a0 Stop all uploads & downloads") issues both calls. The cancelled moment is logged in UTC, unconverted, like the deferred fire's retry moment. **A stop does not block the regular anchor** (operator decision): the direction comes back at its next anchor, and the manual task button clears the marker and runs. Reason: no path in the fire ladder read the stop marker, and both pipelines clear the markers at run start, so the marker was no backstop either. See F-M65, F-M26b, F-M116.

**F-M182:** **Jittered recovery fire after the daily limit:** when a pipeline stops on the daily quota, the direction is re-fired once after the server-reported reset plus a random 30–300 minutes. The offset is diced freshly per fire (cryptographic RNG) and rolled independently per direction, so the retry does not land on the 00:00 UTC reset moment every API consumer hits. The jitter applies to the **quota-anchor path only**; fires carrying their own offset (run-lock retry, rate-limit respacing) keep their exact time. Log lines name the reset anchor, the resulting fire time in the local zone, and the jitter window applied.

**F-M116:** **Scheduler state persisted:** the next scheduled download/upload fire times, the anchor markers, the catch-up flag and the lock-busy deferrals are persisted and restored on restart.

**F-M248:** **The scheduler's whole due-state is persisted, and a missed anchor slot is caught up instead of waiting for the next window.**

Every dedup marker, the catch-up flag and the lock-busy deferrals survive a restart. A slot that already fired stays consumed; a slot whose window was missed fires on the first tick where it is past-due and unconsumed. No fire carries a time window.

The marker counts the CLAIM, not the outcome: a run that fails after being queued stays consumed for the day, so persistence cannot turn a failure into an infinite retry.

**No fire window.** The refetch slot carried `if (now > fire.Effective.AddSeconds(2 * TickSeconds)) continue;` — a 60-s window that made a missed anchor wait for the NEXT regular window, contradicting F-M156. It is removed: a past-due, unconsumed slot fires on the next tick, and stacking is prevented by the persisted marker.

**F-M54:** **Reaction matrix per server response:** 403 auth → immediate stop; 429 → classified by variant (F-M238) and decided against the live counters — a spent allowance follows F-M49/F-M182 (wait-until-reset or clean stop per the direction's setting), a short-term `service_busy`/`rate_limit` trip is respaced by the job spacing; 5xx → retry with backoff; 200 + `status:false` → warn, fail-open for searches.

**F-M204:** **A running cycle is never widened and a trigger is never merged into it.** Any overlap between scheduled upload and download is answered by a **reschedule**: `ScheduleRecoveryFireAt(direction, now + JobSpacingMinutes)` for the direction not covered by the running cycle; the dispatcher logs `rescheduled +N min — cycle already running` and returns `false`. When the fire comes due, the coordinator's tick re-checks the lock check and defers again if still busy. The per-direction latch exists for the fresh-cycle case only.

**F-M67:** **Eine Überlastung des Dienstes bekommt ein kurzes Erholungsfenster.** Ein vorübergehend besetzter Dienst endet den Lauf sauber und terminiert ein nahes Feuern; ein Kontingent-Problem ist es nicht.

**F-M70:** **Die Seite zeigt das Kontingent des Kontos, ohne den Schlüssel auszuliefern.** Die Abfrage läuft serverseitig, wird kurz zwischengespeichert und bei geändertem Schlüssel verworfen.

**F-M72:** **Die Stundenobergrenze des Uploads ist kein Fehler.** Meldet SubDL die Grenze, bleibt das Item fällig, der Lauf endet und ein späterer Lauf fährt mit frischem Stundenbudget; die Marke wird zum Laufbeginn zurückgesetzt.

**F-M81z:** **Ein durch das Tageskontingent beendeter Lauf ist ein sauberer Stopp, kein Fehlschlag.** Je Richtung wird der offene Termin gespeichert und ist abfragbar.

**F-M117:** **Trifft ein Feuern auf einen laufenden Lauf, wird es vertagt.** Der neue Termin steht im Log; die Marke bleibt ungesetzt, damit der Zeitplan es erneut versucht.

**F-M146:** **Wird eine Richtung vom globalen Lauf-Lock übersprungen, terminiert sie sich selbst neu.** Der neue Termin steht als Warnung im Log.

**F-M149:** **In der ersten Runde geht der Download vor dem Upload.**
## 11. Content Registry and Identity

**F-M17z [B2]:** **Upload dedup respects the stored outcome:** a stored verdict — uploaded, or rejected with any reason — counts as "known"; an observation row does not.

**F-M298 [D]:** **A `duplicate-content` skip never overwrites an `uploaded` row — the state follows the CONTENT, not the reason.**

The content-known check deliberately counts a **rejected** row as known just as much as an uploaded one, so a skip raised on that answer fires routinely over content that is **demonstrably up** — most visibly after a container tag write, where the language-tag gate gives the file a NEW media hash and the identity move carries the accepted rows across (F-M61).

The rule: when a skip carries the reason `duplicate-content` **and** the same content hash already holds an `uploaded` row anywhere — embedded or sidecar, under this media hash or under another — the row is written as `uploaded` (reason cleared) and the stream is **not** counted as rejected. In every other case the reject is recorded exactly as before, so a skip over content that is genuinely NOT up still says so.

The decision lives in ONE registry method that every skip path calls — the embedded reject-replay, the sidecar reject-replay, the phase-1 QA gate and the phase-3 upload skip.

**F-M243 [D]:** **The HI variant counts as present whether it is a file or an embedded track.** Both evidence sources answer the same question, and Jellyfin's own the hearing-impaired flag decides a stream — read through ONE detector (the hearing-impaired predicate) used by uploader and downloader alike.

A target language counts as having its HI variant when EITHER `<base>.<lang>.sdh.srt` exists on disk OR the item carries an embedded subtitle stream of that language whose the hearing-impaired flag is true. Only when both are absent is the language queued for an HI download.

The detector reads the flag first and falls back to the stream title (`sdh`, `hearing impaired`) — the same two sources F-M71 uses on the upload side, so both directions agree on what HI means. A title-only rule misses flagged-but-untitled streams; a flag-only rule misses libraries where MediaElch/TinyMediaManager set the title alone.

The verdict is stored once and read from there (F-M254). The detector still runs on the upload side, where the stream is in hand; the download side does not re-derive it.

**F-M254 [D]:** **The HI switch is answered from the registry, not from the streams.** Whether a target language still needs its hearing-impaired variant is read from the stored verdict (the hearing-impaired rows), which joins the embedded rows the uploader wrote (the embedded mark) and the sidecar rows the downloader wrote (the download mark). Jellyfin's live stream list and the on-disk file names are not consulted.

A language counts as having its HI variant when the registry holds a hearing-impaired row for it — embedded or sidecar. Only when no such row exists is it queued. The seeder's queue gate and both download-pipeline branches read the same answer.

The marker list gains `cc`: closed captions serve the same audience as SDH and carry the same non-dialogue cues (`[ BEEPING ]`, `[ GRUNTING ]`, musical notes). Matched on a word boundary (`(?<![a-z])cc(?![a-z])`), never as a substring — `cc` sits inside real language names, and reading that as hearing-impaired would repeat the `hi`/Hindi mistake.

**F-M256 [D]:** **A registry lookup takes the media PATH and resolves the hash itself.** the hearing-impaired reader receives the media file path, resolves the media hash internally (the media-hash lookup), then reads the embedded and sidecar rows; no caller passes a hash for this question.

The resolution lives inside the method, once.

 The reader finds a stored HI row for a media path and finds nothing for the same call with the hash.

**F-M257 [D]:** **The embedded area is written by whoever reads the streams, not only by the uploader.**

The seeder's scan and the download pipeline record each embedded, non-forced, mappable subtitle track as an observation row (position, language, hearing-impaired) carrying `Status = observed` and no verdict. The uploader keeps writing verdicts (`uploaded`/`rejected`) on the same key, and an observation never overwrites them.

The position is the index into the UNFILTERED subtitle list: ffmpeg's `0:s:N` counts bitmap and forced tracks as well (F-M246), so a position taken from the filtered list names a different stream.

A failure to observe is swallowed and logged at Debug. An observation is an improvement, never a precondition: a scan whose streams cannot be read still queues its items normally.

**F-M246:** **A forced subtitle track does not make its language present — a hearing-impaired track does.**

When the download side asks which target languages are covered (the coverage question), an embedded subtitle stream counts as evidence only when Jellyfin's forced flag is NOT true. A forced track carries only the lines of foreign-language scenes, so counting it as coverage leaves the language without a real subtitle and the item marked complete.

A hearing-impaired track DOES count: SDH is the same dialogue with annotations. A bitmap track does NOT (F-M257). The rule is "any non-forced TEXT subtitle stream", and the HI switch is answered separately from the registry (F-M254).

**F-M367:** **`forced` is read from the track TITLE as well as from the flag** — a track titled `Forced` with `DISPOSITION:forced=0` is forced. Matched on a word boundary, so `enforced` is no marker.

**F-M368:** **A deferral re-arms its job, and deadlines live in one clock.** The anchor blocks folded the deadline into "due" (`… || now < until`) and tested it again in the same `if` (`now >= until`) — the two cancel each other once the day marker has been consumed by the earlier fire, so a run that deferred itself on a busy lock never came back (measured: refresh deferred 16:25 with "re-fire scheduled at 15:40 UTC", no refresh line for the rest of the day). Deadlines are now written and compared in UTC only — the tasks passed UTC while the anchor blocks passed local time and every comparison used local — anchors stay local, and the parser normalises a pre-fix state file. Die Entscheidung liegt in der Ankerprüfung. The refresh also carried a SECOND reschedule budget ("dbrefresh") that the fire never reset, so after 16 task-side deferrals the arming call refused silently; one slot per job now. The refetch slot printed a retry time and stored nothing — it re-probed every 30-s tick, burnt its 16× budget in about eight minutes and then went silent; the spacing is a stored UTC deadline now (gated, cleared on fire, persisted). Third: eine bereits verstrichene Frist wird nicht als offener Termin gemeldet — the row read "defer, next attempt Wed 4:40 PM" six hours after that moment while the run had finished green; a task on Jellyfin's own trigger never tells the coordinator.

The upload side follows the same rule: a forced track is removed from the upload set, through the same predicate the pipeline's collector and the seeder's upload-todo prefilter use.

**F-M369:** **One download unit per archive, not per extraction.** An archive is fetched once and its body is kept for later calls, keyed by the archive segment of its download URL, so the same pack yields every episode without a second quota unit. Until the body lived in a run-scoped memory cache in front of the disk cache; F-M386 removed that layer.

**F-M370:** **Packs are cached across runs; single files are not.** Only a ZIP body enters a cache. A cached archive that does not contain the entry a call wants is fetched again, and the call is not rejected. On disk the plugin keeps archives for a configurable number of days (default 14) in its data folder, bounded by a configurable ceiling (default 512 MB, oldest first) and trimmed at the start of every run; 0 in either setting switches the disk cache off. A hit on disk restarts the retention. The run summary reports the number of archive hits and how many of them came from disk; both settings sit in the Download tab.
**F-M386 [D]:** **There is ONE archive cache, and it is the disk cache.** The run-scoped layer (F-M369) is removed, not merely bypassed: a pack is fetched once, written to disk, and every further episode of it — in this run and on the following days — is a read from that one entry. Two layers meant two code paths for the same rule, and the second number of the run summary (`N archive hit(s) (M from disk)`) was 0 on every measured run; the summary now reports a single count.

The consequence is deliberate: with `days = 0` or `megabytes = 0` nothing is cached at all, not even within a run. Der Deckel je Archiv ist eine eigene Konstante (16 MB). 

**F-M372:** **A season pack is earned, never assumed.** SubDL builds the container itself: ein Einzelaufruf je Folge, dann ein Aufruf mit the array `file_n_ids`, `is_full_season=true`, the episode range in `ef`/`ee`, and a `name` that becomes the zip name on download. The switch sends a season that way only when the season is complete — the episodes on disk are exactly the episodes that aired, asked at IMDb with TMDB as the fallback — and every episode passed the gates the single upload passes. Incomplete seasons, a failed gate, mixed sources, loose sidecars and forced tracks stay single uploads. The switch sits directly above the upload gates and is on by default. One pack is one language, one HI flag and one source; its name is `{imdb}.S{ss}.{pack hash}.{LANG}`, the pack hash being the MD5 over the episodes' content hashes in episode order, and every episode of the pack carries the pack's id in its own registry row.

**F-M373:** **A pack needs the whole season in the same run.** The run plans its packable seasons before the first item: a season is packable only when the episodes queued for this run are exactly the episodes that aired — an episode that is already up individually cannot join a container, so a season that is only partly queued is uploaded as singles immediately instead of waiting for the end of the run. The plan is worked first and consecutively, per series then per season then per episode, so a season closes in one go and no buffer outlives it; queued work outside the plan keeps the order the queue gave it.

**F-M374:** **A pack carries season release names, and the first one titles it.** SubDL lists a full-season upload under the first element of `releases` and ignores the `name` field, and the packs of other uploaders carry exactly that: one season release (`Foundation.2021.S03.COMPLETE.ATVP.WEB-DL…`, `Game of Thrones, SEASON 8 4K x265`), never the episode releases. This plugin therefore reduces each episode's release name to its season form (`…S01E05…` → `…S01…`, `3x04` → `S03`) and names that release only when **every** episode of the season reduces to the same one. Release names are never mixed: episodes from different groups, and release names without an episode marker, name no release at all — the neutral label (`<series> S<nn> - complete season [(SDH)] [<lang>]`, IMDb id standing in for an unknown series name) takes its place, so the pack never claims a source it does not have.

**F-M378:** **A pack's members name their season and episode.** SubDL reads both out of each member's own file name and stores what it finds: `S01E05 - Upon Awakening.en.srt` becomes (season 1, episode 5), a member named `01 Upon Awakening.en.srt` becomes (season 0, episode 1) and every episode of the pack is listed under season zero (`S00E01` …). Pack members are therefore named `S<season>E<episode> - <episode title>.<lang>.srt`; the pack's own `season` field is not enough to place them.

**F-M377:** **A pack states no episode range.** SubDL files a full-season upload by `ef`/`ee` when those fields arrive: the entry comes back as `episode: 1, episode_from: 1, episode_end: 10, full_season: false` — one episode with a range note, which is not a season pack. A real full-season entry carries `episode: null, episode_from: null, episode_end: 0, full_season: true`. The file set is the range, so the pack upload sends `is_full_season=true` and `season` and nothing else about episodes.

**F-M376:** **When no file name carries a release, the file's own facts name the season.** Resolution and codec come from the video stream, the source from the documented quality code, the series name from Jellyfin (the IMDb id when Jellyfin knows none), assembled as `<Series>.S<nn>.<res>.<source>.<codec>` with unknown parts simply left out. No group is appended unless a file name carries one, and when the file says nothing at all the neutral label stands in — the pack never invents a source.

The plan names each season it takes and each season it leaves behind, and a dry run names the pack it would send instead of the episodes — the plan is part of what the run decides.
**F-M371:** **Norwegian is one language.** SubDL offers no split between Bokmål and Nynorsk, so a track tagged `nob`, `nno`, `nb` or `nn` counts as `NO` present — a file that already carries one written form is not given a second Norwegian subtitle.

**F-M240 [B1]:** **The hearing-impaired variant is its own subtitle datum, and completeness is derived rather than marked.**

Sie beantwortet die HI-Frage mit SPRACHEN und trägt die Variante als `:hi` token inside the download mark's language list. Both halves are withdrawn: the token with the vocabulary that could not express the datum (14.2a), the mark with the duplicated answer (14.2b) — the pipeline trusted a stored mark while the seeder queued from the disk and the refresh judged from the stored rows, so one file could be simultaneously complete, due and stale. Jede Komponente fragt denselben Leser (die Untertitel-Deckung), so a vanished `.sdh.srt` is simply an open pair.

**F-M193a:** **No construct the database cannot translate may cross into a query.** A string-comparison overload, a helper-method call or any predicate without a database expression must be applied **outside** the query lambda: keep the indexed equality inside and move the rest to LINQ-to-objects afterwards.

 ```
 // wrong — the ItemId index stays unused and the query fails at execution
 Find(x => x.ItemId == itemId && string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase))
 // right
 Find(x => x.ItemId == itemId).Where(x => string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase))
 ```
**F-M194b:** every content area is keyed by a **computed business key stored in the record's own `_id`**, never by a database-assigned auto id. The counters area and the pipeline-run area use a database-assigned id and are addressed by a separate key field.

**F-M185:** **Canonical SRT form — one normalization for hash AND payload.** Every extracted or loose SRT is normalized once on arrival, before any QA gate, hashing or upload: strip a leading UTF-8 BOM, CRLF → LF, lone CR → LF, trim trailing whitespace. The same string feeds both the hash function and the upload body, so hash input and uploaded bytes are identical by construction. The normalization is **idempotent** and runs at extraction, at loose-file read, and again in the upload path (reachable via the retry path).

**F-M186:** **The registry content hash uses SubDL's own function** — the hash function returns the MD5 of the canonical payload from F-M185, lowercase hex, 32 characters, equal to the `md5` SubDL stores in `raw_files[].md5` for our uploads. Changing the function invalidates the existing registry; the database reset (F-M90) is the intended path, no migration. The media hash (F-M61) is unaffected — it is the OpenSubtitles OSHash over size + first/last 64 KiB.

**F-M61:** **Path-independent dedup key (OSHash):** upload dedup keyed on the media content hash, persisted across restarts, moves and renames.

**F-M39 [B2]:** **No double work across directions:** every subtitle the system uploads or downloads is registered under its normalized content hash, so a subtitle that already arrived from either direction is neither fetched nor sent again. The two directions answer it at different points: the **upload** side asks the content-known check on the candidate itself, right before sending. The **download** side does not ask it during the fetch of a regular candidate — the seeder asks it before the item enters the queue and keeps a file out whose sidecar content is already known. The hearing-impaired variant does ask it during the fetch. The download records the hash it saved.

**F-M249b:** **Die Datumsregel braucht Text nach dem Datum.** Ein Datum am Namensende ist kein Datum, sondern ein Titelbestandteil.
## 12. Library Scope and Skip Filters

**F-M213:** **The library selection is a hard gate for every trigger and BOTH directions. Every report names only what was SELECTED.**

**Work:** with the library selection empty, no direction may start a run. The upload pipeline and the seeder stop up front; the download pipeline does the same (`No libraries selected — nothing to do`).

**Report:** the directory list (the GUI's *Library directories* list) filters on the selection check, not on the containment check. The walk scope includes the outer library of a nested selection (F-M189), but listing that library as a status row shows a directory the user never picked.

**Not in scope:** the database refresh's full-server id probe stays unscoped. It answers "does this item still exist", not "should this item be worked", so narrowing it would delete rows for merely deselected libraries.

**F-M220:** **The plugin description has THREE carriers and they must not drift — the card reads the DLL, not build.yaml, and an installed Jellyfin reads the package's build.yaml, not the manifest.**

All three — the plugin card, the package manifest and the catalogue manifest — must hold the same text, capped at **320 characters**, must name the required SubDL and TMDb keys, and must end with the **AI disclosure** (`Created by AI under the directions of the maintainer.`.

The disclosure rides in all three because the reach differs: somebody browsing the catalogue reads the catalogue entry, while a server that has the plugin installed shows the description Jellyfin copied out of the package's `build.yaml` at install time. Jellyfins Installer schreibt diesen Pakettext in die Metadaten des Plugins, not the manifest entry — so a card without the sentence would hide it exactly where the plugin is in use.

`scripts/release.sh` enforces all of it (three identical, under the limit, keys named, disclosure last) as a precondition, so changing one carrier alone aborts the release.

Changing the card text requires a **DLL rebuild and a Jellyfin restart**; the string is not read at runtime.

**F-M34 [B1]:** **Skippable directories:** configurable list of folder/path patterns (e.g. `incomplete/`, `downloading/`, `.tmp/`) — matches are skipped entirely, reason "skip-dir" in status.

**F-M35 [B1]:** **Skippable strings in filenames:** configurable list of substrings (e.g. `sample`, `trailer`, `incomplete`, `.partial`) — match → skip with reason "skip-pattern". Case-insensitive, simple substrings.

**F-M211:** **The shared database context is created under a lock.** Concurrent first access by two jobs firing in the same tick must yield exactly one context; two contexts over the same data file race the object mapper and one task is lost.

**F-M177:** **Conditional follow-up reseed:** after the first seed/download/upload round of a cycle, a follow-up reseed runs exactly once — and only if new library arrivals were collected while a seed, download or upload was already running. Gated by the pending arrivals; the follow-up settings decide which directions take part, and with both off no reseed runs. Otherwise the forced scan seeds the pending arrivals and, if it adds new work, download runs before upload.

**F-M192 [B1]:** **The partial reset scopes (`scope=upload`, `scope=download`) WRITE BACK the rows they mutate.** Clearing a file-complete marker mutates the entities returned by a full read, so the persist step receives **those same objects**. Collect the touched entities and call the write-back. The scope logs the number of cleared markers.

**F-M201:** **The plugin persists its OWN configuration fields by patching them into the file on disk**, not by writing its in-memory object over it. The file is read, only those fields are replaced, the rest of the document is preserved byte for byte, and the result is written as a temp file that then replaces the original.

**Consequence:** the fields are taken from the file, so an empty or damaged in-memory state cannot erase user settings. The temp-file-and-rename write additionally ensures an interrupted save cannot replace a valid file with a truncated one.

A `null` field value is **skipped**, never written as empty: "unknown" must not become "delete".

When no file exists yet, only the plugin's own fields are written; the document is **not** seeded from the in-memory object.

## 13. Configuration and Settings Page

**F-M18 [B1]:** Library selection: checkbox list of all JF libraries — only selected ones are scanned/uploaded (default: none).

**F-M53:** Manual run buttons and statistics reset on the General tab

**F-M216:** **A GUI call to a plugin endpoint that answers `application/json` must request `dataType: "json"`.** Without it the Jellyfin apiclient bundle returns the **raw response object** — `application/json` is not `text/*`, so the bundle falls through to its last branch. The object is truthy, so a `!s` guard never fires, while every field reads `undefined`. Set `dataType: "json"` on the statistics endpoint GET **and** the reset endpoint POST, in the **inline script of the page markup** — the page's only JavaScript since F-M324 withdrew the second copy (keine Datei-Paar-Falle; vgl. F-M218). Other loaders in this file call `.json` themselves.

**F-M270:** **ONE page-wide "Refresh status" button at the END of the General tab reloads every live readout at once.**

**Rule:** the button sits INSIDE the general tab container, directly below the Workers list, and writes one timestamp line directly above itself. It refreshes five readouts in one click: the API/search quota AND the download quota (both in `#SubdlQuotaBox`), the SubDL/TMDb lights AND the library read/write rows, the statistics counters, and the Workers list. Implementation: the refresh control.

**It is not a form action:** it must NOT sit next to Save, and no tab may carry a second refresh button. Nothing live exists on the Expert/Upload/Download tabs, so the General tab is the only correct home.

**Width and depth are set INLINE** (`display:block;width:100%;box-sizing:border-box;background-color:#424242;border-color:#5a5a5a;color:rgba(255,255,255,0.85)`) with `class="raised button-submit block emby-button"` as a fallback, inside a bare wrapper `<div>` — any wrapper margin breaks the flush fit with Save. The `block` class alone is NOT authoritative: a client/theme whose own `.emby-button` rule is ordered after `.block` overrides it silently.

**F-M271:** **The refresh timestamp has exactly ONE writer — the button's own click handler — and it names the manual act.**

**Rule:** the stamp sits directly above the button and reads `Manually refreshed <time>`, rendered from the CLICK moment in the viewer's timezone, seconds included. `Not refreshed yet.` before the first click.

**Resolution IS the feedback:** the time formatter must show seconds. It rendered hour/minute only, so a refresh inside the same minute produced a byte-identical string and the user concluded the button was broken.

**F-M222:** **Every required field is marked ONCE on the configuration page, and states where to get the value.**

Four required inputs: **Libraries** (F-M213), **SubDL account** (login + API key), **TMDb API key** (F-M203), **Download target languages** (F-M42: empty = download off). Each carries a required marker via `.subdl-required` / `.subdl-req-inline`, defined in both the page markup and the page script. The colour is F-M228's accent blue.

The marker sits in parentheses after the field name: `Libraries (opt-in, Required)`, `SubDL Account (Required)`, `TMDb (Required)`, `Target languages (Required)`. Opt-in and required are separated by a comma in the same parenthesis, never stacked.

The description explains what the value is for. A requirement repeated per field (label suffix, `required` attribute, bold "Required") reads as emphasis and stops being read.

Each field names its source in the order the user needs it — register first, then the key. SubDL: `https://subdl.com`, then `https://subdl.com/api-doc`. TMDb: `https://www.themoviedb.org`, then `https://www.themoviedb.org/settings/api`.

Libraries is opt-in AND required: nothing is processed until a library is picked, and the description states that an empty list stops every run.

**F-M230:** **The Libraries description states function and default in one line.** Wording: `Libraries to work in. Empty = no run.` Field descriptions state function plus default value, nothing else. **Jedes Gate nennt seinen Default, nicht nur die neuesten Zeilen** — *"Bei Quality gates upload fehlen noch die defaults in der gui"*, then *"3 von 5 fehlen"*. Von den fünf Gates unter *"Quality gates (before upload)"* trugen genau drei no default (die Mindestzahl an Cues, die SRT-Prüfung, die Synchronprüfung), while the two written later did. A default is what makes a switch readable without flipping it: the operator must be able to see what happens out of the box.

**F-M299:** **An intro block under a section heading describes what the section does — measurements never appear on the settings page.** Messwerte, Folgenzahlen, Vorher-nachher-Zahlen, Genauigkeitsangaben und Anteilees statistics are **spec and commit material**, not UI text. **Budget: 300 rendered characters** per intro. The same holds for a description block under a checkbox: it states what the switch does and its default, and a diagnostic figure such as a failure share belongs in the log line that measures it.
**F-M229:** **Links in the settings page use the same accent blue as the rest of the page.** Jellyfin's stylesheet ships only `a{color:inherit}`, so the links in the field descriptions fell back to the browser default `#0000EE`. Rule: `#SubdlSyncConfigPage a { color: #00a4dc; }` — exactly one blue, no separate hover shade. Scope: link colour only; the destructive red and the status colours are untouched.

**F-M228:** **The settings page marks required fields in the accent colour `#00a4dc`, without extra spacing.** Red is reserved for destructive and failed states, so a red marker made a mandatory field look like a fault. The inline variant carries no margin of its own. Scope: the four markers and their two style rules.

**F-M224:** **The plugin's own log mode is the only authority.** Jellyfin's Serilog pipeline filters a record before the sink writes it, and Jellyfin exposes no API to change its own log level. The plugin therefore writes **everything at the normal level** and gates the detail itself in the log helper (the per-item level, the trace level → Verbose+; the detail level → Debug+); `Normal` stays lifecycle/summary only and warnings/errors are never gated. No plugin line may be written at Trace.

**F-M36 [B2]:** Both lists maintainable on the config page (one pattern per line, textarea), defaults pre-filled (`incomplete`, `sample`, `trailer`, `partial`, `downloading`).

**F-M90:** **Database maintenance UI:** the config page exposes only **Reset** and **Restore** for `subdl-scribe.db`. **Reset** creates a timestamped backup, then removes the database so the plugin starts with a fresh registry. **Restore** restores the most recent backup after a single confirmation. No "Initialize database" control exists.

**Initialisation, first start:** the shared context creates the plugin data directory, opens the data file, raises the collection indexes and checks the schema marker. A missing data file is created empty; an existing one is opened and kept.

**Initialisation, every start:** the same sequence runs on each context creation, so a data file that lost its indexes or its schema marker is repaired to the current shape on open.

**Schema mismatch:** a lower stored schema version logs a warning and raises the marker (F-M195b); a data file written by a newer build is opened read-only.

**Reset is the only path to an empty registry.** Structural changes are never migrated; the stored state is reconstructible from the media files.

**F-M181:** **Single backup retention:** after a database reset all older `subdl-scribe.db.bak-*` files are deleted; only the backup created by that reset remains.

**F-M385 [D]:** **A quality-gate switch names its own action, and the two directions do not share a wording.** The upload language gate corrects (F-M375) and is labelled `Correct language (content vs. tag)`; the download gate throws the candidate away and is labelled `Reject wrong language (content vs. tag)`. The statistics row for attempts that never left reads `Uploads: stopped before sending` — "discarded" was read as SubDL throwing a finished upload away.

**F-M398 [P]:** **Die Worker-Zeile der Statusseite markiert einen Trockenlauf mit dem Wort `dry`, grau, direkt neben dem Ergebniswort.** Steht der Worker auf `run` (blau), liest sich die Zeile als „run dry"; ein Badge in eigener Spalte verbreiterte die Zeile um rund 50 px und schob den Status auf schmalen Anzeigen aus dem Bild.

**F-M409 [P]:** **Der `dry`-Marker trägt dasselbe Grau wie das Wort `cancel` (`#767676`).** Zwei Grautöne nebeneinander auf derselben Seite behaupteten zwei Bedeutungen, wo eine gemeint ist: „nichts passiert".

**F-M477 [D]:** **Upload und Download sind je Anbieter und Richtung geschaltet, und der alte Schalter bleibt der Rückfall.** Vier Schalter ersetzen die zwei globalen: `SubdlDownloadEnabled`, `SubdlUploadEnabled`, `TvSubtitlesDownloadEnabled`, `TvSubtitlesUploadEnabled`. Die vier sind **nullbar**, damit „nie angefasst" von „ausgeschaltet" unterscheidbar bleibt: solange ein Anbieterschalter leer ist, antwortet der Alt-Wert (`DownloadEnabled` bzw. `UploadEnabled`), und eine Konfigurationsdatei von vor der Trennung bedeutet weiter genau, was sie bedeutete — ohne Migration, ohne Neustart-Artefakt. Gelesen wird ausschließlich über `IsDownloadEnabled(provider)` und `IsUploadEnabled(provider)`, nie über das Feld: kein Aufrufer darf die Reihenfolge selbst nachbauen. Die beiden **Ankunfts-Schalter** (`DownloadOnArrival`, `UploadOnArrival`) bleiben global — sie sagen, *wann* eine Richtung läuft, nicht *wohin*, und je Anbieter vervierfacht wäre daraus ein Zeitplan, den niemand mehr überblickt. Geprüft von T202.

**F-M503 [D]:** **Der tvst-Download ist im ungesetzten Zustand AN, der tvst-Upload bleibt AUS.** Nutzerentscheid 15.11.2026: „tvst download als download default aktivieren sie, upload default deaktiviert". `IsDownloadEnabled(TvSubtitles)` antwortet damit `TvSubtitlesDownloadEnabled ?? true`, `IsUploadEnabled(TvSubtitles)` bleibt `TvSubtitlesUploadEnabled ?? false` — die beiden Richtungen derselben Quelle fallen im Default **gegenläufig** aus. Der Default beantwortet **nur** den Fall „nie geschrieben": ein gespeichertes `false` heisst weiter AUS und wird von keinem Default überschrieben; der Alt-Schalter (`DownloadEnabled`) antwortet für tvst **nicht** (er gehört SubDL, F-M477), eine Konfiguration von vor der Trennung ändert also nichts an tvst. Folge, die ausdrücklich gilt: `DownloadEnabled = false` schaltet die **Richtung** nicht mehr ab, solange der tvst-Schalter ungesetzt ist — die Download-Ampel und der Task lesen `AnyDownloadEnabled` (F-M492) und bleiben an, bis **beide** Quellen aus sind; „alles aus" heisst seit diesem Entscheid: beide Download-Schalter aus. Die Seite zeigt den **effektiven** Zustand: ein leeres Feld wird als angehakt dargestellt (der alte `!!value`-Ausdruck zeigte AUS, wo AN galt), und die Feldbeschreibung nennt „Default: on" für den Download und „Default: off" für den Upload. Filme sind nicht betroffen: tvst hat keinen Id-Index und wird ohne Serie/Staffel/Folge gar nicht erst gefragt (`ForeignCandidatesAsync` steigt vor dem Aufruf aus), der Default kostet dort keinen Aufruf. Geprüft von T225.

**F-M478 [D]:** **Ein Schalter ohne verbauten Anbieter ist sichtbar, abgeschaltet und sagt warum.** tvsubtitles.net hat seine zwei Schalter auf der Seite, aber weder einen Abruf noch einen Upload im Code: `HasProvider(TvSubtitles)` ist falsch, beide Richtungen sind unabhängig vom gespeicherten Wert aus, und unter dem Schalter steht der Grund (`MissingProviderReason`) im selben Wortlaut, den auch die Worker-Notiz benutzt. Das Feld ist abgeschaltet (`disabled`) und wird beim Speichern auf `false` geschrieben, damit ein alter Wert nie als Einladung stehen bleibt. Die Regel ist dieselbe wie bei F-M366: ein Bedienelement, das nichts tut, ist schlimmer als keines, weil es eine Wahl behauptet. Geprüft von T202. **Weitergeführt von F-M485:** die zweite Quelle ist eingebunden — `HasProvider(TvSubtitles)` ist seither wahr, die beiden Felder sind bedienbar, und die Schalter wirken je Richtung (der Anbietersatz dieses Eintrags gilt nicht mehr). **Weitergeführt von F-M503:** der Default der beiden tvst-Richtungen ist Download **an**, Upload **aus**. Geprüft von T225.

**F-M479 [P]:** **Die vier Schalter stehen im Tab ihrer Richtung, nicht in einem eigenen Anbieter-Tab.** Download-Schalter im Download-Tab, Upload-Schalter im Upload-Tab, je Anbieter eine Zeile mit dem Anbieternamen im Label (`Download enabled — SubDL`). Ein Anbieter-Tab hätte die Richtung von ihrer Schaltung getrennt und den Weg „erst hier, dann dort" erzwungen; die Richtung ist die Frage, die der Nutzer hat.

## 14. Data Model and Persistence

**F-M38 [B1]:** **Persistent state split by the kind of thing described** (section 14). Embedded tracks live under their video file, keyed by media hash + stream position; sidecars are keyed by normalized content hash; the file's own record carries path, IDs, season/episode and the derived language aggregate. The **hearing-impaired flag belongs to each subtitle datum**, never to the file (F-M282), and **completeness is derived rather than stored** (F-M283) — so the file record carries no download mark and no HI aggregate. Both pipelines read and write the same store.

The plugin keeps its state in **one database file**, grouped into **five clearly separated areas**, each keyed by the thing it actually describes. The areas share a lifetime and are written by the same lock, but they are not mixed.

**F-M194:** **Die fachlichen Schlüssel sind die Primärschlüssel.** Es gibt keine automatisch vergebene Id; ein Upsert einer scheinbar neuen Zeile kann damit keine zweite Zeile erzeugen.

**F-M327:** **Ein Bestandsformat wird einmal beim Start übernommen, vor dem Öffnen des Stores, und bleibt als Sicherung liegen.** Der Übernahmepfad liest das alte Format als einziger und löscht nichts.
#### 14.0 Area 0 — Compatibility record (`meta`)
**F-M195:** exactly **one** row (`_id = "db"`) describing the file itself: the schema version (integer, raised by hand whenever the stored shape changes), the plugin version that wrote it last, the Jellyfin version, and a timestamp.

A **lower** version logs a warning and raises the marker; no migration (F-M195b).

The row is written **once per context creation**, never per run: an audit record that rewrites itself every cycle is a write amplifier and tells nobody anything.

#### 14.1 Area 1 — Media hash cache (`oshashes`)
#### 14.2 Area 2 — Video files and their embedded tracks (`media`, `embeds`)
**F-M61b:** keyed by **file path** → the hash, the size, the modification time, its stamp. A lookup validates size **and** mtime and treats a mismatch as a miss, so a replaced file is re-hashed automatically.

**F-M196:** one record per **video file**, `_id` = the stable OSHash (16 hex characters, F-M61). Carries the path, the Jellyfin item id, the IMDb id, the TMDb id, the SubDL id, the series flag, the season, the episode, the last-seen stamp. For a series episode the ids are the **SHOW** ids, never episode or season ids (F-M191).

Derived, written automatically whenever an embedded track is stored so it cannot drift: the language aggregate (sorted, comma separated). There is **no hearing-impaired aggregate** — the flag belongs to the datum (F-M282) and an aggregate would be a second truth beside the rows.

**F-M197:** one record per **embedded subtitle stream** in `embeds`, `_id` = `"<media hash>|<stream position>"`.

The key is media hash **plus position**. Two streams of one file can share a language (a normal and an SDH variant), so the language alone never identifies the row.

Fields: the language, **both datum properties** (the hearing-impaired flag and the forced flag), the content hash, the status, the reason field, the status stamp, the SubDL id (F-M198). Each property is stored three-valued (F-M285) — `ja` / `nein` / not said — and a row that does not state them claims no coverage.

#### 14.2a The subtitle DATUM is the pair (language, hearing-impaired) — F-M282
**F-M282 [B1]:** **A hearing-impaired subtitle is its own subtitle DATUM** — its own row, identity, content hash and flag, never a property of the media file and never a modifier of a language. Every reader and writer asks at the grain of the **pair** `(language, hearing-impaired)`: area 2 keys by media hash + position (F-M197), area 3 by content hash (F-M199), so `Movie.de.srt` and `Movie.de.sdh.srt` are two rows and two data; the pair is also der Schlüsselraum der Upload-Prüfung, so a normal upload never blocks an SDH variant (F-M71).

**The one rule that does NOT split:** a subtitle of language L proves L whether or not it is the variant — SDH is the same dialogue with annotations (F-M243) — and proves `L (HI)` only when it IS the variant. So `Movie.de.sdh.srt` alone satisfies both pairs of German; `Movie.de.srt` alone satisfies German only.

#### 14.2b Completeness is DERIVED, never stored — F-M283
**F-M283 [B1]:** **There is no download completion mark.** Both download-completion fields kommen im Datei-Datensatz nicht vor, and with them the whole support apparatus that read and wrote it.

Completeness is answered **on every ask** by ONE reader — the subtitle-coverage question, asked by pipeline, seeder and refresh alike — from three sources: the sidecar files beside the media, the container's embedded rows, and the language-level stream list (counted only where the configuration counts embedded tracks as coverage).

**No invalidation step exists, and none is needed.** Deleting `Movie.de.sdh.srt` removes its evidence, so the pair is open on the next ask; the refresh therefore **reports** open files and writes nothing. A pair SubDL does not carry is retried on the next refetch (F-M47) — no expiry, no retry budget as a brake.

#### 14.2c The second property: FORCED — F-M284
**F-M284 [B1]:** **`forced` is a property OF THE SUBTITLE DATUM, like hearing-impaired** — its own row and flag, never a flag on the media file and never a way to identify a language. Grain: `(language, hearing-impaired, forced)`.

**They are NOT symmetric on SubDL's side.** Hearing-impaired is bilateral — two server-side pools (`&hi=1` / `&hi=0`, F-M241) plus an upload field — so it can be searched, fetched and announced. **Forced is LOCAL:** no filter, no field, no candidate names it. A forced datum is therefore **OBSERVED, never DELIVERED** (`SubtitleRef.IsDeliverable`) and never covers its language (F-M246).

**A forced track is RECORDED, not dropped** — that rule lives in the coverage reader alone. Marker `forced`, read as a SET with `sdh` in either order; bitmap tracks stay excluded (F-M6), and a rewrite re-states the flag or is rejected.

#### 14.2d `hi` and `forced` are stored THREE-valued — F-M285
**F-M285 [B1]:** **Every subtitle datum states `hi` and `forced` explicitly as `ja` or `nein`.** Three-valued: `true` (it IS the variant, or IS forced), `false` (it is NOT — also a statement, written explicitly, because a writer that knows the fact must never leave it unsaid), `null` (the row does not say).

**A gap is never read as a statement.** A reader treats `null` as "no claim", so the datum reports no coverage; that direction **converges** — the item is worked once more and that pass states the value — whereas the opposite default would look settled forever. The refresh fills a `null` from the row's own file name **with the value it read, `false` included**. Both properties are REQUIRED parameters on both paths, and both name builders carry `forced`.

#### 14.3 Area 3 — Sidecar files (`sidecars`)
**F-M199:** one record per loose `.srt` file, `_id` = the **normalized content hash** (F-M186).

Content is the identity of a sidecar: the same subtitle stays known after the file is renamed or moved, and two sidecars sharing a language but differing in text stay two rows. The file name is stored for diagnosis only and plays **no part** in the key.

Fields: the shared subtitle state (F-M198), plus the file name, the path, the media hash and a copy of the parent metadata (the IMDb id, the TMDb id, the series flag, the season, the episode) so a sidecar can be judged without loading the file record.

Downloaded subtitles are stored here too: once fetched, the file on disk **is** a sidecar.

Sidecars carry **no stream position**. Terminality is the outcome itself.

#### 14.4 Area 4 — Burned download candidates (`rejected_candidates`)
**F-M200:** one record per fetched-and-discarded download candidate, `_id` = `"<item id>|<language>|<SubDL id>"`.

Its grain is neither a stream nor a piece of content but "this remote release was burned for this file and language", and SubDL's own id is the only identifier such a verdict has.

Fields: the reason field, its stamp. Capped at the newest 50 per file and language. Cleared for a file and language when a download finally succeeds.

#### 14.5 The four subtitle states (F-M198)
**F-M198:** **A subtitle sits in exactly one of four states — `uploaded`, `rejected`, `downloaded`, `pending` — and `pending` is never stored.** The state name carries no reason; the reason has its own field. There is no settled state: whether a position is finished is answered by the presence of one of the three stored outcomes.

A subtitle in area 2 or 3 is in exactly one of four states:
- **uploaded** — sent to SubDL and accepted.
- **rejected** — not accepted. The **reason** lives in its own field (the reason field), never in the state name: `duplicate-remote`, `duplicate-content`, `too-small`, `too-few-cues`, `lang-mismatch`, `bad-structure`, `runtime-mismatch`, `und-off`, `und-too-small`, `und-detection-failed`, `unmapped-language`, `duplicate-self-echo`, `candidate-rejected`.
- **downloaded** — fetched from SubDL.
- **pending** — nothing recorded yet. This is **never stored**: it is the absence of a row.
There is **no "settled"/"done" state.** "Is this position finished?" is answered by asking whether any of the three stored outcomes is present.
#### 14.6 Key discipline (all areas)
- Composite keys are built in exactly one place per area (the embedded key, the candidate key).
- Every area has an index on its key; the query paths used by the pipelines are indexed as well (media hash, content hash, status, language).
#### 14.7 Consequences for readers and writers
- `IsUploaded(mediaHash, language, hearingImpaired)` answers "do I still need to upload this language for this file".
- Per-position questions use the embedded lookup / the rejection reason / the terminal-position test — never a pair-level query.
- A stream skipped **because its language is already uploaded** is recorded as **rejected with `duplicate-self-echo`**, not as "uploaded".
- The derived file aggregate (the language aggregate) is recomputed whenever a track is written, in one place. There is no hearing-impaired aggregate (F-M282/F-M283).
#### 14.8 Migration policy
**F-M195b:** structural changes are **not migrated**. The schema marker in area 0 makes the change visible and the database reset (F-M90) is the intended path; records from an unrecognised schema version are ignored rather than rewritten.

**F-M325 [D]:** **The data store is SQLite through the host's Entity Framework Core. The engine rides along in no ZIP, and there is no second engine in the tree at all.** The plugin keeps a file of its OWN, `subdl-scribe.sqlite`, in its data directory — nothing is ever written into Jellyfin's database, which its own migrations rebuild. Das Schema wird ohne Migrationen angelegt, and the business keys are the primary keys — never an auto-assigned id (F-M194). The engine is a host dependency (`ExcludeAssets=runtime`), so no SQLite file rides in the ZIP. Einen einmaligen Import des früheren Dokument-Stores gibt es nicht, and with it the previous engine: the operator released it on ("die migrationsfunktion und die alte datenbank raus"), because every installation that could still carry an old file has long since imported it. No code reads a legacy file any more, and none is deleted either — a leftover document store stays where it lies. See F-M194, F-M236, F-M326.

**F-M326 [D]:** **The store runs in WAL mode with a busy timeout, and every file-level copy of it is checkpointed first.** WAL is set explicitly — it is not the default — so a reader never blocks the writer and never sees a half-written state. `busy_timeout=5000` covers a second context or a second process over the same file. The reset and restore routes checkpoint first and clear the side files before they copy. Connection pooling is OFF as a correctness setting: the file-level routes dispose the context, copy a file over the database and open a fresh context, and a pooled connection would keep the old handle; the pool is cleared as well for the operation that cannot tolerate a stale handle. See F-M325, F-M327.

**F-M390 [P]:** **Die veröffentlichten Staffelpacks haben eine eigene Tabelle `packs`, eine Zeile je Staffel, Sprache und SDH-Variante.** Der Schlüssel ist `"<imdb>|S<season>|<lang>|<hi|plain>"`, die Zeile trägt Release, Pack-ID, die enthaltenen Folgen und die IDs der Einzel-Uploads, die in den Pack aufgegangen sind. Der Grund ist nicht Ordnungsliebe: ohne diesen Vermerk gibt es kein Gedächtnis dafür, dass eine Staffel bereits gepackt wurde — ein Lauf würde denselben Pack endlos wiederholen oder es nie wieder versuchen, nachdem die Folgen einzeln hochgegangen sind. Die aufgesogenen IDs bleiben erhalten, weil das Einfalten ein Löschen auf SubDL bedeutet; eine Löschung ohne Aufzeichnung ist weder prüfbar noch rückholbar. Die Datei entsteht ohne Migrationen und die Tabelle wird beim Start zusätzlich per `CREATE TABLE IF NOT EXISTS` nachgezogen, weil das Anlegen einer bestehenden Datei nichts hinzufügt; Schema-Version 6.

**F-M514 [D]:** **Ein abgewiesenes Anhängen vergiftet den Store nicht — und eine Zeile ohne Geschäftsschlüssel wird gar nicht erst angehängt.** Vorfall 16.11.2026 auf jf-test, wörtlich aus `jf-run.log`: 02:51:48.410 `[ERR] DownloadPipeline: [SubDL-D] WRITE FAILED … Unable to track an entity of type 'MediaEntity' because its primary key property 'Id' is null.`, 02:51:48.428 `[WRN] WorkerRunRegistry: … recording the result of worker SubdlAutoSyncTask failed: … (Parameter 'Unexpected entry.EntityState: Detached')`, danach in **jedem** Zyklus `[SubDL-Dispatch] … cycle failed` und `[N] [SubDL] cycle finished without an upload run` (03:03:46, 03:07:56 wiederholt) — der Upload-Zyklus startete keinen Lauf mehr. Ursache: der Download schreibt nach einem Speichern eine Seitenträger-Zeile, und der Medien-Hash kommt aus `Registry.GetMediaHash(mediaPath)`, das für eine eben geschriebene Datei **noch keine OSHash-Zeile** kennt und deshalb `null` liefert; `SubdlDbContext.EnsureMedia(null)` baute daraus `new MediaEntity { Id = null }`. Entity Framework Core 10 weist das Anhängen ab **und lässt die abgewiesene Instanz als `Detached` im Change Tracker stehen**; von da an wirft **jedes** `SaveChanges` dieses Kontexts `ArgumentOutOfRangeException: Unexpected entry.EntityState: Detached` (aus dem `CommandBatchPreparer` von `EntityFrameworkCore.Relational`), also auch die Aufzeichnung der Worker. Regel: (1) **Ohne Geschäftsschlüssel gibt es keine Zeile.** `EnsureMedia` schreibt bei leerem Medien-Hash **nichts** und gibt eine nicht gespeicherte Instanz zurück (nur eine `Detail`-Zeile); `SubdlSet<T>.Upsert`, `Update(IEnumerable)` und `InsertBulk` weisen jede Zeile mit leerem Schlüssel ab und **warnen** (`[SubDL-DB] {Area}: a row without a business key was not written`) — die Regel steht im gemeinsamen Set und gilt damit für **jede** Area und **jeden** Worker, nicht am einzelnen Aufrufer. Angehängt wird **vor** der Aufnahme in den Zeilencache, damit eine Abweisung keine Zeile im Cache hinterlässt, die keine Tabelle führt. (2) **`MarkSidecar` reicht „kein Medium bekannt" als leeren Wert durch** — der Medien-Hash ist eine `NOT NULL`-Spalte, und der beobachtende Aufrufer (`RecordSkippedContent`) schreibt seit jeher `mediaHash ?? string.Empty`; die Seitenträger-Zeile bleibt richtig, weil sie über ihren **Inhalt** adressiert ist, nicht über die Datei. (3) **Ein `Detached`-Geist ist kein Dauerzustand:** `SubdlSqliteContext.SaveChanges` fängt **genau** diese Ausnahme, baut den Change Tracker neu (`DetectChanges`, Zustände der verfolgten Zeilen merken, `ChangeTracker.Clear`, dieselben Instanzen mit ihrem Zustand wieder anhängen) und wiederholt den Schreibvorgang **einmal**; `Entry.State = Detached` auf dem Geist entfernt ihn **nicht** (gemessen) — nur `Clear` tut es, deshalb werden die übrigen Zeilen mitgerettet statt verworfen. Der Wiederholungsversuch ist sicher, weil die Ausnahme beim **Vorbereiten** der Kommandos fällt, also bevor irgendein Kommando läuft: ein gescheitertes Vorbereiten schreibt nichts. Ein **zweiter** Fehlschlag wird nicht geschluckt — dann bleibt der Store sichtbar kaputt. (4) **Die Aufzeichnung bleibt Protokoll, nicht Geschäftszweig:** `WorkerRunRegistry.Start`/`Finish`/`MarkCancelled` warnen weiterhin und werfen nicht; der Fix macht nur, dass ihre Zeilen überhaupt wieder geschrieben werden können. (5) **Die Filmzeile sagt, was sie getan hat:** `[moviesubtitles] {Media}: '…' — gespeichert.` behauptete ein Speichern, obwohl `Persist` ohne Medien-Hash **nichts** schreibt; sie lautet jetzt `— gespeichert.` bzw. `— nicht gespeichert: kein Medien-Hash bekannt — die Nummer gilt nur für diesen Lauf.` (Das `?` an derselben Stelle ist der Platzhalter für „kein Medium bekannt" und bleibt.) Geprüft von T236.

## 15. Logging, Status and Transparency

**F-M273:** **The configuration page has a host-free structural regression check.**

**The checks are structural, not wording-dependent:** a comment rewrite must not be able to raise a false failure.

**F-M23 [B2]:** Status view: two cumulative counters on the config page — subtitles uploaded and downloaded since the last reset. The line reads "N subtitles uploaded, M subtitles downloaded since the last reset"; the date stands on its own line below it. "Reset statistics" zeroes both and moves the date. Both are incremented at the end of every run from that run's summary, including runs cancelled from outside.

**F-M207:** **The cumulative counters live in the database; a database reset resets them with it.** Stored as one single row in the database, with 64-bit counters.

`scope=all` calls the reset in the same operation, so display and data cannot disagree.

Eine einmalige Übernahme der XML-Gesamtwerte gibt es nicht, together with its flag. The XML fields stay in place, unread, so a downgrade finds them — nothing adopts them and nothing writes them. Counters that a database reset clears stay cleared; no path seeds them back from the configuration file.

**F-M209:** **A run's in-run watchdog is torn down on EVERY exit path, and its grace is a constant 6 minutes.** The grace does not scale with any setting: it must cover the longest legal single operation, and one file transfer is allowed 5 minutes, so a scaled value could abort a working run (the old scale gave 2.4–3.8 min). Waiting out a configured pause is always legal; only a genuinely frozen run exceeds the grace.

**F-M226:** **Every plugin line's level is visible — as a text marker, written at the normal level (F-M224).** The marker `[N]` (Normal), `[V]` (Verbose and up), `[D]` (Debug and up) sits in front of the message, written by the normal level/the per-item level/the detail level/the trace level. Warnings and errors carry no marker.

**F-M225:** **Every plugin line belongs to exactly one level — and the statistics never depend on logging.** Ungated normal-level calls must not make `Normal` show diagnostics (resolved ffmpeg path, reschedule budget, seed pre-check timestamps). Assignment: internals and diagnostics → the detail level; per-item work → the per-item level; run lifecycle, abort reasons and task summaries stay at Normal. The counters are written in the single statistics writer from the run summaries, unguarded in the run path, and no log call carries a side effect in its arguments, so a `Normal` run counts exactly like a `Debug` run.

**F-M247:** **A dry run contributes NOTHING to the statistics — all six counters, not only the volume ones.**

A run started with a dry run/a dry run adds zero to uploads, downloads, QA-rejected downloads, QA-rejected uploads. A dry run does real work — searches, ranking, the TMDb id test, the QA gates — so its summary fills with plausible numbers while it saves no file and uploads nothing.

The log line reads `DRY RUN, would have uploaded {N}` / `{Saved} would have saved`. The rest of the download diagnostic aggregate is unchanged.

**F-M245:** **A message reaches the log through exactly ONE gate.** A call site picks the lowest level that must carry its message and calls the one matching gate; it never calls two gates for the same message. `Normal < Verbose < Debug` is cumulative, so two gates for one message put it in the log twice. Scope: the TMDb call trace in both pipelines.

**F-M218:** **The reject counters are persisted too, per direction, next to the volume counters.** Two 64-bit fields on the same single status row as F-M207, one field per counter — rejected downloads / rejected uploads: candidates **fetched and then thrown away**, one field per direction. The scope is every reject path, and it is F-M286 that defines it.

**Two counters were WITHDRAWN from this requirement:** *type-corrected-from-file-name* and *TMDb year-filter misses* — the row AND the counter. They are removed from every carrier: the two statistics rows, both entity fields, the writer and its early-out, the reset, both API responses, the pipeline increments and the resolver event. The BEHAVIOUR they measured is unchanged and still required (F-M217). Withdrawing a counter means withdrawing it everywhere: the check asserts the absence across page, entity, writer and API.

Every timestamp uses one format — US order (M/D/YYYY) with local AM/PM time — through the timestamp formatter and the time formatter, so stamps cannot drift apart per call site.

**F-M310 [B1]:** **The time an audio alignment spends is taken off the next download's pacing wait, never below zero.** The alignment runs after a subtitle download and before the next real download, inside a transfer pause sized for a download only.

Gemessen und verrechnet im Rate-Limiter: the fit time books, the pause spends. Both alignments are measured — main track and hearing-impaired — in one helper. The measurement wraps the CALL, not the write, in a `finally` block, so a cancelled or failed alignment also counts. The credit is CONSUMED at the pause, once, and never banked: it can only bring the next pause down, never a later one. Floor 0 — the plugin never waits a negative time. Zero and negative bookings are discarded, not booked. With the alignment switched off nothing is measured, so the pauses are exactly as configured. Uploads are NOT affected (own limiter, own rhythm).

The run's DONE line reports what the alignment cost: `N fitted to audio (Xs)`. See F-M26, F-M308, F-M309.

**F-M309 [B1]:** **The audio fit is logged on two levels: WHETHER at Normal, WHAT AGAINST at Verbose.** At Normal, in the lines F-M24d prescribes: the run START line names the switch — `audio fit=on|off` — the run DONE line carries the counter — `N fitted to audio` (F-M286). At Verbose, the per-file detail to REPRODUCE a fit: `fit reads audio track {Pos} of {N} ({Reason}); subtitle {Release}` — the track actually READ, gated on the fit switch. Verdict lines: `auto-sync corrected …` / `not applied: …` at Verbose (F-M24a), one branch each; den Zweig `ffmpeg not available` gibt es nicht, a missing ffmpeg reported once per run at Warning. See F-M24a, F-M286, F-M307.

**F-M314 [D]:** **The sidecar rename is gated by the SAME switch as the container write — `Allocate missing language codes` — und nicht durch das Auflösen unbestimmter Sprachcodes.** Default changes ON→OFF, because `Allocate missing language codes` is off by default (F-M261). The gate only gates: the file is left as it is, the 2 KB floor and the "no confident verdict" exit unchanged.

**F-M313 [B1]:** **The statistics row counts the LOOSE subtitle files the seeder renamed so their name carries the language, in its own row.**

F-M278 renames an unlabelled sidecar to the shape this plugin writes. That is work on a file the operator owns, and it was visible only in the log. It gets its own row — `Loose subtitles: language codes added` — rather than sharing the container row, because it is a different act on a different kind of file: a container gets a language tag **inside**, a loose `.srt` gets a new **name**. One number for both could be read as either.

**No direction prefix**, for exactly the reason the container row has none: the seeder serves both directions.

Counted on the MOVE, not on the attempt. Every refusal — the target name taken, a directory that cannot be listed, a filesystem error — leaves the file where it was and must not appear in the statistics as work that was done.

**F-M312 [B1]:** **A dry run does not rename either.**

The container rewrite was already suppressed by F-M263, but this rename had **no dry-run guard at all**: the seeder contained not a single der Trockenlauf check, and the gate's switch covers the container only. So a dry run moved the user's files around while its documentation promises it "writes nothing" (F-M22). The seeder now has one predicate for the whole class — the dry-run check, true when **either** direction has a dry run armed — because the seeder belongs to no direction; tying it to one switch would let the other dry run edit the library. Detection still runs: the value of a dry run is answering what it WOULD do. The file is then recorded under its CURRENT name, the one really on disk.

 See F-M22, F-M259, F-M263, F-M278, F-M311.

**F-M311 [B1]:** **The statistics row counts the media FILES whose language codes the seeder wrote, and that row carries no direction.**

The seeder's allocation pass (F-M261) rewrites media containers; it is work the run did to the LIBRARY, so it gets its own statistics row. The count is one per FILE, not per code: a file that got three tags counts once. The gate keeps reporting the per-track number on its own Normal line.

**No direction prefix** — the seeder serves both directions, and the allocation pass sits BEFORE the direction checks, so a `Downloads:` prefix would claim a scope the count does not have. The label reads `Media files: language codes added`.

Counted only when the write actually happened: a dry run `stand`s the write down (F-M263), the switch `Allocate missing language codes` is off by default, and an undecidable detector writes nothing (F-M261) — all three leave the counter at 0. See F-M261, F-M308.

**Placement:** the row sits with the upload rows, directly under `Uploads: stopped before sending`, and the two rows that carry NO direction (F-M311/F-M313) stay the last two — asserted in T123, because a new row reads naturally as "append at the end" and only the grouping is lost when it goes there.

The column reaches an EXISTING store: das Schema entsteht nur für eine neue Datei, so a new column arrives through an explicit `ALTER TABLE` guarded by `PRAGMA table_info`. The schema version is deliberately not bumped — the row grows by addition, an older build ignores the extra column, and a bump would stop the previous build from writing for nothing. See F-M30, F-M308, F-M311.

**F-M308 [B1]:** **The statistics counters are a TABLE, one row per counter, and the table names the direction each count belongs to.**

Form: label left, number right, both columns bounded so the numbers form **one vertical line**. Order: the two volume counters with **download first**, then the counter covering **both** directions, then the remaining rows **grouped by direction — all downloads, then uploads**. A row whose data is per-direction names it (`Uploads:` / `Downloads:`); one covering both says so. Order asserted (T123).

The wording states what happened, not the code's vocabulary:

 uploads DISCARDED BEFORE TRANSFER — upload candidates dropped before anything was sent, on the QA gates, the und/language checks and the self-echo guard; the upload direction fetches NOTHING.
 downloads rejected after being fetched — download candidates fetched and then thrown away.
 auto-synch — downloaded subtitles whose timing the run MOVED onto their audio track (F-M307), counted when APPLIED; no upload row. Operator's wording: `Downloads: Auto-Sync`.
 broken subtitles reported — die unter der Auto-Sync-Zeile, weil der Fit die Datei bemisst und diese Zahl die Antwort darauf zählt: Dateien, die als defekt an SubDL gemeldet und vom Portal angenommen wurden (F-M455). Gezählt wird nur eine angenommene Meldung; eine vertagte oder abgelehnte ist keine.

 See F-M218, F-M286.

**F-M286 [B1]:** **A reject counter counts what the run SPENT, and every counter is printed somewhere.**

Scope: **every path that fetches and then discards** — no bytes, content already known, broken content, hearing-impaired gate, und-off, unmappable language, self-echo, duplicate-remote, forced. A stored `Rejected` verdict without an increment is a silent discard.

Not counted: anything the run did not spend on — an empty hearing-impaired pool, candidates the walk left untried, a retry that re-runs a gate already counted.

The number reconciles with the day's quota (`requests = saved + rejected`) and both run lines print it. See F-M218, F-M24d.

**F-M24a [B1]:** **Tiered logging, all levels via Jellyfin's logger. DEFAULT: Normal.**

Normal: run STARTED/DONE lines with counters, run aborts with reason, critical errors, aggregate skip lines per reason class, wrong API keys reported ONCE as Error.

Verbose: per-file up-/downloads, SKIP reasons, dry-run lines, search results, QA reject reasons, TMDB API traces.

Debug: every SubDL API round-trip (search, download, upload, login), api_key always redacted; TMDB round-trips too.

**F-M24d [B1]:** **High-level log concept for Normal mode:** one summary line per run/direction with counters; one aggregate line covering the skip reasons of a download run; lifecycle events and warnings/errors once per event. **Amended (F-M286): the upload run writes ONE line with its two numbers — uploads and rejections — instead of none.** The old sentence ("no skip aggregate") dates from when the upload's reject number covered a single gate; with the counter now spanning every reject path, a run that discarded its whole inventory read exactly like a run that found nothing. What stays: it is one line with two numbers, not a per-reason aggregate — the per-item SKIP lines remain the Verbose detail.

**F-M152:** **The 429 reaction is decided by the server's rate headers and a live counter read, never by the status code alone.** Every response is parsed for the API's rate headers; the values are the daily limit, the remaining count and the exact server reset.

The counter is read from the account endpoint after a 429 and only then.

A 429 carrying a reset header is the account's daily limit: the run stops against the live counter unless continue-after-limit is on, and the next fire is anchored on the server reset plus the randomised 30–300 min offset (F-M182).

A 429 naming a transient server state is retried in place (F-M58); a 429 carrying no rate header and no named state is treated as an edge case and anchored on the next midnight, not on the daily limit.

The daily-limit decision and its anchor are one rule (F-M62, F-M238).

**F-M255 [D]:** **No candidate and no stream is discarded without a line naming it and the reason.**

Every exit inside the candidate walk that does not end in a save, and every per-stream exit of the upload collector, writes one line at Verbose (`[SubDL-D] … reject …` / `[SubDL-V] …`) carrying the release or file, the language, and the measured reason. The gates are named individually: download failure (no bytes, too few bytes, with the byte count), language detection, structure (monotonic flag, cue span), cue count, runtime, content-already-known, keep-best stop (with the number of untried lower-ranked candidates), the QA memory filter (with the ids it removed), the correction-hunt stop and its abandonment (with the reason no other candidate could change), and the empty HI pool (with its size). The reason carries the measured value, not a verdict.

Every candidate walk and upload-collector exit carries its measured value (monotonic flag, cue span, byte count).

The level is Verbose, not Normal: these lines are per candidate and per stream. The exits that already carried a counter but no line gained the line; the counters keep their meaning.

**F-M262:** **A rewritten container is reported at NORMAL — one line per file naming which languages were written; the per-track detail stays at Verbose.**

The gate logged through the per-item level (Verbose and up) and the tag writer logged nothing on success, so at the default `Normal` a rewrite of a media file left no trace at all. A pass that EDITS the user's media must be visible at the level every install runs at.

One Normal line names the file, how many codes were written, which languages (distinct ISO codes, position order) and — when the identity moved — the old and new hash. Per-track positions and byte counts stay at Verbose. The seeder's identity line carries only what only the seeder knows, so the two do not repeat each other.

**F-M267:** **The waiting download/upload rows report the CYCLE's fate, not their own wait.**

`WorkerRunRegistry.DescribeCycle(cycleFinished, seederOutcome, seederDetail, directionOutcome, directionDetail)` decides the row of a wait-only task, with a strict ranking: (1) cycle not finished at the wait cap → `running` ("cycle still running at the wait cap"); (2) a `failed` direction or seeder → `failed`; (3) a `deferred`/`cancelled` direction, then a `deferred`/`cancelled` seeder → yellow; (4) a `skipped` direction, then a `skipped` seeder → grey; (5) otherwise → `ok`, carrying the seeder's numbers when it reported any, else "cycle finished".

Reporting the task's own "ok" is wrong twice over: it claims success when the wait cap expires while the seeder is still scanning, and it hides a quota stop behind a green light. the fallback word substitutes "not recorded" so a row cannot show a bare outcome word.

**F-M289:** **The direction rows are written by whoever OWNS the cycle — on an arrival cycle that is the dispatcher, not the waiting task.**

An arrival cycle starts in the dispatcher and never passes through the download/upload tasks. The dispatcher records both direction rows for the arrival triggers (`event`, `arrival-followup`) and only for those: on a scheduled or manual run the waiting task owns its row, and a second writer would fight it. The ranking is the cycle description (F-M267), so a row reads identically whichever path produced the cycle. The dispatcher marks both rows `running` at cycle start, so a cycle that dies mid-scan shows the attempt rather than the previous green.

A direction that is switched OFF is recorded `skipped`/`disabled`, not `ok`.

**F-M290:** **With no library selected the seeder does not start at all — it does not scan, pre-check, or take the run lock to conclude that there is nothing to do.**

No selection means there is nothing this seeder could look at, so the answer is known before any work begins. The check sits at the TOP of the seed step, ahead of the global run lock, the change-stamp pre-check and every database read: reaching the same conclusion from inside `Scan` costs a held lock, a walked library and a touched database for a result that was already certain.

The row is `skipped` with "no libraries selected" — GREY, because a scan that was never allowed to run must not read like one that ran and found nothing. The wording matches the line `Scan` logs for the same condition, so the two cannot be told apart.

**F-M330 [D]:** **One lit lamp, for the worker that is working.**

The rule is the operator's own sentence: *"seeder gets blue only, download only download, upload only upload. Not seeder and downloader etc. as it is successive."* The cycle is a sequence — seed, then download, then upload — so at most ONE direction may be blue, and blue claims its own work, never a sibling's.

Jede Richtung markiert ihren Lauf in dem Moment its own queue is prepared at the moment its own queue is prepared, and closes its own row from its own fate in a `finally` (closing the direction row), so a failure or a stop still leaves no lamp hanging. The wait-only scheduled tasks never call `Start`: they start a cycle and wait. A direction whose run never began (switched off, empty queue, lock busy) keeps its row for the waiting task — was das Schreiben der Richtungszeile steuert.

**F-M268:** **One colour rule for every worker: green = the work ran and ended without an exception, yellow = the work did not happen but nothing is broken, red = something is broken, grey = not run.**

The palette: the page accent is `#00a4dc`. `run` → accent; `ok` → `#107c10`; `failed` → `#a4262c`; `cancelled` and `deferred` → `#ffc107` (quota, run-lock deferral, user stop); `skipped` and `never` → `#767676`; a dry-run note → `#9a9a9a`.

The accent is also the link colour (F-M229) and the required-field colour (F-M228). Red is reserved for destructive and failed states. The quota bar uses the same palette for fill, warning (`#ffc107` at 75 %) and danger (`#a4262c` at 95 %). The bar's colour is decided by the fill ALONE — a local deferral never recolours it; that state is stated as its own line under the Workers list (F-M288).

**F-M352 [D]:**the status line names what does not run by itself — the lamps stay untouched.**

**F-M353 [D]:** **A stop clears the direction's lamp with its fire.** Cancels a stop the direction's pending fire, the quota answer carries no fire (`deferred: []`, `Deferred: false`) and the row stops showing `defer` with the previous run's detail, instead of waiting for the next run to rewrite it.

**F-M354 [D]:**a status line names the STATE, not the row's own name a second time.** The three status rows are labelled `SubDL Login`, `SubDL API` and `TMDb API`. Their messages therefore carry the state only: `OK`, `reachable`, `wrong username/password`, `not authorized — check API key`, `rate-limited — try again later`, `no token returned`. Die Erfolgszeilen tragen keinen doppelten Präfix: Upload und Download teilen eine API-Zeile, `reachable` ist EINE Formulierung, zweimal verwendet. Der Zähler steht bei genau 2; statements about the KEY (`API key invalid / not authorized`, `API key not configured`, `API key invalid (HTTP 401)`) stay as they are, because the key is a different subject than the endpoint.

**F-M355 [D]:**a directory row names the library and its state — the path is not repeated.** The `Library directories` rows render the library name plus the state only (`Writable`, `Not writable: …`, `Directory does not exist`); der absolute Pfad wird nicht angehängt, because „Movies Writable" says the same thing twice and a long path pushed the state off a phone-width line. The path stays in the API answer (`DirectoryStatusDto.Path`) — diagnostics and tests read it from there, the page does not.

**F-M356 [D]:**the state word sits on the RIGHT edge of its row.** `SubDL Login`, `SubDL API`, `TMDb API` and the `Library directories` rows put the state word on the row's right edge (`.subdl-msg { margin-left: auto; text-align: right; }`), in one column with the workers block, instead of starting right after the label. Reason: the labels have different widths (Anmeldung, API, Bibliotheksname, Testzeile), so a left-aligned message began at a different x on every line and the block read ragged; `text-align: right` also keeps long messages (`Not writable: …`) ending on that same edge. The workers block itself stays LEFT-aligned — a right-pinned worker table was rejected earlier (F-M308 comment) and is not reintroduced.

**F-M357 [D]:**the refetch field describes itself in the words of its own name.** The description under `Refetch interval` reads `Refetch interval for download and upload.` Der Text nennt die Sache, nicht einen zweiten Namen für sie (F-M354/F-M355).

**F-M358 [D]:**the status rows sit on the same rhythm as the worker lines.** Bei 390 px lagen die drei Statuszeilen **40 px** auseinander (19 px line + 2×4 px row margin + 12.8 px flex gap from the inline style), while the workers block packs its lines at **one line height** — its rows are `display:contents` inside a grid without row-gap, so margins and gap have no effect there. Two blocks of the same page therefore breathed differently and the airy one looked broken on a phone. The flex gap left the inline style of `#SubdlStatusBox` and `#SubdlStatusBox.subdl-status-row { margin: 0; }` puts both groups on one rhythm; the `h4` between them keeps its own margin.

**F-M359 [D]:**the deferral note names the DAY of the next attempt.** The line below the Workers list reads e.g. `Download: download quota/rate limit — rescheduled — next attempt Tue 6:10 AM`: short weekday from the locale plus the time to the minute. Die Sekunden entfallen hier; ein geplanter Termin braucht sie nicht.

**Where.** The line under the Workers list that already reports a pending fire (`#SubdlLocalDeferLine`). The notes are appended after the fire notes, separated with `·`, and come from the same answer as the fire notes (quota `local.notes`), so the two cannot disagree. Nothing to say ⇒ empty line.

**The two words.** `off` = cannot start at all. `manual` = can start, but nothing starts it by itself. No word = it runs by itself; that includes `OnArrival`, which is an automatism too. One word per worker: `off` wins over `manual`.

**When `off`.** Download and Upload: toggle off, no library selected, or credentials missing. Download also: no target language set.

**When `manual`.** Download and Upload: das Refetch-Intervall (Default wöchentlich) on `Manual` (or the legacy `Never`) — no automatic pipeline cycles. Upload Postprocessing: upload toggle off, or its cadence on `Manual`/`Never`. Database refresh: der Aufräum-Modus (Default wöchentlich) on `Manual`/`Never`. OSHash refresh: die Hash-Auffrischung (Default monatlich) on `Manual`/`Never`.

**Unchanged.** The lamps, the stored results, the details and pending fires stay as they are, so a run never disappears behind a switch.

**F-M360 [D]:**the offset fit is fitted on the DIALOGUE lines only.** Music (`♪`/`♫`), bracketed sound labels (`[door slams]`, `{phone rings}`) and bare capitalised sound words (`GUNSHOT`) are excluded from the fit's input; every other cue is used. They are still MOVED — each takes the shift of the last dialogue cue at or before it, so the writer shifts every cue of the file. Fewer than 40 dialogue cues ⇒ the file is fitted on all its cues. Why: those lines sit on music or effects, vote the same at every candidate offset, and set σ, the segment-length floor and the block grid — measured, Line.of.Fire S01E02 collapsed a real 6 s drop into one step (3 s off for 4.5 minutes) and Lioness S02E06 left a 12 s block standing; the exclusion fixed both and left seven exact files exact.

**F-M366 [D]:**no status without a run.** F-M365 took the row away from a download held back by the spent allowance; this closes the other four paths that wrote a row for work that never happened. The lamps know `run`, `ok`, `fail`, `cancel`, `skip`, `defer` and the grey "nie" of a missing row — a switched-off direction rendered as a grey `skip`, which reads like a run that skipped something. Now no row is written for: the switch being off (download and upload), a cycle already active when the task fires, a run that never began (where DescribeCycle painted the SEEDER's sentence onto the direction's line), and a Jellyfin restart before the run started (which claimed `ok / restart`). The cycle keeps saying all of it in the log — `download ran nothing this cycle (switched off|run never began) — no worker row` — so a missing row always has an explanation. A direction row is now written by exactly one thing: its own run, when it begins. A user stop still writes `cancel`, and a real deferral with a scheduled re-fire still writes `defer` with its time.

**F-M365 [D]:**a held direction writes no row.** When the SubDL download allowance is spent the direction does no work at all — no seed, no fetch — eine eigene Zeile mit dem Zeitstempel des Zyklus und dem Wort `defer` entfällt: das Flag bedeutet „ein erneutes Feuern ist terminiert", was ein verbrauchtes Kontingent nicht terminiert. The hold is logged (`download held back — "download limit spent (50/50)"`) and both writers now leave the row alone: the waiting task and the cycle-end writer skip it. The switch being off still writes `skipped: disabled` — that is a deliberate configuration state, not a run that did not happen. Supersedes the row-writing half of F-M331.

**F-M364 [D]:**a segment that buys level on a song is vetoed.** The objective is the mean voice-band level under a cue, and sung vocals are louder there than speech — The Rainmaker S02E01 scored 0.640 for its opening block placed on the title song against 0.538 where the dialogue sits, so the fit pushed 58 cues 15.50 s early and the writer clamped two of them to `00:00:00,000 --> 00:00:00,000`. Mean level cannot separate the two, the ONSET at a cue start can: the level rises by 0.0006 across the cue starts on the song against 0.0227 for the same file's dialogue. Each segment is checked AFTER the fit; a segment that wins ≥ 0.10 mean level over the unshifted placement with an onset below 0.01 takes its NEIGHBOUR's shift. Calibrated on 46 files (~16 000 cues): exactly one segment reaches the 0.10 floor (Rainmaker +0.132; next-highest anywhere: 0.057), so only that file changes — from `−15,50` to `−2,00`, the shift its own remaining segments already use. The onset argmax inside a segment was rejected as a rule: it swings ±0.1 between neighbouring 0.25 s grid points and would fit noise.

**F-M361 [D]:**a cue is a sound label when nothing but bracket groups remain.** After the markup strip and the dash trim, every `(..)`, `[..]` and `{..}` group is removed; if no letter or digit is left outside them, the cue does not feed the fit. This REPLACES the capitalisation guess and the leading-bracket rule of F-M360: embedded SDH writes labels in lower case (`(gunshot)`, `(crowd shouting)`), which the capitals rule could not see — 336 of 22 992 cues over 30 embedded HI tracks, while a cue that starts with a group but carries dialogue outside it (`(SCOFFS) Okay, so now what?`, `[Shirley] Hey!`) IS dialogue, and the old rule had thrown 1 848 of those away. Net on the same corpus: labels drop from 22.9 % to 16.3 % of the cues, the fit gains 1 848 dialogue anchors, 0 false hits on bracket-free lines.

**F-M193 [B1]:** **`/user/mySubtitles` is NOT paginated — the counters must count DISTINCT upload ids.** The endpoint ignores `page`, `per_page`, `offset`, `limit` and `start`. The listing deduplicates by the upload id and breaks on the first page that adds no new id; rows with `UploadId <= 0` are kept unconditionally. The status line does not claim "in N pages".

**F-M265:** **The LAST run of each worker is stored in the database and shown in a "Workers" section on the General tab of the configuration page — one line per worker with the time of its last run and how it ended.** Only the last run is stored, not a history.

**Six outcomes**, each mapped to a colour and a word in the GUI: `ok`, `failed`, `cancelled`, `skipped` (a scan that found nothing to do), `deferred` (nothing broken, the work was pushed forward: run lock busy, quota, user stop) and `never`, plus `running` while a cycle is still working at the wait cap. Colour rule: F-M268. A `skipped` run still writes its row — "the task was not allowed to run" is information, and without it a disabled direction looks identical to a broken one.

**The dry-run note is CONDITIONAL**: a grey `dry run` note appears in the result cell only while the direction's dry-run flag is on. With the flag off the line stays clean.

**F-M293:** **A Jellyfin restart is not a user stop; the worker row reports what it actually did.**

A restart cancels the running task exactly as the stop button does, and the token carries no reason. The task's catch therefore treated both the same — it wrote a stop marker, called the user-stop path, and that records `cancelled`/"user stop" for **both** directions at once, so a restart reported an action the user never took.

**The stop marker is the discriminator:** the `Stop` endpoint writes it *before* it signals, a shutdown never does. `PipelineStopSignal.HasMarker` tests it **without consuming it**; without it the row is `ok`/"restart". A worker with no stop button (postprocessing, OSHash refresh) can therefore never report a user stop.

**F-M294:** **"Deferred" means a fire was scheduled — for ANY worker, and the moment is named.**

A pending re-fire IS the deferral: it exists only because a run could not finish, so it decides the word and the colour of that worker's row — YELLOW as `defer` — for every row that does not claim something stronger. `failed` (broken) and `run` (in progress) are left alone; `ok` and `cancelled` both claim that nothing is owed, and a fire proves otherwise. All workers that can hold a fire are covered (both directions, database refresh, OSHash refresh, postprocessing), and the flag is read per worker from the coordinator rather than from the row.

The line under the Workers list names, per worker and in list order, its stored cause and when the fire is due. It is gated on the fire alone, so a restart cannot blank it; the seeder holds no fire of its own and never appears.

**F-M269:** **The database refresh's detail line names the compaction fallback.**

**This is a NOTE, not a red light:** the outcome stays `ok`.

**F-M339 [D]:** **Every number in an Auto-Sync log line is written culture-invariantly.** An interpolated number follows the host's locale, so the same code printed `t = +8,62` on one day and `t = +8.62` on the next, and the operator parses these lines. **The SRT timestamp formatter keeps its comma** — that is the file format, not a display choice.

**F-M292:** **Ein Worker mit offenem Termin ist gelb, unabhängig davon, welcher Worker es ist.** Die gespeicherte Zeile beschreibt den vergangenen Lauf, der offene Termin beschreibt, was noch aussteht.

**F-M341:** **Jede Zahl in der Korrekturzeile trägt einen Dezimalpunkt, unabhängig von der Sprache des Hosts.** Werkzeuge lesen diese Zeile; ein Komma bricht den Leser.
## 16. Non-Goals

- N-1: Standalone downloader app or separate downloader plugin — integrated in Sub Scribe.
- N-3: Transcoding/opening bitmap subtitles
- N-5: Changes to Jellyfin core

## 17. Non-Functional Requirements

**NF-1:** Platform independence: pure IL DLL (net10.0), no native binding, no P/Invoke, no platform-specific dependencies.
**NF-2:** GPL-3.0-or-later (GNU GPL v3, or any later version) — the SPDX identifier the source headers carry. Every file that can carry a comment carries the same notice in its own syntax; a file whose format has no comment (JSON, PNG, SVG) carries none.
**NF-3:** Performance: no blocking of library scans; uploads asynchronous (background queue, IHostedService), extraction sequential per file
**NF-4:** Robustness: queue survives Jellyfin restart (persistent state files); no infinite retries
**NF-5:** No telemetry/external calls except api.subdl.com (+ dl.subdl.com for downloads, + api.themoviedb.org when a TMDB key is configured)
**NF-6:** Testability: test JF instance with a small test library
**NF-7:** Cross-platform discipline in code: no hardcoded path separators, no P/Invoke, no case-sensitive file operations without normalization
**NF-8:** **Manual stop button** ("■ Stop all uploads & downloads", General tab): one click cancels the running task for BOTH directions.

**NF-9:** **The specification is checked like the code — it is the eighth test suite.** Ein eigener Test prüft ohne Jellyfin-Host: every Contents counter matches the definitions in its section, every requirement id is defined exactly once, the test numbering is gapless from T1, every test a requirement names exists, the header status names the version `build.yaml` builds, and no definition-shaped line escapes the pattern it counts with. Since it is a gate inside `scripts/release.sh`: der Spezifikations-Schritt bricht den Release ab when the document and the build disagree, and the `Test suites` step runs `anchorgate`, `forcedtitle` and `archivecache` right after the build, against the DLL just produced, so a suite that is not green stops the tag. Written by hand until then, the document had drifted twice while every release looked clean.

## 18. Acceptance Criteria

**A-1:** New episode with embedded subs appears in JF → all text subs automatically extracted, QA-checked, uploaded, without manual intervention
**A-2:** Dry run on test library produces a complete status report without API calls
**A-3:** Corrupted/garbled subtitle stream is detected by QA and skipped
**A-4:** Wrong-language test case → detected per config
**A-5:** Upload error (simulated 5xx) → retry + "failed" status, no crash
**A-6:** State survives JF restart: already uploaded files are not processed again
**A-7:** Library scan is not blocked by running uploads (<1s in-scan overhead)

## 19. Test Library

Every functional requirement (F-M*) carries at least one automated test case: a unit test where possible, an integration test where the behaviour needs one.
**T1:** Episode with mixed-language text subs and one wrong stream tag
**T2:** Movie with bitmap subs only (PGS) is ignored
**T3:** Corrupted subtitle stream in a container passes to the QA reject
**T4:** Mini sub below 2 KB and 30 cues hits the size gate
**T5:** Series without a series IMDB is skipped as "no-imdb"
**T6:** Skip-dir and filename pattern hits are skipped
**T7:** An already uploaded file is not uploaded twice
**T8:** A duplicate release is uploaded, then removed by postprocessing and marked `duplicate-remote` (F-M184)
**T9:** The same episode in two library copies is recognised by content hash
**T10:** Rate-limit timing stays inside the target rate and the jitter band
**T11:** An HTTP 429 answers with backoff or waits for the reset (F-M54)
**T12:** A 403 stops the run immediately (F-M54)
**T13:** The same text as LF, CRLF, BOM or lone CR yields one content hash and one payload (F-M185)
**T14:** The content hash is lowercase 32-character hex of the canonical payload (F-M186)
**T15:** UTF-16 downloaded bytes decode to the same content hash as the uploaded file (F-M187)
**T16:** Writing the same track, sidecar or candidate twice leaves one row (F-M194b)
**T17:** Two streams of the same language keep independent verdicts (F-M197)
**T18:** A higher schema version disables writing and is never rewritten (F-M195)
**T19:** A renamed or moved sidecar stays one row; different text stays two (F-M199)
**T20:** An automatic save updates only the plugin's own fields (F-M201)
**T21:** The transfer pause is spread ±30 %, the bare-call pause is constant (F-M26/F-M26a)
**T22:** A deferral lands exactly der Job-Abstand (Default 15 min, 5–120 min) after the STOP moment (no roll-over anchor, no added jitter), it carries the direction's own stored cause rather than a generic "rate limit", and the unchanged items stay `Queued` with the worker row reading `deferred` (F-M26b/F-M288)
**T101:** No code path counts API calls against a local limit: the limiter exposes no bucket, no counter and no refusing call, and a run that makes 4000 calls in a minute is never stopped by the plugin (only a real HTTP 429 stops it) (F-M20)
**T23:** An overlapping trigger reschedules instead of widening the running cycle (F-M204)
**T24:** Sixteen reschedules are accepted, the seventeenth is refused once (F-M205)
**T25:** A respaced fire never claims a quota reset (F-M206)
**T26:** The counters live in the database, reset with it, and are adopted from the XML exactly once (F-M207)
**T27:** The reschedule budget resets at the UTC day roll-over (F-M208)
**T28:** A run that leaves on an exception tears its watchdog down (F-M209)
**T29:** No predicate reaches the database that it cannot translate (F-M193a)

**T30:** Refresh, OSHash, postprocessing and the refetch cycle each fire on their own setting (F-M210/F-M210a/F-M211)

**T31:** A fire that comes due before the tasks are registered is not consumed (F-M212)

**T32:** An empty selection stops every direction, and a nested selection reports only itself (F-M213)

**T33:** Compaction covers data file plus journal (F-M214)

**T34:** The reject counters — download and upload — persist, reset with the volume counters and render as numbers (F-M218). The type and year counters were withdrawn on and must NOT reappear in any carrier.

**T35:** A mis-typed episode is searched by its parsed name and receives its own pack entry (F-M217, F-M215)

**T36:** A title that is both film and series resolves by year, type-neutral first (F-M219)

**T37:** Both description carriers hold the identical string, and the release aborts on drift (F-M220, F-M221)

**T38:** Log lines carry the new namespace while the data folder keeps the assembly name (F-M223)

**T39:** The plugin's own log mode produces each level's lines without touching the server configuration (F-M224)

**T40:** A `Normal` run counts exactly like a `Debug` run (F-M225)

**T41:** Every plugin line carries its level marker at every setting (F-M226)

**T42:** Every task the plugin registers reports the Sub Scribe category (F-M227)

**T43:** Required markers render in the accent colour with no stray space (F-M228)

**T44:** Every anchor on the settings page is the accent blue, none the browser default (F-M229)

**T45:** The Libraries description states function and default only (F-M230)

**T147:** Every upload quality gate names its default.

**T46:** Jellyfin's ids are corrected by title and year, counted over both directions (F-M231)

**T47:** A server-error login is classified as transient; wrong credentials fail closed at once (F-M232)

**T48:** An on-arrival run works exactly the items that arrived (F-M233)

**T49:** Ersetzt durch F-M283. The refresh REPORTS open required files and writes nothing — there is no download mark left to drop (F-M234/F-M283)

**T50:** Three transport timeouts are retried, a user stop is never (F-M235)

**T51:** A failed compaction leaves the database answering (F-M236)

**T52:** A 429 is classified by its variant, not by its status code (F-M238)

**T53:** The quota counters decide exhaustion, and an unreadable quota stops (F-M238)

**T54:** A rebuild that cannot release the pages is replaced by one built from the rows (F-M237)

**T55:** Every day-long 429 stop schedules a fire (F-M238)

**T56:** Sidecar names resolve through one reader, never through the language mapper (F-M239)
**T57:** Ersetzt durch F-M282/F-M283. A vanished `.sdh.srt` is an open pair on the next ask, with no invalidation step (F-M240, F-M283)
**T58:** The HI variant comes from its own search (F-M241)

**T59:** One corrected file per language: the walk stops at the first candidate whose correction proves itself, and an unprocessed file never ends it (F-M319/F-M318)

**T60:** The HI answer comes from the registry, and `cc` counts as a marker (F-M243/F-M254)

**T61:** The pipelines execute the queue item's work order (F-M244)

**T62:** One message produces exactly one log line at every level (F-M245)

**T64:** A forced track does not count as present, SDH does (F-M246)

**T65:** A dry run contributes nothing to any of the six counters (F-M247)

**T66:** The scheduler's due-state survives a restart (F-M248)
**T67:** A missed anchor slot is caught up on the next tick (F-M248)

**T68:** A bare episode marker makes the item a series (F-M249, part a)
**T69:** The bare-marker guard rejects release-group suffixes, and the date rule needs text after the date (F-M249, parts a and b)

**T70:** The parser's four new shapes hold, and the library diff is the acceptance (F-M249/F-M250)

**T71:** One name parser, and a bracketed year leaves no bracket in the title (F-M251/F-M252)

**T72:** Every discarded candidate and stream is named with its reason (F-M255)

**T73:** A registry lookup takes the media path and resolves the hash itself (F-M256)

**T63:** N text streams cost exactly one ffmpeg call, and an unknown index fails the pass (F-M5)

**T74:** The embedded area is written with the uploader switched off (F-M257)
**T75:** The refresh drops embedded rows whose stream is gone (F-M258)
**T76:** The scan records loose sidecars as observations (F-M259)
**T77:** The fetched file's own HI flag decides its name and its record (F-M260)
**T78:** The database is exercised without a Jellyfin host (F-M257/F-M259/F-M260)
**T79:** The subtitle datum is the pair — a variant is a different datum from its regular file, and a variant file still proves its language (F-M282)

**T90:** One coverage reader feeds the pipeline, the seeder and the refresh, and splits the two search pools apart — only the variant is open when only the variant is missing (F-M282/F-M283)

**T91:** A forced subtitle is a datum with its own flag, observed in both areas, and it never covers its language (F-M284)

**T92:** The refresh backfills the forced flag on rows written before the field existed, from their own names (F-M284)

**T93:** `hi` and `forced` are three-valued — a legacy row reads back as `null` and claims nothing, a fresh write states `false` explicitly (F-M285)
**T94:** A forced subtitle is observed but never uploaded — the loose-file path asks its deliverability check and skips it, the embedded path drops it via the dialogue check (F-M284)
**T95:** A container rewrite re-states the forced disposition and is REJECTED if it did not survive, so a language-tag write can never silently turn a forced track into the film's dialogue (F-M284)

**T96:** Every path that fetches and then discards a candidate increments the reject counter, the count reconciles with the day's spend (requests = saved + rejected), and both the download and the upload run line print it (F-M286)

**T97:** A dry run leaves no stored verdict in either direction - the refetch stamp, the registry upserts, the file-missing deletion and the cycle-queue removal are all held back, while the worker-run record is still written (F-M22, F-M287).

**T98:** The walk's candidate limit and the search's early-stop threshold are the SAME number, derived from the ONE correction-attempts setting (F-M50): with that setting at 3 both are 3, at 2 both are 2, at `0` both stay unlimited.

**T99:** The candidate walk's exits are reachable and each is the one that applies: it stops at the first CORRECTED file, and it stops when the download budget is spent.
**T100:** A deferred direction shows its own stored cause under the Workers list (never a generic "rate limit"), that line sits below the quota box rather than inside it, the bars keep their 75 %/95 % colours at every fill level, and the line disappears once the scheduler holds no fire for that direction (F-M288)

**T102:** An arrival cycle updates the Download, Upload and Seeder rows together, and each direction's row carries that cycle's fate (green on work done, yellow on a quota stop, red on a failure); a direction that is switched off is recorded grey/`disabled` rather than green; a scheduled cycle leaves the arrival path out of it entirely (F-M289)

**T103:** With no library selected, a seed step leaves a grey `skipped`/"no libraries selected" seeder row, logs `No libraries selected — seeder not started`, holds no run lock and does not walk the library — neither on a scheduled run nor on an arrival (F-M290)

**T106:** A Jellyfin restart during a run records `ok`/"restart" for the affected worker — not `cancelled`/"user stop": no stop marker exists, so neither the task catch nor the in-cycle path reports a user stop, and both directions keep their own fate; a real stop through the endpoint still records `cancelled`/"user stop" for the direction it names (F-M293)

**T107:** Every worker holding a pending deferred fire shows a yellow `defer` lamp and its own line under the Workers list naming the worker, its stored cause and the moment the fire is due; the fire overrides `ok` AND `cancelled` — a direction with a pending fire is never shown as cancelled — while `failed` and `run` are left alone; the line is gated on the fire alone, so it survives a restart and disappears once the fire is consumed; the seeder never appears (F-M294)

**T108:** The drift detector, driven with synthetic material, reports NO drift for cues that track the speech at a constant offset, recovers planted steps of known size and time at 20 and 30 min with a span matching the planted total, and reports drift for a ramped offset — and the factored two-offset marginal used in the port equals the explicit outer log-sum-exp (worst deviation < 1e-6 over 20 random trials) (F-M295)

**T109:** End-to-end on a real episode: the plain subtitle comes back steady while the hearing-impaired variant of the same episode comes back drifting with a span and boundaries named, and the verdict states a span rather than a correction value (F-M295)

**T110:** The auto-sync's two file-level halves, without audio: the audio-track rule picks (1) the track in the subtitle's language, (2) English, (3) the first untagged track, with the 2-vs-3-letter codes (`en` against `eng`, `de` against `deu`/`ger`, `zh` against `zho`/`chi`) resolving as equal and `und`/null resolving as "no language"; a planted constant shift moves every timestamp by exactly that amount while text, cue count and cue order stay unchanged; a shift that would push the first cue below zero is APPLIED and that cue is CLAMPED to 0, with every later cue still carrying the full measured shift; the corrected file is written CANONICAL (UTF-8, no BOM, LF) — asserted on the real writer, so a reintroduced re-encode to the fetched byte style fails here — while the archived original keeps its own byte style; and the kept name `<base>.<lang>.srt.unsynced.zip` does NOT match the sidecar listing pattern der Namensstamm mit `.srt` while the swapped order does (F-M296, F-M306)

**T111:** End-to-end against real audio, driving the FEATURE (not a copy of its arithmetic): the sign of the correction is established on the material itself by planting +5 s and applying BOTH directions — the one landing within 0.7 s of the plain subtitle is `−detector`, and that is asserted, so a sign flip fails here instead of doubling every corrected file; a subtitle already in sync comes back `applied=False`; and a shift planted at +2.5 / −1.8 / +6.0 s is measured and removed, with the corrected text lying within 0.7 s of the plain subtitle by MEDIAN offset — a file that took no part in the measurement.

**T112:** Driving the registry decision itself (not a copy of its arithmetic): an `uploaded` row survives a `duplicate-content` skip — after the identity move that the language-tag gate triggers, a skip over the same content leaves the row `uploaded` with its reason cleared and increments no reject; and the same skip over content that is NOT up still writes `rejected` with its reason intact, so the two cases are told apart rather than both being called settled.

**T113:** Driving the rendered page text, not the markup: every intro block under a section heading measures at most **300 rendered characters** (tags stripped, whitespace collapsed) and carries **no measured value** — the ban list is the forensic vocabulary itself (`Measured`, a before/after arrow, an episode count, an accuracy figure), so re-introducing "Measured on 36 drifting episodes: worst line 10.74 s → 0.17 s" fails here rather than passing as prose.

**T114:** The staircase correction, driven on the SAME synthetic episode whose cues were built from a known burst list, and judged against that burst list — the ground truth that took no part in the measurement, because a correction scored with the detector that produced it is the exact inverse of its own measurement and always reports success.

**T115:** The specification's structure, checked without a build or a host: every Contents counter equals the number of definition lines in its section (sub-headings and superseded entries included), every requirement id is defined exactly once, the test numbering is gapless from T1, every test a requirement names exists as a definition, and the header status names the version `build.yaml` builds.
**T116:** The correction section's wiring, driven against the page file and the built DLL: exactly ONE checkbox in the section; its id present in the markup and bound on load (default-on aware) and on save; the four absent ids (Drift-Prüfung, Drift-Ablehnung, die Prüfung der Tonspur je Sprache, die Anker-Synchronprüfung) ABSENT from the markup and from the inline script; die Konfigurationsseite als eingebettete Ressource, so the page actually ships inside the DLL; and die automatische Synchronprüfung defaulting to `true` in C#.

**T117:** The staircase scored against REAL material with the AUDIO as the only yardstick — no subtitle takes part, neither embedded nor as a sidecar.

**T118:** The kept original, driven through the real planner and read back through the real name parser: the first original takes slot **99** beside the corrected file; the name **matches** the sidecar listing (that match is the point — it is how the file becomes a selectable track) and parses as the corrected file's own language with `hi=false` and `forced=false`; with 99 taken the next original takes **98**, and the numbering never leaves 90–99; when all ten reserved slots are taken **no name is returned**, so nothing can overwrite an original already kept; and **every corrected slot taken returns the plain name**, never a reserved one — the collision F-M315 exists to prevent.

**T128:** The reserved block is exactly **90–99**: the reservation test is true at 90 and 99 and false at 89 and 1. Die Zielnamensuche läuft **abwärts** — 99 first, then 98 — and returns **null** when all ten are taken, never a name that would overwrite. Der Zielname für korrigierte Dateien vergibt nie einen reservierten Platz: with slots 1–89 taken it returns slot 1 (`<base>.<lang>.01.srt`, F-M316) so the caller's own existence check refuses the move.

**T129:** The sidecar name carries its slot as a two-digit number, always, and the number is what orders the track list.

**T130:** The slot tells the two kinds of downloaded file apart, and the two ranges are counted separately.

**T131:** The download repeats until a correction proves itself, capped at the QA retry limit.

**T132:** The QA retry limit bounds the fit and nothing else.

**T133:** The refusal's TEXT is pinned on the real builder with one synthetic fit per cause, so the four shapes cannot collapse back into one: the thin-gain case must carry `t = +1.71 < 2.0`, the score pair and delta, the segment offsets, `887/887 cues` and `largest 2.50 s`; `nothing to move` must name the cue count, `the correction would make it worse (untreated better)` both scores (and must NOT print the `t < 2.0` comparison, which was not the condition that refused it), `below floor` the floor it missed (0.7 s).

**T134:** The auto-sync has its own worker row and its own light, and the statistics count only alignments that happened.

**T134 also covers the row's survival of the UPLOAD direction** (added with v12.1.12.203, Abschnitt 6b2 der GUI-Strukturprüfung). The parameter must be nullable, and the `downSummary == null` guard must sit BEFORE the first use — asserted as an ORDER, not as presence, because a null check placed after the first dereference compiles, reads correctly and changes nothing. The parameter's own doc darf die Behauptung „never null" nicht tragen that justified the missing guard. **Mutation-verified:** the guard removed and the nullable dropped, two of the three checks turn RED (`guard at -1, first use at 736`) and the baseline is green again on restore.

**T136:** The configuration page carries its JavaScript exactly ONCE.

**T137:** The store is SQLite through the host and the engine rides along in no ZIP.

**T138:** The store runs in WAL, and every file-level copy of it is checkpointed.

**T139:** A version that meets a leftover document store does NOT import it and does NOT delete it.

**The same harness then drives the three paths the operator reaches for once an import has landed — DATABASE REFRESH, RESET and RESTORE — because the STORE suite covers them against a SEEDED store, and a seeded store is not what an import leaves behind.** Every section works on its OWN COPY of the imported file: the refresh prunes rows and the reset deletes them, so a shared directory would let one section's housekeeping decide whether the next has anything to assert, and a suite whose green depends on the order of its own sections proves nothing. The REFRESH half needs no Jellyfin host — it is driven through the same entry points the task calls (each tracker's dead-item prune, the oshash and sidecar sweeps), with a fixture the harness owns so both directions are provable: a DEAD item loses its search stamp, its retry budget and its burned candidate, while a LIVE item keeps all three **with its value**; the file side keeps the verdict of a subtitle that exists and forgets the one whose file is gone; and the FAIL-SAFE rule is asserted from both sides — an unusable root (missing, and an empty list) prunes NOTHING even with a dead entry planted and waiting, because the dangerous outcome is not a missed prune but a false one on an unmounted volume. The RESET half drives all three scopes with a completed fixture, since a reset tested against a store with nothing to clear proves nothing: `upload` clears every marked row and no embedded row, `download` removes every downloaded sidecar while an observed one survives AND clears the retired `file-retry:` rows (der einzige Zugang dieser Waisen (F-M60)), and `all` runs the file-replacing sequence and must leave an empty but usable store with no side file behind and a backup that opens ON ITS OWN with every row the store held. The RESTORE half mutates the store first — two rows deleted, one planted, counters cleared, the three counters all set to 999 — and then asserts VALUES, not presence, because a restore that keeps the old rows is the exact silent failure the pool caused. It also plants a foreign `-wal` beside the database and requires the restore to clear it, and it carries the negative control that gives the whole path its meaning: an OPEN context keeps reading its own generation across a file swap, which is why the plugin disposes and drops the pooled handles first — with those two calls withheld the store still answers with the old generation.

**The order is asserted against the plugin's own source, not only against the harness.** The sections above drive the sequence as this file writes it, so they would stay green if the reset code changed its order. A separate check therefore reads the source and requires it: in `Restore`, the side files are cleared and the pooled handles dropped BEFORE the backup is copied over; in the `all` scope of `Reset`, the checkpoint precedes the backup and the backup precedes the delete. Two mistakes are invisible in behaviour and would otherwise pass — a backup taken without folding the journal looks valid and is missing the recent writes, and a restore that leaves the old `-wal` in place lets SQLite replay foreign pages over the restored file.

**Isolation is also proven, and the guard is tested where it is actually reached.** A check plants the old document store BACK beside the populated SQLite file — the case a botched upgrade leaves behind, and the one the rename in the import path never exercises — and requires the store's per-area counts to be untouched and the old file to still be in place. Because a check that cannot fail is not a check: the source file is located by walking up from the working directory and its absence is a FAILURE, not a skip. A fixed relative path silently pointed nowhere once and the section reported a skip that read like a pass — the measured lesson being that a suite's reachability check belongs to the suite.

**Planted and confirmed RED, one defect per assertion group:** the prune ignoring liveness (the LIVE item's stamp is lost); the oshash fail-safe removed (a dead entry disappears under an unusable root); the restore clearing the side files AFTER the copy, and not clearing them at all; the all-scope reset withholding the pooled-handle drop, and withholding the checkpoint before the backup; and the import's short-circuit removed, which re-imports over a populated store. All confirmed RED, and the harness restores its own baseline to green afterwards. (F-M327)

**T135:** The auto-sync is a worker with a contract — subtitle in, status and output back — and the download run keeps the files and the counters.

**T105:** With a deferred fire pending, the direction's worker row is painted yellow (`defer`) even when its last run ended `ok`, and it returns to green once the fire is consumed; a red, grey or running row is left untouched, and the stored outcome underneath is unchanged.

**T199:** The dry run is asserted against the QUEUE, per direction, and the Auto-Sync row against its own start.

**T104:** The upload binding binds the SCHEDULED run only: with upload switched off no anchor is armed, while BOTH manual routes still work — the dashboard button and the job's own endpoint — neither of which carries an upload gate any more.

**T200:** The dry run is exempt from the DOWNLOAD brake and NOT from the search one, and the two halves are asserted by INDENTATION inside the probe: the guard sits one level above the download read and exactly level with the search read, which is precisely the claim.

**T89:** An untagged track is resolved and the found language written back (F-M261)
**T80:** A container rewrite is reported at `Normal`, the per-track detail at `Verbose` (F-M262)
**T81:** A dry run suppresses the container rewrite for both directions (F-M263)

**T83:** The stored last-run rows survive a restart and are read from the database (F-M265)
**T82:** A media file is rewritten at most once (F-M264)
**T84:** The sweep removes observations but never a verdict (F-M266)
**T85:** The waiting row reports the cycle's fate in the right colour (F-M267/F-M268)
**T86:** The refresh detail line names a rebuild fallback without turning the light red (F-M269)
**T87:** The configuration page's structure and wiring hold without a host (F-M270–F-M273)
**T88:** An unlabelled sidecar is detected and renamed, and a taken combination takes the next slot (F-M278)

**T119:** The fit is scored per cue against a **planted** truth on a file the operator has confirmed as good: staircases are planted (a three-step staircase, a fine multi-step staircase in the opposite direction, a reversal, and an untouched control) and every applied correction must land within the tolerance of the planted value.

**T120:** Real material is scored against the **audio**, never against another subtitle (F-M305), and the run is repeated on its own output to assert idempotence in the same test — the timeline must be unchanged on the second pass.

**T121:** The two defects of F-M307 are regression cases and must fail loudly if they return: **(a)** a shifted cue window that falls outside the audio scores 0 and is **excluded** — the fit must never return a boundary-runner shift on cues that were not moved (the clipped behaviour returned shifts at the search limit); **(b)** the before/after report is scored with the **real cue ends**, asserted against a known value, because a report over one frame at the cue start silently makes the deploy rule revert good files.
**T122:** The streaming decoder, asserted as two ABSENCES on the plugin source, because neither shows up in a log line and both would return silently: den materialisierenden Dekoder gibt es nicht (kein `ms.ToArray` and no `new float[…]` on the audio path — it held a 514 MB `byte[]` and a 514 MB `float[]` at once and the OOM killer took the 140-minute files) while the streaming entry point exists; and **nothing persists the level curve** — no file, no cache, no database column, and no curve-shaped field in the data model.
**T123:** The statistics table renders one ROW per counter with the label and its number in two bounded columns, and every row names the direction its count belongs to where the data is per-direction.
**T124:** The fit's logging split, asserted on the source: the run START line names the fit switch (`audio fit=`), the run DONE line carries the fit counter (`fitted to audio`), and the per-file track line is emitted only under the fit switch and only at Verbose.
**T125:** The alignment time is credited against the transfer pacing, with a floor at zero, driven against the real limiter: an unbooked credit returns the jittered pause; a credit above the pause lands on exactly 0 and never negative (asserted at two rates, because one base value would make the bound look like an artefact); a partial credit pulls the pause under the ceiling that credit implies; the credit is spent once and not carried; zero and negative bookings are discarded, asserted through the pause because a negative booking would ADD to it.
**T126:** The seeder's language-code counter reaches the statistics row and survives all four paths it must cross — row field, writer, reset, API — plus the GUI row that displays it.
**T127:** The loose-subtitle counter reaches the statistics row and the dry run does not rename.
**T154:** The arrival follow-up takes its directions from the follow-up **settings**, never from the
cycle that happened to be running (F-M177, F-M1a). Asserted structurally on
`SubdlEventDispatcher.RunFollowUpRoundsAsync`, because the defect is invisible in the log — the
follow-up looked complete while one direction was simply absent: die beiden Nachlauf-Schalter müssen
derive from die Nachlauf-Runden des Downloads/die Nachlauf-Runden des Uploads and must name no cycle direction, both
legs must be guarded by their own flag, each leg must run its own direction, download must sit
before upload (the F-M177 order), and both switches off must return before the scan.
**Mutation-verified, two plants:** the removed `dir != CycleDirection.DownloadOnly` term (RED by
name in two checks) and a swap of the two legs (RED on the order check). (F-M177) **25 Prüfungen**, Suite `followup`.

**T155:** The upload filename carries the full content MD5, and both the dry run and the real upload
take it from the same helper (F-M30). Asserted structurally on the upload pipeline, because a wrong name is
invisible until somebody reads the file list at SubDL: The neutral name must place `{contentHash}`
in front of `{lang}` in BOTH branches, with `.srt` last, the episode branch must keep imdbId/season/episode, both call sites
must go through the helper, one must feed the early content hash (the hash of the bytes actually sent) and
the other `ContentHashRegistry.ComputeHash(cand.Srt)` (the candidate the dry run advertises), and no
hashless inline name may survive outside the helper. **Mutation-verified, three plants:** re-inlining the
dry run's name (RED on three checks), dropping `{contentHash}` from the episode branch (RED on two),
and substituting a random GUID for the content hash (RED on two — a per-upload id looks like a name
fix and is exactly the fingerprint F-M29 forbids).

**T156:** The upload reports the frame rate code of the item's video (F-M350).
**T157:** A GUI stop cancels the pending fire of the direction it names (F-M351).

**T158:** Off and manual are named on the status line (F-M352).

**T159:** A forced track is recognized by its title when the flag is unset (F-M367).

**T161:** Proves that one archive serves a whole download run — a second pass hits, the per-archive ceiling refuses oversized archives, the entry and byte ceilings evict the oldest, a re-store replaces, an empty body is refused, `Reset` empties entries and counters, and the pipeline resets at both points.
**T162:** Proves the archive key and the disk cache — the entries of one pack share a key, an unusable URL has none, only ZIP bodies are stored, retention expires and a hit restarts it, the ceiling drops the oldest first, and both switches read 0 as off.

**T164:** The season-pack suite asks the run plan without a library: a packable season is worked first and in episode order, the queued work outside it keeps its order, an empty plan leaves the order untouched, a season that is only partly queued is not the season that aired and is therefore not buffered, and the plan asks the same completeness rule as the season index.

**T165:** The language-correction suite asks the shipped gate without a library: a lying tag corrects to the detected language, a tag that holds corrects nothing, a relative of the tag is not corrected, text without a confident verdict corrects nothing, the correction only runs behind the language checkbox, no mismatch reject is written any more while rows from older runs stay readable, the corrected language reaches the candidate the run keeps and the retry path, and the GUI description says what is corrected and where it goes. **19 checks.**

**T166:** Eine vollständige Staffel ist packbar, egal wie viel davon in der Queue liegt (F-M394).

**T167:** Füllmaterial muss hochgeladen sein (F-M395).

**T168:** Kein zweiter Pack mit denselben Mitgliedern, und tote Pack-Zeilen fallen raus (F-M396).

**T169:** Eine `.srt` aus SubDL-Herkunft tötet den Pack (F-M397).

**T170:** Nur die `packs`-Zeile belegt einen Pack (F-M399).

**T171:** Die Sprachzeile stellt den Befund fest (F-M400).

**T172:** „run dry" und eine Schreibweise für den Modus (F-M401–F-M403).

**T173:** Die `packs`-Tabelle hält, was sie verspricht (F-M390).

**T174:** Erst Einzelupload, dann Staffelpack — und im Arrival gar kein Pack (F-M418, F-M420).

**T196:** Der Lauf reiht die Bibliothek mit „nach neuen und aktualisierten Dateien suchen" ein: nur bei gespeicherten Dateien, je Bibliothek einmal, kein vollständiger Scan; der Schalter vor den Quality Gates kann es abschalten. **8 Prüfungen im Lauf, 4 in der Oberfläche.**

**T198:** Ein Aufschub des Seed-Laufs ist ein Termin der Richtung: er ersetzt einen späteren Termin derselben Richtung, schiebt einen früheren nicht nach hinten, steht in der Terminliste, aus der die Statuszeile ihre Zeitpunkte liest, überlebt mit seinem Grund einen Neustart, und die Statuszeile nennt den Grund des Termins statt der Zeile des letzten Laufs; kein Timer im Speicher trägt die Wartezeit, und ein Termin, der gerade verbraucht wird, nennt seinen Grund trotz abgelaufener Frist. Dasselbe gilt für die drei Wartungs-Termine (Database, OSHash, Nachlauf): Sie nennen ihren Grund, ein gewöhnliches Armen löscht den alten Grund, und ein Kurzzeichen ohne Termin wird nicht angezeigt. **29 Prüfungen.**

**T201:** Der HI-Pool ist eine Rangliste, kein Einzelgriff: Er wird abgelaufen, ein Duplikat beendet ihn nicht, ein gespeicherter Kandidat schon; derselbe Ablauf bedient die Variante neben einer gespeicherten Hauptspur und den Rückfall, wenn der reguläre Ablauf ohne Speichern endet, und der Rückfall steht vor der Meldung „nicht verfügbar" und zählt als Speichern der Sprache. Beide Aufrufe hängen am HI-Schalter, eine gespeicherte Datei trägt das Kennzeichen im Namen und `hi = true` im Store, ihre SDH-Zeilen bleiben stehen, und der Ablauf ordnet nach Preis und Reichweite vor dem Textscore. **20 Prüfungen.**

**T211:** Quellenabhängige Vorprüfungen: der SubDL-Schlüssel hängt am SubDL-Download, das Login-Paar am SubDL-Upload, der TMDb-Schlüssel bleibt Pflicht; ein Zyklus ohne SubDL-Download liest die SubDL-Zähler nicht, und eine ausgeschöpfte SubDL-Quota hält keinen Zyklus, der noch tvst hat. **6 Prüfungen**, Suite `endpoints`.

**T212:** Herkunft an jeder Ablage: **jeder** `MarkDownloaded`-Aufruf im Download übergibt `origin:` (gezählt, nicht geschätzt), und die Abschlusszeile nennt `DownloadedWithoutSignificance`. **2 Prüfungen**, Suite `endpoints`.

**T210:** Reihenfolge und Erfolgsbegriff: die Reihenfolge ist eine Einstellung mit Default tvsubtitles.net zuerst, die GUI bietet sie unter Downloads an und speichert die Auswahl, beide Endpunkte werden gefragt und ihre Kandidaten **zusammengeführt** statt ersetzt (F-M494), und eine behaltene Originalfassung zählt bei Auto-Sync nicht als Erfolg, sondern als `DownloadedWithoutSignificance`. **4 Prüfungen**, alle in der Suite `endpoints`.

**T213:** Beide Endpunkte werden gefragt, der zweite bekommt seinen eigenen Versuch (F-M494): die Rangfolge führt mit der Quelle in der eingestellten Reihenfolge vor Pack und Score, ein Abbruch der Korrektur-Suche wird zurückgenommen, solange noch ein Kandidat der anderen Quelle unversucht ist — mitsamt dem Ablehnungszähler dieser Quelle —, und der Abrufpfad prüft den Schalter der Kandidatenquelle unmittelbar vor dem Abruf, über **einen** Helfer (`SourceAllowed`), den Hauptdurchlauf und SDH-Variante gemeinsam benutzen. **3 Prüfungen**, Suite `endpoints`.

**T214:** Der Upload geht die Kette: beide Quellen in der konfigurierten Reihenfolge, kein Einzelziel mehr (`vonZweiter` ist weg, beide Felder können gesetzt sein), der Toggle jeder Quelle wird unmittelbar vor ihrem Start über **einen** Helfer gefragt (überspringen statt abbrechen, mit Logzeile), und das Herkunftstor entscheidet weiter je Quelle — ein verbotenes Ziel wird mit Grund übersprungen, nicht die Kette beendet. **3 Prüfungen**, Suite `endpoints`.

**T215:** Der Trockenlauf verlangt keine Zugangsdaten und spielt die Kette durch: `MissingCredentials()` nennt für einen SubDL-Upload ohne Login-Paar nur noch den TMDb-Schlüssel (nichts abgewiesen), und der tvst-Zweig hat einen eigenen Trockenlauf-Schutz **vor** jedem Aufruf — je Quelle eine eigene Zeile (`[SubDL]`/`[tvsubtitles] DRY-RUN would upload`), Reihenfolge wie die Kette, Toggle vor der Zeile, Ergebnis „übersprungen", kein Senden. **2 Prüfungen**, Suite `endpoints`.

**T216:** Pflicht ist allein der TMDb-Schlüssel (fehlt er, blockiert der Lauf), eine fehlende SubDL-Konfiguration überspringt dagegen die Quelle statt zu blockieren — Upload über den Schalter **und** `IsConfiguredForUpload` mit Logzeile, Download über `IsConfiguredForDownload` vor Suche und Abruf, und der Trockenlauf stellt die Konfigurationsfrage nicht. **2 Prüfungen**, Suite `endpoints`.

**T217:** Die Konfigurationsseite kennzeichnet die drei SubDL-Felder als „Required for SubDL only" und den TMDb-Schlüssel als „Required" (einzige Pflicht) — ohne neue Schalter und ohne Umbenennung bestehender Feld-IDs. **1 Prüfung**, Suite `gui-structure`.

**F-M498 [D]:** Der tvst-Upload leitet den **Rip** aus dem Release-Namen ab und bildet ihn auf die sieben Werte der Seite ab (WEB, HDTV, HEVC, DVDRip, DVDSCR, BDRip, BluRay); Quelle geht vor Codec, und ohne Treffer bleibt das Feld leer (erlaubter Wert). Gemessen im scharfen Lauf am 15.11.2026: alle Folgen `refused: rip must be one of …`, weil `WEB-DL` durchgereicht wurde — die Vorprüfung verwarf den Wert also zu Recht. Das Formularfeld **Hearing impaired** wird jetzt bedient (`hi=1`, am echten `/add.html` abgelesen — ein Kontrollkästchen mit `value="1"`, das unangehakt gar nicht gesendet wird); vorher wurde das Wissen aus der HI-Erkennung für tvst weggeworfen. Autor bleibt auf Anweisung leer. Geprüft von T219.

**T219:** Rip-Zuordnung (WEB-DL → WEB), Aufruf über die Zuordnung, Hearing-impaired-Feld, Fehlertext ohne falschen Parameternamen. **4 Prüfungen**, Suite `endpoints`.

**T220:** Die Upload-Queue fragt je Quelle statt quellenblind (F-M499): der Upload-Zweig des Seeders benutzt die quellenweisen Leser (`ArePositionsTerminalFor`, `CountKnownPairsFor`, `IsSidecarUploadedTo`) und **nicht** mehr die blinden (`ArePositionsTerminal`, `CountKnownPairs`, `IsSidecarUploaded`), und der Helfer läuft nur über Quellen, die eingeschaltet **und** konfiguriert sind — deren Marke liest **eine** Regel (`IsUploadedTo`). **2 Prüfungen**, Suite `endpoints`.

**T221:** Auch der **Kopf** des Upload-Laufs fragt je Quelle (F-M499): die Pipeline liest die Dateimarke nur noch über `IsSubtitlesUploadedFor(…, provider)`, überspringt ein Medium erst, wenn **keine** der eingeschalteten **und** konfigurierten Quellen (`UploadSources()`) offen ist, und ruft die blinde `IsSubtitlesUploaded` dort nicht mehr; das quellenweise Gegenstück der Marke filtert über `IsEmbedTerminalFor` und damit über dieselbe Regel wie der Upload selbst. **2 Prüfungen**, Suite `endpoints`.

**T222:** Das Settle-Urteil „schon hochgeladen" fragt je Quelle (F-M500): der Quellenbeleg kommt aus `IsContentUploadedFor(…, provider)` (dieselben Marken wie `IsUploadedTo`) und wird an **allen vier** Stellen des Upload-Pfads über EINE Regel (`SettleDuplicateContent`) gestellt; `RecordSkippedContent` schreibt die Marke nur für die Quelle, die den Inhalt hält. Gefahren wird der Umbau gegen einen Wegwerf-Store: derselbe Inhalt bei SubDL erledigt tvsubtitles **nicht**, zweimal bei derselben Quelle bleibt blockiert (und schreibt keine Ablehnung), ohne Quellenbeleg bleibt die Ablehnung quellenneutral, und das Herkunftstor gilt in allen drei Fällen: unbekannte bzw. externe Herkunft darf zu **beiden** Quellen (Kettenreihenfolge, je Quelle eigene Marke, beide Marken nebeneinander auf einer Zeile), `origin=subdl` geht **nirgends** hoch (`origin:subdl — downloaded there, never uploaded`), `origin=tvsubtitles` ebenso wenig; und eine Spur, deren Inhalt als Download bekannt ist, ist für **beide** Quellen erledigt (Negativkontrolle: ohne bekannte Herkunft ist sie es nicht). **10 Prüfungen**, Suite `endpoints`.

**T224:** Die Sprachprüfung des tvst-Uploads läuft über die tvst-eigene Brücke (F-M502): die vier Abweichungen der Seite werden in **beiden** Schreibweisen abgebildet (`el`/`ell`/`gre` → `gr`, `cs`/`ces`/`cze` → `cz`, `zh`/`zho`/`chi` → `cn`, `ja`/`jpn` → `jp`), die übrigen Seiten-Sprachen aus ihren ISO-639-2-Formen (`deu`/`ger`, `fra`/`fre`, `spa`, `pob`, `ukr`, `nld`/`dut`, `ron`/`rum` …), die Kürzel der Seite selbst bleiben sich selbst gleich, Groß-/Kleinschreibung und Leerraum zählen nicht, Region und Skript fallen auf den Grundcode (`zh-Hans` → `cn`, `ja-JP` → `jp`, `es-419` → `es`) — **außer** `pt-BR`/`pt_BR`, das brasilianisches Portugiesisch (`br`) ist, während `pt` und `pt-PT` bei `pt` bleiben; ein unbekannter Code liefert `null` statt einer Vermutung und `BuildUpload` lehnt ihn mit **einer** Zeile ab, die die Kürzel der Seite nennt (ein ISO-Code wird dagegen **nicht** mehr abgelehnt), das Feld `lang` trägt danach das Seitenkürzel, `LanguageName`/`Iso3` kommen aus der Tabelle, und der Dateiname des Uploads behält seine ISO3-Endung (`… 2x24.gre.srt`) im Multipart-Rumpf. **9 Prüfungen**, alle in der Suite `tvsubtitles`.

**T225:** Der Default der beiden tvst-Richtungen ist gegenläufig (F-M503): ohne gesetzten Schalter ist der tvst-**Download** an — auch wenn der Alt-Schalter `DownloadEnabled` aus ist —, ein explizit gespeichertes `false` heisst weiter AUS und wird von keinem Default überschrieben, und ohne gesetzten tvst-**Upload**-Schalter bleibt der Upload aus, auch wenn `UploadEnabled` an ist. **3 Prüfungen**, alle in der Suite `offstate`; die Folgen für den Katalog hält dieselbe Suite `endpoints` fest (der ungesetzte tvst-Download zieht den zweiten Endpunkt herein, ein gesetztes `false` hält ihn draußen, „alles aus" verlangt beide Download-Schalter).

**T229:** Die dritte Quelle ist ein Anbieter, und die Medienart entscheidet, welche Seite gefragt wird (F-M507): ohne gesetzten Schalter ist der moviesubtitles-**Download** an und der **Upload** aus, ein gespeichertes `false` bleibt aus und ein `true` schlägt den Alt-Schalter (der für diese Quelle nicht antwortet); `SourceOrder.AppliesTo` sagt SubDL **beides**, tvsubtitles.net **nur Serie**, moviesubtitles.org **nur Film**, und die daraus gebaute Frage-Reihenfolge nennt für eine Serie `tvst, SubDL` und für einen Film `SubDL, moviesubtitles` — die jeweils andere Seite steht in **keiner** der beiden Reihenfolgen; ein ausgeschalteter Schalter lässt genau SubDL übrig; `moviesubtitles` ist eine Herkunft wie die beiden anderen (Name/Parse hin und zurück, eine **bekannte** Herkunft sperrt den Upload bei allen drei Zielen, unbekannte bleibt erlaubt); die Quelle hat **keinen Upload-Weg**: `HasUploadPath` ist für sie falsch, die Kettenreihenfolge des Uploads wird darauf geschnitten und nennt sie nicht einmal als toten Eintrag (Quelltext-Bindung an `.Where(Api.Endpoints.SubtitleEndpointCatalog.HasUploadPath)`); der Lauf schneidet seine Liste über die Medienart, verlangt für Serien Staffel und Folge, holt für Filme den TMDb-Datensatz (`MovieInfoAsync`), löst die Filmnummer über den Store (`MsubMovieId`, beim zweiten Mal **gelesen** statt gesucht — kein zweiter Netzzugriff) und übergibt Titel, Jahr und Nummer; der Endpunkt der Filmseite holt die Filmseite **einmal** je Film und Lauf (zwei Sprachen, ein GET, Sprache aus der Flagge) und verneint eine Serie (Staffel/Folge gesetzt) sowie eine Sprache ohne Seitenkürzel **ohne einen einzigen Aufruf**; und die Migration wird an einer Datei aus **Schema 13** gefahren: die drei Spalten beider Untertitel-Tabellen und `MsubMovieId` werden beim Öffnen nachgezogen, die Bestandszeile bleibt unverändert lesbar (`MsubMovieId` = `null`), der Marker steht danach auf **14**. **14 Prüfungen**, alle in der Suite `endpoints`; der Default der beiden Schalter hält dieselbe Suite `offstate` fest.

**T230:** Der Upload-Weg der Film-Adresse ist gebaut, verdrahtet und gesperrt (F-M508), und die Reihenfolge ordnet zwei Quellen statt drei Adressen (F-M509). Geprüft wird am Quelltext und am Store: die Kette versendet je Quelle über `SendToSourceAsync` (SubDL, Serien-Adresse, Film-Adresse), sammelt ein Ergebnis je Quelle und schreibt je Quelle **ihre** Marke und **ihren** Zeitstempel (`MarkOf` gibt `(null, null)` für eine Quelle, die nicht genommen hat); die Film-Adresse bekommt `Season: 0`, `Episode: 0`, `MsubMovieId` aus dem Auflösungsergebnis und `MovieYear` aus TMDb, lehnt ein Serien-Ticket ab (`film-source-films-only`) und wird mit dem Trockenlauf-Schalter des Laufs gebaut; **kein** Schalter wird in der Kette direkt gelesen (nur über `SourceAllowed`); das Gate kennt die dritte Adresse (`MovieSubtitles`, `AllowedCount`, `already-uploaded:subdl+tvsubtitles+moviesubtitles`) und die Herkunft sperrt sie mit; eine Marke in den Spalten der Film-Adresse hält **nur** für sie (weder für SubDL noch für tvsubtitles.net), und `RecordSkippedContent` setzt bei ihr den Zeitstempel allein. Zur Reihenfolge: `DownloadSourceOrder` hat **zwei** Werte, `ForMedia` nennt für eine Serie `tvst, SubDL` und für einen Film `moviesubtitles, SubDL` (bei „SubDL zuerst" umgekehrt), `tvst zuerst` führt die Film-Adresse also **vor** die SubDL-Suche, und die Upload-Kette enthält die Film-Adresse nur für Filme — bei Vorgabe bleibt ihr Schalter aus. **8 Prüfungen**, alle in der Suite `endpoints`.

**T226:** Die Quellen werden in der eingestellten Reihenfolge **befragt** (nicht nur sortiert), eine leere erste Quelle beendet den Vorgang nicht mehr, und die Quellen sind eine Liste statt eines Paars (F-M504): die Frage-Reihenfolge nennt **jeden** Anbieter des Enums und stellt die konfigurierte Quelle voran (beide Einstellungen, `Leading` inklusive); die Anzahl der befragten Quellen ist die Anzahl der **freigeschalteten**; **der Ablauf wird aus denselben Bausteinen nachgebaut, die der Lauf benutzt** (`SourceOrder.ForOrder` plus `SourceOrder.MainSearch` — die Konstante, auf die die Pipeline verzweigt) und verglichen: bei `TvSubtitlesFirst` steht die tvst-Abfrage **vor** der SubDL-Suche, bei `SubdlFirst` **dahinter**, jeweils als erstes Glied der Liste; am Quelltext liegt der SubDL-Aufruf (`RunV2SearchAsync`) in der lokalen Funktion, die der Zweig „diese Quelle ist die Hauptsuche" aufruft, und **kein** Aufruf steht hinter der Schleife; im Rumpf der Schleife gibt es **kein** `break` und keine Ausstiegszeile, beide Zweige (Hauptsuche, Zusatzquelle) sind vorhanden und **kein** `ForeignCandidatesAsync` steht hinter ihr — keine Quelle wird übersprungen, kein vorzeitiger Ausstieg; der Ausstieg bei leerer Antwort steht **hinter** allen Fragen (auch hinter der SubDL-Suche); der Lauf baut seine Liste aus `SourceOrder.ForOrder(DownloadOrder)` **und** dem Schalter **je Quelle** (keine `zusatzQuellen`-Restliste mehr, kein fest verdrahteter Anbieter); sind alle gefragten Quellen leer, bleibt es beim bisherigen Ergebnis (`NotAvailable`, `SkippedNoCandidates`, Rückkehr) und die Zeile nennt die gefragten Quellen statt „SubDL" allein; `ForeignCandidatesAsync` hat **genau eine** Aufrufstelle (kein Doppelabruf je Quelle und Sprache — die Sprachschleife liest nur das Wörterbuch); und Suche wie Abruf holen den Endpunkt **jeder** Quelle über `EndpointFor` aus dem Katalog (kein `_tvDownload` mehr). **10 Prüfungen**, alle in der Suite `endpoints`.

**T227:** Das Release-Feld des tvst-Uploads ist die Release-Gruppe, nicht der Dateiname (F-M505): für den echten Bibliotheksnamen `The Middle.S01E01.MULTI.1080p.WEB-DL.H265-FTMVHD` liefert `SeasonPack.ReleaseGroup` die Gruppe `FTMVHD` — der Name mit Punkten bleibt Name, die Gruppe hängt hinter dem Bindestrich —, und am Quelltext trägt das tvst-Ticket (von `new Api.Endpoints.UploadTicket(` bis `TvstShowId: show.ShowId);`) `SeasonPack.ReleaseGroup(releaseName) ?? string.Empty` und **kein** `GetFileNameWithoutExtension`: die Seite schneidet ein Release am Bindestrich ab, ein Dateiname käme dort als `The Middle.S01E21.MULTI.1080p.WEB-DL` an und die Gruppe ginge verloren; ein leeres Feld ist erlaubt und die Mehrheit der Seite. **2 Prüfungen**, alle in der Suite `seasonpack`.

**T228:** Der Client der dritten Quelle ist gegen die aufgenommenen Seiten fahrbar und hält seine eigenen Tabellen (F-M506): 13 Sprachkürzel mit Anzeigename und Dateikürzel, von denen **keines** stillschweigend zu Englisch wird — die ISO-Formen der 13 werden abgebildet (`eng spa fra/fre deu/ger pob rus uk/ukr ita el/ell/gre ara hun pol tur`), `pt-BR`/`pt_BR` ist `br`, und `pt`, `por`, `nl`/`dut`, `sv`, `da`, `fi`, `ko`, `zh`/`zho`/`chi`, `ja`/`jpn`, `bg`, `cs`/`ces`/`cze`, `ro`/`rum`, `xx`, leer und `null` liefern **`null`**; die Rip-Tabelle hat die neun Werte des Auswahlfelds in dessen Reihenfolge und ist **nicht** die von tvst (dort `HDTV`/`HEVC`/`DVDSCR`, hier `WEB-DL`/`WEBRip`/`BrRip`/`HDRip`); `WEB-DL` bleibt `WEB-DL`, `BluRay.x265` wird `BluRay`, aber `x265`/`HEVC`/`H.265` **allein** ergeben den leeren Wert (die Seite kennt keinen Codec), und `HDTV` ebenso. Der Auflöser findet `Dune` (1984) und `Dune: Part One` (2021) über den TMDb-Titel, liefert bei **falschem Jahr** nichts, findet die invertierte Schreibweise (`The Opposite of Sex` → `Opposite of Sex, The`), trifft **keinen** Teiltreffer und rät bei zwei gleichen Kandidaten **nicht**. Geparst werden alle drei Listensichten (Filmliste 487 Zeilen, Uploadliste 492, Suche 5 trotz HTTP 500) samt wiederholtem Jahr im Namen, die Filmseite mit 54 Einträgen/12 Abschnitten und 51/13 (Sprache aus der Flagge, Abschnittsüberschrift als Gegenprobe, freie Rip-Werte bleiben), die doppelte Jahresklammer (`O Beautiful (2002) (2002)`), der Film **ohne** Einträge, die Eintragsseite (Rip, Release, Dateiname, `parts`) und die Müllseite einer unbekannten Id (**`null`**). Der Download folgt der **relativen** 302, behält Klammern (`ru(2).zip`), nimmt aus dem Zip die Datei mit dem Sprachkürzel und kennzeichnet Mehrteiler, verweigert `files/` und gibt nackte `.srt`-Bytes unverändert zurück. Der Upload geht in der Reihenfolge des Formulars (`tvid lang rip release author UFile UFile2 UFile3 comments go`) mit `go=1`, ohne `author` wenn leer, mit der **Release-Gruppe** statt des Dateinamens, mit Kennungen und Client im `comments` und mit `UFile2`/`UFile3` nur wenn gefüllt; `tvid=0`, eine unbekannte Sprache, ein Rip außerhalb der Tabelle, fehlende Ids, eine leere Datei, eine falsche Endung und ein dritter Zusatz werden **ohne** eine einzige Anfrage abgelehnt; der Trockenlauf baut den Rumpf und sendet nichts, der scharfe Lauf postet multipart an `add.php` mit dem Formular als Referer. **84 Prüfungen**, alle in der Suite `moviesubtitles`. **Etappe 2b (F-M508):** die vierte Hälfte fährt den UPLOAD-ENDPUNKT gegen einen Zähl-Handler — ein Ticket mit Staffel/Folge, eines mit Staffelpack, eine fremde Sprache und eine fehlende Nummer ohne Jahr werden abgelehnt, ohne dass eine Anfrage entsteht; der Trockenlauf meldet den Rumpf und sendet nichts; der scharfe Weg (gegen den Handler, nicht gegen die Seite) schickt `tvid lang rip release UFile comments go` in der Reihenfolge des Formulars, lässt `author` weg, trägt in `release` die Gruppe `FTMVHD` statt des Dateinamens, meldet 2xx als „angenommen, Id unbekannt" (`Id = null`) und ein 500 als Ablehnung mit dem Text der Seite.

**T231:** Die Logzeilen des ersten Film-Laufs nennen die Adresse, die gefragt wurde, und die Quelle, die gewonnen hat (F-M510): `SourceOrder.FamilyPage` liefert für eine Serie tvsubtitles.net und für einen Film moviesubtitles.org, und `Provenance.Name` macht daraus die Etiketten `tvsubtitles`/`moviesubtitles` — das Etikett der Sammelzeile ist **kein** fester Text mehr (kein `tvsubtitles-D`-Literal im Quelltext der Pipeline, dafür der Platzhalter `{Src}`), die Adresse steht als **erstes** Argument vor Datei, Sprache und Zählung, und die Aussage der Zeile bleibt (Kandidaten je gefragter Quelle aus `quellenReihenfolge`, plus die Frage-Reihenfolge); das Etikett ist genau die Adresse, die `ForMedia` für diese Medienart an die erste Stelle setzt. Die DRY-RUN-Zeile trägt die Herkunft als erstes Feld der Klammer (`({Source}, score {Score}, hi={Hi})`), die aus `cand.Provider` kommt und **im selben** Aufruf steht; die alte Form ohne Herkunft ist verschwunden, `score` und `hi` behalten Platz und Reihenfolge. Die geteilten Zeilen beider Richtungen nennen die Quelle aus der Liste (`Provenance.Name(quelle)` in der Zählung und im Upload-Trockenlauf), während ein festes Etikett nur im **eigenen** Endpunkt-Zweig steht (die Film-Adresse in ihrem Film-Zweig, keine `tvsubtitles`-Zeile im Film-Pfad des Downloads). **8 Prüfungen**, alle in der Suite `endpoints`.

**T232:** Ein abschließendes Klammer-Jahr wird vor dem TMDb-Titel-Vergleich abgetrennt — und nur dort (F-M510): `ComparisonTitle` macht aus `Oppenheimer (2023)` ein `Oppenheimer`, aus `The Office (2019–2023)` (und der Schreibweise mit `-`) ein `The Office` und aus `Dune [2023]` ein `Dune`; `(Ghost) Riders In the Sky (American Outlaws_ Live …)` bleibt **unverändert** (nur ein **abschließendes** Klammer-Jahr zählt, ein Klammer-Titel wird nicht zerschnitten), `Blade Runner 2049` ebenso (Jahr im Titel, keine Klammer), und die Verdopplung `O Beautiful (2002) (2002)` verliert genau EIN Jahr. Gegen einen Handler, der die gemessene TMDb-Antwort nachbildet (der reine Titel trifft, der Klammer-Text nicht), findet der **strenge** Titel+Jahr-Test den Film (tmdb 872585, tt15398776, keine Serie), die Suchanfrage trug `query=Oppenheimer` und das Jahr ging **nie** hinaus (genau eine Suche, kein zweiter Versuch), und der gemessene Store-Stand (Jellyfins falsche `tmdb 1422`) wird **korrigiert** statt geduldet. **9 Prüfungen**, alle in der Suite `moviesubtitles`.

**T233:** Eine rohe Qualität aus dem Release-Namen kommt durch den Film-Endpunkt und landet als Rip-Wert der Seite (F-M511): sechs Fälle — `web`, `1080p BluRay x264`, `WEB-DL`, `HDTV`, `x265` und leer — gehen als Ticket durch `MovieSubtitlesUploadEndpoint`; jeder wird **angenommen** (`Ok`, Trockenlauf, kein Ablehnungsgrund — der gemessene `Ok=false`-Fall ist weg), und die Nutzlast jedes rohen Werts ist **byte-gleich** der Nutzlast seines zugeordneten Werts (`web`=`WEB`, `1080p BluRay x264`=`BluRay`, `WEB-DL`=`WEB-DL`, `HDTV`=leer, `x265`=leer, leer=leer): der Trockenlauf zeigt denselben Rumpf, der im Ernstfall geht. Gegen einen Zähl-Handler belegt der scharfe Weg den Rumpf: `name="rip"` trägt bei `web` `WEB` (und kein `WEB-…`), bei `HDTV` den **leeren** Wert, bei `WEB-DL` `WEB-DL`; die Länge des gesendeten Rumpfs ist die Länge, die der Trockenlauf für dasselbe Ticket nannte, und `TvSubtitlesClient.RipFromQuality("WEB-DL")` bleibt `WEB` — die fremde Zuordnung wird nicht geborgt. **5 Prüfungen**, alle in der Suite `moviesubtitles`.

**T234:** Der Einreiher nimmt die Medienart aus dem Store, Jellyfins Typ ist der Rückfall (F-M512): an einer echten Store-Datei liefert `ContentHashRegistry.MediaKindIsSeries` für die Zeile einer Serie `true`, für die aufgelöste Zeile eines Films `false` und für eine Zeile ohne Kennungen/Staffel/Episode `null`; ohne Zeile, ohne Hash und mit leerem Hash `null` (dann entscheidet Jellyfin). Mit dieser Auskunft schneidet die Adressenliste der Familie genau wie im Einreiher (`SourceOrder.AppliesTo`): Store=Serie lässt **nur** tvsubtitles.net stehen (moviesubtitles.org fällt weg), Store=Film **nur** moviesubtitles.org, und bei `null` bleiben **beide** Adressen stehen — SubDL ist in allen drei Fällen dabei, „lieber ein Item zu viel". Am Quelltext: der Einreiher ruft `MediaKindFor(item, mediaPath)` (die alte Zeile `isSeries = item is Episode …` steht dort nicht mehr), der Helfer fragt `StoredMediaKind` **vor** dem Item-Typ-Rückfall, die Store-Frage ist `_db.Media.FindById(mediaHash)` über den Primärschlüssel, und der Helfer-Block enthält weder `Async(` noch `HttpClient` noch `_tmdb` — kein Netz, kein TMDb-Request im Scan. **7 Prüfungen**, alle in der Suite `endpoints`.

**T235:** Ein fehlender Film wird angelegt, die Nummer wird nachgesehen statt geraten, und der Doppeleintrag-Schutz entscheidet vorher (F-M513): `BuildAddMovie` trägt `name`, `year`, `go=1` in der Reihenfolge des Formulars (getrimmt), der Rumpf enthält genau diese drei Teile in dieser Reihenfolge und weder `filename=` noch `tvid`; ein leerer Name, 1887, `MaxAddMovieYear + 1` werden **ohne eine einzige Anfrage** abgewiesen, die Grenzen selbst (1888, laufendes Jahr+3) und der einbuchstabige Titel `X` gehen durch; der Trockenlauf meldet `dry-run` mit der Rumpfgröße des Ernstfalls und sendet nichts (0 POST, 0 GET); der Ernstfall gegen einen Zähl-Handler postet multipart an `/add-movie.php` mit dem Formular als Referer, und die Id kommt aus der **Nachsuche** (`4242`), nicht aus der Antwort; ein 500 ist `Ok=false` mit `Reason = "HTTP 500"`, dem Text der Seite und **ohne** Nachsuche; ein angenommener POST ohne Treffer in der Nachsuche ergibt `created-id-unknown` mit dem Antworttext und **ohne** Id, wobei alle drei Schreibweisen gefragt wurden (`The Matrix`, `Matrix`, `Matrix, The`). Der Schutz in drei Fällen: gleiches Jahr → `take` mit der vorhandenen Nummer, anderes Jahr → `refuse` mit `FoundYear` 1999, kein Treffer → `create`; zwei Zeilen mit demselben Titel und Jahr sind `refuse` mit `Hits = 2`; die Netz-Fassung fragt den ersten Begriff (der Teilstring-Filter findet ihn nicht), nimmt den Kern und **hört beim Treffer auf** (2 GET, kein POST), und eine unlesbare Suche ergibt `unclear` mit `Answers = 0` statt „gibt es nicht". Am Quelltext: die anlegende Fassung steht **nur** in `UploadPipeline` (der Download-Pfad ruft weiter `ResolveForMediaAsync` mit `_config.DownloadDryRun`), die feste Zeile `neu angelegt — Id {Id}` steht im Auflöser, und die neue Nummer geht über `Persist` in `MsubMovieId`. **15 Prüfungen**, alle in der Suite `moviesubtitles`.

**T236:** Eine Zeile ohne Geschäftsschlüssel wird nicht angehängt, und ein abgewiesenes Anhängen macht den Store nicht schreibunfähig (F-M514) — an echten Store-Dateien gefahren, nicht nur am Quelltext: der Aufruf des Vorfalls (`Registry.MarkDownloaded(contentHash, mediaHash: null, …)`, also `MarkSidecar` → `EnsureMedia(null)`) **wirft nicht**, legt **keine** Medienzeile an (Zeilenstand unverändert, keine Zeile mit leerem `Id`) und schreibt die Seitenträger-Zeile mit leerem Medien-Hash; `Media.Upsert` mit `Id = null` **und** mit `Id = ""` wird abgewiesen, ohne zu werfen und ohne Zeile; danach schreibt der Store weiter — die Seeder- und die Upload-Zeile der `WorkerRunRegistry` stehen **in der Datei** gelesen als `ok`; ein absichtlich vergifteter Change Tracker (Anhängen einer Medienzeile ohne Id am Set vorbei, wie EF Core 10 ihn hinterlässt) wird beim `SaveChanges` **repariert** (kein Wurf) und die vor dem Speichern gesetzte Änderung überlebt (Detail `ueberlebte die Reparatur` aus der Datei gelesen) — die Reparatur ist also kein stiller Datenverlust; am Quelltext: `EnsureMedia` nimmt `string? mediaHash` und schreibt ohne Hash nichts, das gemeinsame Set weist Schlüssellose in `Upsert`, `Update(IEnumerable)` und `InsertBulk` ab (drei Guard-Stellen), `SaveChanges` ist überschrieben und nennt `Unexpected entry.EntityState` und `ChangeTracker.Clear()`, `MarkSidecar` nimmt `string? mediaHash` und normalisiert auf den leeren Wert, und die Filmzeile behauptet kein Speichern mehr (`ist Film {Id} — {Note}`, alter Wortlaut `gespeichert.` verschwunden). **10 Prüfungen**, alle in der Suite `endpoints`.

**T237:** Die Antwort auf `add-movie.php` wird an den zwei live belegten Markern gelesen, und „existiert schon“ führt zur Suche statt zu einem zweiten Versuch (F-M515) — gefahren an den echten Antworten der Seite: `ClassifyAddMovieAnswer` erkennt `Movie has been added. Thank you.` als `created` und `This Movie already exist` als `exists`, während das unveränderte Formular (`fixtures/add-movie.html`, 8399 Bytes, live die Antwort auf einen POST ohne Anlage) und der leere Text `rejected` ergeben; die Marker-Konstanten lauten wörtlich so, wie die Seite schreibt („exist“, nicht „exists“). Die Fixture der Erfolgs-Antwort (8467 Bytes, `add-movie-added.html`) gegen einen Handler, der den Film in der Suche führt, ergibt `created` mit der Nummer **aus der Nachsuche** (26230) und **genau einem** POST; dieselbe Fixture ohne Treffer ergibt `created-id-unknown` mit `Ok = true`, ohne Id und mit allen drei gefragten Schreibweisen (`The Matrix`, `Matrix`, `Matrix, The`) — der Text der Antwort steht daneben. Die Fixture der Verweigerung (8459 Bytes, `add-movie-exists.html`) mit Treffer ergibt `Ok = false`, `exists`, die vorhandene Nummer (23716) und **keinen zweiten POST**; ohne Treffer ergibt sie `exists-id-not-readable` mit 1 POST und 3 GET — kein Fehler, keine Id, kein Upload. Das unveränderte Formular ergibt `rejected` mit `Reason` beginnend `no creation marker`, **0 GET** (es gibt nichts nachzusehen) und genau einem POST. Am Quelltext: der Client ruft `ClassifyAddMovieAnswer(plain)` und kennt `Rejected` wie `ExistsUnknownId`, der Auflöser führt `MsubMovieStatus.ExistsFound` und `ExistsWithoutId` und nennt den Satz der Seite über `MovieSubtitlesClient.AddMovieExistsMarker`. Beide Fixtures stehen mit ihrer Prüfsumme in `fixtures/MANIFEST.sha256`. **6 Prüfungen**, alle in der Suite `moviesubtitles`.

**T238:** Die Anlage einer Serie ist gegen das echte Formular und die echte Antwort gefahren (F-M516), in der Suite `tvsubtitles`: das Formular (Fixture vom 16.11.2026) ist `multipart/form-data`, postet an `/add-tvshow1.php`, und die aus dem HTML gelesenen Feldnamen sind **genau** die des Rumpfes (`name`, `year_s`, `seasons`, `go` — keines erfunden, keines fehlt), mit `go` als verstecktem `1` und einem Absende-Knopf ohne `name`; die Bestätigung „TV-Show has been added." wird auf ihrer Seite erkannt und auf dem unveränderten Formular **nicht**; die gemessene Erfolgsantwort (302 auf `?done=yes`) ist `created`, ebenso dieselbe Antwort als Text hinter der Weiterleitung, während ein 200 mit unverändertem Formular, ein leerer Text, eine 302 ohne Kennzeichen und eine fehlende Antwort `unclear` bleiben und ein 500/403 `refused` ist — **ein 2xx allein ist keine Anlage mehr**; der Trockenlauf der Anlage baut den Rumpf, meldet `dry-run` und sendet nichts (0 Anfragen), der Ernstfall gegen einen Zähl-Handler postet multipart an `/add-tvshow1.php` mit dem Formular als Referer, der Rumpf hat **genau vier** Teile, keinen Datei-Teil und dieselbe Bytezahl wie der Trockenlauf, und die Antwort wird als `created` samt Weiterleitungsziel gelesen. Die Nummer: die echte Suchseite ergibt `4242 → Weissensee` (ohne Jahreszeitraum) und löst eng auf, „No results found" ergibt keinen Treffer, und die weiche Suche (`Pagan Peak` → *Twin Peaks*, *Peaky Blinders*) löst **nicht** auf; `SettleStatus` liefert für gefunden+bestätigt `created`, für gefunden+unbestätigt `existed`, für bestätigt ohne Nummer `created-id-unknown`, für abgelehnt ohne Nummer `refused` und sonst `unclear`. **15 Prüfungen**, alle in der Suite `tvsubtitles` (50 → 65).

**T239:** Die gefundene Filmnummer geht auch **ohne Medien-Hash** in die Medienzeile, die ihr **Pfad** adressiert (F-M517), gefahren gegen einen Wegwerf-Store in der Suite `moviesubtitles`: eine Zeile, die über ihren Hash angelegt wurde und den Pfad trägt, bekommt über `ResolveForMediaAsync(registry, mediaHash: null, …, mediaPath: …)` die gefundene Nummer **13392** in `MsubMovieId` — der Status ist `found`, die **Zeilenzahl bleibt gleich** (1 → 1), und `MsubMovieIdAtPath` liest sie danach; der zweite Aufruf sucht **nicht** erneut (`stored`, genau **ein** GET). Ohne Hash **und** ohne Pfad-Zeile bleibt es beim ehrlichen Ergebnis: die Nummer ist da (`found`, 13392), der Store hat danach **0** Zeilen, und der Vermerk ist `MediaNumberNote.NotStored` („gilt nur für diesen Lauf"). Ein Pfad, den **zwei** Zeilen tragen, ist keine Adresse: `SetMsubMovieId` antwortet `None`, beide Zeilen bleiben ohne `MsubMovieId`, nichts wird gewählt. **4 Prüfungen**, alle in der Suite `moviesubtitles` (119 → 123).

**T240:** Der Serienfall hat dasselbe Loch und denselben Weg (F-M517), in der Suite `tvsubtitles`: an einer echten Store-Datei bekommt die Zeile einer Folge (`TvstShowId` leer, Pfad gesetzt) über `TvstShowResolver.ResolveForMediaAsync(registry, mediaHash: null, …, mediaPath: …)` die Nummer **512** der Serie — Status `found`, `TvstShowId = "512"`, **Zeilenzahl unverändert**, `TvstShowIdAtPath` liest sie, und der zweite Aufruf liest statt zu suchen (`stored`, genau **eine** Anfrage an die Seitenliste, die des ersten Aufrufs). Ohne Hash und ohne Pfad-Zeile bleibt der Store **leer** (0 Zeilen), obwohl die Nummer gefunden wurde, und der Vermerk ist `MediaNumberNote.NotStored`. **3 Prüfungen**, alle in der Suite `tvsubtitles` (65 → 68).

**F-M523 [D]:** **Die Seriennummer von tvsubtitles.net wird auch beim Download gespeichert.** Befund 17.11.2026 („Die Serien und movieid von tvst speichern wir?"): die Felder gibt es seit Schema 13 (`MediaEntity.TvstShowId`, `MediaEntity.MsubMovieId`), geschrieben wurde aber nur im **Upload**-Weg (`TvstShowResolver`). Der Download löste die Nummer bei jeder Suche neu über die Serienliste auf (`ShowsAsync` + `ResolveShow`) und warf sie weg — deshalb stand 512 für The Middle aus dem Staffel-Upload im Store, MobLand und Stranger Things aus dem Download standen nicht drin, und jeder Lauf holte die Liste erneut (genau das, was F-M501 verhindern sollte). Neu: der Endpunkt legt die aufgelöste Nummer in `TvSubtitlesClient.LastResolvedShowId` ab (am Anfang **jeder** Suche zurückgesetzt, damit nie die Nummer des vorigen Titels weiterlebt), die Download-Pipeline schreibt sie über **denselben** Weg wie die Filmnummer (`Registry.SetTvstShowId`, F-M517: Hash zuerst, Pfad als zweite Adresse, bei Mehrdeutigkeit nichts) und protokolliert `„<Titel>" ist Serie <Id> — <wie>`. Drei Regeln: ein **Probelauf** schreibt nichts (F-M22), eine **schon gespeicherte** Nummer bleibt unangetastet (sie wurde geprüft, als sie geschrieben wurde), und **angelegt** wird beim Download nichts — die Anlage bleibt Sache des Upload-Wegs (F-M513).

**F-M522 [D]:** **Die Serienliste wird einmal je Lauf geholt, nicht einmal je Suche.** Befund 17.11.2026 („Wieso ruft der downloader add html auf?"): `https://www.tvsubtitles.net/add.html` ist die einzige Seite der Site, auf der Serien mit ihrer Nummer stehen — der Download braucht diese Nummer für `tvshow-<id>-<staffel>.html`. Ein Lauf auf prod fragte die Seite **53 mal** für eine Liste, die sich während des Laufs nicht ändern kann. Neu: `TvSubtitlesClient.ShowsAsync` hält die Liste im Client; ein Client lebt **einen** Lauf, also ist „gemerkt" gleich „für diesen Lauf" und nichts überlebt ihn. Zwei Regeln halten das ehrlich: eine **leere** Antwort wird **nie** gespeichert (eine abgeschnittene Seite ergäbe sonst einen ganzen Lauf lang „keine solche Serie"), und ein **echtes** Anlegen verwirft den Speicher (das ist das einzige Ereignis im Lauf, das die Liste ändert; der Aufrufer sucht direkt danach die neue Nummer). Ein Probelauf kommt an diese Stelle nicht, die Regel „ein Probelauf ändert nichts" bleibt also unberührt.

**F-M521 [D]:** **Beide Adressen der tvst-Familie schreiben ihre Anfragen ins Log — in beiden Richtungen.** Anweisung 17.11.2026 („Und logging für uploads?"): SubDL protokollierte seit jeher `API call`/`API response`, tvsubtitles.net und moviesubtitles.org **nichts**. Damit war „tvst wurde gefragt" eine Behauptung ohne Beleg — die Zeile `0 tvsubtitles candidate(s)` sah genauso aus wie ein Lauf, in dem die Adresse nie angesprochen wurde (gemessen auf prod: „asked: tvsubtitles, subdl" ohne eine einzige Zeile dieser Quelle). Neu: **zwei Zeilen je Anfrage auf Debug** (`API call GET /search.php`, `API response GET /search.php → 200`), nach der bestehenden Regel F-M24a — nie auf Verbose, im Normalbetrieb also **null** zusätzliche Zeilen. Die Zeilen nennen **Methode und Pfad, nie die Abfrage** (`SiteTrace.Short`), damit weder Suchbegriff noch Kennung in eine Logdatei geraten. Suche, Abruf, Film-/Serienanlage und Hochladen laufen durch dieselben Aufrufstellen, deshalb deckt **eine** Verdrahtung beide Richtungen ab: Download-Pipeline (`_tvClient`, `_msubClient`) und Upload-Pipeline (`_tvstClient`, `_msubClient`) hängen am selben Kanal wie SubDL. Die **Verbose**-Sammelzeile nennt zusätzlich die Kennung: `tvsubtitles (HTTP 200, 0 hit(s))`, bei fehlender Antwort `no answer` — die Zeile, die vorher log.

**F-M520 [D]:** **Die tvst-Familie hat in der Oberfläche EINEN Schalter je Richtung — die Adressen sind Datenfelder, keine Bedienelemente.** Klarstellung 17.11.2026 („Tvst one source two addresses. Weniger in der gui rumlabern bitte. Tmdb decided. Series to movies to…"): Download- und Upload-Tab zeigen je eine Zeile „tvst" mit einem Schalter; die beiden gespeicherten Adressfelder (`TvSubtitlesDownloadEnabled`/`MovieSubtitlesDownloadEnabled`, `…Upload…`) werden beim Speichern **gemeinsam** gesetzt und beim Anzeigen als **wirksamer** Zustand gelesen (an, sobald eine Adresse an ist; beim Download zählt `null` als an). Welche Adresse ein Objekt bekommt, entscheidet **TMDb** — Serie → tvsubtitles.net, Film → moviesubtitles.org (`SourceOrder.FamilyPage`), nicht der Bediener. Die Seite erklärt das in **einem** Satz und nennt in der Reihenfolge-Auswahl „tvst first" statt beider Adressen; die Statusampeln bleiben je Adresse, weil eine antworten kann, während die andere es nicht tut.

**F-M519 [D]:** **Seitenwagen aus dem Originalblock („5xer") werden nie hochgeladen.** Anweisung 17.11.2026 („5xer sidecars bitte generell nicht hochladen"): Dateien mit einer Slot-Nummer **50–99** sind die unsynchronisierten Originale, die eine Korrektur neben ihrem Ergebnis behält (F-M315) — Arbeitsdateien, keine Ware. Sie fallen an der **einen** Stelle heraus, an der Upload-Kandidaten in den Lauf treten (`UploadPipeline.FindLooseSrts`), und die Frage wird **allein am Namen** entschieden (`SidecarNaming.IsOriginalSidecar`, Slot über `SlotOf`) — keine Datei wird dafür geöffnet, und eine Staffel mit 24 Folgen zahlt nichts dafür. Slot **50** zählt mit, obwohl ihn kein Schreiber mehr vergibt: alte Bäume tragen solche Namen, und Originale sind sie trotzdem. Der **korrigierte** Block (Slot 1–49) bleibt Kandidat wie bisher.

**T241:** Eine **echte Staffel** liegt als Ablage im Baum und der tvst-Leser wird daran gemessen (F-M505), in der Suite `tvsubtitles`: `scripts/tests/tvsubtitles/fixtures/season-middle-s01/` enthält die Staffelseite von **The Middle (512)** und alle **24** Folgenseiten, byte-gleich von der Seite (17.11.2026). Die Staffelseite liefert 24 Folgenseiten ohne Dubletten, jede davon liegt im Baum, `ParseSeasons` nennt Staffel 1, und `ParseEpisodeLabel` gibt für jede Seite genau eine Nummer **1–24** (jede einmal). Folge **1x05** trägt **10** Einträge: die Release-Zellen führen die **Gruppen** der Hochlader (`FEVER` ×3, `REWARD` ×1, 5 leer) — der **einzige** Eintrag mit einem Dateinamen ist unser eigener von **vor** F-M505 (`The Middle.S01E05…`), und genau dieser eine ist der Grund, warum die Gruppe gesendet wird; die Rip-Zellen bleiben das Vokabular der Seite (`HDTV` ×8, `WEB` ×1, `DVDRip` ×1), die Sprachen kommen als die Zwei-Buchstaben-Codes der Seite zurück (`fr` ×2, `en` ×1, u. a. `ro`). **8 Prüfungen**, alle in der Suite `tvsubtitles` (68 → 76).

**T246:** Der Download schreibt die tvst-Seriennummer (F-M523), in der Suite `tvsubtitles`: eine Suche über die Seitenabbilder von The Middle meldet `LastResolvedShowId` = **512**; eine folgende Suche nach einem unbekannten Titel setzt sie auf **null** (kein Altlast-Wert); und die Download-Pipeline benutzt zum Schreiben `SetTvstShowId`, liest vorher den gespeicherten Wert über `TvstShowIdAtPath` und ist durch `!_config.DryRun` gedeckt. **3 Prüfungen**, alle in der Suite `tvsubtitles` (79 → 82).

**T245:** Die Serienliste wird je Lauf einmal geholt (F-M522), in der Suite `tvsubtitles`: zwei `ShowsAsync`-Aufrufe ergeben **eine** Anfrage und dieselbe Liste; eine **leere** Antwort wird nicht gemerkt (zwei Aufrufe, zwei Anfragen); nach einem **echten** `AddShowAsync` fällt der Speicher (Liste, POST, Liste = **drei** Anfragen, damit die neue Nummer gesehen wird). **3 Prüfungen**, alle in der Suite `tvsubtitles` (76 → 79).

**T244:** Die tvst-Familie belegt ihre Anfragen (F-M521), in der Suite `endpoints`: die Anfragezeile nennt Methode und Pfad **ohne** Abfrage, die Antwortzeile wiederholt den Pfad mit Kennung, `SiteTrace.Answer` liest sich als `HTTP 200, 3 hit(s)` bzw. `no answer, 0 hit(s)`; beide Clients tragen ein `DebugTrace`-Ereignis und `LastStatus`, und **beide** Pipelines hängen daran (`_tvClient`/`_msubClient` beim Download, `_tvstClient`/`_msubClient` beim Upload). **5 Prüfungen**, alle in der Suite `endpoints` (182 → 187).

**T243:** Die Oberfläche zeigt die tvst-Familie als **eine** Quelle mit **einem** Schalter je Richtung (F-M520), in der Suite `gui-structure`: in beiden Tabs steht genau ein Kasten (`TvstUploadEnabled` bzw. `TvstDownloadEnabled`) und **keiner** der vier alten Adress-Kästen; Überschrift „tvst" genau einmal, ein Satz nennt beide Adressen und „TMDb decides"; Speichern schreibt **beide** Adressfelder (`tvstUp`/`tvstDown`), Laden liest den wirksamen Zustand beider. **8 Prüfungen**, alle in der Suite `gui-structure` (318 → 326).

**T242:** „5xer" sind keine Upload-Kandidaten (F-M519), in der Suite `endpoints`: `IsOriginalSidecar` verweigert die Slots **51**, **52**, **50** und die Variante mit `sdh`-Marker und Slot **99**, während der korrigierte Block (**01**, **49**, ohne Slot) Kandidat bleibt; die Kandidatenliste aus `FindLooseSrts` enthält danach **keinen** Eintrag aus dem Originalblock. **3 Prüfungen**, alle in der Suite `endpoints` (179 → 182).

**T223:** Die tvst-Seriennummer steht im Store und eine fehlende Serie wird mit dem TMDb-Titel angelegt (F-M501): der Vergleich ist eng (Groß-/Kleinschreibung und Leerraum zählen nicht, `&` = `and` = `und`, **kein** Teilstring und **kein** Artikel-Abschneiden); eine gesetzte `TvstShowId` verhindert Suche **und** Anlage (null Anfragen); ein Treffer schreibt die Nummer ins Medium, ohne anzulegen; ohne Treffer wird **genau einmal** angelegt (Name/Jahr/Staffelzahl aus TMDb), danach **erneut** gesucht und erst dann geschrieben; die Staffelzahl wird auf 1..22 begrenzt (30 → 22, keine Angabe → 1) und `BuildAddShow` weist 23 ab; eine gescheiterte Anlage schreibt nichts, blockiert die Kette nicht (`Skipped`) und wird im selben Lauf **nicht** wiederholt; der Trockenlauf legt nichts an; eine unklare Serienliste wird einmal nachgefragt und dann nicht geschrieben; der Endpunkt sucht mit Nummer im Ticket **vor** dem Upload nicht (die eine Liste danach ist die Id-Rücklesung, F-M488); und die Migration selbst wird an einer Datei aus Schema 12 gefahren — die Spalte wird beim Öffnen nachgezogen, die Bestandszeile bleibt lesbar, der Marker steht danach auf 13. **16 Prüfungen**, Suite `endpoints`.

**T218:** Der TMDb-Block steht in der Seite **vor** dem SubDL-Account-Block (Index-Vergleich beider Überschriften und ihrer Felder), und die JS-Bindungen zum Laden und Speichern hängen weiter an den Feld-IDs, nicht an der Position — der Tausch ändert nur die Reihenfolge, keine ID, kein Feld, keinen Schalter. **1 Prüfung**, Suite `gui-structure`.

**T209:** „Hochgeladen" steht je Quelle im Store: beide Zeitstempel-Felder sind vorhanden, Schema 12 legt sie in `sidecars` **und** `embeds` nach, der Lauf setzt nur den Zeitstempel der Quelle, die genommen hat, `IsUploadedTo` liest den Zeitstempel zuerst (Kennzeichen als Rückfall), und ein leerer Wert überschreibt keinen Zeitstempel. **4 Prüfungen**, alle in der Suite `endpoints`.

**T208:** Reup und Kreuzkontamination sind je Quelle verhindert: ohne Uploads dürfen beide Quellen laufen; ist tvsubtitles bedient, bleibt nur SubDL (und umgekehrt); sind beide bedient, läuft nichts und der Grund nennt beide; Kreuzkontamination wird vor der Reup-Frage verweigert; ausgeschaltete Schalter ergeben `no-upload-source`; und die Pipeline fragt **zweimal** über dasselbe Tor und benutzt die alte „irgendwo hochgeladen"-Abfrage an dieser Stelle nicht mehr. **8 Prüfungen**, alle in der Suite `endpoints`.

**T207:** Die vier eigenen Kennzeichen stehen im Store und werden richtig gefüllt: alle vier Felder sind vorhanden, Schema 11 legt sie in `sidecars` **und** `embeds` nach, der Download schreibt das Feld seiner Quelle, der SubDL-Upload schreibt seine Id in sein eigenes Feld, die Id der zweiten Quelle reist im Ergebnis mit und landet in ihrem Feld, der Rückleser existiert (`FindUploadedIdAsync`) samt `SourceId` im Ergebnis, und eine **fehlende** Id wird durch eine eigene ersetzt: `OwnUploadId` liefert immer ein `own:`-Präfix, ist als eigenes Kennzeichen erkennbar (`IsOwnUploadId`), ist reproduzierbar bei gleicher Eingabe und unterscheidet sich bei anderem Inhalt, und im Lauf wird nur das Feld der Quelle gesetzt, die genommen hat. **9 Prüfungen**, alle in der Suite `endpoints`.

**T206:** Die Herkunft eines Untertitels steht im Store und hält die Quellen auseinander: der Store-Wert benennt die Quelle und lässt sich zurücklesen (auch `null`/leer als „sagt nichts"), eine **bekannte** Herkunft wird bei **beiden** Quellen verweigert (verschärft 15.11.2026: „Herkunft subdl: nie zurück zu subdl, nie zu tvst") und der Grund nennt sie (`origin:subdl — downloaded there, never uploaded`), unbekannte Herkunft bleibt überall erlaubt, die Ablehnung nennt beide Seiten, der Download schreibt Herkunft **und** die Id der Quelle, der Upload liest die Herkunft vor der Endpunktwahl, und Schema 10 samt `ALTER TABLE`-Schritt ist vorhanden. **8 Prüfungen**, alle in der Suite `endpoints` (gezählt in T205 zusammen mit dem Schnitt).

**T205:** Beide Richtungen sind einzeln schaltbar und die Schalter wirken: der Katalog gibt bei eingeschaltetem tvsubtitles-Schalter **beide** Endpunkte einer Richtung heraus, bei nur-Download genau den Download-Endpunkt, bei nur-Upload genau den Upload-Endpunkt, bei SubDL-Upload allein genau diesen; bei allen Schaltern an wird nichts verweigert, bei ausgeschaltetem Quellenschalter wird er als `switched off` genannt; die Serienauflösung der zweiten Quelle liefert weiterhin nur mit Namen Treffer; der Trockenlauf des Upload-Endpunkts sendet nichts. Dazu die Quelltext-Bindung: `DownloadPipeline` fragt die zweite Quelle genau an der Stelle, an der eine Sprache sonst „nicht verfügbar" wäre, und holt fremde Kandidaten bei ihrem eigenen Endpunkt; `UploadPipeline` ruft die zweite Quelle genau dann, wenn der Schalter an ist und SubDL nicht aufgenommen hat — beides als Quelltext-Bindung geprüft, damit ein späterer Umbau die Regel nicht stillschweigend entfernt. **37 Prüfungen**, alle in der Suite `endpoints`.

**T204:** Die Naht trägt vier Endpunkte und der Rest bleibt: zwei Download- und zwei Upload-Endpunkte, je einer pro Quelle; der Katalog gibt nur heraus, was **Schalter an** und **eingebunden** ist, und tvsubtitles bleibt auch bei eingeschaltetem Schalter draußen und wird als verweigert mit dem Satz der Konfigurationsseite genannt; die SubDL-Abbildung eines Kandidaten auf den neutralen Treffer ist vollständig und erfindet keinen Rip; die Serienauflösung der Seite braucht den Namen; die Sprachbrücke kennt das Vokabular der Seite; die tvsubtitles-Suche läuft die echten Seiten ab und findet genau die gewünschte Sprache, eine fehlende Folge und eine unbekannte Serie liefern nichts; der Abruf liefert Name und unveränderte Bytes; der Upload-Trockenlauf baut den Rumpf und sendet nichts; Staffelpack, unbekannte Sprache, fehlender Name und unbekannte Serie werden mit Grund verweigert; der SubDL-Endpunkt verweigert Staffelpack, fehlende IMDb-Kennung und Nicht-UTF-8-Bytes, und ohne Sitzung kommt eine lesbare Ablehnung statt einer Ausnahme. Gezählt wird in T205 — dieselbe Suite trägt beide.

**T203:** Die tvsubtitles.net-Methoden sind gegen die Seiten geprüft, die die Seite am 15.11.2026 wirklich geliefert hat: 24 Sprachkürzel der Seite (nicht ISO) samt der Paare, die eine ISO-Tabelle falsch macht, Kommentarzeile aus TMDb- und IMDb-Kennung plus Client-Kennzeichen (Name und laufende Version aus der Assembly), ein Zusatztext sitzt dazwischen, Ablehnung von Show-Id 0, Staffel 0/23, Folge −1/101, unbekanntem Sprachkürzel, unbekanntem Rip, fehlenden Kennungen, leerer Datei und falscher Endung, Feldreihenfolge wie im Formular, **`author` kommt nicht vor**, `double`/`hi` nur wenn gesetzt, Multipart-Rumpf byte-genau mit Datei-Teil und Grenzzeichen, Trockenlauf sendet nichts und meldet trotzdem die Rumpfgröße, ein echter Lauf geht als `multipart/form-data` an `/add1.php` mit `/add.html` als Referer, die Anlage-Felder in ihrer Reihenfolge, dazu die Parser gegen die Fixtures: Serienliste, exakter Name schlägt „enthält", Staffeln, 24 Folgenseiten ohne Dubletten, `2x24` aus der Überschrift, Einträge mit Flaggen-Sprache, Dateiname aus den JavaScript-Fragmenten, und die Zip-Erkennung an den Bytes. **41 Prüfungen**, alle in der Suite `tvsubtitles`; die Live-Probe gegen die Seite ist ausdrücklich **kein** Teil der Prüfungen.

**T202:** Die Schalter sind je Anbieter und Richtung getrennt und der alte Wert bleibt der Rückfall: der Anbieterschalter gewinnt, auch gegen den Alt-Wert; solange er leer ist, antwortet der Alt-Wert in beiden Richtungen; tvsubtitles.net gilt als Anbieter, seine beiden Richtungen sind einzeln schaltbar (F-M485), und im ungesetzten Fall ist der tvst-Download an, der tvst-Upload aus (F-M503). **10 Prüfungen**, alle in der Suite `offstate`.

**T197:** Die Rangfolge der Kandidaten richtet sich nach Preis und Reichweite: vier Stufen, Gratis vor bezahlt, Staffelpack vor Einzelfassung bei gleichem Preis, Pack ohne die Folge bleibt draußen; die Cache-Prüfung geht über die Eintrags-Id; die Rangfolge gilt auch für das Nachfassen und die HI-Variante. **6 Prüfungen.**

**T195:** Ein Staffelpack reicht an die übrigen Folgen desselben Releases weiter: nur die Folgen aus der Warteschlange des Laufs, kein Namensvergleich gegen den Pack-Namen, keine Vollzähligkeitsprüfung, Einreihung direkt hinter der bedienten Folge, Indexschleife statt Aufzählung; ein Nachfass-Kandidat derselben Folge löscht den Merker nicht. **7 Prüfungen.**

**T194:** Ein Weg zu den Bytes: Archiv ganz holen, in die Ablage legen, Mitglied daraus entpacken; nur die Eintrags-Id als Adresse; keine Einzeldatei-Gestalt; Eintrag ohne Kennung wird übersprungen und benannt. **8 Prüfungen.**

**T193:** Der Nachlauf steht einmal auf der Seite; die Statuszeile schweigt über ihn, solange er gesund ist, und nennt ihn im Fehlerfall (F-M470). **3 Prüfungen.**

**T192:** Die Gruppenwahl ist bindend: geteilte Staffel ohne Deckung, deckende Gruppe, namenlose Gruppe (F-M469). **5 Prüfungen.**

**T191:** Die Spurliste wird über die Id des Mediums gelesen, die Objektliste bleibt Rückfall (F-M468). **2 Prüfungen.**

**T190:** Die Serie wird über ihre Id aufgelöst — ohne geladene Serie, ohne Lookup, und mit Vorrang der geladenen Serie (F-M467). **4 Prüfungen.**

**T189:** Der Upload-Nachlauf hängt am Upload, das Staffel-Memo am Fingerabdruck (F-M465, F-M466). **7 Prüfungen.**

**T188:** Ein Abruf trägt sein Urteil, eine Auflösung geschieht einmal — kein Client-Zustand, eine gemeinsame Auflösungsreihenfolge, Archiv über den Weg der Dateiliste (F-M464). **9 Prüfungen.**

**T187:** Der gewählte Dateiname entscheidet im Archiv — er löst einen Eintrag ohne Folgennamen auf, ein nicht vorhandener Name bleibt null, und die Folgen-Suche kommt vor dem Neuladen (F-M463). **7 Prüfungen.**

**T186:** Der Befund des Hauptpfads wird dort eingestellt, wo er gemessen wurde — vor dem Weiterlauf zum nächsten Kandidaten, nicht hinter „nichts gespeichert" (F-M462). **1 Prüfung.**

**T185:** Der v1-Abrufweg schreibt Status, Fehlername und Länge seiner eigenen Antwort — ein Reject nennt den Grund dieser Anfrage, nicht den der vorigen (F-M461). **1 Prüfung.**

**T184:** Der SDH-Pfad meldet seine Messungen — Struktur- und Laufzeiturteil werden zu Befunden, nichts Geliefertes nicht, der leere Abruf wird einmal wiederholt, und der Befund wird sofort eingestellt statt am Laufende (F-M460). **6 Prüfungen.**

**T183:** Die Statistik spannt das Feld und schiebt die Zahl an seine rechte Kante, damit sie auf derselben Senkrechten endet wie die Statusspalten (F-M459). **1 Prüfung.**

**T182:** Kein Statistikname ist länger als die längste Zeile, die die Tabelle schon trägt — der Name wird gekürzt, nie die Zahl (F-M458). **1 Prüfung.**

**T181:** Zierbuchstaben und Urteil je Versuch — `𝐒𝟎𝐄𝟏𝟎` und Vollbreite werden gelesen, die geschriebene `S00` bleibt Staffel 0, ein gelöschtes Urteil trägt keinen Status, und nur ein echter 200 gilt als leere Antwort (F-M456/F-M457). **7 Prüfungen.**

**T180:** Ein leerer Abruf ist kein Urteil — der Refetch nach zehn Sekunden, genau einmal je Kandidat, gemeldet erst beim zweiten leeren Abruf; ein Mitglied ist eine Datei und damit meldbar, ein Pack nicht; und die angenommene Meldung erreicht die Statistikzeile, ein Trockenlauf nicht (F-M455). **6 Prüfungen.**

**T179:** Welcher Name eine Folge trägt — `6.10 …` in einem Pack ist E10, `5.10 …` ist nie Staffel 6, `DDP5.1` ist kein E1, `S06.1080p` ist keine Folge, und `SxxExx` funktioniert daneben unverändert (F-M454). **6 Prüfungen.**

**T178:** Der Store-Harness verweigert ein Repository als Arbeitsverzeichnis und einen Aufruf ohne Verzeichnis — er löscht jedes File in seinem Verzeichnis, und ein Repository-Root wurde dabei schon einmal geleert. **3 Prüfungen** (Repo-Marker, fehlendes Argument, Normalbetrieb).

**T177:** Was eine Datei auf der Platte beweist — der reservierte Originalblock (51–99) schließt kein Paar, eine ausgerichtete `01` schon, eine gelöschte `01` öffnet es wieder, ein unnummerierter Name zählt weiter, und der Slot-Leser liest, was der Schreiber schreibt (F-M452). **12 Prüfungen.**

**T176:** Die Meldung eines beobachteten Defekts — Wortlaut ohne Begründung, Kategorie je Befund, Name der betroffenen Datei in beiden Spuren, Absenderzeile mit Version und Kontoname, Meldung im selben Lauf, Vertagung statt Verwerfen, gesperrter Kandidat nach der Meldung, Schalter und seine Lage als vorletzter Eintrag (F-M450). **4 Prüfungen zur Dateinennung.**

**T175:** Der Account-Filter der Download-Suche — Kontokennung, kein Fehlgriff auf fremde Uploader, Ausschluss vor der Rangfolge und der Schalter samt seiner Beschriftung (F-M449).

**T163:** The season-pack suite asks the arithmetic and the documented upload shape without a network: the aired set drops the E0 entry IMDb reports, completeness is set equality on both sides, the pack hash is order-sensitive and changes with a corrected episode, the name carries no `.zip` because SubDL appends it, and the pack call sends the array of file ids with `is_full_season=true` and the episode range. **84 checks.**

**T160:** A deferred anchor job comes back, and deadlines are read in the clock they were written in (F-M368).

**F-M301:** **Das Lastenheft wird geprüft wie der Code.** Ein Abschnitt, der eine Regel verliert, wird rot, und keine andere Prüfung fängt das auf.
## 20. References

- Plugin template: github.com/jellyfin/jellyfin-plugin-template
- Reference plugin (download): Jellyfin OpenSubtitles plugin
- SubDL API: `https://api.subdl.com`

**T140:** One worker row, one worker's own result, and the download tab in the cycle's order.

**T141:** One lit lamp, for the worker that is working.

**T142:** The IMDb/TMDb match is hard and has no switch.

**T143:** A spent allowance acts like the download switch.

**T144:** A worker row may not claim work the run did not do.

**T148:** The Auto-Sync row is reachable while it runs, and closed by whoever opened it.

**T145:** The General tab carries everything a run needs: Target languages sits on the General tab, directly below the library selection and above the refetch interval, and is **absent** from the Download tab.

**T146:** The two daily-limit switches read the SAME sentence, and the upload one carries no "API".

**T149:** Every number in an Auto-Sync log line is separated by a POINT, whatever the host's culture wants.

**T150:** The compaction refreshes the query statistics.

**T151:** Every SubDL call names its integration and carries the versioned agent.

**T152:** The upload reports the source quality of the subtitle.

**F-M346:** The download's filename fallback is the last resort — it runs only when an item has no id at
all — and there it **confirms instead of guessing**. An IMDb id stated in the name (`tt` plus seven to
nine digits, the way uploaders write it: "The Counterfeiters 2007 tt0813547 sqi") is looked up through
`/find/{id}` and used **only when the title route agrees with it**; a contradiction is logged and
neither route is trusted. Reason: measured on 1224 real SubDL names, the id is the one part of a name
that survives every title variant — a translated title cannot be found by text search at all ("Tan Thuy
Hoang" is the Vietnamese title of "Qin Shi Huang") — while one measured entry stated an id belonging to
a different film than its own title. The id is also stripped from the title, so a name never becomes one.

**F-M347:** A **series** whose name states season and episode but no year is resolved through the
**episode itself**: for the first few search candidates the plugin asks `tv/{id}/season/{s}/episode/{e}`
and accepts the first candidate that really carries that episode. No candidate with it means no
resolution. Reason: measured on 1224 names, **61 of 70 series names carry no year**, so the strict
title+year test cannot run for them, and the loose route's first hit was right only **4 of 14** times. A
**film** without a year stays unresolved for the same reason — never TMDb's most popular namesake. The
der lose Ersttreffer kommt im Rückfall nicht vor; it still serves the Jellyfin-title rung.

**F-M348:** A parsed title that still carries brackets or percent signs is **not a title**, and the
fallback refuses it (54 of 1224 measured names), while leading bracket labels (`[NETFLIX]`,
`[Crunchyroll]`, `[cFpR]`) are stripped from the title (73 measured) — keeping one made the title search
miss.

**F-M349:** Names that dress their letters in presentation forms (mathematical alphanumerics, fullwidth)
are folded with Unicode **NFKD** before parsing — but **only when such a character is present**, so
ordinary names pass through byte-identical. Reason: the bold digits in
"[𝐜𝐅𝐩𝐑] 𝐃𝐀𝐅𝐓 𝐒𝐓𝐀𝐓𝐄 (𝟐𝟎𝟐𝟒) 𝐏𝐑𝐈𝐌𝐄 - 𝐓𝐛𝐕" are a year to no pattern, and such a name yielded the whole
string as title and no year (measured: **0 of 12** resolved). The check is a surrogate test, not a
regex:.NET's regex engine has no `\U` escape, and the first version threw at type-initialisation time —
caught by the harness, not by the build.

**T153:** The filename fallback holds for real SubDL names:
the plugin's own parser (reached by reflection, since the parser is internal), plus source assertions for
the wiring — the series route, the strict title+year route, the confirmation of an id stated in the name,
the refusal of a contradiction, the refusal of an unusable title, the episode call in the resolver and
the gate in front of the fold. The fold checks skip themselves on a runtime without ICU instead of
failing, because normalization is a globalization feature.
