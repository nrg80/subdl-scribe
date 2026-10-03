#!/usr/bin/env python3
"""Independent OSHash oracle for SubDL Scribe.

Written from the PUBLISHED OpenSubtitles hash definition, deliberately NOT from
ContentHashRegistry.ComputeMediaHash — a reference that mirrors the code under
test agrees with it by construction and proves nothing.

The algorithm:
    hash = filesize
    hash += sum of the first 64 KiB read as little-endian 64-bit words
    hash += sum of the last  64 KiB read as little-endian 64-bit words
    result = 16 lowercase hex digits, unsigned 64-bit wraparound
A trailing partial word (< 8 bytes) is NOT folded in.

Two variants exist in the wild and they DIFFER on small files, so this oracle
implements both instead of silently picking one:

  canonical   the classic published reference. Refuses anything under
              64 KiB * 2 (128 KiB) with SizeError: the algorithm is not
              defined there, because the two windows would meet or overlap
              and the spec's read loop runs past EOF.
  firstlast   no refusal. Windows are min(64 KiB, filesize) wide. For a file
              under 64 KiB the two windows are the SAME bytes, which are then
              counted TWICE.

Usage:
    python3 oshash_oracle.py <file> [<file> ...]        # both variants, tab-separated
    python3 oshash_oracle.py --variant canonical <file>
"""

import os
import struct
import sys

WORD = struct.Struct("<Q")  # little-endian unsigned 64-bit
CHUNK = 65536
MASK = 0xFFFFFFFFFFFFFFFF


def _sum_window(fh, length):
    """Sum a window of `length` bytes in 8-byte little-endian words.

    A trailing partial word is dropped, matching `range(chunk // 8)`.
    """
    total = 0
    words = length // WORD.size
    for _ in range(words):
        buf = fh.read(WORD.size)
        if len(buf) < WORD.size:
            raise ValueError("short read: %d of %d bytes" % (len(buf), WORD.size))
        total = (total + WORD.unpack(buf)[0]) & MASK
    return total


def oshash_canonical(path):
    """Classic published reference: SizeError under 128 KiB."""
    size = os.path.getsize(path)
    if size < CHUNK * 2:
        return "SizeError"
    h = size
    with open(path, "rb") as fh:
        h = (h + _sum_window(fh, CHUNK)) & MASK
        fh.seek(size - CHUNK)
        h = (h + _sum_window(fh, CHUNK)) & MASK
    return "%016x" % h


def oshash_firstlast(path):
    """No refusal: windows are min(64 KiB, filesize); below 64 KiB the same
    bytes are summed twice."""
    size = os.path.getsize(path)
    if size == 0:
        return "EmptyError"
    chunk = min(CHUNK, size)
    h = size
    with open(path, "rb") as fh:
        h = (h + _sum_window(fh, chunk)) & MASK
        fh.seek(size - chunk)
        h = (h + _sum_window(fh, chunk)) & MASK
    return "%016x" % h


VARIANTS = {"canonical": oshash_canonical, "firstlast": oshash_firstlast}


def main(argv):
    if not argv:
        print(__doc__)
        return 2

    variant = None
    if argv[0] == "--variant":
        variant = argv[1]
        argv = argv[2:]
        if variant not in VARIANTS:
            print("unknown variant %r" % variant, file=sys.stderr)
            return 2

    rc = 0
    for path in argv:
        if not os.path.exists(path):
            print("%s\tMISSING" % path)
            rc = 1
            continue
        try:
            if variant:
                print("%s\t%s" % (VARIANTS[variant](path), path))
            else:
                print("%s\t%s\t%s" % (
                    oshash_canonical(path), oshash_firstlast(path), path))
        except Exception as exc:  # noqa: BLE001 - oracle reports, never hides
            print("%s\tERROR %s: %s" % (path, type(exc).__name__, exc))
            rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
