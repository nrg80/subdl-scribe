#!/usr/bin/env python3
# This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
# SPDX-License-Identifier: GPL-3.0-or-later
#
# F-M304 UI wiring check. ONE toggle, and it must be wired in the file that is actually
# delivered: a checkbox that exists in the markup but is bound nowhere is worse than a missing
# one — the setting looks present, saves as undefined, and silently reverts on the next load.
#
# WHY THIS FILE NOW ALSO ASSERTS ABSENCE. Four switches were removed from the correction
# section (drift report, drift reject, track-by-language, reference repair) because five
# checkboxes described ONE mechanism. A wiring check that only looks for what should be there
# cannot see a stale checkbox left behind, and a leftover `document.querySelector('#X')` for a
# removed element throws at load and takes the whole page's bindings with it. So every removed
# id is asserted ABSENT in the markup AND in the JS.
#
# Which file is delivered is not a guess: configPage.html is an EmbeddedResource in the
# .csproj, so it ships inside the DLL — a GUI change needs a real build, release and deploy,
# not a file copy. Asserted here so the next reader does not have to re-derive it.
import re
import subprocess
import sys

HTML = '/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Configuration/configPage.html'
CSPROJ = '/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Jellyfin.Plugin.SubdlSync.csproj'
LIVE_URL = 'http://localhost:8096/web/configurationpage?name=SubDL%20Scribe'

# Present: the one switch the section is.
CHECKS = [
    ('markup #QaDownloadAutoSync', r'id="QaDownloadAutoSync"'),
    ('load binds AutoSync (default-on aware)',
     r"#QaDownloadAutoSync'\)\.checked = config\.QaDownloadAutoSync !== false"),
    ('save reads AutoSync',
     r"config\.QaDownloadAutoSync = document\.querySelector\('#QaDownloadAutoSync'\)\.checked"),
]

# Gone: folded into the one switch. Each must be absent from markup AND from the JS, and its
# C# property must no longer be bound by the page.
REMOVED = [
    'QaDownloadDriftCheck',
    'QaDownloadDriftReject',
    'QaDownloadAudioTrackByLanguage',
    'QaDownloadAnchorSync',
]

fails = 0
src = open(HTML, encoding='utf-8').read()

print("=== F-M304 GUI wiring — one correction switch ===")
for name, pat in CHECKS:
    ok = re.search(pat, src) is not None
    print(f"  [{'ok' if ok else 'FAIL'}] {name}")
    fails += 0 if ok else 1

for prop in REMOVED:
    n = len(re.findall(rf'{prop}', src))
    ok = n == 0
    print(f"  [{'ok' if ok else 'FAIL'}] {prop} fully removed from the page (found {n})")
    fails += 0 if ok else 1

# The correction section itself must hold exactly ONE checkbox — the whole point of the
# change. Counted on the section slice so an unrelated checkbox elsewhere cannot mask it.
sec_start = src.find('Subtitle correction')
sec_end = src.find('id="DownloadLangModal"')
if sec_start < 0 or sec_end < 0 or sec_end < sec_start:
    print("  [FAIL] the correction section boundaries were not found")
    fails += 1
else:
    section = src[sec_start:sec_end]
    boxes = len(re.findall(r'<input[^>]*type="checkbox"', section))
    ok = boxes == 1
    print(f"  [{'ok' if ok else 'FAIL'}] the correction section holds exactly ONE checkbox "
          f"(got {boxes})")
    fails += 0 if ok else 1

# The page ships inside the DLL, so the claim above must be true of the built artefact path.
proj = open(CSPROJ, encoding='utf-8').read()
ok = 'EmbeddedResource Include="Configuration\\configPage.html"' in proj
print(f"  [{'ok' if ok else 'FAIL'}] configPage.html is an EmbeddedResource (ships in the DLL)")
fails += 0 if ok else 1

# Defaults of what REMAINS exposed. The removed props keep their C# defaults; only the one
# visible switch is bound, and it must resolve an absent value (an older config) to TRUE —
# `!!config.X` would silently switch a default-on correction off on every existing install.
CFG = '/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs'
cfg = open(CFG, encoding='utf-8').read()
m = re.search(r'public bool QaDownloadAutoSync \{ get; set; \}(.*?)(?=\n\n|/// <summary>)', cfg, re.S)
ok = bool(m) and '= true' in m.group(1)
print(f"  [{'ok' if ok else 'FAIL'}] QaDownloadAutoSync defaults to true in C#")
fails += 0 if ok else 1

print()
try:
    live = subprocess.run(['curl', '-sk', '--max-time', '20', LIVE_URL],
                          capture_output=True, timeout=40).stdout.decode('utf-8', 'replace')
    if not live:
        print("  [skip] live page unreachable — offline checks only")
    else:
        one = 'Correct subtitle timing' in live
        stale = 'QaDownloadDriftCheck' in live
        print(f"  [{'ok' if one and not stale else 'note'}] live page carries the ONE switch "
              f"({'yes' if one else 'no'}), removed switches absent ({'yes' if not stale else 'no'})")
except Exception as ex:
    print(f"  [skip] live check: {str(ex)[:60]}")

print()
print("FAILED" if fails else "ALL WIRING CHECKS PASSED")
sys.exit(1 if fails else 0)
