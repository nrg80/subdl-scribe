#!/usr/bin/env bash
# Release SubDL Scribe — the ONE supported release path.
#
# We build locally, verify the artifact, and upload exactly one ZIP. The CI
# workflows only build and test; they never touch a release. See
# docs/RELEASE.md for why, and .github/workflows/ for the removed workflows.
#
# Usage:
#   scripts/release.sh                 # version from build.yaml
#   scripts/release.sh --dry-run       # build + verify, print the plan, touch nothing
#   scripts/release.sh 12.1.12.82      # explicit version (must match build.yaml)

set -euo pipefail

REPO_SLUG="nrg80/subdl-scribe"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

API="https://api.github.com"
UPLOADS="https://uploads.github.com"

DRY_RUN=0
ARG=""
for a in "$@"; do
    case "$a" in
        --dry-run) DRY_RUN=1 ;;
        *)         ARG="$a" ;;
    esac
done

say()  { printf '\n=== %s ===\n' "$*"; }
ok()   { printf '  %s\n' "$*"; }
die()  { echo "ABORT: $*" >&2; exit 1; }

# --- 1. version + preconditions ---------------------------------------------
say "Preconditions"
VER_BUILD="$(grep '^version:' build.yaml | sed 's/.*"\(.*\)".*/\1/')"
VER="${ARG:-$VER_BUILD}"
[[ "$VER" == "$VER_BUILD" ]] || die "argument $VER != build.yaml $VER_BUILD — bump build.yaml first"
VER_CSPROJ="$(grep -oP '(?<=<Version>)[^<]+' Jellyfin.Plugin.SubdlSync/Jellyfin.Plugin.SubdlSync.csproj | head -1)"
ok "build.yaml : $VER_BUILD"
ok "csproj     : $VER_CSPROJ"
[[ "$VER_CSPROJ" == "$VER" || "$VER_CSPROJ" == "$VER-dev" ]] \
    || die "csproj version $VER_CSPROJ does not match build.yaml $VER"

# The category check guards against the v12.1.12.80 class of bug: the release
# must never go out with anything but the plural "Subtitles".
CATEGORY="$(grep '^category:' build.yaml | sed 's/.*"\(.*\)".*/\1/')"
ok "category   : $CATEGORY"
[[ "$CATEGORY" == "Subtitles" ]] || die "category must be \"Subtitles\", found \"$CATEGORY\""

# The plugin card in Dashboard → Plugins renders Plugin.Description, which is
# compiled into the DLL — NOT the description in build.yaml, which only feeds the
# catalog manifest. Two carriers, so they drift: a shortened build.yaml text stays
# invisible while the card still shows the old essay. Guard both, and keep the card
# to a glanceable length.
python3 - <<'PY' || die "plugin description check failed"
import re, sys

def build_yaml_desc():
    text = open('build.yaml', encoding='utf-8').read()
    m = re.search(r'^description: >\n((?:[ \t].*\n|\n)*)', text, re.M)
    if not m:
        sys.exit('no description block in build.yaml')
    return ' '.join(l.strip() for l in m.group(1).split('\n') if l.strip())

def plugin_cs_desc():
    text = open('Jellyfin.Plugin.SubdlSync/Plugin.cs', encoding='utf-8').read()
    m = re.search(r'public override string Description =>\s*"((?:[^"\\]|\\.)*)"', text)
    if not m:
        sys.exit('no Description override in Plugin.cs')
    return m.group(1)

LIMIT = 260
yaml_desc, cs_desc = build_yaml_desc(), plugin_cs_desc()
for label, desc in (('build.yaml', yaml_desc), ('Plugin.cs (plugin card)', cs_desc)):
    print(f'  {label:22s}: {len(desc)} chars')
    if len(desc) > LIMIT:
        sys.exit(f'  FAIL: {label} description is {len(desc)} chars (limit {LIMIT}) — '
                 'the card is a one-glance summary, not a feature list')

# The card must state the required keys; without this the text is merely short.
if 'TMDb API Key' not in cs_desc or 'SubDL' not in cs_desc:
    sys.exit('  FAIL: Plugin.cs description no longer names the required SubDL/TMDb keys')
if yaml_desc != cs_desc:
    sys.exit('  FAIL: build.yaml and Plugin.cs descriptions differ — two carriers, '
             'one text. build.yaml:\n    ' + yaml_desc + '\n  Plugin.cs:\n    ' + cs_desc)
print('  OK: both carriers identical, card is glanceable')
PY

CHANGELOG_ENTRY="$(python3 - "$VER" <<'PY'
import re, sys
ver = sys.argv[1]
text = open('build.yaml', encoding='utf-8').read()
m = re.search(r'^changelog: >\n((?:[ \t].*\n)*)', text, re.M)
if not m:
    sys.exit('no changelog block in build.yaml')
lines, keep, on = m.group(1).split('\n'), [], False
for line in lines:
    if line.strip().startswith('v' + ver + ':'):
        on = True
    elif re.match(r'^\s{2}v\d', line):
        on = False
    if on:
        keep.append(line)
