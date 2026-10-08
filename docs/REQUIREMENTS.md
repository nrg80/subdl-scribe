# Requirements Specification — Jellyfin Plugin "SubDL Scribe" (Upload + Download)
**Project:** Native Jellyfin plugin: automatic upload of embedded subtitles to SubDL.com + download pipeline for missing external subtitles — both in ONE plugin
**Version:** 2.62
**Status:** Implementation — v12.1.12.194.

**Die Begründungen (warum eine Regel gilt, Messungen, Vorfälle) stehen nicht hier, sondern lokal in
`/opt/data/SubDL-Scribe-Methodik/METHODIK.md`, nach Kapiteln sortiert und mit der Requirement-Nummer
markiert. Dieses Dokument nennt die Regel, die Konstante, den Grund und den Test — sonst nichts.**

**Zählkonvention der Contents-Zähler.** Ein Abschnittszähler ist die **Anzahl der Definitionszeilen
in seinem Block, Unterüberschriften eingeschlossen, superseded-Einträge eingeschlossen** — ein
schlichter Zählwert, der keine Aussage darüber trifft, was noch in Kraft ist. Abschnitt 3 nennt 32
und hält 32 (mit 3.1 = 8 und 3.2 = 2); Abschnitt 9 nennt 25 und hält 25. Ein Abschnittszähler, der
seine Unterabschnitte ausnimmt, ist nicht die Konvention. Geprüft von T115.

## Contents

