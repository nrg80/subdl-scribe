#!/usr/bin/env python3
"""Cross-check the shipped plugin's OSHash against an INDEPENDENT oracle.

Why this exists: the repo's own suite asserts only `GetMediaHash(...).Length > 0`
-- it proves a hash APPEARS, never that the VALUE is the published OpenSubtitles
hash. Every downstream identity in the plugin (media rows, mark invalidation,
seeder dedup, the tag-rewrite identity move) is keyed by that value, so a wrong
hash would break them all silently, with nothing in any log looking abnormal.

This script is the missing assertion.

Which variant SHOULD the plugin match? Read off the shipped code, not guessed.
`ContentHashRegistry.ComputeMediaHash` does:

    chunk = min(65_536, size);  hash = size
    read the first `chunk` bytes, sum 8-byte LE words
    seek(-chunk, End); read `chunk` bytes, sum again

so for every non-empty file the contract is exactly `firstlast` (windows are
min(64 KiB, size) wide; they overlap below 128 KiB, and coincide below 64 KiB,
where the same bytes get summed twice). Canonical agrees with firstlast for
size >= 128 KiB and REFUSES below -- that boundary is reported separately,
because it is the one place a "correct-looking" hash could still be a
different hash from what other OpenSubtitles clients compute.

    python3 verify_oshash.py            # fixtures + real media
    python3 verify_oshash.py --quick    # fixtures only

Exit 0 = every comparison matched.
"""

import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from oshash_oracle import oshash_canonical, oshash_firstlast  # noqa: E402

PROBE = os.environ.get(
    "OSHASH_PROBE",
    "/opt/data/tmp/oshash-harness/bin/Debug/net10.0/oshashprobe.dll")
DOTNET = "/opt/data/.dotnet/dotnet"
ICU = "/opt/data/jf-test/libicu/usr/lib/aarch64-linux-gnu"
REAL_DIR = "/data/movies2"
FIXED = os.path.join(tempfile.gettempdir(), "oshash-verify")
CANONICAL_MIN = 131072  # below this the published canonical algorithm refuses


def probe_env():
    env = dict(os.environ)
    env["LD_LIBRARY_PATH"] = ICU
    env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "0"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    return env


def run_probe(paths):
    """One probe invocation -> (direct, cache, assembly) keyed by path."""
    out = subprocess.run([DOTNET, PROBE] + list(paths),
                         capture_output=True, text=True, env=probe_env(), timeout=900)
    text = out.stdout + "\n" + out.stderr
    direct, cache, assembly = {}, {}, "?"
    for line in text.splitlines():
        if line.startswith("plugin assembly: "):
            assembly = line.split(": ", 1)[1].strip()
            continue
        parts = line.split("|")
        if len(parts) != 4:
            continue
        kind, value, _size, path = parts
        if kind == "D":
            direct[path] = value
        elif kind == "C":
            cache[path] = value
    if not direct:
        raise RuntimeError("probe produced no D lines:\n" + text[:2000])
    return direct, cache, assembly


def build_fixtures():
    """Boundary sizes with a deterministic, position-dependent pattern."""
    os.makedirs(FIXED, exist_ok=True)
    sizes = [
        ("empty_0.bin", 0),
        ("one_byte_1.bin", 1),
        ("seven_bytes_7.bin", 7),
        ("eight_bytes_8.bin", 8),
        ("word_minus1_65535.bin", 65535),
        ("exactly_64k_65536.bin", 65536),
        ("64k_plus1_65537.bin", 65537),
        ("128k_minus1_131071.bin", 131071),
        ("exactly_128k_131072.bin", 131072),
        ("128k_plus1_131073.bin", 131073),
        ("two_hundred_k_200000.bin", 200000),
    ]
    made = []
    for name, size in sizes:
        path = os.path.join(FIXED, name)
        block = bytes(((i * 7 + 13) & 0xFF) for i in range(4096))
        with open(path, "wb") as fh:
            written = 0
            while written < size:
                take = min(len(block), size - written)
                fh.write(block[:take])
                written += take
        made.append((name, path))
    return made


def real_files(limit=6):
    if not os.path.isdir(REAL_DIR):
        return []
    found = []
    for root, _dirs, files in os.walk(REAL_DIR):
        for f in sorted(files):
            if f.lower().endswith((".mkv", ".mp4", ".avi", ".m4v")):
                p = os.path.join(root, f)
                try:
                    if os.path.getsize(p) >= CANONICAL_MIN:
                        found.append(p)
                except OSError:
                    continue
            if len(found) >= limit:
                return found
        if len(found) >= limit:
            break
    return found


def main():
    quick = "--quick" in sys.argv
    print("OSHash cross-check: shipped assembly vs independent oracle")
    print("oracle: " + os.path.join(HERE, "oshash_oracle.py"))

    fixtures = build_fixtures()
    media = [] if quick else real_files()
    if not quick and not media:
        print("WARNING: no real media found under " + REAL_DIR)

    all_paths = [p for _n, p in fixtures] + media
    direct, cache, assembly = run_probe(all_paths)
    print("probe loaded: " + assembly)
    print()

    rows, ok, bad = [], 0, 0

    def record(label, path, expected, tolerated=()):
        nonlocal ok, bad
        got = direct.get(path, "<absent>")
        cached = cache.get(path, "<absent>")
        if got == expected or got in tolerated:
            verdict = "OK"
            ok += 1
        else:
            verdict = "MISMATCH"
            bad += 1
        cache_note = "ok" if cached == got else "DIFFERS " + str(cached)
        rows.append((verdict, label, expected, got, cache_note))

    for name, path in fixtures:
        size = os.path.getsize(path)
        if size == 0:
            record("fixture " + name, path, "<null>")
        else:
            record("fixture " + name, path, oshash_firstlast(path))

    for path in media:
        record("real " + os.path.basename(path)[:34], path, oshash_firstlast(path))

    print("  " + "VERDICT".ljust(9) + "LABEL".ljust(34) + "ORACLE".ljust(18) +
          "PLUGIN".ljust(18) + "CACHE")
    for verdict, label, expected, got, cache_note in rows:
        print("  " + verdict.ljust(9) + label.ljust(34) + expected.ljust(18) +
              got.ljust(18) + cache_note)

    # The boundary that matters beyond the diff: where canonical and firstlast
    # agree (>= 128 KiB), the plugin's value is comparable with other clients.
    if media:
        print()
        print("  canonical comparison on real media (>= 128 KiB, both variants agree):")
        canon_ok = sum(1 for p in media if direct.get(p) == oshash_canonical(p))
        print("    " + str(canon_ok) + "/" + str(len(media)) +
              " real files match the PUBLISHED canonical hash too")
        for p in media:
            if direct.get(p) != oshash_canonical(p):
                print("    DIFFERS: " + p)
                ok -= 1 if canon_ok == len(media) else 0
                bad += 1 if canon_ok != len(media) else 0
                break

    print()
    print("matched: " + str(ok) + "   mismatched: " + str(bad))
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
