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

`manifest.json` is committed on **the branch the release runs on** and read by Jellyfin from
`https://raw.githubusercontent.com/nrg80/subdl-scribe/main/manifest.json` (released line) or
`https://raw.githubusercontent.com/nrg80/subdl-scribe/develop/manifest.json` (prerelease line).

The script writes the new version, the release URL and the md5 of the uploaded ZIP, then commits and
pushes the manifest before creating the release. The manifest carries exactly one version entry.

## Branches

`develop` is the repository's default branch and carries the prereleases (`v<ver>-dev`); the two test
instances subscribe to it. `main` carries the released line (`v<ver>`) and its catalog URL is the one
the README hands to users. Both exist: `main` was restored on 03.10.2026 for `12.1.12.175`, after a
short period in which it had no stable line at all.

**Not every version reaches `main` — only the ones picked for it.** `develop` releases freely and
often; `main` moves deliberately, one chosen version at a time, on the maintainer's decision. There is
no automatic promotion and nothing that carries `main` along:

- no CI workflow touches `main` (`build.yaml` and `test.yaml` are triggered by `develop` only; none of
  the workflows creates a tag or a release),
- no scheduled job runs `release.sh`,
- `scripts/release.sh` pushes only the branch it runs on — never a second one.

So a version stays on `develop` until someone decides otherwise, and skipping versions is normal:
`main` may go from `12.1.12.169` straight to `12.1.12.175`, because a release on `main` is a *release*,
not a merge of everything that happened in between. (The merge in step 2 does bring the code forward —
the point is that the *release* is a choice, not a side effect.)

Normal work happens on `develop`: every release there is tagged `v<ver>-dev` and marked prerelease,
and the two test instances follow it. A release on `main` is not a promotion of a branch pointer —
**the version that users install is the one `main`'s manifest names**, so `main` must be released on,
not merely fast-forwarded.

`main` was restored on 03.10.2026 by re-creating it at the tip it had before deletion
(`a12b8b4d`, a docs-only commit) and merging `develop` into it, so the branch keeps its own history
rather than being pointed at `develop`. A fast-forward would have carried `develop`'s `manifest.json`
with it and served the prerelease URL from a stable-looking branch.

The same applies to a **docs-only change that has to appear on `main`**: `git branch -f main develop`
carries `develop`'s `manifest.json` with it, so `main` silently starts serving the prerelease again.
This happened here while documenting this very section. The fix is to take the code and docs from
`develop` but keep `main`'s own manifest:

```bash
git checkout main
git checkout develop -- README.md docs/RELEASE.md   # the content you want
git checkout main -- manifest.json                  # keep main's released manifest
git commit -m "docs: …" && git push origin main
```

Whenever `main` is touched, end with the manifest assertion below — it is cheap and it is the only
thing that catches this class of mistake.

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
