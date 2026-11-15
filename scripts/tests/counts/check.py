#!/usr/bin/env python3
"""Metrik-Prüfung: die Prüfungszahlen im Lastenheft gegen die Suiten.

Das Lastenheft nennt zu einem Test eine Zahl ("**29 Prüfungen.**"). Diese Zahl war
bisher eine Behauptung: kein Prüfer hat sie nachgerechnet. Dieses Skript bindet jede
behauptete Zahl über ``counts.json`` an die Suite, die sie trägt, führt jede betroffene
Suite einmal aus und hält die behauptete Summe gegen die tatsächlich gemeldete Summe.

Geprüft wird:

1. jede Zahl im Dokument hat eine Bindung (kein ungebundener Anspruch),
2. die Bindung nennt dieselbe Zahl wie das Dokument (kein Auseinanderlaufen),
3. jede gebundene Suite läuft grün,
4. die Suite trägt die Summe ihrer Testzahlen (Suite-Summe >= Summe der Ansprüche).

Punkt 4 ist die harte Grenze, nicht die exakte Prüfung je Test: eine Suite meldet nur
ihre Gesamtsumme, und mehrere Tests teilen sich eine Suite. Eine exakte Prüfung je Test
braucht Gruppenmarken in der Suite; solange die fehlen, meldet der Prüfer die Lücke als
Hinweis, nicht als Fehler.

Aufruf:  python3 scripts/tests/counts/check.py [--no-run] [<repo>]
"""
import json
import os
import re
import subprocess
import sys
import tempfile

DOC = os.path.join("docs", "REQUIREMENTS.md")
COUNTS = os.path.join("scripts", "tests", "counts.json")
DOTNET = os.environ.get("DOTNET_HOST", "/opt/data/.dotnet/dotnet")
TOTAL_RE = re.compile(r"^(?P<name>[\w\-]+):\s*(?P<n>\d+)\s+(?:checks?|Prüfungen)", re.M)


def read_claims(doc_path):
    """Test -> behauptete Prüfungszahl, gelesen aus dem Dokument."""
    lines = open(doc_path, encoding="utf-8").read().split("\n")
    defs = [(i, m.group(1)) for i, l in enumerate(lines)
            for m in [re.match(r"^\*\*(T\d+):\*\*", l)] if m]
    claims = {}
    for k, (i, tid) in enumerate(defs):
        end = defs[k + 1][0] if k + 1 < len(defs) else len(lines)
        body = "\n".join(lines[i:end])
        m = re.search(r"\*\*(\d+)\s*Prüfung", body)
        if m:
            claims[tid] = int(m.group(1))
    return claims


def run_suite(repo, name, spec):
    """Führt eine Suite aus und liefert (rc, gemeldete Summe, Ausgabe)."""
    tmp = tempfile.mkdtemp(prefix="counts-")
    if "python" in spec:
        # A python suite may want the repo (spec-doc) or one file (gui-structure).
        target = os.path.join(repo, spec["file"]) if spec.get("file") else repo
        cmd = [sys.executable, os.path.join(repo, spec["python"]), target]
    else:
        args = [a.replace("<root>", repo).replace("<temp>", tmp) for a in spec["args"]]
        proj = os.path.join(repo, "scripts", "tests", name, name + ".csproj")
        cmd = [DOTNET, "run", "--project", proj, "-c", "Release", "--"] + args
    # A suite that opens the real store must run with real globalization (the store carries a `de`
    # collation); the rest run invariant, which is faster and matches the build flag.
    env = dict(os.environ)
    if spec.get("invariant", True):
        env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1"
    else:
        env.pop("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", None)
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=900, env=env)
    out = proc.stdout + proc.stderr
    total = None
    for m in TOTAL_RE.finditer(out):
        if m.group("name").lower().startswith(name.split("-")[0].lower()):
            total = int(m.group("n"))
    if total is None:
        # Suites that print one PASS/FAIL marker per check but no summary: count the markers.
        # This is the same number the summary line would carry — the marker is the check.
        marks = len(re.findall(r"\[PASS\]|\bOK\s{2,}", out))
        for m in re.finditer(r"(\d+)/(\d+)\s+Prüfungen", out):
            marks = max(marks, int(m.group(2)))
        total = marks if marks else None
    return proc.returncode, total, out


