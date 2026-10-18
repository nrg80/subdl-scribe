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

"""Structural checks for the upload filename (F-M30 — T155).

Why this exists: the filename carried IMDB id, season/episode and language and nothing else, so a
normal and an SDH upload of the same episode were indistinguishable at SubDL, and the name could not
be tied back to the bytes that were sent. The operator order of 17.10.2026 ("die volle md5 macht
sinn") put the FULL MD5 of the normalized upload bytes into the name.

The name is built in one place now (`BuildNeutralName`) and used by the dry run AND the real upload.
Both used to build it inline, and two inline copies drift the moment one of them is edited — the dry
run then advertises a name the upload never sends, which is the failure this check is here for. So
the checks assert the helper, the ORDER of its placeholders, both call sites, that the hash sits in front of the
language, and that no hashless inline name survived anywhere.

Run: python3 scripts/tests/uploadname/check.py [path/to/UploadPipeline.cs]
Exit code 0 = all checks pass.
"""

import os
import re
import sys

DEFAULT = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "..",
    "Jellyfin.Plugin.SubdlSync", "Pipeline", "UploadPipeline.cs",
)

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT
    if not os.path.isfile(path):
        print("FAIL: source not found: %s" % path)
        return 2

    with open(path, encoding="utf-8") as fh:
        src = fh.read()

    # --- the helper -------------------------------------------------------------------------
    start = src.find("private static string BuildNeutralName(")
    check("the filename is built in one helper", start >= 0, "BuildNeutralName")
    if start < 0:
        return report()

    end = src.find("=>", start)
    body_end = src.find(";", src.find("srt\"", end))
    helper = src[start:body_end if body_end > end else end + 400]

    series = re.search(r'\$\"\{imdbId\}\.S\{season:00\}E\{episode:00\}[^"]*\"', helper)
    # The movie branch is matched structurally ("any non-episode name"), not by its expected text:
    # anchoring on the target string made the check report "no movie pattern" when the hash was
    # missing, which reads like a missing branch instead of a missing hash (measured on mutation B).
    movie = None
    for m in re.finditer(r'\$\"\{imdbId\}\.[^"]*\.srt\"', helper):
        if "S{season:00}" not in m.group(0):
            movie = m
            break
    check("the episode pattern names imdbId, season, episode", bool(series),
          series.group(0) if series else "no episode pattern")
    check("the movie pattern names imdbId and lang", bool(movie),
          movie.group(0) if movie else "no movie pattern")

    # The content hash must sit in the name of BOTH branches, behind the language.
    for label, m in (("episode", series), ("movie", movie)):
        pat = m.group(0) if m else ""
        check("the %s name carries the content hash" % label, "{contentHash}" in pat, pat)
        check("the %s name puts the hash in front of the language" % label,
              "{contentHash}.{lang}.srt" in pat, pat)

    # --- the two call sites -----------------------------------------------------------------
    # Both build the name through the helper. The arguments are matched up to the end of the
    # statement (");" at line end), because the dry run nests ComputeHash(cand.Srt) — a naive
    # [^)]+ stops at that inner parenthesis and then compares the wrong text (measured: it made
    # the baseline red twice).
    calls = re.findall(r"string neutralName = BuildNeutralName\(([^;]*?)\);", src)
    check("both the dry run and the real upload build the name", len(calls) == 2,
          "%d call site(s) — expected 2" % len(calls))
    real = [c for c in calls if "contentHashEarly" in c]
    dry = [c for c in calls if "ComputeHash(cand.Srt)" in c]
    check("the real upload feeds the content hash it hashed", len(real) == 1,
          real[0].strip() if real else "no call feeding contentHashEarly")
    check("the dry run feeds the hash of the candidate it advertises", len(dry) == 1,
          dry[0].strip() if dry else "no call feeding ComputeHash(cand.Srt)")
    check("no call site passes a guessed or literal hash",
          all("contentHashEarly" in c or "ComputeHash(cand.Srt)" in c for c in calls),
          "%d call(s) checked" % len(calls))

    # --- negative: no hashless inline name survived -----------------------------------------
    inline = re.findall(r'\$\"\{imdbId\}\.S\{season:00\}E\{episode:00\}[^"]*\.srt\"', src)
    check("no inline episode name outside the helper", len(inline) == 1,
          "%d occurrence(s) — expected exactly the one in BuildNeutralName" % len(inline))
    inline_movie = re.findall(r'\$\"\{imdbId\}\.\{lang\}(?:\.srt|\.\{cand\.Lang\})', src)
    check("no inline movie name outside the helper", len(inline_movie) <= 1,
          "%d occurrence(s)" % len(inline_movie))

    # --- negative: nothing identifying in the name ------------------------------------------
    ident = [t for t in ("Environment.MachineName", "installationId", "machineId", "Guid.NewGuid", "UserName")
             if t in helper]
    check("the name carries no installation, machine or user identifier", not ident,
          ", ".join(ident) if ident else "none — F-M29 stands")

    return report()


def report():
    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    for name, ok, detail in results:
        line = "  %-4s %s" % ("OK" if ok else "FAIL", name)
        if detail:
            line += " — %s" % re.sub(r"\s+", " ", detail)
        print(line)
    print()
    if passed == total:
        print(">>> UPLOADNAME OK: %d/%d Pruefungen bestanden" % (passed, total))
        return 0
    print(">>> UPLOADNAME FAIL: %d/%d — %d rot" % (passed, total, total - passed))
    return 1


if __name__ == "__main__":
    sys.exit(main())
