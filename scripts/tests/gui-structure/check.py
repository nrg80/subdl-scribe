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

"""Structural checks for the configuration page (no Jellyfin host needed).

Why this exists: three defects in this page were invisible in the browser and only showed up
as "it feels sluggish" or "why is this on every tab" —

  1. A section placed OUTSIDE the tab containers stays visible under EVERY tab, because
     subdlShowTab() only toggles elements carrying class="subdl-tab" (F-M265).
  2. A loader called only at page init never updates again, so a manual run looked like it
     had not happened at all (measured 30.09.2026 — the user read it as server caching).
  3. A `window.x` reference to something that is NOT on window resolves to undefined, and the
     refresh silently skips that readout. quota/status are plain function declarations, so
     reading them off window breaks exactly the two the user cares about most.

Run: python3 scripts/tests/gui-structure/check.py [path/to/configPage.html]
Exit code 0 = all checks pass.
"""

import os
import re
import subprocess
import sys
import tempfile

DEFAULT = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "..",
    "Jellyfin.Plugin.SubdlSync", "Configuration", "configPage.html",
)

results = []

def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))

def main():
    path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT
    path = os.path.abspath(path)
    html = open(path, encoding="utf-8").read()

    # ---- 1. Tabs: the Workers section belongs to the general tab, and only there ----
    tabs = ["general", "expert", "upload", "download"]
    idx = {}
    for t in tabs:
        pos = html.find('data-tab-content="%s"' % t)
        check("tab %s exists" % t, pos != -1)
        idx[t] = pos

    check("WorkerRunList occurs exactly once", html.count('id="WorkerRunList"') == 1,
          "count=%d" % html.count('id="WorkerRunList"'))
    if idx.get("general", -1) != -1:
        next_tab = min(p for t, p in idx.items() if t != "general" and p > idx["general"])
        general = html[idx["general"]:next_tab]
        check("WorkerRunList sits INSIDE the general tab",
              'id="WorkerRunList"' in general)
        for t in ("expert", "upload", "download"):
            if idx[t] == -1:
                continue
            start = idx[t]
            rest = html[start:]
            ends = [rest.find('data-tab-content="%s"' % o) for o in tabs]
            ends = [e for e in ends if e > 0]
            section = rest[:min(ends)] if ends else rest
            check("WorkerRunList absent from the %s tab" % t,
                  'id="WorkerRunList"' not in section)

    # ---- 2. The refresh button: exactly one, named "Refresh status", below the tabs, above Save ----
    check("refresh button occurs exactly once",
          html.count('id="RefreshSubdlStatus"') == 1,
          "count=%d" % html.count('id="RefreshSubdlStatus"'))
    check("refresh button reads 'Refresh status'", ">Refresh status</button>" in html)
    check("no leftover 'Refresh' button in the status block",
          html.count(">Refresh</button>") == 0)

    # The button belongs at the END of the GENERAL tab: every live readout it refreshes lives
    # there, and being inside the tab container is what keeps it off the other three tabs
    # (user order 30.09.2026: "es reicht wenn er unter General tab sitzt woanders laufen keine
    # live daten rein").
    btn = html.find('id="RefreshSubdlStatus"')
    save = html.find('type="submit" class="raised button-submit block')
    check("refresh button sits ABOVE Save", btn != -1 and save != -1 and btn < save)
    if idx.get("general", -1) != -1 and btn != -1:
        gen_end = min(p for t, p in idx.items() if t != "general" and p > idx["general"])
        check("refresh button sits INSIDE the general tab", idx["general"] < btn < gen_end)
        for t in ("expert", "upload", "download"):
            if idx[t] == -1:
                continue
            check("refresh button absent from the %s tab" % t, btn < idx[t])
    # It must sit at the tab's end, i.e. below the Workers list it also refreshes.
    check("refresh button sits below the Workers list",
          btn > html.find('id="WorkerRunList"'))

    # ---- 2b. The refresh button looks and sits like Save, without being mistaken for it ----
    # User order 30.09.2026: "Wenn der refresh button jetzt nich genauso groß wie der save Button
    # wäre un vielleicht grau zur abgrenzung" and "bündig links und rechts zum save Button
    # angeordnet wäre". 'block' is what gives Save its full width, and both must sit in a bare
    # wrapper div so the left and right edges line up.
    check("refresh button carries the same width classes as Save",
          'class="raised button-submit block emby-button" id="RefreshSubdlStatus"' in html)
    check("refresh button is greyed to set it apart from Save",
          "background-color:#424242" in html and "border-color:#5a5a5a" in html)
    # Width MUST be inline as well: the user reported it rendering narrow and left-aligned on a
    # phone ("und refresh ist definitiv nicht links und rechtsbündig zum save") while it measured
    # full width in a desktop browser. A theme/client whose own .emby-button rule comes after
    # .block overrides the class; an inline style cannot be reordered away.
    check("refresh button sets display/width/box-sizing INLINE",
          "display:block;width:100%;box-sizing:border-box;" in html)
    check("refresh button keeps the block class as well",
          'class="raised button-submit block emby-button" id="RefreshSubdlStatus"' in html)
    # The wrapper must stay a BARE div (no inline style) so the button keeps Save's full width and
    # both edges line up — any margin/padding on the wrapper would break the flushness. Matched
    # structurally rather than by literal text, because the wrapper legitimately carries an inner
    # HTML comment and its wording changes.
    wrap = re.search(
        r'<div(\s+style="[^"]*")?>\s*(?:<!--.*?-->\s*)*<button[^>]*id="RefreshSubdlStatus"',
        html, re.S)
    check("refresh button sits in a bare wrapper div (no inline style)", wrap is not None)
    check("refresh wrapper carries no inline margin/padding", wrap is not None and wrap.group(1) is None)

    # ---- 2c. A refresh must be VISIBLE: minute resolution made the button look dead ----
    # All four requests fired, yet the footer string stayed identical inside the same minute, so
    # the user concluded it did nothing ("es andert sich nix beim draufdrücken"). Seconds fix that.
    fmt = re.search(r"var subdlFmtTime = function \(iso\) \{(.*?)\};", html, re.S)
    check("subdlFmtTime renders seconds", fmt is not None and "second: '2-digit'" in fmt.group(1),
          "minute-only timestamps make a manual refresh look like a no-op")

    # ---- 2e. The stamp belongs directly ABOVE the button, and ONLY the button writes it ----
    # User order 30.09.2026: "Muss der Zeitstempel dann nicht direkt über den refresh button?"
    # and "Dann nur noch vom refresh bitte". It used to sit up in the SubDL/TMDb block and had TWO
    # automatic writers — the quota fetch (server-cached 5 min) and the status probe — so the line
    # could show a time unrelated to any click, and the poll moved it by itself.
    ts = html.find('id="SubdlStatusTimestamp"')
    check("timestamp element exists exactly once",
          html.count('id="SubdlStatusTimestamp"') == 1,
          "count=%d" % html.count('id="SubdlStatusTimestamp"'))
    check("timestamp sits directly ABOVE the refresh button", ts != -1 and btn != -1 and ts < btn)
    if idx.get("general", -1) != -1:
        check("timestamp sits inside the general tab", idx["general"] < ts)
    # Neither automatic loader may stamp the line.
    check("quota loader does NOT write the stamp",
          "Store fetched time for status footer" not in html)
    check("status loader does NOT write the stamp",
          "ts.textContent = 'Updated '" not in html)
    # Exactly one writer, and it is the button handler.
    check("exactly one writer of the stamp",
          html.count("#SubdlStatusTimestamp") == 1)
    check("the writer is the refresh button",
          "stamp.textContent = 'Manually refreshed ' + subdlFmtTime(new Date())" in html)
    # The stamp must show the click moment, NOT a server timestamp that may be server-cached.
    check("stamp uses the click time, not a server timestamp",
          "subdlFmtTime(new Date())" in html)

    # ---- 2d. Every refreshed endpoint carries a cache-buster ----
    # quota already had one; stats and workers were added so no layer can serve a stale body.
    check("stats request carries a cache-buster", "statsUrl += (statsUrl.indexOf('?')" in html)
    check("workers request carries a cache-buster", "wUrl += (wUrl.indexOf('?')" in html)
    check("status request carries a cache-buster", "statusUrl += (statusUrl.indexOf('?')" in html)
    check("quota request carries a cache-buster", "subdlQ += (subdlQ.indexOf('?')" in html)

    # ---- 3. The one refresh reloads ALL five readouts ----
    check("subdlReloadEverything exists",
          "window.subdlReloadEverything = subdlReloadEverything;" in html)
    for loader in ("subdlLoadQuota]", "subdlLoadStatus]", "subdlLoadStats]",
                   "subdlLoadWorkers]"):
        check("refresh covers %s" % loader.rstrip("]"), loader in html)
    # quota covers BOTH the API/search allowance and the download allowance
    check("quota loader renders the search bar", "renderBar('Search'" in html)
    check("quota loader renders the downloads bar", "renderBar('Downloads'" in html)
    # status covers the lights AND the library read/write rows
    check("status loader renders the library read/write rows",
          "#SubdlDirStatusList" in html)

    # ---- 4. Scope trap: window.* only where it IS on window ----
    check("no window.subdlLoadQuota reference (it is a plain declaration)",
          "window.subdlLoadQuota" not in html)
    check("no window.subdlLoadStatus reference (it is a plain declaration)",
          "window.subdlLoadStatus" not in html)
    check("button handler guards through window.subdlReloadEverything",
          "typeof window.subdlReloadEverything === 'function'" in html)

    # ---- 5. Workers must actually poll, not only run at init ----
    poll = re.search(r"setInterval\(function \(\) \{(.*?)\}, 10000\)", html, re.S)
    check("10s poll exists", poll is not None)
    if poll:
        body = poll.group(1)
        check("10s poll reloads the statistics", "subdlLoadStats" in body)
        check("10s poll reloads the workers", "subdlLoadWorkers" in body)

    # ---- 5b. The default target languages are the five the user chose (08.09.2026) ----
    # Read from the C# source, not from a comment or the docs: a rewrite once cut the
    # list from five entries to three and left the documentation claiming five, so the
    # two disagreed silently for two weeks (found 30.09.2026). The default is what a
    # fresh install gets, so it must be checked where it is defined.
    cfg_cs = os.path.join(os.path.dirname(path), "PluginConfiguration.cs")
    if os.path.isfile(cfg_cs):
        cs = open(cfg_cs, encoding="utf-8").read()
        m = re.search(r'DownloadLanguages\s*\{[^}]*\}\s*=\s*"([^"]*)"', cs)
        check("DownloadLanguages default is found in the C# source", m is not None)
        if m:
            default = [x.strip().upper() for x in m.group(1).split(",") if x.strip()]
            check("default languages are AR, EN, ES, FR, HI, ZH",
                  default == ["AR", "EN", "ES", "FR", "HI", "ZH"],
                  "found=%s" % ",".join(default))
            # Every default must be a language the picker can actually offer.
            for code in default:
                check("default language %s exists in the picker" % code,
                      ("['%s'," % code) in html or ("['%s', " % code) in html.replace("','", "',"))
    else:
        check("PluginConfiguration.cs found next to the page", False, cfg_cs)

    # ---- 5c. The second page script must not write the stamp either ----
    # configPage.js carries its own copy of the status loader. It is shipped (embedded
    # resource + ConfigJs route) and it used to write the stamp with a SERVER timestamp
    # labelled "Last checked", which contradicts the one-writer rule and would show a
    # time the click did not cause. Two files, one rule.
    js_path = os.path.join(os.path.dirname(path), "configPage.js")
    if os.path.isfile(js_path):
        page_js = open(js_path, encoding="utf-8").read()
        check("configPage.js does NOT write the status stamp",
              "SubdlStatusTimestamp" not in page_js
              or ".textContent = 'Last checked" not in page_js)
    else:
        check("configPage.js found next to the page", False, js_path)

    # ---- 6. Backticks break the embedded CSS/JS entirely (a template literal swallows it) ----
    check("exactly two backticks in the file (the CSS comment pair)",
          html.count("`") == 2, "count=%d" % html.count("`"))

    # ---- 7. The embedded script must parse ----
    blocks = re.findall(r"<script>(.*?)</script>", html, re.S)
    check("at least one inline script block", len(blocks) >= 1)
    for i, body in enumerate(blocks):
        with tempfile.NamedTemporaryFile("w", suffix=".js", delete=False,
                                         encoding="utf-8") as fh:
            fh.write(body)
            tmp = fh.name
        try:
            proc = subprocess.run(["node", "--check", tmp],
                                  capture_output=True, text=True)
            check("script block %d parses" % i, proc.returncode == 0,
                  proc.stderr[:300])
        except FileNotFoundError:
            check("node available for syntax check", False, "node not found")
        finally:
            os.unlink(tmp)

    failed = [r for r in results if not r[1]]
    for name, ok, detail in results:
        line = "  %s %s" % ("OK  " if ok else "FAIL", name)
        if not ok and detail:
            line += "  (%s)" % detail
        print(line)
    print()
    print(">>> GUI-STRUKTUR %s: %d/%d Pruefungen bestanden"
          % ("OK" if not failed else "FEHLER", len(results) - len(failed), len(results)))
    return 1 if failed else 0

if __name__ == "__main__":
    sys.exit(main())