def main():
    argv = [a for a in sys.argv[1:] if not a.startswith("--")]
    no_run = "--no-run" in sys.argv
    repo = argv[0] if argv else os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))

    doc = os.path.join(repo, DOC)
    counts_path = os.path.join(repo, COUNTS)
    if not os.path.exists(doc) or not os.path.exists(counts_path):
        print(">>> METRIK FEHLER: Dokument oder counts.json fehlt")
        return 1

    claims = read_claims(doc)
    book = json.load(open(counts_path, encoding="utf-8"))
    bound = book.get("tests", {})
    suites = book.get("suites", {})

    results = []

    def check(name, ok, detail=""):
        results.append((name, ok, detail))

    # 1. Jede Behauptung ist gebunden, und die Bindung nennt dieselbe Zahl.
    unbound = sorted(set(claims) - set(bound), key=lambda t: int(t[1:]))
    check("jede Prüfungszahl im Dokument ist an eine Suite gebunden", not unbound,
          "ungebunden: %s" % ", ".join(unbound))
    mismatched = [(t, claims[t], bound[t]["checks"]) for t in sorted(set(claims) & set(bound), key=lambda x: int(x[1:]))
                  if claims[t] != bound[t]["checks"]]
    check("Dokument und Bindung nennen dieselbe Zahl", not mismatched,
          "; ".join("%s: doc %d, counts.json %d" % m for m in mismatched))
    stale = sorted(set(bound) - set(claims), key=lambda t: int(t[1:]))
    check("keine Bindung ohne Zahl im Dokument", not stale, "verwaist: %s" % ", ".join(stale))

    # 2. Jede genannte Suite ist bekannt.
    unknown = sorted({v["suite"] for v in bound.values()} - set(suites))
    check("jede gebundene Suite ist in counts.json beschrieben", not unknown,
          "unbekannt: %s" % ", ".join(unknown))

    # 3./4. Suiten laufen lassen und die Summe gegen die Ansprüche halten.
    per_suite = {}
    for t, v in bound.items():
        per_suite.setdefault(v["suite"], []).append((t, v["checks"]))
    if no_run:
        print("   (Suiten nicht ausgeführt: --no-run)")
    else:
        for name in sorted(per_suite):
            if name not in suites:
                continue
            if suites[name].get("skip_run"):
                # Bound, but this host cannot run it (see the reason in counts.json). The binding is
                # still checked above; the run is reported as skipped, never as green.
                print("   %-14s Lauf übersprungen  (%s)" % (name, suites[name]["skip_run"]))
                continue
            rc, total, out = run_suite(repo, name, suites[name])
            want = sum(n for _, n in per_suite[name])
            check("%s läuft grün" % name, rc == 0,
                  "exit %d: %s" % (rc, "\n".join(out.strip().split("\n")[-3:])[:200]))
            check("%s meldet eine Summe" % name, total is not None, "keine Summenzeile")
            if total is not None:
                check("%s trägt seine Testzahlen (%d >= %d)" % (name, total, want), total >= want,
                      "%s meldet %d, das Dokument behauptet %d" % (name, total, want))
            print("   %-14s Suite %4s  Summe der Tests %3d  %s"
                  % (name, total if total is not None else "?", want,
                     "exakt" if total == want else "Teilmenge"))

    failed = [r for r in results if not r[1]]
    for name, ok, detail in results:
        if not ok:
            print("  FAIL %s  %s" % (name, detail))
    print(">>> METRIK %s: %d/%d Prüfungen bestanden"
          % ("OK" if not failed else "FEHLER", len(results) - len(failed), len(results)))
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
