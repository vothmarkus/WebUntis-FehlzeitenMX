/* Interacts with the report form exposed by the signed-in school. */
function reportCommand(request) {
    if (location.origin !== request.origin) return { stage: "external" };
    const clean = text => String(text ?? "").replace(/\s+/g, " ").trim();
    const visible = element => !!element && !element.hidden && element.getClientRects().length > 0;
    const frames = [...document.querySelectorAll('iframe#embedded-webuntis,iframe[src*="/embedded.do"]')].filter(visible);
    const documents = [document, ...frames.map(frame => frame.contentDocument).filter(Boolean)];
    const exportHost = document.documentElement;
    const activeExport = exportHost?.untisAuswertungExport;
    const editor = exportHost?.fehlzeitenMxAbsenceEditor;

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

    const loginForm = doc => [...doc.querySelectorAll('input[type="password"]')].find(visible);
    if (request.mobileLogin === true) fitMobileLogin(documents.map(loginForm).find(Boolean));
    if (request.openBrowserLogin === true &&
        /willkommen bei web\s*untis/i.test(clean(document.body?.innerText || document.body?.textContent))) {
        const actions = [...document.querySelectorAll('a,button,[role="button"]')].filter(node =>
            visible(node) && clean(node.innerText || node.textContent).toLowerCase() === "im browser öffnen");
        if (actions.length === 1) {
            const target = pointerTarget(actions[0], "nearest");
            return { stage: "browserWelcome", browserLoginX: target?.x, browserLoginY: target?.y };
        }
    }
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
    const absenceAction = ["absenceActions", "openExcuse", "excuseState"].includes(request.action);
    const absenceList = readingNotes || absenceAction;
    const readingTimes = request.action === "absenceTimes";
    if (absenceAction && location.pathname.replace(/\/$/, "") !== "/absences")
        return { stage: documents.some(loginForm) ? "login" : "loading" };
    if ((absenceList || readingTimes) && globalYear && globalYear !== String(request.targetSchoolYear))
        return { stage: "wrongYear" };
    if (request.action === "excuseState")
        return documents.some(loginForm) ? { stage: "login" } : absenceEditorState();
    const formId = absenceList ? "absenceListForm" : readingTimes ? "absenceTimesForm" : "classregReportForm";
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
        const landing = request.allowLandingAsSignedIn !== false && location.pathname === "/" && document.readyState === "complete";
        if (visible(classRegister) || request.mobileLogin === true && classRegister || landing) {
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
        hasDateInputs: dateInputs.every(visible) && (absenceList || readingTimes || request.action === "configureRange" ||
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
    if (absenceAction) return absenceActions();
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

    function widgetById(id, ownerWindow = page) {
        const legacy = ownerWindow.dijit?.byId?.(id);
        if (legacy) return legacy;
        try { return ownerWindow.require?.("dijit/registry")?.byId(id); }
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
                return lessonButton(back, state, 'Zurück');
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
            return lessonButton(button, state, 'Berichte');
        }
        if (request.action === 'openLessonReport') return { ...state, error: 'Die Unterrichtszeile wurde in WebUntis nicht wiedergefunden.' };
        return { ...state, rangeReady: true, lessons };
    }
    function lessonButton(button, state, label) {
        if (request.mobileLesson) {
            const target = pointerTarget(button, "nearest");
            return { ...state, changedField: label, lessonActionX: target?.x, lessonActionY: target?.y };
        }
        button.focus({ preventScroll: true });
        return { ...state, changedField: label };
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
        const target = request.action === 'prepareLessonExport' && !request.mobileLesson ? null : pointerTarget(button);
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
    function matchesDate(field, i) {
        const widget = dateWidget(field);
        const widgetDate = asIso(widget?.get?.("value"));
        const wanted = i === 0 ? request.from : request.to;
        return asIso(field.value) === wanted &&
            (!widget || widgetDate === wanted) &&
            field.getAttribute("aria-invalid") !== "true" &&
            field.closest('[role="combobox"]')?.getAttribute("aria-invalid") !== "true";
    }
    function changeDate() {
        const order = request.from > asIso(dateInputs[1].value) ? [1, 0] : [0, 1];
        const index = order.find(i => !matchesDate(dateInputs[i], i));
        setDate(dateInputs[index], index === 0 ? request.from : request.to);
        return index === 0 ? "Startdatum" : "Enddatum";
    }

    function fitMobileLogin(field) {
        const previous = exportHost.fehlzeitenMxLoginLayout;
        if (previous?.field === field) return;
        previous?.restore();
        delete exportHost.fehlzeitenMxLoginLayout;
        if (!field) return;

        const owner = field.ownerDocument;
        const frame = owner.defaultView.frameElement;
        const bodyAttr = "data-fehlzeiten-mx-login-content";
        const outerAttr = "data-fehlzeiten-mx-login-frame-parent";
        const frameAttr = "data-fehlzeiten-mx-login-frame";
        const markers = [];
        const sheets = [];
        let viewport;
        if (!document.querySelector('meta[name="viewport"]') && document.head) {
            viewport = document.createElement("meta");
            viewport.name = "viewport";
            viewport.content = "width=device-width, initial-scale=1";
            document.head.append(viewport);
        }
        markParents(field.form ?? field, bodyAttr);
        addRules(owner, `
            [${bodyAttr}] { overflow: visible !important; height: auto !important; max-height: none !important; }
            html[${bodyAttr}] { overflow-y: auto !important; min-height: 100% !important; }
        `);
        if (frame) {
            markParents(frame.parentElement, outerAttr);
            mark(frame, frameAttr);
            addRules(document, `
                [${outerAttr}] { overflow: visible !important; transform: none !important; contain: none !important; }
                [${frameAttr}] {
                    position: fixed !important; inset: 0 !important; width: 100% !important; height: 100% !important;
                    max-height: none !important; max-width: none !important; margin: 0 !important;
                    padding: 0 !important; border: 0 !important; z-index: 2147483647 !important;
                }
            `);
        }
        exportHost.fehlzeitenMxLoginLayout = { field, restore() {
            for (const [node, attr] of markers) node.removeAttribute(attr);
            for (const [doc, sheet] of sheets)
                doc.adoptedStyleSheets = doc.adoptedStyleSheets.filter(active => active !== sheet);
            viewport?.remove();
        } };

        function markParents(node, attr) {
            for (let element = node; element; element = element.parentElement) mark(element, attr);
        }

        function mark(node, attr) {
            node.setAttribute(attr, "");
            markers.push([node, attr]);
        }

        function addRules(doc, css) {
            const sheet = new doc.defaultView.CSSStyleSheet();
            sheet.replaceSync(css);
            doc.adoptedStyleSheets = [...doc.adoptedStyleSheets, sheet];
            sheets.push([doc, sheet]);
        }
    }

    function absenceActions() {
        if (request.waitForFormStamp === formStamp) return state;
        if (!globalYear) return state;
        const opening = request.action === "openExcuse";
        const entireClass = request.classAbsences === true;
        const day = request.absenceDay;
        const isoDay = /^\d{4}-\d{2}-\d{2}$/;
        if (!isoDay.test(request.from) || !isoDay.test(request.to) || request.from > request.to ||
            ((!entireClass || opening) && (!isoDay.test(day) || day < request.from || day > request.to)) ||
            (!entireClass && (day !== request.from || day !== request.to)))
            return unavailable("Der gewünschte WebUntis-Tag konnte nicht bestätigt werden.");
        const key = absenceLabel;
        const wantedClass = options(classSelect).filter(option =>
            option.id === request.classId && key(option.name) === key(request.className));
        if (wantedClass.length !== 1) return unavailable("Die Klasse ist in WebUntis nicht eindeutig verfügbar.");
        if (classSelect.value !== wantedClass[0].id) {
            changeSelect(classSelect, wantedClass[0].id);
            return changed("Klasse");
        }
        const students = options(studentSelect).filter(option => /^\d+$/.test(option.id));
        const person = request.absencePerson;
        let student;
        if (!entireClass || opening) {
            const matches = students.filter(option => key(option.name) === key(person?.name) && (!person?.id || option.id === person.id));
            if (matches.length !== 1) return unavailable("Die Person konnte in der WebUntis-Klassenliste nicht eindeutig bestätigt werden.");
            [student] = matches;
        }
        const studentFilter = entireClass ? [...(studentSelect?.options ?? [])].find(option => !option.disabled &&
            (option === all || /^(alle\b|[-–—]+$)/i.test(clean(option.text)))) : student;
        if (!studentFilter) return unavailable("WebUntis bietet keine Abwesenheitsliste für die ganze Klasse an.");
        const studentFilterValue = entireClass ? studentFilter.value : studentFilter.id;
        if (studentSelect.value !== studentFilterValue) {
            if (studentSelect.disabled) return unavailable("Der Schülerfilter lässt sich in WebUntis nicht ändern.");
            changeSelect(studentSelect, studentFilterValue);
            return changed("Schülerfilter");
        }
        for (const name of ["absenceReasonId", "excuseStatusId", "guardianNotified"]) {
            const filter = form.querySelector(`select[name="${name}"]`);
            const unfiltered = [...(filter?.options ?? [])].find(option => !option.disabled && /^[- ]*alle\b/i.test(clean(option.text)));
            if (filter && unfiltered && filter.value !== unfiltered.value) {
                changeSelect(filter, unfiltered.value);
                return changed(name);
            }
        }
        const minors = form.querySelector('input[type="checkbox"][name="withMinorStudentsOnly"]:checked');
        if (minors) {
            if (minors.disabled) return unavailable("WebUntis bietet nur eine gefilterte Abwesenheitsliste an.");
            minors.click();
            return changed("Altersfilter");
        }
        const search = form.querySelector('input[name="searchString"]');
        if (search?.value.trim()) {
            const submit = form.querySelector("button.icon-search");
            if (!enabled(submit)) return unavailable("Der Suchfilter konnte in WebUntis nicht zurückgesetzt werden.");
            search.value = "";
            submit.click();
            return changed("Suche");
        }
        if (!state.hasDateInputs || dateInputs.some(field => !field)) return state;
        if (!dateInputs.every(matchesDate)) return changed(changeDate());
        const table = doc.getElementById("absenceListForm.absences");
        if (!table && entireClass && !opening &&
            !/keine\s+(daten|einträge|ergebnisse|abwesenheiten)|0\s+elemente?\s+gefunden/i.test(clean(form.innerText))) return state;
        if (!table)
            return opening ? unavailable("Der Eintrag fehlt in der aktuellen WebUntis-Abwesenheitsliste.") :
                { ...state, absenceActionsReady: true, editableAbsences: [] };
        const headings = [...(table.tHead?.rows[0]?.cells ?? [])].map(cell => key(cell.textContent));
        const fields = ["schüler*innen", "klasse", "von", "beginn", "bis", "ende", "status"].map(label => headings.indexOf(label));
        if (fields.some(index => index < 0)) return unavailable("Die Spalten der WebUntis-Abwesenheitsliste konnten nicht bestätigt werden.");
        if (form.querySelector('.pagination,.pager,[class*="paging" i]'))
            return unavailable("WebUntis liefert nur einen Ausschnitt der Abwesenheitsliste.");
        const reasonField = headings.indexOf("grund");
        const [personField, classField, fromField, startField, toField, endField, statusField] = fields;
        const idsByName = new Map();
        for (const candidate of students) {
            const name = key(candidate.name);
            idsByName.set(name, idsByName.has(name) ? "" : candidate.id);
        }
        const matchesById = new Map();
        for (const row of [...table.tBodies].flatMap(body => [...body.rows])) {
            if (row.cells.length !== headings.length) continue;
            const id = row.querySelector('input[type="checkbox"][name="selId"]')?.value;
            if (!id || !/^\d+$/.test(id)) continue;
            const text = index => clean(row.cells[index]?.textContent);
            const studentName = text(personField);
            const studentId = entireClass ? idsByName.get(key(studentName)) ?? "" : student.id;
            if ((!entireClass && key(studentName) !== key(student.name)) || key(text(classField)) !== key(request.className))
                return unavailable("WebUntis zeigt Zeilen außerhalb des bestätigten Filters an.");
            const fromDay = asIso(text(fromField));
            const toDay = asIso(text(toField));
            if (!fromDay || !toDay || fromDay > toDay || fromDay > request.to || toDay < request.from) continue;
            if (matchesById.has(id)) return unavailable("WebUntis zeigt dieselbe Abwesenheitskennung mehrfach an.");
            const statusCell = row.cells[statusField];
            const excuse = statusCell.querySelector("button.icon-questionmark") ??
                [...statusCell.querySelectorAll("a[href]")].find(link =>
                    /^javascript:absenceListForm\.excuseAbsence\(\s*(\d+)\s*,\s*\d+\s*\);\s*void\(0\);?$/i
                        .exec(link.getAttribute("href") ?? "")?.[1] === id);
            const status = clean(excuse?.title || excuse?.textContent || statusCell.textContent).replace(/\s*\/\s*\d+$/, "");
            matchesById.set(id, {
                value: { id, studentName, studentId,
                    from: fromDay, to: toDay, start: absenceTime(text(startField)), end: absenceTime(text(endField)),
                    status, reason: text(reasonField), canExcuse: enabled(excuse) && !!studentId },
                excuse
            });
        }
        if (!opening) {
            const notes = readAbsenceNotes(table);
            return { ...state, absenceActionsReady: true, editableAbsences: [...matchesById.values()].map(row => row.value),
                notesReady: notes != null, absenceNotes: notes ?? [] };
        }
        const row = matchesById.get(request.absenceRow?.id);
        const picked = request.absenceRow;
        const control = row?.excuse;
        if (!row || !enabled(control) || row.value.studentId !== student.id || day < row.value.from || day > row.value.to ||
            row.value.from !== picked.from || row.value.to !== picked.to ||
            row.value.start !== picked.start || row.value.end !== picked.end)
            return unavailable("Dieser Eintrag hat sich geändert oder ist gesperrt. Wähle die Tageszelle erneut.");
        if (editor || [...doc.querySelectorAll('[role="dialog"]')].some(visible))
            return unavailable("In der WebUntis-Ansicht ist bereits ein Dialog geöffnet.");
        exportHost.fehlzeitenMxAbsenceEditor = { id: request.editorId, doc, person: student.name, saveRequested: false };
        // Return before WebUntis opens the editor or runs any of its native prompts.
        page.setTimeout(() => control.click(), 0);
        return { ...state, absenceEditorRequested: true };

        function changed(changedField) { return { ...state, changedField }; }
        function unavailable(error) { return { ...state, error }; }
    }

    function enabled(control) {
        return visible(control) && !control.disabled && control.getAttribute("aria-disabled") !== "true" &&
            !control.classList.contains("dijitDisabled");
    }

    function absenceLabel(value) { return clean(value).normalize("NFC").toLowerCase(); }

    function absenceTime(value) {
        const time = clean(value);
        return /^\d:\d{2}$/.test(time) ? "0" + time : time;
    }

    function readAbsenceNotes(table) {
        const rows = [...(table?.rows ?? [])];
        const header = rows.find(row => [...row.cells].some(cell => cell.tagName === "TH"));
        if (!header) return null;
        const headings = [...header.cells].map(cell => clean(cell.textContent).toLowerCase());
        const name = headings.indexOf("schüler*innen");
        const from = headings.indexOf("von");
        const to = headings.indexOf("bis");
        const reason = headings.indexOf("grund");
        const text = headings.indexOf("text");
        const excuseText = headings.indexOf("entschuldigungstext");
        if (name < 0 || from < 0 || to < 0 || (reason < 0 && text < 0 && excuseText < 0)) return null;
        return rows.filter(row => row.cells.length === headings.length && row.cells[name].tagName === "TD")
            .map(row => {
                const cell = index => row.cells[index]?.textContent?.trim() ?? "";
                return { name: clean(cell(name)), from: asIso(cell(from)), to: asIso(cell(to)),
                    reason: cell(reason), text: cell(text), excuseText: cell(excuseText) };
            }).filter(note => note.name && note.from && note.to && (note.reason || note.text || note.excuseText));
    }

    function absenceEditorState() {
        if (editor?.id !== request.editorId) return {};
        const { doc } = editor;
        if (editor.dialog) {
            const closed = editor.closed === true || !visible(editor.dialog);
            return { absenceEditorVisible: !closed, absenceEditorClosed: closed,
                absenceEditorSaveRequested: editor.saveRequested, editorWidth: editor.width, editorHeight: editor.height };
        }
        if (!documents.includes(doc)) return {};
        const form = doc.getElementById("excuseForm");
        const current = form?.closest('[role="dialog"]');
        if (!visible(current)) return {};
        const title = absenceLabel(current.querySelector(".dijitDialogTitle")?.textContent);
        if (title !== absenceLabel("Entschuldigung für " + editor.person))
            return { error: "WebUntis hat die Person im Entschuldigungsdialog nicht bestätigt." };

        const pane = form.closest(".dijitDialogPaneContent");
        if (!pane || !current.contains(pane)) return { error: "Das WebUntis-Entschuldigungsformular konnte nicht erkannt werden." };
        const page = doc.defaultView;
        const frame = page.frameElement;
        const mobile = request.mobileEditor === true;
        if (mobile) {
            const head = document.head;
            const viewport = head?.querySelector('meta[name="viewport"]') ?? document.createElement("meta");
            viewport.name = "viewport";
            viewport.content = "width=device-width, initial-scale=1, maximum-scale=4, user-scalable=yes";
            if (!viewport.isConnected) head?.append(viewport);
        }

        // The host sizes the window. Keep Dijit from fitting this dialog inside it a second time.
        const widget = widgetById(current.getAttribute("widgetid") || current.id, page);
        if (widget?.domNode === current) {
            if (Number.isFinite(widget.maxRatio)) widget.maxRatio = Infinity;
            if (mobile && typeof widget.autofocus === "boolean") widget.autofocus = false;
        }

        // The scroll area can be an inner wrapper. Let every ancestor of the original form grow with it.
        const parts = [current.querySelector(".dijitDialogTitleBar"), current.querySelector(".dijitDialogPaneActionBar")].filter(Boolean);
        for (let part = form; part !== current; part = part.parentElement) parts.push(part);
        const naturalSize = { width: "auto", height: "auto", "min-width": "0px", "min-height": "0px",
            "max-width": "none", "max-height": "none", overflow: "visible" };
        const dialogStyle = { ...naturalSize, position: "absolute", inset: "0px auto auto 0px",
            width: "max-content", margin: "0px", "box-sizing": "border-box", "box-shadow": "none" };
        if (mobile) Object.assign(dialogStyle, { position: "fixed", inset: "0px",
            width: "100%", height: "100%", overflow: "auto" });
        const partStyle = { ...naturalSize, position: "relative", inset: "auto" };
        const content = mobile ? [...current.children] : [];
        const fieldSelector = 'input:not([type="hidden"]):not([type="button"]):not([type="submit"]):not([type="reset"]),' +
            'select,textarea,[role="combobox"],.dijitTextBox,.dijitSelect';
        if (mobile) {
            fitMobileViewport(current);
            if (frame) fitMobileViewport(frame);
        }
        applyLayout();

        addLayout(doc, `
            html { background: white !important; overflow: auto !important; }
            body { margin: 0 !important; min-width: 0 !important; min-height: 0 !important; }
            [data-fehlzeiten-mx-excuse-background] { display: none !important; }
        `);
        const background = doc.getElementById("app") ?? doc.getElementById("absencelist");
        for (const layer of [background, ...doc.querySelectorAll(".dijitDialogUnderlayWrapper")]) {
            if (!layer || layer.contains(current)) continue;
            layer.inert = true;
            layer.setAttribute("data-fehlzeiten-mx-excuse-background", "");
        }

        if (frame && !mobile) {
            frame.setAttribute("data-fehlzeiten-mx-excuse-frame", "");
            addLayout(frame.ownerDocument, `
                html, body { overflow: hidden !important; }
                [data-fehlzeiten-mx-excuse-frame] {
                    position: fixed !important; inset: 0 !important;
                    width: 100vw !important; height: 100vh !important;
                    min-width: 0 !important; min-height: 0 !important;
                    max-width: none !important; max-height: none !important;
                    margin: 0 !important; padding: 0 !important; border: 0 !important;
                    box-sizing: border-box !important; z-index: 2147483647 !important;
                }
            `);
        }
        page.scrollTo(0, 0);
        if (mobile) {
            // Dijit focuses the first editable field on open. Defer text focus until the teacher taps the form.
            if (form.contains(doc.activeElement)) doc.activeElement.blur();
            fitFormWidth();
        }
        const size = measure();
        if (!size) return { error: "Die Größe des WebUntis-Entschuldigungsformulars konnte nicht bestimmt werden." };
        if (!mobile) {
            // Fix the measured width before the desktop window changes, so wrapping stays stable.
            dialogStyle.width = (size.width - 2) + "px";
            applyLayout();
        }
        Object.assign(editor, size, { dialog: current });

        doc.addEventListener("click", onButton, true);
        form.addEventListener("submit", onSave);
        const observer = new page.MutationObserver(() => {
            if (visible(current)) applyLayout();
            else notify("closed");
        });
        observer.observe(current, { attributes: true, attributeFilter: ["style", "hidden", "aria-hidden"] });
        for (const part of parts) observer.observe(part, { attributes: true, attributeFilter: ["style"] });
        observer.observe(current.parentElement, { childList: true });
        const resizeObserver = new page.ResizeObserver(() => {
            if (mobile) fitFormWidth();
            const size = measure();
            if (!size || (size.width === editor.width && size.height === editor.height)) return;
            if (!mobile) {
                dialogStyle.width = (size.width - 2) + "px";
                applyLayout();
            }
            Object.assign(editor, size);
            notify("size");
        });
        resizeObserver.observe(current);
        resizeObserver.observe(form);
        if (mobile) for (const field of form.querySelectorAll(fieldSelector)) resizeObserver.observe(field);
        page.addEventListener("pagehide", () => notify("closed"), { once: true });
        document.defaultView.addEventListener("pagehide", () => notify("closed"), { once: true });
        return { absenceEditorVisible: true, editorWidth: editor.width, editorHeight: editor.height };

        function applyLayout() {
            setLayout(current, dialogStyle);
            for (const part of parts) setLayout(part, partStyle);
        }

        function fitFormWidth() {
            const width = current.clientWidth;
            if (width <= 0) return;
            const fields = [...form.querySelectorAll(fieldSelector)].filter(visible);
            if (fields.length === 0) return;
            const { scrollLeft, scrollTop } = current;
            for (const part of content) setLayout(part, { zoom: "1" });
            const origin = current.getBoundingClientRect().left + current.clientLeft - current.scrollLeft;
            let left = Infinity;
            let right = 0;
            for (const field of fields) {
                const bounds = field.getBoundingClientRect();
                if (bounds.width <= 0 || bounds.height <= 0) continue;
                left = Math.min(left, bounds.left - origin);
                right = Math.max(right, bounds.right - origin);
            }
            // Mirror the form's left inset at its rightmost field. The title and pane can be wider.
            if (!Number.isFinite(left) || right <= left || right <= 0) return;
            const scale = (width / (right + Math.max(0, left))).toFixed(4);
            for (const part of content) setLayout(part, { zoom: scale });
            current.scrollLeft = scrollLeft;
            current.scrollTop = scrollTop;
        }

        function fitMobileViewport(surface) {
            // The embedded page and the Dijit dialog can both sit inside fixed-height clipping parents.
            const owner = surface.ownerDocument;
            for (let parent = surface.parentElement; parent; parent = parent.parentElement) {
                parent.setAttribute("data-fehlzeiten-mx-excuse-ancestor", "");
            }
            surface.setAttribute("data-fehlzeiten-mx-excuse-surface", "");
            addLayout(owner, `
                [data-fehlzeiten-mx-excuse-ancestor] {
                    overflow: visible !important; clip: auto !important; clip-path: none !important;
                    transform: none !important; translate: none !important; filter: none !important;
                    perspective: none !important; contain: none !important; will-change: auto !important;
                }
                html, body { margin: 0 !important; width: 100% !important; height: 100% !important; }
                html[data-fehlzeiten-mx-excuse-ancestor] { overflow: hidden !important; }
                iframe[data-fehlzeiten-mx-excuse-surface] {
                    display: block !important; position: fixed !important; inset: 0 !important;
                    width: 100% !important; height: 100% !important;
                    min-width: 0 !important; min-height: 0 !important;
                    max-width: none !important; max-height: none !important;
                    margin: 0 !important; padding: 0 !important; border: 0 !important;
                    box-sizing: border-box !important; z-index: 2147483647 !important;
                }
            `);
        }

        function setLayout(element, rules) {
            for (const [name, value] of Object.entries(rules)) {
                if (element.style.getPropertyValue(name) !== value || element.style.getPropertyPriority(name) !== "important")
                    element.style.setProperty(name, value, "important");
            }
        }

        function measure() {
            const { width, height } = current.getBoundingClientRect();
            if (width <= 0 || height <= 0) return null;
            // Include overflowing content and leave room for CSS-to-device-pixel rounding.
            return {
                width: Math.ceil(width + Math.max(0, current.scrollWidth - current.clientWidth)) + 2,
                height: Math.ceil(height + Math.max(0, current.scrollHeight - current.clientHeight)) + 2
            };
        }

        function onButton(event) {
            const button = event.target?.closest?.('button,[role="button"]');
            if (button && current.contains(button) && enabled(button) &&
                /^speichern$/i.test(clean(button.textContent).replace(/^●\s*/, ""))) {
                onSave();
            }
        }

        function onSave() {
            if (editor.saveRequested) return;
            editor.saveRequested = true;
            notify("save");
        }

        function notify(action) {
            if (action === "closed") {
                if (editor.closed) return;
                editor.closed = true;
                observer.disconnect();
                resizeObserver.disconnect();
                doc.removeEventListener("click", onButton, true);
                form.removeEventListener("submit", onSave);
            }
            // Send dimensions and lifecycle signals to the host without transferring form values.
            const message = { editorId: editor.id, action, saveRequested: editor.saveRequested,
                width: editor.width, height: editor.height };
            if (mobile) globalThis.FehlzeitenMxEditor?.postMessage(JSON.stringify(message));
            else globalThis.chrome?.webview?.postMessage(message);
        }

        function addLayout(ownerDocument, rules) {
            const sheet = new ownerDocument.defaultView.CSSStyleSheet();
            sheet.replaceSync(rules);
            ownerDocument.adoptedStyleSheets = [...ownerDocument.adoptedStyleSheets, sheet];
        }
    }
    const dateMatches = dateInputs.map(matchesDate);
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
        if (!table) return state;
        const absenceNotes = readAbsenceNotes(table);
        return absenceNotes == null ? { ...state, notesUnavailable: true } : { ...state, notesReady: true, absenceNotes };
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
