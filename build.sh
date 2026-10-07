#!/usr/bin/env bash
# This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
# SPDX-License-Identifier: GPL-3.0-or-later
#
# SubDL Scribe is free software: you can redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the
# Free Software Foundation, either version 3 of the License, or (at your
# option) any later version.
# SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
# warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
# See the GNU General Public License for more details.

# Build + package SubDL Scribe Jellyfin plugin
# Usage: ./build.sh [version]
# Without version, reads version from build.yaml

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
    VERSION="$(grep '^version:' build.yaml | sed 's/.*"\(.*\)".*/\1/')"
fi

echo "Building SubDL Scribe v$VERSION ..."

# Build
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
DOTNET_CLI_TELEMETRY_OPTOUT=1 \
/opt/data/.dotnet/dotnet publish \
  Jellyfin.Plugin.SubdlSync/Jellyfin.Plugin.SubdlSync.csproj \
  -c Release -o out \
  /p:GenerateDocumentationFile=false --nologo

# Verify required artifacts exist
ARTIFACTS=(
    "out/Jellyfin.Plugin.SubdlSync.dll"
    "out/LanguageDetection.dll"
    "out/LiteDB.dll"
    "build.yaml"
)

for f in "${ARTIFACTS[@]}"; do
    if [[ ! -f "$f" ]]; then
        echo "ERROR: required artifact missing: $f" >&2
        exit 1
    fi
done

# Package — store only basenames inside the zip to avoid path traversal
ZIP="out/Jellyfin.Plugin.SubdlSync_${VERSION}.zip"
rm -f "$ZIP"

python3 -c "
import zipfile, os
files = [
    'out/Jellyfin.Plugin.SubdlSync.dll',
    'out/LanguageDetection.dll',
    'out/LiteDB.dll',
    'build.yaml'
]
# Licence texts: Apache-2.0 requires the licence and notice to travel with the
# binary when it is redistributed, and this ZIP is what gets published.
licences = [
    'licenses/THIRD-PARTY.txt',
    'licenses/LanguageDetection-Apache-2.0.txt',
    'licenses/LiteDB-MIT.txt',
    'licenses/SubDL-Scribe-GPL-3.0.txt',
]
with zipfile.ZipFile('$ZIP', 'w', zipfile.ZIP_DEFLATED) as z:
    for f in files:
        z.write(f, os.path.basename(f))
    for f in licences:
        z.write(f, f)
"

echo "Built $ZIP"
ls -lh "$ZIP"

echo "Zip contents:"
python3 -c "import zipfile; [print(f'  {n}') for n in zipfile.ZipFile('$ZIP').namelist()]"
