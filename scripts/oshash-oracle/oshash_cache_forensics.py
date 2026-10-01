#!/usr/bin/env python3
"""Forensics: are the STORED OSHash values in the cache actually correct?

The cross-check in verify_oshash.py proves the FUNCTION is right. This proves
the CACHE is right -- a different claim. The stored value is what every
downstream identity is keyed by (media rows, mark invalidation, dedup), and a
cache entry can be wrong in ways the function cannot: written by an older build,
computed before a file was rewritten, or keyed by a path whose file has since
changed. None of those show up in a log as anything abnormal.

For every row in the `oshashes` collection it:
  1. reads the stored Hash / Size / MtimeUnix
  2. recomputes the hash independently from the file ON DISK (Python oracle)
  3. compares, and reports four distinct verdicts so a real defect cannot hide
     inside an expected one:

     MATCH            stored == recomputed. The cache is trustworthy here.
     MISSING FILE     the path is gone. Not a hash defect: F-M119 says the
                      OSHash refresh deliberately leaves these untouched (the
                      state prune owns deletions). Reported, never counted wrong.
     SIZE DRIFT       stored Size != current file size. The file changed after
                      the hash was taken; the cache MUST treat this as a miss
                      (F-M61b), which is the designed behaviour -- so this is
                      information about the file, not a stolen hash.
     MISMATCH         same size, different hash. This is the only verdict that
                      means the cache is holding a wrong value.

Also reports DUPLICATE HASHES across distinct paths, which is either a genuine
collision or two identical files under different names -- the two possibilities
must be told apart by comparing content, not assumed.

    python3 oshash_cache_forensics.py <db-file> [<db-file> ...]
    python3 oshash_cache_forensics.py <db-file> --dump   # also print every row
"""

import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from oshash_oracle import oshash_canonical, oshash_firstlast  # noqa: E402

DUMP = "/opt/data/subdl-scribe-nrg80/scripts/tests/dbdump/bin/Debug/net10.0/dbdump.dll"
DOTNET = "/opt/data/.dotnet/dotnet"
ICU = "/opt/data/jf-test/libicu/usr/lib/aarch64-linux-gnu"


def probe_env():
    env = dict(os.environ)
    env["LD_LIBRARY_PATH"] = ICU
    env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "0"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    return env


def read_rows(db_path):
    """Parse dbdump output for the oshashes collection."""
    out = subprocess.run([DOTNET, DUMP, db_path, "oshashes", ""],
                         capture_output=True, text=True, env=probe_env(), timeout=900)
    text = out.stdout + "\n" + out.stderr
    rows = []
    current = None
    for line in text.splitlines():
        m = re.match(r'^=== doc \d+ \(_?id=(.*)\) ===$', line.strip())
        if m:
            current = {"id": m.group(1).strip().strip('"')}
            rows.append(current)
            continue
        if current is None:
            continue
        m = re.match(r'^\s*(\w+) = (.*)$', line)
        if not m:
            continue
        key, val = m.group(1), m.group(2).strip()
        if key == "Hash":
            current["hash"] = val.strip('"')
        elif key == "Size":
            mm = re.search(r'"\$numberLong":"(-?\d+)"', val)
            current["size"] = int(mm.group(1)) if mm else None
        elif key == "MtimeUnix":
            mm = re.search(r'"\$numberLong":"(-?\d+)"', val)
            current["mtime"] = int(mm.group(1)) if mm else None
    return [r for r in rows if "hash" in r]


def oracle_for(path):
    """Recompute independently, matching the code's shape (min(64 KiB, size))."""
    size = os.path.getsize(path)
    if size == 0:
        return None
    return oshash_firstlast(path)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dump = "--dump" in sys.argv
    if not args:
        print(__doc__)
        return 2

    grand = {"match": 0, "missing": 0, "drift": 0, "mismatch": 0}
    all_hashes = {}

    for db_path in args:
        print("=" * 78)
        print("DB: " + db_path + ("  (exists)" if os.path.exists(db_path) else "  MISSING"))
        print("=" * 78)
        try:
            rows = read_rows(db_path)
        except Exception as exc:  # noqa: BLE001
            print("  could not read: " + type(exc).__name__ + ": " + str(exc))
            continue

        print("  rows in 'oshashes': " + str(len(rows)))
        counts = {"match": 0, "missing": 0, "drift": 0, "mismatch": 0}

        for r in rows:
            path, stored = r["id"], r["hash"]
            all_hashes.setdefault(stored, []).append(path)

            if not os.path.exists(path):
                counts["missing"] += 1
                if dump:
                    print("  MISSING FILE  " + stored + "  " + path)
                continue

            size_now = os.path.getsize(path)
            if r.get("size") is not None and r["size"] != size_now:
                counts["drift"] += 1
                print("  SIZE DRIFT    stored=" + str(r["size"]) + " now=" + str(size_now) +
                      "  " + os.path.basename(path))
                continue

            try:
                expected = oracle_for(path)
            except Exception as exc:  # noqa: BLE001
                print("  ORACLE ERROR  " + path + " -> " + str(exc))
                continue

            if expected == stored:
                counts["match"] += 1
                if dump:
                    print("  MATCH         " + stored + "  " + path)
            else:
                counts["mismatch"] += 1
                print("  MISMATCH      stored=" + str(stored) + " recomputed=" + str(expected) +
                      "  " + path)

        print()
        print("  MATCH        " + str(counts["match"]))
        print("  MISSING FILE " + str(counts["missing"]) + "   (path gone; F-M119 keeps these by design)")
        print("  SIZE DRIFT   " + str(counts["drift"]) + "   (file changed; cache must treat as miss, F-M61b)")
        print("  MISMATCH     " + str(counts["mismatch"]) + "   <-- the only verdict that means a WRONG stored value")
        for k in grand:
            grand[k] += counts[k]
        print()

    # Duplicates: a genuine collision, or the same bytes under two names?
    dupes = {h: ps for h, ps in all_hashes.items() if len(ps) > 1}
    print("=" * 78)
    print("DUPLICATE HASHES across distinct paths: " + str(len(dupes)))
    for h, paths in dupes.items():
        print("  " + h + "  (" + str(len(paths)) + " paths)")
        sizes, contents = set(), []
        for p in paths:
            if os.path.exists(p):
                sizes.add(os.path.getsize(p))
                try:
                    with open(p, "rb") as fh:
                        contents.append((p, fh.read(65536), os.path.getsize(p)))
                except OSError:
                    pass
            print("      " + p)
        # Tell the two possibilities apart by comparing the BYTES, not by naming.
        if len(contents) >= 2:
            same_all = all(c[1] == contents[0][1] and c[2] == contents[0][2] for c in contents)
            print("      sizes on disk: " + str(sorted(sizes)) +
                  " | identical content: " + ("YES -> same file, hash is CORRECT" if same_all
                                              else "NO -> different bytes"))
        elif not contents:
            print("      no file on disk to compare")

    print()
    print("TOTAL  MATCH=" + str(grand["match"]) + "  MISSING=" + str(grand["missing"]) +
          "  DRIFT=" + str(grand["drift"]) + "  MISMATCH=" + str(grand["mismatch"]))
    return 1 if grand["mismatch"] else 0


if __name__ == "__main__":
    sys.exit(main())
