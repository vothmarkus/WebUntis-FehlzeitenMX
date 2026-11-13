/* Interacts with the report form exposed by the signed-in school. */
function reportCommand(request) {
    if (location.origin !== request.origin) return { stage: "external" };
    const clean = text => String(text ?? "").replace(/\s+/g, " ").trim();
    const visible = element => !!element && !element.hidden && element.getClientRects().length > 0;
    const frames = [...document.querySelectorAll('iframe#embedded-webuntis,iframe[src*="/embedded.do"]')].filter(visible);
    const documents = [document, ...frames.map(frame => frame.contentDocument).filter(Boolean)];
    const exportHost = document.documentElement;
    const activeExport = exportHost?.untisAuswertungExport;

    function reportNotices() {
        return documents.flatMap(doc => [...doc.querySelectorAll('[role="alert"]')].filter(visible));
    }
    function csvLinks(notices) {
        const links = new Set();
        for (const notice of notices) {
            for (const link of notice.querySelectorAll('a[href]')) {
                if (!visible(link)) continue;
                let url;
                try { url = new URL(link.href); }
                catch { continue; }
                if (url.origin === request.origin && !url.username && !url.password &&
                    url.pathname.toLowerCase() === "/webuntis/reports.do" &&
                    url.searchParams.get("format")?.toLowerCase() === "csv" &&
                    url.searchParams.get("msgId") && url.searchParams.has("get") && url.searchParams.has("name")) {
                    links.add(url.href);
                }
            }
        }
        return links;
    }
    const emptyNotices = notices => notices.map(notice => clean(notice.innerText || notice.textContent))
        .filter(message => message.length <= 300 &&
            !/keine berechtigung|kein zugriff|nicht berechtigt/i.test(message) &&
            /\b(?:keine\s+(?:daten|einträge|ergebnisse|fehlzeiten|abwesenheiten|anwesenheiten|verspätungen)|no\s+(?:data|results))\b/i.test(message));
    if (request.action === "exportStatus") {
        if (activeExport?.id !== request.exportId) return {};
        if (!activeExport.clicked) return { exportPrepared: true, exportClicked: false };
        const notices = reportNotices();
        const links = [...csvLinks(notices)].filter(url => !activeExport.previousLinks.has(url) &&
            (!activeExport.expectedReportName || new URL(url).searchParams.get('name')?.startsWith(activeExport.expectedReportName)));
        return {
            exportPrepared: true,
            exportClicked: activeExport.clicked,
            csvUrl: links.length === 1 ? links[0] : null,
            ambiguousCsvLinks: links.length > 1,
            emptyReport: emptyNotices(notices).some(message => !activeExport.previousEmptyNotices.has(message))
        };
    }
    if (request.action === "clearExport") {
        if (activeExport?.id === request.exportId) delete exportHost.untisAuswertungExport;
        return {};
    }

    const loginForm = doc => [...doc.querySelectorAll('input[type="password"]')].some(visible);
    const globalYears = [...document.querySelectorAll('.global-school-year-selection-container .ant-select-selector')];
    const yearLabel = node => /^(\d{4})\s*\/\s*\d{4}$/.exec(clean(node?.getAttribute('title') || node?.textContent))?.[1];
    function topTarget(node) {
        if (!visible(node)) return null;
        const box = node.getBoundingClientRect();
        const x = box.left + box.width / 2;
        const y = box.top + box.height / 2;
        return node.contains(document.elementFromPoint(x, y)) ? { x, y } : null;
    }
    const activeYearSelector = globalYears.find(node => topTarget(node));
    const globalYear = yearLabel(activeYearSelector?.querySelector('.ant-select-selection-item')) ??
        globalYears.filter(visible).map(node => yearLabel(node.querySelector('.ant-select-selection-item'))).find(Boolean);
    if (request.action === 'globalSchoolYear') {
        if (documents.some(loginForm)) return { stage: 'login' };
        const state = { globalYear };
        if (globalYear === String(request.targetSchoolYear)) return state;
        const menu = [...document.querySelectorAll('.ant-select-dropdown:not(.ant-select-dropdown-hidden)')]
            .filter(visible).find(node => [...node.querySelectorAll('.ant-select-item-option')].some(yearLabel));
        if (menu) {
            const entries = [...menu.querySelectorAll('.ant-select-item-option')].filter(visible);
            const years = entries.map(yearLabel).filter(Boolean);
            const option = entries.find(node => yearLabel(node) === String(request.targetSchoolYear));
            if (!option || option.getAttribute('aria-disabled') === 'true' || option.classList.contains('ant-select-item-option-disabled'))
                return { ...state, globalYearUnavailable: years.length > 0 };
            option.scrollIntoView({ block: 'nearest', inline: 'nearest' });
            const target = topTarget(option);
            return { ...state, globalYearOption: true, schoolYearX: target?.x, schoolYearY: target?.y };
        }
        const target = topTarget(activeYearSelector);
        return { ...state, schoolYearX: target?.x, schoolYearY: target?.y };
    }
    if (request.action === 'schoolPeriods') {
        if (documents.some(loginForm)) return { stage: 'login' };
        if (location.pathname !== '/timetable/my-teacher') return {};
        if (globalYear && globalYear !== String(request.targetSchoolYear)) return { stage: 'wrongYear' };
        const times = [...document.querySelectorAll('.timetable-grid-slots-container .timetable-grid-slot-time--time-value')]
            .map(node => clean(node.textContent));
        if (times.length === 0 || times.length % 2 !== 0 || times.some(time => !/^\d{2}:\d{2}$/.test(time))) return {};
        const periods = [];
        for (let i = 0; i < times.length; i += 2) {
            if (times[i] >= times[i + 1] || (i > 0 && times[i] < times[i - 1])) return {};
            periods.push({ number: i / 2 + 1, start: times[i], end: times[i + 1] });
        }
        return { periods };
    }
    if (['lessonList', 'openLessonReport', 'lessonBack', 'prepareLessonExport', 'lessonExportFallback'].includes(request.action))
        return lessonCommand();
    const readingNotes = request.action === "absenceNotes";
    const readingTimes = request.action === "absenceTimes";
    if ((readingNotes || readingTimes) && globalYear && globalYear !== String(request.targetSchoolYear))
        return { stage: "wrongYear" };
    const formId = readingNotes ? "absenceListForm" : readingTimes ? "absenceTimesForm" : "classregReportForm";
    const classSelector = 'select[name="klasseOrStudentgroupId"],select[id="classregReportForm.idklasseId"]';
    const form = documents.map(doc => doc.getElementById(formId))
        .find(item => visible(item?.querySelector(classSelector)));
    if (!form) {
        if (documents.some(loginForm)) return { stage: "login" };
        const surface = documents.map(item => clean(item.body?.innerText).slice(0, 800)).join(" ");
        if (/keine berechtigung|zugriff verweigert|nicht berechtigt|keine zugriffsrechte/i.test(surface)) {
            return { stage: "denied" };
        }
        const atReports = location.pathname.replace(/\/$/, "") === "/class-register-reports" ||
            location.hash.includes("/class-register-reports");
        const classRegister = document.querySelector('a[href="/class-register-overview"],a[href="/class-register-reports"]');
        const landing = location.pathname === "/" && document.readyState === "complete";
        if (visible(classRegister) || landing) {
            return { stage: "signedIn", atReports, hasFrame: frames.length > 0 };
        }
        return { stage: "loading", atReports, hasFrame: frames.length > 0 };
    }

    const doc = form.ownerDocument;
    const page = doc.defaultView;
    const classSelect = form.querySelector(classSelector);
    const studentSelect = form.querySelector('select[name="studentId"],select[id="classregReportForm.idstudentId"]');
    const all = [...(studentSelect?.options ?? [])].find(option => option.value === "-1" || /^[- ]*alle\b/i.test(clean(option.text)));
    const options = select => [...(select?.options ?? [])].filter(option =>
        option.value && option.value !== "-1" && !option.disabled && !/^[- ]*alle\b/i.test(clean(option.text)))
        .map(option => ({ id: option.value, name: clean(option.text) }));
    const reportName = request.kind === "Timetable" ? "Tagesübersicht Klasse" : "Fehlzeiten pro Klasse";
    const dateInputs = ["startDate", "endDate"].map(name => form.querySelector(`[id="${formId}.id${name}"]`));
    // WebUntis replaces the form via AJAX, leaving the iframe's timeOrigin unchanged.
    const formStamp = classSelect.dataset.untisAuswertungForm ??= page.crypto.randomUUID();
    const trackRoster = request.action === "probe" || request.action === "configureRange";
    const rosterStamp = trackRoster && studentSelect && (studentSelect.dataset.untisAuswertungRoster ??= page.crypto.randomUUID());
    const state = {
        stage: "reports",
        globalYear,
        classId: classSelect.value,
        hasStudentFilter: !!all,
        allStudents: !!all && studentSelect.value === all.value,
        hasDateInputs: dateInputs.every(visible) && (readingNotes || readingTimes || request.action === "configureRange" ||
            !!form.querySelector('button[aria-label="CSV-Ausgabe"]')),
        formStamp,
        rosterStamp: rosterStamp ? rosterStamp + ":" + [...studentSelect.options].map(option => option.value).join("|") : null,
        classes: request.includeClasses ? options(classSelect) : [],
        students: request.roster ? options(studentSelect) : []
    };

    const changeSelect = (control, value) => {
        if (control.value === value) return;
        control.value = value;
        control.dispatchEvent(new page.Event("input", { bubbles: true }));
        control.dispatchEvent(new page.Event("change", { bubbles: true }));
    };
    if (request.action === "selectClass") {
        const choice = options(classSelect).find(option => option.id === request.classId);
        if (!choice) return { error: "Die ausgewählte Klasse ist im Berichtsformular nicht verfügbar." };
        if (classSelect.value !== choice.id) changeSelect(classSelect, choice.id);
        return state;
    }
    if (request.action === "selectAll") {
        if (classSelect.value !== request.classId || !all)
            return { error: "Die Auswahl «Alle Schüler*innen» ist für diese Klasse nicht verfügbar." };
        changeSelect(studentSelect, all.value);
        return state;
    }
    if (request.action === "probe") return state;

    function widgetById(id) {
        const legacy = page.dijit?.byId?.(id);
        if (legacy) return legacy;
        try { return page.require?.("dijit/registry")?.byId(id); }
        catch { return null; }
    }
    function dateWidget(field) {
        const id = field.closest("[widgetid]")?.getAttribute("widgetid") ?? field.id;
        return widgetById(id);
    }
    function asIso(value) {
        if (value && typeof value.getFullYear === "function" && Number.isFinite(value.getTime())) {
            return [value.getFullYear(), String(value.getMonth() + 1).padStart(2, "0"),
                String(value.getDate()).padStart(2, "0")].join("-");
        }
        const text = clean(value);
        const german = /^(\d{1,2})\.(\d{1,2})\.(\d{4})$/.exec(text);
        if (german) return [german[3], german[2].padStart(2, "0"), german[1].padStart(2, "0")].join("-");
        if (/^\d{8}$/.test(text)) return text.slice(0, 4) + "-" + text.slice(4, 6) + "-" + text.slice(6);
        return /^\d{4}-\d{2}-\d{2}$/.test(text) ? text : "";
    }
    function pointerTarget(element, block = "center") {
        if (!visible(element)) return null;
        const doc = element.ownerDocument;
        const frame = doc === document ? null : frames.find(item => item.contentDocument === doc);
        if (doc !== document && !frame) return null;
        frame?.scrollIntoView({ block: "nearest", inline: "nearest", behavior: "instant" });
        element.scrollIntoView({ block, inline: "nearest", behavior: "instant" });
        const rect = element.getBoundingClientRect();
        const localX = rect.left + rect.width / 2;
        const localY = rect.top + rect.height / 2;
        const hit = doc.elementFromPoint(localX, localY);
        const box = frame?.getBoundingClientRect();
        const scaleX = frame && frame.offsetWidth ? box.width / frame.offsetWidth : 1;
        const scaleY = frame && frame.offsetHeight ? box.height / frame.offsetHeight : 1;
        const x = box ? box.left + (frame.clientLeft + localX) * scaleX : localX;
        const y = box ? box.top + (frame.clientTop + localY) * scaleY : localY;
        return rect.width > 0 && rect.height > 0 && hit && element.contains(hit) &&
            (!frame || document.elementFromPoint(x, y) === frame) ? { x, y } : null;
    }
    function selectYearPreset(form, dates, selectValue) {
        const isSchoolYear = label => /^(aktuelles schuljahr|current school year)$/i.test(clean(label));
        for (const control of form.querySelectorAll("select")) {
            const choice = [...control.options].find(option => !option.disabled && isSchoolYear(option.text));
            if (!choice || control.disabled) continue;
            const schoolYearSelected = control.value === choice.value;
            selectValue(control, choice.value);
            return { ...dates, schoolYearRequested: true, schoolYearSelected };
        }
        const marker = form.querySelector('input[name="selectedDateRange"]');
        const range = marker?.parentElement?.querySelector('[widgetid^="dijit_form_FilteringSelect_"]');
        if (!range) return { ...dates, schoolYearUnavailable: true };
        const menu = form.ownerDocument.getElementById(range.getAttribute("widgetid") + "_popup");
        const option = [...(menu?.querySelectorAll('[role="option"]') ?? [])]
            .find(item => visible(item) && isSchoolYear(item.textContent));
        const target = pointerTarget(option ?? range.querySelector(".dijitDownArrowButton"), "nearest");
        return { ...dates, schoolYearOption: !!option, schoolYearX: target?.x, schoolYearY: target?.y };
    }
    function lessonCommand() {
        if (documents.some(loginForm)) return { stage: 'login' };
        if (location.pathname !== '/my-lessons' || (globalYear && globalYear !== String(request.targetSchoolYear))) return {};
        const rosterLabel = 'Schüler*innen im Unterricht';
        const report = documents.map(doc => doc.getElementById('repCommand')).find(form => visible(form));
        if (report) {
            const formStamp = report.dataset.untisAuswertungForm ??= report.ownerDocument.defaultView.crypto.randomUUID();
            const state = { stage: 'lessonReport', formStamp };
            if (request.action === 'lessonBack') {
                const back = [...report.ownerDocument.querySelectorAll('button,[role="button"]')].find(node =>
                    visible(node) && clean(report.ownerDocument.getElementById(node.getAttribute('aria-labelledby'))?.textContent ||
                        node.textContent).endsWith('Zurück'));
                if (!back) return state;
                back.focus({ preventScroll: true });
                return { ...state, changedField: 'Zurück' };
            }
            if (request.action === 'lessonList' || request.action === 'openLessonReport') return state;
            const row = [...report.querySelectorAll('tr')].find(tr => clean(tr.cells[0]?.textContent) === rosterLabel);
            const button = row?.querySelector('button[aria-label="CSV-Ausgabe"]');
            if (!button || button.disabled) return { ...state, error: 'Der Bericht «Schüler*innen im Unterricht» bietet keine CSV-Ausgabe.' };
            const metadata = [...report.ownerDocument.querySelectorAll('tr')].filter(row => !row.contains(report) && row.cells.length === 2);
            const value = label => clean(metadata.find(row => clean(row.cells[0].textContent) === label)?.cells[1]?.textContent);
            const subject = value('Fach');
            const group = value('Schülergruppe');
            const classes = value('Klasse') || value('Klassen');
            if (!subject || subject !== request.lesson?.subject || (group && request.lesson.group && group !== request.lesson.group) ||
                (classes && request.lesson.classes.length > 0 && !classNames(classes).some(name => request.lesson.classes.includes(name))))
                return { ...state, error: 'Der geöffnete WebUntis-Bericht gehört nicht zur gewählten Unterrichtszeile.' };
            return exportButton(button, state);
        }
        const form = documents.map(doc => doc.getElementById('lessonListForm')).find(form => visible(form));
        if (!form) {
            const denied = documents.some(doc => /keine berechtigung|zugriff verweigert|nicht berechtigt|keine zugriffsrechte/i
                .test(clean(doc.body?.innerText).slice(0, 800)));
            return denied ? { error: 'WebUntis meldet fehlende Berechtigung für «Mein Unterricht».' } : {};
        }
        const formStamp = form.dataset.untisAuswertungForm ??= form.ownerDocument.defaultView.crypto.randomUUID();
        const state = { stage: 'lessonList', formStamp };
        const from = form.querySelector('[id="lessonListForm.idstartDate"]');
        const to = form.querySelector('[id="lessonListForm.idendDate"]');
        if (asIso(from?.value) !== request.from || asIso(to?.value) !== request.to ||
            [from, to].some(field => field?.getAttribute('aria-invalid') === 'true')) {
            if (request.action === 'openLessonReport') return state;
            return selectYearPreset(form, state, (control, value) => {
                if (control.value === value) return;
                control.value = value;
                control.dispatchEvent(new form.ownerDocument.defaultView.Event('change', { bubbles: true }));
            });
        }
        const table = form.ownerDocument.getElementById('lessonListForm.lessons');
        if (!table) return /nichts anzuzeigen|keine (daten|einträge|unterrichte)/i.test(clean(form.innerText))
            ? { ...state, rangeReady: true, lessons: [] } : state;
        const header = [...table.rows].find(row => [...row.cells].some(cell => cell.tagName === 'TH'));
        const columns = [...(header?.cells ?? [])].map(cell => clean(cell.textContent));
        const subjectIndex = columns.indexOf('Fach');
        const reportIndex = columns.indexOf('Berichte');
        if (subjectIndex < 0 || reportIndex < 0) return { ...state, error: 'Die Unterrichtsliste enthält keine Fach- oder Berichtsspalte.' };
        const occurrences = new Map();
        const lessons = [];
        for (const row of table.rows) {
            if (row.cells[subjectIndex]?.tagName !== 'TD') continue;
            const text = index => clean(row.cells[index]?.textContent);
            const subject = text(subjectIndex);
            if (!subject || /^[-–—]$/.test(subject)) continue;
            const classCell = row.cells[columns.indexOf('Klasse')];
            const classLabels = [...(classCell?.querySelectorAll('[data-dojo-type$="/Tooltip"]') ?? [])]
                .map(node => clean(node.textContent)).filter(Boolean);
            const classes = [...new Set(classLabels.length > 1 ? classLabels : classNames(classCell?.textContent ?? ''))];
            const group = text(columns.indexOf('Schülergruppe'));
            const rowKey = JSON.stringify([classes, group, subject, text(columns.indexOf('Lehrkraft')),
                text(columns.indexOf('Von')), text(columns.indexOf('Bis'))]);
            const ordinal = (occurrences.get(rowKey) ?? 0) + 1;
            occurrences.set(rowKey, ordinal);
            const key = rowKey + ':' + ordinal;
            lessons.push({ key, classes, group, subject });
            if (request.action !== 'openLessonReport' || request.lesson?.key !== key) continue;
            const button = row.cells[reportIndex]?.querySelector('button[aria-label="Berichte"]');
            if (!button || button.disabled) return { ...state, error: 'Für diese Unterrichtszeile sind keine Berichte verfügbar.' };
            button.focus({ preventScroll: true });
            return { ...state, changedField: 'Berichte' };
        }
        if (request.action === 'openLessonReport') return { ...state, error: 'Die Unterrichtszeile wurde in WebUntis nicht wiedergefunden.' };
        return { ...state, rangeReady: true, lessons };
    }
    function classNames(text) {
        return String(text).split(/[,;|\n]+/).map(clean).filter(Boolean);
    }
    function exportButton(button, state) {
        if (request.action === 'exportFallback' || request.action === 'lessonExportFallback') {
            if (activeExport?.id !== request.exportId || activeExport.formStamp !== state.formStamp)
                return { error: 'Das Berichtsformular hat sich vor dem CSV-Klick geändert.' };
            if (!activeExport.clicked) button.click();
            return { ...state, exportClicked: activeExport.clicked };
        }
        const target = request.action === 'prepareLessonExport' ? null : pointerTarget(button);
        const notices = reportNotices();
        const prepared = { id: request.exportId, formStamp: state.formStamp,
            previousLinks: csvLinks(notices), previousEmptyNotices: new Set(emptyNotices(notices)), clicked: false };
        exportHost.untisAuswertungExport = prepared;
        button.addEventListener('click', () => { prepared.clicked = true; }, { capture: true, once: true });
        if (request.action === 'prepareLessonExport') {
            prepared.expectedReportName = 'LessonStudentList';
            button.focus({ preventScroll: true });
        }
        return { ...state, exportX: target?.x ?? null, exportY: target?.y ?? null };
    }
    if (request.action === "schoolYear" || request.action === "schoolYearDates") {
        if (!state.hasDateInputs) return state;
        const dates = { ...state, schoolYearFrom: asIso(dateInputs[0].value), schoolYearTo: asIso(dateInputs[1].value) };
        if (request.action === "schoolYearDates") return dates;
        return selectYearPreset(form, dates, changeSelect);
    }
    if (readingNotes || readingTimes) {
        if (request.waitForFormStamp === formStamp) return state;
        const className = clean(request.className).toLowerCase();
        const nameMatches = readingTimes && clean(classSelect.selectedOptions?.[0]?.text).toLowerCase() === className;
        if (readingTimes ? !nameMatches : classSelect.value !== request.classId) {
            const choice = request.className && readingTimes
                ? options(classSelect).filter(item => item.name.toLowerCase() === className)
                : options(classSelect).filter(item => item.id === request.classId);
            if (choice.length !== 1) return { ...state, notesUnavailable: true };
            if (choice[0].id !== classSelect.value) {
                changeSelect(classSelect, choice[0].id);
                return { ...state, changedField: "Klasse" };
            }
        }
        const filterNames = readingTimes ? ['subjectId', 'absenceReasonId', 'periodExcuseStatusId'] : ['absenceReasonId', 'excuseStatusId', 'guardianNotified'];
        for (const select of [studentSelect, ...filterNames
            .map(name => form.querySelector(`select[name="${name}"]`))]) {
            if (!select) continue;
            const unfiltered = [...select.options].find(item => !item.disabled && /^[- ]*alle\b/i.test(clean(item.text)));
            if (unfiltered && select.value !== unfiltered.value) {
                changeSelect(select, unfiltered.value);
                return { ...state, changedField: select.name };
            }
        }
    }
    const configuringRange = request.action === "configureRange";
    const classMatches = readingTimes ? clean(classSelect.selectedOptions?.[0]?.text).toLowerCase() === clean(request.className).toLowerCase()
        : classSelect.value === request.classId;
    if (!configuringRange && (!classMatches || !state.allStudents)) {
        return { error: "Die ausgewählte Klasse oder der Schülerfilter wurde noch nicht übernommen." };
    }
    if (dateInputs.some(field => !field)) return { error: "Die Datumsfelder der Berichtsseite fehlen." };

    function setDate(field, iso) {
        const widget = dateWidget(field);
        const [year, month, day] = iso.split("-").map(Number);
        if (widget?.set) {
            widget.set("value", new page.Date(year, month - 1, day), true);
            return;
        }
        field.value = field.type === "date" ? iso : iso.split("-").reverse().join(".");
        field.dispatchEvent(new page.Event("input", { bubbles: true }));
        field.dispatchEvent(new page.Event("change", { bubbles: true }));
        field.dispatchEvent(new page.FocusEvent("blur"));
    }
    const wantedDates = [request.from, request.to];
    const dateMatches = dateInputs.map((field, i) => {
        const widget = dateWidget(field);
        const widgetDate = asIso(widget?.get?.("value"));
        return asIso(field.value) === wantedDates[i] &&
            (!widget || widgetDate === wantedDates[i]) &&
            field.getAttribute("aria-invalid") !== "true" &&
            field.closest('[role="combobox"]')?.getAttribute("aria-invalid") !== "true";
    });
    function changeDate() {
        const order = request.from > asIso(dateInputs[1].value) ? [1, 0] : [0, 1];
        const index = order.find(item => !dateMatches[item]);
        setDate(dateInputs[index], wantedDates[index]);
        return index === 0 ? "Startdatum" : "Enddatum";
    }
    if (configuringRange) {
        if (!state.hasDateInputs || request.waitForFormStamp === formStamp) return state;
        return dateMatches.every(Boolean) ? { ...state, rangeReady: true } : { ...state, changedField: changeDate() };
    }
    if (request.action === "readRoster") {
        const rangeReady = dateMatches.every(Boolean);
        return { ...state, rangeReady, students: rangeReady ? state.students : [] };
    }
    if (readingTimes) {
        if (!dateMatches.every(Boolean)) return { ...state, changedField: changeDate() };
        for (const [id, checked] of [['withAbsences1', !request.lateOnly], ['withLateness1', !!request.lateOnly],
            ['onlyExams1', false], ['onlyOpen1', false]].sort((a, b) => Number(b[1]) - Number(a[1]))) {
            const flag = form.querySelector(`input[type="checkbox"][id="${id}"]`);
            if (!flag || (flag.checked !== checked && flag.disabled)) return { ...state, notesUnavailable: true };
            if (flag.checked !== checked) {
                flag.click();
                return { ...state, changedField: id };
            }
        }
        const table = doc.getElementById('absenceTimesForm.absences');
        if (!table) {
            const text = clean(form.innerText);
            return /keine\s+(daten|einträge|ergebnisse|fehlzeiten)|0\s+elemente?\s+gefunden/i.test(text)
                ? { ...state, timesReady: true, times: [] } : state;
        }
        if (form.querySelector('.pagination,.pager,[class*="paging" i]')) return { ...state, notesUnavailable: true };
        const rows = [...table.rows];
        const header = rows.find(row => [...row.cells].some(cell => cell.tagName === 'TH'));
        if (!header) return { ...state, notesUnavailable: true };
        const labels = [...header.cells].map(cell => clean(cell.textContent).toLowerCase());
        const field = label => labels.indexOf(label);
        if (['schüler*innen', 'klasse', 'datum', 'zeit', 'fach', 'fehlstd.', 'fehlmin.', 'status'].some(label => field(label) < 0))
            return { ...state, notesUnavailable: true };
        const groupLabel = classSelect.selectedOptions[0].closest('optgroup')?.label ?? '';
        const groupIsClass = !/gruppe/i.test(groupLabel);
        const times = [];
        for (const row of rows.filter(row => row.cells.length === labels.length && row.cells[0].tagName === 'TD')) {
            const cell = label => clean(row.cells[field(label)]?.textContent);
            const name = cell('schüler*innen');
            const day = asIso(cell('datum'));
            const rowClass = cell('klasse');
            if (!name || !day || day < request.from || day > request.to || !rowClass ||
                (groupIsClass && rowClass.toLowerCase() !== clean(request.className).toLowerCase())) return { ...state, notesUnavailable: true };
            const time = /^(\d{1,2}:\d{2})\s*[-–]\s*(\d{1,2}:\d{2})$/.exec(cell('zeit'));
            times.push({ name, day, subject: cell('fach'), start: time?.[1] ?? '', end: time?.[2] ?? '',
                units: cell('fehlstd.'), minutes: cell('fehlmin.'), status: cell('status'), reason: cell('abwesenheitsgrund'),
                text: row.cells[field('text')]?.textContent?.trim() ?? '', counts: !row.cells[field('zählt')]?.querySelector('input[type="checkbox"]:not(:checked)') });
        }
        if (times.length === 0 && !/keine\s+(daten|einträge|ergebnisse|fehlzeiten)|0\s+elemente?\s+gefunden/i.test(clean(form.innerText)))
            return state;
        return { ...state, timesReady: true, times };
    }
    if (readingNotes) {
        if (!dateMatches.every(Boolean)) return { ...state, changedField: changeDate() };
        const table = doc.getElementById("absenceListForm.absences");
        const rows = [...(table?.rows ?? [])];
        const header = rows.find(row => [...row.cells].some(cell => cell.tagName === "TH"));
        if (!header) return state;
        const headings = [...header.cells].map(cell => clean(cell.textContent).toLowerCase());
        const name = headings.indexOf("schüler*innen");
        const from = headings.indexOf("von");
        const to = headings.indexOf("bis");
        const reason = headings.indexOf("grund");
        const text = headings.indexOf("text");
        const excuseText = headings.indexOf("entschuldigungstext");
        if (name < 0 || from < 0 || to < 0 || (reason < 0 && text < 0 && excuseText < 0))
            return { ...state, notesUnavailable: true };
        const absenceNotes = rows.filter(row => row.cells.length === headings.length && row.cells[name].tagName === "TD")
            .map(row => {
                const cell = index => row.cells[index]?.textContent?.trim() ?? "";
                return { name: clean(cell(name)), from: asIso(cell(from)), to: asIso(cell(to)),
                    reason: cell(reason), text: cell(text), excuseText: cell(excuseText) };
            }).filter(note => note.name && note.from && note.to && (note.reason || note.text || note.excuseText));
        return { ...state, notesReady: true, absenceNotes };
    }
    const isAbsence = request.kind === "Absences" || request.kind === "Lateness";
    const group = form.querySelector("select[name='absClassGroup']");
    const excuse = form.querySelector("select[name='classExcuseStatusId']");
    const perPeriod = [...(group?.options ?? [])].find(option => clean(option.text).toLowerCase() === "pro stunde");
    const statusLabel = request.excusedOnly ? /^\[\s*entschuldigt\s*\]$/i : /^[- ]*alle\b/i;
    const statusFilter = [...(excuse?.options ?? [])].find(option => !option.disabled && statusLabel.test(clean(option.text)));
    const flag = id => form.querySelector(`input[type="checkbox"][id="${id}"]`);
    const absenceFlag = flag("withAbsences1");
    const lateFlag = flag("withLateness1");
    const allStudentsFlag = flag("allStudents1");
    if (request.excusedOnly && !statusFilter) {
        return { error: "WebUntis bietet den Berichtsfilter «[entschuldigt]» nicht an." };
    }
    if (isAbsence && (!perPeriod || !statusFilter || !absenceFlag || !lateFlag || !allStudentsFlag)) {
        return { error: "Die Filter für den Fehlzeitenbericht sind unvollständig." };
    }
    const controls = [
        [absenceFlag, request.kind !== "Lateness", "Fehlzeiten", true],
        [lateFlag, request.kind !== "Absences", "Verspätungen", true],
        [allStudentsFlag, true, "Alle Schüler*innen", true],
        [flag("excludeNotCountingAbsences1"), false, "Nur zählende Abwesenheiten", true]
    ].sort((a, b) => Number(b[1]) - Number(a[1]));
    if (isAbsence) controls.push(
        [group, perPeriod.value, "Gruppierung nach Stunden", false],
        [excuse, statusFilter.value, "Entschuldigungsstatus", false],
        [flag("absClassSorted1"), false, "Sortierung der Fehlzeiten", false],
        [flag("classregReportForm.idseparateAbsentDays"), false, "Getrennte Abwesenheitstage", false]
    );
    const change = controls.find(([field, value]) => field &&
        (typeof value === "boolean" ? field.checked : field.value) !== value);
    const rangeReady = dateMatches.every(Boolean);
    const flagsReady = !change;
    const verified = { ...state, rangeReady, flagsReady };

    if (request.action === "configure") {
        if (!rangeReady) return { ...verified, changedField: changeDate(), reloadExpected: true };
        if (change) {
            const [field, value, changedField, reloadExpected] = change;
            if (field.disabled) return { error: "Die Berichtsoption «" + changedField + "» ist gesperrt." };
            if (typeof value === "boolean") field.click();
            else changeSelect(field, value);
            return { ...verified, changedField, reloadExpected };
        }
        return verified;
    }
    if (request.action !== "prepareExport" && request.action !== "exportFallback") {
        return { error: "Unbekannte Berichtsaktion." };
    }
    if (!rangeReady || !flagsReady) return { error: "WebUntis hat Zeitraum oder Berichtsfilter noch nicht bestätigt." };

    const row = [...form.querySelectorAll("tr")].find(tr => tr.cells.length > 0 &&
        clean(tr.cells[0].innerText || tr.cells[0].textContent) === reportName);
    const button = row?.querySelector('button[aria-label="CSV-Ausgabe"],[title="CSV-Ausgabe"]');
    if (!button || button.disabled || button.getAttribute("aria-disabled") === "true") {
        return { error: "Die CSV-Schaltfläche für „" + reportName + "“ ist nicht verfügbar." };
    }
    return exportButton(button, state);
}
