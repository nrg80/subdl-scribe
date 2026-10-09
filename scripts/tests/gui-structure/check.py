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

# F-M273: the configuration page has a host-free structural regression check. The checks are
# structural, not wording-dependent, so a comment rewrite cannot raise a false failure.
# F-M270-F-M272: it asserts the refresh control, its single stamp writer and the cadences.
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


def read_source(*parts):
    """Read a source file from the repo, or '' when it is not there.

    The GUI check runs host-free and is also pointed at a bare page by the caller, so a missing
    source tree must skip the code-level assertions rather than crash the page-level ones.
    """
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..",
                        "Jellyfin.Plugin.SubdlSync")
    try:
        with open(os.path.join(root, *parts), encoding="utf-8") as fh:
            return fh.read()
    except OSError:
        return ""

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

    # ---- 5c. F-M324: there is NO second page script ----
    # The page carried TWO copies of the same JavaScript: the inline block in this file and a
    # separate Configuration/configPage.js, embedded as a resource and served over the ConfigJs
    # route. The route was never called. Checked live in a real browser on 08.10.2026, by two
    # independent methods (a MutationObserver on script injection and performance.getEntries),
    # across direct URL, reload, tab switches and the plugin menu: ZERO loads. The inline block
    # was the only code that ran — every inline function existed (subdlSetLight, subdlLoadStatus,
    # subdlRenderStats) while the two defined only in that file (subdlBootstrapOnce,
    # subdlRefreshBackupSelect) were undefined in the page. And the second copy was not merely
    # redundant: four functions shared a name with a DIFFERENT body (subdlLoadQuota 8,077 vs
    # 3,578 bytes; subdlFindTaskId 286 vs 1,627; subdlLoadStatus 3,226 vs 2,740;
    # subdlStartQuotaPoll 239 vs 135), and it predated the login light — had JF ever loaded it,
    # it would have overwritten the live functions with older ones.
    #
    # So the file, its resource registration, its PluginPageInfo entry and its controller are
    # GONE, and their absence is asserted: a page that grows a second script again is the state
    # this check exists to prevent. A reintroduced copy would restore the file-pair drift with
    # nothing in any log looking abnormal.
    # The project root is two levels up from Configuration/.
    root = os.path.dirname(os.path.dirname(path))
    js_path = os.path.join(os.path.dirname(path), "configPage.js")
    check("no second page script sits next to the page", not os.path.isfile(js_path), js_path)
    ctrl = os.path.join(root, "Api", "SubdlConfigController.cs")
    check("the ConfigJs route controller is gone", not os.path.isfile(ctrl), ctrl)
    plugin_cs = os.path.join(root, "Plugin.cs")
    check("the .js page is no longer registered as a plugin page",
          "configPage.js" not in open(plugin_cs, encoding="utf-8").read(), plugin_cs)
    csproj = os.path.join(root, "Jellyfin.Plugin.SubdlSync.csproj")
    check("the .js page is no longer an EmbeddedResource",
          "configPage.js" not in open(csproj, encoding="utf-8").read(), csproj)

    # ---- 8. Intro text stays OPERATIONAL: no measured values, and a length budget ----
    # F-M299: the block under an h4 heading says what the section DOES. Measurements, episode
    # counts and before/after numbers are spec and commit material. Two intros shipped at 392
    # and 493 rendered characters carrying "Measured accurate to about 0.2 s" and "Measured on
    # 36 drifting episodes: worst line 10.74 s -> 0.17 s, 36 of 36 improved" — the user rejected
    # it as the storyteller returning. The accepted house norm is the Drift-check intro (~241).
    #
    # F-M302: AND THE TEXT MUST NOT CONTRADICT THE CODE. Length and vocabulary were the whole
    # check until 06.10.2026, and both were green while two intros still claimed the audio path
    # CANNOT fix a drifting file — false since the staircase (F-M300) landed, and the user read
    # it on his phone as "4 methods where there should be one". A short sentence stating the
    # opposite of the code passes a length cap AND a ban list, so neither half can carry this.
    # The pattern below is deliberately narrow: it matches the REFUTED claims only, not every
    # mention of drift, because a check that fires on correct prose gets disabled.
    intros = re.findall(r'<div class="fieldDescription" style="margin-bottom:\.6em;">(.*?)</div>',
                        html, re.S)
    # The count is the guard against a VACUOUS pass: the loop below polices whatever this
    # regex finds, so finding nothing would green-light every page. It was `>= 2` while the
    # correction section carried its own h4; that heading was removed on operator order
    # (07.10.2026) and the page now has ONE section description, so the guard is `>= 1`.
    check("intro blocks found (section descriptions)", len(intros) >= 1,
          "count=%d" % len(intros))
    INTRO_MAX = 300
    for raw in intros:
        text = re.sub(r"\s+", " ", re.sub(r"<[^>]+>", "", raw)).strip()
        head = text[:46]
        check("intro <= %d rendered chars: %r" % (INTRO_MAX, head),
              len(text) <= INTRO_MAX, "len=%d" % len(text))
        forensic = [m for m in ("Measured", "of 36", " -> ", "\u2192", "0.2 s") if m in text]
        check("intro carries no measured value: %r" % head, not forensic,
              "found=%s" % forensic)

    # The refuted claims, checked over EVERY user-visible text node on the page — intros,
    # per-switch fieldDescription blocks AND the switch LABELS (the <span> in each label).
    # Measured 06.10.2026: restricting this to fieldDescription missed a planted
    # "never shifted" label, which is exactly how the real defect survived — the false
    # "no valid single correction" sat in a description while the refuted phrasing sat in
    # the intro, and a third variant fits in a label.
    all_desc = re.findall(r'<div class="fieldDescription"[^>]*>(.*?)</div>', html, re.S)
    all_labels = re.findall(r'<label class="emby-checkbox-label">.*?<span>(.*?)</span>', html, re.S)
    refuted = {
        "cannot correct": "the audio path DOES correct a drifting file now (F-M300 staircase)",
        "can not correct": "the audio path DOES correct a drifting file now (F-M300 staircase)",
        "has no valid single correction": "no valid SINGLE offset does not mean no correction (F-M300)",
        "is only reported": "a drifting file is corrected, not merely reported (F-M300)",
        "never shifted": "a drifting file IS shifted, segment by segment (F-M300)",
        "cannot touch": "the audio path is not limited to constant offsets any more (F-M300)",
    }
    for raw in all_desc + all_labels:
        text = re.sub(r"\s+", " ", re.sub(r"<[^>]+>", "", raw)).strip()
        for bad, why in refuted.items():
            if bad in text:
                check("no refuted claim %r: %r" % (bad, text[:40]), False, why)
    check("no description repeats a claim the code refuted", True)

    # ---- 6. Backticks break the embedded CSS/JS entirely (a template literal swallows it) ----
    # The rule is about the CSS TEMPLATE LITERAL, not about the backtick character: one inside
    # the literal closes it early, the script throws, and NO stylesheet is injected while the
    # page still renders — it reads as a CSS-specificity bug. Backticks in `//` comments are
    # inert and are the documented way to quote a status word. Counting the whole file cannot
    # tell the two apart and fails on correct code (it did: 10 total, 2 of them the literal).
    css_lit = re.search(r"var css = `(.*?)`;", html, re.S)
    check("the CSS template literal exists", css_lit is not None)
    if css_lit:
        check("the CSS template literal is closed exactly once (no stray backtick inside)",
              css_lit.group(1).count("`") == 0,
              "inner backticks=%d" % css_lit.group(1).count("`"))
    # And the whole file must hold no MORE than the literal's two delimiters plus the comment
    # quotes that are legitimately there — verified per script block below where line numbers
    # are meaningful, so this is only a coarse net for a third, unbalanced delimiter.
    check("no third backtick outside a comment or the CSS literal",
          html.count("`") % 2 == 0, "count=%d" % html.count("`"))

    # ---- 6b. F-M308/T123: the statistics table ----
    # One ROW per counter, label left and number right in two bounded columns. Asserted here rather
    # than trusted, because the counters are the only readout of what a run did, and the failure
    # modes are all silent: a row that reads a field the API never sends prints 0 forever (the
    # F-M218 class of bug), a row added to ONE copy of the file pair never renders (the file-pair
    # trap), and a row claiming a direction the counter does not have states something untrue.
    #
    # Cross-checked against the SOURCE, so a rename on the C# side cannot leave the page reading a
    # field that no longer exists — the two are in different languages and nothing else compares them.
    check("the statistics render as a table, not a sentence",
          'id="StatsTable"' in html and "#StatsTable {" in html)
    check("the table has two bounded columns (numbers line up)",
          "grid-template-columns: max-content max-content;" in html)
    # Only the RENDERER counts: the phrase may legitimately appear in a comment that explains why
    # the sentence form was dropped, and a check that fires on prose gets disabled.
    renderer = html[html.index("var subdlRenderStats = function (s) {"):]
    renderer = renderer[:renderer.index("window.subdlRenderStats =")]
    check("no run-on quality line is left in the renderer",
          "StatsQualityLine" not in renderer and "Corrections and rejects" not in renderer)

    # The ORDER of the rows is the user's (07.10.2026): volumes first with DOWNLOAD leading, then
    # the both-directions counter, then the rest grouped by direction (downloads, then uploads).
    # Asserted because a regrouping that reverts is invisible — the table still renders, it just
    # reads the old way, and no number changes. The labels are matched as they appear in `rows`.
    order = re.findall(r"\['([^']+)'", renderer)
    # F-M322 (operator order 08.10.2026): the type row and the year row were WITHDRAWN, with their
    # counters — the operator asked for both to go ("Type movies series und searches run without the
    # year kann entfallen. In statistik und der Zähler"). The table is SEVEN rows now and the volume
    # pair leads directly; the both-directions row that used to sit between them no longer exists, so
    # the prefix assertion covers the two volume rows only.
    expected_prefix = [
        "Subtitles downloaded",
        "Subtitles uploaded",
    ]
    check("the volume rows lead, download first",
          order[:2] == expected_prefix,
          "got: %s" % " | ".join(order[:2]))
    dl = [i for i, o in enumerate(order) if o.startswith("Downloads:")]
    up = [i for i, o in enumerate(order) if o.startswith("Uploads:")]
    check("the remaining rows are grouped by direction, downloads before uploads",
          bool(dl) and bool(up) and max(dl) < min(up),
          "downloads at %s, uploads at %s" % (dl, up))
    check("every quality row is present exactly once",
          len(order) == 7 and len(set(order)) == 7,
          "%d row(s): %s" % (len(order), order))
    # The two withdrawn counters must not come back by halves: a row deleted from the page while the
    # field stays in the API is the F-M218 file-pair trap in reverse, and it leaves a counter that
    # still counts and is never read.
    # Checked across EVERY carrier, not just the page: a withdrawal done by halves is the F-M218
    # file-pair trap in reverse — the row disappears from the table while the field survives in the
    # entity, the writer or the API, and the counter keeps counting with nobody reading it. Planted
    # and confirmed RED for the page AND for the API field separately (a page-only check passed the
    # planted API field, which is why this reads all four sources).
    withdrawn = {
        "type": ("movie/series", "TypeCorrectedByFileName", "TypeCorrected", "typeCorrected"),
        "year": ("without the year", "TmdbYearFilterMisses", "TmdbYearMisses", "tmdbYearMisses"),
    }
    src_ent = os.path.abspath(os.path.join(os.path.dirname(path), "..", "Data", "Entities.cs"))
    src_plg = os.path.abspath(os.path.join(os.path.dirname(path), "..", "Plugin.cs"))
    src_api = os.path.abspath(os.path.join(os.path.dirname(path), "..", "Api", "SubdlStatusController.cs"))
    carriers = {"page": html}
    if os.path.exists(src_ent):
        carriers["entity"] = open(src_ent, encoding="utf-8").read()
    if os.path.exists(src_plg):
        carriers["writer"] = open(src_plg, encoding="utf-8").read()
    if os.path.exists(src_api):
        carriers["api"] = open(src_api, encoding="utf-8").read()
    for label, needles in withdrawn.items():
        rows_left = [o for o in order if any(n in o for n in needles[:1])]
        live = sorted({(name, f) for name, src in carriers.items()
                       for f in needles[1:] if f in src})
        check("the withdrawn %s counter is gone from every carrier" % label,
              not rows_left and not live,
              "row(s) %s / still wired in %s" % (rows_left, live))
    # F-M311/F-M313: the last two rows are the only ones that are NOT a direction — they count FILES
    # the seeder edited on disk, and the seeder serves both directions. A direction prefix on either
    # would claim a scope the count does not have. Asserted because both sit among prefixed rows and
    # "Downloads:" is the tempting thing to write.
    check("the language-code row carries no direction prefix",
          "Media files: language codes added" in order,
          "row missing or renamed: %s" % order)
    check("the loose-subtitle row carries no direction prefix either",
          "Loose subtitles: language codes added" in order,
          "row missing or renamed: %s" % order)
    # F-M218 (user decision 07.10.2026): the upload row must NOT say "after being fetched" — the
    # upload direction fetches nothing, every increment sits on a skip path ahead of the API call.
    # Asserted because the wording was copied from the download row and reads as plausible: nothing
    # breaks, the number is right, only the sentence is false. Also asserted positively, so a rename
    # to something else has to be a deliberate act rather than a silent drift.
    check("the upload row says discarded before transfer, not fetched",
          "Uploads: discarded before transfer" in order,
          "row missing or reverted: %s" % [o for o in order if o.startswith("Uploads:")])
    check("and no upload row claims a fetch",
          not any(o.startswith("Uploads:") and "fetch" in o for o in order),
          "an upload row still speaks of fetching: %s" % [o for o in order if o.startswith("Uploads:")])
    check("the download row keeps its own wording, fetched is true there",
          "Downloads: rejected after being fetched" in order,
          "download row missing or renamed: %s" % [o for o in order if o.startswith("Downloads:")])

    # F-M322 (operator correction 08.10.2026): the row labels are SHORT. "direction" is written
    # "dir" and the year row names the year, not the tag it is carried on. Asserted as a pair of
    # negatives, because the long forms are the ones a copy-paste re-introduces and the table
    # renders perfectly either way — the only thing that changes is that it no longer fits.
    check("the year row says year, not year tag",
          not any("year tag" in o for o in order),
          "a row still says 'year tag': %s" % [o for o in order if "year" in o])

    # F-M322 (operator order 08.10.2026): the value column is sized for SIX digits and right-aligned,
    # so every number shares one edge. Asserted, because the failure is invisible in a screenshot:
    # with max-content alone the column takes the width of the widest number PRESENT, so the row of
    # numbers looks aligned on the day it is looked at and shifts as the counters grow.
    check("the statistics value column fits six digits and aligns right",
          "min-width: 6ch" in html and "text-align: right" in html
          and "#StatsTable .subdl-stat-value" in html,
          "the value column is not sized for six digits")

    check("the two language-code rows read the fields the API sends",
          "LanguageCodesAllocated" in html and "LooseSubtitlesRenamed" in html,
          "a row reads a field name the API does not publish")

    # Every row must name a field the plugin API actually returns.
    api_src = os.path.join(os.path.dirname(path), "..", "Api", "SubdlStatusController.cs")
    api_src = os.path.abspath(api_src)
    if os.path.exists(api_src):
        api = open(api_src, encoding="utf-8").read()
        api_fields = set(re.findall(r"^\s+(\w+) = row\.(\w+),", api, re.M))
        api_names = {f for _, f in api_fields}
        used = set(re.findall(r"s\.(\w+) \|\| 0", html))
        missing = sorted(used - api_names)
        check("every table row reads a field the API returns", not missing,
              "not in the API: %s" % ", ".join(missing))
        # And the fit counter must be a REAL field, not a name invented in the page.
        check("the fitted-to-audio row reads a field the API sends",
              "FittedToAudio" in api_names,
              "FittedToAudio missing from the Stats endpoint")
        # F-M311: same for the language-code counter — a name invented in the page would render a
        # permanent 0 and look like a feature that never fires.
        check("the language-code row reads a field the API sends",
              "LanguageCodesAllocated" in api_names,
              "LanguageCodesAllocated missing from the Stats endpoint")

        # The reset must zero EVERY counter the API publishes. This is a source cross-check
        # because the failure is silent in the worst way: add a column, forget the reset, and the
        # old total keeps being displayed next to a reset button that claims to have cleared it —
        # the same drift the counters were moved out of the configuration XML to remove.
        ent = os.path.abspath(os.path.join(os.path.dirname(path), "..", "Data", "Entities.cs"))
        plg = os.path.abspath(os.path.join(os.path.dirname(path), "..", "Plugin.cs"))
        if os.path.exists(ent) and os.path.exists(plg):
            ents = open(ent, encoding="utf-8").read()
            row = ents[ents.index("class StatusStatsEntity"):]
            row = row[:row.index("\n}")]
            counters = re.findall(r"public long (\w+) \{ get; set; \}", row)
            plugin = open(plg, encoding="utf-8").read()
            body = plugin[plugin.index("public Data.StatusStatsEntity ResetStatusStats()"):]
            body = body[:body.index("db.StatusStats.Upsert(row);")]
            not_zeroed = sorted(c for c in counters if ("row.%s = 0;" % c) not in body)
            check("the reset zeroes every counter on the status row", not not_zeroed,
                  "not zeroed: %s" % ", ".join(not_zeroed))
            # And the writer's early-out must know about every PARAMETER: a run that only fitted
            # subtitles would otherwise be dropped as "nothing happened" and its count lost.
            # Match on the PARAMETERS, not the entity field names — the two differ on purpose
            # (there is no field whose name equals its parameter), and comparing field names to
            # parameter names reports a complete guard as broken.
            add = plugin[plugin.index("public void AddStatusCounters("):]
            add = add[:add.index("db.StatusStats.Upsert(row);")]
            sig = add[:add.index(")")]
            params = re.findall(r"long (\w+)(?: = 0)?", sig)
            guard = add[add.index("if ("):add.index("return; // nothing happened")]
            not_guarded = sorted(p for p in params if p not in guard)
            check("the counter writer's early-out covers every counter", not not_guarded,
                  "missing from the guard: %s" % ", ".join(not_guarded))
            # And every parameter must actually reach a column: a parameter the guard checks but
            # the writer never stores silently accepts work and reports nothing.
            body_after = add[add.index("row.Uploaded"):]
            unwritten = sorted(p for p in params
                               if p not in body_after and p not in ("uploaded", "downloaded"))
            check("every counter parameter is written to the row", not unwritten,
                  "never written: %s" % ", ".join(unwritten))
        else:
            check("Entities.cs and Plugin.cs are reachable for the reset check", False)

        # The TWO endpoints must carry the SAME fields. The GUI feeds the reset response straight
        # back into the renderer, so a field missing from the reset POST renders "undefined" right
        # after a reset — this happened before with the quality counters, one endpoint over.
        # Compare the actual SETS, not a sample count: "count == 2" also holds when one block has
        # a field twice and the other misses it.
        # Anchor on the COUNTER fields, not on every Ok(new {...}): the controller also returns
        # DTOs that carry no counters (status checks, directory checks), which made a plain scan
        # report four blocks and never the two that matter.
        blocks = []
        for m in re.finditer(r"return Ok\(new\s*\{(.*?)\}\);", api, re.S):
            body = m.group(1)
            fields = set(re.findall(r"^\s+(\w+) = row\.(\w+),", body, re.M))
            if {f for _, f in fields} & {"Uploaded", "Downloaded"}:
                blocks.append(fields)
        check("the status controller exposes two counter DTOs", len(blocks) == 2,
              "found %d" % len(blocks))
        if len(blocks) == 2:
            only_stats = sorted(blocks[0] - blocks[1])
            only_reset = sorted(blocks[1] - blocks[0])
            check("the reset response and the stats response carry the same fields",
                  not only_stats and not only_reset,
                  "missing in reset: %s | missing in stats: %s"
                  % (", ".join(f for _, f in only_stats) or "-",
                     ", ".join(f for _, f in only_reset) or "-"))
    else:
        check("the status controller is reachable for the field cross-check", False, api_src)

    # F-M324: the renderer exists ONCE. It used to be duplicated in configPage.js, and the two
    # copies had to be asserted IDENTICAL because a row added to one only would render in one
    # embedding and not the other. There is no second copy any more, so the check that remains is
    # that the surviving one carries the renderer at all — and that no second file reappears.
    check("the page carries the statistics table renderer",
          "StatsTable" in html and "s.FittedToAudio" in html)
    check("no second copy of the page script exists",
          not os.path.isfile(os.path.join(os.path.dirname(path), "configPage.js")))

    # ---- 6h. F-M322: the auto-sync's own light and the switch wording ----
    # The operator asked for the alignment's status as its own readout, both in the Workers list and
    # under the download switch, and for the switch to say what it does in plain words. Asserted on the
    # SOURCE because all three failures are silent: a light wired to a worker the API does not publish
    # shows "never" forever, a second fetch for the mirrored light could show a different run than the
    # list right above it, and a switch whose sentence was left on the old wording still renders.
    #
    # The two source files below are the authorities the page must agree with: the worker registry
    # carries the light's WORDS, the pipeline carries its NUMBERS. Read here rather than assumed,
    # because a page that agrees with a stale copy of either is the silent failure this section exists
    # to catch — and a renamed worker or counter would otherwise render a permanent "never"/0.
    worker_src = os.path.abspath(os.path.join(os.path.dirname(path), "..", "Registry", "WorkerRunRegistry.cs"))
    pipeline_src = os.path.abspath(os.path.join(os.path.dirname(path), "..", "Pipeline", "DownloadPipeline.cs"))
    if os.path.exists(worker_src):
        worker_src = open(worker_src, encoding="utf-8").read()
    else:
        worker_src = ""
        check("the worker registry is reachable for the light's word check", False, worker_src)
    if not os.path.exists(pipeline_src):
        check("the download pipeline is reachable for the counter check", False, pipeline_src)

    check("the download switch names the spoken track",
          "Automatically synchronize subtitle to spoken track" in html,
          "switch wording not updated")
    check("no page still carries the old switch wording",
          "Correct subtitle timing" not in html,
          "old wording left behind")

    # F-M322 (operator correction 08.10.2026): the worker row and the statistics row spell the same
    # thing the same way — "Auto-Sync". Asserted POSITIVELY and as a negative pair, because the two
    # spellings drifted apart once already ("Autosync" here, "Auto-Synch" there) and neither breaks
    # the page: one word for one worker is the point.
    check("the auto-sync's row is registered as a worker of its own",
          "AutoSyncWorkerKey" in worker_src and '"Auto-Sync"' in worker_src,
          "no Auto-Sync worker registered")
    check("the worker row is not spelled Autosync",
          '"Autosync"' not in worker_src,
          "the worker row still carries the old spelling")
    check("the postprocessing row is named for the upload direction",
          '"Upl. Postproc."' in worker_src and '"Postproc."' not in worker_src,
          "postprocessing row not renamed")

    # F-M322 (operator order 08.10.2026, corrected the same day): the auto-sync's light lives in the
    # WORKERS LIST and nowhere else. The first version also mirrored it under the download switch;
    # the operator struck that — one readout, one place. Asserted as an ABSENCE, because a mirror is
    # exactly the kind of thing that gets helpfully re-added and the page still renders either way:
    # no mirrored markup (id/class), and no second reader for it in the script.
    check("the auto-sync's light is NOT duplicated under the download switch",
          "SubdlAutosyncLight" not in html and "SubdlAutosyncOutcome" not in html
          and "SubdlAutosyncStatus" not in html,
          "a second auto-sync readout is still wired into the page")
    check("no second reader for the auto-sync light remains in the script",
          "SubdlAutosyncWhen" not in html,
          "the mirrored light's script survived the markup")

    # The statistics row says what it counts, and it counts the FILES THE RUN MOVED — the operator's
    # order is that only successful auto-syncs are measured, so a "no proven gain" file (F-M321) must
    # not reach this counter. Asserted as a positive/negative pair on the pipeline source.
    check("the statistics row is named for the auto-sync",
          "Downloads: Auto-Sync" in order,
          "row missing or renamed: %s" % order)
    if os.path.exists(pipeline_src):
        pipe = open(pipeline_src, encoding="utf-8").read()
        # Find the refusal branch and require that the ALREADY-GOOD case does not touch FittedToAudio.
        branch = pipe[pipe.index("if (goodAsDownloaded)"):]
        branch = branch[:branch.index("summary.Downloaded++")]
        check("an already-good file is not counted as a successful alignment",
              "AlreadyGoodAsDownloaded++" in branch and "FittedToAudio++" not in branch,
              "the already-good case still bumps the alignment counter")
        check("the already-good case has its own counter",
              "public int AlreadyGoodAsDownloaded" in pipe,
              "no separate counter for files that needed no correction")
    else:
        check("the download pipeline is reachable for the counter check", False, pipeline_src)

    # ---- 6b2. F-M322/T134: the auto-sync row survives the UPLOAD direction ----
    # The row is written from RunDirectionAsync, which runs for BOTH directions; the download's summary
    # is the upload run's `null`. The first version dereferenced it straight away, and the NRE it threw
    # landed AFTER a green run — the upload's catch painted that direction red and MarkCycleFailure
    # repainted the download with it, so a cycle with 3 uploads "failed" on both lamps (live 08.10.2026
    # 18:21, prod .201 and test .202 alike). Asserted on the SOURCE because the throw is invisible in
    # the page: both lamps render either way, the wrong one just says "Object reference not set".
    # A guard that is only documented is exactly what a later refactor removes again.
    disp = os.path.abspath(os.path.join(os.path.dirname(path), "..", "ScheduledTasks", "SubdlEventDispatcher.cs"))
    if os.path.exists(disp):
        dsrc = open(disp, encoding="utf-8").read()
        check("the auto-sync row takes a nullable download summary",
              re.search(r"private void RecordAutoSyncRow\(PluginConfiguration config,\s*Pipeline\.DownloadRunSummary\?\s+downSummary\)", dsrc) is not None,
              "the parameter is not nullable — an upload run hands over null")
        # The guard must sit BEFORE the first use. Asserted as an ORDER, not as presence: a null check
        # placed after the first dereference compiles, reads correctly and changes nothing.
        m_body = re.search(r"private void RecordAutoSyncRow\([^)]*\)\s*\{(.*?)\n    \}", dsrc, re.S)
        body = m_body.group(1) if m_body else ""
        pos_guard = body.find("if (downSummary == null)")
        pos_use = body.find("downSummary.IsDryRun")
        check("the null guard precedes every use of the download summary",
              pos_guard != -1 and pos_use != -1 and pos_guard < pos_use,
              "guard at %d, first use at %d" % (pos_guard, pos_use))
        # And the false CLAIM must stay gone. Scoped to the parameter's own doc line, not to the whole
        # file: the explanation of why the guard exists names the old wording on purpose, and a
        # check that forbids the phrase outright would forbid documenting the defect.
        m_doc = re.search(r'<param name="downSummary">([^<]*)</param>', dsrc)
        doc = m_doc.group(1) if m_doc else ""
        check("the auto-sync row's doc no longer claims a non-null summary",
              doc != "" and "never null" not in doc,
              "the false non-null claim is back in the param doc")
    else:
        check("the dispatcher is reachable for the auto-sync null check", False, disp)

    # ---- 6c. F-M309/T124: the fit's logging split ----
    # Normal must show WHETHER the fit ran and HOW MUCH it did; Verbose must show WHICH audio track
    # and WHICH subtitle. Asserted on the source because the levels are what make the feature
    # falsifiable in the field: a fit that leaves no Normal trace cannot be told apart from a fit
    # that never ran, and F-M286 requires every counter to be printed somewhere.
    dl = os.path.abspath(os.path.join(os.path.dirname(path), "..", "Pipeline", "DownloadPipeline.cs"))
    if os.path.exists(dl):
        src = open(dl, encoding="utf-8").read()
        check("the run START line names the fit switch at Normal",
              "audio fit={Fit}" in src)
        check("the run DONE line carries the fit counter at Normal",
              "fitted to audio" in src)
        # The track line must be per-item (Verbose) AND gated on the switch: with the fit off
        # nothing reads the audio, so a line claiming it did would be false.
        m = re.search(r"if \(_config\.QaDownloadAutoSync\)\s*\{\s*LogUtil\.PerItem\([^)]*fit reads audio track", src, re.S)
        check("the per-file track line sits behind the fit switch", m is not None)
        check("the per-file track line is a per-item line, not a Normal one",
              m is not None and "LogUtil.PerItem" in m.group(0)
              and "LogUtil.Normal" not in m.group(0))
        # And the dead branch must stay gone: a second branch under the same threshold changed
        # nothing while suggesting a policy that did not exist.
        check("no dead second branch for a missing ffmpeg",
              src.count('syncResult.Reason == "ffmpeg not available"') == 0)
        # The HI track is a fitted subtitle too — its increment must exist, or the counter
        # under-reports exactly the pool where the drift lives.
        check("the HI fit counts into the same counter",
              src.count("summary.FittedToAudio++;") == 2)
    else:
        check("DownloadPipeline.cs reachable for the logging check", False, dl)

    # ---- 6d. F-M311: the seeder's counter must not be lost before a run reports it ----
    # The scan does not always get a run (no arrivals, empty queue), and it has edited files either
    # way — so the pending counter must ACCUMULATE across scans and be cleared exactly once, by the
    # writer. An assignment here loses every number produced by a scan whose direction ended without
    # a run: the work would be on disk and absent from the statistics forever. Asserted on the
    # source because the visible symptom is only ever "the counter reads 0".
    disp = os.path.abspath(os.path.join(os.path.dirname(path), "..", "ScheduledTasks", "SubdlEventDispatcher.cs"))
    if os.path.exists(disp):
        dsrc = open(disp, encoding="utf-8").read()
        check("the seeder's language-code count accumulates across scans",
              "_pendingLanguageCodesAllocated += snapshot.LanguageCodesAllocated;" in dsrc)
        check("and is consumed exactly once, by the statistics writer",
              dsrc.count("_pendingLanguageCodesAllocated = 0;") == 1
              and dsrc.count("_pendingLanguageCodesAllocated += ") == 1)
    else:
        check("SubdlEventDispatcher.cs reachable for the counter check", False, disp)

    # ---- 6e. F-M312/F-M313: a dry run must not RENAME a loose subtitle either ----
    # The container rewrite was already suppressed (F-M263), but this rename had NO dry-run guard:
    # the seeder contained not one DryRun check, and the gate's switch covers the container only — so
    # a dry run moved the user's files while reporting that it writes nothing (F-M22). Asserted on the
    # source because the symptom is silent and rare: it only shows on an install that has unlabelled
    # sidecars AND a dry run armed, and nothing in the log contradicts the report.
    sd = os.path.abspath(os.path.join(os.path.dirname(path), "..", "ScheduledTasks", "SubdlSeeder.cs"))
    if os.path.exists(sd):
        sdsrc = open(sd, encoding="utf-8").read()
        # The predicate must exist AND cover BOTH directions: the seeder serves no single direction,
        # so tying the write to one switch would let the other dry run edit the library (F-M22).
        # Matched on the PREDICATE BODY, not on the call site: an earlier form of this check tested a
        # condition that was true for any file containing the call — it could never fail.
        pred_start = sdsrc.find("private static bool DryRunActive()")
        pred_body = sdsrc[pred_start:sdsrc.find(";", pred_start)] if pred_start >= 0 else ""
        check("the seeder has a dry-run predicate covering both directions",
              pred_start >= 0 and "DryRun" in pred_body and "DownloadDryRun" in pred_body,
              "DryRunActive must consult BOTH switches: %r" % pred_body[:70])
        check("and the rename consults it BEFORE moving the file",
              "if (DryRunActive())" in sdsrc
              and sdsrc.index("if (DryRunActive())") < sdsrc.index("File.Move(loosePath, target)"),
              "the dry-run guard must sit above the move")
        # The counter must count the MOVE, not the attempt: driving it off the path comparison would
        # count a refusal whose target happened to differ, and off the call itself a dry run.
        check("the rename counter is driven by the move, not the attempt",
              "out bool renamed" in sdsrc and "renamed = true;" in sdsrc
              and sdsrc.index("File.Move(loosePath, target)") < sdsrc.index("renamed = true;"),
              "renamed must be set at the move")
        check("the loose row counts renames, not registry rows",
              ".Renamed;" in sdsrc and "snapshot.LooseSubtitlesRenamed += ObserveSidecarFacts(mediaPath).Renamed;" in sdsrc,
              "the statistics row must read Renamed, not Rows")
    else:
        check("SubdlSeeder.cs reachable for the dry-run check", False, sd)

    # ---- 6g. F-M314: the rename hangs off the SAME switch as the container write ----
    # Two switches for one act — allocating a missing language code — let the operator ask for
    # allocation and still get a library that reads as unlabelled. The rename used to sit on
    # `UploadResolveUnd`, the UPLOADER's switch on the Upload tab, which never owned a seeder pass
    # serving both directions. Asserted on the source: the wrong switch still compiles and still
    # works, so only a reader would notice.
    if os.path.exists(sd):
        sdsrc2 = open(sd, encoding="utf-8").read()
        # The gate for the RENAME specifically: take the block that resolves an unlabelled sidecar.
        rename_from = sdsrc2.find("bool wasUnlabeled = looseLang == null;")
        rename_to = sdsrc2.find("string normalized =", rename_from)
        rename_gate = sdsrc2[rename_from:rename_to] if rename_from >= 0 and rename_to > rename_from else ""
        check("the sidecar rename is gated by the allocation switch",
              "AllocateMissingLanguageCodes != true" in rename_gate,
              "the rename must hang off AllocateMissingLanguageCodes: %r" % rename_gate[-160:])
        check("and no longer by the uploader's und switch",
              "if (config?.UploadResolveUnd" not in rename_gate,
              "UploadResolveUnd still gates the rename")
        check("the container path and the rename agree on the switch",
              sdsrc2.count("config?.AllocateMissingLanguageCodes != true") >= 2,
              "container and rename must consult the same switch")

    # ---- 6f. F-M313: the second seeder counter follows the same accumulate-and-consume rule ----
    if os.path.exists(disp):
        dsrc2 = open(disp, encoding="utf-8").read()
        check("the loose-subtitle count accumulates across scans",
              "_pendingLooseSubtitlesRenamed += snapshot.LooseSubtitlesRenamed;" in dsrc2)
        check("and is consumed exactly once",
              dsrc2.count("_pendingLooseSubtitlesRenamed = 0;") == 1
              and dsrc2.count("_pendingLooseSubtitlesRenamed += ") == 1)

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

    # ---- F-M324 (operator order 08.10.2026): the download tab reads in the order the cycle works ----
    # "Continue after daily limit" directly above "Follow-up rounds for downloads", and BOTH above
    # the quality-gates heading, where "Automatically synchronize" used to sit. Asserted as ORDER,
    # not as presence: all three controls render perfectly in any sequence, so a presence check
    # passes on the layout the operator just corrected.
    dl_tab = html.rfind('data-tab-content="download"')
    dl_end = html.find('data-tab-content=', dl_tab + 1)
    dl_section = html[dl_tab:dl_end if dl_end != -1 else len(html)]
    pos_limit = dl_section.find('id="DownloadContinueAfterLimit"')
    pos_fu = dl_section.find('id="FollowUpRoundsDownload"')
    pos_sync = dl_section.find('id="QaDownloadAutoSync"')
    pos_gates = dl_section.find("Quality gates (before download save)")
    check("download: Continue after daily limit comes before Follow-up rounds",
          pos_limit != -1 and pos_fu != -1 and pos_limit < pos_fu,
          "limit=%d follow-up=%d" % (pos_limit, pos_fu))
    check("download: Follow-up rounds comes before Automatically synchronize",
          pos_fu != -1 and pos_sync != -1 and pos_fu < pos_sync,
          "follow-up=%d sync=%d" % (pos_fu, pos_sync))
    check("download: Follow-up rounds sits ABOVE the quality gates",
          pos_fu != -1 and pos_gates != -1 and pos_fu < pos_gates,
          "follow-up=%d gates=%d" % (pos_fu, pos_gates))
    check("download: Automatically synchronize sits ABOVE the quality gates",
          pos_sync != -1 and pos_gates != -1 and pos_sync < pos_gates,
          "sync=%d gates=%d" % (pos_sync, pos_gates))
    check("download: exactly one Automatically synchronize control",
          dl_section.count('id="QaDownloadAutoSync"') == 1,
          "count=%d" % dl_section.count('id="QaDownloadAutoSync"'))

    # ---- F-M324: one worker row, one worker's own result ----
    # The green branch handed the SEEDER's string to every direction row, so `Seeder` and `Upload`
    # carried the identical sentence on the live endpoint (measured on prod) while the upload row
    # never stated its own result. Asserted as a negative pair, because both spellings render.
    worker_src = read_source("Registry", "WorkerRunRegistry.cs")
    if worker_src:
        check("DescribeCycle's green branch reports the DIRECTION's own detail",
              "string.IsNullOrWhiteSpace(directionDetail) ? \"cycle finished\" : directionDetail!"
              in worker_src)
        check("and no longer hands the seeder's detail to the direction row",
              "IsNullOrWhiteSpace(seederDetail) ? \"cycle finished\" : seederDetail!"
              not in worker_src)
    disp_src = read_source("ScheduledTasks", "SubdlEventDispatcher.cs")
    if disp_src:
        check("each direction's row gets its OWN run summary",
              "DescribeDirectionRun(upload" in disp_src)
        check("and the detail is set before the quota stop may overwrite it",
              disp_src.find("SetDirectionDetail(upload,") != -1
              and disp_src.find("SetDirectionDetail(upload,") < disp_src.rfind("CleanupDirectionQueue(upload,"))
        check("the seeder row sums the cycle's legs instead of re-writing per leg",
              "_seedQueuedUp +=" in disp_src and "_seedLegsScanned++" in disp_src)
        # A row that examines items and refuses them all must not read as "nothing to do": measured
        # live on prod, five files rejected for a missing id produced that exact claim.
        # The upload's refusal paths increment FilesSkipped, NOT SkippedItems (measured on the
        # source: SkippedItems is written on two paths only). Asserting the wrong field is how the
        # first attempt at this fix passed its check while the live row still said nothing.
        check("the direction row names the skipped count",
              "upSummary.FilesSkipped > 0" in disp_src and "downSummary.SkippedItems > 0" in disp_src)
        # Anchored on the RETURN, not on the phrase: the words survive in the comments that quote
        # the old behaviour, so a substring check fails on a correct change. What must be gone is
        # the fallback that produced the claim.
        # The fallback IS "nothing to do" again, and correctly so: it is reachable only when EVERY
        # counter is zero, which now includes the skipped count. The earlier assertion banned the
        # phrase outright; that was wrong once the skipped counter was actually wired.
        check("the empty-run fallback exists for a run with no counters at all",
              'return parts.Count == 0 ?' in disp_src)
        check("a skipped leg does not overwrite a scan that ran this cycle",
              "_seedLegsScanned > 0" in disp_src)

        # ---- F-M330 (operator order 08.10.2026): one lit lamp, for the worker that is working ----
        # "seeder gets blue only, download only download, upload only upload — as it is successive."
        # Measured live on prod before the fix: the Seeder scanned while Download AND Upload were
        # already blue, both started 19:12:52 by the same cycle. The cycle marked both directions up
        # front, so a lamp claimed work that had not begun.
        check("the cycle no longer lights both directions up front",
              "RecordArrivalDirectionStart(dir)" not in disp_src,
              "the up-front start is what lit two lamps for one worker")
        check("and cannot do so through the old helper either",
              "private void RecordArrivalDirectionStart" not in disp_src)
        check("a direction marks ITSELF running when its own run begins",
              "MarkDirectionRunning(upload)" in disp_src)
        check("that mark sits in the direction's own run, not in the cycle",
              disp_src.find("MarkDirectionRunning(upload)") > disp_src.find("private async Task RunDirectionAsync"))
        check("the direction closes its own row start-to-finish",
              "FinishDirectionRow(upload)" in disp_src
              and "DirectionRowWritten(bool upload)" in disp_src)
        check("and closes it in a finally, so a failure leaves no lamp hanging",
              disp_src.find("FinishDirectionRow(upload)") > disp_src.find("finally"))

    task_src = read_source("ScheduledTasks", "SubdlUploadTask.cs")
    task_dl = read_source("ScheduledTasks", "SubdlDownloadTask.cs")
    if task_src and task_dl:
        # The wait-only tasks start a cycle and WAIT for it: marking their direction running there
        # lit a lamp for the seeder's work. They still own the rows of directions that never ran.
        check("the upload task no longer marks its row running at cycle start",
              "RecordWorkerStart(plugin);" not in task_src)
        check("the download task no longer marks its row running at cycle start",
              "RecordWorkerStart(plugin);" not in task_dl)
        check("and both leave a row the direction wrote itself alone",
              "DirectionRowWritten(upload: true)" in task_src
              and "DirectionRowWritten(upload: false)" in task_dl)

    # ---- F-M331 (operator order 08.10.2026): a spent allowance acts like the download switch ----
    # "Api limit oder download limit voll, downloader startet erst garnicht." Measured on prod the
    # same day: the 50/day download limit was spent from 05:59, yet four further cycles each walked
    # ~1000 queued items, spent the day's SEARCH allowance (59 -> 194) and saved nothing.
    #
    # The gate lives in the DISPATCHER, at the two places the download SWITCH is consulted, and the
    # seeder only takes a flag: the seeder fills queues and does not decide policy. Operator
    # correction 08.10.2026 — an earlier version put the API probe INSIDE the seeder and was
    # rejected, so the absence of that probe is asserted too.
    #
    # Asserted as a PAIR because either half alone is the defect: a probe nobody consults changes
    # nothing, and a flag nobody lowers never fires.
    seed_src = read_source("ScheduledTasks", "SubdlSeeder.cs")
    if seed_src:
        check("the seeder does NOT query the API itself (it only reads a flag)",
              "ReadQuotaAsync" not in seed_src and "SubdlApiClient" not in seed_src,
              "the seeder fills queues; asking the API is the dispatcher's job")
        check("the download queue is gated by that flag, like the download switch",
              "&& downloadAllowed" in seed_src
              and "bool downloadAllowed = true" in seed_src,
              "a spent limit must keep items out of the queue the way switching off does")
    if disp_src:
        # Assert the EXACT call site, not the method name: `if (false) …ReadDownloadAllowanceAsync(…)`
        # contains every one of those words and disables the probe completely. Measured: the
        # word-level version stayed GREEN under exactly that mutation (plant 6).
        PROBE_CALL = "_downloadAllowanceHeld = await ReadDownloadAllowanceAsync(configForCycle).ConfigureAwait(false);"
        check("the dispatcher reads the allowance once per cycle",
              PROBE_CALL in disp_src)
        check("and the probe is not disabled behind a guard",
              "if (false)" not in disp_src.split("ReadDownloadAllowanceAsync")[0][-400:])
        check("the probe reads BOTH counters from the server",
              "api.ReadQuotaAsync(forDownload: true" in disp_src
              and "api.ReadQuotaAsync(forDownload: false" in disp_src,
              "each allowance stops a download run on its own path")
        # Read the CATCH BLOCK's own returns, bounded by its `catch` and the `finally` that closes
        # it. An earlier version anchored on the log line and missed a `return "…"` inserted BEFORE
        # it; plant 5 does exactly that and stayed GREEN against the phrase-level check.
        probe_at = disp_src.find("private async Task<string?> ReadDownloadAllowanceAsync")
        catch_at = disp_src.find("catch (Exception ex)", probe_at)
        finally_at = disp_src.find("finally", catch_at)
        catch_block = disp_src[catch_at:finally_at] if -1 < probe_at < catch_at < finally_at else ''
        catch_returns = [ln.strip() for ln in catch_block.splitlines() if ln.strip().startswith('return ')]
        check("an unreadable counter is NOT treated as spent",
              catch_returns == ['return null;'],
              f"the catch must only return null, found {catch_returns}")
        check("the quota probe failure is logged, not silent",
              "quota probe failed" in disp_src)
        # The two sites the SWITCH occupies. Asserted against the switch's own expression so the
        # pair cannot drift apart: a limit that gates the fill but not the run walks the leftovers
        # into the same wall, and prod had 1144 items already queued.
        check("the RUN is refused at the same place as the download switch",
              "heldByAllowance" in disp_src
              and "!config.DownloadEnabled || heldByAllowance" in disp_src,
              "gating only the fill still walks what earlier cycles queued")
        check("and the held-back direction still reports why, deferred",
              "Outcome.Deferred, _downloadAllowanceHeld" in disp_src)
        check("the flag is handed to the seeder, so the queue stays empty too",
              "downloadAllowed: _downloadAllowanceHeld == null" in disp_src)

    # ---- F-M334 (operator order 09.10.2026): the dry run escapes the DOWNLOAD brake, not the search one ----
    # "Also weiter wenn download dry run test ein" — the test could not be started: the probe read
    # 50/50 and held back the one run whose entire purpose is to report what WOULD happen.
    #
    # Only the FILE brake may be skipped. A dry run DOES search every item, spends search quota and
    # draws every line it prints from those answers, so the search allowance must keep holding it —
    # an exception that lifted both would let a dry run walk a library with the search quota spent
    # and print nothing, which is the same wall F-M331 was written against.
    #
    # Asserted by POSITION inside the probe, and the closing brace is the point: it proves the
    # download read sits INSIDE the guard while the search read sits OUTSIDE it. Two separate
    # word-level checks would stay green against `if (!config.DownloadDryRun) { }` around both reads.
    if disp_src:
        _probe = disp_src[probe_at:finally_at] if probe_at >= 0 and finally_at > probe_at else ''
        _guard = _probe.find("if (!config.DownloadDryRun)")
        _down = _probe.find("api.ReadQuotaAsync(forDownload: true")
        _search = _probe.find("api.ReadQuotaAsync(forDownload: false")
        # INDENTATION carries the answer here, not brace counting: a brace-level test counts the
        # braces of the `return string.Format(...)` blocks too and stayed red on correct code
        # (measured). The guard sits one level ABOVE the download read and exactly level WITH the
        # search read, which is precisely the claim: the one read is inside it, the other is not.
        def _indent_of(marker: str) -> int:
            for ln in _probe.splitlines():
                if marker in ln and ln.strip() and not ln.strip().startswith("//"):
                    return len(ln) - len(ln.lstrip())
            return -1
        _guard_ind = _indent_of("if (!config.DownloadDryRun)")
        _down_ind = _indent_of("api.ReadQuotaAsync(forDownload: true")
        _search_ind = _indent_of("api.ReadQuotaAsync(forDownload: false")
        check("the dry run skips the DOWNLOAD brake",
              _guard_ind >= 0 and _down_ind > _guard_ind and _guard < _down < _search,
              "without the guard the dry run cannot start when the limit is spent (measured: 50/50) "
              f"(indents guard={_guard_ind} download={_down_ind})")
        check("but the SEARCH brake still holds a dry run",
              _search_ind >= 0 and _search_ind == _guard_ind and _search_ind < _down_ind,
              "a dry run searches, spends search quota, and prints only what those answers carry "
              f"(indent search={_search_ind}, guard={_guard_ind})")
        # A decoy the position check could NOT see (measured): `if (!config.DownloadDryRun) { }`
        # followed by its own block compiles, keeps the guard's indentation AND leaves the download
        # read inside a block that always runs — the brake stays on and plant 5 stayed GREEN. The
        # guard must therefore be a BLOCK, not a statement that closes itself.
        check("the guard is a real block, not an empty decoy",
              not any("if (!config.DownloadDryRun) { }" in ln or "if (!config.DownloadDryRun){}" in ln
                      for ln in _probe.splitlines()),
              "a self-closing guard would exempt nothing and still satisfy a position check")
        check("the exception names the DOWNLOAD dry run, not the upload one",
              "!config.DownloadDryRun" in _probe and "!config.DryRun" not in _probe,
              "the two switches are separate settings; the wrong one would exempt the wrong mode")

    # ---- F-M335 (operator order 09.10.2026): the dry run's queue guard, and the Auto-Sync row's start ----
    # TWO defects the operator reported together, both live on prod.
    #
    # (1) "Wieso funktioniert der stop download button nicht mehr" led to the queue, and the queue
    # exposed this: the DOWNLOAD outcome handler guarded on `_upInFlightDryRun` — the UPLOAD flag.
    # On a download run that flag is false, so a download dry run marked every item it had only
    # DESCRIBED as `Done`. Measured: a dry run wrote 7 `Done` rows, exactly the 7 items it printed a
    # DRY-RUN line for; the next real run skipped them. Nothing caught it because `_downInFlightDryRun`
    # was written and never READ, and no check looked at either flag.
    #
    # Asserted from BOTH sides: each handler must consult its OWN flag, and each flag must have a
    # READER. The second half is the one that would have caught it — a set-and-never-read field is
    # invisible to a check that only greps the guard.
    if disp_src:
        _dl = disp_src.find("private void OnItemResult(List<QueueItem> queue")
        _ul = disp_src.find("private void ApplyOutcomeUpload(")

        def _code_only(text: str) -> str:
            # Comments MUST be dropped first: the fix's own comment names the wrong flag it replaced
            # ("this line used to read `_upInFlightDryRun`"), so a raw substring test failed on
            # CORRECT code — measured. The claim is about the executed guard, not about the prose.
            return "\n".join(ln for ln in text.splitlines()
                             if not ln.strip().startswith("//") and not ln.strip().startswith("///"))

        _dl_body = _code_only(disp_src[_dl:_ul]) if 0 <= _dl < _ul else ''
        _ul_body = _code_only(disp_src[_ul:_ul + 1800]) if _ul >= 0 else ''
        check("the DOWNLOAD dry run consults the DOWNLOAD flag",
              "_downInFlightDryRun) { break; }" in _dl_body
              and "_upInFlightDryRun" not in _dl_body,
              "reading the upload flag leaves a download dry run unguarded — measured 7 Done rows")
        check("the UPLOAD dry run consults the UPLOAD flag",
              "_upInFlightDryRun) { break; }" in _ul_body
              and "_downInFlightDryRun" not in _ul_body,
              "each direction owns its own mode flag; the two switches are separate settings")
        for _flag, _setter in (("_downInFlightDryRun", "config.DownloadDryRun"),
                               ("_upInFlightDryRun", "config.DryRun")):
            _assigned = disp_src.count(f"{_flag} = {_setter}")
            _read = disp_src.count(f"if ({_flag})")
            check(f"{_flag} is both written and READ",
                  _assigned >= 1 and _read >= 1,
                  f"a flag written but never read is a mode that is not actually guarded "
                  f"(writes={_assigned}, reads={_read})")

    # (2) The Auto-Sync row was only ever FINISHED, never STARTED: every writer called Finish() for
    # the alignment while Start() was called for the seeder, both directions and all three refresh
    # tasks. `Finish` falls back to "now" only when Started is null, so the stale stamp survived —
    # prod showed a nine-hour run for 274 s of alignment. Asserted as the PAIR write/read on the row
    # key, in the one method that holds the download summary.
    if disp_src:
        _row_at = disp_src.find("private void RecordAutoSyncRow(")
        _row_body = disp_src[_row_at:disp_src.find("private void ApplyStatusCounters", _row_at)] if _row_at >= 0 else ''
        check("the Auto-Sync row is STARTED, not only finished",
              "runs.Start(Registry.WorkerRunRegistry.AutoSyncWorkerKey" in _row_body,
              "a row that is only closed keeps its previous start time (measured: 9 h for 274 s)")
        check("its start is written before every outcome branch",
              _row_body.find("runs.Start(Registry.WorkerRunRegistry.AutoSyncWorkerKey")
              < _row_body.find("if (downSummary.IsDryRun)") if
              _row_body.find("runs.Start(Registry.WorkerRunRegistry.AutoSyncWorkerKey") >= 0
              and _row_body.find("if (downSummary.IsDryRun)") >= 0 else False,
              "a branch that returns before the start leaves skipped/failed runs with a stale stamp")

    # ---- F-M50 (operator order 08.10.2026): ONE setting, TWO effects ----
    # "Die correction attempts bestimmen wieviele Kandidaten pro Sprache gesucht werden und wie oft die
    # autosync Schleife maximal Kandidaten zieht bis das Ergebnis passt. Aus 2 variables mach eine."
    #
    # The pair had drifted: the walk's cap was checked BEFORE the correction budget and counted the
    # same attempts, so at equal defaults the cap always fired first and the budget could never
    # trigger (replayed against the loop). Asserted as a PAIR again — a removed knob whose number
    # still sits in the loop is a hidden second variable, and a merged read without the removal
    # leaves the setting able to contradict it.
    pipe_src = read_source("Pipeline", "DownloadPipeline.cs")
    budget_src = read_source("Pipeline", "DownloadBudget.cs")
    if pipe_src and budget_src:
        check("the second candidate setting is gone from the pipeline",
              "DownloadMaxCandidatesPerLanguage" not in pipe_src,
              "a leftover read would be the second variable again")
        check("the search width is derived from the ONE correction-attempts setting",
              "SearchEarlyStopThreshold(\n                _config.DownloadQaRetryLimit)" in pipe_src,
              "the search must read the same value as the walk")
        check("the walk's limit reads that same value",
              "int walkLimit = Math.Max(0, _config.DownloadQaRetryLimit);" in pipe_src)
        check("the removed second cap is gone from the loop",
              "attempts >= downloadCap" not in pipe_src,
              "that cap fired first at equal values and hid the limit")
        check("the limit counts FITS only, never gate rejections",
              "if (walkLimit > 0 && refusalsThisRun >= walkLimit)" in pipe_src,
              "a gate rejection must not spend the Auto-Sync's budget (F-M320)")
        # Assert the SIGNATURE, not the word: `keepBest` still appears in the comment that explains
        # why the parameter was removed, and a word-level ban would forbid documenting history.
        # Measured: the word-level version went RED on a correct file.
        check("the helper takes one value and no second parameter",
              "SearchEarlyStopThreshold(int correctionAttempts)" in budget_src
              and "SearchEarlyStopThreshold(int correctionAttempts," not in budget_src
              and "EffectiveDownloadCap" not in budget_src,
              "a second parameter would be the dead keepBest knob again")
        check("0 stays 0 (no limit), never a threshold of zero",
              "correctionAttempts <= 0 ? 0 : correctionAttempts" in budget_src)

        # The Auto-Sync-OFF path: without a fit there is no verdict, so the abandon must not fire —
        # it used to end the walk on the FIRST candidate of every language.
        check("the abandon break requires the Auto-Sync to be ON",
              "if (_config.QaDownloadAutoSync && !huntForCorrection)" in pipe_src,
              "without the gate a run with the Auto-Sync off pulled ONE candidate per language")

    # ---- Manual run buttons for the two refresh tasks (operator order 08.10.2026) ----
    # "Für database refresh und oscache refresh hätte ich jetzt auch gerne einen manuellen knopf
    #  unter der jeweiligen Beschreibung."
    #
    # Each button sits under ITS OWN description and starts the task by NAME through the same
    # `subdlStartTask` helper the run buttons use. The point of the pair of assertions below is that
    # the button is a MANUAL TRIGGER and nothing else: F-M210 makes the cadence setting the only thing
    # that schedules a job, so a button that also touched `PruneMode`/`OshashRefresh` would be a
    # second, hidden scheduler.
    for btn_id, task_name, cfg_id in [
        ("RunDatabaseRefreshNow", "SubDL Scribe — Database Refresh", "PruneMode"),
        ("RunOshashRefreshNow", "SubDL Scribe — OSHash Cache Refresh", "OshashRefresh"),
    ]:
        check(f"the {cfg_id} section carries a manual run button",
              f'id="{btn_id}"' in html,
              "no button means the refresh can only wait for its schedule")
        # The handler must name THIS task and go through the shared starter.
        check(f"{btn_id} starts {task_name!r} through subdlStartTask",
              f"document.querySelector('#{btn_id}').addEventListener('click', function () {{ subdlStartTask('{task_name}'); }})" in html)
        # ...and the button must not write the cadence: it lives in the same container as the select,
        # so assert the handler body carries no assignment to the cadence field. This is the
        # F-M210 half — a manual fire must not reschedule anything.
        handler = html.split(f"document.querySelector('#{btn_id}').addEventListener", 1)[1].split("});", 1)[0]
        check(f"{btn_id} does not touch the {cfg_id} cadence",
              f"{cfg_id}" not in handler and "subdlStartTask" in handler,
              "a manual button that also sets the interval would be a hidden scheduler (F-M210)")

    # The LABEL follows the button (F-M210a, operator order 08.10.2026: "Never -> manual in beiden
    # Auswahlmenüs"). The VALUE must stay `Never` — the enum and every stored configuration key on it,
    # so a renamed label that also renamed the value would silently reset what a user had chosen.
    # Operator order 08.10.2026: the menu entry itself reads just "Manual" — no see-below sentence
    # tacked onto it. The button sits directly under its own description, so naming it in the option
    # text was noise the operator removed twice ("Never -> manual in beiden Auswahlmenüs", then
    # "Und nur manual im Auswahlmenü der 3 worker bitte, kein see below bla bla blah").
    for cfg_id, expected in [("PruneMode", "Manual"),
                             ("OshashRefresh", "Manual")]:
        check(f"{cfg_id} offers a Manual option (F-M210a)",
              f'<option value="Never">{expected}</option>' in html,
              "a service with a manual button must offer Manual, not Never")
        # Scoped to THIS select's own option list. A page-wide search is wrong twice over: the page
        # carries tab buttons AND a cycle-interval menu that legitimately uses value="Manual"
        # (measured — the page-wide version went RED on a correct file). Bound the block by the
        # select's own delimiters instead.
        # `split` takes LITERAL text, not a pattern: index the id, then take the block up to the
        # select's own closing tag.
        _i = html.find(f'id="{cfg_id}"')
        blk = html[_i:html.find('</select>', _i)] if _i != -1 else ''
        check(f"{cfg_id} keeps the stored VALUE Never",
              f'<option value="Never">{expected}</option>' in blk and '<option value="Manual"' not in blk,
              "renaming the value would reset an existing configuration")
    # The third job joined them (operator order 08.10.2026: "Knopf für upload Postprocessing habe ich
    # dann vergessen. Auch manual dann statt never") - so all three carry a button AND the Manual label,
    # and no service on this page offers Manual without a way to trigger it.
    check("postprocessing offers Manual (F-M210a)",
          '<option value="Never">Manual</option>' in html,
          "every service with a manual button must label its disabling option Manual")
    # Operator order 08.10.2026: the option reads EXACTLY "Manual" — no see-below sentence after it.
    # Asserted on all three menus by reading each select's own block, because the entry is the label the
    # operator keeps shortening and a suffix would otherwise only be caught by the VALUE check above,
    # which would report the wrong reason ("renaming the value") for a purely cosmetic change.
    for cfg_id in ("PruneMode", "OshashRefresh", "UploadPostprocessInterval"):
        _i = html.find(f'id="{cfg_id}"')
        blk = html[_i:html.find('</select>', _i)] if _i != -1 else ''
        check(f"{cfg_id} menu entry reads just 'Manual'",
              '<option value="Never">Manual</option>' in blk,
              "the label is the bare word; the button sits right below and needs no pointing at")

    # F-M60 REMOVED (operator order 08.10.2026: "Dann bitte file-retry weg in code und gui. Alleinige
    # Aufgabe database refresh."). A removal is invisible in a passing run, so it needs its own absence
    # assertions — and they must cover BOTH ends the order names: the page AND the code. Each one is
    # proven red by a planted negative control (see the suite's own report).
    check("the page no longer offers a file-missing retry field",
          "FileRetryLimit" not in html,
          "the field is gone; the Database Refresh owns the gone-file case now")
    check("no config load/save binding is left for the removed field",
          ".value = (function () { var v = parseInt(config.FileRetryLimit)" not in html
          and "config.FileRetryLimit =" not in html,
          "a binding for a removed element throws at load and takes other bindings with it")
    # Resolved UNCONDITIONALLY and asserted non-None first: a guard shaped like
    # `if src:` around the whole block turns every check below it into a no-op the day a file moves,
    # and a block that cannot run reads exactly like a block that passed. This suite already carries
    # that lesson from a page-wide search that went RED on a correct file.
    _up = read_source("Pipeline", "UploadPipeline.cs")
    _dl = read_source("Pipeline", "DownloadPipeline.cs")
    _cfg = read_source("Configuration", "PluginConfiguration.cs")
    _ref = read_source("ScheduledTasks", "SubdlDatabaseRefreshTask.cs")
    check("the F-M60 removal checks can reach their sources",
          None not in (_up, _dl, _cfg, _ref),
          "an unreadable source would silently skip every assertion below it")
    if True:
        check("the setting itself is gone from the configuration",
              "FileRetryLimit" not in _cfg,
              "a property no code reads would keep the field alive through any later re-add")
        check("neither pipeline carries the file-missing give-up any more",
              "_fileRetries" not in _up and "_fileRetries" not in _dl,
              "the give-up and its counter are what the order removes")
        # The tracker FILE was deleted, and that is asserted on the file, not on the pipelines: the
        # first version of this check searched the pipeline sources for the type name, which stays
        # present in the explanatory comments and would have gone RED on a correct tree. Read the
        # registry directory and require the file's absence.
        _reg_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..",
                                "Jellyfin.Plugin.SubdlSync", "Registry")
        _reg_files = sorted(f for f in os.listdir(_reg_dir) if f.endswith(".cs")) if os.path.isdir(_reg_dir) else []
        check("the tracker type is gone from the tree",
              "FileRetryTracker.cs" not in _reg_files,
              "a tracker kept 'just in case' becomes the next session's dead code")
        check("the neighbouring trackers are still there",
              "DownloadSearchTracker.cs" in _reg_files and "QaFailTracker.cs" in _reg_files,
              "a deletion that took its neighbours would pass the check above for the wrong reason")
        # The OWNERSHIP half: the removal is only correct if the refresh actually took the job over,
        # and took the fail-safe with it. Without this pair, deleting the mechanism would pass every
        # assertion above while leaving the gone-file case unowned — the exact failure the operator's
        # rule is about.
        check("the database refresh owns the gone-file sweep",
              "PruneMissingPathMediaAndSubtitles()" in _ref,
              "removing the pipeline give-up without an owner would leave vanished files uncleaned")
        check("the refresh's gone-file sweep sits behind the same fail-safe as its neighbours",
              "mediaRoots" in _ref and "RootsUsable(mediaRoots)" in _ref,
              "without the root gate an unmounted volume would delete live media state")

    # F-M333 (operator order 08.10.2026): "Only missing languages" gets a real second mode. ON is
    # unchanged (sidecar counts, embedded counts); OFF means ONLY our own recorded download closes a
    # pair, so every target language is fetched once even when the file already carries it — and the
    # fetched file must not overwrite the one already there.
    #
    # The label lost its parenthetical: the switch no longer describes "embedded count as present",
    # because with it OFF the embedded track is exactly what stops counting.
    check("the switch label drops the embedded parenthetical",
          "<span>Only missing languages</span>" in html
          and "embedded count as present" not in html,
          "the old suffix named the ON behaviour and contradicted the OFF one")
    _cov = read_source("Registry", "SubtitleCoverage.cs")
    if _cov:
        check("coverage has a mode that reads ONLY our own downloads",
              "onlyOwnDownloads" in _cov and "Data.SubtitleStatus.Downloaded" in _cov,
              "with the switch off, sidecars and embedded tracks must not close a pair")
        # `str.index` RAISES where the substring is missing, so the first version of this check
        # traced back instead of reporting — measured: renaming the mode made the suite die with a
        # ValueError and NO named failure, which reads like a broken harness rather than a defect.
        # Find once, assert non-negative, compare only then.
        _mode_at = _cov.find("if (onlyOwnDownloads)")
        _file_evidence_at = _cov.find("SidecarNaming.List(mediaPath)")
        check("the own-download mode RETURNS before the other evidence is collected",
              _mode_at >= 0 and _file_evidence_at >= 0 and _mode_at < _file_evidence_at,
              "collecting the file's own evidence first would let it close a pair again"
              if _mode_at >= 0 and _file_evidence_at >= 0
              else "the mode block or the file-evidence reader was not found at all")
    _pipe = read_source("Pipeline", "DownloadPipeline.cs")
    _seed = read_source("ScheduledTasks", "SubdlSeeder.cs")
    _ref2 = read_source("ScheduledTasks", "SubdlDatabaseRefreshTask.cs")
    if _pipe and _seed and _ref2:
        check("the pipeline passes the switch's mode to the shared reader",
              _pipe.count("onlyOwnDownloads: !_config.DownloadOnlyMissing") == 2,
              "a caller that ignores the switch would keep deciding by the old rule")
        check("the seeder passes the switch's mode to the shared reader",
              "onlyOwnDownloads: !onlyMissing" in _seed,
              "the queue gate and the pipeline must answer alike")
        check("the refresh passes the switch's mode too",
              "onlyOwnDownloads: Plugin.Instance?.Configuration.DownloadOnlyMissing != true" in _ref2,
              "a third answer would make the readout contradict the queue")
        # The overwrite guard: with the switch off the same language IS fetched over a file that is
        # already there, and the writer moves with overwrite:true.
        check("the download slot is planned against the files on disk",
              "PlanTarget(mediaPath, lang, effectiveHi, namesHere)" in _pipe,
              "a per-run counter would hand out .01 again and overwrite the existing subtitle")

    check("the postprocessing section carries a manual run button",
          'id="RunPostprocessNow"' in html)
    # F-M295 (08.10.2026): the button starts the TASK, not the endpoint. The endpoint ran the work but
    # wrote no status row, so the operator saw an empty list and reported the run as not having happened
    # (prod log 23:20:22→25: the work WAS done). Only SubdlPostprocessTask writes the "Upl. Postproc."
    # row, so the task route is the one that leaves evidence.
    check("RunPostprocessNow starts the task, not the endpoint",
          "subdlStartTask('SubDL Postprocessing')" in html
          and "Plugins/SubdlSync/PostprocessUploads" not in html,
          "the endpoint writes no status row; the button must reach the task that does")
    # The string above is matched against the task's Name at RUNTIME (subdlStartTask looks the task up by
    # name). A rename on either side would answer "Task not found" in the browser while every build and
    # every other check stayed green — so the literals are tied together here.
    post_task_src = read_source("ScheduledTasks", "SubdlPostprocessTask.cs")
    if post_task_src:
        check("the button's task name matches SubdlPostprocessTask.Name",
              'Name => "SubDL Postprocessing"' in post_task_src,
              "a name mismatch fails only in the browser, as 'Task not found'")

    # F-M291 (operator order 08.10.2026, "Nur die automatischen runs vom Postprocessing an den upload
    # binden, manuell darf immer"): the upload binding belongs to the AUTOMATIC path ONLY. This is a
    # half-removal by nature — the failure mode is not a crash but a manual run that silently declines —
    # so it is asserted from BOTH sides, and each side alone would pass on a broken file.
    ctrl = read_source("Api", "SubdlPostprocessController.cs")
    task = read_source("ScheduledTasks", "SubdlPostprocessTask.cs")
    sched = read_source("ScheduledTasks", "SubdlSchedulerCoordinator.cs")
    if ctrl and task and sched:
        # Assert the GATE CONSTRUCT, not the word: both files legitimately NAME UploadEnabled in the
        # comment that explains why the gate was removed, and a word-level ban went RED on a correct
        # file (measured). Match an `if` that tests the flag — that is the shape a re-added gate takes.
        gate = re.compile(r"if\s*\(\s*!?\s*[\w.]*UploadEnabled\s*\)")
        check("the manual endpoint no longer refuses when upload is off",
              gate.search(ctrl) is None,
              "a manual trigger must run regardless of the upload switch")
        check("the task no longer refuses when upload is off",
              gate.search(task) is None,
              "the dashboard's run button reaches the work through this task")
        # ...and the AUTOMATIC path must still be bound, or the removal took the rule with it.
        check("the automatic path is still bound to the upload switch",
              "config.UploadEnabled\n                && ppInterval is not (UpdateInterval.Never" in sched,
              "removing the rule entirely would let the schedule run with upload off")
    # The description names the BOUND half and only that. Operator order 08.10.2026: the trailing
    # "the button below always runs" had to go — the button sits directly beneath this line, so
    # naming it in the sentence was noise, and the operator asked for it twice on other labels too.
    # Asserted as a PAIR so a re-added suffix is caught as well as a lost rule: the binding sentence
    # is what the page must keep, the pointer at the button is what it must not grow back.
    check("the description names the inhibited scheduled run",
          "Scheduled run is inhibited if upload is disabled" in html,
          "the page must say which of the two paths is bound")
    check("the description does not point at the button below it",
          "the button below always runs" not in html,
          "the button sits directly under the line; naming it again is the noise the operator removed")

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
