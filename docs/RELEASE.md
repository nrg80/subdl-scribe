# Releasing SubDL Scribe

## The release

```bash
# 1. bump the version in build.yaml, the csproj and docs/REQUIREMENTS.md
# 2. write the changelog entry at the top of build.yaml's changelog block
# 3. build, verify, tag, release and check the result
scripts/release.sh
```

`scripts/release.sh --dry-run` builds and verifies without committing, tagging or uploading.

**One branch, one channel:**

| branch | tag | release |
|---|---|---|
| `develop` | `v<ver>-dev` | prerelease |

`develop` is the only branch that exists today and the repository's default. Every release here is a
prerelease, so a build cannot be handed out as a finished one by accident. The channel is derived from
the branch rather than from a flag, so it cannot drift from where it was cut, and the tag name always
says which channel a ZIP came from.

`release.sh` still knows a `main` channel (`v<ver>`, stable) and would use it on a branch of that name.
With no such branch the case cannot trigger, and it is kept deliberately: restoring a stable line means
recreating the branch *and* releasing on it, and the script should not have to be edited for that.

The script aborts when the tree is dirty, the branch is not `develop` (or the version argument does not
match `build.yaml`), that branch is not pushed, the tag already exists, or the version in `build.yaml`
and the csproj disagree.

## What the script verifies

- version: `build.yaml` == command argument; the csproj matches with or without the `-dev` suffix
- `category` is exactly `Subtitles`
- a changelog entry exists for this version
- the plugin description is identical in `build.yaml` and in the plugin card, is at most 260 characters, and names both required keys
- tree clean, branch is `develop`, and that branch is pushed
- the tag does not exist yet
- the ZIP carries the plugin DLL, its two dependencies (`LanguageDetection`, `LiteDB`) and `build.yaml`, and no `meta.json`
- version and category inside the ZIP, not in the working tree
- exactly one asset after upload
- the md5 of the downloaded asset equals the built one
- the **tagged** `build.yaml` carries the version and `Subtitles`

The order is: manifest commit and push, then tag, then release. The release therefore never points at a tag the server does not know.

## Catalog manifest

`manifest.json` is committed on `develop` and read by Jellyfin from
`https://raw.githubusercontent.com/nrg80/subdl-scribe/develop/manifest.json`.

The script writes the new version, the release URL and the md5 of the uploaded ZIP, then commits and
pushes the manifest before creating the release. The manifest carries exactly one version entry.

## Branches

`develop` is the repository's default branch and the only release branch. It carries the prereleases
(`v<ver>-dev`) and is what the two test instances subscribe to.

There is **no stable branch**: `main` was deleted on 03.10.2026 after it had been used for exactly one
release, `12.1.12.169`. The version that release published is still installable — a release lives in
its tag and its GitHub release, not in the branch that cut it — but no branch serves a stable catalog
URL any more.

**What that means in practice.** The README's install URL points at `develop`, so whoever installs from
the catalog gets the newest prerelease. That is the intended state: everything published here is
pre-release software, and the `-dev` tag says so.

**If a stable line is ever wanted again,** it does not come back by recreating a branch alone. It needs
all three of: a branch that survives (recreating `main` and pushing it again is the smallest form), a
manifest on that branch naming a `v<ver>` — **not** a `v<ver>-dev` — `sourceUrl`, and a release cut on
that branch so the tag, the asset and the manifest agree. Pointing a fresh branch at `develop` gives a
stable-looking branch that serves the prerelease URL, which is the one mistake this document used to
warn about; the warning still holds, it just has no branch to apply to today.

Restoring the deleted branch, if that is ever decided:
`git push origin a12b8b4d3b6335c8f061ac03ce77fce007f5a7ff:refs/heads/main` puts back the exact tip it had
(a docs-only commit). The stable release it served — tag `v12.1.12.169` — was never deleted.

**Never rehearse the release flow against a cloned copy without cutting the API off.** `REPO_SLUG` is
hardcoded to `nrg80/subdl-scribe` and the token is read from `/opt/data/.secrets.json`, so a
`release.sh` run in a throwaway clone does not stay local: it looks up and creates real releases on
GitHub. In a sandbox run it found the existing release and tried to re-upload its asset — GitHub
rejected the duplicate name and the asset stayed intact, but that was luck, not containment. For any
rehearsal use `--dry-run`, or point the clone's `origin` at a local bare repository *and* expect the
API calls to still reach GitHub; a real test release must go to a different `REPO_SLUG`.

## CI

`build.yaml` and `test.yaml` compile and test every push to `develop`. `scan-codeql.yaml` runs weekly and on push. None of them publishes a release.

## Installing

Dashboard → plugins → all → SubDL Scribe → install → restart Jellyfin.

Versions older than the current one are installable from their release page only.
