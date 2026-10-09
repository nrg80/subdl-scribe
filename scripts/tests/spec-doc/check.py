#!/usr/bin/env python3
# This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
# SPDX-License-Identifier: GPL-3.0-or-later
#
# SubDL Scribe is free software: you can redistribute it and/or modify it under
# the terms of the GNU General Public License as published by the Free Software
# Foundation, either version 3 of the License, or (at your option) any later
# version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the
# implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
# See the GNU General Public License for more details.

# F-M301: the spec is checked like the code, because nothing else does. Seven test suites
# guarded the plugin and NOT ONE touched docs/REQUIREMENTS.md, so its table of contents could
# disagree with its own body indefinitely without a single red check — and it did: three
# counters were wrong and nobody could tell by reading. This is the eighth suite.
#
# WHY THE COUNTERS DRIFT, AND WHY A HAND COUNT DOES NOT CATCH IT. The definition line has FOUR
# accepted shapes, and three of them hide the ID from a naive pattern:
#
#     **F-M37 [B1]:** ...              a tier marker between the ID and the colon
#     **F-M300 [D] (development):**   a tier marker AND a parenthetical
#     **T49 (superseded by F-M283):** a lifecycle marker instead of the colon
#     **F-M151a [B3] (superseded ...):**
#
# Matching `**ID:` therefore MISSES every marked entry, and its complement — "these IDs have no
# definition" — is a false alarm, not a finding. Measured on 06.10.2026: an audit done by hand
# reported T49/T57 as gaps and 14 duplicated definitions; both were artifacts of the pattern,
# while the three genuinely wrong counters stayed invisible. Hence: match the ID at the line
# start and require the bold span to end in a colon ANYWHERE on the line, and let this script
# do the counting.
#
# THE COUNTER CONVENTION IS "TOTAL, SUBSECTIONS INCLUDED" — settled here, because the document
# was previously split three ways. Section 3 states 32 and holds 32 counting 3.1 (8) and 3.2
# (2); section 9 states 25 and holds 25. 17 of 21 entries agree with "total", 1 with "live",
# 2 with neither. A section counter always means the number of definition lines in its block,
# sub-headings included, superseded entries included, so the counter stays a plain count and
# never encodes a policy about what is still in force.
#
# Run: python3 scripts/tests/spec-doc/check.py [path/to/REQUIREMENTS.md]
# Exit code 0 = all checks pass.

"""Structural checks for docs/REQUIREMENTS.md (the Lastenheft), no build needed.

What it asserts, and why each one has bitten or would bite:

  1. The TABLE OF CONTENTS agrees with the BODY, per section. It did not: section 4 said 24
     and held 26, 4.1 said 5 and held 6, 11 said 13 and held 14. A reader trusts the entry
     count to know the size of a chapter, and a stale count is the cheapest possible lie.
  2. Every requirement ID is DEFINED EXACTLY ONCE. The document is edited by inserting blocks
     anchored on headings; an anchor that lands in the wrong region duplicates a definition
     and the second copy silently overrides the first in a reader's head.
  3. The TEST NUMBERING IS GAPLESS. A hole means a requirement was added without its test, or
     a test was deleted leaving its requirement unproven. Measured 06.10.2026: zero gaps over
     T1..T115 — but only visible once the superseded form was matched.
  4. Every test REFERENCED by a requirement EXISTS as a definition. A reference to T99 that
     was never written reads exactly like a proven rule.
  5. The HEADER STATUS names the version that is actually built. Cheap, and it goes stale
     within days of every release.
  6. Every definition line is RECOGNISED. This is the guard on the guard: if a fourth shape
     appears that this script cannot see, the counts above silently shrink instead of
     failing. It asserts that no line looks like a definition (starts with a bolded ID) while
     failing to parse as one.

WHAT THIS DELIBERATELY DOES NOT CHECK: that every `F-Mnnn` marker in the CODE has a definition
here. 34 code markers have none and most are legitimate — the "Removed requirements (no longer
implemented)" line in `PluginConfiguration.cs` names eleven of them on purpose, and the rest are
historical cross-references in comments. A check that demands a definition for every code marker
would fail on correct code and be disabled within a week. The code-vs-doc comparison stays a
JUDGEMENT CALL with three piles (genuine gap / benign historical marker / stale constant inside a
live entry); it is done by hand during an audit, not asserted here.
"""

