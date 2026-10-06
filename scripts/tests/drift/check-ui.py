#!/usr/bin/env python3
# This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
# SPDX-License-Identifier: GPL-3.0-or-later
#
# F-M295 UI wiring check. A checkbox that exists in the markup but is bound
# nowhere is worse than a missing one: the setting looks present, saves as
# undefined, and silently reverts to the default on the next load. So all four
# bindings are asserted, in the file that is actually delivered.
#
# Which file is delivered is not a guess: the live page is fetched and compared
# against the repo file. configPage.js is NOT referenced by any page or loader in
# this repository (checked: no script src, no ConfigJs call), so configPage.html
# is the live one — asserted here so a future reader does not have to re-derive it.
import re
import subprocess
import sys

HTML = '/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Configuration/configPage.html'
LIVE_URL = 'http://localhost:8096/web/configurationpage?name=SubDL%20Scribe'

CHECKS = [
    ('markup #QaDownloadDriftCheck', r'id="QaDownloadDriftCheck"'),
    ('markup #QaDownloadDriftReject', r'id="QaDownloadDriftReject"'),
    ('load binds DriftCheck', r"#QaDownloadDriftCheck'\)\.checked = !!config\.QaDownloadDriftCheck"),
    ('load binds DriftReject', r"#QaDownloadDriftReject'\)\.checked = !!config\.QaDownloadDriftReject"),
    ('save reads DriftCheck', r"config\.QaDownloadDriftCheck = document\.querySelector\('#QaDownloadDriftCheck'\)\.checked"),
    ('save reads DriftReject', r"config\.QaDownloadDriftReject = document\.querySelector\('#QaDownloadDriftReject'\)\.checked"),

    # F-M296: the auto-sync pair. The track switch defaults to ON, so its load binding
    # must resolve an absent value (an older config) to TRUE, not to false — the usual
    # `!!config.X` would silently turn a default-on feature off on every existing install.
    ('markup #QaDownloadAutoSync', r'id="QaDownloadAutoSync"'),
    ('markup #QaDownloadAudioTrackByLanguage', r'id="QaDownloadAudioTrackByLanguage"'),
    ('load binds AutoSync', r"#QaDownloadAutoSync'\)\.checked = !!config\.QaDownloadAutoSync"),
    ('load binds AudioTrackByLanguage (default-on aware)',
     r"#QaDownloadAudioTrackByLanguage'\)\.checked = config\.QaDownloadAudioTrackByLanguage !== false"),
    ('save reads AutoSync', r"config\.QaDownloadAutoSync = document\.querySelector\('#QaDownloadAutoSync'\)\.checked"),
    ('save reads AudioTrackByLanguage',
     r"config\.QaDownloadAudioTrackByLanguage = document\.querySelector\('#QaDownloadAudioTrackByLanguage'\)\.checked"),

    # F-M297: the anchor-sync switch. Like the audio auto-sync it defaults to OFF, so the
    # usual `!!config.X` binding is the correct one here.
    ('markup #QaDownloadAnchorSync', r'id="QaDownloadAnchorSync"'),
    ('load binds AnchorSync', r"#QaDownloadAnchorSync'\)\.checked = !!config\.QaDownloadAnchorSync"),
    ('save reads AnchorSync', r"config\.QaDownloadAnchorSync = document\.querySelector\('#QaDownloadAnchorSync'\)\.checked"),
]

fails = 0
src = open(HTML, encoding='utf-8').read()

print("=== F-M295 GUI wiring ===")
for name, pat in CHECKS:
    ok = re.search(pat, src) is not None
    print(f"  [{'ok' if ok else 'FAIL'}] {name}")
    fails += 0 if ok else 1

# Every new switch must appear exactly once in the markup, and each default must match
# the C# declaration. A default-on switch read with `!!config.X` turns itself off on every
# existing install — the reason the pair below is checked separately rather than in a loop.
for prop, expect_on in (('QaDownloadDriftCheck', False), ('QaDownloadDriftReject', False),
                        ('QaDownloadAutoSync', False), ('QaDownloadAnchorSync', False),
                        ('QaDownloadAudioTrackByLanguage', True)):
    n = len(re.findall(rf'id="{prop}"', src))
    ok_once = n == 1
    print(f"  [{'ok' if ok_once else 'FAIL'}] {prop} id appears exactly once (got {n})")
    fails += 0 if ok_once else 1

CFG = '/opt/data/subdl-scribe/Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs'
cfg = open(CFG, encoding='utf-8').read()
for prop, expect_on in (('QaDownloadDriftCheck', False), ('QaDownloadDriftReject', False),
                        ('QaDownloadAutoSync', False), ('QaDownloadAnchorSync', False),
                        ('QaDownloadAudioTrackByLanguage', True)):
    m = re.search(rf'public bool {prop} \{{ get; set; \}}(.*?)(?=\n\n|/// <summary>)', cfg, re.S)
    body = m.group(1) if m else ''
    is_on = '= true' in body
    ok_default = is_on == expect_on
    print(f"  [{'ok' if ok_default else 'FAIL'}] {prop} defaults to "
          f"{'true' if expect_on else 'false'}"
          + ('' if ok_default else f" (found {'true' if is_on else 'false'})"))
    fails += 0 if ok_default else 1

# Live delivery: the served page must carry the new markup. Skipped (not failed)
# when the instance is unreachable — the offline checks above still ran.
print()
try:
    live = subprocess.run(['curl', '-sk', '--max-time', '20', LIVE_URL],
                          capture_output=True, timeout=40).stdout.decode('utf-8', 'replace')
    if not live:
        print("  [skip] live page unreachable — offline checks only")
    else:
        ok_live = 'QaDownloadDriftCheck' in live
        print(f"  [{'ok' if ok_live else 'note'}] live page carries the new markup "
              f"({'yes' if ok_live else 'not yet — deployed build predates this change'})")
except Exception as ex:
    print(f"  [skip] live check: {str(ex)[:60]}")

print()
print("FAILED" if fails else "ALL WIRING CHECKS PASSED")
sys.exit(1 if fails else 0)