- [1. Objective and Scope](#1-objective-and-scope) — 2 requirements
- [2. Seeder — what enters the queue](#2-seeder-what-enters-the-queue) — 12 requirements
- [3. Upload Pipeline](#3-upload-pipeline) — 32 requirements
  - [3.1 Quality Gates — Upload](#31-quality-gates-upload) — 8 requirements
  - [3.2 Dry Run — Upload](#32-dry-run-upload) — 2 requirements
- [4. Download Pipeline](#4-download-pipeline) — 38 requirements
  - [4.1 Quality Gates — Download](#41-quality-gates-download) — 6 requirements
  - [4.2 Dry Run — Download](#42-dry-run-download) — 1 requirement
  - [4.3 Auto-Sync — Download](#43-auto-sync-download) — 15 requirements
- [5. Upload Postprocessing](#5-upload-postprocessing) — 13 requirements
- [6. Database Refresh](#6-database-refresh) — 7 requirements
- [7. OSHash Refresh](#7-oshash-refresh) — 3 requirements
- [8. Rules Shared by Both Directions](#8-rules-shared-by-both-directions) — 4 requirements

- [9. SubDL/TMDb API, IDs and Credentials](#9-subdltmdb-api-ids-and-credentials) — 25 requirements
- [10. Scheduler, Quota and Timing](#10-scheduler-quota-and-timing) — 13 requirements
- [11. Content Registry and Identity](#11-content-registry-and-identity) — 14 requirements
- [12. Library Scope and Skip Filters](#12-library-scope-and-skip-filters) — 8 requirements
- [13. Configuration and Settings Page](#13-configuration-and-settings-page) — 14 requirements
- [14. Data Model and Persistence](#14-data-model-and-persistence) — 12 requirements
- [15. Logging, Status and Transparency](#15-logging-status-and-transparency) — 30 requirements
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

**F-M1a [B3]:** ItemAdded real-time trigger — the item-added event subscription in the event dispatcher (the handler); there is no separate watcher class. Each arrival starts a non-resetting debounce timer (the arrival debounce window (default 1 min), default 1 min). On fire the dispatcher runs one cycle: seed → download → upload (download first, F-M149). Events during an active cycle collect in the pending ids. A follow-up reseed runs at cycle end only if that list is non-empty; the follow-up settings decide which directions take part. Arrivals that are still pending when the cycle ends start one further full cycle after a 90-second delay.

**F-M233:** **An on-arrival run works ONLY the arrivals; a full-coverage pass happens on the schedule or the manual button.**

The arrival path must be scoped to the items that arrived. Unscoped, one arrival window swept every gap in the library into the queue and the run died on the daily limit before reaching its own items.

The debounce window collects EVERY event in it, not merely the first, and arrivals during an active run form the follow-up window. An arrival cycle seeds and works exactly those ids.

Only `event` and `arrival-followup` are scoped. `scheduled-upload`, `scheduled-download`, recovery fires and the manual button carry no item scope and keep full coverage for the selected libraries.

"Kein treffer, kein lauf": an arrival-scoped round whose seed queued nothing for that direction ends; it does not work leftover queue entries.

An arrival cycle with an empty collector ends without a scan. Falling back to a full scan on the arrival path is a defect, not a graceful default. **See T48.**

**F-M244 [B1/D]:** **The queue item is a complete work order; the pipelines only carry it out.** The seeder decides WHAT is to be done and hands it over in the item; the downloader and the uploader execute that order.

The download pipeline reads its languages from the item and does not answer the HI question again — that decision is the HI wish plus the languages the order names. The upload pipeline likewise consumes the positions it was handed.

The pipelines keep ONE non-derivation check — the media file's existence (F-M60, its consecutive-failure counter). A file that vanishes between seeding and the run is reported as a failure.

**F-M263:** **A dry run suppresses the container rewrite — for BOTH directions — and the seeder is the only caller of the language gate.**

With a dry run (upload) OR a dry run (download) armed, the gate still resolves untagged tracks and reports them but writes nothing into the media file. Detection is not suppressed: a dry run's value is answering what it WOULD do (F-M22).

**F-M259 [D]:** **Loose .srt files are observed by the seeder's scan.**

The scan records every loose subtitle file beside the media whose NAME carries a language as an observation row, keyed by the normalized content hash (the sidecar's identity) with its language and HI flag. A sidecar whose name carries no language token is resolved by detection and renamed first (F-M278); if its language cannot be established it gets no observation row. `Status = observed`, no verdict; an existing verdict is never overwritten.

Content is the key, not the path, and the hash is the uploader's own function. Two sidecars with the same language but different text stay two rows.

**F-M278 [D]:** **An unlabelled sidecar is detected and then RENAMED to the shape this plugin writes — `<container>.<lang>[.sdh].srt`. Only a file whose name carries no language token is touched.**

The name is the only thing Jellyfin, MediaElch and every reader here can see. An unlabelled `<container>.srt` is read as "no language" (F-M239), so it proves no coverage, gets no observation row (F-M259) and the item is searched for a language its own disk already holds — the item never settles. Detecting the language and writing it into the name is what ends that.

**The run order is detect, then rename, then record.** Detection reads the file's own text — an `.srt` is plain text, so no ffmpeg pass is involved. The gates apply unchanged and in this order: switch **`Allocate missing language codes` off** (F-M314 — the same switch as the container write, NOT `UploadResolveUnd`), text below the 2 KB floor (F-M16), or no language found all mean **the file is left exactly as it is** — no rename, no row. Only a detection that names a language renames.

**A file that already carries a language token is never renamed.** Its name is already the shape this rule produces, and rewriting it would churn files that are correct.

**A taken combination takes the next free slot, never an overwrite.** The target is slot 1 (`<container>.<lang>.01.srt`, F-M316); where that name is already on disk the next slot is used (`.02.srt`, `.03.srt`, …), because two unlabelled files that both detect as the same language are two subtitles and tidying a name must not destroy one. The slot is per combination: a `DE` file present does not push an `EN` file to a slot.

**A rename moves the stored row's location with it.** The content hash — the row key — does not change, so the row keeps its verdict and only its path and name are updated. Without that the refresh task would find a path that no longer exists and forget the row (F-M234) for a file that is right there under a new name.

**A refused rename is not a failed observation.** If the target cannot be built, the directory cannot be listed or the filesystem rejects the move, the file stays where it is and is recorded under its current name. The rename is a tidying step, never a precondition for recording.

**F-M55:** **Download-on-arrival as its own switch** + UI restructure: "Download subtitles on arrival" checkbox (Download tab), independent of the upload arrival setting. Both directions share the same diced anchors/jitter.

**F-M57:** **Symmetric per-direction config, one shared rhythm:** each direction carries its own "… on arrival" checkbox — "Upload subtitles on arrival" and "Download subtitles on arrival". The scheduled rhythm is **not** per direction: the single refetch dropdown governs both (F-M111). Both directions share the same diced anchors; `Manual` suppresses the scheduled pipeline fires for both.

**F-M111:** **Queue-driven operation:** both pipelines run only over items the seeder placed in the in-memory queue. Runs are atomic per direction and serialized by a global run lock.

## 3. Upload Pipeline

**F-M2 [B1]:** Manually triggerable scheduled task (dashboard "Scheduled Tasks") for a full library rebuild

**F-M3 [B1]:** **A media file is upload material when it carries at least one text subtitle stream, or at least one loose subtitle file next to it.** Bitmap streams (PGS/VobSub) do not qualify: they carry no text (F-M6). A stream Jellyfin reports as external is a loose file already, not a container stream. A forced track carries only the lines of foreign-language scenes and does not qualify either (F-M246).

**F-M4 [B1]:** Never upload an (item, language) pair twice — persistent state file in the plugin data dir

**F-M6 [B1]:** Text subtitle streams only (SRT/ASS/SSA, converted to SRT); bitmap subs (PGS/VobSub) explicitly excluded

**F-M7 [B1]:** **Extraction writes nothing into the media library, and its working files are removed on every exit.** Each extraction runs in its own directory outside the library, one per job. That directory is removed when the job ends — after a success, after a failure and after a cancellation. The removal is best effort and stays silent: a leftover working file must never fail a run, and it is never worth a warning.

**F-M8 [B1]:** SubDL API (`https://api.subdl.com`): login (user/pass → token) and the upload endpoint. The call rate is configured, not fixed (F-M20)

**F-M9 [B1]:** Metadata per upload: series IMDB (**not** episode IMDB) + season + episode for TV; movie IMDB for movies — from Jellyfin provider IDs. Without IMDB → skip (F-M28).

**F-M10 [B1]:** Language mapping to SubDL's two-letter codes (`nob`→`NO` etc.)

**F-M48 [D2]:** **Loose SRT files as an additional upload source:** external `.srt` files next to the media file are uploaded in addition to embedded streams. The language comes from the file name; a name that carries **no** language token goes through language detection, at the same switch as an `und` stream (F-M74) and with the same outcomes — switch off, text below the 2 KB floor (F-M16), or no language found all mean **skip**, never a guess. The marker is **`sdh` only**, and such a file is uploaded with `hi=true` (F-M71). `hi` is NOT a marker — it is the ISO 639-1 code for **Hindi** (`MapToSubdl("hin") = HI`), so a `.hi.srt` name is a Hindi subtitle. Nothing in this plugin writes `.hi.srt`; the downloader writes `.sdh.srt` (F-M239). The HI flag is part of the identity in both areas (F-M197, F-M199).

**F-M88c [B3]:** **Upload-side short-circuit (the upload-side completion mark):** a file whose embedded subtitle side is complete is skipped before any ffprobe, directory walk or API call. Complete means every stream position reached a terminal state (uploaded or rejected). The field is named after the SUBTITLES, not the file: the video file itself is never uploaded.

**F-M21 [B1]:** Update interval: **manual / daily / twice daily / twice weekly / weekly / monthly**. A scheduled interval fires at the per-installation diced random anchor (F-M51); **manual** leaves no scheduled fire at all and keeps the dashboard and config-page triggers; "on new file" = F-M1a. **Default: Weekly**, shared by both directions (F-M111).

**F-M26b [D]:** **A deferred direction waits the job spacing — one knob, no hidden clock.** Whenever a stop defers a direction, the one-shot recovery fire lands **JobSpacingMinutes after the stop** (both directions), clamped 5–120, default 15. **Test: T22.**

**The fire used to be anchored to a roll-over (removed 03.10.2026, user decision).** A per-run hourly bucket anchored it, and that bucket started at the **first API call of the run** — so its "roll-over" was a moving point nobody could predict from the settings page: a run that began at 04:50 and stopped at 04:55 fired at 06:05, and the only explanation lived in the code. Both the anchor and the bucket that produced it are gone (F-M20). What replaced them is the plain deferral every other stop already uses (run-lock retry, overload, postprocessing), so one knob now means one thing everywhere.

**The stop reason is never invented.** A deferral is recorded with the direction's own stored wording, so a run-lock push is not reported as a quota problem (F-M288).

One knob for all spacing — the user configures it through the existing **Job spacing (minutes)** field, with no separate control to discover.

Anti-herd spreading is not needed here: the offset is per installation. The daily-limit reset keeps its randomised 30–300 min offset (F-M152, F-M182).

The coordinator must not add a second offset on top: this path uses the recovery-fire scheduler, whose `alreadyJittered: true` leaves the caller's offset alone. The offset must not affect the daily-limit reset.

**F-M249:** **The file-name parser recognises a bare episode marker without a season, and a broadcast date in the middle of the name with the title after it.** Every other shape keeps its result.

**(a) Bare `Exx`.** A name with a bare `Exx` carries no `S<d>E<d>`. The rule sets a series flag and the episode number, then falls through to the shared head/year/quality cleanup.

**(b) Mid-name broadcast date.** The shape is `<strand>.<YYYY>.<MM>.<DD>.<title>.<tags>`. The rule takes the text after the date, cuts at the first release/language token and drops trailing bare numbers.

**Scope of the date rule:** it applies only when (1) no `SxxExx`/long-form marker is present, (2) the date is a **separated** `YYYY<sep>MM<sep>DD` (the compact `YYYYMMDD` stamp belongs to the TV-stamp rule), (3) month and day are in range, and (4) **text follows the date**. A name with text BEFORE the date keeps its title and year.

**Scope:** every other file parses byte-identically in title, year, type, season and episode, and no file loses its title.

**Rules:** the prefix of (b) is dropped, not combined; a bare `Exx` never invents a season; the compact TV-stamp path is untouched; and a bare `Exx` marker is accepted in either case, upper or lower.

**F-M252:** **A bracketed year is a year, and its opening bracket is not part of the title.** The tail-year rule accepts `(YYYY)` and `[YYYY]` at the tail.

**Rules:** (a) the tail-year rule closes the bracket; (b) the "year anywhere" branch cuts at the **start of the match** (the separator), never at the year; (c) the title cleanup drops an unclosed trailing `(`/`[`/`{`.

**Scope:** every changed file must be a bracket name. **Test: T71.**

**F-M250:** **The parser reads two series markers: `NxNN`, and `SxxExx` at the end of the name.**

**(a) `NxNN` (`2x01`).** Read as a season/episode marker.

**The `NxNN` rule cannot misfire on a resolution.** The season is capped at two digits, must not start with `0`, and may not be preceded by a digit. `1920x1080`, `2160x1080` and `0x01` yield nothing; `1x01` → S1E1, `10x05` → S10E5.

**(b) `SxxExx` at the end of the name.** The separator after the episode number is optional at the end of the name.

**F-M202 [B1]:** **An episode TMDB id is PROVEN against a candidate show — never guessed.**

Rule: title + season + episode → `search/multi` → candidate show → `tv/{show}/season/{s}/episode/{e}` → accept the show **only when the episode id returned there equals Jellyfin's id**. A confirmed match yields the SHOW ids (imdb via `/external_ids`; the detail endpoint reports `imdb_id: null` for series).

**Fail-closed:** no confirmed equality ⇒ `(null, null)` ⇒ the item is skipped like any other unresolvable series.

Season and episode come from Jellyfin's own metadata where it reports them; the parsed file name is the fallback and leads only when the name itself marked the item as a series.

**F-M29 [B1]:** **No user-identifying metadata in uploads:** exclusively IMDB ID + season/episode + language + subtitle file. No username, no library/path information, no source media filenames, no server/installation identifiers, no version telemetry.

**F-M30 [B1]:** Neutral upload filename: derived generically from the IMDB/language/release info — never internal JF paths or user names.

**F-M264:** **A media file is rewritten at most once — a track that already carries a tag in the container is never written again.**

Within ONE cycle both seed passes write the codes: the DOWNLOAD-seed pass, and the UPLOAD-seed pass that follows. The second pass reads a stale stream list and finds the same tracks untagged.

Jellyfin caches its stream list, so a corrected container keeps reporting its old tag for the rest of the cycle. No registry read and no self-observation of a freshly written tag changes that.

Before the extraction pass, read the container's ACTUAL per-subtitle tags (a single ffprobe read, one ffprobe call, no remux) and drop every pending position that already carries a mappable tag. Those positions are recorded as ordinary tracks instead, so the registry and the queue decision still see the real language. When nothing remains pending, the run returns with `Written == 0` and logs `N track(s) already carry a language tag in the container; nothing to write` at Normal.

The switch the missing-language switch sits on the **General** tab under `Media files`, directly above the statistics section. **Test: T82.**

**F-M239 [B1/D2]:** **One reader for sidecar file names.** The name→(language, hearing-impaired) rule exists ONCE (the sidecar reader); the uploader, the pipeline's missing-language check and the database refresh all call it. Marker is `sdh` only — never `hi` (F-M48). Recognized shapes: `<base>.srt` (no language — it is detected before upload, F-M48), `<base>.<lang>.srt`, `<base>.<lang>.sdh.srt`, and the numbered slots `<base>.<lang>.<n>.srt` the downloader writes (F-M316). `.part` is never a subtitle.

A marker is consumed as a marker and the language token is read from the position BEFORE it; a marker must never reach the language mapper. A name whose marker has no resolvable language token yields nothing.

**F-M266:** **The registry sweep removes OBSERVATIONS only — a VERDICT and a detection-attempt record are out of its reach.**

**F-M191 [B1]:** **A SERIES item must carry SHOW-level ids** — TMDB and SubDL expect the tvshow id together with season and episode, **never an episode id**. Jellyfin's provider ids for a mis-typed episode are EPISODE ids in **both** fields.

**Episode → show resolution:** `GET /find/{imdbId}?external_source=imdb_id` → `tv_episode_results[0].show_id` IS the series TMDB id; `tv_results[0].id` is the series itself when the id was already show-level. The series IMDb then comes from `/tv/{showId}/external_ids`.

**An episode TMDB id cannot be walked up directly:** `/find/{episodeId}?external_source=tmdb_id` returns EMPTY and `/tv/{episodeId}` is 404. Such a value is first PROVEN against a candidate show (F-M202); only when that proof fails is it dropped, and the caller's type-free title search then resolves the show by name.

### 3.1 Quality Gates — Upload

The upload chain, in the order the code applies it. Each switchable gate names its own switch and default.

**F-M16 [B2]:** **Minimum size and minimum cue count.** The 2 KB size floor applies to UPLOADS only and is always on. The cue count (30) applies in BOTH directions and is switchable per direction, default on. Below either, the stream is skipped as "too small".

**F-M13 [B2]:** **SRT parser validation (switchable, default on):** ≥1 cue, monotonically increasing timestamps, plausible cue duration (>0.1 s, <10 min — long music cues stay legal)

**F-M14 [B2]:** **Sync plausibility (switchable, default on):** cue span vs. media runtime (+5 min tolerance; all cues concentrated in <10 % of runtime = reject)

**F-M15 [B2]:** **Language verification (switchable in each direction, default on):** detected language of the content (bundled n-gram classifier) vs. stream tag; mismatch → skip; undetectable content passes (fail-open)

**F-M142:** **A pair already uploaded in this run is recorded as rejected, never as uploaded.** A stream whose (media content hash, language, hearing-impaired) pair is already up is skipped, and its position is recorded with the reason `duplicate-self-echo`. It is not marked `uploaded`. The hearing-impaired variant has its own key space, so a normal upload never blocks an SDH variant.

**F-M17c [B2]:** **Own upload dedup:** (item, language) pair + content hash in persistent state — never upload an already-uploaded or in-session-processed content hash twice. Depends on the canonical SRT form (F-M185): the hash is only stable across extraction runs when the payload is normalized first.

**F-M17x [B2]:** **Remote duplicate learning:** when SubDL rejects an upload with "Duplicate upload: identical file already published", the normalized content hash is stored as `duplicate-remote`. Future runs skip that content before any API call.

**F-M74:** **An `und` stream is resolved by detection before upload. Switchable, default on.** With the switch on, the detected language replaces the tag and the stream uploads normally; a detection that fails skips the stream. With the switch off, every `und` stream is removed from the upload set and logged. Detection needs text to work on, so a stream below the 2 KB floor (F-M16) is skipped as too little text.

### 3.2 Dry Run — Upload

**F-M22 [B1]:** **Dry-run switch, one per direction (both default off): the run walks its decision path, reports what it WOULD do, and writes nothing.** No file is transferred in either direction and no stored verdict is written. The two directions do not cost the same: the download dry run searches (F-M277), the upload dry run does not (F-M276).

**No stored verdict is written or cleared by a dry run** — a mark a dry run sets takes the work away instead of describing it, because the next real run reads the mark and skips the file.

**The rule covers every write that sits BEFORE the mode's own exit, not only the writes after it** — a dry run exits per item, so a write in the per-item preamble runs unless it is
guarded at its own site. What that covers, and the two sanctioned exceptions, are stated once in **F-M287**. **Test: T97, L1–L6.**

**F-M276:** **The upload dry run walks the upload decision path up to the transfer and spends no API call.**

It runs: the extraction and the quality gates of 3.1, the id resolution, and the per-file candidate list.

It does not run: the login (F-M8), the duplicate pre-check — which a real run does not make either (F-M184) — and the upload transfer. No search call is made, so no search quota is spent.

The report is one line per stream: `DRY-RUN would upload <neutral name>`, the neutral name being the one a real upload would send (F-M29, F-M30).

The file-retry counter is recorded as a success, so a dry run neither burns a retry nor leaves the retry state stale. No completion mark is written (F-M88c).

## 4. Download Pipeline

**F-M151a [B3] (superseded by F-M283, 02.10.2026):** **Download-side short-circuit — replaced by the derived open-pair question.**

There is no download-side completion mark any more. The pipeline asks `SubtitleCoverage` which required PAIRS have no evidence (14.2b) and returns when the answer is empty; that answer is derived fresh on every run, so it cannot be overtaken by a deletion and needs no stored language set and no validation pass. See F-M283 for why the stored mark had to go.

**F-M187:** **Downloaded bytes have exactly one decode path.** Every conversion of downloaded subtitle bytes to text goes through one path, which honours a UTF-16 byte-order mark (LE/BE, stripped) and falls back to UTF-8. The download side hashes the decode path, and the upload side later reads the written file as UTF-8/UTF-16 text, so the two must agree. An **uncorrected** file on disk stays **byte-identical** to SubDL's payload — normalization applies in memory for hashing and upload, never to the stored file. The one exception is a file the correction CHANGED: that goes out canonical (F-M296), because the bytes on disk must be the bytes the stored hash describes. The archived original is always the fetched bytes verbatim (F-M306).

**F-M251:** **One name parser, not two.** Both directions read names through the same parser; the download side holds no copy of the name logic.

**Scope is arithmetic:** the files this change may touch fall into six named groups (`NxNN`, bare `Exx`, `SxxExx`-at-end, date names, long form, bracket titles), and the groups must add up to the diff total. If they do not, an unintended change hides in it. **Tests: T71.**

**F-M41 [D, parallel]:** **Download pipeline in the same plugin:** search for missing external subtitles per item (missing languages against a configurable target-language list), download via the same API client, registration in the registry, storage as an external stream next to the media file. Own scheduled task, own switch, own status block.

**F-M42 [D]:** **Preferred languages:** target-language list (multi-select, no priority). Default `AR, EN, ES, FR, HI, ZH`; empty = download off. `DE` is absent.

**F-M42b [D]:** **Hearing-impaired version additionally** (checkbox, default off): when on, the best hearing-impaired candidate per (item, language) is downloaded in addition to the regular version and stored as `<basename>.<lang>.sdh.srt`. It comes from the second search (F-M241), and the branch must sit before the best-per-language cut break, or with the default one per language it is unreachable.

**F-M260 [D]:** **The downloader reads the hearing-impaired flag of the file it actually fetched.** When a candidate resolves to one file inside a season or range pack (the pack member), that file's own `hi` flag governs naming and registration; for a plain single-file release the candidate's flag does, since the candidate is the file.

The HI block of the download loop is guarded by the *effective* flag, not the candidate's: when the file just saved already was the HI variant, no second download follows.

**F-M319 [D] (operator order 08.10.2026, supersedes F-M242):** **A language receives exactly ONE corrected file. The "best subtitles to keep per language" setting is REMOVED.**

**The rule.** The candidate walk ends at the first candidate whose correction proves itself against the audio. An **unprocessed** file does not end it: it is a fallback, kept so the repeat costs no content (F-M317/F-M318).

**Why the setting went.** It existed to save X numbered files per language. Once the slot encodes WHICH KIND a file is (F-M317) and the walk repeats until a correction is proven (F-M318), the count had one meaningful value left — `1`, its own default — and every other value asked for the same language twice. The setting, its GUI field, the slot preview, the second parameter of `DownloadBudget` and the tests built on them are gone together.

**A removed setting must be removed at every layer** (config type, page, pipeline): a field left on the config type is a knob the next reader wires back up. Asserted in T131.

**F-M95:** **The search's early stop is the download cap, derived — never a literal.** The page walk stops once every requested language has at least N candidates, where N is the configured download budget (F-M50); `0` disables the early stop. Until 02.10.2026 the threshold was hard-coded to 3, so raising "Max candidates per language" widened the download budget but never the search: the walk still stopped after three candidates per language and the loop never saw a fourth. **One setting, one number:** the helper that derives the threshold takes the budget alone (F-M319).

**F-M241 [D]:** **Two searches, one per side of the hearing-impaired split.** The regular slot is filled from a `&hi=0` search and the HI slot from a `&hi=1` search; the HI search runs only while the switch is on, so a user who does not want HI pays one search exactly as before.

SubDL filters HI server-side and the two pools do not overlap — a release appears in one or the other, never both. An unfiltered response is a MIXTURE, and the regular ranking would silently depend on how many HI releases it happened to contain.

A failed HI search keeps the regular candidates and logs it — the HI variant is a bonus (F-M42b), the regular subtitle is the target.

A dry run reports the HI pick from the HI pool; the real selection happens after the file download, which a dry run never reaches.

**F-M47 [D]:** **Configurable refetch interval:** manual / daily / weekly / monthly. **Default: Weekly.** The per-file last-search stamp (the last-search stamp and the stored language list) protects already-downloaded languages and controls re-search. The unified cycle interval governs both directions; the per-direction refetch property is carried for XML compatibility only and read by no code path.

**F-M215 [D]:** **A season or range pack is resolved to the episode it belongs to — never saved whole.** A pack must yield exactly one subtitle file per episode, chosen from the pack's own listing:

**The ZIP fallback matches the entry NAME** (`S01E06`, `s01.e06`, `1x06`, SubDL's `S0106`). A multi-file archive whose entry cannot be resolved returns **null**.

A pack with no entry for this episode is **skipped**, not saved wrong. Non-pack paths keep their existing behaviour.

**F-M156:** **Missed refetch anchors:** an unhandled anchor is taken up by the next regular fire; the marker counts the claim, not the outcome. Missed slots do not stack.

**F-M58:** **Transient-overload 429 classification.** A `service_busy` 429 is server overload, not the daily allowance: the download side retries in place, up to 3 attempts, waiting the server's retry hint (default 5 s) and continuing the run. The upload side has no in-run retry — it ends the run and schedules the overload fire. A `rate_limit` 429 gets the same treatment (default 30 s); the status code cannot tell the two apart (F-M238). A login is classified rather than propagated: 404 and 403 are auth verdicts and fail closed at once, 429 retries, and a 5xx or a non-JSON body after 3 attempts ends the run as a transient overload and schedules the overload fire.

**F-M64:** **Target-language change needs no reset pass:** each file stores the language list it was last searched under plus the timestamp (the stored language list). the due check compares that stored list against the current configuration, so a changed list simply makes affected files due again — no global reset run and no second bookkeeping row. Items already holding all new languages still skip without API calls.

**F-M183:** **Legible download run reporting:** the download run counts **queued** items as the denominator of its abort line; the progress counter runs over every item of the selected libraries. At run end one compact aggregate line (saved, no candidates, language not available, skipped with reasons, failed, processed/queued), so a run that searched and saved nothing is distinguishable from a run that did nothing without raising the log mode.

### 4.1 Quality Gates — Download

The download chain runs against each candidate in score order, before the file is saved. F-M15 and F-M16 are the two gates both directions share.

**F-M44 [D]:** **Release match:** candidate release name scored against the local filename: release-group match > token overlap > download-count tie-breaker. Weights configurable (expert mode).

**F-M43 [D]:** **Runtime/FPS match with tolerance:**
**Stage 1 — pre-download (FPS):** SubDL provides `framerate`/`fps` per candidate. If set: difference > ±1 % vs. item FPS → candidate rejected. Missing → criterion skipped, no hard fail.
**Stage 2 — post-download (structure + runtime):** corruption check on the downloaded bytes (cue timings present, monotonically increasing, plausible durations), then runtime check of SRT cue span vs. item the runtime, default tolerance ±600 s. Outside → discard. Missing runtime metadata → criterion deactivated.

**F-M45 [D]:** **IMDB/TMDB match** (default on, switchable off): candidates matched against item IDs. Default is a hard criterion (no ID → no download); switchable off for title-based fallback.

**F-M50 [D]:** **Download budget per (item, language):** max N candidate downloads before the language counts as "not available" (default 3, 0 = unlimited). It is the ONLY budget on the download side — the raise to a keep-best count went with that setting (F-M319) and the QA retry limit is the fit's own budget, not a second download budget (F-M318/F-M320). **Test: T98.**

**The same language verification runs here verbatim** (F-M15), on the downloaded bytes, with its own per-direction switch.

**F-M295 [D] (superseded by F-M307, 07.10.2026):** **Drift gate — cue vs. speech: does the subtitle hold ONE offset, or does that offset MOVE?**
Decodes the audio, derives speech islands from the frame envelope, anchors each cue to the last island start before it, and compares the model "one offset" against "two offsets split at a candidate cue" by their marginal likelihood (Bayes factor). Recursion left and right finds further boundaries. Two switches: **analyse** (report only) and **reject**; both default off.

**What it decides — and what it refuses to:** a direction plus a span. A drifting file has NO valid single offset, so a correction value printed here would be read as an instruction and would be wrong. The gate therefore never emits a shift.

**Measured, on the reference set:** a clean control file yields **0 findings**; steps planted at a known time and size come back **6/6**; genuinely drifting files give **77 % recall at 36 % precision**, positions scattering **±1–2 min**. The scatter is set by the material (an SDH cue leads the speech by a per-cue varying amount, ~1.5 s MAD), not by the search.

**Rejected approaches, with their numbers** — recorded so they are not retried: nearest-island single cue (21 % precision — in dense dialogue the nearest start is always ~0 s away, so the likelihood flattens and the offset lands anywhere: measured +10.70 s against a truth of +4.65 s); binary-mask cross-correlation (found ±25 s jumps in the KNOWN-CLEAN file, scatter 12.7 s); full-surface correlation vs. the anchored method (2.37 s vs. 2.49 s mean error — equally poor, because a file drifting from −2 s to +10 s has no valid single offset, so every number is an average over the drift); joint optimisation over k windows (synthetic 2/6 vs. 6/6, recall 22 % vs. 77 % — with a free offset per segment each extra boundary pays for itself, so k pins to its upper limit).

**Cost and failure posture:** one full audio decode per candidate file (~14 s per 44 min episode, measured). No ffmpeg, unreadable audio, no speech, too few cues → the gate reports "did not run" and the file passes, like every other gate. **Tests: T108 (synthetic: clean / planted steps / ramp, plus the factored-marginal equality), T109 (end-to-end on a real episode: the plain subtitle steady, the SDH variant drifting).**

**F-M46 [D]:** **Overall selection:** one best candidate per (item, language) by combined score from F-M43–F-M45 — the best CORRECTED one where a correction can be proven (F-M318/F-M319). No candidate passing → the language counts as "not available".

### 4.2 Dry Run — Download

**F-M277:** **The download dry run runs the search and the selection, and stops before the fetch — the search quota IS spent.**

It runs: the id quality gate (F-M151b), the searches (F-M241), the release scoring and ranking (F-M44), the FPS tolerance filter (F-M43), the skip of candidates QA-rejected in earlier runs (F-M50), and the choice of the best candidate per language.

It does not run: the file fetch, the quality gates of 4.1 (language verify, minimum cue count, runtime match), the file write, the download mark and the registry write.

The report names, per language, the chosen release with its score and its hearing-impaired flag. While the hearing-impaired switch is on, the candidate from the hearing-impaired pool is named as well (F-M241).

**The report stops at the candidate, not at the file.** A dry run fetches nothing, so it cannot know the byte size, and it never reaches the point where the name is built from the fetched file — the hearing-impaired marker of the F-M260 name is therefore NOT part of a dry run. What a dry run answers is *which release* per language, not *which file*.

**It stops before the download call,** so no file is transferred — but the searches DO cost API quota, one per language set (two while the HI switch is on, F-M241). The GUI text must name both: "without saving files" and "without API calls" are not the same claim.

The run's stored statistics stay untouched (F-M247).

### 4.3 Auto-Sync — Download

**F-M296 [D] (development):** **A fetched subtitle is shifted to the audio: by the ONE constant offset
the detector measures while that offset is constant, and by a STAIRCASE — one offset per segment —
when it moves (F-M300).**

The feature has two halves that must not be confused: **which** track the audio is read from, and
**whether** the file is then moved. Both were measured against this library before either was built.

#### 4.3.1 Method — the audio track is chosen by LANGUAGE, not by stream order

**Rule.** Priority: **(1)** a track whose language equals the subtitle's language, **(2)** an English
track, **(3)** the first track without a language tag, else the first track at all.

**Why a rule was needed at all.** Both audio-reading gates decoded `0:a:0` — the first audio stream —
with a comment defending the hard index, and the reasoning behind that comment is sound about
*what to do when a stream is missing* while being wrong about *which stream to read*. Measured over
**304 files of this library that carry sidecar subtitles**, the rule above picks a different track than
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
assumption *"the first track is the right one"*, which is measurably false on ~9 % of these files. **This is no longer a setting**, because it is not a choice: choosing by language is the right answer, so the page no longer offers a switch for it (F-M303, F-M304). The configuration entry `QaDownloadAudioTrackByLanguage` remains in the file with its default `true`; forced off, the old hard `0:a:0` is read again.

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
sync** — caught by the end-to-end test, not by reasoning. `MinShiftSec` is **1.0 s**, at which a finding
is at least twice the floor and applying it can still be expected to remove more error than it adds.

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
  cue is **clamped to 0**. **Supersedes the former whole-file refusal** (operator order, 07.10.2026:
  *"Bitte immer um die Berechnung verschieben. Negative cues auf 0 setzen"*). **Rule.** The fit decides
  the offset; the clamp only stops a cue running off the front of the file. A refusal may not replace the
  fitted value — it discarded the whole correction over one leading cue and reported the file as clean
  (`no proven gain`) while it was **9.00 s** out of sync. The clamp changes one cue's relation to its
  neighbour; that is the smaller price, and the fitted shift stands. **Test: T110.**

**Applied to times only.** Text is carried through byte-for-byte, the cue count and cue order are
unchanged, and the corrected file is written **canonical** (UTF-8, no BOM, LF, trailing whitespace
trimmed) — the same form F-M185 establishes and the content hash describes. The fetched original is
untouched by this: it keeps its own byte style inside the archive (F-M306), because that artefact is
the uncorrected file. **Operator order (07.10.2026):** `Ich wollte wenn ich Dateien ändere sie dann auch
gleich normalisiert schreiben.` The former rule wrote the corrected file in the fetched payload's own
byte style, which made the stored hash describe a byte sequence that existed nowhere on disk; the two
agreed only because `NormalizeSrt` happened to strip exactly what the style put back. Normalizing on the
way out makes written bytes and hashed bytes identical **by construction**, so the correction and the
hash can no longer disagree.

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
below `MinGapSec` must not be shrunk further: measured on
a real episode, **560 of 724 gaps are below 0.04 s** with a median of **0.002 s**, so demanding the floor
everywhere makes the guard fire on nearly every cue and carry one shift through the whole file.
**Getting this direction wrong is not cosmetic**: the synthetic test measured a **1.92 s** residual and
**22** guarded cues with the sign reversed, against **one misplaced step** and **6** guarded cues with
it right.

**Test: T114.**

**F-M305 [D] (development):** **A real-material score is taken against the AUDIO, never against a
subtitle.** The synthetic episode T114 runs on puts the boundaries where the test put them, so it cannot
fail on boundary placement — the one thing that decides the result on real audio. A real-material score
is therefore required, and its yardstick must be independent of every subtitle: **the audio envelope
itself**, the same input the correction reads. The detector runs on it and the result is read as the
**per-segment offset curve** — a file that is in sync yields offsets near zero and a small span, a file
that still drifts yields several segments far apart.
**A subtitle must not be the yardstick, because that makes the measurement circular.** The residual of a
correction is computed RELATIVE to the reference, so a reference that itself drifts makes the result look
better than it is, and the error is counted twice — once in the reference and once in what is measured
against it. A reference may be used to **find** a defect, never as the number a requirement is accepted on.
**The control run comes first, and it decides whether a file may be scored at all.** The untouched
material goes through the same measurement before anything is corrected: an untouched file that already
looks wrong means the yardstick is misaligned to that material. The **order-guard count is read together
with the result**, because a guard firing on hundreds of cues flattens the staircase into one constant
shift while the step heights still look right. **Test: T117.**
**F-M300 [D] (superseded by F-M307, 07.10.2026):** **A subtitle whose offset MOVES is repaired segment by segment — one
offset per segment, the boundaries the drift gate already found — instead of being left as downloaded.**

This supersedes the "a drifting file is only reported" clause of F-M296. The average describes no real
state, so it must not be applied; the **segments** are not an average, they are the steps themselves.
Method, measurements and the two measured failures: §4.3.3. **Test: T114.**

**F-M302:** **A user-facing text must not contradict the code, and length plus a ban list cannot carry that.** A short sentence stating the opposite of the code satisfies a length cap AND a ban list, so T113 also matches the **refuted claims** — `cannot correct` / `can not correct`, `has no valid single correction`, `is only reported`, `never shifted`, `cannot touch` — across **every user-visible text node**: the section intros, the per-switch `fieldDescription` blocks **and the switch labels**, since a description-only scope misses a label. The list stays narrow on purpose: it names the refuted phrasings, not every mention of drift, because a check that fires on correct prose gets disabled. **Test: T113, extended.**
**F-M303:** **The correction sections are ONE section, and it carries ONE switch.** One block, one switch, `QaDownloadAutoSync`, governing the ONE correction — and the block carries **no intermediate heading**: the switch is the entry point, not a heading above it. What the switch does sits in its **own** `fieldDescription`: the audio path of §4.3, constant as one shift and moving as a staircase. The audio track is chosen by language (§4.3.1) — not a choice the operator makes, so it carries no switch. **The behaviour stays.** The removed entries keep their defaults in the configuration file (`QaDownloadAudioTrackByLanguage` `true`; `QaDownloadDriftCheck` and `QaDownloadDriftReject` `false`), so an existing installation behaves as the page shows. **Test: T113, extended.**
**F-M304 [D] (development):** **A switch switches the whole of what it names, and the page offers no switch for something that is simply right.**

**Rule — the switch governs every route that moves the file.** One visible switch, `QaDownloadAutoSync`, governs the constant shift (§4.3.2) **and** the staircase (§4.3.3) — every path that moves a file. A switch that does not switch what it names is worse than no switch.

**Rule — the absence of a removed control is asserted, not assumed.** A check that asserts only PRESENCE stays green over a stale checkbox left in the markup. The failure mode that matters is the other one: a leftover `getElementById` on an element that no longer exists returns `null`, the next property read throws, and the throw aborts the whole load handler — every binding on the page dies although the surviving switch's own markup is present and correct. The check therefore asserts the removed ids (`QaDownloadDriftCheck`, `QaDownloadDriftReject`, `QaDownloadAudioTrackByLanguage`, `QaDownloadAnchorSync`) are absent from the markup **and** from the inline script, and counts the section's checkboxes and fails unless there is exactly **one**.

**Test: T116.**

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
(`IsContentKnown`, `IsSidecarUploaded`, `SidecarRejectedReason`) keep seeing the truth. **A correction
must never lift a duplicate guard** — had the hash been registered over the pre-shift bytes, every
corrected file would have looked new and been re-downloaded and re-uploaded.

**Why the name sits AFTER `.srt`.** The sidecar listing is
`Directory.EnumerateFiles(dir, baseName + "*.srt")` (F-M251). Measured against that pattern: a name
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

**F-M306 [D] (development):** **The untouched original is kept as a selectable sidecar in the reserved slot block, and is locked against upload.** *(Superseded the one-entry archive on 07.10.2026 by operator order.)*

**Rule.** After a correction the pipeline writes the original beside the corrected file as a **loose sidecar** `<base>.<lang>.99.srt`, **numbered from 99 downward** and taking the first free number down to 90. It carries the corrected file's own language token and its `.sdh` marker when the corrected file is the variant; it carries **no marker of its own** — an unsynchronized original is not a forced subtitle, and saying so would make the file read as something it is not. The reserved block is **90–99**; the writer of *corrected* files therefore stops at slot **89** (F-M315), so a corrected file can never be handed the name of an original. When all ten reserved slots are taken, **no original is written** — the pipeline logs that and refuses, rather than inventing a name that would overwrite one.

**Why a loose file and not an archive.** The archive could only be reached by unpacking it by hand. As a sidecar it is **a track the operator selects in the player** — Jellyfin indexes a `.srt` beside the media file by its name, and a numbered slot becomes the track's title. Measured on the test instance 07.10.2026: `For All Mankind S02E01.en.99.srt` came back as `idx=0 lang=eng title='99' ext=True`, next to `title=None` for the unnumbered file. The archive is **no longer written**; archives already on disk are left untouched.

**The lock, and why nothing new was needed.** The original is registered exactly like **every other downloaded subtitle** — `MarkDownloaded`, a `downloaded` row keyed on the content hash of the bytes on disk. Nothing else was invented, and there is no new status and no new reject reason. It locks because the seeder's `IsContentKnown` counts every row **except `observed`** as known content and the uploader applies the same test, so:
- the seeder drops the file from the queue instead of queueing it, and
- the uploader returns `duplicate-content` without spending a search request.

Both are existing code paths that every downloaded subtitle already travels. The bytes are written **canonical** (F-M296) and the row hashes **those** bytes, so the row and the file describe the same sequence — writing the fetched byte style instead would leave the row pointing at a sequence that exists nowhere and the file would read as unknown and be uploaded.

**Both tracks, one helper** — the HI variant goes through the same one, so the two cannot drift apart. **Failure posture:** a failed write is logged as a warning and never undoes the corrected file, and a directory that cannot be listed refuses the write rather than guessing at a free slot. Governed by `QaDownloadAutoSync` (F-M304) — no second switch.

**Test: T118.**

**F-M315 [D] (user decision 07.10.2026):** **The slots 90–99 are reserved for kept originals; corrected files stop at 89.**

**Rule.** `SidecarNaming` carries the reservation as a pure property of the number: `IsOriginalSlot(slot)` is true for 90–99 and for nothing else. `PlanTarget` — the name a *corrected* sidecar is moved to, and the name the downloader composes — never returns a reserved slot: it walks 1…89 and, when every one is taken, returns slot 1 so the caller's own existence check refuses the move. `PlanOriginalTarget` walks **99…90** and returns **null** when all ten are taken. Slot assignment for originals is downward on purpose: corrected names grow up from 1, originals come down from 99, and a viewer sees the reserved number in the track list (F-M316).

**Why the cap is not cosmetic.** Without it, the next corrected file of a language whose slots 1–89 are taken would be handed a name an original already occupies — the correction would overwrite the very file it exists to keep. Measured 07.10.2026 against the loop bound of 999: with 1–89 taken the corrected writer returns slot 1 (`<base>.<lang>.01.srt`, F-M316), never a reserved number.

**Test: T128.**

**F-M316 [D] (operator order 08.10.2026):** **The slot is always written, always two digits; it is the track order.**

**Rule.** `SidecarNaming.Build` composes every sidecar name as `<base>.<lang>[.sdh][.forced].<NN>.srt` with `NN` = the slot as `D2` (`01`…`99`). Slot 1 is no longer the bare `<base>.<lang>.srt`. The number stays the LAST name token, so `Parse` and `ReadFlags` read it exactly as before — they accept one and two digits, so names written before F-M316 keep parsing and no migration shim exists.

**Why the number is load-bearing.** Jellyfin indexes a sidecar as an external track and orders the track list by the FILE NAME, not by any property of the file. A name is therefore the only ordering signal the player has. Two forms broke that order: a bare `en.srt` sorts AFTER `en.99.srt` (the `s` of `.srt` is greater than `9`), and an unpadded `en.2.srt` sorts after `en.10.srt`. Measured on prod 08.10.2026 (BCS S01E05, six sidecars): the menu offered `99 – German` ABOVE `German` — the kept originals listed before the corrected files, the exact inverse of what F-M315 reserves them for. With two digits the list runs languages alphabetically and, within a language, `01`…`89` (corrected) before `90`…`99` (originals).

**Cost, stated because it is real.** Names written before this rule keep their old form until they are renamed; the pipeline only writes new names. Renaming an existing library is a separate, explicit operation — nothing renames on disk by itself.

**Test: T129.**

**F-M317 [D] (operator order 08.10.2026):** **The slot encodes WHICH KIND the file is: aligned takes 01 upward, unprocessed takes 99 downward.**

**Rule.** A downloaded subtitle is written into one of two ranges, and the range is decided by one question: was it ALIGNED?

- **Aligned** (the fit applied a correction, or the fit switch is off and nothing was moved but the file is the corrected artifact) → `<base>.<lang>.<NN>.srt` with `NN` = `01`, `02`, `03` … **upward**, counted by its own counter.
- **Unprocessed** (the fit found the file **WRONG** — a shift beyond the limit, a first cue that would go negative, too few cues — or the fit is off and the file is left as downloaded) → the **reserved block**, `99`, `98`, `97` … **downward**, exactly as an original was numbered before.
- **`no proven gain` is NEITHER of the two** — see F-M321. The fit found nothing wrong with the file, so the file IS the best version: it takes `01` upward **and** a byte-identical copy at `99` downward. It is not "unprocessed", because nothing about it needs processing.

**"Regardless of whether a corrected version exists"** is the operator's own wording and it is the load-bearing part: an unprocessed file takes a reserved slot **even when there is no corrected sibling beside it**. The reserved block therefore stops being only the home of *the original of a correction* and becomes the home of **everything that is not the corrected file** — including the byte-identical copy of an already-good file (F-M321).

**The counter must be separate from the corrected slot number.** With one shared counter an unprocessed file consumes slot `01` and pushes the next aligned file to `02`, while the rule the operator stated is that alignment starts at `01`. `correctedSlotCount` and `hiCorrectedSlotCount` are therefore incremented on the aligned path only — and the already-good case of F-M321 counts as aligned, because `01` is where its best version belongs.

**A file that is not aligned IS the original, so it is written ONCE — unless it is an already-good file.** On the unprocessed path the subtitle lands in the reserved slot, `unsyncPayload` stays null, and the second write is skipped by its `unsyncPayload != null` guard: writing it again would put the same subtitle on disk twice. An already-good file (F-M321) is the deliberate exception: it takes `01` AND `99`, and the reserved write REUSES the array written to `01`, so the two are byte-identical rather than two separately encoded copies.

**When all ten reserved slots are taken** the write is **refused** and counted (`summary.Failed++` on the main path, `summary.RejectedCandidates++` on the HI path) with a distinct log line — this is the one path where a fetched subtitle is not written at all, so it must not be silent. Inventing a name would overwrite an unprocessed file already kept.

**Both tracks carry the rule.** The HI branch follows the same two ranges with its own counter, for the reason the HI pool is where the drift lives: it is the pool most likely to land in the reserved block, and one rule on one branch only would put an aligned file at `01` beside an unprocessed one at `01`.

**What this changes about F-M315.** F-M315's reservation (90–99, corrected stops at 89) stands; its *scope* widens. The block no longer holds only the original kept beside a correction — it now also holds every file that could not be aligned. `CorrectedSlotMax = 89` remains correct and unreachable in practice: the aligned writer counts from 01 with its own counter, so it does not walk up into the block.

**GUI text (operator wording, 08.10.2026, updated the same day for F-M321).** The switch description reads: *"Aligns a fetched subtitle segment-wise on a spoken track. A file the alignment finds nothing wrong with is kept as the best version: it takes index 01 upward, together with a copy of it from index 99 downward. A file that could not be aligned — or that is out of sync beyond repair — is kept from index 99 downward as well. Default: on."* The sentence had to change with F-M321: it used to say that *any* file the alignment could not process is kept from 99 downward, which is now true only for the files that are actually WRONG — a file the alignment found nothing wrong with takes 01. Leaving the old sentence would have shipped a page contradicting the code, which is what F-M302 forbids.

**Test: T130.**

**F-M318 [D] (operator order 08.10.2026):** **The download REPEATS until a candidate's correction proves itself, capped at the QA retry limit.**

**The rule.** A fetched subtitle whose correction the fit REFUSES does not end the walk. The pipeline keeps fetching lower-ranked candidates — up to **`DownloadQaRetryLimit`** candidates refused by the fit (default 3, the same knob the QA-reject give-up already uses; `0` = no cap) — and stops at the first candidate whose correction proves itself against the audio.

**Why the walk used to end at the first refusal.** The save counter drove the walk's stop, and an unprocessed file is memorized as a download (F-M317 keeps it) and incremented it — so the loop broke right after the first refusal and the "repeat" could never happen, leaving the item with one unprocessed file. The counter that decides the walk is **`correctedSaved`**, and with the count setting removed the stop is simply the first corrected file (F-M319): an unprocessed file is a FALLBACK, never a "best".

**"Refused" is not one thing.** Only a verdict on the FILE justifies another download. A verdict on the AUDIO or the tooling (`ffmpeg not available`, `audio decode failed`, `not measured: no audio samples`) would come back word for word for every candidate, while each attempt still costs daily quota and writes another unprocessed file — up to ten for one episode. Such a refusal therefore ends the hunt, and says so: `correction hunt abandoned after N candidate(s): the fit refused for a reason no other candidate can change (<reason>)`. The distinction lives in `SubtitleSync.RefusalIsCandidateSpecific` — one predicate, exact prefixes, so "not measured: only 12 cues" (about the FILE, hunt continues) and "not measured: no audio samples" (about the AUDIO, hunt stops) cannot be confused.

**Every refusal is still kept.** Each refused candidate lands in the reserved block as an unprocessed file (`99`, `98`, `97` … F-M317), so the repeat never costs content — and the hunt therefore also has a physical ceiling: when the ten reserved slots are taken, the write is refused and counted (F-M317).

**The order is a precondition, not a detail.** The repeat sits AFTER every other download gate (FPS, no-bytes, language verification, structure, min-cues, runtime). A candidate whose CONTENT fails a gate never reaches the fit, so a download spent on one would be spent on a file the plugin is about to discard — the walk is worth continuing only because everything admitted to the fit has already passed those gates. The order is asserted as a property (T131), so a later edit that moves the fit above the gates goes red instead of silently costing quota.

**The interaction with `DownloadMaxCandidatesPerLanguage`.** That budget still caps the total fetches; the refusal budget is a second, independent stop. Whichever is reached first ends the walk, and each has its own log line naming how many candidates were left untried.

**Test: T131.**

**F-M320 [D] (operator order 08.10.2026):** **The QA retry limit bounds the FIT. No other gate gives up, and nothing closes a pair.**

**The rule.** `DownloadQaRetryLimit` (default 3) is the fit's budget — how many candidates may be fetched hoping for a provable correction (F-M318). It no longer counts anything else. A gate rejection (language, structure, min-cues, runtime, FPS, no-bytes) is **not** a budget: the candidate is discarded, counted in `RejectedCandidates`, memorized by release id (F-M200, so it is never fetched twice), and the walk moves on.

**What was removed, at all four places it lived.** The failed-run counter (`QaFailTracker.RecordFailure`/`IsExhausted`) incremented once per saveless run of a (item, language) pair and, at the limit, the pair was CLOSED — by the pipeline's own work list, by the seeder's queue gate, by the refresh task's "actionable" filter, and by the run-end "language marked not-available" verdict. All four are gone with the counter, and so are the two status counters that reported them (`SkippedQaGiveUp`, `QaGiveUpLanguages`).

**Why it had to go.** It was the second give-up in a system that now has a designed one. A file whose candidates are all badly ripped used to be declared "settled as unavailable" after N rejections and was then never searched again — indistinguishable, in the record, from a language SubDL genuinely does not carry. With the counter removed the file is searched again every cycle, and what bounds the work is the download budget per run (F-M50) plus the burned-release memory (F-M200), neither of which hides a live file.

**What `QaFailTracker` still is:** the burned-candidate record and nothing else. Its fail counter, `IsExhausted`, `RecordFailure` and the `qa-fail:` keys are gone; a reset after a real save now clears only the burned candidates.

**Test: T132.**

**F-M321 [D] (operator order 08.10.2026):** **A file the fit finds nothing wrong with is a GOOD file: it is written at 01 upward, its original at 99 downward, and the walk ends.**

**The rule.** `no proven gain` is not a failure — it is the deploy rule DECLINING to move the file. Nothing had to move, or the measured gain did not prove itself, or the needed shift sits below the measurement floor. In every one of those cases the file as downloaded is the best version of that language that exists, and the operator's order is that it is kept as what it is:

- it is written as a **good file at `01` upward** (`<base>.<lang>.01.srt`, then `02` … F-M316) — the same range a genuinely corrected file uses, because this file needs no correction;
- it advances the **corrected counter**, so `01` is used by the good file and the reserved block stays free for files that are actually wrong;
- the **original is written as well**, into the reserved block (`99` downward) — the operator's words are "beide original": nothing was corrected, so BOTH files carry the original subtitle, and they are identical by construction because the reserved write REUSES the array that went to `01` rather than re-encoding the text;
- the walk **ENDS** there. The hunt of F-M318 exists to find a candidate whose correction proves itself; against a file the fit found nothing wrong with, no candidate can beat it, so continuing would spend the daily quota and write up to ten unprocessed files for one episode.

**What still keeps the old behaviour.** Only the deploy rule's OWN refusal counts. A refusal that says the file is **wrong** — a shift beyond the limit, too few cues, a first cue that would go negative — keeps the reserved range and keeps the F-M318 hunt alive, because another candidate may well fit. The two are told apart by `SubtitleSync.RefusalMeansAlreadyGood`, matched on the exact `no proven gain` prefix the class composes itself, so the prefix is the contract rather than a guess at wording. A tooling verdict (`ffmpeg not available`, `audio decode failed`, `no audio samples`) is likewise not "already good", and `null` never is.

**Both tracks carry the rule.** The HI branch answers the same question with its own counter (F-M317), which matters most here: the HI pool is where the drift lives, so it is the pool most likely to produce a file that is already fine.

**Why the 99 copy is byte-identical rather than a second artifact.** For a real correction the 99 copy is the ORIGINAL text and therefore differs from the corrected file at 01 — that case is untouched. For an already-good file there is nothing to correct, so "the original" and "the best version" are the same bytes; re-encoding the text a second time would let the two files differ by encoding alone, which would break the identity the operator asked for. Reusing the written array makes them identical by construction. The registry row is content-keyed (F-M186/F-M199), so one row describes both files — and since a downloaded row is what locks a file against upload (F-M315), both copies are locked by it.

**Test: T133.**

**F-M322 [D] (operator order 08.10.2026):** **The auto-sync has its own status row, the same words as every worker; and the statistics count only the alignments that really happened.**

**The rule.** The alignment's status is its own readout, in two places:

- a **row of its own** in the Workers list, next to Download — `Autosync`, kept as `WorkerRunRegistry.AutoSyncWorkerKey`. It is separate because the Download row cannot answer the question the operator watches: a run can fetch forty files and align none, and one word cannot tell those two apart;
- the **same light mirrored under the download switch**, which is where the switch it describes lives.

**ONE source for both readouts.** The mirrored light reads the row the Workers renderer has just built; it does NOT fetch again. A second request could return a different run than the list directly above it, and the two would then disagree on the same screen.

**The words and colours are the existing ones** (F-M268), with nothing invented for this row: `ok` green when the run aligned something or found it already in sync, `skipped` grey when the alignment is switched off or nothing was measured, `failed` red when the run failed and aligned nothing. A DRY RUN reports `skipped` with its own note: it writes no file, so it cannot have aligned anything.

**The statistics count successful auto-syncs only.** The row is `Downloads: Auto-Synch` and reads `FittedToAudio`, which advances ONLY where a correction was really applied. An already-good file (F-M321) therefore does NOT reach it: nothing moved, and counting it would make the row claim an alignment that never happened. Those files have their own counter (`AlreadyGoodAsDownloaded`), which is also what turns the light green.

**The switch says what it does.** The setting's label reads *"Automatically synchronize subtitle to spoken track"* — the operator's own wording (08.10.2026). It replaced *"Correct subtitle timing"*, which described the effect rather than the action.

**The postprocessing row is named for its direction:** `Upl. Postproc.` — it is the upload direction's work, and the bare `Postproc.` left that unsaid.

**Test: T134.**

**F-M307 [D] (development, 07.10.2026):** **The offset is a piecewise-constant function of time, fitted by exact dynamic programming. Supersedes the recursive Bayes-factor gate (F-M295) and the staircase applied from gate boundaries (F-M300).**

**How the speech is recognised.** One audio track is decoded (chosen by language, §4.3.1), mono, 16 kHz, band-passed **300–3400 Hz** — the band that carries voice. The signal is cut into **20 ms frames** and each frame's RMS is taken as its level in dB. The levels are normalised against the file's own distribution: the **10th percentile** of frame level becomes 0 and the **90th percentile** becomes 1, clipped. The result is `p(t)`, a **speech probability between 0 and 1 per frame**. The method is **threshold-free** — no absolute level is assumed, so any recording level works, and it adapts to the file rather than to a calibration. `p(t)` is then dilated by a **0.30 s max-filter** (`SLACK`), which absorbs the fact that a subtitle boundary is not frame-exact and sharpens the peak.

**What is compared, and how.** Nothing is cross-correlated and no matrix of coefficients is formed. The comparison is a **single scalar per candidate shift**, and it is an **average, not a correlation coefficient**: nothing is centred, nothing is normalised against a second curve, and no Pearson or cross-correlation figure is computed anywhere in the method.

The comparison is built in two steps:

1. **Per cue.** For a candidate shift `d`, cue `c` covers the frames of the window `[start_c + d, end_c + d]`. The mean of `p(t)` over exactly those frames is that cue's score. It is read from a **cumulative sum of `p(t)`**, so the mean over a window costs one subtraction regardless of its length. If the shifted window would fall outside the audio at either end it scores **0 and is excluded**, never truncated to the edge — a truncated window is short and its mean is therefore too high (see the regression case T121a).
2. **Per candidate shift.** The score of `d` is the **arithmetic mean of the per-cue scores**, taken over the cues that are being scored for that `d`. A higher value means the cues sit on more speech. Chance level is the file's own mean of `p(t)`: if cues and speech were unrelated, the score would land there.

**The result is a table, and the table is what the fit reads.** Evaluating all 161 candidate shifts (`−20 s … +20 s`, step `0.25 s`) for every cue yields `S[cue, shift]` — the per-cue score at every shift — plus a flag per entry saying whether that window fitted inside the audio. The fit never touches the audio again; it works on this table alone. That is what makes an exhaustive optimum affordable.

**Why not a correlation coefficient.** A coefficient divides by the spread of both curves, which is only meaningful when both are free to vary. Here the audio curve `p(t)` is fixed and every candidate shift is one number measuring *how much speech the cues land on*; the quantity to maximise is therefore a mean, and the fitted charge is calibrated to the **spread of that mean**, not to a coefficient. Cross-correlation and a product form of the two curves were both tried on this material and both failed; the records are in `METHODIK.md`, section 4, so they are not retried.

**The fit.** The offset is not one number but a function of time, and the function is chosen by maximising

    sum over cues of (score at that cue's shift)   −   Z · sigma · sqrt(cues in the segment)

over **all** placements of change points, where `sigma` is the median absolute change of a cue's score per grid step, read from the data. The optimum is found **exactly** by dynamic programming over (block, state) pairs: blocks of **`BLOCK_CUES = 40`** cues, candidate shifts on a **0.25 s** grid from **−20 s to +20 s**, then the accepted boundaries are refined off the block grid. **A constant offset, a slow drift and a single hard cut are the same fit** — one segment, many segments, or two.

**Why the charge is sqrt-shaped.** A segment of `L` cues may pick the best of all candidate shifts, so it gains by luck alone an amount that grows as the square root of `L`. Charging `Z · sigma · sqrt(L)` means a step must earn more than its own luck to be kept, and the charge grows faster than the luck available. Splitting is never free. This is the rule that keeps the fit from inventing steps; a fixed shift-size threshold cannot do the job, because a threshold cannot distinguish a real small step from noise.

**The do-nothing fit competes.** One segment with shift exactly **0** is always an alternative inside the same objective, evaluated with the shift **fixed at zero** — not free to take the best value. A file with no drift therefore scores better left alone, and is left alone.

**Deploy rule — a correction must prove itself.** A shift is written only if all of the following hold: the cues the fit actually moves show a mean score gain above `DeployZ` standard errors of that mean (**paired test over the moved cues only**); the file as a whole does not get worse; and the largest shift is at least `MinShiftSec`. Consequences, both intended: a file already in sync is returned **byte-identical**, and a repeat run changes nothing.

**`DeployZ` is separate from `Z`, and the two must not be merged.** `Z` is the charge inside the DP and decides where segments are placed; `DeployZ` only decides whether a result is written. Tightening the deploy rule by raising `Z` would move the segmentation underneath it — the measured separation below was taken at `Z = 1.0`. Measured on **34 real fits of one series** (one morning, all language variants) against the operator's listening verdicts: every file he reported as audibly **wrong** sat at `t = 1.06 / 1.15 / 1.38 / 1.51`, every file he confirmed as **good** at `t = 2.66` and `8.45` — nothing in between, so the gap separates the groups completely, while `Z = 1.0` let all four bad files through because they cleared it by a hair. At `DeployZ = 2.0` the four are refused and no good file is lost. Refusal is the intended outcome there: those fits had landed on the wrong piece of sound (adjacent-segment jumps of **10.25–34.00 s**, against at most **5.50 s** for every good one), so leaving the file as downloaded is correct. Caveat to carry: this is a threshold fitted on the run it judges, so watch for a run of refusals and re-derive the bound as verdicts accumulate.

**Constants.** `Z = 1.0` (DP charge). `DeployZ = 2.0` (deploy rule). `MIN_SEG_FRAC = 0.12` — the shortest segment is a **fraction of the file's cues**, not a cue count, so the resolution is the same for a 45-minute episode and a feature film; floor 40 cues. `BLOCK_CUES = 40`. State grid `0.25 s`, range `±20 s`. `SLACK = 0.30 s`. `MinShiftSec` as in the refusal list of §4.3.2.

**Language-neutral.** The rule reads cue times and the audio only. Script, case and SDH notation change nothing, so there is no per-language calibration and no language-specific parameter.

**What it does not claim.** `p(t)` measures energy in the voice band; it does not classify speech against music, and it does not read words. It therefore measures *where the speech is*, which is what synchronisation needs, and nothing about what is said.

**Streaming — the decoder returns the FRAME LEVELS, never the samples.** **Rule.** The audio is read in
chunks and reduced to one level per **20 ms frame** as it arrives; the samples are never materialised as
an array. The decoder's return value is the level curve, because the levels are the only thing any
consumer reads — `SpeechProbability` and `SpeechIslands` both walk the frames and compute nothing but
`20·log10(sqrt(mean(v²)))`, and no code reads an individual sample outside that loop. **Consequence: the
levels are never persisted** — no database column, no cache, no file. They live for one fit and are
dropped. **Why.** The whole track in memory costs **~1 GB peak** at 140 minutes (a 514 MB `byte[]` and a
514 MB `float[]` alive at once) against **3.2 MB** streamed; the fits are **bit-identical**, and the
140-minute files could not be fitted at all under the old path (the OOM killer took them). **Test: T122.**

**Cost and failure posture.** One audio decode per candidate file, streamed. No ffmpeg, unreadable
audio, no speech, too few cues ⇒ "did not run" and the file passes, as with every other gate, and
**nothing is written**. The correction is applied to **times only**: text is carried through
byte-for-byte, cue count and cue order are unchanged. Order guard last: no cue overtakes its
predecessor's end, and a cue that would land below zero is **clamped to 0** — the fitted shift is
applied as measured (as in the list of §4.3.2).

**Tests: T119, T120, T121.** Reference implementation: `/opt/data/drift-lab/RC/sy-1.0.0-rc1` (`core4.py` the fit, `drift_algo5.py` the runner); `CURRENT-RC.txt` names the active candidate. Reasons, measurements and the records of rejected approaches: `METHODIK.md`, section 4.
## 5. Upload Postprocessing

**F-M287 [B1] (user decision 02.10.2026):** **Everything a run writes that is reachable from a dry run sits behind the dry-run flag — the guard belongs at the WRITE, not at the mode's exit.**

Measured 02.10.2026, both directions: the refetch stamp, the file-retry counter, the id-resolution budget, `EnsureMedia`, `ObserveEmbed` and the file-missing deletion all executed in a dry run, and the dispatcher marked every reported item `Done` and deleted it from the cycle queue — one dry run consumed the work it described. The guard rule: **a write is dry-run-guarded at its own site**, so moving an exit cannot silently expose it. Sanctioned exceptions: a write that makes state stale-free rather than claiming a fact (recording a success, clearing a stamp), and the worker-run record, which reports that the run happened. **Test: T97, L1–L6.**

**F-M184:** **Duplicate handling delegated to postprocessing:** the upload path does not search SubDL for duplicates before uploading. A duplicate upload is accepted by the API ("sent for review"), resolves to `rejected` on the SubDL dashboard, and the postprocessing job deletes that entry and marks the local row `remote-duplicate`. This removes one search call per item from the daily search quota; the cost is one upload per duplicate. A local self-echo guard (registry, per media/language pair) still prevents uploading the same pair twice.

**F-M227:** **Every plugin task appears under one heading in the dashboard.** Only `SubDL Postprocessing` reported the category `SubDL Scribe`; the other four reported the default category, so the dashboard split the plugin's work across two groups. Rule: every task this plugin schedules reports `Category => "SubDL Scribe"`; a new task must not inherit a Jellyfin category. **Test: T42.**

**F-M223:** **Every name the user sees says SubDL Scribe — the ASSEMBLY name is the one thing that must NOT follow.** Jellyfin's logger derives its category from the type, so the namespace says SubDL Scribe. The embedded-resource names move with the namespace.ml`/`.js` and the embedded-resource read literal), and all three must change together or the configuration page fails to load. **the assembly name stays the assembly name** — Jellyfin derives the plugin data folder and the configuration file from it, so renaming would orphan the credentials, the state database and the run history. The assembly name is a persistence key, not branding. Free text follows (postprocessing the category, console tags, reset dialog, GPL headers); API routes stay the plugin's own routes. **Test: T38.**

**F-M210:** **Every scheduled job is driven by its OWN setting.** Database refresh, OSHash refresh and upload postprocessing each carry their own cadence setting and their own diced anchor, and must fire on them regardless of any other job's setting. **One exception, and it is not a return to the old coupling:** postprocessing additionally requires the upload direction to be ON (F-M291) — not another *job's* setting but the direction it works for, since with upload off it has no subject at all. the cycle interval governs **only** the automatic pipeline cycles; `Manual` and `OnArrival` there suppress those cycles and nothing else. A job must never sit behind an early return belonging to a different job's configuration.

**F-M210a:** **A service with no manual start button offers "Never"/"off", never "Manual".** `Manual` means "triggered by the dashboard button"; where no such control exists, the disabling option is `Never` (prune, OSHash, postprocessing).

**F-M212:** **No fire may be consumed before its task is registered.** Jellyfin's task queue drops the fire (logging `Unable to find scheduled task of type "X"`) when the target task is not yet registered. Every fire path tests the registration **before** it consumes its slot (no marker set, no reschedule counter reset), so the next 30-s tick retries. This applies to **all six** paths: database refresh, OSHash refresh, postprocessing, the refetch cycle, the F-M156 catch-up and the F-M65 recovery fires. **Test: T31.**

**F-M131:** **Manual stop via marker files:** the Stop button creates `.stop-upload` and/or `.stop-download` in the plugin data directory. Each pipeline checks its marker before processing the next item (and, for uploads, between streams of the same item), cancels the direction, deletes the marker and reports the stop marker. The dispatcher ends the cycle without starting further directions. Stop markers do **not** affect the delayed postprocessing task. The canonical endpoint for programmatic stops is `POST /Plugins/SubdlSync/Stop?direction=upload|download|all`; `DELETE /ScheduledTasks/Running/{id}` cancels the Jellyfin task wrapper, but a running pipeline item may finish first.

**F-M17y:** **Delayed upload postprocessing:** after an upload run (normal finish or stop), on the postprocessing schedule (F-M176; not tied to the run and not to a fixed delay — SubDL review latency varies), the plugin queries `/user/mySubtitles` and resolves every locally pending-review entry whose status is `rejected`: an entry with "Duplicate upload" is deleted on SubDL and marked duplicate-remote; any other rejected entry is deleted without a mark. Accepted entries are not touched. All steps logged at Debug. The task runs on its own cadence and diced anchor (F-M210), independent of any run.

**F-M291 (user decision 03.10.2026):** **With the upload direction switched off, postprocessing does not run either.**

Its whole subject is work that only exists because upload is on: it resolves locally pending-review entries against `/user/mySubtitles`, and an entry can only be pending review if something was uploaded. With upload off there is nothing to resolve, so the task does not go looking — it does not call SubDL, does not touch the database and does not write a run.

Enforced at three points, and each one is needed:
- **the anchor is not armed** in the scheduler while `UploadEnabled` is false, so no fire is ever queued;
- **the task refuses** even so — a fire armed before the toggle was switched off, or one queued earlier, must not do the work either;
- **the manual endpoint** (`POST /Plugins/SubdlSync/PostprocessUploads`) refuses for the same reason and answers `{"status":"skipped","reason":"upload disabled"}` rather than silently doing nothing.

The recorded outcome is `skipped`/"upload disabled" — GREY, because nothing is broken and nothing ran; green would claim a run that did not happen. Wording matches the upload task's own line for the same condition. **Test: T104.**

**F-M176:** **Postprocessing reschedule spacing:** if postprocessing cannot start because the global run lock is busy (F-M94h: immediate `false`, no waiting), it schedules a one-shot re-fire in the job spacing (5–120 min). A local rate limit can no longer defer it (F-M20). The re-fire still respects the diced anchor and does not move the next regular run. A busy lock is first checked for staleness; only a living previous run causes a deferral.

**F-M94h:** **ONE global run lock for all six state-mutating components** (seeder, downloader, uploader, postprocessing, database refresh, OSHash refresh) — they all touch the same state, so one mutual exclusion is what the design needs. **Overlap is never waited out:** a caller that cannot acquire is refused immediately (a 20-s in-process hand-off grace) and reschedules itself by the job spacing.

**Stale detection:** dead holder PID → take over immediately; holder alive but stamp older than the 6-h run ceiling → wedged run, take over; file older than 24 h → take over. the startup cleanup deletes a leftover block file at plugin start. Fail-open: a malformed block file never wedges the plugin.

**F-M205:** **Every reschedule is counted; after 16 the rescheduling stops.** One counter per slot (upload, download, postprocess, database refresh, OSHash refresh, refetch); once a slot has been rescheduled 16 times it is refused. The database refresh carries two slot keys, one for its anchor and one for its reschedule fire. **The limit is 16** — a single constant; every log line renders `{Limit}`, so raising it touches no message text.

Giving up is logged as a **warning** exactly once; further refusals are logged at debug.

The counter is cleared by the reschedule reset on a real fire and by the per-day budget reset (F-M208). It lives in RAM only — a restart resets it, which is the intended fail-open.

**Not counted:** daily-limit quota-reset fires (a quota-reset fire) — they are driven by the reset, and dropping them would skip the direction outright. **Test: T24.**

## 6. Database Refresh

**F-M94:** **Database refresh as scheduler task:** a dedicated task reconciles the stored state with reality — it removes tracker state for Jellyfin items that are gone and verifies the file side of stored verdicts (F-M234). The task, its dashboard name and its log prefix read "database refresh" (`[SubDL-Refresh]`). It uses the global run lock and operates on the shared database.

**F-M274:** **The refresh runs its steps in one fixed order, in a single run, under the global run lock, and reports what it changed.**

**Step 0 — library probe:** the full item id set is resolved (movies, series, episodes). A library that is not queryable, or that answers with zero items, skips the whole run; nothing is judged.

**Step 1 — dead rows:** media and subtitle rows whose Jellyfin item is gone are removed, and the count is reported.

**Step 2 — guid-keyed trackers:** the search tracker, the file-retry tracker, the id-not-found tracker and the QA-fail tracker are pruned of rows whose item is gone.

**Step 3 — OSHash cache paths:** cached paths whose root is no longer listable are dropped.

**Step 4 — vanished subtitle files:** the sidecar verdicts of F-M234, and the open required FILES of F-M283 (reported, never repaired — there is no stored mark).

**Step 4b — embedded rows:** the check of F-M258.

**Step 5 — compaction:** the fold and the rebuild of F-M214.

A step that cannot read its evidence is skipped, never judged ("unknown ≠ deleted").

**The run's own line** reports the removed rows per area, the forgotten subtitle verdicts, the dropped download marks and the forgotten embedded rows; a run that changed nothing reports that the database matches reality. A run that cannot take the global run lock is recorded as `deferred` and re-fires later, never as failed.

**F-M234:** **The state prune is a database refresh: it verifies the FILE side of a stored verdict, not only whether the item still exists.**

A removed item is only the crudest case. A subtitle file deleted while its item stays keeps a verdict saying "uploaded"/"rejected"/"downloaded" and a download mark saying the language is settled — both permanently wrong, and the item is reported complete forever while the seeder keeps queueing it.

Fail-safe, same two-tier rule as the oshash cache: every root the stored paths live under must exist and list cleanly, else the file side is skipped entirely. Rows without a stored path are never judged. "Unknown ≠ deleted" — a missed refresh costs nothing, a false one destroys valid verdicts.

Subset rule: a stored language set that COVERS the configured one counts as complete, so removing a language does not invalidate every mark; adding one still does.

**What "the file side" is.** A mark is only stale when the language has lost its evidence EVERYWHERE the configuration counts it: no sidecar file AND no embedded track, or a track settled as unavailable. The refresh asks the same question the download pipeline asks, through the same reader.

**"Settled as unavailable" is read from the whole stored set**, not from the languages that failed the disk test. A language SubDL does not have has neither a file nor a track, so deriving the settled set from the missing list left it empty in exactly the case it exists for.

**A missing probe is not a deletion.** When the embedded half cannot be read (item unresolvable, unreadable stream list), the check falls back to the files alone rather than judging.

**F-M258 [D]:** **The database refresh checks the embedded side too, not only the sidecar side.**

For every media row whose Jellyfin item still exists and which has stored embedded rows, the refresh reads the item's current streams and forgets every stored row that disagrees — on the key (position) and on the recorded facts (language, hearing-impaired). A row whose position is gone, or whose language/HI no longer matches the stream at that position, is dropped.

The check is structural for observations only. A row carrying a verdict (`uploaded`/`rejected`) or a detection attempt is kept unconditionally; only a plain observation is dropped when position, language or HI disagree.

**The comparison is made against positions, not against the tracks whose language resolved.** A row is dropped when its POSITION no longer exists among the item's non-external subtitle streams; a position that exists but answered nothing keeps its row, and only a position that exists and answers differently is a disagreement.

Fail-safe, as the rest of the refresh ("unknown ≠ deleted"). An item Jellyfin no longer resolves, an unreadable stream list, and an EMPTY stream list are all skipped rather than judged. Only a NON-empty list that disagrees with a stored row is evidence.

The check runs only for files that HAVE stored rows, so it costs one stream lookup per file with state.

**F-M214:** **A database refresh compacts the database in the same run.** The prune is followed, in the same run and while it holds the global run lock, by the compaction: fold the journal in (the journal fold), then rebuild the file (the rebuild) to release the free pages.

**Compaction is measured as the whole footprint** — data file plus journal, before and after.

**It must never fail the task:** an exception is logged as a warning and swallowed. It is housekeeping, and the refresh's own work has already succeeded.

**F-M237:** **A rebuild that cannot release the pages is replaced by a rebuild from the file's own rows.**

Rule: when the library's own the rebuild fails, rows are read out, written into a fresh file, counted against the original, and only a matching count is swapped in. Rows are never traded for a smaller file.

The swap is a rename, not a copy: a running Jellyfin holds the file open, and a copy over it is refused with "being used by another process".

The previous file survives as exactly one timestamped backup, reachable through the database restore function. Nothing is deleted until the row count is proven equal.

The rebuild's side files are addressed by their STEM (`subdl-scribe-log.db`, `subdl-scribe-temp.db`), never by appending to the full name, which matches nothing on disk. **See T54.**

**F-M236:** **A failed compaction must not leave the shared database dead.**

The compaction is verified, not trusted: after a failed rebuild the database is probed with a real query (the compatibility row), and only a failing probe replaces the engine.

The repair preserves the data: the failed swap leaves the original file on disk, so rows survive. **See T51.**

## 7. OSHash Refresh

**F-M119:** **OSHash refresh as scheduler task:** a dedicated task recomputes expired or fingerprint-changed OSHash cache entries on **its own diced WEEKLY anchor** ("D HH:mm", drawn once at install and never re-rolled). It fires **every week**. It holds the global run lock while mutating the shared cache. The OSHash cadence bounds how long a cached fingerprint is trusted (`Never` = fingerprint mismatch only, zero media reads in the steady state), while the fire date comes from the anchor alone. Setting it to `Never` does **not** disable the job.

**F-M275:** **The refresh works on a snapshot of the cache and decides per entry from the file's fingerprint.**

For every entry the file is stat-ed for **size** and **modification time**. A file that is missing, or that cannot be stat-ed, leaves its entry untouched and is counted as **missing** — removing entries is the state prune's job (F-M94), not this task's.

A fingerprint mismatch (size or modification time differs) recomputes the media hash. An unchanged fingerprint recomputes only when the entry's trust window has expired; with the cadence set to `Never` the window never expires, so only a mismatch triggers a recomputation.

A recomputed hash is stored together with size and modification time. A file that cannot be hashed keeps its old entry and is counted as **skipped**.

The cache is flushed at the end of the run. The run's line reports the entry count and the changed, missing and skipped counts. The run ends `ok`, `cancelled` or `failed`; a failed run keeps the entries already flushed and leaves the remainder untouched. A run that cannot take the global run lock is recorded as `deferred` and re-fires after the job spacing (default 15 minutes, clamped to 5–120).

**F-M279 [D]:** **The OSHash VALUE is verified against an independent oracle, not merely against itself. Switchable: not applicable — this is a test obligation, not runtime behaviour.**
A hash that is computed wrongly is still 16 hex characters and still stable, so the identity layer works perfectly on a value nobody else shares: dedup compares our hashes with our hashes, the cache matches, and no log line looks abnormal. Correctness here is only observable against a reference computed OUTSIDE this codebase.
**The reference:** `scripts/oshash-oracle/oshash_oracle.py` implements the published OpenSubtitles algorithm (size + first and last 64 KiB summed as little-endian 64-bit words, trailing partial word dropped, 16 lowercase hex, unsigned 64-bit wraparound) and was written from the specification, NOT from `ComputeMediaHash`. `scripts/seed-db-test` section J asserts the shipped code against literals produced by that oracle.
**The contract the code actually implements:** `chunk = min(64 KiB, size)`, both windows read at that width. Above 128 KiB this equals the published canonical hash; between 64 KiB and 128 KiB the two windows overlap; below 64 KiB they coincide and the same bytes are summed twice. Every non-empty file therefore has a defined value; an empty file yields `null`, never a made-up hash.
**What must be pinned:** the values at 8 B, 64 KiB, 128 KiB and 200 KB, the window edges at 64 KiB ±1 and 128 KiB ±1, that a change in the tail or the last window changes the value, and that a missing path yields `null` rather than throwing.
**The obligation is negative-controlled:** reverting the algorithm in a COPY of the source must turn the assertion red. Measured 01.10.2026: chunk 65 536 → 32 768 in a copy produced 7 failures and exit 1; the same suite against the shipped build reports 0 failures.
**Where it does not reach:** the cache on a host the agent cannot read (the production instance's data file lives inside its container) can only be audited through `scripts/oshash-oracle/oshash_cache_forensics.py` when that file is reachable. Its verdicts separate `MATCH`, `MISSING FILE` (F-M119 keeps these by design), `SIZE DRIFT` (F-M61b treats this as a miss by design) and `MISMATCH` — the only one that means a stored value is wrong.

## 8. Rules Shared by Both Directions

**F-M5:** **One ffmpeg call per file, however many subtitle streams it carries.** All text subtitle streams are extracted in a SINGLE invocation into a temp folder: repeated `-map 0:s:N -c:s srt -f srt <out>` output pairs on one input.
**The index rule:** stream positions are SUBTITLE-relative (`0:s:N`), never container indices. Mixing the two numbering schemes lands on video or audio and fails with exit 8 or a no-stream error.
**No optional mapping — `0:s:N` without `?`.** On an index that does not exist a trailing `?` makes ffmpeg write the FIRST subtitle stream into that slot, exit 0 and report nothing. An unknown index must fail the call; the caller reads that as a whole-pass failure and retries the empty streams one at a time.
**The fallback rule:** ffmpeg's exit code decides what an empty result means. Exit 0 → the stream carries no text; that is a verdict and must NOT be retried. Non-zero → the pass failed as a whole, and each empty stream is retried once with the per-stream call.

**F-M261 [D]:** **An untagged or `und` subtitle track is resolved, and the found language is written back into the container. Switchable, default off — it governs both the resolution and the write.**
A text subtitle track whose tag is absent, empty, `und` or `undefined` is not a fact about its language. The gate extracts those tracks, detects the language offline, records it as an observation and — when enabled — writes it into the container as a real tag.
**It belongs to the SEEDER, before the queue decision:** a resolution that has not happened yet cannot change the missing-language answer.
**The sequence:** find the untagged text tracks from the stream list (no ffmpeg call when there are none) → one ffmpeg pass for all of them (F-M5) → detect offline with the 2 KB floor (F-M74) → write the codes into the container → move the file's registry state to the new hash → record the tracks. The queue decision then reads the corrected language.
**The registry move:** the rewrite changes the file's IDENTITY (the OSHash covers size plus the first and last 64 KB, and a Matroska segment header carries its own size). `ReplaceMediaIdentity(oldHash, newHash)` is a rename, not a re-decision: marks, ids, language aggregates, embed rows (rebuilt under `"<hash>|<pos>"`) and the parents of the sidecars travel. Where both sides hold a row the OLD one wins; an equal pair is a no-op and an empty hash is refused.
**The position rule:** a track's position is `0:s:N` over TEXT-eligible subtitle streams only (subtitle streams, not external, not forced, not bitmap), in container order. Video and audio never enter the count; bitmap and forced tracks DO occupy a position and stay in it; the tag goes back to the position it came from. External streams are skipped, not counted.
**No row** for a track the detector cannot decide (below the 2 KB floor, no text, no confident verdict).
**The resolution counts as PRESENT for the coverage check in the same run**, because Jellyfin's cached list still reports the old tag for a container that was just corrected.
**The write is a stream copy (`-c copy`)**, and the original is replaced only after the result was read back and found to carry the wanted tag.
The result is read back out of the file across the stream shapes that exist (audio in front, interleaved, bitmap tracks between, forced tracks between): every corrected track carries the wanted language and none is misassigned. Only files carrying an untagged track are touched, and each once. **Test: T79.**

**F-M59:** **LIFO queue order:** both pipelines process items by the creation stamp descending — newest first. The id-order partition keeps id-resolvable items before the id-less backlog.

**F-M60:** **File-missing retries before permanent skip:** persistent counter per item (shared by both pipelines). Each failed file-existence check bumps it, success resets it. An extraction failure does not bump it. At the configurable threshold (default 3, 0 = never) the item is skipped as "file-missing (retries exhausted)".

## 9. SubDL/TMDb API, IDs and Credentials

**F-M11 [B1]:** Upload queue with retry: a failed item is retried across runs up to the configured maximum, without backoff; at the limit it is purged. Timeouts are retried in-transport three times with a doubling 1 s backoff.

**F-M12 [B1]:** Configurable account (SubDL user/pass) in plugin config, password never in plaintext in logs

**F-M17c2:** **Remote duplicate detection requires a canonical payload AND a matching hash function.** The registry hash is comparable with SubDL's stored raw-file MD5 only when both describe the same bytes and use the same function (F-M185, F-M186).

**F-M19 [B1]:** **All four credentials are required — they are not alternatives.** The SubDL **email/password** pair authenticates the UPLOAD (its three steps carry a Bearer token from `/login`); the SubDL **API key** authenticates search, file download and the quota read; the **TMDb key** resolves and corrects the ids in both directions. A missing one is not a degraded mode: the run is refused up front, naming the field (F-M203).

**F-M208:** **The reschedule budget is per UTC day and resets at the day roll-over** — the same 00:00 UTC boundary the SubDL quota reset uses, so the budget returns when the quota does. the daily reset clears every slot at the roll-over. It is a no-op when the day has not changed.

A slot at the limit has no other path back: the refusal drops its pending fire, so it can never fire, so it can never clear its counter. With `RefetchInterval = Manual`/`OnArrival` no anchor exists that could.

Both refusal messages name the day roll-over as the recovery path, and do not promise a regular anchor — that anchor does not exist in those modes.

A manual run and a successful run do not clear the counter. Only a real fire or the day roll-over does. **Test: T27.**

**F-M219:** **The type-neutral TMDb search is the FIRST rung wherever an id is resolved, and the year is checked on its RESULTS.**

The year is not sent to `search/multi`; the typed endpoints honour it, and the typed search is the second rung.

The year is applied when the hits are read: a hit whose own date (`first_air_date`, `release_date`) equals the year wins over the first hit. With no year-equal hit the first film/series hit is used; a Jellyfin year is often the import year (F-M217).

Both id-less paths start type-neutral: the download pipeline's title rung and the id resolution. A typed search inherits Jellyfin's guess, so a name-detected series typed as a film asks `search/movie` and can never match. **Test: T36.**

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

The give-up line names the server as not answering in time, never as a caller cancel, and the retry lines are routed into the run's log channel. **See T50.**

**F-M221:** **The plugin card states the defaults and the two required keys in the same sentence as what the plugin does.** Card text (all carriers, F-M220): *"SubDL Scribe brings SubDL.com to Jellyfin: it downloads missing subtitles for the languages and libraries you pick and uploads your own. Download is on by default; upload is off — enable at your choice. Requires a SubDL login and API Key plus a TMDb API Key."* Order: what it does, both things the user picks (languages AND libraries — F-M42 pre-fills a list, it is not a commitment), download on by default, upload off by default and enabled by choice (F-M41), then the keys. Do not write non-ASCII characters as `\uXXXX` escapes in the plugin description literal: the F-M220 guard compares source text, and an escape is six characters where the YAML carries one. **Test: T37.**

**F-M24b [B1]:** **Credentials never in logs:** password/API key/token are never written at any log level (redaction before writing, also in Debug)

**F-M25 [NOT IMPLEMENTED]:** A per-run start offset (random wait before the first upload) is not implemented — no code path delays a run, no config property backs it. The anti-herd function is carried by the per-installation diced fire times (F-M51), the per-stop deferral offset (the job spacing, F-M26b) and the daily-limit recovery jitter (F-M182, 30–300 min). Manually triggered runs start immediately.

**F-M26a:** **Which pause applies where (single source of truth).** the bare-call pause — bare API calls, deterministic. the transfer pause — real transfers, ±30 %.

**F-M27 [B2]:** **Auto-backoff:** on HTTP 429/rate-limit or 5xx a file transfer is retried up to 3 attempts with exponential backoff (2 s, then 4 s). The server's retry hint with a 5 s floor applies on the login and transient paths. On exhaustion the request fails cleanly and the run-level reaction matrix (F-M54) applies.

**F-M28 [B1]:** **IMDB ID mandatory — correct per media type.** For series strictly the series IMDB (tvshow, not episode) plus season and episode; for movies the movie IMDB. Resolution order: (1) Jellyfin metadata as-is; (2) TMDB id → IMDB via the TMDB REST API (key required, F-M203); (3) TMDB title search; (4) skip "no-imdb". Episode IMDB is never used.

**F-M190 [B1]:** **ID resolution is type-free and TMDB-authoritative, and it is the standard path for every upload** — not an optional gate.

**Type detection comes from the FILE NAME and from TMDB, never from the Jellyfin library type.** The file-name parser reads the name (`S01E05`, `Season 1 Episode 5`, TV stamps `Title_<YYYYMMDD>_<HHMMSS>`); when it reports a series while Jellyfin reports none, the item IS resolved as a series. A later `search/multi` answer overrides the assumed type: **TMDB decides** film or series. The library type is never the source — an episode in a library typed "movies" is imported as a film, so class-based detection would search TMDB for a FILM named after the episode.

**ID arbitration (JF vs TMDB) with a report:** both IDs present → cross-validate; on disagreement a **Normal-level** line is emitted (`ID conflict for "<title>": Jellyfin says <tt…>, TMDB says <tt…> — using the TMDB id`) and **TMDB wins**. Visible without raising the log level.

**Fallback chain:** TMDB returns no IMDb → the Jellyfin ids are kept; Jellyfin carries only a TMDB id → the IMDb id is fetched from TMDB; **no IMDb resolvable at all → NO upload** (fail-closed).

**Upload payload preference:** IMDb + TMDB when both are known, IMDb alone when TMDB is missing. Neither → no upload.

**Series IMDb needs `/external_ids`:** the detail endpoint reports `imdb_id: null` for series, while `<kind>/<id>/external_ids` returns the real `tt…`. the IMDb fallback falls back to it whenever the detail response leaves the id empty — without it every series was skipped as "no-imdb".

**F-M28a:** Before every SubDL search/upload the plugin calls the id validation to cross-check and correct Jellyfin's IDs via TMDB when a key is configured. Mismatches are corrected, missing IDs backfilled. Automatic whenever a TMDB key is present; no separate UI switch.

**F-M151 [B1]:** **The upload ID quality gate — no switch, it always runs.** Before an upload Jellyfin's IMDb/TMDb ids are validated against TMDb: missing ids are filled, conflicting ids are corrected towards TMDb, and the type is re-decided from the answer. A setting named `UploadIdQualityGate` existed and was read by no code path (removed 01.10.2026) — the gate is not an option but the thing that makes the TMDb key load-bearing (F-M203).

**F-M151b [D]:** **The download ID quality gate — same, and no switch either.** Before a search the ids are validated and corrected via TMDb so the SubDL search points at the right title. `DownloadIdQualityGate` was equally unread and is gone.

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

**F-M206:** **A respacing log line must not claim a quota reset.** A fire carrying an exact, caller-computed time (lock-busy, rate-limit respacing) applies **no jitter** and must log `respaced by JobSpacingMinutes after lock-busy or rate-limit (no jitter)`. The quota-reset wording (reset anchor + 30–300 min jitter) is confined to the branch that actually dices it. **Test: T25.**

**F-M191b — Unresolvable IMDb id ⇒ DROP it, never keep it.** When `/find` returns every result array empty, TMDB does not index that IMDb id at all — for a series item it is an episode id TMDB has no record of. Keeping it "as the best available" uploads an episode IMDb id in the SERIES slot, exactly what this rule forbids. Both ids are dropped so the caller's type-free title search resolves the show by name.

## 10. Scheduler, Quota and Timing

**F-M20 [B1]:** Configurable pacing — a transfer rate (**default 400/h**) and a pause between API calls (**0.1–10 s, default 0.5 s**) — **and no local call limit of any kind**. A configured pause replaces the rate-derived one; the two are not compared. The range is the server's own fastest allowed rate at the floor (SubDL allows 600 req/min = 0.1 s) and a slow, polite pace at the top; it never permits an unpaused burst. The GUI range and the limiter clamp are identical (0.1–10), so a value entered in the page is the value the limiter uses. The rate is clamped to **1–2000**.

**The plugin enforces no quota of its own (03.10.2026, user decision).** No code path counts API calls, no counter can run out, and no run is stopped for making "too many" calls. **Only SubDL stops a run**: on a real HTTP 429 the server's own counters are read and the `QuotaStopDecision` decides between a short respacing and a day-long stop (F-M238). A per-run hourly bucket lived in `GlobalRateLimiter` until this date and DID stop runs; it was the plugin's own invention — SubDL publishes **daily** counters only — and it stopped runs the server would have allowed (measured live: 735 of 2000 searches and 16 of 50 downloads still free when the bucket fired). The class now only spaces calls; it cannot refuse one.

**F-M288:** **A LOCAL deferral is reported under the Workers list, in plain text — the quota bars stay a pure server reading.**

The server counters alone cannot show why a direction is idle. They can read `3 / 1000` (bar blue, everything looking fine) while the direction is deferred, so the deferral needs its own line.

That line sits **under the Workers list**, in the page's normal text colour, and is not part of the quota box. It carried yellow and recoloured the bar in a first version (03.10.2026, reverted the same day on the user's report): a bar painted yellow at 66 % reads as "the allowance is nearly spent" when the fill says nothing of the kind, and the box's job is to report the SERVER's allowance. The two facts are stated side by side instead of over each other — the bars keep the 75 %/95 % rule unchanged, the line states our own wait.

**The cause is passed through, never guessed.** `deferred` covers three unrelated situations: the run lock being busy, our own quota/rate cap, and a user stop. The line carries the direction's own stored wording ("download quota/rate limit — rescheduled", "cycle already active", "run lock busy — rescheduled") instead of a label invented in the page. A lock deferral painted as a rate limit sends the reader after a quota problem that does not exist.

**The line answers "why is this direction idle", so it is gated on the direction being STOPPED — a pending fire alone is not enough.** (Reworked 03.10.2026 on the user's report; the first version rendered whenever a fire existed.) Measured the same day on prod: the download hit its daily limit at 06:08 and left 384 of 412 queued items due, armed its recovery fire for 04:37 the next day, then ran normally at 10:30 and 13:50 — its last row read `ok`, yet the line kept claiming "Download: deferred — next attempt 4:37:00 AM" for the rest of the day, naming ordinary backlog pickup as if the direction were stuck. A direction that is running needs no explanation; only a stopped one does.

**A deferral whose fire is gone is not shown either** — when the scheduler holds no fire the task has already moved on, and the server clears the stopped flag for that case before the page ever sees it. The time the line names is that fire, in the viewer's zone.

**F-M272:** **The page has THREE refresh cadences, and none of them is "10 s for everything".**

**The SubDL/TMDb lights, the login light and the library read/write rows** have **NO interval at all** — they run at page init, after Save, and on the Refresh button only.

**Every refreshed endpoint carries a cache-buster** (a cache-buster), so no layer can serve a stale body.

**F-M238:** **A 429 is not a verdict — the COUNTERS decide whether the allowance is spent.**

Every 429 is classified into three variants: rate headers present → the daily allowance, exact reset from the header; body `service_busy` → transient overload, wait the server window; body `rate_limit` with the server's retry hint → a short-term trip, waited out like the overload. Only when the counters say the allowance is spent does the run stop for the day.

The status code alone cannot decide: SubDL answers 429 for both cases. "Spent" turns a short pause into a lost day; "harmless" turns a spent account into a retry storm.

The counters come from the same source the settings page shows (`GET /api/v2/me`, `usage.search`/`usage.downloads`), read only after a 429, so GUI and pipeline cannot disagree. Search and download are separate allowances, so every decision names its direction.

An unreadable quota is not a free quota: when the read fails or carries no limits, the 429 remains a stop.

A next-day fire anchors on the server's own `reset_at`, never a local midnight guess, with the jitter on top. `Continue after daily limit` still gates that path per direction.

The respacing offset is the job spacing (5–120 min, default 15) (clamped 5–120); no fixed interval is introduced for this case.

Every day-long stop schedules: a spent and an unreadable allowance both end in a fire after the reset plus the 30–300 min jitter. Only the direction's toggle off, or an arrival run holding the slot, stops without a fire.

A dry run reports the hearing-impaired choice — the HI variant is selected beyond the point a dry run stops, so the run repeats the selection and reports it (`DRY-RUN HI … (n/m candidates are HI)`). **See T52, T53, T55.**

**F-M26 [B2]:** **Random jitter in the rate limit — on the transfer rhythm, not on bare calls.**

The pause between two real transfers (upload→upload, download→download) is the base interval ±30 % (the transfer pause).

The pause between two API calls carrying no transfer (empty search, skip, metadata lookup) is **deterministic** (the bare-call pause): exactly the configured minimum pause when configured, otherwise `3600/rate`.

The band is base ±30 %, floor 1 s. With the default rate of 400/h that is 6.3–11.7 s.

A failed or skipped candidate gets **no** transfer pause — nothing was transferred — and stays on the bare-call pause (F-M26a).

The "derive from the rate" sentinel `-1` is **not** clamped; only a real value is clamped. **Test: T21.**

**F-M49 [D]:** **Daily-limit resume per direction:** two independent checkboxes, "Continue after daily limit" (Download) and "Continue after daily API limit" (Upload). **Both default ON.** ON → wait once until reset (max 24 h) and retry; OFF → clean stop.

**F-M51:** **Per-installation random schedule anchors:** all scheduled fires come from a background coordinator (30-s tick). Daily, weekly and monthly anchors are diced once per installation and persisted in config, and every maintenance job carries its own weekly anchor. No default fixed-time triggers. `Manual` disables the scheduled pipeline fires.

**F-M65:** **One-shot night recovery fire per direction:** a run stopped on the daily limit schedules one re-fire after the quota reset plus a random 30–300 min jitter (away from the 00:00 UTC herd), diced freshly per fire by cryptographic RNG — see F-M182.

**F-M182:** **Jittered recovery fire after the daily limit:** when a pipeline stops on the daily quota, the direction is re-fired once after the server-reported reset plus a random 30–300 minutes. The offset is diced freshly per fire (cryptographic RNG) and rolled independently per direction, so the retry does not land on the 00:00 UTC reset moment every API consumer hits. The jitter applies to the **quota-anchor path only**; fires carrying their own offset (run-lock retry, rate-limit respacing) keep their exact time. Log lines name the reset anchor, the resulting fire time in the local zone, and the jitter window applied.

**F-M116:** **Scheduler state persisted:** the next scheduled download/upload fire times, the anchor markers, the catch-up flag and the lock-busy deferrals are persisted and restored on restart.

**F-M248:** **The scheduler's whole due-state is persisted, and a missed anchor slot is caught up instead of waiting for the next window.**

Every dedup marker, the catch-up flag and the lock-busy deferrals survive a restart. A slot that already fired stays consumed; a slot whose window was missed fires on the first tick where it is past-due and unconsumed. No fire carries a time window.

The marker counts the CLAIM, not the outcome: a run that fails after being queued stays consumed for the day, so persistence cannot turn a failure into an infinite retry.

**No fire window.** The refetch slot carried `if (now > fire.Effective.AddSeconds(2 * TickSeconds)) continue;` — a 60-s window that made a missed anchor wait for the NEXT regular window, contradicting F-M156. It is removed: a past-due, unconsumed slot fires on the next tick, and stacking is prevented by the persisted marker.

**F-M54:** **Reaction matrix per server response:** 403 auth → immediate stop; 429 → classified by variant (F-M238) and decided against the live counters — a spent allowance follows F-M49/F-M182 (wait-until-reset or clean stop per the direction's setting), a short-term `service_busy`/`rate_limit` trip is respaced by the job spacing; 5xx → retry with backoff; 200 + `status:false` → warn, fail-open for searches.

**F-M204:** **A running cycle is never widened and a trigger is never merged into it.** Any overlap between scheduled upload and download is answered by a **reschedule**: `ScheduleRecoveryFireAt(direction, now + JobSpacingMinutes)` for the direction not covered by the running cycle; the dispatcher logs `rescheduled +N min — cycle already running` and returns `false`. When the fire comes due, the coordinator's tick re-checks the lock check and defers again if still busy. The per-direction latch exists for the fresh-cycle case only.

## 11. Content Registry and Identity

**F-M17z [B2]:** **Upload dedup respects the stored outcome:** a stored verdict — uploaded, or rejected with any reason — counts as "known"; an observation row does not.

**F-M298 [D] (development):** **A `duplicate-content` skip never overwrites an `uploaded` row — the state follows the CONTENT, not the reason.**

The content-known check deliberately counts a **rejected** row as known just as much as an uploaded one: it answers "has this text been dealt with", which is the question its callers ask. A skip raised on that answer therefore fires routinely over content that is **demonstrably up** — most visibly after a container tag write, where the language-tag gate gives the file a NEW media hash and the identity move carries the accepted rows across (F-M61), while the same text is then seen again as "known" and skipped on the next run.

Recording `rejected` in that situation contradicts the evidence: the position looks unsettled, the file is re-extracted on every later run and rejected again, and the rejected counter grows without a single new verdict. Measured on prod 06.10.2026 — Lanterns S01E08: 39 streams, **0 uploaded against 39 rejected**, while the identical text sat as `uploaded` under the pre-rewrite identity (39 skips, one per stream, `duplicate-content`, with a `rejected` row written each time).

The rule: when a skip carries the reason `duplicate-content` **and** the same content hash already holds an `uploaded` row anywhere — embedded or sidecar, under this media hash or under another — the row is written as `uploaded` (reason cleared) and the stream is **not** counted as rejected. In every other case the reject is recorded exactly as before: a skip over content that is genuinely NOT up must keep saying so, or the fix would claim an upload that never happened.

The decision lives in ONE registry method (`RecordSkippedContent`) that every skip path calls — the embedded reject-replay, the sidecar reject-replay, the phase-1 QA gate and the phase-3 upload skip. **Test: T112.**

**F-M243 [D]:** **The HI variant counts as present whether it is a file or an embedded track.** Both evidence sources answer the same question, and Jellyfin's own the hearing-impaired flag decides a stream — read through ONE detector (the hearing-impaired predicate) used by uploader and downloader alike.

A target language counts as having its HI variant when EITHER `<base>.<lang>.sdh.srt` exists on disk OR the item carries an embedded subtitle stream of that language whose the hearing-impaired flag is true. Only when both are absent is the language queued for an HI download.

The detector reads the flag first and falls back to the stream title (`sdh`, `hearing impaired`) — the same two sources F-M71 uses on the upload side, so both directions agree on what HI means. A title-only rule misses flagged-but-untitled streams; a flag-only rule misses libraries where MediaElch/TinyMediaManager set the title alone.

The verdict is stored once and read from there (F-M254). The detector still runs on the upload side, where the stream is in hand; the download side does not re-derive it.

**F-M254 [D]:** **The HI switch is answered from the registry, not from the streams.** Whether a target language still needs its hearing-impaired variant is read from the stored verdict (the hearing-impaired rows), which joins the embedded rows the uploader wrote (the embedded mark) and the sidecar rows the downloader wrote (the download mark). Jellyfin's live stream list and the on-disk file names are not consulted.

A language counts as having its HI variant when the registry holds a hearing-impaired row for it — embedded or sidecar. Only when no such row exists is it queued. The seeder's queue gate and both download-pipeline branches read the same answer.

The marker list gains `cc`: closed captions serve the same audience as SDH and carry the same non-dialogue cues (`[ BEEPING ]`, `[ GRUNTING ]`, musical notes). Matched on a word boundary (`(?<![a-z])cc(?![a-z])`), never as a substring — `cc` sits inside real language names, and reading that as hearing-impaired would repeat the `hi`/Hindi mistake.

**F-M256 [D]:** **A registry lookup takes the media PATH and resolves the hash itself.** the hearing-impaired reader receives the media file path, resolves the media hash internally (the media-hash lookup), then reads the embedded and sidecar rows; no caller passes a hash for this question.

The resolution lives inside the method, once.

**Test: T73.** The reader finds a stored HI row for a media path and finds nothing for the same call with the hash.

**F-M257 [D]:** **The embedded area is written by whoever reads the streams, not only by the uploader.**

The seeder's scan and the download pipeline record each embedded, non-forced, mappable subtitle track as an observation row (position, language, hearing-impaired) carrying `Status = observed` and no verdict. The uploader keeps writing verdicts (`uploaded`/`rejected`) on the same key, and an observation never overwrites them.

The position is the index into the UNFILTERED subtitle list: ffmpeg's `0:s:N` counts bitmap and forced tracks as well (F-M246), so a position taken from the filtered list names a different stream.

A failure to observe is swallowed and logged at Debug. An observation is an improvement, never a precondition: a scan whose streams cannot be read still queues its items normally.

**F-M246:** **A forced subtitle track does not make its language present — a hearing-impaired track does.**

When the download side asks which target languages are covered (the coverage question), an embedded subtitle stream counts as evidence only when Jellyfin's forced flag is NOT true. A forced track carries only the lines of foreign-language scenes, so counting it as coverage leaves the language without a real subtitle and the item marked complete.

A hearing-impaired track DOES count: SDH is the same dialogue with annotations. A bitmap track does NOT (F-M257). The rule is "any non-forced TEXT subtitle stream", and the HI switch is answered separately from the registry (F-M254).

The upload side follows the same rule: a forced track is removed from the upload set, through the same predicate the pipeline's collector and the seeder's upload-todo prefilter use.

**F-M240 [B1] (superseded by F-M282/F-M283, 02.10.2026):** **The hearing-impaired variant is its own subtitle datum, and completeness is derived rather than marked.**

It used to answer the HI question with LANGUAGES and carry the variant as a `:hi` token inside the download mark's language list. Both halves are withdrawn: the token with the vocabulary that could not express the datum (14.2a), the mark with the duplicated answer (14.2b) — the pipeline trusted a stored mark while the seeder queued from the disk and the refresh judged from the stored rows, so one file could be simultaneously complete, due and stale. Every component now asks the same reader (`SubtitleCoverage`), so a vanished `.sdh.srt` is simply an open pair. **Test: T79/T90.**

**F-M193a:** **No construct the database cannot translate may cross into a query.** A string-comparison overload, a helper-method call or any predicate without a database expression must be applied **outside** the query lambda: keep the indexed equality inside and move the rest to LINQ-to-objects afterwards.

 ```
 // wrong — the ItemId index stays unused and the query fails at execution
 Find(x => x.ItemId == itemId && string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase))
 // right
 Find(x => x.ItemId == itemId).Where(x => string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase))
 ```
**F-M194b:** every content area is keyed by a **computed business key stored in the record's own `_id`**, never by a database-assigned auto id. The counters area and the pipeline-run area use a database-assigned id and are addressed by a separate key field. **Test: T16.**

**F-M185:** **Canonical SRT form — one normalization for hash AND payload.** Every extracted or loose SRT is normalized once on arrival, before any QA gate, hashing or upload: strip a leading UTF-8 BOM, CRLF → LF, lone CR → LF, trim trailing whitespace. The same string feeds both the hash function and the upload body, so hash input and uploaded bytes are identical by construction. The normalization is **idempotent** and runs at extraction, at loose-file read, and again in the upload path (reachable via the retry path).

**F-M186:** **The registry content hash uses SubDL's own function** — the hash function returns the MD5 of the canonical payload from F-M185, lowercase hex, 32 characters, equal to the `md5` SubDL stores in `raw_files[].md5` for our uploads. Changing the function invalidates the existing registry; the database reset (F-M90) is the intended path, no migration. The media hash (F-M61) is unaffected — it is the OpenSubtitles OSHash over size + first/last 64 KiB.

**F-M61:** **Path-independent dedup key (OSHash):** upload dedup keyed on the media content hash, persisted across restarts, moves and renames.

**F-M39 [B2]:** **No double work across directions:** every subtitle the system uploads or downloads is registered under its normalized content hash, so a subtitle that already arrived from either direction is neither fetched nor sent again. The two directions answer it at different points: the **upload** side asks the content-known check on the candidate itself, right before sending. The **download** side does not ask it during the fetch of a regular candidate — the seeder asks it before the item enters the queue and keeps a file out whose sidecar content is already known. The hearing-impaired variant does ask it during the fetch. The download records the hash it saved.

## 12. Library Scope and Skip Filters

**F-M213:** **The library selection is a hard gate for every trigger and BOTH directions. Every report names only what was SELECTED.**

**Work:** with the library selection empty, no direction may start a run. The upload pipeline and the seeder stop up front; the download pipeline does the same (`No libraries selected — nothing to do`).

**Report:** the directory list (the GUI's *Library directories* list) filters on the selection check, not on the containment check. The walk scope includes the outer library of a nested selection (F-M189), but listing that library as a status row shows a directory the user never picked.

**Not in scope:** the database refresh's full-server id probe stays unscoped. It answers "does this item still exist", not "should this item be worked", so narrowing it would delete rows for merely deselected libraries. **Test: T32.**

**F-M220:** **The plugin description has TWO carriers and they must not drift — the card reads the DLL, not build.yaml.**

Both must hold the same text, capped at **260 characters**, and must still name the required SubDL and TMDb keys.

`scripts/release.sh` enforces all three (identical, under the limit, keys named) as a precondition, so changing one carrier alone aborts the release. **Test: T37.**

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

**F-M216:** **A GUI call to a plugin endpoint that answers `application/json` must request `dataType: "json"`.** Without it the Jellyfin apiclient bundle returns the **raw response object** — `application/json` is not `text/*`, so the bundle falls through to its last branch. The object is truthy, so a `!s` guard never fires, while every field reads `undefined`. Set `dataType: "json"` on the statistics endpoint GET **and** the reset endpoint POST, in **both** the inline script of the page markup and the page script (the second instance of this file-pair trap, cf. F-M218). Other loaders in these files call `.json` themselves.

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

**F-M230:** **The Libraries description states function and default in one line.** Wording: `Only selected libraries are processed. None: no upload or download. Default: None.` Field descriptions state function plus default value, nothing else. **Test: T45.**

**F-M299:** **An intro block under a section heading describes what the section does — measurements never appear on the settings page.** Measured values, episode counts, before/after numbers, accuracy figures and share-of-files statistics are **spec and commit material**, not UI text. **Budget: 300 rendered characters** per intro. The same holds for a `fieldDescription` under a checkbox: it states what the switch does and its default, and a diagnostic figure such as a failure share belongs in the log line that measures it. **Test: T113.**
**F-M229:** **Links in the settings page use the same accent blue as the rest of the page.** Jellyfin's stylesheet ships only `a{color:inherit}`, so the links in the field descriptions fell back to the browser default `#0000EE`. Rule: `#SubdlSyncConfigPage a { color: #00a4dc; }` — exactly one blue, no separate hover shade. Scope: link colour only; the destructive red and the status colours are untouched. **Test: T44.**

**F-M228:** **The settings page marks required fields in the accent colour `#00a4dc`, without extra spacing.** Red is reserved for destructive and failed states, so a red marker made a mandatory field look like a fault. The inline variant carries no margin of its own. Scope: the four markers and their two style rules. **Test: T43.**

**F-M224:** **The plugin's own log mode is the only authority.** Jellyfin's Serilog pipeline filters a record before the sink writes it, and Jellyfin exposes no API to change its own log level. The plugin therefore writes **everything at the normal level** and gates the detail itself in the log helper (the per-item level, the trace level → Verbose+; the detail level → Debug+); `Normal` stays lifecycle/summary only and warnings/errors are never gated. No plugin line may be written at Trace. **Test: T39.**

**F-M36 [B2]:** Both lists maintainable on the config page (one pattern per line, textarea), defaults pre-filled (`incomplete`, `sample`, `trailer`, `partial`, `downloading`).

**F-M90:** **Database maintenance UI:** the config page exposes only **Reset** and **Restore** for `subdl-scribe.db`. **Reset** creates a timestamped backup, then removes the database so the plugin starts with a fresh registry. **Restore** restores the most recent backup after a single confirmation. No "Initialize database" control exists.

**Initialisation, first start:** the shared context creates the plugin data directory, opens the data file, raises the collection indexes and checks the schema marker. A missing data file is created empty; an existing one is opened and kept.

**Initialisation, every start:** the same sequence runs on each context creation, so a data file that lost its indexes or its schema marker is repaired to the current shape on open.

**Schema mismatch:** a lower stored schema version logs a warning and raises the marker (F-M195b); a data file written by a newer build is opened read-only.

**Reset is the only path to an empty registry.** Structural changes are never migrated; the stored state is reconstructible from the media files.

**F-M181:** **Single backup retention:** after a database reset all older `subdl-scribe.db.bak-*` files are deleted; only the backup created by that reset remains.

## 14. Data Model and Persistence

**F-M38 [B1]:** **Persistent state split by the kind of thing described** (section 14). Embedded tracks live under their video file, keyed by media hash + stream position; sidecars are keyed by normalized content hash; the file's own record carries path, IDs, season/episode and the derived language aggregate. The **hearing-impaired flag belongs to each subtitle datum**, never to the file (F-M282), and **completeness is derived rather than stored** (F-M283) — so the file record carries no download mark and no HI aggregate. Both pipelines read and write the same store.

The plugin keeps its state in **one database file**, grouped into **five clearly separated areas**, each keyed by the thing it actually describes. The areas share a lifetime and are written by the same lock, but they are not mixed.
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
**F-M282 [B1] (user decision 02.10.2026):** **A hearing-impaired subtitle is its own subtitle DATUM** — its own row, identity, content hash and flag, never a property of the media file and never a modifier of a language. Every reader and writer asks at the grain of the **pair** `(language, hearing-impaired)`: area 2 keys by media hash + position (F-M197), area 3 by content hash (F-M199), so `Movie.de.srt` and `Movie.de.sdh.srt` are two rows and two data; the pair is also the key space of `IsUploaded(...)`, so a normal upload never blocks an SDH variant (F-M71).

**The one rule that does NOT split:** a subtitle of language L proves L whether or not it is the variant — SDH is the same dialogue with annotations (F-M243) — and proves `L (HI)` only when it IS the variant. So `Movie.de.sdh.srt` alone satisfies both pairs of German; `Movie.de.srt` alone satisfies German only. **Test: T79.**

#### 14.2b Completeness is DERIVED, never stored — F-M283
**F-M283 [B1] (user decision 02.10.2026):** **There is no download completion mark.** `SubtitlesDownloadedAt` and `SubtitlesDownloadedLanguages` are GONE from the file record, and with them the whole support apparatus that read and wrote it.

Completeness is answered **on every ask** by ONE reader — `SubtitleCoverage`, asked by pipeline, seeder and refresh alike — from three sources: the sidecar files beside the media, the container's embedded rows, and the language-level stream list (counted only where the configuration counts embedded tracks as coverage).

**No invalidation step exists, and none is needed.** Deleting `Movie.de.sdh.srt` removes its evidence, so the pair is open on the next ask; the refresh therefore **reports** open files and writes nothing. A pair SubDL does not carry is retried on the next refetch (F-M47) — no expiry, no retry budget as a brake. **Test: T90.**

#### 14.2c The second property: FORCED — F-M284
**F-M284 [B1] (user decision 02.10.2026):** **`forced` is a property OF THE SUBTITLE DATUM, like hearing-impaired** — its own row and flag, never a flag on the media file and never a way to identify a language. Grain: `(language, hearing-impaired, forced)`.

**They are NOT symmetric on SubDL's side.** Hearing-impaired is bilateral — two server-side pools (`&hi=1` / `&hi=0`, F-M241) plus an upload field — so it can be searched, fetched and announced. **Forced is LOCAL:** no filter, no field, no candidate names it. A forced datum is therefore **OBSERVED, never DELIVERED** (`SubtitleRef.IsDeliverable`) and never covers its language (F-M246).

**A forced track is RECORDED, not dropped** — that rule lives in `SubtitleCoverage` alone. Marker `forced`, read as a SET with `sdh` in either order; bitmap tracks stay excluded (F-M6), and a rewrite re-states the flag or is rejected. **Test: T91–T95.**

#### 14.2d `hi` and `forced` are stored THREE-valued — F-M285
**F-M285 [B1] (user decision 02.10.2026):** **Every subtitle datum states `hi` and `forced` explicitly as `ja` or `nein`.** Three-valued: `true` (it IS the variant, or IS forced), `false` (it is NOT — also a statement, written explicitly, because a writer that knows the fact must never leave it unsaid), `null` (the row does not say).

**A gap is never read as a statement.** A reader treats `null` as "no claim", so the datum reports no coverage; that direction **converges** — the item is worked once more and that pass states the value — whereas the opposite default would look settled forever. The refresh fills a `null` from the row's own file name **with the value it read, `false` included**. Both properties are REQUIRED parameters on both paths, and `SidecarNaming.Build`/`PlanTarget` carry `forced`. **Test: T93.**

#### 14.3 Area 3 — Sidecar files (`sidecars`)
**F-M199:** one record per loose `.srt` file, `_id` = the **normalized content hash** (F-M186).

Content is the identity of a sidecar: the same subtitle stays known after the file is renamed or moved, and two sidecars sharing a language but differing in text stay two rows. The file name is stored for diagnosis only and plays **no part** in the key.

Fields: the shared subtitle state (F-M198), plus the file name, the path, the media hash and a copy of the parent metadata (the IMDb id, the TMDb id, the series flag, the season, the episode) so a sidecar can be judged without loading the file record.

Downloaded subtitles are stored here too: once fetched, the file on disk **is** a sidecar.

Sidecars carry **no stream position**. Terminality is the outcome itself. **Test: T19.**

#### 14.4 Area 4 — Burned download candidates (`rejected_candidates`)
**F-M200:** one record per fetched-and-discarded download candidate, `_id` = `"<item id>|<language>|<SubDL id>"`.

Its grain is neither a stream nor a piece of content but "this remote release was burned for this file and language", and SubDL's own id is the only identifier such a verdict has.

Fields: the reason field, its stamp. Capped at the newest 50 per file and language. Cleared for a file and language when a download finally succeeds.

#### 14.5 The four subtitle states (F-M198)
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

## 15. Logging, Status and Transparency

**F-M273:** **The configuration page has a host-free structural regression check.**

**The checks are structural, not wording-dependent:** a comment rewrite must not be able to raise a false failure.

**F-M23 [B2]:** Status view: two cumulative counters on the config page — subtitles uploaded and downloaded since the last reset. The line reads "N subtitles uploaded, M subtitles downloaded since the last reset"; the date stands on its own line below it. "Reset statistics" zeroes both and moves the date. Both are incremented at the end of every run from that run's summary, including runs cancelled from outside.

**F-M207:** **The cumulative counters live in the database; a database reset resets them with it.** Stored as one single row in the database, with 64-bit counters.

`scope=all` calls the reset in the same operation, so display and data cannot disagree.

The pre-migration XML totals are adopted once, guarded by the one-time adoption flag. Without the flag a restart after a database reset would re-import them. It is listed in the plugin-owned field list so an automatic save cannot drop it.

The pre-migration XML totals stay in place, unread, so a downgrade finds them. **Test: T26.**

**F-M209:** **A run's in-run watchdog is torn down on EVERY exit path, and its grace is a constant 6 minutes.** The grace does not scale with any setting: it must cover the longest legal single operation, and one file transfer is allowed 5 minutes, so a scaled value could abort a working run (the old scale gave 2.4–3.8 min). Waiting out a configured pause is always legal; only a genuinely frozen run exceeds the grace. **Test: T28.**

**F-M226:** **Every plugin line's level is visible — as a text marker, written at the normal level (F-M224).** The marker `[N]` (Normal), `[V]` (Verbose and up), `[D]` (Debug and up) sits in front of the message, written by the normal level/the per-item level/the detail level/the trace level. Warnings and errors carry no marker. **Test: T41.**

**F-M225:** **Every plugin line belongs to exactly one level — and the statistics never depend on logging.** Ungated normal-level calls must not make `Normal` show diagnostics (resolved ffmpeg path, reschedule budget, seed pre-check timestamps). Assignment: internals and diagnostics → the detail level; per-item work → the per-item level; run lifecycle, abort reasons and task summaries stay at Normal. The counters are written in the single statistics writer from the run summaries, unguarded in the run path, and no log call carries a side effect in its arguments, so a `Normal` run counts exactly like a `Debug` run. **Test: T40.**

**F-M247:** **A dry run contributes NOTHING to the statistics — all six counters, not only the volume ones.**

A run started with a dry run/a dry run adds zero to uploads, downloads, type-corrected-from-file-name, TMDb year-filter misses, QA-rejected downloads, QA-rejected uploads. A dry run does real work — searches, ranking, the TMDb id test, the QA gates — so its summary fills with plausible numbers while it saves no file and uploads nothing.

The log line reads `DRY RUN, would have uploaded {N}` / `{Saved} would have saved`. The rest of the download diagnostic aggregate is unchanged. **Test: T65.**

**F-M245:** **A message reaches the log through exactly ONE gate.** A call site picks the lowest level that must carry its message and calls the one matching gate; it never calls two gates for the same message. `Normal < Verbose < Debug` is cumulative, so two gates for one message put it in the log twice. Scope: the TMDb call trace in both pipelines. **Test: T62.**

**F-M218:** **The quality counters are persisted too, per direction, next to the volume counters.** Four 64-bit fields on the same single status row as F-M207, one field per counter.

type-corrected-from-file-name — items whose type/season/episode came from the file name (F-M217). TMDb year-filter misses — searches that only matched once the Jellyfin year was dropped. Rejected downloads / rejected uploads — candidates **fetched and then thrown away**, one field per direction; the scope is every reject path, and it is F-M286 that defines it. **Test: K1–K8.**

Every timestamp uses one format — US order (M/D/YYYY) with local AM/PM time — through the timestamp formatter and the time formatter, so stamps cannot drift apart per call site. an unqualified locale call without an explicit locale follows the BROWSER's locale.

**F-M310 [B1] (user decision 07.10.2026):** **The time an audio alignment spends is taken off the next download's pacing wait, never below zero.**

The alignment runs AFTER a subtitle is downloaded and BEFORE the next real download, so its duration sits inside a transfer pause that was sized for a download only — the rhythm was charging the run for time it spent correcting instead of transferring (user request: "die Zeit messen, die der Fit gebraucht hat, und sie von der transfer rate per hour für Downloads abziehen, minimum 0").

Measured and credited in `GlobalRateLimiter`, not in the pipeline: the arithmetic has to be testable, and a bound that only exists as a private field of a Jellyfin-hosted pipeline cannot be exercised. `AddFitMs` books, `TransferPauseMsLessFit` spends.

  both alignments are measured — the main track and the hearing-impaired one, in one helper, so a
  second alignment path cannot bypass the stopwatch;
  the measurement wraps the CALL, not the write: an alignment that ran and concluded "no measurable
  gain" cost the same as one that corrected the file, and the rhythm charges for neither;
  measured in a `finally` block, because a cancelled or failed alignment also spent the time;
  the credit is CONSUMED at the pause (`Interlocked.Exchange`, not read-then-clear, so two readers
  cannot spend it twice) and never banked: it can only bring the next pause down, never pay for a
  later one, and two pauses are never shortened for one alignment;
  floor 0 — the plugin never waits a negative time, and an alignment longer than the pause simply
  means no further wait. Both transfer gaps carry it, because both gaps contain an alignment (this
  one the main track's, the next one the HI track's);
  zero and negative bookings are discarded, not booked: a negative value would ADD to the pause.
  With the alignment switched off nothing is measured, so the credit stays 0 and the pauses are
  exactly as configured — no special case needed;
  uploads are NOT affected: they have their own limiter instance and their own rhythm, and the
  request was for the download direction.

The run's DONE line reports what the alignment cost: `N fitted to audio (Xs)` — the counter says how many, the seconds say whether the correction is a footnote or the bulk of the wall time. **Test: T125.** See F-M26, F-M308, F-M309.

**F-M309 [B1] (user decision 07.10.2026):** **The audio fit is logged on two levels: WHETHER at Normal, WHAT AGAINST at Verbose.**

Normal carries the fit as a run-level fact, in the two lines F-M24d already prescribes:

  the run START line names the switch — `audio fit=on|off` — so a run with the correction off is
  distinguishable from a run where every fit simply found nothing to do;
  the run DONE line carries the counter — `N fitted to audio` — so the one number that says how much
  of the correction work landed is visible without raising the log level. F-M286 already required
  every counter to be printed somewhere; this is where the fit's counter is printed.

Verbose carries the per-file detail a reader needs to REPRODUCE a fit: `fit reads audio track {Pos}
of {N} ({Reason}); subtitle {Release}`. It names the track that was READ — not a substitution — and
the subtitle it was fitted against, and it is gated on the fit switch, because with the correction off
nothing reads the audio and the line would be false. It is emitted for track 0 of N as well: the
exceptions are not the interesting set, the track actually read is.

The verdict lines stay where F-M24a puts per-item work — `auto-sync corrected …` / `not applied: …`
at Verbose, one branch each. The former separate `ffmpeg not available` branch is GONE: it called the
same helper under the same threshold as the general branch, so it changed nothing while suggesting a
policy that did not exist. A missing ffmpeg is reported once per run by `FfmpegTools` at Warning
level, which is visible at Normal. **Test: T124.** See F-M24a, F-M286, F-M307.

>**F-M314 [D] (user decision 07.10.2026):** **The sidecar rename is gated by the SAME switch as the container write — `Allocate missing language codes` — and no longer by `UploadResolveUnd`.**

A rename allocates a missing language code exactly as the container write does; it writes that code into the NAME instead of into the container. Two different switches for one act would let an operator ask for allocation and still be left with a library that reads as unlabelled — the two halves of one feature disagreeing about whether it is on.

**`UploadResolveUnd` was the wrong owner.** It is the UPLOADER's und-resolution switch (Upload tab): it decides whether the upload direction looks at an untagged stream. The seeder pass serves BOTH directions and never belonged to that switch. The container path never consulted it either — so the container was written under `Allocate missing language codes` while the file beside it was renamed under a different switch. The two now agree.

**The default therefore changes from ON to OFF**, because `Allocate missing language codes` is off by default (F-M261) while `UploadResolveUnd` is on. On a default installation the seeder no longer renames unlabelled sidecars unless the operator asks for allocation. Stated here because it is a behaviour change on existing installs, not a refactor.

**The gate is a gate, not a deletion:** the file is left exactly as it is, and the uploader still owns it. The 2 KB floor and the "no confident verdict" exit are unchanged and still independent of this switch.

**F-M313 [B1] (user decision 07.10.2026):** **The statistics row counts the LOOSE subtitle files the seeder renamed so their name carries the language, in its own row.**

F-M278 renames an unlabelled sidecar to the shape this plugin writes. That is work on a file the operator owns, and it was visible only in the log. It gets its own row — `Loose subtitles: language codes added` — rather than sharing the container row, because it is a different act on a different kind of file: a container gets a language tag **inside**, a loose `.srt` gets a new **name**. One number for both could be read as either.

**No direction prefix**, for exactly the reason the container row has none: the seeder serves both directions.

Counted on the MOVE, not on the attempt. Every refusal — the target name taken, a directory that cannot be listed, a filesystem error — leaves the file where it was and must not appear in the statistics as work that was done.

**F-M312 [B1] (defect fixed 07.10.2026):** **A dry run does not rename either.**

The container rewrite was already suppressed by F-M263, but this rename had **no dry-run guard at all**: the seeder contained not a single `DryRun` check, and the gate's switch covers the container only. So a dry run moved the user's files around while its documentation promises it "writes nothing" (F-M22). The seeder now has one predicate for the whole class — `DryRunActive`, true when **either** direction has a dry run armed — because the seeder belongs to no direction; tying it to one switch would let the other dry run edit the library. Detection still runs: the value of a dry run is answering what it WOULD do. The file is then recorded under its CURRENT name, the one really on disk.

**Test: T127.** See F-M22, F-M259, F-M263, F-M278, F-M311.

**F-M311 [B1] (user decision 07.10.2026):** **The statistics row counts the media FILES whose language codes the seeder wrote, and that row carries no direction.**

The seeder's allocation pass (F-M261) rewrites media containers, and until now that was visible only in the log. It is work the run did to the LIBRARY, not work that entered or left it, and the row reported only the latter — a run that edited 40 files read exactly like one that edited none.

The count is one per FILE, not per code (user decision): a file that got three tags counts once, because the question the line answers is "how many media files did the run edit". The gate keeps reporting the per-track number on its own Normal line, where the languages themselves are named.

**No direction prefix.** Every other quality row is prefixed because its counter is produced inside a direction; this one is not — the seeder serves both, and the allocation pass sits BEFORE the direction checks, so it runs on every scan whichever direction was asked for. A `Downloads:` prefix would claim a scope the count does not have. The label therefore reads `Media files: language codes added`.

Counted only when the write actually happened: a dry run `stand`s the write down (F-M263), the switch `Allocate missing language codes` is off by default, and a detector that cannot decide writes nothing on purpose (F-M261). All three leave the counter at 0 — no dry-run filter is needed at the statistics boundary, because the count cannot exist in a run that wrote nothing. **Test: T126.** See F-M261, F-M308.

**F-M308 [B1] (user decision 07.10.2026):** **The statistics counters are a TABLE, one row per counter, and the table names the direction each count belongs to.**

Form: label left, number right, both columns bounded so the numbers form **one vertical line** — the
counters are compared at a glance, and a run-on sentence makes that impossible (the user could not read
the previous single line).

**The order is the user's (07.10.2026):** the two volume counters lead with **download first** (it is the
direction every install runs, and it sat second before), then the counter that covers **both** directions,
then the remaining rows **grouped by direction — all downloads, then uploads**. A reader meets one
direction's numbers together instead of hopping between the two. Every quality row names its direction
where the data is per-direction (`Uploads:` / `Downloads:`), and where one counter covers both directions
the row says so instead of picking one. The order is asserted (T123) because a regrouping that reverts is
invisible: the table still renders, no number changes, it just reads the old way.

The wording states what happened, not the code's vocabulary. Four counters, plus the fit:

  type (movie/series) adjusted — the item's type came from the FILE NAME and was corrected, both directions.
  uploads DISCARDED BEFORE TRANSFER — upload candidates dropped before anything was sent, on the
  QA gates, the und/language checks and the self-echo guard (user decision 07.10.2026). It reads
  "after being fetched" no longer: the upload direction fetches NOTHING. Every increment site sits
  on a skip path ahead of the API call — the bytes are extracted, detected on and screened, never
  transferred — so the download word "fetched" was a copy that stated something false. The two rows
  now name what each direction actually spent.
  downloads rejected after being fetched — download candidates fetched and then thrown away.
  searches run without the year tag — TMDb searches that only matched once the year filter was dropped.
  auto-synch — downloaded subtitles whose timing the run MOVED onto their audio track (F-M307),
  counted when the correction is APPLIED; the upload direction has no such row, because it has no
  audio to align against. The operator's wording for the row is `Downloads: Auto-Synch`
  (08.10.2026): it is short on purpose, because the statistics column is narrow and the row sits
  among others that follow the same `Downloads:` pattern. The long form first written here
  ("...aligned to the spoken track", echoing the switch's sentence) was the operator's correction —
  a row in a table of counts names its counter, it does not restate the switch's description.

**Test: T123.** See F-M218, F-M286.

**F-M286 [B1] (user decision 02.10.2026):** **A reject counter counts what the run SPENT, and every counter is printed somewhere.**

Scope: **every path that fetches and then discards** — no bytes, content already known, broken content, hearing-impaired gate, und-off, unmappable language, self-echo, duplicate-remote, forced. A stored `Rejected` verdict without an increment is a silent discard.

Not counted: anything the run did not spend on — an empty hearing-impaired pool, candidates the walk left untried, a retry that re-runs a gate already counted.

The number reconciles with the day's quota (`requests = saved + rejected`) and both run lines print it. **Test: T96, K1–K8.** See F-M218, F-M24d.

**F-M24a [B1]:** **Tiered logging, all levels via Jellyfin's logger. DEFAULT: Normal.**

Normal: run STARTED/DONE lines with counters, run aborts with reason, critical errors, aggregate skip lines per reason class, wrong API keys reported ONCE as Error.

Verbose: per-file up-/downloads, SKIP reasons, dry-run lines, search results, QA reject reasons, TMDB API traces.

Debug: every SubDL API round-trip (search, download, upload, login), api_key always redacted; TMDB round-trips too.

**F-M24d [B1]:** **High-level log concept for Normal mode:** one summary line per run/direction with counters; one aggregate line covering the skip reasons of a download run; lifecycle events and warnings/errors once per event. **Amended 02.10.2026 (F-M286): the upload run writes ONE line with its two numbers — uploads and rejections — instead of none.** The old sentence ("no skip aggregate") dates from when the upload's reject number covered a single gate; with the counter now spanning every reject path, a run that discarded its whole inventory read exactly like a run that found nothing. What stays: it is one line with two numbers, not a per-reason aggregate — the per-item SKIP lines remain the Verbose detail.

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

One Normal line names the file, how many codes were written, which languages (distinct ISO codes, position order) and — when the identity moved — the old and new hash. Per-track positions and byte counts stay at Verbose. The seeder's identity line carries only what only the seeder knows, so the two do not repeat each other. **Test: T80.**

**F-M267:** **The waiting download/upload rows report the CYCLE's fate, not their own wait.**

`WorkerRunRegistry.DescribeCycle(cycleFinished, seederOutcome, seederDetail, directionOutcome, directionDetail)` decides the row of a wait-only task, with a strict ranking: (1) cycle not finished at the wait cap → `running` ("cycle still running at the wait cap"); (2) a `failed` direction or seeder → `failed`; (3) a `deferred`/`cancelled` direction, then a `deferred`/`cancelled` seeder → yellow; (4) a `skipped` direction, then a `skipped` seeder → grey; (5) otherwise → `ok`, carrying the seeder's numbers when it reported any, else "cycle finished".

Reporting the task's own "ok" is wrong twice over: it claims success when the wait cap expires while the seeder is still scanning, and it hides a quota stop behind a green light. the fallback word substitutes "not recorded" so a row cannot show a bare outcome word. **Test: T85.**

**F-M289:** **The direction rows are written by whoever OWNS the cycle — on an arrival cycle that is the dispatcher, not the waiting task.**

An arrival cycle starts in the dispatcher and never passes through `SubdlDownloadTask`/`SubdlUploadTask`; those two only record their row while they wait. Before this rule an arrival cycle therefore did its work, logged it and left the Download and Upload rows untouched: they kept showing the last SCHEDULED run, and only the Seeder row moved. The rows were truthful about the wrong run — the failure mode is a reader concluding nothing had happened.

The dispatcher records both direction rows for the arrival triggers (`event`, `arrival-followup`) and only for those: on a scheduled or manual run the waiting task owns its row, and a second writer would fight it. The ranking is `DescribeCycle` (F-M267), so a row reads identically whichever path produced the cycle. The dispatcher marks both rows `running` at cycle start, so a cycle that dies mid-scan shows the attempt rather than the previous green.

A direction that is switched OFF is recorded `skipped`/`disabled`, not `ok`: the arrival path leaves it untouched, so a plain `DescribeCycle` call would fall through to green and paint "nothing happened here" as "this ran fine". **Test: T102.**

**F-M290:** **With no library selected the seeder does not start at all — it does not scan, pre-check, or take the run lock to conclude that there is nothing to do.**

No selection means there is nothing this seeder could look at, so the answer is known before any work begins. The check sits at the TOP of the seed step, ahead of the global run lock, the change-stamp pre-check and every database read: reaching the same conclusion from inside `Scan()` costs a held lock, a walked library and a touched database for a result that was already certain.

The row is `skipped` with "no libraries selected" — GREY, because a scan that was never allowed to run must not read like one that ran and found nothing. The wording matches the line `Scan()` logs for the same condition, so the two cannot be told apart. **Test: T103.**

**F-M268:** **One colour rule for every worker: green = the work ran and ended without an exception, yellow = the work did not happen but nothing is broken, red = something is broken, grey = not run.**

The palette: the page accent is `#00a4dc`. `run` → accent; `ok` → `#107c10`; `failed` → `#a4262c`; `cancelled` and `deferred` → `#ffc107` (quota, run-lock deferral, user stop); `skipped` and `never` → `#767676`; a dry-run note → `#9a9a9a`.

The accent is also the link colour (F-M229) and the required-field colour (F-M228). Red is reserved for destructive and failed states. The quota bar uses the same palette for fill, warning (`#ffc107` at 75 %) and danger (`#a4262c` at 95 %). The bar's colour is decided by the fill ALONE — a local deferral never recolours it; that state is stated as its own line under the Workers list (F-M288).

**F-M193 [B1]:** **`/user/mySubtitles` is NOT paginated — the counters must count DISTINCT upload ids.** The endpoint ignores `page`, `per_page`, `offset`, `limit` and `start`. The listing deduplicates by the upload id and breaks on the first page that adds no new id; rows with `UploadId <= 0` are kept unconditionally. The status line does not claim "in N pages".

**F-M265:** **The LAST run of each worker is stored in the database and shown in a "Workers" section on the General tab of the configuration page — one line per worker with the time of its last run and how it ended.** Only the last run is stored, not a history.

**Six outcomes**, each mapped to a colour and a word in the GUI: `ok`, `failed`, `cancelled`, `skipped` (a scan that found nothing to do), `deferred` (nothing broken, the work was pushed forward: run lock busy, quota, user stop) and `never`, plus `running` while a cycle is still working at the wait cap. Colour rule: F-M268. A `skipped` run still writes its row — "the task was not allowed to run" is information, and without it a disabled direction looks identical to a broken one.

**The dry-run note is CONDITIONAL**: a grey `dry run` note appears in the result cell only while the direction's dry-run flag is on. With the flag off the line stays clean.

**F-M293:** **A Jellyfin restart is not a user stop; the worker row reports what it actually did.**

A restart cancels the running task exactly as the stop button does, and the token carries no reason. The task's catch therefore treated both the same — it wrote a stop marker, called `RequestUserStop`, and that records `cancelled`/"user stop" for **both** directions at once, so a restart reported an action the user never took.

**The stop marker is the discriminator:** the `Stop` endpoint writes it *before* it signals, a shutdown never does. `PipelineStopSignal.HasMarker` tests it **without consuming it**; without it the row is `ok`/"restart". A worker with no stop button (postprocessing, OSHash refresh) can therefore never report a user stop. **Test: T106.**

**F-M294:** **"Deferred" means a fire was scheduled — for ANY worker, and the moment is named.**

A pending re-fire IS the deferral: it exists only because a run could not finish, so it decides the word and the colour of that worker's row — YELLOW as `defer` — for every row that does not claim something stronger. `failed` (broken) and `run` (in progress) are left alone; `ok` and `cancelled` both claim that nothing is owed, and a fire proves otherwise. All workers that can hold a fire are covered (both directions, database refresh, OSHash refresh, postprocessing), and the flag is read per worker from the coordinator rather than from the row.

The line under the Workers list names, per worker and in list order, its stored cause and when the fire is due. It is gated on the fire alone, so a restart cannot blank it; the seeder holds no fire of its own and never appears. **Test: T105, T107.**

**F-M269:** **The database refresh's detail line names the compaction fallback.**

**This is a NOTE, not a red light:** the outcome stays `ok`. **Test: T86.**

## 16. Non-Goals

- N-1: Standalone downloader app or separate downloader plugin — integrated in SubDL Scribe.
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
**NF-8:** **Manual stop button** ("■ Stop all uploads & downloads", General tab): one click sends `DELETE /ScheduledTasks/Running/{taskId}` for BOTH directions.

**NF-9:** **The specification is checked like the code — it is the eighth test suite.** `scripts/tests/spec-doc/check.py` asserts, without a Jellyfin host: every Contents counter matches the definitions in its section, every requirement id is defined exactly once, the test numbering is gapless from T1, every test a requirement names exists, the header status names the version `build.yaml` builds, and no definition-shaped line escapes the pattern it counts with. **Test: T115.**

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
**T22:** A deferral lands exactly `JobSpacingMinutes` after the STOP moment (no roll-over anchor, no added jitter), it carries the direction's own stored cause rather than a generic "rate limit", and the unchanged items stay `Queued` with the worker row reading `deferred` (F-M26b/F-M288)
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

**T34:** The four quality counters persist, reset with the volume counters and render as numbers (F-M218)

**T35:** A mis-typed episode is searched by its parsed name and receives its own pack entry (F-M217, F-M215)

**T36:** A title that is both film and series resolves by year, type-neutral first (F-M219)

**T37:** Both description carriers hold the identical string, and the release aborts on drift (F-M220, F-M221)

**T38:** Log lines carry the new namespace while the data folder keeps the assembly name (F-M223)

**T39:** The plugin's own log mode produces each level's lines without touching the server configuration (F-M224)

**T40:** A `Normal` run counts exactly like a `Debug` run (F-M225)

**T41:** Every plugin line carries its level marker at every setting (F-M226)

**T42:** Every task the plugin registers reports the SubDL Scribe category (F-M227)

**T43:** Required markers render in the accent colour with no stray space (F-M228)

**T44:** Every anchor on the settings page is the accent blue, none the browser default (F-M229)

**T45:** The Libraries description states function and default only (F-M230)

**T46:** Jellyfin's ids are corrected by title and year, counted over both directions (F-M231)

**T47:** A server-error login is classified as transient; wrong credentials fail closed at once (F-M232)

**T48:** An on-arrival run works exactly the items that arrived (F-M233)

**T49 (superseded by F-M283):** The refresh REPORTS open required files and writes nothing — there is no download mark left to drop (F-M234/F-M283)

**T50:** Three transport timeouts are retried, a user stop is never (F-M235)

**T51:** A failed compaction leaves the database answering (F-M236)

**T52:** A 429 is classified by its variant, not by its status code (F-M238)

**T53:** The quota counters decide exhaustion, and an unreadable quota stops (F-M238)

**T54:** A rebuild that cannot release the pages is replaced by one built from the rows (F-M237)

**T55:** Every day-long 429 stop schedules a fire (F-M238)

**T56:** Sidecar names resolve through one reader, never through the language mapper (F-M239)
**T57 (superseded by F-M282/F-M283):** A vanished `.sdh.srt` is an open pair on the next ask, with no invalidation step (F-M240, F-M283)
**T58:** The HI variant comes from its own search (F-M241)

**T59:** One corrected file per language: the walk stops at the first candidate whose correction proves itself, and an unprocessed file never ends it (F-M319/F-M318)

**T60:** The HI answer comes from the registry, and `cc` counts as a marker (F-M243/F-M254)

**T61:** The pipelines execute the queue item's work order (F-M244)

**T62:** One message produces exactly one log line at every level (F-M245)

**T64:** A forced track does not count as present, SDH does (F-M246)

**T65:** A dry run contributes nothing to any of the six counters (F-M247)

**T66:** The scheduler's due-state survives a restart (F-M248)
**T67:** A missed anchor slot is caught up on the next tick (F-M248)

**T68:** A bare episode marker makes the item a series (F-M249a)
**T69:** The bare-marker guard rejects release-group suffixes, and the date rule needs text after the date (F-M249a/F-M249b)

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
**T94:** A forced subtitle is observed but never uploaded — the loose-file path asks `IsDeliverable` and skips it, the embedded path drops it via `IsDialogueStream` (F-M284)
**T95:** A container rewrite re-states the forced disposition and is REJECTED if it did not survive, so a language-tag write can never silently turn a forced track into the film's dialogue (F-M284)

**T96:** Every path that fetches and then discards a candidate increments the reject counter, the count reconciles with the day's spend (requests = saved + rejected), and both the download and the upload run line print it (F-M286)

**T97:** A dry run leaves no stored verdict in either direction - the refetch stamp, the file-retry counter, the id-resolution budget, the registry upserts, the file-missing deletion and the cycle-queue removal are all held back, while the worker-run record is still written (F-M22, F-M287)

**T98:** The download cap and the search's early-stop threshold are the SAME number, derived from the single setting: with `MaxCandidatesPerLanguage = 3` both are 3, with `2` both are 2, with `0` both stay unlimited. The helper that derives them takes the budget ALONE — asserted structurally as well, so the removed keep-best parameter cannot reappear as a second argument (F-M95/F-M50/F-M319).

**T99:** The candidate walk's exits are reachable and each is the one that applies: it stops at the first CORRECTED file, and it stops when the download budget is spent. A budget that no setting can raise is asserted to be passed through unchanged, so a deliberate value is never silently widened (F-M319/F-M50).
**T100:** A deferred direction shows its own stored cause under the Workers list (never a generic "rate limit"), that line sits below the quota box rather than inside it, the bars keep their 75 %/95 % colours at every fill level, and the line disappears once the scheduler holds no fire for that direction (F-M288)


**T102:** An arrival cycle updates the Download, Upload and Seeder rows together, and each direction's row carries that cycle's fate (green on work done, yellow on a quota stop, red on a failure); a direction that is switched off is recorded grey/`disabled` rather than green; a scheduled cycle leaves the arrival path out of it entirely (F-M289)


**T103:** With no library selected, a seed step leaves a grey `skipped`/"no libraries selected" seeder row, logs `No libraries selected — seeder not started`, holds no run lock and does not walk the library — neither on a scheduled run nor on an arrival (F-M290)


**T106:** A Jellyfin restart during a run records `ok`/"restart" for the affected worker — not `cancelled`/"user stop": no stop marker exists, so neither the task catch nor the in-cycle path reports a user stop, and both directions keep their own fate; a real stop through the endpoint still records `cancelled`/"user stop" for the direction it names (F-M293)

**T107:** Every worker holding a pending deferred fire shows a yellow `defer` lamp and its own line under the Workers list naming the worker, its stored cause and the moment the fire is due; the fire overrides `ok` AND `cancelled` — a direction with a pending fire is never shown as cancelled — while `failed` and `run` are left alone; the line is gated on the fire alone, so it survives a restart and disappears once the fire is consumed; the seeder never appears (F-M294)

**T108:** The drift detector, driven with synthetic material, reports NO drift for cues that track the speech at a constant offset, recovers planted steps of known size and time at 20 and 30 min with a span matching the planted total, and reports drift for a ramped offset — and the factored two-offset marginal used in the port equals the explicit outer log-sum-exp (worst deviation < 1e-6 over 20 random trials) (F-M295)

**T109:** End-to-end on a real episode: the plain subtitle comes back steady while the hearing-impaired variant of the same episode comes back drifting with a span and boundaries named, and the verdict states a span rather than a correction value (F-M295)

**T110:** The auto-sync's two file-level halves, without audio: the audio-track rule picks (1) the track in the subtitle's language, (2) English, (3) the first untagged track, with the 2-vs-3-letter codes (`en` against `eng`, `de` against `deu`/`ger`, `zh` against `zho`/`chi`) resolving as equal and `und`/null resolving as "no language"; a planted constant shift moves every timestamp by exactly that amount while text, cue count and cue order stay unchanged; a shift that would push the first cue below zero is APPLIED and that cue is CLAMPED to 0, with every later cue still carrying the full measured shift; the corrected file is written CANONICAL (UTF-8, no BOM, LF) — asserted on the real writer, so a reintroduced re-encode to the fetched byte style fails here — while the archived original keeps its own byte style; and the kept name `<base>.<lang>.srt.unsynced.zip` does NOT match the sidecar listing pattern `baseName + "*.srt"` while the swapped order does (F-M296, F-M306)

**T111:** End-to-end against real audio, driving the FEATURE (not a copy of its arithmetic): the sign of the correction is established on the material itself by planting +5 s and applying BOTH directions — the one landing within 0.7 s of the plain subtitle is `−detector`, and that is asserted, so a sign flip fails here instead of doubling every corrected file; a subtitle already in sync comes back `applied=False`; and a shift planted at +2.5 / −1.8 / +6.0 s is measured and removed, with the corrected text lying within 0.7 s of the plain subtitle by MEDIAN offset — a file that took no part in the measurement. The check is a MEDIAN and not a spread: a constant shift leaves the spread at 0.00 s whatever its size, so an earlier version of this test passed even on files it had made twice as bad (F-M296)

**T112:** Driving the registry decision itself (not a copy of its arithmetic): an `uploaded` row survives a `duplicate-content` skip — after the identity move that the language-tag gate triggers, a skip over the same content leaves the row `uploaded` with its reason cleared and increments no reject; and the same skip over content that is NOT up still writes `rejected` with its reason intact, so the two cases are told apart rather than both being called settled. The second half is what makes the check meaningful: a rule that simply treats every `duplicate-content` skip as settled passes the first half and fails here (F-M298)

**T113:** Driving the rendered page text, not the markup: every intro block under a section heading measures at most **300 rendered characters** (tags stripped, whitespace collapsed) and carries **no measured value** — the ban list is the forensic vocabulary itself (`Measured`, a before/after arrow, an episode count, an accuracy figure), so re-introducing "Measured on 36 drifting episodes: worst line 10.74 s → 0.17 s" fails here rather than passing as prose. The budget is a ceiling and not a target: the check reads the same text a phone renders, so an HTML comment or an entity cannot buy length. Both halves are needed — the length alone would pass a short sentence full of measurements, and the vocabulary alone would pass an unmeasured essay (F-M299). **It also asserts that no user-visible text contradicts the code (F-M302, F-M303).** This is the half that was missing, and its absence shipped a defect: two intros claimed the audio path *cannot correct* a drifting file — false since F-M300 — while both the length cap and the ban list stayed green, because "…which the audio sync above cannot correct" is 46 characters and carries no number. The refuted phrasings are matched across the section intros, the per-switch `fieldDescription` blocks **and the switch labels**, and the scope is proved by planting: a `never shifted` label, a `cannot touch` label, `has no valid single correction`, `is only reported`, `cannot correct` and a 350-character intro were each planted on 06.10.2026 — the label cases fail ONLY with the label scope included, which is why a description-only check is not enough. Finally, the three former sections are ONE block and it carries **no intermediate heading**: the one intro sits in the switch's own `fieldDescription` and the four removed ids are asserted **absent** from both the markup and the inline script (F-M303, F-M304)

**T114:** The staircase correction, driven on the SAME synthetic episode whose cues were built from a known burst list, and judged against that burst list — the ground truth that took no part in the measurement, because a correction scored with the detector that produced it is the exact inverse of its own measurement and always reports success. Two steps (+2.5 s at 20 min, +2.0 s at 30 min) are planted over a +4.0 s constant offset; the detector must report `Drifts` WITH at least two segments, the applied shift must move the worst distance to a true cue position from **8.60 s** to no more than one misplaced step above the per-cue jitter (**a bound of this tightness holds on THIS synthetic episode only, where the boundaries sit where the test put them — on real material the residual is set by the detector's ±1–2 min boundary placement and measures 4.5–5.1 s; see F-M305/T117**), the cue count and the cue order must be unchanged, and the order guard must stay rare (at most **12 of 661** cues — a broad fire would carry one cue's shift through the file and flatten the staircase into a single constant shift). Two assertions carry this test and neither can be replaced by the other: the **applied shifts read back per segment** must reproduce the planted steps (−2.50 s and −2.00 s between consecutive segments, sampled in each segment's middle so the guard's legitimate bite at the edges is not read as a lost step), which is what proves the staircase survived — and the constant case must report **no** segments at all, so a constant offset cannot silently be routed through the staircase path. A guard implemented with the wrong SIGN fails here and nowhere else: measured, the mirrored rule left a **1.92 s** residual and guarded **22** cues against **6** with the direction right. The negative-first-cue case is applied and clamped to 0 (F-M300, operator order 07.10.2026)

**T115:** The specification's structure, checked without a build or a host: every Contents counter equals the number of definition lines in its section (sub-headings and superseded entries included), every requirement id is defined exactly once, the test numbering is gapless from T1, every test a requirement names exists as a definition, and the header status names the version `build.yaml` builds. The check must FAIL on each of these when it is planted — a wrong counter, a duplicated definition, a deleted test number, a reference to a test that was never written, a stale status — because a suite that cannot fail proves nothing; all six were planted on 06.10.2026 and all six were caught. The last assertion is the guard on the guard: a definition-shaped line (opening with a bolded id) that the counting pattern does NOT recognise must fail the run rather than vanish from the count, since five shapes occur — `**F-Mnnn:**`, `**F-Mnnn [tier]:**`, `**F-Mnnn [tier] (superseded …):**`, `**Tnn (superseded …):**` and the em-dash form `**F-Mnnn — text.**` — and a pattern that expects only the first silently shrinks every count while staying green (F-M301, NF-9)
**T116:** The correction section's wiring, driven against the page file and the built DLL: exactly ONE checkbox in the section; its id present in the markup and bound on load (default-on aware) and on save; the four removed ids (`QaDownloadDriftCheck`, `QaDownloadDriftReject`, `QaDownloadAudioTrackByLanguage`, `QaDownloadAnchorSync`) ABSENT from the markup and from the inline script; `configPage.html` declared as an `EmbeddedResource`, so the page actually ships inside the DLL; and `QaDownloadAutoSync` defaulting to `true` in C#. The ABSENCE assertions carry the test: a presence-only check passes over a stale switch, while a leftover `getElementById` on a removed element throws at load and takes every binding on the page with it. The check also reads the LIVE page over the API and reports what it finds there — a note, not an assertion, so a built-but-undeployed DLL is visible instead of assumed (F-M304)

**T117:** The staircase scored against REAL material with the AUDIO as the only yardstick — no subtitle takes part, neither embedded nor as a sidecar. Per episode one audio decode, then the detector runs on the result and the verdict is read as the **per-segment offset curve**: segments, offset span and the largest absolute offset. A corrected file passes when it shows **no drift**: one segment, or offsets that sit near zero with a span inside the material's measurement floor. The **control runs first** and decides whether the episode may be scored at all: the UNTOUCHED material goes through the same measurement, and a file that already looks wrong there means the yardstick is misaligned to that material — that is a finding about the yardstick and must NOT be reported as a failure of the correction. A subtitle reference may be used to FIND a defect but is never the number this test is accepted on, because the residual would be computed relative to a reference that may itself drift, which counts the same error twice and flatters the result. The order-guard count is read together with the result, and a broad fire is a failure signal rather than a detail: it means the staircase was flattened into one constant shift. The `<= 1.0 s` bound of T114 is a SYNTHETIC expectation and is asserted nowhere here (F-M305).

**T118:** The kept original, driven through the real planner and read back through the real name parser: the first original takes slot **99** beside the corrected file; the name **matches** the sidecar listing (that match is the point — it is how the file becomes a selectable track) and parses as the corrected file's own language with `hi=false` and `forced=false`; with 99 taken the next original takes **98**, and the numbering never leaves 90–99; when all ten reserved slots are taken **no name is returned**, so nothing can overwrite an original already kept; and **every corrected slot taken returns the plain name**, never a reserved one — the collision F-M315 exists to prevent. On the pipeline source: **no archive is written any more** (`AtomicWriteAsync(unsyncZip…)` absent), the original **is** written for both tracks through `AtomicWriteAsync(originalPath…)`, it is registered with **`MarkDownloaded` on the original's own hash** for both tracks, and **no invented reject reason** is introduced — counted on `MarkDownloaded(originalHash…)` rather than on `MarkDownloaded` overall, because the corrected files carry the same call and a total would rise when *they* change. The bytes written are **canonical** (F-M296) so the row and the file describe the same sequence, and the **dry run still guards the write** (F-M287). That the lock holds at all rests on `IsContentKnown` counting every row except `observed`, which is asserted as well — a change that made downloads non-terminal would fail here (F-M306, F-M315)

**T128:** The reserved block is exactly **90–99**: `IsOriginalSlot` is true at 90 and 99 and false at 89 and 1. `PlanOriginalTarget` walks **downward** — 99 first, then 98 — and returns **null** when all ten are taken, never a name that would overwrite. `PlanTarget` for corrected files never returns a reserved slot: with slots 1–89 taken it returns slot 1 (`<base>.<lang>.01.srt`, F-M316) so the caller's own existence check refuses the move. Measured 07.10.2026 against the former loop bound of 999, which would have handed a corrected file slot 90 (F-M315)

**T129:** The sidecar name carries its slot as a two-digit number, always, and the number is what orders the track list. Driven through the real builder and read back through the real parser: `Build` writes `.en.01.srt`, `.en.02.srt`, `.en.99.srt` — never a bare `.en.srt` — and a file written by the OLD form still parses, so the switch needs no migration. The order is asserted as a property of the names, not of the files: sorting a language's names lexicographically must place every corrected slot (1…89) before every reserved original (90…99), which is the ordering F-M315 reserves the block for — with the pre-F-M316 forms the same sort puts `en.srt` last and `en.2.srt` after `en.10.srt`, so this case FAILS against them. The marker shapes are covered too: the slot follows the markers (`.de.sdh.01.srt`), never precedes them, because the reader steps over the slot first and then reads the markers. (F-M316)

**T130:** The slot tells the two kinds of downloaded file apart, and the two ranges are counted separately. Asserted on the real writer with a real directory listing: an **aligned** file is written `<base>.<lang>.01.srt`, the next aligned file `02`, and its kept original `99`; an **unprocessed** file — the fit refused, or the fit switch is off — is written straight into the reserved block as `99`/`98`/`97`, **with no corrected sibling beside it**, and the counter it advances is NOT the corrected one, so the next aligned file still gets `01` rather than being pushed up by an unprocessed predecessor. Asserted as an ABSENCE as well, because the failure is invisible in a file listing: on the unprocessed path exactly ONE file is written for one candidate — the write that used to run after a correction must not also run here, or the same subtitle exists twice. The ten-slot exhaustion case is asserted per branch: with 90–99 all taken, an unprocessed file is REFUSED, counted (`summary.Failed` on the main path, `summary.RejectedCandidates` on the HI path) and logged, never handed an invented name that would overwrite an unprocessed file already kept. The HI variant carries the same two ranges through its own counter. Negative control: plant the single-counter behaviour back (increment `correctedSlotCount` on the unprocessed path too) and require the case to go RED — the ordering `01`-first is the whole point and it must not pass by accident. (F-M317)

**T131:** The download repeats until a correction proves itself, capped at the QA retry limit. Asserted on the REAL refusal strings through the REAL predicate — each case's fragment is first required to still exist in the source, so the case cannot keep passing after the wording drifts: `no proven gain`, a shift beyond the limit, `not measured: only N cues`, `not applied: …` and `fit error` are **candidate-specific** (hunt continues); `ffmpeg not available`, `audio decode failed` and `not measured: no audio samples` are **not** (hunt stops, and the stop is logged with its reason). The cap is asserted to BE `DownloadQaRetryLimit` (default 3) rather than a second knob invented beside it; the stop is asserted to sit against `refusalsThisRun`, and `keep-best` against `correctedSaved` — counting an unprocessed file as a "best" is exactly the bug that made the repeat impossible under the default `KeepBest=1`. **The ORDER is asserted as a property**: all six other download gates (FPS pre-check, no-bytes, language verification, structure, min-cues, runtime) must sit textually BEFORE the fit in the MAIN path, so a repeat never spends a download on a file a gate is about to discard. Negative controls (two): plant `if (savedCount >= keepBest)` back, and move the fit above the gates — each must turn the case RED. (F-M318)

**T132:** The QA retry limit bounds the fit and nothing else. Asserted as an ABSENCE, because removing a give-up is invisible in a passing run: the pipeline contains no `IsExhausted` call, the `QaFailTracker` has no `IsExhausted`/`RecordFailure`/`CounterKey`, the seeder's queue gate and the refresh task's actionable filter no longer filter by a QA verdict, and the two counters that used to report it (`SkippedQaGiveUp`, `QaGiveUpLanguages`) are gone from the run summary. The three surviving consumers are asserted too, so the removal did not take the wrong thing with it: `GetSkippedCandidates` still feeds the walk's memory filter, `RecordSkippedCandidates` still burns this run's discards, and the fit still reads `DownloadQaRetryLimit` as its refusal budget (F-M318). Negative control: plant `IsExhausted` back into the pipeline source and require the case to go RED. (F-M320)

**T133:** A file the fit finds nothing wrong with is written as the best version: it takes the corrected range, advances the corrected counter, is copied into the reserved block with the SAME bytes, and ends the walk. Asserted on the REAL predicate, driven with the real refusal strings — the genuine `no proven gain` text answers true, while a shift beyond the limit, too few cues, a first cue that would go negative, a tooling verdict (`ffmpeg not available`) and `null` all answer false, because the predicate must not let a file that is actually WRONG end the walk. The pipeline side is asserted on its wiring: `alignedForNaming` includes the already-good case, the hunt is turned off for it, the HI branch carries the same rule, and both reserved copies reuse the written array (which is what makes the two files identical rather than merely similar). A NEGATIVE CONTROL plants the old routing back — an already-good file sent to the reserved block — and requires the check to go RED. (F-M321)

**T134:** The auto-sync has its own worker row and its own light, and the statistics count only alignments that happened. Asserted on the SOURCE of all three carriers, because every failure here is silent: a light wired to an unknown worker reads "never" forever, a mirror that fetches on its own can show a different run than the list above it, and a switch whose sentence was left on the old wording still renders. The check proves the worker is registered in `WorkerRunRegistry` with its own key and name, that the mirrored light is present in the markup and reads `workers.forEach` rather than issuing its own `ApiClient.ajax`, that it reuses `subdlWorkerOutcome` instead of inventing words, that the statistics row is named for the auto-sync, and that the setting's label carries the new wording while the old one is gone. The counter rule is asserted as a positive/negative PAIR on the pipeline: the already-good branch must bump `AlreadyGoodAsDownloaded` and must NOT contain `FittedToAudio++`, so a file that needed no correction cannot be reported as an alignment. `Upl. Postproc.` is asserted to have replaced the bare `Postproc.` (F-M322)

**T105:** With a deferred fire pending, the direction's worker row is painted yellow (`defer`) even when its last run ended `ok`, and it returns to green once the fire is consumed; a red, grey or running row is left untouched, and the stored outcome underneath is unchanged. The deferral line under the Workers list follows the opposite gate — it appears only while the direction is stopped — so a direction that has started running again shows a yellow lamp and no line (F-M294, F-M288)


**T104:** With upload switched off, postprocessing does not run: no anchor is armed, a fire that slips through records grey `skipped`/"upload disabled" instead of calling SubDL, and the manual endpoint answers `skipped`/`upload disabled` (F-M291)


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

**T119:** The fit is scored per cue against a **planted** truth on a file the operator has confirmed as good: staircases are planted (a three-step staircase, a fine multi-step staircase in the opposite direction, a reversal, and an untouched control) and every applied correction must land within the tolerance of the planted value. The untouched control must come out **byte-identical**; a configuration change that lowers the accuracy or touches the control fails the test. (F-M307)

**T120:** Real material is scored against the **audio**, never against another subtitle (F-M305), and the run is repeated on its own output to assert idempotence in the same test — the timeline must be unchanged on the second pass. The control run comes first, as F-M305 requires, and the operator's listening verdict is recorded beside the result. (F-M307)

**T121:** The two defects of F-M307 are regression cases and must fail loudly if they return: **(a)** a shifted cue window that falls outside the audio scores 0 and is **excluded** — the fit must never return a boundary-runner shift on cues that were not moved (the clipped behaviour returned shifts at the search limit); **(b)** the before/after report is scored with the **real cue ends**, asserted against a known value, because a report over one frame at the cue start silently makes the deploy rule revert good files. (F-M307)
**T122:** The streaming decoder, asserted as two ABSENCES on the plugin source, because neither shows up in a log line and both would return silently: the sample-materialising decoder is gone (no `ms.ToArray()` and no `new float[…]` on the audio path — it held a 514 MB `byte[]` and a 514 MB `float[]` at once and the OOM killer took the 140-minute files) while the streaming entry point exists; and **nothing persists the level curve** — no file, no cache, no database column, and no curve-shaped field in `Data/Entities.cs`. Negative-controlled: planting `new float[…]` back into the audio path turns the assertion RED (measured 07.10.2026), so the two absences are guarded and not merely declared. (F-M307)
**T123:** The statistics table renders one ROW per counter with the label and its number in two bounded columns, and every row names the direction its count belongs to where the data is per-direction. The **row order** is asserted too (user order 07.10.2026): the volume rows lead with download before upload, then the both-directions row, then the remaining rows grouped by direction with all downloads before the uploads, and every row appears exactly once. Asserted because a regrouping that reverts changes no number: the table still renders correctly, it just reads the old way. Asserted on the page source, because the numbers are the only readout of what a run did: the eight rows exist, the two volume counters carry the total weight, the fitted-to-audio row reads the `FittedToAudio` field (F-M308) and not a field that never leaves 0, and no row claims a direction the counter does not have. The page source is the same file pair that is checked structurally — a row added to one copy only is the F-M218 file-pair trap. The **reset** is guarded on the SOURCE: every counter on the status row must be zeroed by `ResetStatusStats()`, and every counter PARAMETER must appear in the writer's early-out — a counter added later and forgotten in either place fails silently, showing an old total beside a button that claims to have cleared it, or dropping a run that only did the new work. Both were planted and confirmed RED. (F-M308)
**T124:** The fit's logging split, asserted on the source: the run START line names the fit switch (`audio fit=`), the run DONE line carries the fit counter (`fitted to audio`), and the per-file track line is emitted only under the fit switch and only at Verbose. Asserted because the levels are what make the feature falsifiable in the field: a fit that leaves no Normal trace cannot be told from a fit that never ran, and the counter is required to be printed somewhere. Planting a missing `fitted to audio` in the DONE line and a track line moved to Normal each turn it RED. (F-M309)
**T125:** The alignment time is credited against the transfer pacing, with a floor at zero, driven against the real limiter: an unbooked credit returns the jittered pause; a credit above the pause lands on exactly 0 and never negative (asserted at two rates, because one base value would make the bound look like an artefact); a partial credit pulls the pause under the ceiling that credit implies; the credit is spent once and not carried; zero and negative bookings are discarded, asserted through the pause because a negative booking would ADD to it. Plus, on the source: both transfer gaps use the crediting pause, both alignments go through the measuring helper, exactly one raw call remains (the helper's own), the helper books the measurement to the limiter and keeps the run total. The three source-level checks exist because every numeric case passes in a state where the measurement is taken and never credited — the feature would silently do nothing. All failure modes planted and confirmed RED. (F-M310)
**T126:** The seeder's language-code counter reaches the statistics row and survives all four paths it must cross — row field, writer, reset, API — plus the GUI row that displays it. Asserted end to end on the sources because a counter that is added but never carried dies silently at any one of them: the value is produced in the seeder, forwarded on the snapshot, consumed by the dispatcher and written by the plugin, and every hop is a place where a rename or a forgotten parameter leaves a permanent 0 beside a working reset button. The row must carry NO direction prefix, because the seeder serves both directions; the reset must zero the new counter; the API must publish it in BOTH of its responses; and the GUI row must read the published field and appear exactly once in the order F-M308 prescribes. Six failure modes planted (counter not zeroed, counter not incremented, field missing from the endpoint, row removed, row wrongly prefixed, row reading an invented field), all six confirmed RED. (F-M311)
**T127:** The loose-subtitle counter reaches the statistics row and the dry run does not rename. On the source, because both failures are silent: a rename performed by a dry run contradicts the report rather than the log, and a counter wired to the attempt instead of the move counts tidying that never happened. Asserted: the seeder has a dry-run predicate naming BOTH switches (checked on the predicate body, not the call site — an earlier form of this check was true for any file containing the call and could never fail), the rename consults it ABOVE the move, `renamed` is set AT the move and nowhere else, the row counts renames and not registry rows, and the second counter follows the same accumulate-across-scans / consume-once rule as F-M311's. Four failure modes planted (guard removed, predicate narrowed to one direction, `renamed` set at entry, the row counting `Rows`), all four confirmed RED. (F-M312, F-M313)
## 20. References

- Plugin template: github.com/jellyfin/jellyfin-plugin-template
- Reference plugin (download): Jellyfin OpenSubtitles plugin
- SubDL API: `https://api.subdl.com`