import os
import re
import sys

ID = r"F-M\d+[a-z]?\d*"
# A definition starts the line with a bolded ID. FOUR shapes occur, and three of them hide the
# ID from a pattern that expects "**ID:**" — see the header. The colon test is therefore applied
# to the whole bold span, and `**F-M191b — text.**` (the em-dash form) is accepted separately:
# its bold span closes the sentence instead of naming a rule field.
RE_DEF_LINE = re.compile(r"^\*\*(?P<id>" + ID + r")\b(?P<rest>[^\n]*?)(?::\*\*|\*\*|—[^\n]*\*\*)")
# An "ID-looking" line that is NOT a definition — the guard-on-the-guard set.
RE_ID_LINE = re.compile(r"^\*\*(?P<id>" + ID + r")\b")
RE_TEST_DEF = re.compile(r"^\*\*T(?P<t>\d+)\b(?P<rest>[^\n]*?):\*\*")
RE_TEST_REF = re.compile(r"\bT(?P<t>\d+)\b")
RE_TOC = re.compile(r"^\s*- \[(?P<num>\d+(?:\.\d+)*)[^\]]*\]\(#[^)]*\) — (?P<n>\d+) requirements?", re.M)
RE_HEADING = re.compile(r"^(?P<hashes>#{2,3}) (?P<num>\d+(?:\.\d+)*)\.? .+$", re.M)
RE_HEADING_LINE = re.compile(r"^(?P<hashes>#{2,3}) (?P<num>\d+(?:\.\d+)*)\.? .+$")
RE_STATUS = re.compile(r"^\*\*Status:\*\* .*?v(?P<ver>\d+\.\d+\.\d+\.\d+)", re.M)
RE_BUILDVER = re.compile(r'^version:\s*"?(?P<ver>\d+\.\d+\.\d+\.\d+)"?', re.M)

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))


def definitions(text):
    """Ordered definition lines: (line_index, id). Superseded entries ARE definitions."""
    out = []
    for i, line in enumerate(text.splitlines()):
        m = RE_DEF_LINE.match(line)
        if m:
            out.append((i, m.group("id")))
    return out


def blocks(text):
    """Section number -> (first line, last line+1), bounded by the next heading of <= level.

    LINE numbers, not character offsets: the definition list is keyed by line, and comparing a
    line number against a byte offset silently reports "holds 0" for every section — a green
    counter check that never looked at anything.
    """
    heads = []
    for i, line in enumerate(text.splitlines()):
        m = RE_HEADING_LINE.match(line)
        if m:
            heads.append((i, m.group("num"), len(m.group("hashes"))))
    heads.append((len(text.splitlines()), None, 0))
    out = {}
    for k, (ln, num, lvl) in enumerate(heads[:-1]):
        end = len(text.splitlines())
        for ln2, _, lvl2 in heads[k + 1:]:
            if lvl2 and lvl2 <= lvl:
                end = ln2
                break
        out.setdefault(num, (ln, end))
    return out


def repo_root(start):
    """Walk up until the repo root (the directory holding build.yaml) is found.

    Counted parent hops are what put this file at scripts/docs/REQUIREMENTS.md on the first run:
    the check sat one directory deeper than the suite it was copied from. Walking up to a MARKER
    cannot be off by one.
    """
    d = os.path.abspath(start)
    while d != os.path.dirname(d):
        if os.path.exists(os.path.join(d, "build.yaml")):
            return d
        d = os.path.dirname(d)
    return os.path.abspath(start)


