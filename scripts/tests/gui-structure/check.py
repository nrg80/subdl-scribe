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
    expected_prefix = [
        "Subtitles downloaded",
        "Subtitles uploaded",
        "Type (movie/series) adjusted, both directions",
    ]
    check("the volume rows lead, download first, then the both-directions row",
          order[:3] == expected_prefix,
          "got: %s" % " | ".join(order[:3]))
    dl = [i for i, o in enumerate(order) if o.startswith("Downloads:")]
    up = [i for i, o in enumerate(order) if o.startswith("Uploads:")]
    check("the remaining rows are grouped by direction, downloads before uploads",
          bool(dl) and bool(up) and max(dl) < min(up),
          "downloads at %s, uploads at %s" % (dl, up))
    check("every quality row is present exactly once",
          len(order) == 9 and len(set(order)) == 9,
          "%d row(s): %s" % (len(order), order))
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
            # (`typeCorrected` writes `TypeCorrectedByFileName`, `tmdbYearMisses` writes
            # `TmdbYearFilterMisses`), and comparing field names to parameter names reports a
            # complete guard as broken.
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

    # The file-pair trap: the renderer is duplicated in configPage.js. A row added to one copy
    # only would render in one embedding and not the other.
    js = os.path.join(os.path.dirname(path), "configPage.js")
    if os.path.exists(js):
        jstext = open(js, encoding="utf-8").read()
        # Match the FIELD ACCESS, not the bare name: "XXFittedToAudio" contains "FittedToAudio"
        # and would pass a substring test while rendering a field that does not exist.
        check("both copies of the page carry the table renderer",
              "StatsTable" in jstext and "s.FittedToAudio" in jstext)
        # Both copies must carry the SAME renderer — a row in one only is the file-pair trap.
        def renderer_of(text):
            i = text.index("var subdlRenderStats = function (s) {")
            j = text.index("window.subdlRenderStats =", i)
            return text[i:j]
        check("the two copies of the renderer are identical",
              renderer_of(jstext) == renderer_of(html))

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
