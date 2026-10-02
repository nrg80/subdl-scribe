// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
// (19.09.2026): JF strips <style> blocks when embedding
            // The page and executes only the first <script> — inject the page
            // CSS right here from the main page script.
            (function () {
                var css = `        .subdl-tabs { display: flex; flex-wrap: wrap; gap: 6px; margin: 0 0 1.2em 0; }
        .subdl-tab-btn { flex: 1 1 auto; min-width: 0; white-space: nowrap; }
        @media (max-width: 480px) { .subdl-tabs { gap: 4px; } .subdl-tab-btn { padding: 0.6em 0.5em; font-size: 90%; } }
        .subdl-tab-btn.active { background: #00a4dc; color: #fff; }
        .subdl-tab-btn.active .itemLabelText { color: #fff; }
        #DownloadLangList { display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); gap: 2px 12px; margin: 0.5em 0; }
        .subdl-prio { display: inline-block; min-width: 1.1em; margin-left: 4px; color: #00a4dc; font-weight: bold; }
        .subdl-required { color: #ff6b6b; font-weight: 600; font-size: 0.9em; }
        .subdl-req-inline { color: #ff6b6b; font-weight: 600; font-size: 0.9em; margin-left: 4px; }
        .subdl-status-row { display: flex; align-items: center; gap: 0.7em; padding: 0.5em 0; border-bottom: 1px solid rgba(128,128,128,0.2); }
        .subdl-light { width: 14px; height: 14px; border-radius: 50%; background: var(--color,#888); flex-shrink: 0; box-shadow: inset 0 0 2px rgba(0,0,0,0.3); }
        .subdl-label { min-width: 130px; font-weight: 600; }
        .subdl-msg { opacity: 0.9; }
        #SubdlDirStatusList .subdl-status-row { padding-left: 1.5em; }
    `;
                var el = document.createElement('style');
                el.textContent = css;
                document.head.appendChild(el);
            })();
            var SubdlSyncConfig = {
                pluginUniqueId: '7d1c5a2e-3f4b-4d6e-9a8b-5c2f8e1d4a9b'
            };

            // One date format for the whole page: US order (M/D/YYYY) with local AM/PM
            // time, so statistics, quota and status stamps read identically instead of
            // each call site relying on its own locale default.
            var subdlFmtDateTime = function (iso) {
                var d = (iso instanceof Date) ? iso : new Date(iso);
                if (isNaN(d.getTime())) { return ''; }
                var date = d.toLocaleDateString('en-US', { year: 'numeric', month: 'numeric', day: 'numeric' });
                var time = d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', hour12: true });
                return date + ' ' + time;
            };
            var subdlFmtTime = function (iso) {
                var d = (iso instanceof Date) ? iso : new Date(iso);
                if (isNaN(d.getTime())) { return ''; }
                return d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', hour12: true });
            };

            // --- Tab switching (plain JS, works standalone + in dashboard) ---
            function subdlShowTab(tab) {
                var sections = document.querySelectorAll('.subdl-tab');
                for (var i = 0; i < sections.length; i++) {
                    sections[i].style.display = (sections[i].getAttribute('data-tab-content') === tab) ? '' : 'none';
                }
                var btns = document.querySelectorAll('.subdl-tab-btn');
                for (var j = 0; j < btns.length; j++) {
                    if (btns[j].getAttribute('data-tab') === tab) {
                        btns[j].classList.add('active');
                    } else {
                        btns[j].classList.remove('active');
                    }
                }
            }

            (function () {
                var btns = document.querySelectorAll('.subdl-tab-btn');
                for (var i = 0; i < btns.length; i++) {
                    btns[i].addEventListener('click', function () {
                        subdlShowTab(this.getAttribute('data-tab'));
                    });
                }
                subdlShowTab('general');
            })();

            var SUBDL_LANGS = [
                ['SQ', 'Albanian'], ['AR', 'Arabic'], ['HY', 'Armenian'], ['AZ', 'Azerbaijani'],
                ['EU', 'Basque'], ['BS', 'Bosnian'], ['BG', 'Bulgarian'], ['MY', 'Burmese'],
                ['CA', 'Catalan'], ['ZH', 'Chinese'], ['HR', 'Croatian'], ['CS', 'Czech'],
                ['DA', 'Danish'], ['NL', 'Dutch'], ['EN', 'English'], ['ET', 'Estonian'],
                ['FI', 'Finnish'], ['FR', 'French'], ['GL', 'Galician'], ['KA', 'Georgian'],
                ['DE', 'German'], ['EL', 'Greek'], ['HE', 'Hebrew'], ['HI', 'Hindi'],
                ['HU', 'Hungarian'], ['IS', 'Icelandic'], ['ID', 'Indonesian'], ['IT', 'Italian'],
                ['JA', 'Japanese'], ['KM', 'Khmer'], ['KO', 'Korean'], ['KU', 'Kurdish'],
                ['LV', 'Latvian'], ['LT', 'Lithuanian'], ['MK', 'Macedonian'], ['MS', 'Malay'],
                ['ML', 'Malayalam'], ['MN', 'Mongolian'], ['NO', 'Norwegian'], ['FA', 'Persian'],
                ['PL', 'Polish'], ['PT', 'Portuguese'], ['RO', 'Romanian'], ['RU', 'Russian'],
                ['SR', 'Serbian'], ['SI', 'Sinhala'], ['SK', 'Slovak'], ['SL', 'Slovenian'],
                ['ES', 'Spanish'], ['SV', 'Swedish'], ['TA', 'Tamil'], ['TE', 'Telugu'],
                ['TH', 'Thai'], ['TR', 'Turkish'], ['UK', 'Ukrainian'], ['UR', 'Urdu'],
                ['VI', 'Vietnamese']
            ];

            var SUBDL_LANG_PICKED = []; // Click order = saved order (no visual numbering anymore)

            function subdlGetSelectedLangs() {
                return SUBDL_LANG_PICKED.slice();
            }

            function subdlSyncPickedFromDisplay() {
                var current = (document.querySelector('#DownloadLanguagesDisplay') || {}).value || '';
                SUBDL_LANG_PICKED = current.split(',').map(function (s) { return s.trim().toUpperCase(); }).filter(Boolean);
                var pickedSet = {};
                SUBDL_LANG_PICKED.forEach(function (l) { pickedSet[l] = true; });
                var boxes = document.querySelectorAll('#DownloadLangList input[type=checkbox]');
                for (var i = 0; i < boxes.length; i++) {
                    var code = boxes[i].getAttribute('data-lang');
                    boxes[i].checked = !!pickedSet[code];
                }
            }

            function subdlUpdateLangPreview() {
                // Order footer removed — the save order is the language-list order.
            }

            function subdlPopulateLangList(config) {
                var container = document.querySelector('#DownloadLangList');
                if (!container) { return; }
                var existing = (config.DownloadLanguages || '').split(',').map(function (s) { return s.trim().toUpperCase(); }).filter(Boolean);
                var known = {};
                SUBDL_LANGS.forEach(function (pair) { known[pair[0]] = true; });
                // List strictly alphabetical (SUBDL_LANGS order), ticks mark the selection
                var ordered = SUBDL_LANGS.map(function (pair) { return pair[0]; });
                existing.forEach(function (l) { if (!known[l] && ordered.indexOf(l) === -1) { ordered.push(l); } });
                container.innerHTML = '';
                ordered.forEach(function (code) {
                    var name = code;
                    for (var i = 0; i < SUBDL_LANGS.length; i++) {
                        if (SUBDL_LANGS[i][0] === code) { name = SUBDL_LANGS[i][1] + ' (' + code + ')'; break; }
                    }
                    var isPicked = existing.indexOf(code) !== -1;
                    var div = document.createElement('div');
                    div.className = 'checkboxContainer';
                    var label = document.createElement('label');
                    label.className = 'emby-checkbox-label';
                    var input = document.createElement('input');
                    input.type = 'checkbox';
                    input.setAttribute('is', 'emby-checkbox');
                    input.id = 'dl-lang-' + code;
                    input.setAttribute('data-lang', code);
                    if (isPicked) { input.setAttribute('checked', 'checked'); }
                    var span = document.createElement('span');
                    span.textContent = name;
                    label.appendChild(input);
                    label.appendChild(span);
                    div.appendChild(label);
                    container.appendChild(div);
                    input.checked = isPicked; // After mount — emby-checkbox upgrade can drop the pre-mount checked
                    input.addEventListener('change', function () {
                        var code = this.getAttribute('data-lang');
                        if (this.checked) {
                            if (SUBDL_LANG_PICKED.indexOf(code) === -1) { SUBDL_LANG_PICKED.push(code); }
                        } else {
                            SUBDL_LANG_PICKED = SUBDL_LANG_PICKED.filter(function (l) { return l !== code; });
                        }
                        subdlUpdateLangPreview();
                    });
                });
                subdlUpdateLangPreview();
            }

            function subdlOpenLangPicker() {
                var modal = document.querySelector('#DownloadLangModal');
                if (!modal) { return; }
                subdlSyncPickedFromDisplay();
                subdlUpdateLangPreview();
                modal.style.display = 'flex';
            }

            function subdlCloseLangPicker() {
                var modal = document.querySelector('#DownloadLangModal');
                if (modal) { modal.style.display = 'none'; }
            }

            (function () {
                var pickerBtn = document.querySelector('#DownloadLangPickerBtn');
                var okBtn = document.querySelector('#DownloadLangOkBtn');
                var cancelBtn = document.querySelector('#DownloadLangCancelBtn');
                if (pickerBtn) { pickerBtn.addEventListener('click', function () { subdlOpenLangPicker(); }); }
                if (okBtn) {
                    okBtn.addEventListener('click', function () {
                        document.querySelector('#DownloadLanguagesDisplay').value = subdlGetSelectedLangs().join(', ');
                        subdlCloseLangPicker();
                    });
                }
                if (cancelBtn) {
                    cancelBtn.addEventListener('click', function () {
                        subdlCloseLangPicker();
                    });
                }
            })();

            function subdlPopulateLibraries(config) {
                // The library selection renders in BOTH places (General tab and
                // Download tab) — same config list, two views, kept in sync on save.
                var containers = ['#LibraryList'].map(function (s) { return document.querySelector(s); }).filter(Boolean);
                if (containers.length === 0) { return; }
                var container = containers[0];
                ApiClient.getVirtualFolders().then(function (folders) {
                    containers.forEach(function (c) { c.innerHTML = ''; });
                    folders.forEach(function (f) {
                        containers.forEach(function (container) {
                            var id = 'lib_' + (container.id || '') + '_' + f.Name;
                            var div = document.createElement('div');
                            div.className = 'checkboxContainer';
                            var label = document.createElement('label');
                            label.className = 'emby-checkbox-label';
                            var input = document.createElement('input');
                            input.type = 'checkbox';
                            input.setAttribute('is', 'emby-checkbox');
                            input.id = id;
                            input.setAttribute('data-libname', f.Name);
                            if ((config.SelectedLibraries || []).indexOf(f.Name) !== -1) {
                                input.checked = true;
                            }
                            var span = document.createElement('span');
                            span.textContent = f.Name;
                            label.appendChild(input);
                            label.appendChild(span);
                            div.appendChild(label);
                            container.appendChild(div);
                        });
                    });
                });
            }

            function subdlBootstrapOnce() {
                var pg = document.querySelector('#SubdlSyncConfigPage');
                if (!pg || pg.dataset.subdlLoaded === '1') { return; }
                pg.dataset.subdlLoaded = '1';
                pg.dispatchEvent(new CustomEvent('pageshow'));
            }
            // JF 12's SPA loader runs <script src> async AFTER pageshow
            // Fired — bootstrap the load directly when the page div exists.
            document.querySelector('#SubdlSyncConfigPage')
                .addEventListener('pageshow', function () {
                    Dashboard.showLoadingMsg();
                    ApiClient.getPluginConfiguration(SubdlSyncConfig.pluginUniqueId).then(function (config) {
                        document.querySelector('#Username').value = config.Username || '';
                        document.querySelector('#Password').value = config.Password || '';
                        document.querySelector('#ApiKey').value = config.ApiKey || '';
                        document.querySelector('#TmdbApiKey').value = config.TmdbApiKey || '';
                        subdlLoadQuota();
                        subdlStartQuotaPoll();
                        document.querySelector('#UploadsPerHour').value = config.UploadsPerHour;
        document.querySelector('#MinCallPauseSec').value = (config.MinCallPauseSec != null ? config.MinCallPauseSec : 0.5);
                        document.querySelector('#FollowUpRoundsDownload').checked = (config.FollowUpRoundsDownload !== false);
                        document.querySelector('#FollowUpRoundsUpload').checked = (config.FollowUpRoundsUpload !== false);
                        document.querySelector('#FileRetryLimit').value = (function () { var v = parseInt(config.FileRetryLimit); return isNaN(v) ? 3 : v; })();
                        document.querySelector('#IdRetryLimit').value = (function () { var v = parseInt(config.IdRetryLimit); return isNaN(v) ? 3 : v; })();
                        document.querySelector('#DownloadQaRetryLimit').value = (function () { var v = parseInt(config.DownloadQaRetryLimit); return isNaN(v) ? 3 : v; })();
                        document.querySelector('#ArrivalDebounceMinutes').value = (function () { var v = parseInt(config.ArrivalDebounceMinutes); return isNaN(v) ? 5 : v; })();                                                document.querySelector('#RefetchInterval').value = config.RefetchInterval;
                        document.querySelector('#JobSpacingMinutes').value = (function () { var v = parseInt(config.JobSpacingMinutes); return isNaN(v) ? 15 : v; })();

                        var rsi = document.querySelector('#RandomScheduleInfo');
                        if (rsi) {
                            var wd = ['Mon','Tue','Wed','Thu','Fri','Sat','Sun'];
                            var parts = (config.RandomWeeklyTime || '').split(' ');
                            function ord(n) { var s = ['th','st','nd','rd'], v = n % 100; return n + (s[(v - 20) % 10] || s[v] || s[0]); }
                            function toAmPm(t) {
                                if (!t) { return '—'; }
                                var p = t.split(':');
                                var h = parseInt(p[0], 10);
                                var m = p[1] || '00';
                                var suffix = h >= 12 ? 'PM' : 'AM';
                                var hh = h % 12 || 12;
                                return hh + ':' + m + ' ' + suffix;
                            }
                            var txt = 'Current windows: Daily ' + toAmPm(config.RandomDailyTime) +
                                ', Weekly ' + (parts.length === 2 ? wd[parseInt(parts[0]) - 1] + ' ' + toAmPm(parts[1]) : '—') +
                                ', Monthly ' + (config.RandomMonthlyDay ? ord(config.RandomMonthlyDay) : '—') + '. ' + toAmPm(config.RandomMonthlyTime);
                            rsi.textContent = txt;
                        }
                        document.querySelector('#UploadOnArrival').checked = config.UploadOnArrival;
                        document.querySelector('#UploadEnabled').checked = config.UploadEnabled;
                        document.querySelector('#DryRun').checked = config.DryRun;
                        document.querySelector('#LogMode').value = config.LogMode;
                        document.querySelector('#OshashRefresh').value = config.OshashRefresh || 'Monthly';
        document.querySelector('#PruneMode').value = config.PruneMode || 'Weekly';
                        document.querySelector('#QaValidateSrt').checked = config.QaValidateSrt;
                        document.querySelector('#QaCheckSync').checked = config.QaCheckSync;
                        document.querySelector('#QaVerifyLanguage').checked = config.QaVerifyLanguage;
                        document.querySelector('#UploadResolveUnd').checked = config.UploadResolveUnd;
                        document.querySelector('#QaMinCues').checked = config.QaMinCues;
                        // F-M34/M35: show effective defaults when the user list is empty (visible prefill)
                        document.querySelector('#SkipDirs').value = (config.SkipDirPatterns && config.SkipDirPatterns.length)
                            ? config.SkipDirPatterns.join('\n')
                            : ['incomplete', 'downloading', '.tmp'].join('\n');
                        document.querySelector('#SkipFiles').value = (config.SkipFilePatterns && config.SkipFilePatterns.length)
                            ? config.SkipFilePatterns.join('\n')
                            : ['sample', 'trailer', 'incomplete',
                               '.!qb', '.!ut', '.bc!', '.!bt', '.az!', '.bt!', '.part', '.partial', '.downloading',
                               '.crdownload', '.opdownload',
                               '.ob!', '.fb!', '.jc!', '.td', '.dtapart', '.tmp'].join('\n');
                        document.querySelector('#DownloadEnabled').checked = config.DownloadEnabled;
                        subdlPopulateLangList(config);
                        document.querySelector('#DownloadLanguagesDisplay').value = (config.DownloadLanguages || '');
                        document.querySelector('#DownloadOnlyMissing').checked = config.DownloadOnlyMissing;
                        document.querySelector('#DownloadHearingImpaired').checked = config.DownloadHearingImpaired;
                        document.querySelector('#DownloadRuntimeToleranceSec').value = config.DownloadRuntimeToleranceSec;
                        document.querySelector('#DownloadRequireImdb').checked = config.DownloadRequireImdb;
                        document.querySelector('#QaDownloadMinCues').checked = config.QaDownloadMinCues;
                        document.querySelector('#QaDownloadVerifyLanguage').checked = config.QaDownloadVerifyLanguage;
                        document.querySelector('#DownloadScoreWeightGroup').value = config.DownloadScoreWeightGroup;
                        document.querySelector('#DownloadScoreWeightToken').value = config.DownloadScoreWeightToken;
                        document.querySelector('#DownloadScoreWeightDownload').value = config.DownloadScoreWeightDownload;
                                                document.querySelector('#DownloadOnArrival').checked = config.DownloadOnArrival;
                        var dmcl = parseInt(config.DownloadMaxCandidatesPerLanguage);
                        document.querySelector('#DownloadMaxCandidatesPerLanguage').value = isNaN(dmcl) ? 3 : dmcl;
                        var dkb = parseInt(config.DownloadKeepBestPerLanguage);
                        document.querySelector('#DownloadKeepBestPerLanguage').value = isNaN(dkb) ? 1 : dkb;
                        document.querySelector('#UploadContinueAfterLimit').checked = config.UploadContinueAfterLimit;
                        document.querySelector('#DownloadContinueAfterLimit').checked = config.DownloadContinueAfterLimit;
                        document.querySelector('#DownloadDryRun').checked = config.DownloadDryRun;

                        // F-M23 (rework 25.09.2026): the counters live in the database now, so the GUI
                        // asks the plugin API instead of the configuration.
                        var fmtLast = function (iso) {
                            if (!iso) { return 'never'; }
                            return subdlFmtDateTime(iso);
                        };
                        var subdlRenderStats = function (s) {
                            var el = document.querySelector('#StatsLine');
                            if (!el) { return; }
                            if (!s) { el.textContent = '—'; return; }
                            el.textContent =
                                s.Uploaded + ' subtitles uploaded, ' +
                                s.Downloaded + ' subtitles downloaded since the last reset.';

                            // F-M218 quality line: what the volume counters cannot show —
                            // where the run had to work around Jellyfin's metadata, and how
                            // much the QA gates filtered out.
                            var q = document.querySelector('#StatsQualityLine');
                            if (q) {
                                q.textContent =
                                    'Corrections and rejects: ' +
                                    (s.TypeCorrectedByFileName || 0) + ' items the file name typed ' +
                                    '(Jellyfin had the wrong type), ' +
                                    (s.TmdbYearFilterMisses || 0) + ' titles found only after dropping ' +
                                    'Jellyfin\u2019s year, ' +
                                    (s.RejectedDownload || 0) + ' downloads / ' +
                                    (s.RejectedUpload || 0) + ' uploads rejected after being fetched.';
                            }

                            // The period stamp sits on its own line, last — it belongs to
                            // all of the counters above, not to a sentence in the middle.
                            var since = document.querySelector('#StatsSinceLine');
                            if (since) {
                                since.textContent = s.SinceUtc
                                    ? 'Counting since ' + subdlFmtDateTime(s.SinceUtc)
                                    : 'Counting since: no reset yet.';
                            }
                        };
                        window.subdlRenderStats = subdlRenderStats;
                        var subdlLoadStats = function () {
                            // dataType:'json' is REQUIRED: without it the apiclient bundle returns the
                            // RAW Response object (Content-Type application/json is not text/*), so
                            // s.Uploaded/s.SinceUtc are undefined and the line reads
                            // "undefined subtitles uploaded ... since —".
                            return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Plugins/SubdlSync/Stats'), dataType: 'json' })
                                .then(function (s) { subdlRenderStats(s); return s; });
                        };
                        window.subdlLoadStats = subdlLoadStats;
                        subdlLoadStats();

                        // Workers section (bottom of the page): the LAST run of each worker, read from
                        // the data file — one line per worker, never a history. Kept in the database
                        // rather than in a static field precisely because a Jellyfin restart used to
                        // reset that "last run" to "never run" while the run was minutes old.
                        var subdlWorkerOutcome = function (o) {
                            switch ((o || '').toLowerCase()) {
                                // ABBREVIATED status words, at most 6 characters. The Workers columns are
                                // max-content and cannot shrink, so the longest word here sets the minimum
                                // width of the whole line on a phone. The full words were 8-9 characters and
                                // forced the section down to 0.85em to fit a 320px screen; these fit at full
                                // size. Keep new status words <= 6 characters and re-measure (see
                                // #WorkerRunList) -- a longer one pushes the status off the right edge
                                // instead of eliding.
                                case 'run': return { color: '#00a4dc', text: 'run' };
                                case 'ok': return { color: '#107c10', text: 'ok' };
                                case 'failed': return { color: '#a4262c', text: 'fail' };
                                case 'cancelled': return { color: '#ffc107', text: 'cancel' };
                                case 'skipped': return { color: '#767676', text: 'skip' };
                                default: return { color: '#767676', text: 'never' };
                            }
                        };
                        var subdlRenderWorkers = function (d) {
                            var list = document.querySelector('#WorkerRunList');
                            if (!list) { return; }
                            var workers = (d && d.Workers) || [];
                            var line = document.querySelector('#WorkersLine');
                            if (line) {
                                line.textContent = 'The last run of each worker.';
                            }
                            list.innerHTML = '';
workers.forEach(function (w) {
                                var oc = subdlWorkerOutcome(w.Outcome);
                                var when = w.Started ? subdlFmtDateTime(w.Started) : '\u2014';
                                var row = document.createElement('div');
                                row.className = 'subdl-status-row';
                                // FOUR cells, one per column: light, name, time, result. The dry-run note
                                // is grey text INSIDE the result cell (not a separate badge/cell) --
                                // a badge in its own column widened the line by ~50px and pushed the
                                // status off the right edge on a narrow phone. Inside the result cell it
                                // costs nothing extra in column alignment, because the cell is already
                                // sized by its own content.
                                row.innerHTML = '<span class="subdl-light" style="background:' + oc.color + ';"></span>' +
                                    '<span class="subdl-label">' + (w.Name || '?') + '</span>' +
                                    '<span class="subdl-when">' + when + '</span>' +
                                    '<span class="subdl-outcome" style="color:' + oc.color + ';">' + oc.text +
                                    (w.DryRun ? '<span class="subdl-dryrun">dry run</span>' : '') + '</span>';
                                list.appendChild(row);
                            });
                        };
                        var subdlLoadWorkers = function () {
                            return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Plugins/SubdlSync/WorkerRuns'), dataType: 'json' })
                                .then(function (d) { subdlRenderWorkers(d); return d; })
                                .catch(function () { subdlRenderWorkers(null); });
                        };
                        window.subdlLoadWorkers = subdlLoadWorkers;
                        subdlLoadWorkers();
                        Dashboard.hideLoadingMsg();
                        subdlPopulateLibraries(config);
                    });

                // F-M48 (user decision 08.09.2026): manual run buttons — always available,
                // In every interval mode. The task itself only gates on its master switch.
                // Buttons stay enabled even when a pipeline is disabled — the task then
                // Skips with a log line; the user gets consistent behaviour ("disabled
                // Pipelines stay off") without us re-implementing state polling here.
                function subdlFindTaskId(name, cb) {
                    console.log('[SubDL Scribe] looking for task', name);
                    var tasksPromise = null;
                    if (typeof ApiClient !== 'undefined' && typeof ApiClient.getScheduledTasks === 'function') {
                        try {
                            tasksPromise = ApiClient.getScheduledTasks();
                        } catch (err) {
                            console.error('[SubDL Scribe] ApiClient.getScheduledTasks threw', err);
                        }
                    }
                    if (!tasksPromise || typeof tasksPromise.then !== 'function') {
                        var fallbackUrl = ApiClient.getUrl('ScheduledTasks');
                        console.log('[SubDL Scribe] falling back to fetch', fallbackUrl);
                        tasksPromise = fetch(fallbackUrl, { headers: { 'Accept': 'application/json' } }).then(function (r) { return r.json(); });
                    }
                    tasksPromise.then(function (tasks) {
                        console.log('[SubDL Scribe] got', tasks ? tasks.length : 0, 'tasks');
                        if (!Array.isArray(tasks)) { tasks = []; }
                        var hit = tasks.filter(function (t) { return t.Name === name || t.Key === name || t.Id === name; })[0];
                        console.log('[SubDL Scribe] hit', hit ? hit.Name : 'none');
                        cb(hit ? hit.Id : null);
                    }).catch(function (err) {
                        console.error('[SubDL Scribe] task lookup failed', err);
                        cb(null);
                    });
                }

                function subdlStartTask(name) {
                    Dashboard.showLoadingMsg();
                    subdlFindTaskId(name, function (id) {
                        if (!id) {
                            Dashboard.hideLoadingMsg();
                            Dashboard.alert('Task not found: ' + name);
                            return;
                        }
                        // JF API: POST /ScheduledTasks/Running/{taskId}
                        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('ScheduledTasks/Running/' + id) }).then(function () {
                            Dashboard.hideLoadingMsg();
                            Dashboard.alert('Started: ' + name);
                        }, function () {
                            Dashboard.hideLoadingMsg();
                            Dashboard.alert('Could not start task (is it already running?): ' + name);
                        });
                    });
                }

                // F-M70 (09.09.2026): live SubDL quota under the API key field.
                // GET /Plugins/SubdlQuota/Get proxies /api/v1/me server-side
                // (free of request quota, 5-min cached server-side).
                // Auto-refresh: 60 s poll — the server cache keeps this free.
                var subdlQuotaTimer = null;
                function subdlStopQuotaPoll() {
                    if (subdlQuotaTimer) { clearInterval(subdlQuotaTimer); subdlQuotaTimer = null; }
                }
                function subdlStartQuotaPoll() {
                    subdlStopQuotaPoll();
                    subdlQuotaTimer = setInterval(subdlLoadQuota, 60000);
                }
                function subdlLoadQuota() {
                    var box = document.querySelector('#SubdlQuotaBox');
                    if (!box) { return; }
                    // ApiClient.ajax rejects silently in JF 10.11 (error handler
                    // Hides the box). Plain fetch() with the session token
                    // (ApiClient.accessToken, localStorage fallback) is robust everywhere.
                    var subdlQ = ApiClient.getUrl('Plugins/SubdlQuota/Get');
                    var subdlH = {};
                    try { subdlH['X-Emby-Token'] = ApiClient.accessToken(); } catch (e) {
                        try { subdlH['X-Emby-Token'] = JSON.parse(localStorage.getItem('jellyfin_credentials')).Servers[0].AccessToken; } catch (e2) {}
                    }
                    fetch(subdlQ, { headers: subdlH }).then(function (r) { return r.json(); }).then(function (res) {
                        function n(v) { return (typeof v === 'number') ? v.toLocaleString() : v; }
                        function t(iso) {
                            // FetchedAt is UTC ISO — render in the viewer's timezone (Europe/Berlin)
                            return isNaN(new Date(iso).getTime()) ? '' : 'Updated ' + subdlFmtTime(iso);
                        }
                        if (res && res.error) {
                            if (res.error === 'no-api-key') {
                                box.textContent = 'No API key configured.';
                            } else {
                                box.textContent = 'Unavailable (' + res.error + ')';
                            }
                            box.style.display = 'block';
                            return;
                        }
                        // (15.09.2026): docs-shaped rendering —
                        // Plan / search / downloads, errors as 429 line.
                        // Old "API requests today... as of" form dropped
                        // (user decision 15.09.2026).
                        if (!res || !res.search) {
                            box.textContent = 'Unavailable (bad response)';
                            box.style.display = 'block';
                            return;
                        }
                        function resetLocal(iso) {
                            return isNaN(new Date(iso).getTime()) ? '' : subdlFmtTime(iso);
                        }
                        var lines = [];
                        lines.push('Plan: ' + (res.plan || 'Free'));
                        lines.push('Search: ' + n(res.search.used) + ' / ' + n(res.search.limit) + ' today (resets ' + resetLocal(res.search.reset_at) + ')');
                        if (res.downloads) {
                            lines.push('Downloads: ' + n(res.downloads.used) + ' / ' + n(res.downloads.limit) + ' today (resets ' + resetLocal(res.downloads.reset_at) + ')');
                        }
                        if (res.downloads && res.downloads.remaining === 0) {
                            lines.push('Error: download limit reached — next reset ' + resetLocal(res.downloads.reset_at));
                        }
                        var foot = t(res.fetchedAt);
                        if (foot) { lines.push('<div style="margin-top:6px; opacity:0.75;">' + foot + '</div>'); }
                        box.innerHTML = lines.map(function (l) { return '<div>' + l + '</div>'; }).join('');
                        box.style.display = 'block';

                    }, function () {
                        box.style.display = 'none';
                    });
                }

                // NF-8 (20.09.2026 rev.3): stop button — simply creates.stop-upload
                // And.stop-download marker files in the plugin data directory.
                function subdlNotify(msg) {
                    if (typeof Dashboard !== 'undefined' && typeof Dashboard.alert === 'function') {
                        try { Dashboard.alert(msg); return; } catch (e) {}
                    }
                    if (typeof alert === 'function') { alert(msg); }
                    else { console.log('[SubDL Scribe] notify:', msg); }
                }

                function subdlStopTask(name) {
                    var dir = name.indexOf('Upload') >= 0 ? 'upload' : 'download';
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('Plugins/SubdlSync/Stop', { direction: dir })
                    }).then(function (r) {
                        if (r && r.ok) {
                            subdlNotify('Stop marker set: ' + name);
                        } else {
                            subdlNotify('Stop failed: ' + name + ' (' + (r && r.error ? r.error : 'unknown') + ')');
                        }
                    }).catch(function (err) {
                        console.error('[SubDL Scribe] stop failed', name, err);
                        subdlNotify('Stop failed — plugin not reachable: ' + name);
                    });
                }

                document.querySelector('#RunUploadNow').addEventListener('click', function () { subdlStartTask('SubDL/TMDB — Subtitle Upload'); });
                document.querySelector('#RunDownloadNow').addEventListener('click', function () { subdlStartTask('SubDL/TMDB — Subtitle Download'); });
                document.querySelector('#StopAllNow').addEventListener('click', function () {
                    // NF-8: stop BOTH directions — one click, two marker files.
                    subdlStopTask('SubDL/TMDB — Subtitle Upload');
                    subdlStopTask('SubDL/TMDB — Subtitle Download');
                });

                // Statistics reset: zero the counters via plugin config update.
                document.querySelector('#ResetRegistry').addEventListener('click', function (e) {
                    e.preventDefault();
                    // F-M90: two-stage confirmation — the wording is intentionally blunt.
                    if (!confirm('Reset the plugin database?\n\nThis deletes ALL persistent state files: uploaded/rejected memory, stream positions, hash cache. On the next run every stream is re-screened from scratch and re-verified against SubDL (existing uploads are skipped, nothing is uploaded twice). Timestamped backups are kept in the plugin data directory.\n\nAre you absolutely sure?')) {
                        return;
                    }
                    if (!confirm('FINAL CONFIRMATION\n\nThis makes EVERYTHING new — the plugin forgets every decision it ever made. A full re-screen will consume API quota and may take several runs.\n\nProceed only if you really want this.')) {
                        return;
                    }
                    Dashboard.showLoadingMsg();
                    ApiClient.ajax({ url: ApiClient.getUrl('Plugins/SubdlReset/Reset', { confirm: 'true', scope: 'all' }), type: 'POST' }).then(function (r) {
                        return r.json();
                    }).then(function (res) {
                        Dashboard.hideLoadingMsg();
                        if (res && res.ok) {
                            Dashboard.alert('Plugin database reset. Backup kept as ' + res.backup + '. Everything is re-derived on the next run.');
                        } else {
                            Dashboard.alert('Reset failed: ' + (res && res.error ? res.error : 'unknown'));
                        }
                    }).catch(function () {
                        Dashboard.hideLoadingMsg();
                        Dashboard.alert('Reset failed — plugin not reachable.');
                    });
                });

                // Restore a timestamped backup of the plugin database.
                // The dropdown is populated when the page loads and refreshed via List().
                function subdlRefreshBackupSelect() {
                    var sel = document.querySelector('#BackupSelect');
                    if (!sel) { return; }
                    ApiClient.ajax({ url: ApiClient.getUrl('Plugins/SubdlReset/List'), type: 'GET' }).then(function (r) {
                        return r.json();
                    }).then(function (res) {
                        sel.innerHTML = '';
                        var backups = (res && res.backups) || [];
                        if (backups.length === 0) {
                            var opt = document.createElement('option');
                            opt.value = '';
                            opt.textContent = 'No backups found';
                            sel.appendChild(opt);
                            return;
                        }
                        backups.forEach(function (stamp) {
                            var opt = document.createElement('option');
                            opt.value = stamp;
                            opt.textContent = stamp;
                            sel.appendChild(opt);
                        });
                    }).catch(function () {
                        sel.innerHTML = '';
                        var opt = document.createElement('option');
                        opt.value = '';
                        opt.textContent = 'Could not load backups';
                        sel.appendChild(opt);
                    });
                }

                document.querySelector('#InitDatabase').addEventListener('click', function (e) {
                    e.preventDefault();
                    if (!confirm('Initialize the SubDL Scribe database?\n\nThis keeps the current database as a timestamped backup and creates a fresh empty database. The next run will re-scan and rebuild state from scratch.\n\nProceed?')) {
                        return;
                    }
                    Dashboard.showLoadingMsg();
                    ApiClient.ajax({ url: ApiClient.getUrl('Plugins/SubdlReset/Initialize', { confirm: 'true' }), type: 'POST' }).then(function (r) {
                        return r.json();
                    }).then(function (res) {
                        Dashboard.hideLoadingMsg();
                        if (res && res.ok) {
                            subdlRefreshBackupSelect();
                            Dashboard.alert('Database initialized. Backup kept as ' + res.backup + '.');
                        } else {
                            Dashboard.alert('Initialize failed: ' + (res && res.error ? res.error : 'unknown'));
                        }
                    }).catch(function () {
                        Dashboard.hideLoadingMsg();
                        Dashboard.alert('Initialize failed — plugin not reachable.');
                    });
                });

                document.querySelector('#ResetRegistry').addEventListener('click', function (e) {
                    e.preventDefault();
                    // F-M90: two-stage confirmation — the wording is intentionally blunt.
                    if (!confirm('Reset the plugin database?\n\nThis deletes ALL persistent state files: uploaded/rejected memory, stream positions, hash cache. On the next run every stream is re-screened from scratch and re-verified against SubDL (existing uploads are skipped, nothing is uploaded twice). Timestamped backups are kept in the plugin data directory.\n\nAre you absolutely sure?')) {
                        return;
                    }
                    if (!confirm('FINAL CONFIRMATION\n\nThis makes EVERYTHING new — the plugin forgets every decision it ever made. A full re-screen will consume API quota and may take several runs.\n\nProceed only if you really want this.')) {
                        return;
                    }
                    Dashboard.showLoadingMsg();
                    ApiClient.ajax({ url: ApiClient.getUrl('Plugins/SubdlReset/Reset', { confirm: 'true', scope: 'all' }), type: 'POST' }).then(function (r) {
                        return r.json();
                    }).then(function (res) {
                        Dashboard.hideLoadingMsg();
                        if (res && res.ok) {
                            subdlRefreshBackupSelect();
                            Dashboard.alert('Plugin database reset. Backup kept as ' + res.backup + '. Everything is re-derived on the next run.');
                        } else {
                            Dashboard.alert('Reset failed: ' + (res && res.error ? res.error : 'unknown'));
                        }
                    }).catch(function () {
                        Dashboard.hideLoadingMsg();
                        Dashboard.alert('Reset failed — plugin not reachable.');
                    });
                });

                document.querySelector('#RestoreBackup').addEventListener('click', function (e) {
                    e.preventDefault();
                    var sel = document.querySelector('#BackupSelect');
                    var stamp = sel ? sel.value : '';
                    if (!stamp) {
                        Dashboard.alert('Select a backup from the dropdown first.');
                        return;
                    }
                    if (!confirm('Restore subdl-scribe.db from backup ' + stamp + '?\nThe current database is kept as .pre-restore copy before overwriting.\n\nProceed?')) {
                        return;
                    }
                    Dashboard.showLoadingMsg();
                    ApiClient.ajax({ url: ApiClient.getUrl('Plugins/SubdlReset/Restore', { confirm: 'true', stamp: stamp }), type: 'POST' }).then(function (r) {
                        return r.json();
                    }).then(function (res2) {
                        Dashboard.hideLoadingMsg();
                        if (res2 && res2.ok) {
                            subdlRefreshBackupSelect();
                            Dashboard.alert('Restored subdl-scribe.db from ' + res2.stamp + '.');
                        } else {
                            Dashboard.alert('Restore failed: ' + (res2 && res2.error ? res2.error : 'unknown'));
                        }
                    }).catch(function () {
                        Dashboard.hideLoadingMsg();
                        Dashboard.alert('Restore failed — plugin not reachable.');
                    });
                });
                subdlRefreshBackupSelect();

                document.querySelector('#ResetStats').addEventListener('click', function (e) {
                    e.preventDefault();
                    Dashboard.showLoadingMsg();
                    // Counters live in the database now — reset them through the plugin API, not by
                    // rewriting the configuration.
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('Plugins/SubdlSync/StatsReset', { confirm: 'true' }),
                        dataType: 'json'
                    }).then(function (res) {
                        Dashboard.hideLoadingMsg();
                        if (res && res.ok) {
                            if (typeof window.subdlRenderStats === 'function') {
                                window.subdlRenderStats(res);
                            } else if (typeof window.subdlLoadStats === 'function') {
                                window.subdlLoadStats();
                            }
                        } else {
                            Dashboard.alert('Reset failed: ' + (res && res.error ? res.error : 'unknown'));
                        }
                    }, function () {
                        Dashboard.hideLoadingMsg();
                        Dashboard.alert('Reset failed.');
                    });
                });
            });

            document.querySelector('#SubdlSyncConfigForm')
                .addEventListener('submit', function (e) {
                    // PreventDefault MUST run synchronously FIRST — any throw in
                    // The async chain (missing JF global, quota poll helper) otherwise
                    // Lets the browser do a NATIVE form submit: page reloads, nothing
                    // Saved (user report 10.09.2026 "configuration is no longer
                    // Saved after save").
                    e.preventDefault();
                    try { subdlStopQuotaPoll(); } catch (err) { /* non-fatal */ }
                    if (typeof ApiClient === 'undefined' || typeof SubdlSyncConfig === 'undefined') { return false; }
                    try { Dashboard.showLoadingMsg(); } catch (err) { /* non-fatal */ }
                    ApiClient.getPluginConfiguration(SubdlSyncConfig.pluginUniqueId).then(function (config) {
                        // ALL FOUR credentials are required, and they are required in the
                        // same way: there is no partial operation here. SubDL needs the
                        // login pair for the upload (the three upload steps authenticate
                        // with a Bearer token from /login) AND the API key for search,
                        // file download and the quota read — they serve different
                        // endpoints, so neither replaces the other. TMDb is what resolves
                        // and corrects the ids in both directions. An empty field refuses
                        // the save and names the field, instead of degrading the pipeline
                        // silently and only failing later mid-run.
                        var subdlRequired = [
                            ['Username', 'SubDL email'],
                            ['Password', 'SubDL password'],
                            ['ApiKey', 'SubDL API key'],
                            ['TmdbApiKey', 'TMDb API key (v3)']
                        ];
                        for (var ri = 0; ri < subdlRequired.length; ri++) {
                            var fieldId = subdlRequired[ri][0];
                            var fieldEl = document.querySelector('#' + fieldId);
                            var fieldVal = String(fieldEl ? fieldEl.value : '').trim();
                            if (!fieldVal) {
                                try { Dashboard.hideLoadingMsg(); } catch (err) { /* non-fatal */ }
                                subdlNotify(subdlRequired[ri][1] + ' is required — the plugin does not run without all four credentials.');
                                if (fieldEl && typeof fieldEl.focus === 'function') { fieldEl.focus(); }
                                return false;
                            }
                        }
                        config.Username = document.querySelector('#Username').value;
                        config.Password = document.querySelector('#Password').value;
                        config.ApiKey = document.querySelector('#ApiKey').value;
                        config.TmdbApiKey = String(document.querySelector('#TmdbApiKey').value || '').trim();
                        var uph = parseInt(document.querySelector('#UploadsPerHour').value) || 400;
                        config.FileRetryLimit = (function () { var v = parseInt(document.querySelector('#FileRetryLimit').value, 10); return isNaN(v) ? 3 : Math.min(20, Math.max(0, v)); })();
                        config.IdRetryLimit = (function () { var v = parseInt(document.querySelector('#IdRetryLimit').value, 10); return isNaN(v) ? 3 : Math.min(20, Math.max(0, v)); })();
                        config.DownloadQaRetryLimit = (function () { var v = parseInt(document.querySelector('#DownloadQaRetryLimit').value, 10); return isNaN(v) ? 3 : Math.min(20, Math.max(0, v)); })();
                        config.ArrivalDebounceMinutes = (function () { var v = parseInt(document.querySelector('#ArrivalDebounceMinutes').value, 10); return isNaN(v) ? 5 : Math.min(120, Math.max(1, v)); })();                                                config.RefetchInterval = document.querySelector('#RefetchInterval').value;
                        config.JobSpacingMinutes = (function () { var v = parseInt(document.querySelector('#JobSpacingMinutes').value, 10); return isNaN(v) ? 15 : Math.min(120, Math.max(5, v)); })();

                        config.UploadsPerHour = Math.min(500, Math.max(100, uph)); // Range 100..500
                        var mcp = parseFloat(String(document.querySelector('#MinCallPauseSec').value).replace(',', '.'));
                        if (isNaN(mcp)) { mcp = 0.5; }
                        config.MinCallPauseSec = Math.min(5, Math.max(0.1, mcp)); // 0.1..5
                        config.FollowUpRoundsDownload = document.querySelector('#FollowUpRoundsDownload').checked;
                        config.FollowUpRoundsUpload = document.querySelector('#FollowUpRoundsUpload').checked;
                        config.UploadOnArrival = document.querySelector('#UploadOnArrival').checked;
                                                config.UploadEnabled = document.querySelector('#UploadEnabled').checked;
                        config.DryRun = document.querySelector('#DryRun').checked;
                        config.LogMode = document.querySelector('#LogMode').value;
                        config.OshashRefresh = document.querySelector('#OshashRefresh').value;
        config.PruneMode = document.querySelector('#PruneMode').value;
                        config.QaValidateSrt = document.querySelector('#QaValidateSrt').checked;
                        config.QaCheckSync = document.querySelector('#QaCheckSync').checked;
                        config.QaVerifyLanguage = document.querySelector('#QaVerifyLanguage').checked;
                        config.UploadResolveUnd = document.querySelector('#UploadResolveUnd').checked;
                        config.QaMinCues = document.querySelector('#QaMinCues').checked;
                        config.UploadContinueAfterLimit = document.querySelector('#UploadContinueAfterLimit').checked;
                        config.SkipDirPatterns = document.querySelector('#SkipDirs').value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
                        config.SkipFilePatterns = document.querySelector('#SkipFiles').value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
                        config.DownloadEnabled = document.querySelector('#DownloadEnabled').checked;
                        config.DownloadLanguages = document.querySelector('#DownloadLanguagesDisplay').value;
                        config.DownloadOnlyMissing = document.querySelector('#DownloadOnlyMissing').checked;
                        config.DownloadHearingImpaired = document.querySelector('#DownloadHearingImpaired').checked;
                        config.DownloadRuntimeToleranceSec = (function () { var v = parseInt(document.querySelector('#DownloadRuntimeToleranceSec').value, 10); return isNaN(v) ? 600 : v; })();
                        config.DownloadRequireImdb = document.querySelector('#DownloadRequireImdb').checked;
                        config.QaDownloadMinCues = document.querySelector('#QaDownloadMinCues').checked;
                        config.QaDownloadVerifyLanguage = document.querySelector('#QaDownloadVerifyLanguage').checked;
                        config.DownloadScoreWeightGroup = parseInt(document.querySelector('#DownloadScoreWeightGroup').value) || 1000;
                        config.DownloadScoreWeightToken = parseInt(document.querySelector('#DownloadScoreWeightToken').value) || 10;
                        config.DownloadScoreWeightDownload = parseInt(document.querySelector('#DownloadScoreWeightDownload').value, 10); if (isNaN(config.DownloadScoreWeightDownload)) { config.DownloadScoreWeightDownload = 1; }
                                                config.DownloadOnArrival = document.querySelector('#DownloadOnArrival').checked;
                        config.DownloadMaxCandidatesPerLanguage = (function () { var v = parseInt(document.querySelector('#DownloadMaxCandidatesPerLanguage').value, 10); return isNaN(v) ? 3 : v; })();
                        config.DownloadKeepBestPerLanguage = (function () { var v = parseInt(document.querySelector('#DownloadKeepBestPerLanguage').value, 10); return isNaN(v) ? 1 : Math.min(10, Math.max(1, v)); })();
                        config.DownloadContinueAfterLimit = document.querySelector('#DownloadContinueAfterLimit').checked;
                        config.DownloadDryRun = document.querySelector('#DownloadDryRun').checked;
                        // Union of both library views — a tick in either the
                        // General or the Download tab selects the library.
                        var selected = [];
                        document.querySelectorAll('#LibraryList input[type=checkbox]').forEach(function (cb) {
                            var n = cb.getAttribute('data-libname');
                            if (cb.checked && selected.indexOf(n) === -1) { selected.push(n); }
                        });
                        config.SelectedLibraries = selected;
                        ApiClient.updatePluginConfiguration(SubdlSyncConfig.pluginUniqueId, config).then(function (result) {
                            Dashboard.processPluginConfigurationUpdateResult(result);
                            // F-M70: saved — restart the quota poll so the box shows
                            // The (possibly new) key's numbers within a minute.
                            subdlLoadQuota();
                            subdlStartQuotaPoll();
                        });
                    });
                    return false;
                });

            // Deferred script load — run load routine now if page div is present
            if (document.readyState === "loading") { document.addEventListener("DOMContentLoaded", subdlBootstrapOnce); } else { subdlBootstrapOnce(); }


            // --- live status tab (traffic lights) ---
            function subdlSetLight(id, light, message) {
                var row = document.querySelector(id);
                if (!row) { return; }
                var colors = { red: '#a4262c', yellow: '#ffc107', green: '#107c10', grey: '#767676' };
                var bulb = row.querySelector('.subdl-light');
                var msg = row.querySelector('.subdl-msg');
                if (bulb) { bulb.style.setProperty('--color', colors[light] || colors.grey); }
                if (msg) { msg.textContent = message || ''; }
            }

            function subdlLoadStatus() {
                var box = document.querySelector('#SubdlStatusBox');
                if (!box) { return; }
                subdlSetLight('#SubdlApiStatus', 'grey', 'Checking…');
                subdlSetLight('#TmdbApiStatus', 'grey', 'Checking…');
                var dirList = document.querySelector('#SubdlDirStatusList');
                if (dirList) { dirList.innerHTML = '<div class="fieldDescription">Loading…</div>'; }

                ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Plugins/SubdlSync/Status') }).then(function (data) {
                    subdlSetLight('#SubdlApiStatus', data.subdl.light, data.subdl.message);
                    subdlSetLight('#TmdbApiStatus', data.tmdb.light, data.tmdb.message);

                    if (dirList) {
                        if (!data.directories || data.directories.length === 0) {
                            dirList.innerHTML = '<div class="fieldDescription">No library directories found.</div>';
                        } else {
                            dirList.innerHTML = '';
                            data.directories.forEach(function (d) {
                                var row = document.createElement('div');
                                row.className = 'subdl-status-row';
                                var colors = { red: '#a4262c', yellow: '#ffc107', green: '#107c10', grey: '#767676' };
                                row.innerHTML = '<span class="subdl-light" style="background:' + (colors[d.light] || colors.grey) + ';"></span>' +
                                    '<span class="subdl-label">' + (d.libraryName || '?') + '</span>' +
                                    '<span class="subdl-msg">' + (d.message || '') + ' — ' + (d.path || '') + '</span>';
                                dirList.appendChild(row);
                            });
                        }
                    }

                    // The stamp above the button is NOT written here (30.09.2026). This loader runs
                    // on the 10 s poll and after Save, so writing it would move the line without
                    // anyone pressing Refresh — and it would label the line with the SERVER's
                    // timestamp, which the quota answer caches for 5 minutes. Only the Refresh
                    // button writes it, with the viewer's own click time.
                }).catch(function (err) {
                    subdlSetLight('#SubdlApiStatus', 'red', 'Status probe failed');
                    subdlSetLight('#TmdbApiStatus', 'red', 'Status probe failed');
                    if (dirList) { dirList.innerHTML = '<div class="fieldDescription" style="color:#a4262c;">Unable to load status.</div>'; }
                });
            }

            (function () {
                var refresh = document.querySelector('#RefreshSubdlStatus');
                if (refresh) { refresh.addEventListener('click', subdlLoadStatus); }
                // Status box is in General tab; loaded during config page init below.
            })();
