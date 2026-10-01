# SubDL Scribe

SubDL Scribe is a Jellyfin plugin that downloads missing subtitles from [SubDL](https://subdl.com) for the languages and libraries you select, and uploads your own embedded subtitles back to SubDL.

Licensed under **GPL-3.0-or-later** — see [LICENSE](LICENSE).

## Requirements

- Jellyfin **12.1.x** (targetAbi `12.1.0.0`)
- A [SubDL](https://subdl.com) account
- **SubDL API key** (required)
- **TMDb API key** (required — without it series cannot be resolved)

## Installation

### Via the plugin catalog

1. In Jellyfin, open **Dashboard → Plugins → Repositories**.
2. Add this repository URL:

   ```
   https://raw.githubusercontent.com/nrg80/subdl-scribe/develop/manifest.json
   ```

3. Open **Dashboard → Plugins → All**, find **SubDL Scribe** and install it.
4. Restart Jellyfin.

### Manual

1. Download `Jellyfin.Plugin.SubdlSync_12.1.12.157.zip` from the [releases page](../../releases).
2. Create a folder for the plugin under your Jellyfin `plugins/` directory and extract the ZIP into it:

   ```
   /var/lib/jellyfin/plugins/SubDL Scribe/
   ├── Jellyfin.Plugin.SubdlSync.dll
   ├── LanguageDetection.dll
   └── LiteDB.dll
   ```

3. Restart Jellyfin.

## Quick start

Open **Dashboard → Plugins → SubDL Scribe** and fill in:

1. **SubDL API key** — and your SubDL username, if you have one.
2. **TMDb API key.**
3. **Libraries** to scan.
4. **Target languages** for the download side.

Save, then run the tasks **SubDL/TMDB — Subtitle Download** and **SubDL/TMDB — Subtitle Upload** from **Dashboard → Scheduled Tasks**, or wait for their schedule. Upload is off until you enable it on the Upload tab.

The tab **General** holds credentials, library selection, rate limit and filters; **Upload** and **Download** hold the switches for each direction; **Expert** holds the scoring, quota and diagnostic knobs.

## Build

```bash
./build.sh    # reads the version from build.yaml, writes out/*.zip
```

Releasing is a single command — it builds, verifies the artifact, tags, creates the release and checks that exactly one asset was uploaded:

```bash
scripts/release.sh --dry-run   # build + verify, touch nothing
scripts/release.sh             # the real thing
```

Do not release by tagging and pushing alone: a release must carry exactly one asset. See [docs/RELEASE.md](docs/RELEASE.md).

## Documentation

- Functional specification and acceptance criteria: [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md)
- Release process: [docs/RELEASE.md](docs/RELEASE.md)
