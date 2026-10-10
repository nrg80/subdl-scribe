#!/bin/bash
# Deploy a built SubDL Scribe version into the test Jellyfin instance — SAFELY.
#
# Order is mandatory and inherited from deploy-safe.sh: stop -> wait until really gone -> swap ->
# start. Replacing the DLL while Jellyfin runs leaves the process with a file that no longer matches
# the assembly it loaded; .NET compiles methods lazily, so a method first reached during SHUTDOWN is
# read off disk at that moment and the runtime dies with BadImageFormatException: Bad IL range.
#
# Usage: deploy-jf-test.sh <version> <zip>
set -u

VER="${1:?usage: deploy-jf-test.sh <version> <zip>}"
ZIP="${2:?usage: deploy-jf-test.sh <version> <zip>}"

ROOT=/opt/data/subdl-scribe
PLUGINS=/opt/data/jf-test/data/plugins
TGT="$PLUGINS/SubDL Scribe_$VER"
PATTERN='jellyfin.*--datadir /opt/data/jf-test/data'

[ -f "$ZIP" ] || { echo "ABORT: zip not found: $ZIP"; exit 1; }

# The package is verified BEFORE anything is removed. The target folder is wiped below, so a ZIP
# without the plugin assembly used to leave an install holding nothing but the logo: the deploy
# reported success and Jellyfin answered 503 (measured 08.10.2026 — `unzip` was missing from the
# PATH, the extraction failed silently, and the old version had already been deleted).
python3 -c 'import sys,zipfile; sys.exit(0 if "Jellyfin.Plugin.SubdlSync.dll" in zipfile.ZipFile(sys.argv[1]).namelist() else 1)' "$ZIP" \
    || { echo "  ABORT: package carries no Jellyfin.Plugin.SubdlSync.dll — nothing was touched."; exit 1; }

echo "=== 1. Zielordner vorbereiten ==="
rm -rf "$TGT"
mkdir -p "$TGT"
# unzip is not on this host's PATH and the script must not abort halfway on that: the target folder
# has already been wiped by then, so a missing extractor leaves an install holding nothing but the
# logo — which is how a deploy "succeeds" and Jellyfin answers 503 (measured 08.10.2026). Python's
# zipfile is always present; fall back to it, and abort if nothing could extract.
if command -v unzip >/dev/null 2>&1; then
    unzip -q "$ZIP" -d "$TGT"
else
    python3 -c 'import sys,zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$ZIP" "$TGT"
fi
[ -f "$TGT/Jellyfin.Plugin.SubdlSync.dll" ] || { echo "  ABORT: extract failed — no DLL in $TGT"; exit 1; }
[ -f "$ROOT/Jellyfin.Plugin.SubdlSync/logo.png" ] && cp "$ROOT/Jellyfin.Plugin.SubdlSync/logo.png" "$TGT/logo.png"
echo "  entpackt: $(ls "$TGT" | tr '\n' ' ')"

echo
echo "=== 2. Jellyfin stoppen und auf das ENDE warten ==="
PID=$(pgrep -f "$PATTERN" | head -1)
if [ -n "$PID" ]; then
    echo "  PID $PID -> SIGTERM"
    kill "$PID" 2>/dev/null
    for _ in $(seq 1 40); do
        sleep 1
        pgrep -f "$PATTERN" >/dev/null || break
    done
    if pgrep -f "$PATTERN" >/dev/null; then
        echo "  reagiert nicht -> SIGKILL"
        pkill -9 -f "$PATTERN"
        sleep 3
    fi
fi
if pgrep -f "$PATTERN" >/dev/null; then
    echo "  ABORT: Prozess laeuft noch — nichts getauscht."
    exit 1
fi
echo "  gestoppt, kein Prozess mehr aktiv"

echo
echo "=== 3. Alte Installationen entfernen (gleiche GUID — sonst doppelt geladen) ==="
for d in "$PLUGINS"/"SubDL Scribe"_*; do
    [ -d "$d" ] || continue
    if [ "$d" != "$TGT" ]; then
        echo "  entferne: $(basename "$d")"
        rm -rf "$d"
    fi
done

echo
echo "=== 4. meta.json schreiben ==="
CHANGELOG=$(python3 "$ROOT/scripts/changelog_entry.py" "$ROOT/build.yaml" "$VER") || exit 1
TIMESTAMP=$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)

# The three catalogue fields that CHANGE over time are read from build.yaml instead of being
# written out here. Measured 10.10.2026: this block carried a literal copy of the description, so
# the bench kept showing the old 258-char text after build.yaml had moved on — and the day before,
# Jellyfin's own installer was measured taking the installed description from the package's
# build.yaml (not from the manifest entry, which already carried a longer text): a literal here is
# therefore a SECOND source for a field that has exactly one elsewhere. Same for the overview and
# targetAbi, which move with the Jellyfin ABI.
FIELDS=$(python3 - "$ROOT/build.yaml" <<'PY'
import json, re, sys
text = open(sys.argv[1], encoding='utf-8').read()
desc = re.search(r'^description: >\n((?:[ \t].*\n|\n)*)', text, re.M)
if not desc:
    sys.exit('no description block in build.yaml')
out = {
    'description': ' '.join(l.strip() for l in desc.group(1).split('\n') if l.strip()),
}
for key in ('overview', 'targetAbi'):
    m = re.search(r'^%s:\s*"([^"]*)"' % key, text, re.M)
    if not m:
        sys.exit('no %s in build.yaml' % key)
    out[key] = m.group(1)
print(json.dumps(out))
PY
) || exit 1

python3 - "$TGT/meta.json" "$VER" "$TGT/logo.png" "$TIMESTAMP" "$CHANGELOG" "$FIELDS" <<'PY'
import json, sys
out, ver, logo, ts, changelog, fields = sys.argv[1:7]
catalogue = json.loads(fields)
meta = {
    "category": "Subtitles",
    "changelog": json.loads(changelog),
    "description": catalogue["description"],
    "guid": "7d1c5a2e-3f4b-4d6e-9a8b-5c2f8e1d4a9b",
    "name": "SubDL Scribe",
    "overview": catalogue["overview"],
    "owner": "nrg80",
    "targetAbi": catalogue["targetAbi"],
    "timestamp": ts,
    "version": ver,
    "status": "Active",
    # Off: this is a test bench driven by hand, and autoUpdate would pull the published prerelease
    # over whatever was just deployed here. (Jellyfin's own installer sets it True — measured
    # 10.10.2026 when the package API was used on this bench, and it had to be put back by hand.)
    "autoUpdate": False,
    "imagePath": logo,
    "assemblies": [],
}
with open(out, "w", encoding="utf-8") as fh:
    json.dump(meta, fh, indent=2, ensure_ascii=False)
print("  version=" + meta["version"] + "  changelog=" + str(len(meta["changelog"])) + " chars"
      + "  description=" + str(len(meta["description"])) + " chars")
PY

echo
echo "=== 5. Starten ==="
/opt/data/jf-test/start-test-jf.sh
for i in $(seq 1 80); do
    sleep 3
    c=$(curl -s -m 5 -o /dev/null -w '%{http_code}' http://127.0.0.1:8096/System/Info/Public 2>/dev/null)
    [ "$c" = "200" ] && { echo "  bereit nach ~$((i*3))s"; break; }
done
echo "  HTTP $(curl -s -m 5 -o /dev/null -w '%{http_code}' http://127.0.0.1:8096/System/Info/Public)"