def main():
    root = repo_root(os.path.dirname(os.path.abspath(__file__)))
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(root, "docs", "REQUIREMENTS.md")
    with open(path, encoding="utf-8") as fh:
        text = fh.read()

    defs = definitions(text)
    defs_by_id = {}
    for _, i in defs:
        defs_by_id[i] = defs_by_id.get(i, 0) + 1

    # ---- 1. Table of contents vs the body, per section ----
    secs = blocks(text)
    toc = list(RE_TOC.finditer(text))
    check("the table of contents lists at least one section", len(toc) > 0)
    mismatched = []
    for m in toc:
        num, stated = m.group("num"), int(m.group("n"))
        rng = secs.get(num)
        if rng is None:
            mismatched.append("%s: no section body" % num)
            continue
        actual = sum(1 for ln, _ in defs if rng[0] <= ln < rng[1])
        if actual != stated:
            mismatched.append("%s: says %d, holds %d" % (num, stated, actual))
    check("every contents counter matches the definitions in its section",
          not mismatched, "; ".join(mismatched))

    # ---- 2. One definition per requirement id ----
    dupes = sorted(k for k, v in defs_by_id.items() if v > 1)
    check("every requirement id is defined exactly once", not dupes,
          "duplicated: %s" % ", ".join(dupes))

    # ---- 3. Test numbering is gapless ----
    test_defs = []
    for i, line in enumerate(text.splitlines()):
        m = RE_TEST_DEF.match(line)
        if m:
            test_defs.append((i, int(m.group("t"))))
    nums = sorted(t for _, t in test_defs)
    gaps = [n for n in range(1, (max(nums) + 1) if nums else 1) if n not in nums]
    check("the test numbering is gapless from T1", not gaps,
          "missing: %s" % ", ".join("T%d" % g for g in gaps))

    # ---- 4. Every referenced test exists ----
    defined_t = set(nums)
    referenced = set()
    # Only look INSIDE a definition's body, so a "T-number" in prose (a version, a byte count)
    # cannot manufacture a reference.
    bounds = [i for i, _ in defs] + [len(text.splitlines())]
    lines = text.splitlines()
    for k, (i, _) in enumerate(defs):
        body = "\n".join(lines[i:bounds[k + 1]])
        referenced.update(int(t) for t in RE_TEST_REF.findall(body))
    dangling = sorted(referenced - defined_t)
    check("every test named by a requirement is defined", not dangling,
          "referenced but never defined: %s" % ", ".join("T%d" % d for d in dangling))

    # ---- 5. Header status names the built version ----
    st = RE_STATUS.search(text)
    check("the header status names a version", st is not None)
    build = os.path.join(root, "build.yaml")
    if st and os.path.exists(build):
        with open(build, encoding="utf-8") as fh:
            bv = RE_BUILDVER.search(fh.read())
        check("the header status matches build.yaml", bv is not None and bv.group("ver") == st.group("ver"),
              "doc says %s, build.yaml says %s" % (
                  st.group("ver"), bv.group("ver") if bv else "?"))

    # ---- 6. Guard on the guard: no unparsed definition-looking line ----
    parsed = {i for i, _ in defs}
    unparsed = []
    for i, line in enumerate(lines):
        if i in parsed:
            continue
        m = RE_ID_LINE.match(line)
        # A line that opens with a bolded ID but is not a definition: allowed ONLY when the bold
        # span never closes with a colon (a cross-reference such as "**F-M17a fix**" in prose).
        if m and not re.match(r"^\*\*" + re.escape(m.group("id")) + r"\b[^\n]*:\*\*", line):
            unparsed.append((i + 1, m.group("id")))
    check("no definition-shaped line escaped the pattern (the guard on the guard)",
          not unparsed, "%d unrecognised: %s" % (
              len(unparsed), ", ".join("L%d %s" % (l, i) for l, i in unparsed[:5])))

    failed = [r for r in results if not r[1]]
    for name, ok, detail in results:
        line = "  %s %s" % ("OK  " if ok else "FAIL", name)
        if not ok and detail:
            line += "  (%s)" % detail
        print(line)
    print()
    print("   %d requirement definitions, %d tests (max T%d)"
          % (len(defs), len(nums), max(nums) if nums else 0))
    print(">>> SPEC-DOC %s: %d/%d Pruefungen bestanden"
          % ("OK" if not failed else "FEHLER", len(results) - len(failed), len(results)))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
