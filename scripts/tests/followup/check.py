#!/usr/bin/env python3
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

"""Structural checks for the arrival follow-up (F-M177, F-M1a — T154).

Why this exists: an arrival that lands inside a running cycle is worked by the follow-up
reseed, and that reseed was gated on the direction of the cycle that happened to be running
(`dir != CycleDirection.DownloadOnly`). An arrival during a scheduled download run therefore
produced no upload work at all — its upload waited for the next anchor, while an arrival that
started its own cycle got both directions. The rule in F-M177 and F-M1a is that the follow-up
SETTINGS decide which directions take part; the direction of the running cycle decides nothing.

The defect was invisible in the log (the follow-up looked complete) and no test named either
requirement, so it lived in the tree for weeks. These checks assert the rule as an ORDER plus a
NEGATIVE: the two flags derive from the two switches and from nothing else, both legs are
reachable in the body, download runs before upload, and neither switch off means no reseed.

Run: python3 scripts/tests/followup/check.py [path/to/SubdlEventDispatcher.cs]
Exit code 0 = all checks pass.
"""

import os
import re
import sys

DEFAULT = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "..",
    "Jellyfin.Plugin.SubdlSync", "ScheduledTasks", "SubdlEventDispatcher.cs",
)

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))


def method_body(src, signature, next_member_marker):
    """Text from `signature` up to the next member, so a check can look INSIDE one method."""
    start = src.find(signature)
    if start < 0:
        return None
    end = src.find(next_member_marker, start)
    if end < 0:
        end = len(src)
    return src[start:end]


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT
    if not os.path.isfile(path):
        print("FAIL: source not found: %s" % path)
        return 2

    with open(path, encoding="utf-8") as fh:
        src = fh.read()

    body = method_body(
        src,
        "private async Task RunFollowUpRoundsAsync(",
        "/// Runs one direction against its queue",
    )
    check("the follow-up method exists", body is not None, path)
    if body is None:
        return report()

    # The two flags: derived from the switches, and from nothing else.
    down = re.search(r"bool followUpDown\s*=\s*([^;]+);", body)
    up = re.search(r"bool followUpUp\s*=\s*([^;]+);", body)
    check("followUpDown is derived from the download switch",
          bool(down) and "FollowUpRoundsDownload" in down.group(1),
          down.group(1).strip() if down else "no assignment found")
    check("followUpUp is derived from the upload switch",
          bool(up) and "FollowUpRoundsUpload" in up.group(1),
          up.group(1).strip() if up else "no assignment found")

    # NEGATIVE: the direction of the running cycle may not gate either flag. This is the exact
    # term that was removed on 16.10.2026, and the reason this check exists.
    for name, rx in (("followUpDown", down), ("followUpUp", up)):
        expr = rx.group(1) if rx else ""
        check("%s is not gated by the cycle direction" % name,
              "dir !=" not in expr and "CycleDirection" not in expr,
              re.sub(r"\s+", " ", expr.strip()))
    check("the follow-up body names no cycle direction at all",
          "CycleDirection.DownloadOnly" not in body and "CycleDirection.UploadOnly" not in body,
          "the running cycle's direction is an argument, not a gate (F-M177)")

    # Both legs reachable, and download runs before upload (the F-M177 order).
    dl = body.find("if (followUpDown && config.DownloadEnabled)")
    ul = body.find("if (followUpUp && config.UploadEnabled)")
    check("the download leg is guarded by followUpDown", dl >= 0, "if (followUpDown && config.DownloadEnabled)")
    check("the upload leg is guarded by followUpUp", ul >= 0, "if (followUpUp && config.UploadEnabled)")
    check("download runs before upload", dl >= 0 and ul >= 0 and dl < ul,
          "download at %d, upload at %d" % (dl, ul))

    # Each leg really seeds/uploads, rather than the flag being decorative.
    for name, at in (("download", dl), ("upload", ul)):
        if at < 0:
            continue
        window = body[at:at + 400]
        check("the %s leg runs its direction" % name,
              ("upload: false" in window) if name == "download" else ("upload: true" in window),
              re.sub(r"\s+", " ", window)[:110])

    # Both switches off = no reseed at all.
    check("both switches off means no reseed",
          re.search(r"if \(!followUpDown && !followUpUp\)\s*\{\s*return;", body) is not None,
          "early return kept")

    return report()


def report():
    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    for name, ok, detail in results:
        line = "  %-4s %s" % ("OK" if ok else "FAIL", name)
        if detail:
            line += " — %s" % detail
        print(line)
    print()
    if passed == total:
        print(">>> FOLLOW-UP OK: %d/%d Pruefungen bestanden" % (passed, total))
        return 0
    print(">>> FOLLOW-UP FAIL: %d/%d — %d rot" % (passed, total, total - passed))
    return 1


if __name__ == "__main__":
    sys.exit(main())
