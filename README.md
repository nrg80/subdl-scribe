# SubDL Scribe

SubDL Scribe keeps your Jellyfin library subtitled and gives back to [SubDL](https://subdl.com). It
**downloads** missing subtitles for the languages and libraries you select, and **uploads** the
subtitle tracks already embedded in your own media files.

The upload direction is **off by default** — see [Upload](#upload-off-by-default).

## Subtitles that are seconds out of sync

A downloaded subtitle is often off by whole seconds: the release's own timing does not match your rip.
Jellyfin plays it anyway, and you nudge the delay in the player every episode. SubDL Scribe measures it
and **removes the shift before the file is saved**.

- **Auto-sync (audio)** — the offset is measured against the spoken track. Planted shifts of −3 / +2 /
  +4 / +8 / +12 s came back as −3.20 / +1.80 / +3.80 / +7.80 / +11.80 s, so the correction is accurate to
  about **0.2 s**. The untouched original is kept beside the corrected file as
  `<name>.<lang>.srt.unsynchronized`, so a correction is reversible without spending download quota
  again. A subtitle whose offset **moves** is corrected **segment by segment** — the boundaries the
  drift check already found are the repair — which took the worst line over 36 drifting episodes from a
  10.74 s median to 4.51 s. Costs one audio decode per saved file.
- **Anchor-sync (reference)** — the route that needs no audio: it repairs a **drifting** subtitle by
  comparing it line-by-line with a plain subtitle in the **same language** beside the file or
  embedded in the container. Identical lines are the same line, so the difference is that line's true
  error. The repair is kept only when it improves the worst single line — a subtitle already in sync is
  **never** moved. Needs a plain same-language reference; without one nothing is changed and the
  finding is reported instead.

Both sit on the **Download** tab under **Quality gates (before download save)** and are **on by
default** — no episode needs a manual delay again.

> Both are on the prerelease channel (`develop`) and reach the stable catalog with the next release.

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
