# Releasing SubDL Scribe

## The release

```bash
# 1. bump the version in build.yaml, the csproj and docs/REQUIREMENTS.md
# 2. write the changelog entry at the top of build.yaml's changelog block
# 3. build, verify, tag, release and check the result
scripts/release.sh
```

`scripts/release.sh --dry-run` builds and verifies without committing, tagging or uploading.

**The branch picks the channel:**

| branch | tag | release |
|---|---|---|
| `develop` | `v<ver>-dev` | prerelease |
| `main` | `v<ver>` | stable |

So a stable release cannot be cut by accident, and a prerelease cannot silently become the version
users install. Run the script on the branch whose channel you mean.

The script aborts when the tree is dirty, the branch is neither `develop` nor `main`, that branch is
not pushed, the tag already exists, or the version in `build.yaml` and the csproj disagree.

## What the script verifies

- version: `build.yaml` == command argument; the csproj matches with or without the `-dev` suffix
- `category` is exactly `Subtitles`
- a changelog entry exists for this version
- the plugin description is identical in `build.yaml` and in the plugin card, is at most 260 characters, and names both required keys
- tree clean, branch is `develop` or `main`, and that branch is pushed
- the tag does not exist yet
- the ZIP carries the plugin DLL, its two dependencies (`LanguageDetection`, `LiteDB`) and `build.yaml`, and no `meta.json`
- version and category inside the ZIP, not in the working tree
- exactly one asset after upload
- the md5 of the downloaded asset equals the built one
- the **tagged** `build.yaml` carries the version and `Subtitles`

The order is: manifest commit and push, then tag, then release. The release therefore never points at a tag the server does not know.

## Catalog manifest

`manifest.json` is committed on `develop` and read by Jellyfin from
`https://raw.githubusercontent.com/nrg80/subdl-scribe/main/manifest.json` (released line) or
`https://raw.githubusercontent.com/nrg80/subdl-scribe/develop/manifest.json` (prerelease line).

The script writes the new version, the release URL and the md5 of the uploaded ZIP, then commits and pushes the manifest before creating the release.

The manifest carries exactly one version entry.

## Branches

`main` carries the released line and is the URL the README hands to users, so installing from the
catalog yields the version `main` holds. `develop` carries the prereleases (`-dev`) and is what the
two test instances subscribe to.

`scripts/release.sh` writes and pushes the manifest to **the branch it runs on**, so a release cut on
`main` advances `main` and a release cut on `develop` advances `develop`. Each branch therefore serves
its own version, and no manual branch move is needed.

Promoting a tested prerelease to the released line is still an explicit step: merge or fast-forward
`develop` into `main`, then run the script on `main` so a stable tag and the matching manifest entry
are produced. Fast-forwarding alone would leave `main` pointing at a `-dev` release.

## CI

`build.yaml` and `test.yaml` compile and test every push to `develop`. `scan-codeql.yaml` runs weekly and on push. None of them publishes a release.

## Installing

Dashboard → plugins → all → SubDL Scribe → install → restart Jellyfin.

Versions older than the current one are installable from their release page only.
