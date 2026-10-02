# Requirements Specification — Jellyfin Plugin "SubDL Scribe" (Upload + Download)
**Project:** Native Jellyfin plugin: automatic upload of embedded subtitles to SubDL.com + download pipeline for missing external subtitles — both in ONE plugin
**Version:** 2.61
**Status:** Implementation — v12.1.12.160.

## Contents

- [1. Objective and Scope](#1-objective-and-scope) — 2 requirements
- [2. Seeder — what enters the queue](#2-seeder-what-enters-the-queue) — 11 requirements
- [3. Upload Pipeline](#3-upload-pipeline) — 32 requirements
  - [3.1 Quality Gates — Upload](#31-quality-gates-upload) — 8 requirements
  - [3.2 Dry Run — Upload](#32-dry-run-upload) — 2 requirements
- [4. Download Pipeline](#4-download-pipeline) — 21 requirements
  - [4.1 Quality Gates — Download](#41-quality-gates-download) — 5 requirements
  - [4.2 Dry Run — Download](#42-dry-run-download) — 1 requirement
- [5. Upload Postprocessing](#5-upload-postprocessing) — 11 requirements
- [6. Database Refresh](#6-database-refresh) — 7 requirements
- [7. OSHash Refresh](#7-oshash-refresh) — 2 requirements
- [8. Rules Shared by Both Directions](#8-rules-shared-by-both-directions) — 4 requirements

- [9. SubDL/TMDb API, IDs and Credentials](#9-subdltmdb-api-ids-and-credentials) — 25 requirements
- [10. Scheduler, Quota and Timing](#10-scheduler-quota-and-timing) — 12 requirements
- [11. Content Registry and Identity](#11-content-registry-and-identity) — 13 requirements
- [12. Library Scope and Skip Filters](#12-library-scope-and-skip-filters) — 8 requirements
- [13. Configuration and Settings Page](#13-configuration-and-settings-page) — 13 requirements
- [14. Data Model and Persistence](#14-data-model-and-persistence) — 8 requirements
- [15. Logging, Status and Transparency](#15-logging-status-and-transparency) — 19 requirements
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

**F-M1a [B3]:** ItemAdded real-time trigger — the item-added event subscription in the event dispatcher (the handler); there is no separate watcher class. Each arrival starts a non-resetting debounce timer (the arrival debounce window (default 5 min), default 5 min). On fire the dispatcher runs one cycle: seed → download → upload (download first, F-M149). Events during an active cycle collect in the pending ids. A follow-up reseed runs at cycle end only if that list is non-empty; the follow-up settings decide which directions take part. Arrivals that are still pending when the cycle ends start one further full cycle after a 90-second delay.

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

**The run order is detect, then rename, then record.** Detection reads the file's own text — an `.srt` is plain text, so no ffmpeg pass is involved. The three gates of F-M74 apply unchanged and in the same order: switch `UploadResolveUnd` off, text below the 2 KB floor (F-M16), or no language found all mean **the file is left exactly as it is** — no rename, no row. Only a detection that names a language renames.

**A file that already carries a language token is never renamed.** Its name is already the shape this rule produces, and rewriting it would churn files that are correct.

**A taken combination takes the next free slot, never an overwrite.** The target is slot 1 (`<container>.<lang>.srt`); where that name is already on disk the next slot is used (`<container>.<lang>.2.srt`, `.3.srt`, …), because two unlabelled files that both detect as the same language are two subtitles and tidying a name must not destroy one. The slot is per combination: a `DE` file present does not push an `EN` file to a slot.

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

**F-M26b [D]:** **Hourly-cap roll-over fire is offset by the job spacing.** When the shared hourly bucket is exhausted mid-run, the run stops and a one-shot recovery fire is scheduled at the bucket roll-over **+ the job spacing** (both directions, the roll-over fire), clamped 5–120. Default 15.

One knob for all spacing — the user configures it through the existing **Job spacing (minutes)** field, with no separate control to discover.

Anti-herd spreading is not needed here: the offset is per installation and rides the account's own hourly roll-over, not a shared wall-clock event. The daily-limit reset keeps its randomised 30–300 min offset (F-M152, F-M182).

The coordinator must not add a second offset on top: this path uses the recovery-fire scheduler, whose `alreadyJittered: true` leaves the caller's offset alone. The offset must not affect the daily-limit reset. **Test: T22.**

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

**F-M239 [B1/D2]:** **One reader for sidecar file names.** The name→(language, hearing-impaired) rule exists ONCE (the sidecar reader); the uploader, the pipeline's missing-language check and the database refresh all call it. Marker is `sdh` only — never `hi` (F-M48). Recognized shapes: `<base>.srt` (no language — it is detected before upload, F-M48), `<base>.<lang>.srt`, `<base>.<lang>.sdh.srt`, and the numbered slots `<base>.<lang>.<n>.srt` the downloader writes. `.part` is never a subtitle.

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

**No stored verdict is written or cleared by a dry run.** A completion mark (F-M88c) says a file is settled; a dry run that sets one takes the work away instead of describing it, because the next real run reads the mark and skips the file. The same applies in the other direction: what a dry run leaves behind must not change what a later run finds.

**F-M276:** **The upload dry run walks the upload decision path up to the transfer and spends no API call.**

It runs: the extraction and the quality gates of 3.1, the id resolution, and the per-file candidate list.

It does not run: the login (F-M8), the duplicate pre-check — which a real run does not make either (F-M184) — and the upload transfer. No search call is made, so no search quota is spent.

The report is one line per stream: `DRY-RUN would upload <neutral name>`, the neutral name being the one a real upload would send (F-M29, F-M30).

The file-retry counter is recorded as a success, so a dry run neither burns a retry nor leaves the retry state stale. No completion mark is written (F-M88c).

## 4. Download Pipeline

**F-M151a [B3] (superseded by F-M283, 02.10.2026):** **Download-side short-circuit — replaced by the derived open-pair question.**

There is no download-side completion mark any more. The pipeline asks `SubtitleCoverage` which required PAIRS have no evidence (14.2b) and returns when the answer is empty; that answer is derived fresh on every run, so it cannot be overtaken by a deletion and needs no stored language set and no validation pass. See F-M283 for why the stored mark had to go.

**F-M187:** **Downloaded bytes have exactly one decode path.** Every conversion of downloaded subtitle bytes to text goes through one path, which honours a UTF-16 byte-order mark (LE/BE, stripped) and falls back to UTF-8. The download side hashes the decode path, and the upload side later reads the written file as UTF-8/UTF-16 text, so the two must agree. The file on disk stays **byte-identical** to SubDL's payload — normalization applies in memory for hashing and upload, never to the stored file.

**F-M251:** **One name parser, not two.** Both directions read names through the same parser; the download side holds no copy of the name logic.

**Scope is arithmetic:** the files this change may touch fall into six named groups (`NxNN`, bare `Exx`, `SxxExx`-at-end, date names, long form, bracket titles), and the groups must add up to the diff total. If they do not, an unintended change hides in it. **Tests: T71.**

**F-M41 [D, parallel]:** **Download pipeline in the same plugin:** search for missing external subtitles per item (missing languages against a configurable target-language list), download via the same API client, registration in the registry, storage as an external stream next to the media file. Own scheduled task, own switch, own status block.

**F-M42 [D]:** **Preferred languages:** target-language list (multi-select, no priority). Default `AR, EN, ES, FR, HI, ZH`; empty = download off. `DE` is absent.

**F-M42b [D]:** **Hearing-impaired version additionally** (checkbox, default off): when on, the best hearing-impaired candidate per (item, language) is downloaded in addition to the regular version and stored as `<basename>.<lang>.sdh.srt`. It comes from the second search (F-M241), and the branch must sit before the best-per-language cut break, or with the default one per language it is unreachable.

**F-M260 [D]:** **The downloader reads the hearing-impaired flag of the file it actually fetched.** When a candidate resolves to one file inside a season or range pack (the pack member), that file's own `hi` flag governs naming and registration; for a plain single-file release the candidate's flag does, since the candidate is the file.

The HI block of the download loop is guarded by the *effective* flag, not the candidate's: when the file just saved already was the HI variant, no second download follows.

**F-M242 [D]:** **"Best subtitles to keep per language" saves exactly that many numbered files.** With `KeepBestPerLanguage = X` the pipeline saves the top X QA-passed candidates per (item, language): slot 1 is `<basename>.<lang>.srt`, slots 2..X are `<basename>.<lang>.2.srt`, `.<lang>.3.srt`, … Fewer usable candidates than X saves fewer files, never an error.

The ONLY exit from the candidate loop is `savedCount >= keepBest`. `KeepBestPerLanguage = 1` (the default) is unchanged.

The GUI caps the saved-slot count at 10.

While `KeepBestPerLanguage > 1` the dry run names the slots it would fill (`DRY-RUN slot 2/3 … → <name>.en.2.srt`), so the setting is verifiable without spending quota. With the default of 1 there is one slot and the line is not written. The slot preview carries the slot number, not the hearing-impaired marker: it runs before any file is fetched (F-M277).

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

**F-M50 [D]:** **Download budget per (item, language):** max N candidate downloads before the language counts as "not available" (default 3, 0 = unlimited).

**The same language verification runs here verbatim** (F-M15), on the downloaded bytes, with its own per-direction switch.

**F-M46 [D]:** **Overall selection:** one best candidate per (item, language) by combined score from F-M43–F-M45. The keep-best count is configurable (default 1); above 1 the QA-passed candidates are saved as numbered sidecars. No candidate passing → the language counts as "not available".

### 4.2 Dry Run — Download

**F-M277:** **The download dry run runs the search and the selection, and stops before the fetch — the search quota IS spent.**

It runs: the id quality gate (F-M151b), the searches (F-M241), the release scoring and ranking (F-M44), the FPS tolerance filter (F-M43), the skip of candidates QA-rejected in earlier runs (F-M50), and the choice of the best candidate per language.

It does not run: the file fetch, the quality gates of 4.1 (language verify, minimum cue count, runtime match), the file write, the download mark and the registry write.

The report names, per language, the chosen release with its score and its hearing-impaired flag. While the hearing-impaired switch is on, the candidate from the hearing-impaired pool is named as well (F-M241). While `KeepBestPerLanguage > 1`, the slots it would fill are named (F-M242).

**The report stops at the candidate, not at the file.** A dry run fetches nothing, so it cannot know the byte size, and it never reaches the point where the name is built from the fetched file — the hearing-impaired marker of the F-M260 name is therefore NOT part of a dry run. What a dry run answers is *which release* per language, not *which file*.

**It stops before the download call,** so no file is transferred — but the searches DO cost API quota, one per language set (two while the HI switch is on, F-M241). The GUI text must name both: "without saving files" and "without API calls" are not the same claim.

The run's stored statistics stay untouched (F-M247).

## 5. Upload Postprocessing

**F-M184:** **Duplicate handling delegated to postprocessing:** the upload path does not search SubDL for duplicates before uploading. A duplicate upload is accepted by the API ("sent for review"), resolves to `rejected` on the SubDL dashboard, and the postprocessing job deletes that entry and marks the local row `remote-duplicate`. This removes one search call per item from the hourly bucket and the daily search quota; the cost is one upload per duplicate. A local self-echo guard (registry, per media/language pair) still prevents uploading the same pair twice.

**F-M227:** **Every plugin task appears under one heading in the dashboard.** Only `SubDL Postprocessing` reported the category `SubDL Scribe`; the other four reported the default category, so the dashboard split the plugin's work across two groups. Rule: every task this plugin schedules reports `Category => "SubDL Scribe"`; a new task must not inherit a Jellyfin category. **Test: T42.**

**F-M223:** **Every name the user sees says SubDL Scribe — the ASSEMBLY name is the one thing that must NOT follow.** Jellyfin's logger derives its category from the type, so the namespace says SubDL Scribe. The embedded-resource names move with the namespace.ml`/`.js` and the embedded-resource read literal), and all three must change together or the configuration page fails to load. **the assembly name stays the assembly name** — Jellyfin derives the plugin data folder and the configuration file from it, so renaming would orphan the credentials, the state database and the run history. The assembly name is a persistence key, not branding. Free text follows (postprocessing the category, console tags, reset dialog, GPL headers); API routes stay the plugin's own routes. **Test: T38.**

**F-M210:** **Every scheduled job is driven by its OWN setting.** Database refresh, OSHash refresh and upload postprocessing each carry their own cadence setting and their own diced anchor, and must fire on them regardless of any other job's setting. the cycle interval governs **only** the automatic pipeline cycles; `Manual` and `OnArrival` there suppress those cycles and nothing else. A job must never sit behind an early return belonging to a different job's configuration.

**F-M210a:** **A service with no manual start button offers "Never"/"off", never "Manual".** `Manual` means "triggered by the dashboard button"; where no such control exists, the disabling option is `Never` (prune, OSHash, postprocessing).

**F-M212:** **No fire may be consumed before its task is registered.** Jellyfin's task queue drops the fire (logging `Unable to find scheduled task of type "X"`) when the target task is not yet registered. Every fire path tests the registration **before** it consumes its slot (no marker set, no reschedule counter reset), so the next 30-s tick retries. This applies to **all six** paths: database refresh, OSHash refresh, postprocessing, the refetch cycle, the F-M156 catch-up and the F-M65 recovery fires. **Test: T31.**

**F-M131:** **Manual stop via marker files:** the Stop button creates `.stop-upload` and/or `.stop-download` in the plugin data directory. Each pipeline checks its marker before processing the next item (and, for uploads, between streams of the same item), cancels the direction, deletes the marker and reports the stop marker. The dispatcher ends the cycle without starting further directions. Stop markers do **not** affect the delayed postprocessing task. The canonical endpoint for programmatic stops is `POST /Plugins/SubdlSync/Stop?direction=upload|download|all`; `DELETE /ScheduledTasks/Running/{id}` cancels the Jellyfin task wrapper, but a running pipeline item may finish first.

**F-M17y:** **Delayed upload postprocessing:** after an upload run (normal finish or stop), on the postprocessing schedule (F-M176; not tied to the run and not to a fixed delay — SubDL review latency varies), the plugin queries `/user/mySubtitles` and resolves every locally pending-review entry whose status is `rejected`: an entry with "Duplicate upload" is deleted on SubDL and marked duplicate-remote; any other rejected entry is deleted without a mark. Accepted entries are not touched. All steps logged at Debug. The task runs on its own cadence and diced anchor (F-M210), independent of any run.

**F-M176:** **Postprocessing reschedule spacing:** if postprocessing cannot start because the global run lock is busy (F-M94h: immediate `false`, no waiting) or it hits the hourly rate limit, it schedules a one-shot re-fire in the job spacing (5–120 min). The re-fire still respects the diced anchor and does not move the next regular run. A busy lock is first checked for staleness; only a living previous run causes a deferral.

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

**F-M25 [NOT IMPLEMENTED]:** A per-run start offset (random wait before the first upload) is not implemented — no code path delays a run, no config property backs it. The anti-herd function is carried by the per-installation diced fire times (F-M51), the hourly-cap roll-over offset (the job spacing, F-M26b) and the daily-limit recovery jitter (F-M182, 30–300 min). Manually triggered runs start immediately.

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

**F-M206:** **A respacing log line must not claim a quota reset.** A fire carrying an exact, caller-computed time (lock-busy, hourly-cap, rate-limit respacing) applies **no jitter** and must log `respaced by JobSpacingMinutes after lock-busy or rate-limit (no jitter)`. The quota-reset wording (reset anchor + 30–300 min jitter) is confined to the branch that actually dices it. **Test: T25.**

**F-M191b — Unresolvable IMDb id ⇒ DROP it, never keep it.** When `/find` returns every result array empty, TMDB does not index that IMDb id at all — for a series item it is an episode id TMDB has no record of. Keeping it "as the best available" uploads an episode IMDb id in the SERIES slot, exactly what this rule forbids. Both ids are dropped so the caller's type-free title search resolves the show by name.

## 10. Scheduler, Quota and Timing

**F-M20 [B1]:** Configurable rate limit: uploads/hour (**default 400**) or a minimum pause between API calls (0.1–10 s, default 0.5 s, GUI-capped at 0.1–5 s). A configured pause replaces the rate-derived pause; the two are not compared. the rate limiter clamps the rate to **1–2000**; the run watchdog clamps its grace calculation to **1–500**. The default of 400 sits inside both.

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

The pause between two API calls carrying no transfer (empty search, skip, metadata lookup) is **deterministic** (the bare-call pause): exactly the configured minimum pause when configured, otherwise `3600/cap`.

The band is base ±30 %, floor 1 s. With the default cap of 400/h that is 6.3–11.7 s.

A failed or skipped candidate gets **no** transfer pause — nothing was transferred — and stays on the bare-call pause (F-M26a).

The "derive from the hourly cap" sentinel `-1` is **not** clamped; only a real value is clamped. **Test: T21.**

**F-M49 [D]:** **Daily-limit resume per direction:** two independent checkboxes, "Continue after daily limit" (Download) and "Continue after daily API limit" (Upload). **Both default ON.** ON → wait once until reset (max 24 h) and retry; OFF → clean stop.

**F-M51:** **Per-installation random schedule anchors:** all scheduled fires come from a background coordinator (30-s tick). Daily, weekly and monthly anchors are diced once per installation and persisted in config, and every maintenance job carries its own weekly anchor. No default fixed-time triggers. `Manual` disables the scheduled pipeline fires.

**F-M65:** **One-shot night recovery fire per direction:** a run stopped on the daily limit schedules one re-fire after the quota reset plus a random 30–300 min jitter (away from the 00:00 UTC herd), diced freshly per fire by cryptographic RNG — see F-M182.

**F-M182:** **Jittered recovery fire after the daily limit:** when a pipeline stops on the daily quota, the direction is re-fired once after the server-reported reset plus a random 30–300 minutes. The offset is diced freshly per fire (cryptographic RNG) and rolled independently per direction, so the retry does not land on the 00:00 UTC reset moment every API consumer hits. The jitter applies to the **quota-anchor path only**; fires carrying their own offset (run-lock retry, hourly-cap roll-over) keep their exact time. Log lines name the reset anchor, the resulting fire time in the local zone, and the jitter window applied.

**F-M116:** **Scheduler state persisted:** the next scheduled download/upload fire times, the anchor markers, the catch-up flag and the lock-busy deferrals are persisted and restored on restart.

**F-M248:** **The scheduler's whole due-state is persisted, and a missed anchor slot is caught up instead of waiting for the next window.**

Every dedup marker, the catch-up flag and the lock-busy deferrals survive a restart. A slot that already fired stays consumed; a slot whose window was missed fires on the first tick where it is past-due and unconsumed. No fire carries a time window.

The marker counts the CLAIM, not the outcome: a run that fails after being queued stays consumed for the day, so persistence cannot turn a failure into an infinite retry.

**No fire window.** The refetch slot carried `if (now > fire.Effective.AddSeconds(2 * TickSeconds)) continue;` — a 60-s window that made a missed anchor wait for the NEXT regular window, contradicting F-M156. It is removed: a past-due, unconsumed slot fires on the next tick, and stacking is prevented by the persisted marker.

**F-M54:** **Reaction matrix per server response:** 403 auth → immediate stop; 429 → classified by variant (F-M238) and decided against the live counters — a spent allowance follows F-M49/F-M182 (wait-until-reset or clean stop per the direction's setting), a short-term `service_busy`/`rate_limit` trip is respaced by the job spacing; 5xx → retry with backoff; 200 + `status:false` → warn, fail-open for searches.

**F-M204:** **A running cycle is never widened and a trigger is never merged into it.** Any overlap between scheduled upload and download is answered by a **reschedule**: `ScheduleRecoveryFireAt(direction, now + JobSpacingMinutes)` for the direction not covered by the running cycle; the dispatcher logs `rescheduled +N min — cycle already running` and returns `false`. When the fire comes due, the coordinator's tick re-checks the lock check and defers again if still busy. The per-direction latch exists for the fresh-cycle case only.

## 11. Content Registry and Identity

**F-M17z [B2]:** **Upload dedup respects the stored outcome:** a stored verdict — uploaded, or rejected with any reason — counts as "known"; an observation row does not.

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

The question is asked at the grain of the pair — see 14.2a and 14.2b — through the one reader (`SubtitleCoverage`), which consults the files next to the media file, the container's own embedded rows, and (when the configuration counts them) the language-level stream list.

**What F-M240 used to say, and why it is withdrawn.** It answered the HI question with LANGUAGES and carried the variant as a `:hi` token inside the download mark's language list. Both halves are gone:

- the **token** is withdrawn with the vocabulary that could not express the datum (14.2a) — the variant is a pair, not a spelling;
- the **mark** is withdrawn with the duplicated answer (14.2b) — completeness is derived on every ask, so nothing has to be invalidated, repaired, or kept consistent.

A variant that SubDL does not carry today is looked for again on the next refetch (F-M47). **No expiry, no give-up, no counter as a brake. Test: T79/T90.**

**Every component asks the same reader** — the download pipeline, the seeder, and the database refresh. That is what ends the disagreement the old model produced: the pipeline trusted a stored mark while the seeder queued from the disk and the refresh judged from the stored rows, so one file could be simultaneously complete, due and stale. A vanished `.sdh.srt` is now simply an open pair, everywhere.

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

Derived, written automatically whenever an embedded track is stored so they cannot drift: the language aggregate (sorted, comma separated) and the hearing-impaired aggregate.

**F-M197:** one record per **embedded subtitle stream** in `embeds`, `_id` = `"<media hash>|<stream position>"`.

The key is media hash **plus position**. Two streams of one file can share a language (a normal and an SDH variant), so the language alone never identifies the row.

Fields: the language, the hearing-impaired flag, the content hash, the status, the reason field, the status stamp, the SubDL id (F-M198).

#### 14.2a The subtitle DATUM is the pair (language, hearing-impaired) — F-M282
**F-M282 [B1] (user decision 02.10.2026):** **A hearing-impaired subtitle is its own subtitle DATUM.** It is not a property of the media file and not a modifier of a language: it is its own row, with its own identity, its own content hash, and its own hearing-impaired flag. The smallest thing a question can be asked about is therefore the **pair** `(language, hearing-impaired)`, and every reader and writer asks at that grain.

The whole model follows from this one sentence:

- **Area 2** (`embeds`) — one row per stream, keyed by media hash + position, carrying the language and the flag (F-M197). Two streams of one file may differ only in the flag, which is why the position — not the language — is the identity.
- **Area 3** (`sidecars`) — one row per FILE, keyed by content hash, carrying the language and the flag (F-M199). `Movie.de.srt` and `Movie.de.sdh.srt` are two files with two contents, so they are two rows, two hashes, two data.
- **Upload** — the pair is the key space of `IsUploaded(mediaHash, language, hearingImpaired)`; a normal upload never blocks an SDH variant (F-M71).
- **Download** — the pair decides what is searched: the regular datum goes to the `hi=0` pool, the variant datum to the `hi=1` pool, which SubDL serves separately (F-M241).
- **Refresh, seeder** — both compare pairs, and both read them through the SAME reader, so no two components can judge one file differently.

**The one rule that does NOT split:** a subtitle of language L proves L, whether or not it is the variant — SDH is the same dialogue with annotations (F-M243). It proves `L (HI)` only when it IS the variant. So a lone `Movie.de.sdh.srt` satisfies both pairs of German; a lone `Movie.de.srt` satisfies German only.

**What this replaced, and why it had to go.** The download mark carried the pair as TEXT: a comma-separated language list where the variant rode as a `:hi` suffix (`DE`, `DE:hi`). That spelling could be re-projected back to a plain language, and every projection was a place the conflation re-entered — a missing variant was answered as a missing LANGUAGE, the regular search was fed that language, and `.de.srt` was fetched again over the file already on disk. Measured on the live library: 41 downloads in one run against a 50/day limit, 35 marks withheld in the same run, the same content hash on three consecutive days. A pair cannot be re-projected by accident: there is no spelling of `DE` that carries the variant and no parse that can lose it.

**The string-token vocabulary is withdrawn**, because a type that cannot express the datum is the defect: `HiTokenSuffix`, `Token()`, `IsHiToken`, `TokenLanguage`, `PresentTokens`. **Test: T79.**

#### 14.2b Completeness is DERIVED, never stored — F-M283
**F-M283 [B1] (user decision 02.10.2026):** **There is no download completion mark.** `SubtitlesDownloadedAt` and `SubtitlesDownloadedLanguages` are GONE from the file record, and with them `MarkSubtitlesDownloaded`, `IsSubtitlesDownloaded`, `GetDownloadedLanguages`, `CoversLanguages`, `FindStaleDownloadedLanguages` and `InvalidateDownloadedMark`.

Whether a file is complete is answered **on every ask**, from the evidence that describes the subtitles themselves — the files next to the media file and the container's own embedded rows — at the grain of F-M282.

**Why a stored mark had to go.** It was a SECOND truth beside the rows, and a second truth can only drift. It did: the short-circuit that TRUSTED the mark asked the disk **plus** the stored variant verdict, while the check that decided whether to WRITE the mark asked the disk **alone**. A variant embedded in the container has a registry verdict and no sidecar — so the withhold check reported `no file evidence`, refused the mark, the mark was never written, the item stayed due, and the pipeline re-downloaded the regular subtitle on every pass. Two readers of one question, fed different evidence, is not a bug in either one; it is what a duplicated answer produces.

**One reader: `SubtitleCoverage`.** The download pipeline, the seeder, and the database refresh all ask it, with the same three sources of evidence:

1. **the files next to the media file** — the exact pair each sidecar name states. This is what self-corrects.
2. **the embedded rows of the container** — each with its own language and its own flag, written by whichever pass observed the streams (F-M257). This is the source the withhold side was missing.
3. **the language-level embedded evidence** the caller may hold (Jellyfin's stream list), counted only when the configuration counts embedded tracks as coverage. It speaks about a language and can never prove a variant.

**No invalidation step exists, and none is needed.** A stored verdict had to be corrected when it was overtaken by reality; a derived answer cannot be overtaken. Deleting `Movie.de.sdh.srt` removes its evidence, so the pair is simply open on the next ask — no mark to drop, no stamp to clear, no repair pass. The database refresh therefore **reports** which items have open files and writes nothing (the `MarksInvalidated` counter is gone with its subject).

**The cost is deliberate and small:** an item with nothing open is no longer skipped by a stored stamp, so every run asks the question. That is a directory listing and an indexed lookup, not an API call — and it is the point, because the answer can no longer be stale.

**Not a give-up.** A pair SubDL does not carry today is looked for again on the next refetch (F-M47); there is no expiry and no retry budget as a brake. The QA budget is keyed `(item, language)` and **shared** between a language's two data, which is why a "no candidate in the pool" outcome must never be recorded as a QA failure (F-M42b). **Test: T90.**

#### 14.2c The second property: FORCED — F-M284
**F-M284 [B1] (user decision 02.10.2026):** **`forced` is an Eigenschaft OF THE SUBTITLE DATUM, exactly like hearing-impaired.** A forced subtitle is its own datum with its own row and its own flag — never a flag on the media file, and never a way to identify a language. The grain is therefore the triple `(language, hearing-impaired, forced)`.

Jellyfin reports both on its streams (`MediaStream.IsHearingImpaired`, `MediaStream.IsForced`), so both are readable at the source. **But the two are NOT symmetric on SubDL's side, and the difference decides what may be done with each:**

- **hearing-impaired is bilateral.** SubDL serves it from two separate server-side pools (`&hi=1` / `&hi=0`, F-M241) and accepts the flag on upload — so the datum can be **searched for, fetched and announced**.
- **forced is LOCAL.** SubDL has no forced filter and returns no forced field. Measured live 02.10.2026: `forced=1`, `forced=0` and no parameter at all returned the **same 10 candidates**; the response carries only `hi`; and **0 of 22** candidates across three languages carried "forced" in the release name.

**So a forced datum is OBSERVED, never DELIVERED.** The plugin never searches for one, never fetches one, and never uploads one. `SubtitleRef.IsDeliverable` states this for callers that plan work.

**What forced is FOR: it is the reason a language can be due while a track of it exists.** A forced track carries the lines of foreign-language scenes, not the film's dialogue (F-M246), so it never counts as coverage: the regular datum stays open and is searched for. The row that records the forced track is what makes that visible, instead of a rule each caller has to re-derive.

**One reader, not five call sites.** Before this, the forced exclusion was re-derived from Jellyfin's flag in five places (the seeder twice, the upload pipeline, the language gate, and the embedded-track enumeration itself). The enumeration now **records** a forced track rather than dropping it, and the "does not count as coverage" rule lives in `SubtitleCoverage` alone — the same rule for a sidecar (`.forced.srt`) and for a track inside the container.

**Naming:** the marker is `forced`, read as a SET with `sdh` in either order (`Movie.de.forced.srt`, `Movie.de.sdh.forced.srt`), so a datum that is both resolves with both flags. Bitmap tracks remain excluded, and that is a different matter: they carry no text at all (F-M6), so there is no datum to record. **Test: T91.**

**A row written before the property existed is BACKFILLED** (user decision 02.10.2026). A document store has no schema, so such a row has no `forced` field and reads back as `false` — which is not a harmless default: a forced track would be indistinguishable from the film's dialogue, would count as coverage, and its language would stay settled although its only track carries foreign-language scenes. The refresh re-reads the flag off the row's own file name, through the marker reader `Parse` uses, so the backfill derives nothing new and cannot disagree with the normal read.

An embedded row cannot be backfilled this way and is deliberately left alone: its flag lives in the container's stream list, not in a name, and the next scan or download pass observes the track and writes the current value. Recording `false` there would be the mistake the backfill exists to undo.

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
- Derived file aggregates (the language aggregate, the hearing-impaired aggregate) are recomputed whenever a track is written, in one place.
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

**F-M209:** **A run's in-run watchdog is torn down on EVERY exit path.** 

**F-M226:** **Every plugin line's level is visible — as a text marker, written at the normal level (F-M224).** The marker `[N]` (Normal), `[V]` (Verbose and up), `[D]` (Debug and up) sits in front of the message, written by the normal level/the per-item level/the detail level/the trace level. Warnings and errors carry no marker. **Test: T41.**

**F-M225:** **Every plugin line belongs to exactly one level — and the statistics never depend on logging.** Ungated normal-level calls must not make `Normal` show diagnostics (resolved ffmpeg path, reschedule budget, seed pre-check timestamps). Assignment: internals and diagnostics → the detail level; per-item work → the per-item level; run lifecycle, abort reasons and task summaries stay at Normal. The counters are written in the single statistics writer from the run summaries, unguarded in the run path, and no log call carries a side effect in its arguments, so a `Normal` run counts exactly like a `Debug` run. **Test: T40.**

**F-M247:** **A dry run contributes NOTHING to the statistics — all six counters, not only the volume ones.**

A run started with a dry run/a dry run adds zero to uploads, downloads, type-corrected-from-file-name, TMDb year-filter misses, QA-rejected downloads, QA-rejected uploads. A dry run does real work — searches, ranking, the TMDb id test, the QA gates — so its summary fills with plausible numbers while it saves no file and uploads nothing.

The log line reads `DRY RUN, would have uploaded {N}` / `{Saved} would have saved`. The rest of the download diagnostic aggregate is unchanged. **Test: T65.**

**F-M245:** **A message reaches the log through exactly ONE gate.** A call site picks the lowest level that must carry its message and calls the one matching gate; it never calls two gates for the same message. `Normal < Verbose < Debug` is cumulative, so two gates for one message put it in the log twice. Scope: the TMDb call trace in both pipelines. **Test: T62.**

**F-M218:** **The quality counters are persisted too, per direction, next to the volume counters.** Four 64-bit fields on the same single status row as F-M207, one field per counter.

type-corrected-from-file-name — items whose type/season/episode came from the file name (F-M217). TMDb year-filter misses — searches that only matched once the Jellyfin year was dropped. QA-rejected downloads/QA-rejected uploads — candidates the QA gates rejected, counted AT the gate, not at the skip counter.

Every timestamp uses one format — US order (M/D/YYYY) with local AM/PM time — through the timestamp formatter and the time formatter, so stamps cannot drift apart per call site. an unqualified locale call without an explicit locale follows the BROWSER's locale.

**F-M24a [B1]:** **Tiered logging, all levels via Jellyfin's logger. DEFAULT: Normal.**

Normal: run STARTED/DONE lines with counters, run aborts with reason, critical errors, aggregate skip lines per reason class, wrong API keys reported ONCE as Error.

Verbose: per-file up-/downloads, SKIP reasons, dry-run lines, search results, QA reject reasons, TMDB API traces.

Debug: every SubDL API round-trip (search, download, upload, login), api_key always redacted; TMDB round-trips too.

**F-M24d [B1]:** **High-level log concept for Normal mode:** one summary line per run/direction with counters; one aggregate line covering the skip reasons of a download run; lifecycle events and warnings/errors once per event. The upload run writes no skip aggregate.

**F-M152:** **The 429 reaction is decided by the server's rate headers and a live counter read, never by the status code alone.** Every response is parsed for the API's rate headers; the values are the daily limit, the remaining count and the exact server reset.

The counter is read from the account endpoint after a 429 and only then.

A 429 carrying a reset header is the account's daily limit: the run stops against the live counter unless continue-after-limit is on, and the next fire is anchored on the server reset plus the randomised 30–300 min offset (F-M182).

A 429 naming a transient server state is retried in place (F-M58); a 429 carrying no rate header and no named state is treated as an edge case and anchored on the next midnight, not on the daily limit.

The daily-limit decision and its anchor are one rule (F-M62, F-M238).

**F-M255 [D]:** **No candidate and no stream is discarded without a line naming it and the reason.**

Every exit inside the candidate walk that does not end in a save, and every per-stream exit of the upload collector, writes one line at Verbose (`[SubDL-D] … reject …` / `[SubDL-V] …`) carrying the release or file, the language, and the measured reason. The gates are named individually: download failure (no bytes, too few bytes, with the byte count), language detection, structure (monotonic flag, cue span), cue count, runtime, content-already-known, keep-best stop (with the number of untried lower-ranked candidates), the QA memory filter (with the ids it removed), and the empty HI pool (with its size). The reason carries the measured value, not a verdict.

Every candidate walk and upload-collector exit carries its measured value (monotonic flag, cue span, byte count).

The level is Verbose, not Normal: these lines are per candidate and per stream. The exits that already carried a counter but no line gained the line; the counters keep their meaning.

**F-M262:** **A rewritten container is reported at NORMAL — one line per file naming which languages were written; the per-track detail stays at Verbose.**

The gate logged through the per-item level (Verbose and up) and the tag writer logged nothing on success, so at the default `Normal` a rewrite of a media file left no trace at all. A pass that EDITS the user's media must be visible at the level every install runs at.

One Normal line names the file, how many codes were written, which languages (distinct ISO codes, position order) and — when the identity moved — the old and new hash. Per-track positions and byte counts stay at Verbose. The seeder's identity line carries only what only the seeder knows, so the two do not repeat each other. **Test: T80.**

**F-M267:** **The waiting download/upload rows report the CYCLE's fate, not their own wait.**

`WorkerRunRegistry.DescribeCycle(cycleFinished, seederOutcome, seederDetail, directionOutcome, directionDetail)` decides the row of a wait-only task, with a strict ranking: (1) cycle not finished at the wait cap → `running` ("cycle still running at the wait cap"); (2) a `failed` direction or seeder → `failed`; (3) a `deferred`/`cancelled` direction, then a `deferred`/`cancelled` seeder → yellow; (4) a `skipped` direction, then a `skipped` seeder → grey; (5) otherwise → `ok`, carrying the seeder's numbers when it reported any, else "cycle finished".

Reporting the task's own "ok" is wrong twice over: it claims success when the wait cap expires while the seeder is still scanning, and it hides a quota stop behind a green light. the fallback word substitutes "not recorded" so a row cannot show a bare outcome word. **Test: T85.**

**F-M268:** **One colour rule for every worker: green = the work ran and ended without an exception, yellow = the work did not happen but nothing is broken, red = something is broken, grey = not run.**

The palette: the page accent is `#00a4dc`. `run` → accent; `ok` → `#107c10`; `failed` → `#a4262c`; `cancelled` and `deferred` → `#ffc107` (quota, run-lock deferral, user stop); `skipped` and `never` → `#767676`; a dry-run note → `#9a9a9a`.

The accent is also the link colour (F-M229) and the required-field colour (F-M228). Red is reserved for destructive and failed states. The quota bar uses the same palette for fill, warning (`#ffc107`) and danger (`#a4262c`).

**F-M193 [B1]:** **`/user/mySubtitles` is NOT paginated — the counters must count DISTINCT upload ids.** The endpoint ignores `page`, `per_page`, `offset`, `limit` and `start`. The listing deduplicates by the upload id and breaks on the first page that adds no new id; rows with `UploadId <= 0` are kept unconditionally. The status line does not claim "in N pages".

**F-M265:** **The LAST run of each worker is stored in the database and shown in a "Workers" section on the General tab of the configuration page — one line per worker with the time of its last run and how it ended.** Only the last run is stored, not a history.

**Six outcomes**, each mapped to a colour and a word in the GUI: `ok`, `failed`, `cancelled`, `skipped` (a scan that found nothing to do), `deferred` (nothing broken, the work was pushed forward: run lock busy, quota, user stop) and `never`, plus `running` while a cycle is still working at the wait cap. Colour rule: F-M268. A `skipped` run still writes its row — "the task was not allowed to run" is information, and without it a disabled direction looks identical to a broken one.

**The dry-run note is CONDITIONAL**: a grey `dry run` note appears in the result cell only while the direction's dry-run flag is on. With the flag off the line stays clean.

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
**T22:** The hourly-cap roll-over fire lands the job spacing after the roll-over, with no added jitter (F-M26b)
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

**T59:** The best-per-language setting writes exactly that many numbered files (F-M242)

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

## 20. References

- Plugin template: github.com/jellyfin/jellyfin-plugin-template
- Reference plugin (download): Jellyfin OpenSubtitles plugin
- SubDL API: `https://api.subdl.com`
