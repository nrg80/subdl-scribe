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

echo "=== 1. Zielordner vorbereiten ==="
rm -rf "$TGT"
mkdir -p "$TGT"
unzip -q "$ZIP" -d "$TGT"
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
python3 - "$TGT/meta.json" "$VER" "$TGT/logo.png" "$TIMESTAMP" "$CHANGELOG" <<'PY'
import json, sys
out, ver, logo, ts, changelog = sys.argv[1:6]
meta = {
    "category": "Subtitles",
    "changelog": json.loads(changelog),
    "description": "SubDL Scribe brings SubDL.com to Jellyfin: it downloads missing subtitles for the languages and libraries you pick and uploads your own. Download is on by default; upload is off — enable at your choice. Requires a SubDL login and API Key plus a TMDb API Key.",
    "guid": "7d1c5a2e-3f4b-4d6e-9a8b-5c2f8e1d4a9b",
    "name": "SubDL Scribe",
    "overview": "Sync subtitles with SubDL — download missing and upload new subtitles.",
    "owner": "nrg80",
    "targetAbi": "12.1.0.0",
    "timestamp": ts,
    "version": ver,
    "status": "Active",
    # Off: this is a test bench driven by hand, and autoUpdate would pull the published prerelease
    # over whatever was just deployed here.
    "autoUpdate": False,
    "imagePath": logo,
    "assemblies": [],
}
with open(out, "w", encoding="utf-8") as fh:
    json.dump(meta, fh, indent=2, ensure_ascii=False)
print("  version=" + meta["version"] + "  changelog=" + str(len(meta["changelog"])) + " chars")
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
