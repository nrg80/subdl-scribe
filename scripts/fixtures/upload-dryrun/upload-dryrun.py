#!/usr/bin/env python3
# This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Sub Scribe is free software: you can redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the
# Free Software Foundation, either version 3 of the License, or (at your
# option) any later version.
#
# Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the
# implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
# See the GNU General Public License for more details.

"""F-M496: das Upload-Fixture — die Kette ueber BEIDE Endpunkte als Trockenlauf.

Was hier geprueft wird, und warum jede Zeile beissen wuerde:

  A  Die Kette versucht BEIDE Endpunkte, in der konfigurierten Reihenfolge. Der Trockenlauf
     stand bis F-M496 als EIN Ausstieg vor der Kette und schrieb EINE Zeile — die zweite
     Quelle, ihr Schalter und die Reihenfolge waren im Bericht nicht vorhanden. Zwei Zeilen,
     eine je Quelle, sind der einzige Beleg, dass die Kette wirklich gelaufen ist.
  B  Vor dem Start jeder Quelle wurde ihr Toggle gefragt. Das steht im Quelltext (die Zeile
     nennt nur die Quelle, nicht ihre Pruefung) — der Bericht sagt ausdruecklich, dass dieser
     Beleg aus dem Code kommt und nicht aus dem Log.
  C  Kein echter Upload: im Store steht fuer das Fixture-Medium KEIN Upload-Zeitstempel und
     keine Upload-Id, und im Log gibt es keine Erfolgszeile einer Quelle. Der Trockenlauf
     darf nichts schreiben (F-M22) — ein geschriebener Zeitstempel naehme dem naechsten
     echten Lauf die Arbeit weg.
  D  Der Lauf endet regulaer (Abschlusszeile vorhanden).

Das Fixture-Medium ist eine Folge, deren Untertitel-INHALT im Store keine Herkunft hat: ein
bekannter Inhalt wuerde am Herkunftstor (F-M487) haengen und die zweite Quelle zu Recht
auslassen. Gibt es keine solche Datei, wird eine Kopie eines vorhandenen Seitenwagens unter
neuem Namen angelegt (der Inhalt wird dabei veraendert, sonst haette die Kopie denselben Hash
wie das Original und damit dessen Herkunft) und am Ende wieder geloescht.

Aufruf:
  scripts/fixtures/upload-dryrun/upload-dryrun.py [--url URL] [--secrets PFAD]
                                                  [--log PFAD] [--store PFAD]
                                                  [--media PFAD] [--timeout SEK]
Exit-Code 0 nur wenn alle Pruefungen bestanden sind.
"""

import argparse
import hashlib
import json
import os
import re
import signal
import sqlite3
import sys
import time
import urllib.error
import urllib.request

PLUGIN_ID = "7d1c5a2e-3f4b-4d6e-9a8b-5c2f8e1d4a9b"
UPLOAD_TASK_KEY = "SubdlSyncUploadTask"

DEFAULTS = {
    "secrets": "/opt/data/.secrets.json",
    "log": "/opt/data/jf-test/jf-run.log",
    "store": "/opt/data/jf-test/data/plugins/Jellyfin.Plugin.SubdlSync/subdl-scribe.sqlite",
    "media": "/opt/data/jf-test/media/middle381",
}

results = []


def check(what, ok, detail=""):
    results.append((what, bool(ok), detail))
    print("  [%s] %s%s" % ("PASS" if ok else "FAIL", what, "" if ok or not detail else " — " + detail))
    return ok


