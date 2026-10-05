# SubDL Scribe

SubDL Scribe keeps your Jellyfin library subtitled and gives back to [SubDL](https://subdl.com). It
**downloads** missing subtitles for the languages and libraries you select, and **uploads** the
subtitle tracks already embedded in your own media files.

The upload direction is **off by default** — see [Upload](#upload-off-by-default).

Licensed under **GPL-3.0-or-later** — see [LICENSE](LICENSE).

## How this plugin is developed

The code, the specification and this README are written by an **AI agent** under the maintainer's
direction. The maintainer supplies the requirements, the design decisions and the tests — they come
from his own library and the failures it produced — and reviews, measures and approves every change.

The [Jellyfin project asks](https://jellyfin.org/docs/general/contributing/llm-policies/) that projects
shared in its community disclose LLM involvement, so it is disclosed here. Anyone who would rather not
run LLM-written software can decide on that basis.

## Requirements

- Jellyfin **12.1.x** (targetAbi `12.1.0.0`)
- A [SubDL](https://subdl.com) account with its **API key**
- A **TMDb API key**

## Installation

### Via the plugin catalog

1. In Jellyfin, open **Dashboard → Plugins → Repositories**.
2. Add this repository URL:

   ```
   https://raw.githubusercontent.com/nrg80/subdl-scribe/main/manifest.json
   ```

3. Open **Dashboard → Plugins → All**, find **SubDL Scribe** and install it.
4. Restart Jellyfin.

### Manual

1. Download `Jellyfin.Plugin.SubdlSync_12.1.12.175.zip` from the [releases page](../../releases).
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

1. **SubDL API key.**
2. **SubDL username and password.**
3. **TMDb API key.**
4. **Libraries** to scan.
5. **Target languages** for the download side.

Save.

## Upload (off by default)

Uploads the subtitle tracks embedded in your media files to SubDL, through **your own** SubDL
account — enable it under **Dashboard → Plugins → SubDL Scribe → Upload → Upload enabled**. Quality
gates and duplicate protection apply here exactly as on the download side.

## Build

```bash
./build.sh    # reads the version from build.yaml, writes out/*.zip
```

Releasing is a single command — it builds, verifies the artifact, tags, creates the release and checks that exactly one asset was uploaded:

```bash
scripts/release.sh --dry-run   # build + verify, touch nothing
scripts/release.sh             # the real thing
```

The branch picks the channel: `develop` publishes a prerelease (`v<ver>-dev`), `main` publishes the
stable release. See [Branches](docs/RELEASE.md#branches) for what that means for installs.

Do not release by tagging and pushing alone: a release must carry exactly one asset. See [docs/RELEASE.md](docs/RELEASE.md).

## Documentation

- Functional specification and acceptance criteria: [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md)
- Release process: [docs/RELEASE.md](docs/RELEASE.md)