out = '\n'.join(keep).strip()
if not out:
    sys.exit(f'no changelog entry for v{ver} in build.yaml')
print(out)
PY
)" || die "changelog extraction failed"
ok "changelog  : $(echo "$CHANGELOG_ENTRY" | head -1 | cut -c1-64)…"

TAG="v${VER}-dev"
if [[ $DRY_RUN -eq 0 ]]; then
    BRANCH="$(git rev-parse --abbrev-ref HEAD)"
    [[ "$BRANCH" == "develop" ]] || die "must release from develop (on $BRANCH)"
    [[ -z "$(git status --porcelain)" ]] || die "working tree not clean — commit first"
    git fetch --quiet origin develop
    [[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/develop)" ]] \
        || die "develop is not pushed / behind origin"
    git rev-parse "$TAG" >/dev/null 2>&1 && die "tag $TAG already exists"
    TOKEN="${GITHUB_TOKEN:-}"
    if [[ -z "$TOKEN" && -f /opt/data/.secrets.json ]]; then
        TOKEN="$(python3 -c "import json;print(json.load(open('/opt/data/.secrets.json'))['github']['nrg80']['token'])")"
    fi
    [[ -n "$TOKEN" ]] || die "no GitHub token"
    AUTH="Authorization: token $TOKEN"
else
    ok "(dry-run: git state and token not checked)"
fi

# --- 2. build ----------------------------------------------------------------
say "Build"
./build.sh "$VER" | tail -3
ZIP="$ROOT/out/Jellyfin.Plugin.SubdlSync_${VER}.zip"
[[ -f "$ZIP" ]] || die "build produced no ZIP at $ZIP"

# --- 3. verify the artifact --------------------------------------------------
say "Verify artifact"
python3 - "$ZIP" "$VER" "$CATEGORY" <<'PY'
import sys, zipfile
zip_path, ver, want_cat = sys.argv[1:4]
z = zipfile.ZipFile(zip_path)
names = z.namelist()
for req in ('Jellyfin.Plugin.SubdlSync.dll', 'LanguageDetection.dll', 'LiteDB.dll', 'build.yaml'):
    if req not in names:
        sys.exit(f"  FAIL: {req} missing from the ZIP")
if any(n.endswith('meta.json') for n in names):
    sys.exit("  FAIL: this ZIP carries meta.json — that is the CI format, not ours")
txt = z.read('build.yaml').decode()
def field(name):
    return [l.split(':', 1)[1].strip().strip('"') for l in txt.split('\n') if l.startswith(name + ':')][0]
if field('version') != ver:
    sys.exit(f"  FAIL: ZIP version {field('version')} != {ver}")
if field('category') != want_cat:
    sys.exit(f"  FAIL: ZIP category {field('category')!r} != {want_cat!r}")
print(f"  contents : {sorted(names)}")
print(f"  version  : {field('version')}")
print(f"  category : {field('category')}")
print("  OK")
PY
MD5="$(md5sum "$ZIP" | cut -d' ' -f1)"
ok "md5        : $MD5"

# --- 4. manifest -------------------------------------------------------------
say "Manifest"
MANIFEST_OUT="$ROOT/manifest.json"
[[ $DRY_RUN -eq 1 ]] && MANIFEST_OUT="/tmp/subdl-manifest-dryrun.json"
python3 - "$MANIFEST_OUT" "$VER" "$MD5" "$REPO_SLUG" "$TAG" "$CHANGELOG_ENTRY" <<'PY'
import json, sys
from datetime import datetime, timezone
out_path, ver, md5, slug, tag, changelog = sys.argv[1:7]
manifest = json.load(open('manifest.json', encoding='utf-8'))
entry = manifest[0]
if entry['category'] != 'Subtitles':
    sys.exit(f"  FAIL: manifest category is {entry['category']!r}")
abi = entry['versions'][0]['targetAbi'] if entry['versions'] else '12.1.0.0'
url = f"https://github.com/{slug}/releases/download/{tag}/Jellyfin.Plugin.SubdlSync_{ver}.zip"
entry['versions'] = [{
    'version': ver,
    'changelog': changelog,
    'targetAbi': abi,
    'sourceUrl': url,
    'checksum': md5,
    'timestamp': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
}]
with open(out_path, 'w', encoding='utf-8') as fh:
    json.dump(manifest, fh, indent=2, ensure_ascii=False)
    fh.write('\n')
print(f"  version  : {ver}")
print(f"  checksum : {md5}")
print(f"  url      : {url}")
print(f"  written  : {out_path}")
PY

if [[ $DRY_RUN -eq 1 ]]; then
    say "Dry run complete — nothing was committed, tagged or released"
    ok "ZIP    : $ZIP"
    ok "md5    : $MD5"
    ok "tag    : $TAG (would be created)"
    exit 0
fi

git add manifest.json
git -c user.name="SubDL Scribe" -c user.email="noreply@localhost" \
    commit -q -m "release: ${VER}" || ok "(manifest unchanged)"
git push --quiet origin develop

# --- 5. tag, and check what the TAG carries ---------------------------------
say "Tag $TAG"
git tag -a "$TAG" -m "SubDL Scribe $VER"
# Tag first, release second: the release can then never point at a tag the
# server does not know. And check the TAGGED build.yaml, not the working tree —
# a tag left behind on an older commit is exactly how v12.1.12.80 shipped
# category "Subtitle" while the branch already said "Subtitles".
git push --quiet origin "$TAG"
python3 - "$TAG" "$VER" <<'PY'
import subprocess, sys
tag, ver = sys.argv[1], sys.argv[2]
txt = subprocess.run(['git', 'show', f'{tag}:build.yaml'],
                     capture_output=True, text=True, check=True).stdout
def field(name):
    return [l.split(':', 1)[1].strip().strip('"') for l in txt.split('\n') if l.startswith(name + ':')][0]
if field('version') != ver:
    sys.exit(f"  FAIL: tag {tag} carries version {field('version')}, expected {ver}")
if field('category') != 'Subtitles':
    sys.exit(f"  FAIL: tag {tag} carries category {field('category')!r}, expected 'Subtitles'")
print(f"  tag build.yaml: version {field('version')}, category {field('category')}  OK")
PY

# --- 6. release with exactly one asset ---------------------------------------
say "Release"
REL_ID="$(curl -sS -m 30 -H "$AUTH" "$API/repos/$REPO_SLUG/releases/tags/$TAG" \
    | python3 -c "import json,sys;print(json.load(sys.stdin).get('id',''))" 2>/dev/null || true)"
if [[ -n "$REL_ID" ]]; then
    ok "release exists (id $REL_ID) — reusing"
else
    python3 - "$TAG" "$CHANGELOG_ENTRY" <<'PY' > /tmp/subdl-release-payload.json
import json, sys
print(json.dumps({
    "tag_name": sys.argv[1], "target_commitish": "develop", "name": sys.argv[1],
    "body": sys.argv[2], "draft": False, "prerelease": True,
}))
PY
    REL_ID="$(curl -sS -m 60 -X POST -H "$AUTH" -H 'Accept: application/vnd.github+json' \
        "$API/repos/$REPO_SLUG/releases" -d @/tmp/subdl-release-payload.json \
        | python3 -c "import json,sys;d=json.load(sys.stdin);print(d.get('id') or sys.exit('  FAIL: '+str(d)))")"
    ok "created release id $REL_ID"
fi

# Drop every asset that is not our verified ZIP, then (re-)upload it. The CI
# upload workflow is gone, so extras should not appear — but an old run or a
# manual upload can leave some, and "exactly one asset" is the contract.
KEEP="Jellyfin.Plugin.SubdlSync_${VER}.zip"
curl -sS -m 30 -H "$AUTH" "$API/repos/$REPO_SLUG/releases/$REL_ID/assets" \
 | python3 -c "
import json, sys
for a in json.load(sys.stdin):
    if a['name'] != '$KEEP':
        print('%s|%s' % (a['id'], a['name']))" \
 | while IFS='|' read -r aid aname; do
       [[ -z "$aid" ]] && continue
       curl -sS -m 60 -X DELETE -H "$AUTH" -o /dev/null \
           "$API/repos/$REPO_SLUG/releases/assets/$aid"
       ok "removed foreign asset: $aname"
   done

curl -sS -m 300 -X POST -H "$AUTH" -H 'Content-Type: application/zip' \
    --data-binary @"$ZIP" \
    "$UPLOADS/repos/$REPO_SLUG/releases/$REL_ID/assets?name=$KEEP" \
 | python3 -c "import json,sys;d=json.load(sys.stdin);print('  uploaded: %s (%s, %d bytes)' % (d.get('name'), d.get('state'), d.get('size', 0)))" \
 || die "asset upload failed"

# --- 7. verify the published release ----------------------------------------
say "Verify release"
curl -sS -m 60 -H "$AUTH" "$API/repos/$REPO_SLUG/releases/$REL_ID/assets" \
 | python3 -c "
import json, sys
assets = json.load(sys.stdin)
print('  assets: %d' % len(assets))
for a in assets:
    print('   %-48s %d' % (a['name'], a['size']))
if len(assets) != 1:
    sys.exit('  FAIL: release must carry exactly one asset')
if assets[0]['name'] != '$KEEP':
    sys.exit('  FAIL: unexpected asset name')"
GOT="$(curl -sSL -m 300 -o /tmp/subdl-asset.zip \
    "https://github.com/$REPO_SLUG/releases/download/$TAG/$KEEP" \
    && md5sum /tmp/subdl-asset.zip | cut -d' ' -f1)"
[[ "$GOT" == "$MD5" ]] || die "downloaded asset md5 $GOT != built $MD5"
ok "downloaded md5 matches the build: $GOT"

say "Done — $TAG"
printf '  release : https://github.com/%s/releases/tag/%s\n' "$REPO_SLUG" "$TAG"
printf '  asset   : %s  (md5 %s)\n' "$KEEP" "$MD5"
printf '  manifest: https://raw.githubusercontent.com/%s/develop/manifest.json\n' "$REPO_SLUG"