# ---------------------------------------------------------------------------------------------
# HTTP — der Header ist "Authorization: MediaBrowser Token=\"<key>\""; X-Emby-Token antwortet
# auf dieser Instanz mit 401, obwohl der Key gueltig ist (bekannte Fehlspur, siehe .secrets).
# ---------------------------------------------------------------------------------------------
class Jellyfin:
    def __init__(self, url, key):
        self.url = url.rstrip("/")
        self.key = key

    def call(self, path, method="GET", body=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(
            self.url + path,
            data=data,
            method=method,
            headers={
                "Authorization": 'MediaBrowser Token="%s"' % self.key,
                "Content-Type": "application/json",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=120) as resp:
                raw = resp.read()
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as exc:
            raise RuntimeError("%s %s -> HTTP %s %s" % (method, path, exc.code, exc.read()[:200]))


# ---------------------------------------------------------------------------------------------
# Der Inhalts-Hash des Plugins: MD5 ueber den normalisierten Text (BOM weg, CRLF -> LF,
# Rand-Leerraum weg). Die Zuordnung wird gegen den Store GEPRUEFT, bevor sie benutzt wird —
# eine falsch nachgebaute Normalisierung wuerde sonst eine "unbekannte" Datei behaupten.
# ---------------------------------------------------------------------------------------------
def content_hash(path):
    with open(path, "rb") as fh:
        raw = fh.read()
    text = raw.decode("utf-8-sig", errors="replace")
    text = text.replace("\r\n", "\n").replace("\r", "\n").rstrip()
    return hashlib.md5(text.encode("utf-8")).hexdigest()


def sidecar_rows(store):
    con = sqlite3.connect(store)
    try:
        return con.execute(
            "select FileName, Path, ContentHash, Origin, MediaHash, Language, "
            "SubdlUploadId, TvSubtitlesUploadId, SubdlUploadedAt, TvSubtitlesUploadedAt "
            "from sidecars"
        ).fetchall()
    finally:
        con.close()


def upload_marks(store, media_hash):
    """Upload-MARKEN zu einem Medium: Zeitstempel und Ids beider Quellen."""
    con = sqlite3.connect(store)
    try:
        return con.execute(
            "select FileName, ContentHash, SubdlUploadId, TvSubtitlesUploadId, "
            "SubdlUploadedAt, TvSubtitlesUploadedAt from sidecars "
            "where MediaHash = ? and (SubdlUploadedAt is not null or TvSubtitlesUploadedAt is not null "
            "or SubdlUploadId is not null or TvSubtitlesUploadId is not null)",
            (media_hash,),
        ).fetchall()
    finally:
        con.close()


def hash_rows_for(store, digest):
    con = sqlite3.connect(store)
    try:
        return con.execute(
            "select FileName, Origin, SubdlUploadedAt, TvSubtitlesUploadedAt, SubdlUploadId, TvSubtitlesUploadId "
            "from sidecars where ContentHash = ?",
            (digest,),
        ).fetchall()
    finally:
        con.close()


def verify_hash_function(rows, root):
    """Der nachgebaute Hash muss die Zeilen des Stores treffen — sonst ist er nicht der Hash."""
    hit = miss = 0
    for file_name, path, digest, *_ in rows:
        candidate = path if path and os.path.exists(path) else None
        if candidate is None and file_name:
            for dirpath, _, files in os.walk(root):
                if file_name in files:
                    candidate = os.path.join(dirpath, file_name)
                    break
        if candidate is None:
            continue
        if content_hash(candidate) == digest:
            hit += 1
        else:
            miss += 1
    return hit, miss


def media_hash_of(media_path):
    """Der MediaHash des Stores fuer eine Videodatei (aus den Zeilen ihrer Seitenwagen)."""
    con = sqlite3.connect(DEFAULTS["store"])
    try:
        stem = os.path.splitext(os.path.basename(media_path))[0]
        for file_name, media_hash in con.execute(
            "select FileName, MediaHash from sidecars"
        ):
            if file_name and stem in file_name and media_hash:
                return media_hash
    finally:
        con.close()
    return None


def library_files(root):
    out = []
    for dirpath, _, files in os.walk(root):
        for name in sorted(files):
            if name.lower().endswith(".srt"):
                out.append(os.path.join(dirpath, name))
    return out


def make_fixture_copy(source_srt):
    """Kopie unter neuem Namen (<Folge>.de.srt) mit VERAENDERTEM Inhalt.

    Unveraendert haette die Kopie denselben Hash wie das Original und damit dessen Herkunft
    (F-M487) — der ganze Zweck der Kopie waere dahin. Die Aenderung ist eine Textzeile im
    letzten Cue: Cues, Zeiten und Groesse bleiben, die QA sieht dieselbe Datei.
    """
    media_stem = re.sub(r"\.en\.(\d+)$", "", os.path.splitext(os.path.basename(source_srt))[0])
    target = os.path.join(os.path.dirname(source_srt), media_stem + ".de.srt")
    with open(source_srt, "r", encoding="utf-8-sig", errors="replace") as fh:
        lines = fh.read().replace("\r\n", "\n").replace("\r", "\n").rstrip("\n").split("\n")

    last_text = None
    for index in range(len(lines) - 1, -1, -1):
        line = lines[index].strip()
        if line and not line.isdigit() and "-->" not in line:
            last_text = index
            break
    if last_text is None:
        raise RuntimeError("kein Cue-Text in %s gefunden" % source_srt)

    lines.insert(last_text + 1, "dry-run fixture (F-M496)")
    with open(target, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")
    return target


# ---------------------------------------------------------------------------------------------
# Log
# ---------------------------------------------------------------------------------------------
DRY_LINE = re.compile(
    r"\[(?P<source>SubDL|tvsubtitles)\] DRY-RUN would upload (?P<name>.+?)(?: \[(?P<lang>[A-Za-z]{2})\])?$"
)


def read_from(path, offset):
    """Nur die NEUEN Zeilen — was vor dem Start im Log stand, geht das Fixture nichts an."""
    if not os.path.exists(path):
        return ""
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        fh.seek(offset)
        return fh.read()


def file_size(path):
    try:
        return os.path.getsize(path)
    except OSError:
        return 0


def main():
    ap = argparse.ArgumentParser(description="F-M496: Upload-Kette als Trockenlauf pruefen")
    ap.add_argument("--url")
    ap.add_argument("--secrets", default=DEFAULTS["secrets"])
    ap.add_argument("--log", default=DEFAULTS["log"])
    ap.add_argument("--store", default=DEFAULTS["store"])
    ap.add_argument("--media", default=DEFAULTS["media"])
    ap.add_argument("--timeout", type=int, default=600, help="Sekunden bis zur Abschlusszeile")
    ap.add_argument("--poll", type=int, default=10)
    ap.add_argument("--off", action="append", default=[], choices=["SubDL", "tvsubtitles"],
                    help="diese Quelle NICHT einschalten (prueft den SKIP-Fall: eine Quelle per Toggle aus)")
    args = ap.parse_args()

    DEFAULTS["store"] = args.store
    DEFAULTS["media"] = args.media

    with open(args.secrets, encoding="utf-8") as fh:
        secret = json.load(fh)["jellyfin_test"]
    url = args.url or secret["url"]
    jf = Jellyfin(url, secret["api_key"])
    print("F-M496 — Upload-Trockenlauf gegen %s" % url)

    # ---- 0. Vorpruefung: der nachgebaute Hash trifft die Zeilen des Stores -------------------
    rows = sidecar_rows(args.store)
    hit, miss = verify_hash_function(rows, args.media)
    if not check("der Inhalts-Hash ist der des Stores (Stichprobe %d/%d)" % (hit, hit + miss), hit > 0 and miss == 0,
                 "%d Abweichungen — die Herkunft waere nicht bestimmbar" % miss):
        return 2

    # ---- 1. Konfiguration sichern (nur im Speicher — sie traegt Zugangsdaten) ---------------
    cfg_path = "/Plugins/%s/Configuration" % PLUGIN_ID
    before = jf.call(cfg_path) or {}
    restored = {"done": False}

    def restore():
        if restored["done"]:
            return
        try:
            jf.call(cfg_path, "POST", before)
            restored["done"] = True
            print("  [INFO] Konfiguration wiederhergestellt")
        except Exception as exc:  # noqa: BLE001 — der Bericht darf hier nicht sterben
            print("  [WARN] Konfiguration NICHT wiederhergestellt: %s" % exc)

    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda *_: (restore(), sys.exit(3)))

    fixture_file = None
    try:
        # ---- 2. Konfiguration setzen ---------------------------------------------------------
        order = before.get("DownloadOrder") or "TvSubtitlesFirst"
        after = dict(before)
        after["DryRun"] = True
        after["UploadEnabled"] = True
        # --off laesst genau diese Quelle aus: dann ist der SKIP-Fall zu sehen (genau eine Zeile,
        # die andere Quelle als SKIP gemeldet, kein FAIL).
        after["SubdlUploadEnabled"] = "SubDL" not in args.off
        after["TvSubtitlesUploadEnabled"] = "tvsubtitles" not in args.off
        after["LogMode"] = "Verbose"
        jf.call(cfg_path, "POST", after)
        live = jf.call(cfg_path) or {}
        print("  [INFO] gesetzt: DryRun=%s Upload=%s SubDL-Upload=%s tvst-Upload=%s Reihenfolge=%s" % (
            live.get("DryRun"), live.get("UploadEnabled"),
            live.get("SubdlUploadEnabled"), live.get("TvSubtitlesUploadEnabled"), live.get("DownloadOrder")))

        # die effektiven Schalter, so wie das Plugin sie liest (Alt-Wert als Rueckfall)
        def upload_on(name):
            if name == "SubDL":
                value = live.get("SubdlUploadEnabled")
                return live.get("UploadEnabled") if value is None else value
            value = live.get("TvSubtitlesUploadEnabled")
            return False if value is None else value

        expected = [s for s in (["SubDL", "tvsubtitles"] if order == "SubdlFirst" else ["tvsubtitles", "SubDL"])
                    if upload_on(s)]
        for source in set(["SubDL", "tvsubtitles"]) - set(expected):
            print("  [SKIP] Quelle %s ist per Toggle aus — sie wird nicht erwartet" % source)

        # ---- 3. Fixture-Medium ---------------------------------------------------------------
        known = {row[2] for row in rows if (row[3] or "").strip()}
        medium, created = None, False
        for path in library_files(args.media):
            if content_hash(path) not in known:
                medium = path
                break
        if medium is None:
            source = None
            for path in library_files(args.media):
                if re.search(r"\.en\.\d+$", os.path.splitext(path)[0], re.IGNORECASE):
                    source = path
                    break
            if source is None:
                print("  [FAIL] kein Seitenwagen zum Kopieren gefunden — Fixture-Medium fehlt")
                return 2
            medium = make_fixture_copy(source)
            created = True
        fixture_file = medium if created else None
        digest = content_hash(medium)
        print("  [INFO] Fixture-Medium: %s (%s, Hash %s…)" % (
            os.path.basename(medium), "angelegt" if created else "vorhanden", digest[:8]))

        # ---- 4. Upload-Task starten ---------------------------------------------------------
        tasks = jf.call("/ScheduledTasks") or []
        task = next((t for t in tasks if t.get("Key") == UPLOAD_TASK_KEY), None)
        if task is None:
            print("  [FAIL] Task '%s' nicht gefunden" % UPLOAD_TASK_KEY)
            return 2
        start_size = file_size(args.log)
        started = time.time()
        jf.call("/ScheduledTasks/Running/%s" % task["Id"], "POST")
        print("  [INFO] Task gestartet (%s), Log ab Byte %d" % (task["Id"], start_size))

        finished = None
        while time.time() - started < args.timeout:
            time.sleep(args.poll)
            text = read_from(args.log, start_size)
            if "[SubDL] upload: finished" in text:
                finished = text
                break
        if finished is None:
            finished = read_from(args.log, start_size)
        elapsed = int(time.time() - started)
        print("  [INFO] Lauf beendet nach %ds, %d neue Log-Zeilen" % (elapsed, len(finished.splitlines())))

        lines = finished.splitlines()
        started_line = any("[SubDL] upload: started" in l for l in lines)

        # ---- A: beide Endpunkte, in der konfigurierten Reihenfolge --------------------------
        per_item = {}
        order_broken = []
        for line in lines:
            match = DRY_LINE.search(line)
            if not match:
                continue
            key = (match.group("name").strip(), (match.group("lang") or "").upper())
            per_item.setdefault(key, []).append(match.group("source"))
        fixture_lines = [src for (name, _), srcs in per_item.items() if digest in name for src in srcs]
        order_rank = {name: index for index, name in enumerate(expected)}
        for key, sources in per_item.items():
            ranks = [order_rank.get(s, -1) for s in sources]
            if ranks != sorted(ranks):
                order_broken.append("%s: %s" % (key[0], " -> ".join(sources)))

        if len(expected) == 2:
            check("die Kette fragt beide Endpunkte — je Quelle eine Zeile, Fixture: %s"
                  % (" -> ".join(fixture_lines) or "keine"),
                  fixture_lines == expected,
                  "erwartet %s" % " -> ".join(expected))
        elif len(expected) == 1:
            check("nur eine Quelle ist an: genau eine Zeile, und zwar %s (SKIP fuer die andere)" % expected[0],
                  fixture_lines == expected,
                  "gesehen: %s" % (" -> ".join(fixture_lines) or "keine"))
        else:
            check("keine Quelle ist an — der Lauf darf keine Zeile schreiben", not fixture_lines)
        check("keine Zeile einer Quelle steht ausser der Reihe", not order_broken, "; ".join(order_broken[:3]))

        # ---- B: Toggle vor dem Start (Beleg aus dem Quelltext) ------------------------------
        source_file = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                   "../../../Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs")
        source_file = os.path.normpath(source_file)
        with open(source_file, encoding="utf-8") as fh:
            upload_src = fh.read()
        block = upload_src[upload_src.index("if (_config.DryRun)"):upload_src.index("await SendToSecondSourceAsync(")]
        toggle_before = block.index("if (!SourceAllowed(quelle))") < block.index("[SubDL] DRY-RUN would upload")
        check("jede Quelle fragt ihren Toggle, bevor ihre Zeile steht (Beleg: Quelltext, nicht Log)",
              toggle_before and "foreach (var quelle in UploadSourceOrder())" in block,
              "UploadPipeline.cs, Trockenlauf-Block")

        # ---- C: kein echter Upload -----------------------------------------------------------
        marks = [row for row in hash_rows_for(args.store, digest)]
        marked = [row for row in marks if row[2] or row[3] or row[4] or row[5]]
        check("kein Upload-Zeitstempel und keine Upload-Id fuer das Fixture-Medium (Store)",
              not marked, "Zeilen: %s" % marks[:3])
        success_lines = [l for l in lines if "[SubDL] UPLOADED" in l or re.search(r"\[tvsubtitles\].*— sent", l)]
        check("keine Erfolgszeile einer Quelle im Log", not success_lines, success_lines[:2])
        check("der Lauf meldet sich als Trockenlauf",
              any("DRY-RUN would have uploaded" in l for l in lines))

        # ---- D: regulaeres Ende --------------------------------------------------------------
        check("der Lauf endet regulaer (Abschlusszeile)", any("[SubDL] upload: finished" in l for l in lines),
              "Startzeile gesehen: %s" % started_line)

        # ---- Belegsammlung fuer den Bericht -------------------------------------------------
        if fixture_lines:
            print("  [INFO] Fixture-Zeilen in der Reihenfolge des Logs: %s" % " -> ".join(fixture_lines))
        print("  [INFO] Fixture-Datei: %s" % medium)
    finally:
        if fixture_file and os.path.exists(fixture_file):
            os.remove(fixture_file)
            print("  [INFO] Fixture-Datei geloescht: %s" % os.path.basename(fixture_file))
        restore()

    failed = [r for r in results if not r[1]]
    print()
    print(">>> UPLOAD-DRYRUN %s: %d/%d Pruefungen bestanden"
          % ("OK" if not failed else "FEHLER", len(results) - len(failed), len(results)))
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
