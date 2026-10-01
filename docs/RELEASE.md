# Releasing SubDL Scribe

This document is the single source of truth for how a release is made. If the
behaviour of the tooling ever disagrees with this file, the tooling is wrong.

## The one supported path

```bash
# 1. bump the version in build.yaml, the csproj and docs/REQUIREMENTS.md
# 2. write the changelog entry at the top of build.yaml's changelog block
# 3. run it — it builds, verifies, tags, releases and checks the result
scripts/release.sh
```

Use `scripts/release.sh --dry-run` to build and verify without touching
anything. The script refuses to run when the tree is dirty, when the branch is
not `develop`, when `develop` is not pushed, when a tag already exists, or when
the version in `build.yaml` and the csproj disagree.

## Why it works this way

The release path used to be split in two, and that is what made every release
produce the same arguments:

**Two ZIPs per release.** `scripts/release.sh` builds a ZIP whose metadata lives
in `build.yaml`. The Jellyfin `publish.yaml` workflow built the same commit
again with its own toolchain, and attached a *second* ZIP whose metadata lives
in `meta.json`, plus an `.md5` and a `.sha256`. Four assets, two of them built
from the same source by different tools. Someone had to delete the extras by
hand for every release, and the manifest pointed at only one of the two.

**The wrong value could ship and no fix could reach it.** The publish workflow
builds the *tagged* commit. `v12.1.12.80-dev` was tagged while `build.yaml`
still said `category: "Subtitle"` (singular); the correction landed on
`develop` afterwards. Because the tag never moved, the released ZIP carried the
wrong category forever, and the only symptom was a plugin filed under the wrong
heading in the catalog. **Always check the tagged commit, never the branch.**

**That is the rule this repository is built on now:** one build, one ZIP, one
asset, verified locally, and the tag checked for what it actually carries.

Removed for this reason: `.github/workflows/publish.yaml` (attached assets) and
`.github/workflows/changelog.yaml` (drafted releases nobody used — it had never
run, because it is gated on the upstream template repository).

CI keeps doing what it is good at: `build.yaml` and `test.yaml` compile and test
every push to `develop`. Neither touches a release.

## What the script enforces

| Check | Why |
| --- | --- |
| `build.yaml` version == csproj version == argument | a half-bumped version is the classic source of a release that lies about itself |
| `category` is exactly `Subtitles` | the v12.1.12.80 failure: singular `Subtitle` shipped |
| changelog entry exists for this version | releases without notes cannot be diagnosed later |
| tree clean, branch `develop`, pushed | a release must be reproducible from the remote |
| tag does not exist yet | reusing a tag silently keeps the old assets and the old build |
| the ZIP carries `build.yaml`, not `meta.json` | a `meta.json` ZIP is the CI format, not ours |
| version and category inside the ZIP | the artifact is checked, not the working tree |
| exactly one asset after upload | the contract that was broken for every release |
| downloaded asset md5 == built md5 | proves what users get is what was tested |
| the **tagged** `build.yaml` carries version and `Subtitles` | the only check that would have caught v80 |

## Manifest

`manifest.json` is committed on `develop` and read by Jellyfin from
`https://raw.githubusercontent.com/nrg80/subdl-scribe/develop/manifest.json`.
The script writes it with the new version, the release URL and the md5 of the
uploaded ZIP, then commits and pushes it *before* creating the release, so the
catalog and the release cannot disagree.

The manifest keeps exactly one version entry. An earlier entry pointing at a
tag other than the current one is a broken install waiting to happen, and the
catalog is not a changelog — the changelog lives in `build.yaml` and in the
release notes.

## Versions

Both Jellyfin instances are configured with the `develop` manifest URL, so a
release is installable as soon as it is published. The catalog is served from
`develop` and not from `main`: the repository ships prereleases (`-dev`) only.
`main` exists as a reference line pinned to the last released version, and the
release path never pushes to it — it moves only on an explicit instruction.

Install on an instance:

```bash
# dashboard -> plugins -> all -> SubDL Scribe -> install -> restart Jellyfin
```

Versions older than the current one remain installable only from their release
page, not from the catalog.
