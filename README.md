# SubDL Scribe — Jellyfin plugin

SubDL Scribe brings [SubDL](https://subdl.com) to Jellyfin: it downloads missing subtitles for the languages and libraries you pick and uploads your own. Download is on by default; upload is off — enable at your choice.

**Requires a SubDL login and API Key, plus a TMDb API Key.**

Licensed under **GPL-3.0-or-later** — see [LICENSE](LICENSE).

## Features

### Download (missing subtitles)
- Target-language search (multi-select, each language independently — no fallback chain)
- Release scoring: release group > token overlap > download count (configurable weights)
- Pre-download FPS validation and post-download runtime validation (configurable tolerance)
- Hearing-impaired (SDH) variants as extra files (`movie.en.sdh.srt`) — optional
- "Only missing languages" mode: embedded streams count as present — optional
- Content-hash dedup: never re-downloads what you already have
- IMDB/TMDB match required by default (safe); title-based fallback for libraries
  with incomplete metadata

### Upload (embedded subtitles)
- Extracts embedded text subtitles (SRT) from your media files
- Uploads them to SubDL so other users benefit
- Per-hour rate limiting, hash-based dedup (no duplicate uploads)
- Skip filters for in-progress downloads (`.!qB`, `.part`, `incomplete`, …)

### Shared infrastructure
- One SubDL API client, one content-hash registry, one language mapper
  for both directions
- Scheduled tasks: download (daily 05:00) and upload (daily 04:00),
  interval configurable (Manual/Daily/Weekly/Monthly/Quarterly)
- Real-time mode: reacts to newly added items (OnArrival)
- Queue persistence across restarts

## Requirements

- Jellyfin **12.1.x** (targetAbi `12.1.0.0`)
- A [SubDL](https://subdl.com) account (free)
- SubDL login + **API key** (required)
- **TMDb API key** (required — without it series are skipped; only films upload)

## Installation

### Option 1: via Jellyfin plugin catalog (recommended)

1. In Jellyfin: **Dashboard → Plugins → Repositories**
2. Add this repository URL:
   ```
   https://raw.githubusercontent.com/nrg80/subdl-scribe/develop/manifest.json
   ```
3. Go to **Dashboard → Plugins → All**
4. Find **SubDL Scribe** and install the latest version.
5. Restart Jellyfin.
6. Open **Dashboard → Plugins → SubDL Scribe** and configure it.

### Option 2: manual

1. Download `Jellyfin.Plugin.SubdlSync_12.1.12.81.zip` from the [releases page](../../releases).
2. Extract the contents into a new folder under your Jellyfin `plugins/` directory:
   ```
   /var/lib/jellyfin/plugins/SubDL Scribe_12.1.12.81/
   Jellyfin.Plugin.SubdlSync.dll
   LanguageDetection.dll
   ```
3. Restart Jellyfin.
4. Open **Dashboard → Plugins → SubDL Scribe** and configure it.

## Configuration

Open **Dashboard → Plugins → SubDL Scribe**:

- **General** — SubDL credentials, library selection, rate limit, logging, filters,
  on-arrival triggers
- **Upload** — enable/disable upload, dry-run mode, quota handling
- **Download** — enable/disable download, target languages, validation tolerances,
  score weights, refetch interval, dry-run mode
- **Expert** — advanced knobs for scoring, quota retry, follow-up rounds and
  diagnostic toggles

### Quick start
1. Enter your **SubDL API key** (or username/password to fetch one).
2. Select the libraries to scan.
3. Choose target languages for download.
4. Enable/disable upload as needed.
5. Save and trigger a manual download/upload run from **Scheduled Tasks**.

## Branch workflow

- **`develop`** — the only active branch. Every fix, feature and release lands here.
- **`main`** — reserved for a stable line, and **created only on explicit instruction**. Commits do not go to `main` on their own initiative; the repository currently has no `main` branch at all.

### For maintainers

1. Work on `develop`:
   ```bash
   git checkout develop
   # make changes, commit, push
   ```
2. **Test the build on a non-production Jellyfin instance** (or at least verify the plugin loads and the affected feature works).
3. Release from `develop` with the one supported path:
   ```bash
   scripts/release.sh --dry-run   # build + verify, touch nothing
   scripts/release.sh             # build, verify, tag, release, verify
   ```
   The script enforces the version, the `Subtitles` category, a clean pushed
   tree and exactly one release asset. See [docs/RELEASE.md](docs/RELEASE.md).
4. Keep working on `develop`.

### For users

Install from the catalog using the `develop` manifest URL above — that is what
both test and production instances use. Releases are published as prereleases
(`-dev`) and are the tested artifacts.

## Build

```bash
./build.sh              # version from build.yaml, writes out/*.zip
```

Or via the Jellyfin plugin build tooling:

```bash
jprm --allow-type-extras plugin build .
```

**Releasing is a single command** — it builds, verifies the artifact, tags,
creates the release and checks that exactly one asset was uploaded:

```bash
scripts/release.sh --dry-run   # build + verify, touch nothing
scripts/release.sh             # the real thing
```

Do not release by tagging and pushing alone: the CI `publish` workflow was
removed precisely because it attached a second, differently-built ZIP to every
release. See [docs/RELEASE.md](docs/RELEASE.md) for the full reasoning.

## Status

Beta — core upload/download pipelines run, queue persistence works, GUI is
functional. See [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) for the full
functional specification, milestones and acceptance criteria.

Developed and tested on Linux/ARM64 (Raspberry Pi 5), pure managed code, no
platform dependencies.

## Documentation

- Full requirements spec: [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md)
- Release process: [docs/RELEASE.md](docs/RELEASE.md)

## Support

For bugs or feature requests please open an issue with:
- Jellyfin version
- Plugin version
- SubDL API response/quota status (if available)
- Relevant log excerpt (set log level to **Debug** in the plugin settings)
