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
| `main` | `v<ver>` | stable |

`develop` is the repository's default branch. Every release there is a prerelease, so a build cannot be
handed out as a finished one by accident. The channel is derived from the branch rather than from a flag,
so it cannot drift from where it was cut, and the tag name always says which channel a ZIP came from.
`main` publishes the stable line; see [Branches](#branches) for what that means for installs.

The script aborts when the tree is dirty, the branch is not `develop` or `main` (or the version argument
does not match `build.yaml`), that branch is not pushed, the tag already exists, or the version in
`build.yaml` and the csproj disagree.

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

`manifest.json` is committed on **the branch the release runs on** and read by Jellyfin from
`https://raw.githubusercontent.com/nrg80/subdl-scribe/main/manifest.json` (released line) or
`https://raw.githubusercontent.com/nrg80/subdl-scribe/develop/manifest.json` (prerelease line).

The script writes the new version, the release URL and the md5 of the uploaded ZIP, then commits and
pushes the manifest before creating the release. The manifest carries exactly one version entry.

## Branches

`develop` is the repository's default branch and carries the prereleases (`v<ver>-dev`); the two test
instances subscribe to it. `main` carries the released line (`v<ver>`) and its catalog URL is the one
the README hands to users.

**Both branches exist, and each serves its own version.** Neither is a promotion of the other: a release
is cut on the branch whose channel is meant, because `scripts/release.sh` writes and pushes the manifest
to the branch it runs on.

**Not every version reaches `main` — only the ones picked for it.** `develop` releases freely and
often; `main` moves deliberately, one chosen version at a time, on the maintainer's decision. Skipping
versions is normal (`main` went from `12.1.12.169` straight to `12.1.12.175`), because a release on
`main` is a *release*, not a merge of everything in between — the merge brings the code forward, but the
release is a choice. Nothing carries `main` along automatically: no CI workflow touches it, no scheduled
job runs `release.sh`, and the script pushes only the branch it runs on.

**`main` was absent for part of 03.10.2026** — deleted after it had served exactly one release,
`12.1.12.169`, and restored the same day for `12.1.12.175`. It came back by re-creating it at the tip it
had (`a12b8b4d`, a docs-only commit) and merging `develop` into it, **not** by pointing it at `develop`:
a fast-forward carries `develop`'s `manifest.json` along and a stable-looking branch then serves the
prerelease URL, which is the one mistake this document exists to prevent.

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

**Whenever `main` is touched, check the manifest it serves.** It is cheap and it is the only thing that
catches this class of mistake — the reference to an "assertion below" that used to stand here pointed at
a block that had been dropped, so the check is written out in full:

```bash
curl -sS https://raw.githubusercontent.com/nrg80/subdl-scribe/main/manifest.json \
  | python3 -c "import json,sys; m=json.load(sys.stdin); v=m[0]['versions'][0]; print(v['version'], v['sourceUrl'])"
# must print a released version and a tag WITHOUT -dev
```

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

## Deploying to the test bench

`scripts/deploy-jf-test.sh <version> <zip>` stops the test Jellyfin, removes every other
`SubDL Scribe_*` folder (one GUID — two copies would both load), unpacks the ZIP, writes `meta.json`
and starts the instance again. The order is mandatory: replacing the DLL while Jellyfin runs leaves the
process holding a file that no longer matches the loaded assembly, and .NET compiles methods lazily, so
a method first reached during SHUTDOWN is read off disk then and the runtime dies with
`BadImageFormatException: Bad IL range`.

**The package is verified before anything is removed**, and the extraction is verified after it: a
deploy that cannot unpack must not leave an install holding nothing but the logo while reporting
success. Both failure modes are real and were measured on 08.10.2026 — `unzip` is not on this host's
PATH, so the extraction failed silently, the old version had already been deleted, and Jellyfin
answered 503 on a deploy that printed no error. The script now falls back to Python's `zipfile` and
aborts with `extract failed — no DLL in <path>` if the assembly is still missing. **A negative control
is part of the change:** handing it a ZIP without the plugin assembly must abort before the running
instance is touched.

**`AssemblyVersion` is bumped by hand.** `release.sh` verifies only the `<Version>` element against
`build.yaml`, so a manual version bump that misses `<AssemblyVersion>`/`<FileVersion>` ships a package
whose `meta.json` says the new version while Jellyfin's log reports the old assembly version for the
newly loaded plugin. Bump all three together.

**The readiness line of the script can end on `HTTP 000` while the instance is fine.** The loop breaks
on the first 200 and then re-probes once more; a slow start makes that second probe time out. Verify
with your own call — measured 08.10.2026: the script printed `bereit nach ~3s` with `HTTP 000`, and the
same endpoint answered 200 eight seconds later.
