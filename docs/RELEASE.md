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

## Releasing on `main` (prerelease → released line)

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

A release cut on `develop` leaves `main` a commit behind and both branches changed in `manifest.json`:

| | `main` | `develop` |
|---|---|---|
| `manifest.json` `sourceUrl` | `…/download/v<ver>/…` | `…/download/v<ver>-dev/…` |
| everything else | identical | identical |

**The merge therefore conflicts in `manifest.json` every time — that is expected, not a mistake.**
It is the one generated file that legitimately differs.

```bash
# 1. develop is released and pushed; version, csproj and REQUIREMENTS.md agree
git checkout develop && git pull --ff-only

# 2. bring the code over. manifest.json WILL conflict:
git checkout main && git pull --ff-only
git merge develop
#    CONFLICT (content): Merge conflict in manifest.json

# 3. take either side — release.sh rewrites this file in step 5 anyway.
#    The develop side carries the newer version, so it is the less confusing choice:
git checkout --theirs manifest.json
git add manifest.json
git commit -m "merge develop into main"

# 4. PUSH FIRST. release.sh refuses to run unless the branch equals origin/<branch>
#    — it aborts with "main is not pushed / behind origin" on an unpushed merge:
git push origin main

# 5. release on main. This writes the stable tag AND rewrites the manifest
#    entry to the v<ver> URL, so the -dev reference from step 3 is corrected here:
./scripts/release.sh --dry-run   # expect: channel stable, tag v<ver> (no -dev)
./scripts/release.sh             # pushes the manifest commit itself
```

**Order matters: merge, push, then release.** `release.sh` verifies the branch is level with its
remote before it does anything, so running it between the merge and the push aborts. The script pushes
only the manifest commit it makes itself — the merge commit has to be pushed by hand first.

After step 5, `main`'s manifest names `…/download/v<ver>/…` and the catalog served from `main` offers
the stable version. Verify, don't assume:

```bash
curl -sS 'https://raw.githubusercontent.com/nrg80/subdl-scribe/main/manifest.json' \
  | python3 -c "import json,sys;print(json.load(sys.stdin)[0]['versions'][0]['sourceUrl'])"
# expect: .../download/v<version>/Jellyfin.Plugin.SubdlSync_<version>.zip   (no -dev)
```

**Do not fast-forward `main` to `develop` and stop there.** That points `main` at the `-dev` tag and
publishes a prerelease URL as the installable version — the exact thing the branch-derived tag
prevents. `main` has to be released on.

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

**Verify `main` by installing from it.** The whole point of the branch split is that a user who
installs from the README's URL gets the released line, so the end-to-end check is: add
`…/subdl-scribe/main/manifest.json` as a Jellyfin repository, install from the catalog, and confirm
the loaded assembly version and that the downloaded DLL matches the release asset's hash. A 200 on
the raw URL does not prove the catalog resolves.

## CI

`build.yaml` and `test.yaml` compile and test every push to `develop`. `scan-codeql.yaml` runs weekly and on push. None of them publishes a release.

## Installing

Dashboard → plugins → all → SubDL Scribe → install → restart Jellyfin.

Versions older than the current one are installable from their release page only.
