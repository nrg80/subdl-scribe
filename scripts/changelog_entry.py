#!/usr/bin/env python3
"""Read the changelog entry for one version out of build.yaml.

Used by the deploy script, and the same extraction release.sh does — kept in one place so the
deployed meta.json and the published manifest cannot disagree.
"""
import json
import re
import sys

if len(sys.argv) != 3:
    sys.exit("usage: changelog_entry.py <build.yaml> <version>")

path, ver = sys.argv[1], sys.argv[2]
text = open(path, encoding="utf-8").read()
m = re.search(r'^changelog: >\n((?:[ \t].*\n)*)', text, re.M)
if not m:
    sys.exit("no changelog block in " + path)

lines, keep, on = m.group(1).split("\n"), [], False
for line in lines:
    if line.strip().startswith("v" + ver + ":"):
        on = True
    elif re.match(r"^\s{2}v\d", line):
        on = False
    if on:
        keep.append(line.strip())

out = "\n".join(keep).strip()
if not out:
    sys.exit(f"no changelog entry for v{ver} in {path}")

# JSON so the shell can drop it straight into meta.json without quoting games.
print(json.dumps(out))
