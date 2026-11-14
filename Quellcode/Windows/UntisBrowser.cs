using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Web.WebView2.Core;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung;

internal sealed class UntisBrowser(Form owner, string appFolder, bool rememberLogin) : IDisposable
{
    internal const string Origin = "https://bkt.webuntis.com";
    internal const string ReportsUrl = Origin + "/class-register-reports";
    private const string AbsencesUrl = Origin + "/absences";
    private const string AbsenceTimesUrl = Origin + "/absence-times";
    private const string MyLessonsUrl = Origin + "/my-lessons";
    private const string TimetableUrl = Origin + "/timetable/my-teacher";
    private const string ReportCommandName = "__fehlzeitenMxReport";
    private static readonly TimeSpan ExcuseLoadTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AbsencePreloadTimeout = TimeSpan.FromSeconds(12);
    internal static IReadOnlyList<ReportKind> AutomaticReports { get; } =
        [ReportKind.Absences, ReportKind.Lateness, ReportKind.Timetable];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Script = ReadScript();
    private static readonly string InstallScript = "(() => { if (location.origin !== " + JsonSerializer.Serialize(Origin) +
        ") return; " + Script + "\nglobalThis." + ReportCommandName + " = reportCommand; })()";
    private readonly List<LoginWindow> loginPopups = [];
    private readonly SavedWebUntisSession savedSession = new(appFolder);
    private readonly Dictionary<int, SchoolPeriod[]> schoolPeriods = [];
    private readonly Dictionary<string, SupplementPage> supplementPages = [];
    private readonly Dictionary<ReportSelection, SupplementPage> absencePages = [];
    private readonly SemaphoreSlim absenceAccess = new(1);
    private readonly SemaphoreSlim supplementFallback = new(1);
    private readonly HashSet<string> supplementsUsingMainView = [];
    private CoreWebView2Environment? environment;
    private LoginWindow? mainWindow;
    private CoreWebView2? view;
    private PendingDownload? pending;
    private bool navigating;
    private bool navigationStarted;
    private bool reportsNeedReload;
    private bool loginCanceled;
    private bool disposed;
    private bool persistLogin = rememberLogin;
    public bool SessionConfirmed { get; private set; }
    public bool HasSavedLogin { get; private set; }
    public bool AllowLoginPrompt { get; set; } = true;
    private int responseStatus;

    public async Task<(Student[] Classes, ReportRange? SchoolYear)> ClassesAsync(IProgress<string> progress, CancellationToken token,
        int? selectedYear = null)
    {
        loginCanceled = false;
        try
        {
            var year = selectedYear ?? SchoolYears.StartYear(DateOnly.FromDateTime(DateTime.Today));
            var reportPage = await OpenReportsForYearAsync(year, progress, token, reload: true, includeClasses: true);
            var schoolYear = await ReadSchoolYearAsync(token);
            if (schoolYear != null && SchoolYears.StartYear(schoolYear.From) == year)
            {
                reportPage = await ReadStateAsync(token, includeClasses: true);
                Check(reportPage);
            }
            else if (selectedYear.HasValue)
                throw new InvalidOperationException($"WebUntis hat die Daten für das Schuljahr {SchoolYears.Label(year)} nicht geliefert.");
            else schoolYear = null;
            return (reportPage.Classes, schoolYear);
        }
        finally { HideLogin(); }
    }

    private async Task<ReportState> OpenReportsForYearAsync(int year, IProgress<string> progress, CancellationToken token,
        bool reload = false, bool includeClasses = false)
    {
        var current = SchoolYears.StartYear(DateOnly.FromDateTime(DateTime.Today));
        var expected = year.ToString(CultureInfo.InvariantCulture);
        var report = await OpenReportsAsync(progress, token, reload, includeClasses);
        if (report.GlobalYear == expected || (string.IsNullOrEmpty(report.GlobalYear) && year == current)) return report;

        progress.Report($"WebUntis wird auf {SchoolYears.Label(year)} umgestellt …");
        CloseSupplementPages();
        reportsNeedReload = true;
        var waiting = Stopwatch.StartNew();
        var openingMenu = false;
        var clickedYear = false;
        do
        {
            token.ThrowIfCancellationRequested();
            var state = await CommandAsync("globalSchoolYear", token, targetSchoolYear: year);
            if (state.GlobalYear == expected && !navigating)
            {
                var updated = await OpenReportsAsync(progress, token, reload: true, includeClasses: includeClasses);
                if (updated.GlobalYear == expected) return updated;
                break;
            }
            if (state.GlobalYearUnavailable)
                throw new InvalidOperationException($"Das Schuljahr {SchoolYears.Label(year)} wird in deinem WebUntis-Menü nicht angeboten.");
            if (!clickedYear && state.SchoolYearX is { } x && state.SchoolYearY is { } y && (state.GlobalYearOption || !openingMenu))
            {
                await ClickReportAsync(x, y, token);
                if (state.GlobalYearOption) clickedYear = true;
                else openingMenu = true;
            }
            await Task.Delay(180, token);
        } while (waiting.Elapsed < TimeSpan.FromSeconds(12));
        throw new InvalidOperationException($"Der Wechsel auf das Schuljahr {SchoolYears.Label(year)} wurde in WebUntis nicht bestätigt.");
    }

    public async Task SignOutAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            savedSession.Clear();
            HasSavedLogin = false;
            schoolPeriods.Clear();
            if (view is { } browser)
            {
                browser.Stop();
                browser.Navigate("about:blank");
                await browser.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            }
        }
        finally { ResetWindow(); }
    }

    public async Task SetRememberLoginAsync(bool remember, CancellationToken token)
    {
        if (persistLogin == remember) return;
        token.ThrowIfCancellationRequested();
        if (!remember)
        {
            savedSession.Clear();
            HasSavedLogin = false;
        }
        else if (SessionConfirmed && view is { } browser)
            HasSavedLogin = await savedSession.SaveAsync(browser.CookieManager, token);
        persistLogin = remember;
    }

    private async Task<ReportRange?> ReadSchoolYearAsync(CancellationToken token)
    {
        var waiting = Stopwatch.StartNew();
        ReportState? selection = null;
        ReportRange? previous = null;
        var menuOpened = false;
        while (waiting.Elapsed < TimeSpan.FromSeconds(6))
        {
            var state = await CommandAsync(selection == null ? "schoolYear" : "schoolYearDates", token);
            if (selection == null)
            {
                if (state.SchoolYearRequested) selection = state;
                else if (state.SchoolYearX is { } x && state.SchoolYearY is { } y && (state.SchoolYearOption || !menuOpened))
                {
                    try { await ClickReportAsync(x, y, token); }
                    catch (Exception error) when (error is COMException or TimeoutException) { return null; }
                    if (state.SchoolYearOption) selection = state;
                    else menuOpened = true;
                }
                else if (state.SchoolYearUnavailable && waiting.Elapsed > TimeSpan.FromSeconds(1.5)) break;
            }
            else if (state.HasDateInputs && (selection.SchoolYearSelected || state.FormStamp != selection.FormStamp ||
                (!selection.SchoolYearOption && (state.SchoolYearFrom != selection.SchoolYearFrom || state.SchoolYearTo != selection.SchoolYearTo))) &&
                DateOnly.TryParseExact(state.SchoolYearFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) &&
                DateOnly.TryParseExact(state.SchoolYearTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
            {
                var candidate = new ReportRange(from, to);
                if (candidate.IsValid() && candidate.From < candidate.To)
                {
                    if (candidate == previous) return candidate;
                    previous = candidate;
                }
                else previous = null;
            }
            else previous = null;
            await Task.Delay(230, token);
        }
        return null;
    }

    public async Task<ReportBatch> FetchAsync(string className, DateOnly from, DateOnly to,
        IProgress<string> progress, CancellationToken token, Action<int>? reportCompleted = null)
    {
        loginCanceled = false;
        try
        {
            var (selection, roster) = await PrepareClassAsync(className, new ReportRange(from, to), progress, token);
            var batch = new ReportBatch(roster, selection.ClassId);
            var completed = 0;
            foreach (var kind in AutomaticReports)
            {
                token.ThrowIfCancellationRequested();
                if (reportsNeedReload)
                {
                    await OpenReportsAsync(progress, token);
                    var reportPage = await ConfigureRangeAsync(selection, token);
                    await SelectClassAsync(reportPage, selection, token);
                }
                try
                {
                    progress.Report($"{CsvReport.Label(kind)} · Zeitraum und Optionen werden eingestellt …");
                    await ConfigureReportAsync(selection, kind, token);
                    progress.Report($"{CsvReport.Label(kind)} · WebUntis erstellt den Bericht …");
                    var csv = await DownloadAsync(selection, kind, progress, token);
                    if (csv == null) batch.EmptyReports.Add(kind);
                    else batch.Csvs.Add(new ReportCsv(kind, csv));
                }
                catch (Exception error) when (error is InvalidOperationException or TimeoutException or COMException or IOException or InvalidDataException)
                {
                    batch.Notes.Add($"{CsvReport.Label(kind)}: {error.Message}");
                    reportsNeedReload = true;
                }
                reportCompleted?.Invoke(++completed);
            }
            return batch;
        }
        catch (OperationCanceledException)
        {
            reportsNeedReload = true;
            throw;
        }
        finally { HideLogin(); }
    }

    public async Task<Student[]> ReadClassRosterAsync(string className, ReportRange range,
        IProgress<string> progress, CancellationToken token)
    {
        loginCanceled = false;
        try
        {
            var (_, roster) = await PrepareClassAsync(className, range, progress, token);
            return roster;
        }
        catch (OperationCanceledException)
        {
            reportsNeedReload = true;
            throw;
        }
        finally { HideLogin(); }
    }

    public async Task<LessonEmailBatch> ReadLessonEmailsAsync(ReportRange range,
        IProgress<string> progress, Action<int, int> reportProgress, CancellationToken token)
    {
        var year = SchoolYears.StartYear(range.From);
        await OpenReportsForYearAsync(year, progress, token);
        var selection = new ReportSelection("", range.From, range.To);
        var emails = new List<RosterEmail>();
        var read = 0;
        var total = 0;
        string? failure = null;
        try
        {
            progress.Report("Mein Unterricht · Berichte des Schuljahres werden gesucht …");
            view!.Navigate(MyLessonsUrl);
            reportsNeedReload = true;
            var list = await WaitForLessonListAsync(selection, year, token);
            total = list.Lessons.Length;
            reportProgress(0, total);
            for (var index = 0; index < total; index++)
            {
                token.ThrowIfCancellationRequested();
                var lesson = list.Lessons[index];
                var label = $"Unterrichtsbericht {index + 1}/{total} · {lesson.Subject}";
                progress.Report(label + " wird gelesen …");
                try
                {
                    var opened = await CommandAsync("openLessonReport", token, selection, targetSchoolYear: year, lesson: lesson);
                    if (opened.Error != null || opened.ChangedField == null)
                        throw new InvalidOperationException(opened.Error ?? "WebUntis hat die Unterrichtszeile nicht geöffnet.");
                    await PressEnterAsync(token);
                    await WaitForLessonReportAsync(selection, year, token);
                    var content = await DownloadAsync(selection, ReportKind.Absences, progress, token, lesson, year, label);
                    if (content != null)
                        emails.AddRange(await Task.Run(() => RosterCsv.ReadLessonEmails(content, lesson.Classes, lesson.Group), token));
                    read++;
                }
                catch (Exception error) when (error is InvalidOperationException or TimeoutException or COMException or IOException or InvalidDataException)
                {
                    failure ??= error.Message;
                    progress.Report(label + " konnte nicht gelesen werden.");
                }
                reportProgress(index + 1, total);
                if (index + 1 < total) await WaitForLessonListAsync(selection, year, token);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or COMException)
        {
            failure ??= error.Message;
        }
        finally { HideLogin(); }
        return new LessonEmailBatch(emails.Distinct().ToArray(), read, total, failure);
    }

    private async Task<ReportState> WaitForLessonListAsync(ReportSelection selection, int year, CancellationToken token)
    {
        var waiting = Stopwatch.StartNew();
        var back = false;
        var openedPreset = false;
        var pickedPreset = false;
        var retried = false;
        while (true)
        {
            var state = await CommandAsync(back ? "lessonList" : "lessonBack", token, selection, targetSchoolYear: year);
            if (state.Error != null) throw new InvalidOperationException(state.Error);
            if (state.Stage is "login" or "external" || responseStatus is 401 or 403)
                throw new InvalidOperationException("WebUntis verlangt eine neue Anmeldung. Starte den CSV-Export erneut.");
            if (state.Stage == "lessonList" && state.RangeReady) return state;
            if (state.Stage == "lessonReport" && state.ChangedField != null)
            {
                back = true;
                await PressEnterAsync(token);
            }
            if (!pickedPreset && state.SchoolYearX is { } x && state.SchoolYearY is { } y && (state.SchoolYearOption || !openedPreset))
            {
                await ClickReportAsync(x, y, token);
                if (state.SchoolYearOption) pickedPreset = true;
                else openedPreset = true;
            }
            if (waiting.Elapsed >= TimeSpan.FromSeconds(8))
            {
                if (retried) throw new InvalidOperationException("WebUntis hat «Mein Unterricht» für das gewählte Schuljahr nicht geladen.");
                view!.Navigate(MyLessonsUrl);
                retried = true;
                back = openedPreset = pickedPreset = false;
                waiting.Restart();
            }
            await Task.Delay(180, token);
        }
    }

    private async Task WaitForLessonReportAsync(ReportSelection selection, int year, CancellationToken token)
    {
        var waiting = Stopwatch.StartNew();
        do
        {
            var state = await CommandAsync("lessonList", token, selection, targetSchoolYear: year);
            if (state.Stage == "lessonReport") return;
            if (state.Stage is "login" or "external") break;
            await Task.Delay(160, token);
        } while (waiting.Elapsed < TimeSpan.FromSeconds(7));
        throw new InvalidOperationException("WebUntis hat den Unterrichtsbericht nicht geöffnet.");
    }

    public async Task<SchoolPeriod[]> ReadSchoolPeriodsAsync(int year, IProgress<string> progress, CancellationToken token)
    {
        if (schoolPeriods.TryGetValue(year, out var cached)) return cached;
        progress.Report("Unterrichtsstunden werden mit dem WebUntis-Zeitraster abgeglichen …");
        var periods = await ReadSupplementAsync<SchoolPeriod[]>(TimetableUrl, year, token, async (browser, blocked) =>
        {
            var waiting = Stopwatch.StartNew();
            do
            {
                token.ThrowIfCancellationRequested();
                if (blocked()) return null;
                var state = await CommandAsync("schoolPeriods", token, targetSchoolYear: year, target: browser);
                CheckSupplementSession(state);
                if (state.Periods.Length > 0)
                {
                    var parsed = state.Periods.Select(row => row.ToPeriod()).ToArray();
                    if (parsed.All(period => period != null)) return parsed.Select(period => period!).ToArray();
                }
                if (state.Stage == "denied") break;
                await Task.Delay(180, token);
            } while (waiting.Elapsed < TimeSpan.FromSeconds(7));
            return null;
        });
        if (periods == null) return [];
        return schoolPeriods[year] = periods;
    }

    private async Task<(ReportSelection Selection, Student[] Roster)> PrepareClassAsync(string className, ReportRange range,
        IProgress<string> progress, CancellationToken token)
    {
        await OpenReportsForYearAsync(SchoolYears.StartYear(range.From), progress, token);
        var available = await ConfigureRangeAsync(new ReportSelection("", range.From, range.To), token, includeClasses: true);
        var matches = available.Classes.Where(choice => choice.Name.Equals(className, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"WebUntis bietet «{className}» für den gewählten Zeitraum nicht eindeutig an. Aktualisiere die Klassenliste unter Einstellungen.");
        var selection = new ReportSelection(matches[0].Id, range.From, range.To);
        await SelectClassAsync(available, selection, token);
        var roster = await CommandAsync("readRoster", token, selection, roster: true);
        Check(roster);
        if (roster.ClassId != selection.ClassId || !roster.AllStudents || !roster.RangeReady)
            throw new InvalidOperationException("WebUntis hat Klasse, Zeitraum oder Namensauswahl während des Abrufs geändert.");
        return (selection, roster.Students);
    }

    public async Task<byte[]?> FetchExcusedAsync(ReportBatch batch, DateOnly from, DateOnly to,
        IProgress<string> progress, CancellationToken token)
    {
        try
        {
            var selection = new ReportSelection(batch.ClassId, from, to, ExcusedOnly: true);
            await OpenReportsAsync(progress, token);
            var page = await ConfigureRangeAsync(selection, token);
            await SelectClassAsync(page, selection, token);
            progress.Report("Entschuldigte Fehlzeiten · Bericht wird eingestellt …");
            await ConfigureReportAsync(selection, ReportKind.Absences, token);
            progress.Report("Entschuldigte Fehlzeiten · WebUntis erstellt den Zusatzbericht …");
            return await DownloadAsync(selection, ReportKind.Absences, progress, token);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or COMException or IOException or InvalidDataException)
        {
            reportsNeedReload = true;
            batch.Notes.Add($"Entschuldigte Fehlzeiten · {error.Message}");
            return null;
        }
        finally { HideLogin(); }
    }

    public async Task<AbsenceNote[]> ReadAbsenceNotesAsync(ReportBatch batch, ReportRange range, IProgress<string> progress,
        CancellationToken token)
    {
        var selection = new ReportSelection(batch.ClassId, range.From, range.To);
        var year = SchoolYears.StartYear(range.From);
        progress.Report("Abwesenheitstexte werden gelesen …");
        return await ReadSupplementAsync<AbsenceNote[]>(AbsencesUrl, year, token, async (browser, blocked) =>
        {
            var waiting = Stopwatch.StartNew();
            string? waitForFormStamp = null;
            do
            {
                token.ThrowIfCancellationRequested();
                if (blocked()) return null;
                var state = await CommandAsync("absenceNotes", token, selection, waitForFormStamp: waitForFormStamp,
                    targetSchoolYear: year, target: browser);
                CheckSupplementSession(state);
                if (state.NotesReady) return state.AbsenceNotes;
                if (state.NotesUnavailable || state.Error != null || state.Stage == "denied") break;
                if (state.ChangedField != null) waitForFormStamp = state.FormStamp;
                await Task.Delay(180, token);
            } while (waiting.Elapsed < TimeSpan.FromSeconds(8));
            return null;
        }) ?? [];
    }

    public void KeepPreparedAbsences(IEnumerable<AttendanceMatrix> matrices)
    {
        var active = matrices.Select(AbsenceSelection).ToHashSet();
        foreach (var (selection, page) in absencePages.ToArray())
            if (!active.Contains(selection)) DiscardPreparedAbsences(selection, page);
    }

    public async Task<AbsenceNote[]?> PrepareClassAbsencesAsync(string name, string classId, ReportRange range,
        CancellationToken token, bool refresh = false)
    {
        await absenceAccess.WaitAsync(token);
        try
        {
            if (!SessionConfirmed || classId.Length == 0) return null;
            var selection = new ReportSelection(classId, range.From, range.To);
            if (refresh && absencePages.TryGetValue(selection, out var previous)) DiscardPreparedAbsences(selection, previous);
            return await PrepareAbsencePageAsync(selection, name, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is COMException or InvalidOperationException or TimeoutException or IOException or JsonException)
        {
            return null;
        }
        finally { absenceAccess.Release(); }
    }

    public AbsenceListEntry[]? PreparedAbsenceActions(AttendanceMatrix matrix, Student student, DateOnly day)
    {
        if (!SessionConfirmed || !absencePages.TryGetValue(AbsenceSelection(matrix), out var page) || page.Absences is not { } rows ||
            page.Redirected || page.Status >= 400 || !GetAbsencePerson(matrix, student, out var person)) return null;
        var name = AbsenceName(person.Name);
        var named = rows.Where(row => row.From <= day && day <= row.To && AbsenceName(row.StudentName) == name).ToArray();
        // A name shared by multiple students can only be resolved with WebUntis' own student filter.
        if (named.Any(row => row.StudentId.Length == 0)) return null;
        return named.Where(row => person.Id.Length == 0 || row.StudentId == person.Id).ToArray();
    }

    public async Task<AbsenceListEntry[]> ReadAbsenceActionsAsync(AttendanceMatrix matrix, Student student, DateOnly day,
        IProgress<string> progress, CancellationToken token)
    {
        if (PreparedAbsenceActions(matrix, student, day) is { Length: > 0 } prepared) return prepared;
        progress.Report("Passende WebUntis-Abwesenheiten werden gesucht …");
        await absenceAccess.WaitAsync(token);
        loginCanceled = false;
        var year = SchoolYears.StartYear(matrix.From);
        try
        {
            if (PreparedAbsenceActions(matrix, student, day) is { Length: > 0 } loaded) return loaded;
            await OpenReportsForYearAsync(year, progress, token);
            var page = await OpenAbsencePageAsync(year, token);
            try
            {
                var state = await WaitForAbsenceActionsAsync(page, matrix.ClassName, new ReportSelection(matrix.ClassId, day, day),
                    token, RequireAbsencePerson(matrix, student), day);
                return state.EditableAbsences;
            }
            catch (TimeoutException)
            {
                DiscardAbsencePage(page);
                throw;
            }
        }
        finally { HideLogin(); absenceAccess.Release(); }
    }

    // The WebUntis form owns validation and the save operation. We only open and show it.
    public async Task<bool> ShowExcuseAsync(AttendanceMatrix matrix, Student student, DateOnly day,
        AbsenceListEntry row, IProgress<string> progress, CancellationToken token)
    {
        await absenceAccess.WaitAsync(token);
        loginCanceled = false;
        var year = SchoolYears.StartYear(matrix.From);
        var selection = AbsenceSelection(matrix);
        try
        {
            var person = RequireAbsencePerson(matrix, student);
            if (PreparedAbsenceActions(matrix, student, day)?.Any(candidate => candidate.Id == row.Id && candidate.CanExcuse) == true &&
                absencePages.Remove(selection, out var prepared))
            {
                progress.Report("Originaldialog in WebUntis wird geöffnet …");
                return await ShowExcuseOnPageAsync(prepared, matrix, selection, person, day, row, token, entireClass: true);
            }
            if (absencePages.TryGetValue(selection, out var previous)) DiscardPreparedAbsences(selection, previous);
            await OpenReportsForYearAsync(year, progress, token);
            progress.Report("Originaldialog in WebUntis wird geöffnet …");
            var page = await OpenAbsencePageAsync(year, token);
            return await ShowExcuseOnPageAsync(page, matrix, new ReportSelection(matrix.ClassId, day, day),
                person, day, row, token, entireClass: false);
        }
        finally { HideLogin(); absenceAccess.Release(); }
    }

    private async Task<SupplementPage> OpenAbsencePageAsync(int year, CancellationToken token)
    {
        SupplementPage? page = null;
        try
        {
            page = await OpenSupplementPageAsync(AbsencesUrl, year, token);
            if (page.Redirected || page.Status >= 400 || page.Browser.Source != AbsencesUrl)
            {
                page.Redirected = false;
                page.Status = 0;
                await NavigateSupplementAsync(page.Browser, AbsencesUrl, token, ExcuseLoadTimeout);
            }
            return page;
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            if (page != null) DiscardAbsencePage(page);
            if (error is OperationCanceledException) throw;
            throw new TimeoutException("WebUntis hat die Abwesenheitsseite nach 45 Sekunden noch nicht geladen. Versuche es erneut.", error);
        }
    }

    private async Task<AbsenceNote[]?> PrepareAbsencePageAsync(ReportSelection selection, string name, CancellationToken token)
    {
        if (absencePages.TryGetValue(selection, out var previous))
        {
            if (!previous.Redirected && previous.Status < 400 && previous.Absences != null) return previous.AbsenceNotes;
            DiscardPreparedAbsences(selection, previous);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(AbsencePreloadTimeout);
        var prepareToken = deadline.Token;
        var year = SchoolYears.StartYear(selection.From);
        var page = await CreateSupplementPageAsync(AbsencesUrl, year, prepareToken);
        absencePages.Add(selection, page);
        try
        {
            await NavigateSupplementAsync(page.Browser, AbsencesUrl, prepareToken, AbsencePreloadTimeout);
            var state = await WaitForAbsenceActionsAsync(page, name, selection, prepareToken, entireClass: true);
            page.Absences = state.EditableAbsences;
            page.AbsenceNotes = state.NotesReady ? state.AbsenceNotes : null;
            return page.AbsenceNotes;
        }
        catch
        {
            DiscardPreparedAbsences(selection, page);
            throw;
        }
    }

    private void DiscardPreparedAbsences(ReportSelection selection, SupplementPage page)
    {
        if (absencePages.TryGetValue(selection, out var active) && active == page)
        {
            absencePages.Remove(selection);
            page.Dispose();
        }
    }

    private static ReportSelection AbsenceSelection(AttendanceMatrix matrix) => new(matrix.ClassId, matrix.From, matrix.To);

    private static string AbsenceName(string name) => string.Join(' ', name.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).Normalize().ToLowerInvariant();

    private static bool GetAbsencePerson(AttendanceMatrix matrix, Student student, out AbsencePerson person)
    {
        var id = student.Id.Length > 0 && student.Id.All(char.IsAsciiDigit) ? student.Id : "";
        person = new AbsencePerson(id, student.Name);
        return id.Length > 0 || matrix.Students.Count(candidate => AbsenceName(candidate.Name) == AbsenceName(student.Name)) == 1;
    }

    private static AbsencePerson RequireAbsencePerson(AttendanceMatrix matrix, Student student) =>
        GetAbsencePerson(matrix, student, out var person) ? person :
            throw new InvalidOperationException("Für diese Person gibt es keine eindeutige WebUntis-Schülerkennung.");

    private async Task<ReportState> WaitForAbsenceActionsAsync(SupplementPage page, string className,
        ReportSelection selection, CancellationToken token, AbsencePerson? person = null, DateOnly? day = null,
        AbsenceListEntry? row = null, string? editorId = null, bool entireClass = false)
    {
        var action = row == null ? "absenceActions" : "openExcuse";
        var year = SchoolYears.StartYear(selection.From);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ExcuseLoadTimeout);
        var lookupToken = deadline.Token;
        string? changedForm = null;
        try
        {
            while (true)
            {
                lookupToken.ThrowIfCancellationRequested();
                if (page.Window.IsDisposed) throw new InvalidOperationException("Die WebUntis-Abwesenheitsseite wurde geschlossen.");
                if (page.Redirected || page.Status is 401 or 403)
                    throw new InvalidOperationException("WebUntis erlaubt den Zugriff auf diese Abwesenheitsliste in der aktuellen Sitzung nicht.");
                var state = await CommandAsync(action, lookupToken, selection, waitForFormStamp: changedForm, targetSchoolYear: year,
                    className: className, target: page.Browser, absencePerson: person, absenceDay: day, absenceRow: row,
                    editorId: editorId, commandTimeout: ExcuseLoadTimeout, classAbsences: entireClass);
                CheckSupplementSession(state);
                if (state.Stage == "denied")
                    throw new InvalidOperationException("WebUntis meldet fehlende Berechtigung für die Abwesenheitsliste.");
                if (state.Error != null) throw new InvalidOperationException(state.Error);
                if (state.AbsenceActionsReady || state.AbsenceEditorRequested) return state;
                if (state.ChangedField != null) changedForm = state.FormStamp;
                await Task.Delay(220, lookupToken);
            }
        }
        catch (Exception error) when (error is TimeoutException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new TimeoutException("WebUntis hat die Abwesenheitsliste nach 45 Sekunden noch nicht bestätigt. Versuche es erneut.", error);
        }
    }

    private async Task<bool> ShowExcuseOnPageAsync(SupplementPage page, AttendanceMatrix matrix, ReportSelection selection,
        AbsencePerson person, DateOnly day, AbsenceListEntry row, CancellationToken token, bool entireClass)
    {
        var editorId = Guid.NewGuid().ToString("N");
        var year = SchoolYears.StartYear(matrix.From);
        var browser = page.Browser;
        var window = page.Window;
        var saveRequested = false;
        var closed = false;
        var latestSize = Size.Empty;
        try
        {
            browser.WebMessageReceived += OnEditorMessage;
            await WaitForAbsenceActionsAsync(page, matrix.ClassName, selection, token, person, day, row, editorId, entireClass);
            var size = await WaitForExcuseFormAsync(page, year, editorId, token);
            // Resume outside WebView2's callback before entering the WinForms modal loop.
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            using var cancellation = token.Register(() => FinishDialog(DialogResult.Cancel));
            if (!closed && !window.IsDisposed)
                window.ShowForExcuse(owner, latestSize.IsEmpty ? size : latestSize);
            token.ThrowIfCancellationRequested();
            return saveRequested;
        }
        finally
        {
            try
            {
                if (!window.IsDisposed) browser.WebMessageReceived -= OnEditorMessage;
            }
            catch (Exception error) when (error is COMException or InvalidOperationException) { }
            DiscardAbsencePage(page);
        }

        void OnEditorMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!IsSchoolAddress(args.Source) || !Uri.TryCreate(args.Source, UriKind.Absolute, out var source) ||
                source.AbsolutePath.TrimEnd('/') != "/absences") return;
            EditorMessage? message;
            try { message = JsonSerializer.Deserialize<EditorMessage>(args.WebMessageAsJson, Json); }
            catch (JsonException) { return; }
            if (message?.EditorId != editorId || closed) return;
            if (message.Action == "size")
            {
                if (message.Width <= 0 || message.Height <= 0) return;
                latestSize = new Size(message.Width, message.Height);
                if (!window.IsDisposed && window.IsHandleCreated && window.Visible)
                    window.BeginInvoke(() => { if (!closed && !window.IsDisposed) window.UpdateExcuseSize(latestSize); });
                return;
            }
            if (message.Action is not ("save" or "closed")) return;
            saveRequested |= message.SaveRequested;
            if (message.Action != "closed") return;
            closed = true;
            FinishDialog(DialogResult.OK);
        }

        void FinishDialog(DialogResult result)
        {
            if (!window.IsDisposed && window.IsHandleCreated)
                window.BeginInvoke(() => { if (window.Visible) window.DialogResult = result; });
        }
    }

    private void DiscardAbsencePage(SupplementPage page)
    {
        if (supplementPages.TryGetValue(AbsencesUrl, out var active) && active == page) supplementPages.Remove(AbsencesUrl);
        page.Dispose();
    }

    private async Task<Size> WaitForExcuseFormAsync(SupplementPage page, int year, string editorId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ExcuseLoadTimeout);
        var loadingToken = deadline.Token;
        try
        {
            while (true)
            {
                loadingToken.ThrowIfCancellationRequested();
                if (page.Redirected || page.Status is 401 or 403)
                    throw new InvalidOperationException("Die WebUntis-Sitzung ist abgelaufen oder der Dialog wurde gesperrt.");
                var state = await CommandAsync("excuseState", loadingToken, targetSchoolYear: year, target: page.Browser,
                    editorId: editorId, commandTimeout: ExcuseLoadTimeout);
                CheckSupplementSession(state);
                if (state.Error != null) throw new InvalidOperationException(state.Error);
                if (state.AbsenceEditorVisible && state.EditorWidth > 0 && state.EditorHeight > 0)
                    return new Size(state.EditorWidth, state.EditorHeight);
                await Task.Delay(200, loadingToken);
            }
        }
        catch (Exception error) when (error is TimeoutException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new TimeoutException("WebUntis hat den Entschuldigungsdialog nach 45 Sekunden noch nicht geöffnet. Versuche es erneut.", error);
        }
    }

    public async Task<Report?> ReadAbsenceTimesAsync(string className, ReportBatch batch, ReportRange range, ReportKind kind,
        IProgress<string> progress, CancellationToken token, bool reusePage = false)
    {
        if (kind is not (ReportKind.Absences or ReportKind.Lateness)) return null;
        var selection = new ReportSelection(batch.ClassId, range.From, range.To);
        var year = SchoolYears.StartYear(range.From);
        progress.Report(kind == ReportKind.Absences ? "Fehlzeitenliste wird gelesen …" : "Verspätungsliste wird gelesen …");
        return await ReadSupplementAsync<Report>(AbsenceTimesUrl, year, token, async (browser, blocked) =>
        {
            var waiting = Stopwatch.StartNew();
            string? changedForm = null;
            do
            {
                token.ThrowIfCancellationRequested();
                if (blocked()) return null;
                var state = await CommandAsync("absenceTimes", token, selection, kind, waitForFormStamp: changedForm,
                    targetSchoolYear: year, className: className, target: browser);
                CheckSupplementSession(state);
                if (state.TimesReady)
                {
                    var entries = state.Times.Select(row => row.ToEntry(kind)).ToArray();
                    return entries.Any(entry => entry == null) ? null : new Report(kind, entries.Select(entry => entry!).ToArray(), [], []);
                }
                if (state.NotesUnavailable || state.Error != null || state.Stage == "denied") break;
                if (state.ChangedField != null) changedForm = state.FormStamp;
                await Task.Delay(180, token);
            } while (waiting.Elapsed < TimeSpan.FromSeconds(16));
            return null;
        }, reusePage);
    }

    private async Task<T?> ReadSupplementAsync<T>(string address, int year, CancellationToken token,
        Func<CoreWebView2, Func<bool>, Task<T?>> read, bool reusePage = false) where T : class
    {
        var main = view;
        if (main == null) return null;
        if (!supplementsUsingMainView.Contains(address))
        {
            var keepPage = false;
            try
            {
                var page = await OpenSupplementPageAsync(address, year, token);
                var browser = page.Browser;
                if (!reusePage || !browser.Source.StartsWith(address, StringComparison.OrdinalIgnoreCase))
                {
                    page.Redirected = false;
                    page.Status = 0;
                    await NavigateSupplementAsync(browser, address, token);
                }
                if (!ReferenceEquals(view, main)) return null;
                var result = await read(browser, () => page.Redirected || page.Status is 401 or 403 || !ReferenceEquals(view, main));
                keepPage = result != null;
                if (keepPage || !(page.Redirected || page.Status == 401)) return result;
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or TimeoutException) { }
            finally
            {
                if (!keepPage && supplementPages.Remove(address, out var page)) page.Dispose();
            }
        }

        // The main view can still have a valid session if a new view could not open a page.
        await supplementFallback.WaitAsync(token);
        try
        {
            if (!ReferenceEquals(view, main)) return null;
            reportsNeedReload = true;
            if (!reusePage || !main.Source.StartsWith(address, StringComparison.OrdinalIgnoreCase))
            {
                responseStatus = 0;
                await NavigateSupplementAsync(main, address, token);
            }
            var result = await read(main, () => responseStatus is 401 or 403 || !ReferenceEquals(view, main));
            if (result != null && ReferenceEquals(view, main)) supplementsUsingMainView.Add(address);
            return result;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or TimeoutException) { return null; }
        finally { supplementFallback.Release(); }
    }

    private async Task<SupplementPage> OpenSupplementPageAsync(string address, int year, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (supplementPages.TryGetValue(address, out var cached) && cached.Year == year) return cached;
        if (supplementPages.Remove(address, out var previous)) previous.Dispose();
        var page = await CreateSupplementPageAsync(address, year, token);
        supplementPages.Add(address, page);
        return page;
    }

    private async Task<SupplementPage> CreateSupplementPageAsync(string address, int year, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var main = view;
        var window = new LoginWindow();
        try
        {
            await window.InitializeAsync(environment ?? throw new InvalidOperationException("WebUntis ist nicht geöffnet."));
            token.ThrowIfCancellationRequested();
            var browser = window.Browser;
            ConfigureBrowserSettings(browser);
            await InstallReportScriptAsync(browser, token, address == AbsencesUrl ? ExcuseLoadTimeout : null);
            token.ThrowIfCancellationRequested();
            if (main == null || view != main || disposed) throw new InvalidOperationException("Die WebUntis-Sitzung wurde beendet.");
            var page = new SupplementPage(window, year);
            if (address == AbsencesUrl)
            {
                browser.Settings.AreDefaultScriptDialogsEnabled = true;
                browser.Settings.AreDefaultContextMenusEnabled = false;
                browser.Settings.AreDevToolsEnabled = false;
                browser.Settings.IsWebMessageEnabled = true;
            }
            browser.NavigationStarting += (_, args) =>
            {
                page.Status = 0;
                page.Absences = null;
                page.AbsenceNotes = null;
                if (IsSchoolAddress(args.Uri) || args.Uri == "about:blank") return;
                args.Cancel = true;
                page.Redirected = true;
            };
            browser.FrameNavigationStarting += (_, args) =>
            {
                if (address == AbsencesUrl && args.Uri != "about:blank" && !IsSchoolAddress(args.Uri))
                {
                    args.Cancel = true;
                    page.Redirected = true;
                }
            };
            browser.NavigationCompleted += (_, args) => page.Status = args.HttpStatusCode;
            browser.NewWindowRequested += (_, args) => args.Handled = true;
            browser.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
            return page;
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    private static async Task NavigateSupplementAsync(CoreWebView2 browser, string address, CancellationToken token,
        TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        ulong? navigationId = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Started(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (args.Uri.Equals(address, StringComparison.OrdinalIgnoreCase)) navigationId = args.NavigationId;
        }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (navigationId == args.NavigationId) ready.TrySetResult();
        }
        void DocumentReady(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
        {
            if (navigationId == args.NavigationId) ready.TrySetResult();
        }
        browser.NavigationStarting += Started;
        browser.DOMContentLoaded += DocumentReady;
        browser.NavigationCompleted += Completed;
        try
        {
            browser.Navigate(address);
            await ready.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(8), token);
        }
        finally
        {
            browser.NavigationStarting -= Started;
            browser.DOMContentLoaded -= DocumentReady;
            browser.NavigationCompleted -= Completed;
        }
    }

    private void CloseSupplementPages()
    {
        foreach (var page in supplementPages.Values) page.Dispose();
        supplementPages.Clear();
        foreach (var page in absencePages.Values) page.Dispose();
        absencePages.Clear();
    }

    private static void CheckSupplementSession(ReportState state)
    {
        if (state.Stage is "login" or "external" or "wrongYear")
            throw new InvalidOperationException("Die WebUntis-Ansicht wurde für die aktuelle Sitzung nicht geladen.");
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        if (view != null) return;
        var window = new LoginWindow();
        try
        {
            environment ??= await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(appFolder, "Browserprofil"));
            await window.InitializeAsync(environment);
            var browser = window.Browser;
            if (persistLogin) HasSavedLogin = await savedSession.RestoreAsync(browser.CookieManager, token);
            ConfigureView(browser);
            await InstallReportScriptAsync(browser, token);
            browser.NavigationStarting += (_, _) => { navigating = true; responseStatus = 0; };
            browser.NavigationCompleted += (_, args) =>
            {
                navigating = false;
                responseStatus = args.HttpStatusCode;
            };
            browser.SourceChanged += (_, _) => window.ShowAddress(browser.Source);
            browser.ProcessFailed += (_, args) =>
            {
                if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                {
                    pending?.Finished.TrySetException(new InvalidOperationException("Die WebUntis-Sitzung wurde unterbrochen. Starte den Abruf erneut."));
                    if (!owner.IsDisposed) owner.BeginInvoke(ResetWindow);
                }
            };
            window.Canceled += () => loginCanceled = true;
            mainWindow = window;
            view = browser;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            window.Dispose();
            throw new InvalidOperationException("Microsoft Edge WebView2 Runtime fehlt. Die Installationsquelle steht in der Kurzanleitung.");
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    private async Task<ReportState> OpenReportsAsync(IProgress<string> progress, CancellationToken token,
        bool reload = false, bool includeClasses = false)
    {
        progress.Report("WebUntis-Berichte werden geöffnet …");
        await InitializeAsync(token);
        token.ThrowIfCancellationRequested();
        reload |= reportsNeedReload;
        var discardedStamp = reload ? (await ReadStateAsync(token)).FormStamp : null;
        if (!navigationStarted || reload) NavigateReports();
        var waiting = Stopwatch.StartNew();
        var didReload = false;
        var redirected = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (loginCanceled) throw new OperationCanceledException();
            if (view == null) throw new InvalidOperationException("Die WebUntis-Sitzung wurde beendet. Starte den Abruf erneut.");

            var state = await ReadStateAsync(token, includeClasses: includeClasses);
            if (state.Stage == "reports" && (discardedStamp == null || state.FormStamp != discardedStamp))
            {
                HideLogin();
                if (!SessionConfirmed && persistLogin)
                    HasSavedLogin = await savedSession.SaveAsync(view.CookieManager, token);
                SessionConfirmed = true;
                reportsNeedReload = false;
                return state;
            }
            if (responseStatus == 403 || state.Stage == "denied")
                throw new InvalidOperationException("WebUntis meldet fehlende Berechtigung für die Klassenbuchberichte.");

            if (state.Stage is "login" or "external" || responseStatus == 401)
            {
                if (SessionConfirmed)
                {
                    CloseSupplementPages();
                    supplementsUsingMainView.Clear();
                }
                SessionConfirmed = false;
                HasSavedLogin = false;
                if (!AllowLoginPrompt)
                    throw new InvalidOperationException("Für die Aktualisierung ist eine Anmeldung bei WebUntis erforderlich.");
                if (state.Stage == "external" && mainWindow?.Visible != true && waiting.Elapsed < TimeSpan.FromSeconds(1))
                {
                    await Task.Delay(200, token);
                    continue;
                }
                if (mainWindow is { Visible: false } login)
                {
                    progress.Report("Melde dich im Anmeldefenster bei WebUntis an.");
                    login.ShowForLogin(owner);
                }
                waiting.Restart();
                redirected = false;
            }
            else
            {
                if (state.Stage == "signedIn")
                {
                    if (mainWindow?.Visible == true)
                    {
                        HideLogin();
                        progress.Report("Anmeldung erkannt. WebUntis lädt die Berichte …");
                        waiting.Restart();
                    }
                    if (!state.AtReports && !redirected && !navigating)
                    {
                        NavigateReports();
                        waiting.Restart();
                        redirected = true;
                    }
                }
                if (waiting.Elapsed > TimeSpan.FromSeconds(28))
                {
                    if (didReload)
                    {
                        var reason = state.HasFrame
                            ? "Die Berichtsseite ist geöffnet. WebUntis hat das Berichtsformular darin nicht geladen."
                            : "WebUntis hat die Berichtsseite nach der Anmeldung nicht geladen.";
                        throw new InvalidOperationException(reason + " Starte den Abruf erneut.");
                    }
                    NavigateReports();
                    waiting.Restart();
                    didReload = true;
                }
            }
            await Task.Delay(300, token);
        }
    }

    private async Task SelectClassAsync(ReportState start, ReportSelection selection, CancellationToken token)
    {
        var state = start;
        if (start.ClassId != selection.ClassId)
        {
            Check(await CommandAsync("selectClass", token, selection));
            state = await WaitForFormAsync(selection, token,
                next => next.HasStudentFilter && (next.FormStamp != start.FormStamp || next.RosterStamp != start.RosterStamp),
                "WebUntis hat das Berichtsformular für die ausgewählte Klasse nicht neu geladen.");
        }
        if (!state.RangeReady) state = await ConfigureRangeAsync(selection, token);
        if (state.ClassId != selection.ClassId)
            throw new InvalidOperationException("WebUntis hat die Klasse beim Einstellen des Zeitraums geändert.");
        if (!state.HasStudentFilter)
            state = await WaitForFormAsync(selection, token, next => next.HasStudentFilter,
                "WebUntis hat die Namensliste für die ausgewählte Klasse nicht geladen.");
        if (!state.AllStudents)
        {
            Check(await CommandAsync("selectAll", token, selection));
            await WaitForFormAsync(selection, token, next => next.AllStudents,
                "WebUntis hat den Filter «Alle Schüler*innen» nicht übernommen.");
        }
    }

    private async Task<ReportState> ConfigureRangeAsync(ReportSelection selection, CancellationToken token, bool includeClasses = false)
    {
        var waiting = Stopwatch.StartNew();
        string? changedForm = null;
        try
        {
            do
            {
                var state = await CommandAsync("configureRange", token, selection, includeClasses: includeClasses, waitForFormStamp: changedForm);
                if (state.Stage is "login" or "external" || state.Error != null) Check(state);
                if (state.RangeReady) return state;
                if (state.ChangedField != null) changedForm = state.FormStamp;
                await Task.Delay(160, token);
            } while (waiting.Elapsed < TimeSpan.FromSeconds(12));
            throw new InvalidOperationException("WebUntis hat den gewählten Zeitraum im Berichtsformular nicht bestätigt.");
        }
        catch
        {
            reportsNeedReload = true;
            throw;
        }
    }

    private async Task<ReportState> WaitForFormAsync(ReportSelection selection, CancellationToken token,
        Func<ReportState, bool> accept, string failure)
    {
        var waiting = Stopwatch.StartNew();
        do
        {
            token.ThrowIfCancellationRequested();
            var state = await ReadStateAsync(token);
            if (state.Stage == "reports" && state.ClassId == selection.ClassId && accept(state)) return state;
            if (state.Stage is "login" or "external")
                throw new InvalidOperationException("WebUntis verlangt eine neue Anmeldung. Starte den Abruf erneut.");
            await Task.Delay(150, token);
        } while (waiting.Elapsed < TimeSpan.FromSeconds(8));
        throw new InvalidOperationException(failure);
    }

    private async Task ConfigureReportAsync(ReportSelection selection, ReportKind kind, CancellationToken token)
    {
        await WaitForFormAsync(selection, token, state => state.AllStudents && state.HasDateInputs,
            "WebUntis hat die Datumsfelder für die ausgewählte Klasse nicht geladen.");
        // Changing one control can replace all the others. Never write to the outgoing form.
        for (var step = 0; step < 12; step++)
        {
            var change = await CommandAsync("configure", token, selection, kind);
            Check(change);
            if (change.RangeReady && change.FlagsReady) return;
            if (change.ChangedField == null) break;
            if (!change.ReloadExpected) await Task.Delay(300, token);
            await WaitForFormAsync(selection, token, state => state.AllStudents && state.HasDateInputs &&
                (!change.ReloadExpected || state.FormStamp != change.FormStamp),
                $"WebUntis hat die Einstellung «{change.ChangedField}» nicht übernommen.");
        }
        throw new InvalidOperationException("WebUntis hat beim Laden des Berichtsformulars Einstellungen zurückgesetzt.");
    }

    private async Task<byte[]?> DownloadAsync(ReportSelection selection, ReportKind kind,
        IProgress<string> progress, CancellationToken token, LessonChoice? lesson = null, int? year = null, string? label = null)
    {
        using var download = new PendingDownload(token);
        pending = download;
        var wait = TimeSpan.FromSeconds(lesson == null ? 38 : 18);
        try
        {
            var target = await CommandAsync(lesson == null ? "prepareExport" : "prepareLessonExport", token, selection, kind,
                exportId: download.Id, targetSchoolYear: year, lesson: lesson);
            Check(target, lesson == null ? "reports" : "lessonReport");
            if (lesson != null || target.ExportX.HasValue && target.ExportY.HasValue)
            {
                try
                {
                    if (lesson != null) await PressEnterAsync(token);
                    else await ClickReportAsync(target.ExportX!.Value, target.ExportY!.Value, token);
                }
                catch (Exception error) when (error is COMException or TimeoutException)
                {
                    download.PointerUnavailable = true;
                }
                if (!download.HasBrowserActivity)
                    await Task.WhenAny(download.Finished.Task, Task.Delay(250, token));
                token.ThrowIfCancellationRequested();
            }
            if (!download.Finished.Task.IsCompleted && !download.HasBrowserActivity)
            {
                var observed = await CommandAsync("exportStatus", token, exportId: download.Id);
                download.ClickReceived = observed.ExportClicked;
                if (observed.ExportPrepared && !observed.ExportClicked && !download.HasBrowserActivity)
                {
                    var fallback = await CommandAsync(lesson == null ? "exportFallback" : "lessonExportFallback", token, selection, kind,
                        exportId: download.Id, targetSchoolYear: year, lesson: lesson);
                    Check(fallback, lesson == null ? "reports" : "lessonReport");
                    download.ClickReceived = fallback.ExportClicked;
                    if (!fallback.ExportClicked && !download.HasBrowserActivity && !download.Finished.Task.IsCompleted)
                        throw new InvalidOperationException("Die Berichtsseite hat den Klick auf «CSV-Ausgabe» nicht entgegengenommen.");
                }
            }

            label ??= selection.ExcusedOnly ? "Entschuldigte Fehlzeiten" : CsvReport.Label(kind);
            return await WaitForDownloadAsync(download, label, progress, wait);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(download.TimeoutMessage(), error);
        }
        finally
        {
            if (!download.Finished.Task.IsCompletedSuccessfully) reportsNeedReload = true;
            pending = null;
            download.Cancel();
            if (!token.IsCancellationRequested)
                await CommandAsync("clearExport", token, exportId: download.Id);
        }
    }

    private async Task<byte[]?> WaitForDownloadAsync(PendingDownload download, string label, IProgress<string> progress, TimeSpan limit)
    {
        var waiting = Stopwatch.StartNew();
        var token = download.Token;
        while (!download.Finished.Task.IsCompleted)
        {
            token.ThrowIfCancellationRequested();
            if (!download.FollowedNotificationLink && !download.Transferring)
            {
                var state = await CommandAsync("exportStatus", token, exportId: download.Id);
                if (download.Finished.Task.IsCompleted) break;
                download.ClickReceived |= state.ExportClicked;
                if (state.AmbiguousCsvLinks)
                    throw new InvalidOperationException("WebUntis hat mehrere neue CSV-Links angezeigt. Der Bericht lässt sich nicht eindeutig zuordnen.");
                if (state.CsvUrl is { } url)
                {
                    if (!IsReadyCsvLink(url))
                        throw new InvalidOperationException("Der angezeigte CSV-Link gehört nicht zur WebUntis-Berichtsseite der Schule.");
                    download.FollowedNotificationLink = true;
                    progress.Report($"{label} · CSV wird eingelesen …");
                    await TransferCsvAsync(download, view!, url);
                }
                else if (state.EmptyReport)
                {
                    download.Finished.TrySetResult(null);
                }
            }
            if (waiting.Elapsed >= limit && !download.Finished.Task.IsCompleted)
                    throw new TimeoutException();
            await Task.WhenAny(download.Finished.Task, Task.Delay(220, token));
        }
        return await download.Finished.Task;
    }

    private async Task ClickReportAsync(double x, double y, CancellationToken token)
    {
        var browser = view ?? throw new InvalidOperationException("WebUntis ist nicht geöffnet.");
        foreach (var (type, buttons) in new[] { ("mousePressed", 1), ("mouseReleased", 0) })
        {
            var input = JsonSerializer.Serialize(new { type, x, y, button = "left", buttons, clickCount = 1 }, Json);
            await browser.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", input)
                .WaitAsync(TimeSpan.FromSeconds(6), token);
        }
    }

    private async Task PressEnterAsync(CancellationToken token)
    {
        var browser = view ?? throw new InvalidOperationException("WebUntis ist nicht geöffnet.");
        foreach (var type in new[] { "keyDown", "keyUp" })
        {
            var input = JsonSerializer.Serialize(new { type, key = "Enter", code = "Enter", windowsVirtualKeyCode = 13,
                text = type == "keyDown" ? "\r" : "" }, Json);
            await browser.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", input)
                .WaitAsync(TimeSpan.FromSeconds(4), token);
        }
    }

    private void NavigateReports()
    {
        view!.Stop();
        navigating = true;
        navigationStarted = true;
        responseStatus = 0;
        view.Navigate(ReportsUrl);
    }

    private Task<ReportState> ReadStateAsync(CancellationToken token, bool includeClasses = false) =>
        CommandAsync("probe", token, includeClasses: includeClasses);

    private async Task<ReportState> CommandAsync(string action, CancellationToken token, ReportSelection? selection = null,
        ReportKind kind = ReportKind.Absences, bool includeClasses = false, bool roster = false, string? exportId = null,
        string? waitForFormStamp = null, int? targetSchoolYear = null, string? className = null, LessonChoice? lesson = null,
        CoreWebView2? target = null, AbsencePerson? absencePerson = null, DateOnly? absenceDay = null,
        AbsenceListEntry? absenceRow = null, string? editorId = null, TimeSpan? commandTimeout = null, bool classAbsences = false)
    {
        var browser = target ?? view;
        if (browser == null) return new ReportState();
        if (!IsSchoolAddress(browser.Source))
            return Uri.TryCreate(browser.Source, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
                ? new ReportState { Stage = "external" } : new ReportState();
        var request = JsonSerializer.Serialize(new
        {
            action, origin = Origin, classId = selection?.ClassId,
            from = selection?.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = selection?.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            kind = kind.ToString(), excusedOnly = selection?.ExcusedOnly == true, includeClasses, roster, exportId, waitForFormStamp,
            targetSchoolYear, className, lateOnly = kind == ReportKind.Lateness, lesson, absencePerson, absenceDay,
            absenceRow, editorId, classAbsences
        }, Json);
        try
        {
            var command = "globalThis." + ReportCommandName + "(" + request + ")";
            var result = await browser.ExecuteScriptAsync($"typeof globalThis.{ReportCommandName} === 'function' ? {command} : 'uninitialized'")
                .WaitAsync(commandTimeout ?? TimeSpan.FromSeconds(6), token);
            if (result == "\"uninitialized\"")
                result = await browser.ExecuteScriptAsync(InstallScript + ";\nglobalThis." + ReportCommandName + "?.(" + request + ")")
                    .WaitAsync(commandTimeout ?? TimeSpan.FromSeconds(6), token);
            return JsonSerializer.Deserialize<ReportState>(result, Json) ?? new ReportState();
        }
        catch (Exception error) when ((action is "probe" or "schoolYear" or "schoolYearDates" or "globalSchoolYear" or "configureRange" or "absenceNotes" or "absenceTimes" or "exportStatus" or "clearExport" or
            "lessonList" or "lessonBack" or "schoolPeriods") &&
            (error is COMException or TimeoutException or JsonException))
        {
            return new ReportState();
        }
    }

    private static void Check(ReportState state, string expectedStage = "reports")
    {
        if (state.Error != null) throw new InvalidOperationException(state.Error);
        if (state.Stage != expectedStage) throw new InvalidOperationException("WebUntis hat das Berichtsformular während des Abrufs verlassen.");
    }

    private static Task InstallReportScriptAsync(CoreWebView2 browser, CancellationToken token, TimeSpan? timeout = null) =>
        browser.AddScriptToExecuteOnDocumentCreatedAsync(InstallScript).WaitAsync(timeout ?? TimeSpan.FromSeconds(6), token);

    private static void ConfigureBrowserSettings(CoreWebView2 browser)
    {
        browser.Settings.IsPasswordAutosaveEnabled = false;
        browser.Settings.IsGeneralAutofillEnabled = false;
        browser.Settings.AreHostObjectsAllowed = false;
        browser.Settings.IsWebMessageEnabled = false;
        browser.Settings.AreDefaultScriptDialogsEnabled = false;
    }

    private void ConfigureView(CoreWebView2 browser)
    {
        ConfigureBrowserSettings(browser);
        browser.NavigationStarting += (_, args) =>
        {
            ObserveReportNavigation(browser, args.Uri);
            args.Cancel = args.Uri != "about:blank" &&
                !(pending != null && args.Uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) && IsSchoolAddress(args.Uri)) &&
                (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps);
        };
        browser.FrameNavigationStarting += (_, args) => ObserveReportNavigation(browser, args.Uri);
        browser.DownloadStarting += (_, args) => HandleDownload(browser, args);
        browser.WebResourceResponseReceived += (_, args) => HandleReportResponse(browser, args);
        browser.NewWindowRequested += (_, args) => OpenPopup(browser, args);
        browser.ScriptDialogOpening += (_, args) => HandleDialog(browser, args);
    }

    private async void HandleDownload(CoreWebView2 browser, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Handled = true;
        args.Cancel = true;
        var download = pending;
        if (download == null || !IsReportSender(download, browser) || download.Transferring || download.Finished.Task.IsCompleted) return;

        var operation = args.DownloadOperation;
        var extension = Path.GetExtension(args.ResultFilePath);
        var mime = MediaType(operation.MimeType);
        var csv = IsReadyCsvLink(operation.Uri) || string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase) ||
            IsCsvMediaType(mime) || NamesCsvFile(operation.ContentDisposition) ||
            string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) && mime == "text/plain";
        if (!IsSchoolAddress(operation.Uri) || !csv)
        {
            download.Finished.TrySetException(new InvalidOperationException(!csv
                ? "WebUntis hat eine Datei in einem anderen Format als CSV ausgeliefert."
                : "Der Download stammt von einer anderen Adresse als dem Schulserver."));
            return;
        }
        if (operation.TotalBytesToReceive > CsvReport.MaxFileBytes)
        {
            download.Finished.TrySetException(new InvalidOperationException("Der CSV-Bericht ist größer als 25 MB. Wähle einen kürzeren Zeitraum."));
            return;
        }

        download.BrowserDownload = true;
        if (operation.Uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            (Uri.TryCreate(operation.Uri, UriKind.Absolute, out var uri) &&
                (IsReadyCsvLink(operation.Uri) || IsReportRoute(uri) || uri.AbsolutePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))))
            await TransferCsvAsync(download, browser, operation.Uri);
    }

    private async Task TransferCsvAsync(PendingDownload download, CoreWebView2 browser, string address)
    {
        if (pending != download || download.Transferring || download.Finished.Task.IsCompleted) return;
        download.Transferring = true;
        try
        {
            if (address.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            {
                download.Finished.TrySetResult(await ReadReportBlobAsync(browser, address, download.Token));
                return;
            }
            var uri = new Uri(address);
            var cookies = new CookieContainer(128, 128, 8192);
            var browserCookies = await browser.CookieManager.GetCookiesAsync(address).WaitAsync(TimeSpan.FromSeconds(5), download.Token);
            foreach (var cookie in browserCookies)
            {
                try { cookies.Add(uri, cookie.ToSystemNetCookie()); }
                catch (CookieException) { }
            }
            var content = await ReportTransfer.FetchAsync(uri, new Uri(Origin), cookies.GetCookieHeader(uri), browser.Settings.UserAgent, download.Token);
            download.Finished.TrySetResult(content);
        }
        catch (OperationCanceledException) when (download.Token.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or COMException or TimeoutException or IOException or InvalidDataException or ArgumentException or JsonException or FormatException)
        {
            download.Finished.TrySetException(error);
        }
    }

    private static async Task<byte[]?> ReadReportBlobAsync(CoreWebView2 browser, string address, CancellationToken token)
    {
        if (!IsSchoolAddress(address)) throw new InvalidOperationException("Der Bericht stammt nicht vom WebUntis-Schulserver.");
        var expression = $$"""
            (async () => {
                if (location.origin !== {{JsonSerializer.Serialize(Origin)}}) return null;
                const response = await fetch({{JsonSerializer.Serialize(address)}});
                if (!response.ok) return null;
                const file = await response.blob();
                if (file.size > {{CsvReport.MaxFileBytes}}) return null;
                return await new Promise((resolve, reject) => {
                    const reader = new FileReader();
                    reader.onload = () => resolve(reader.result.split(',', 2)[1]);
                    reader.onerror = reject;
                    reader.readAsDataURL(file);
                });
            })()
            """;
        var request = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }, Json);
        var result = await browser.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request).WaitAsync(TimeSpan.FromSeconds(12), token);
        using var reply = JsonDocument.Parse(result);
        if (!reply.RootElement.TryGetProperty("result", out var value) || !value.TryGetProperty("value", out var content) ||
            content.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Die von WebUntis erzeugte CSV ließ sich nicht im Arbeitsspeicher lesen.");
        var bytes = Convert.FromBase64String(content.GetString()!);
        if (bytes.Length > CsvReport.MaxFileBytes) throw new InvalidDataException("Der Bericht ist größer als 25 MB.");
        return bytes.Length == 0 ? null : bytes;
    }

    private async void HandleReportResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        var download = pending;
        if (download == null || !IsReportSender(download, sender) || download.Finished.Task.IsCompleted) return;
        var readingThisResponse = false;
        try
        {
            var request = args.Request;
            if (!IsSchoolAddress(request.Uri)) return;
            var response = args.Response;
            var mime = MediaType(ReadHeader(response.Headers, "Content-Type"));
            var disposition = ReadHeader(response.Headers, "Content-Disposition");
            var csv = IsCsvMediaType(mime) || NamesCsvFile(disposition);
            var uri = new Uri(request.Uri);
            var reportFormResponse = IsReportRoute(uri);
            if (!csv && !reportFormResponse) return;

            download.HttpStatus = response.StatusCode;
            download.HttpMediaType = mime;
            if (response.StatusCode is 401 or 403)
            {
                download.Finished.TrySetException(new InvalidOperationException($"WebUntis hat den Bericht abgelehnt (HTTP {response.StatusCode})."));
                return;
            }
            if (response.StatusCode >= 400)
            {
                download.Finished.TrySetException(new InvalidOperationException($"Der Schulserver meldet beim Bericht HTTP {response.StatusCode}."));
                return;
            }

            if (!csv || response.StatusCode is < 200 or >= 300 || IsAttachment(disposition) ||
                download.Transferring || download.ReadingInline) return;
            download.ReadingInline = readingThisResponse = true;
            using var content = await response.GetContentAsync();
            if (content == null || pending != download || download.Transferring) return;
            var bytes = await Task.Run(() => ReportTransfer.ReadAsync(content, download.Token), download.Token);
            if (pending == download && !download.Transferring) download.Finished.TrySetResult(bytes);
        }
        catch (OperationCanceledException) when (download.Token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or COMException or UnauthorizedAccessException or InvalidDataException)
        {
            if (pending == download && !download.Transferring && readingThisResponse)
                download.Finished.TrySetException(new InvalidOperationException("Die CSV-Serverantwort konnte nicht gelesen werden. " + error.Message));
        }
        finally
        {
            if (readingThisResponse) download.ReadingInline = false;
        }
    }

    private void ObserveReportNavigation(CoreWebView2 browser, string address)
    {
        if (pending is { } download && IsReportSender(download, browser) && IsSchoolAddress(address) &&
            Uri.TryCreate(address, UriKind.Absolute, out var uri) && IsReportRoute(uri))
        {
            download.ReportNavigation = true;
        }
    }

    private static bool IsReportRoute(Uri uri) =>
        uri.AbsolutePath.StartsWith("/WebUntis/", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.EndsWith(".do", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Contains("report", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadyCsvLink(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !IsSchoolAddress(address) ||
            uri.UserInfo.Length != 0 || !uri.AbsolutePath.Equals("/WebUntis/reports.do", StringComparison.OrdinalIgnoreCase))
            return false;
        var query = HttpUtility.ParseQueryString(uri.Query);
        return string.Equals(query["format"], "csv", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(query["msgId"]);
    }

    private bool IsReportSender(PendingDownload download, object? sender) =>
        ReferenceEquals(sender, view) || download.Windows.Any(window => ReferenceEquals(sender, window.Browser));

    private static string ReadHeader(CoreWebView2HttpResponseHeaders headers, string name) =>
        headers.Contains(name) ? headers.GetHeader(name) : "";

    private static string MediaType(string? value)
    {
        var mediaType = value?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? "";
        return mediaType.Length <= 80 && mediaType.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '+' or '.' or '_')
            ? mediaType : "";
    }

    private static bool IsCsvMediaType(string mime) =>
        mime is "text/csv" or "application/csv" or "text/x-csv" or "application/x-csv" or "text/comma-separated-values";

    private static bool NamesCsvFile(string? value) =>
        ContentDispositionHeaderValue.TryParse(value, out var header) &&
        string.Equals(Path.GetExtension(header.FileNameStar ?? header.FileName?.Trim('"')), ".csv", StringComparison.OrdinalIgnoreCase);

    private static bool IsAttachment(string value) =>
        ContentDispositionHeaderValue.TryParse(value, out var header) &&
        header.DispositionType.Equals("attachment", StringComparison.OrdinalIgnoreCase);

    private void HandleDialog(object? sender, CoreWebView2ScriptDialogOpeningEventArgs args)
    {
        if (pending is { } download && IsReportSender(download, sender) && IsSchoolAddress(args.Uri))
        {
            var message = args.Message;
            if (args.Kind == CoreWebView2ScriptDialogKind.Alert &&
                !Regex.IsMatch(message, "keine Berechtigung|kein Zugriff|nicht berechtigt", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(message,
                @"(?:keine\s+(?:Daten|Einträge|Ergebnisse|Fehlzeiten|Abwesenheiten|Anwesenheiten|Verspätungen)|no\s+(?:data|results))",
                RegexOptions.IgnoreCase))
            {
                args.Accept();
                download.Finished.TrySetResult(null);
            }
            else download.Finished.TrySetException(new InvalidOperationException("WebUntis hat beim Erstellen des Berichts eine Meldung angezeigt."));
            return;
        }
        if (mainWindow?.Visible != true && !loginPopups.Any(popup => popup.Visible)) return;
        var dialogOwner = loginPopups.LastOrDefault(popup => popup.Visible) ?? mainWindow!;
        var deferral = args.GetDeferral();
        dialogOwner.BeginInvoke(() =>
        {
            using (deferral)
            {
                if (dialogOwner.IsDisposed || !dialogOwner.Visible) return;
                if (args.Kind == CoreWebView2ScriptDialogKind.Alert)
                {
                    MessageBox.Show(dialogOwner, args.Message, "WebUntis");
                    args.Accept();
                }
                else if (args.Kind is CoreWebView2ScriptDialogKind.Confirm or CoreWebView2ScriptDialogKind.Beforeunload &&
                    MessageBox.Show(dialogOwner, args.Message, "WebUntis", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    args.Accept();
            }
        });
    }

    private async void OpenPopup(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        if (environment == null || disposed) return;
        using var deferral = args.GetDeferral();
        args.Handled = true;
        var download = pending;
        try
        {
            if (download != null)
            {
                if (IsReportSender(download, sender)) await OpenReportWindowAsync(download, args);
                return;
            }
            if (mainWindow?.Visible != true) return;
            var popup = new LoginWindow();
            try
            {
                await popup.InitializeAsync(environment);
                if (disposed || mainWindow?.Visible != true)
                {
                    popup.Dispose();
                    return;
                }
                ConfigureView(popup.Browser);
                popup.Browser.SourceChanged += (_, _) => popup.ShowAddress(popup.Browser.Source);
                popup.Browser.WindowCloseRequested += (_, _) => popup.Hide();
                loginPopups.Add(popup);
                args.NewWindow = popup.Browser;
                popup.ShowForLogin(mainWindow);
            }
            catch
            {
                popup.Dispose();
                throw;
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            download?.Finished.TrySetException(new InvalidOperationException("Das WebUntis-Berichtsfenster konnte nicht geöffnet werden."));
        }
    }

    private async Task OpenReportWindowAsync(PendingDownload download, CoreWebView2NewWindowRequestedEventArgs args)
    {
        download.PopupRequested = true;
        if (args.Uri is not ("" or "about:blank") && !IsSchoolAddress(args.Uri))
        {
            download.Finished.TrySetException(new InvalidOperationException("Das Berichtsfenster verweist auf eine andere Adresse als den Schulserver."));
            return;
        }
        var browser = await CreateReportViewAsync(download);
        if (browser != null) args.NewWindow = browser;
    }

    private async Task<CoreWebView2?> CreateReportViewAsync(PendingDownload download)
    {
        var popup = new LoginWindow();
        try
        {
            await popup.InitializeAsync(environment!);
            if (pending != download || disposed)
            {
                popup.Dispose();
                return null;
            }
            var browser = popup.Browser;
            ConfigureView(browser);
            browser.NavigationStarting += (_, args) =>
            {
                if (args.Uri == "about:blank" || IsSchoolAddress(args.Uri)) return;
                args.Cancel = true;
                download.Finished.TrySetException(new InvalidOperationException("Der CSV-Download verweist auf eine andere Adresse als den Schulserver."));
            };
            browser.NavigationCompleted += (_, completed) => InspectReportPage(download, browser, completed.HttpStatusCode);
            download.Windows.Add(popup);
            return browser;
        }
        catch
        {
            popup.Dispose();
            throw;
        }
    }

    private async void InspectReportPage(PendingDownload download, CoreWebView2 browser, int status)
    {
        if (pending != download || download.Finished.Task.IsCompleted || !IsSchoolAddress(browser.Source) || status == 0)
            return;

        download.HttpStatus ??= status;
        if (status >= 400)
        {
            download.Finished.TrySetException(new InvalidOperationException($"Der Schulserver meldet im Berichtsfenster HTTP {status}."));
            return;
        }

        try
        {
            const string inspect = """
                (() => {
                    if (location.origin !== "https://bkt.webuntis.com") return {};
                    if (document.querySelector('input[type="password"]')) return { notice: "login" };
                    const text = (document.body?.innerText ?? "").trim();
                    if (text.length < 1600) {
                        if (/keine berechtigung|kein zugriff|nicht berechtigt/i.test(text)) return { notice: "denied" };
                        if (/keine\s+(daten|einträge|ergebnisse|fehlzeiten|abwesenheiten|anwesenheiten|verspätungen)|no\s+(data|results)/i.test(text)) {
                            return { notice: "empty" };
                        }
                    }
                    const links = [...document.querySelectorAll("a[href]")]
                        .filter(a => a.getClientRects().length > 0 &&
                            (/\.csv$/i.test(a.download ?? "") || /\.csv$/i.test(new URL(a.href).pathname)));
                    if (links.length === 1) return { csvUrl: links[0].href };
                    return {};
                })()
                """;
            var json = await browser.ExecuteScriptAsync(inspect).WaitAsync(TimeSpan.FromSeconds(5), download.Token);
            if (pending != download || download.Finished.Task.IsCompleted) return;
            var page = JsonSerializer.Deserialize<ReportPage>(json, Json);
            if (page?.Notice == "empty")
                download.Finished.TrySetResult(null);
            else if (page?.Notice == "login")
                download.Finished.TrySetException(new InvalidOperationException("WebUntis verlangt für den Bericht eine neue Anmeldung."));
            else if (page?.Notice == "denied")
                download.Finished.TrySetException(new InvalidOperationException("WebUntis meldet fehlende Berechtigung für diesen Bericht."));
            else if (!download.FollowedCsvLink && page?.CsvUrl is { } csvUrl && IsSchoolAddress(csvUrl))
            {
                download.FollowedCsvLink = true;
                await TransferCsvAsync(download, browser, csvUrl);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or COMException or JsonException) { }
    }

    private void HideLogin()
    {
        foreach (var popup in loginPopups) popup.Dispose();
        loginPopups.Clear();
        mainWindow?.Hide();
    }

    private void ResetWindow()
    {
        HideLogin();
        CloseSupplementPages();
        supplementsUsingMainView.Clear();
        view = null;
        mainWindow?.Dispose();
        mainWindow = null;
        navigating = false;
        navigationStarted = false;
        reportsNeedReload = false;
        SessionConfirmed = false;
        responseStatus = 0;
    }

    public void Dispose()
    {
        disposed = true;
        ResetWindow();
        supplementFallback.Dispose();
    }

    private static bool IsSchoolAddress(string? value)
    {
        if (value?.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) == true) value = value[5..];
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.GetLeftPart(UriPartial.Authority).Equals(Origin, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Reports.js")
            ?? throw new InvalidOperationException("Die WebUntis-Berichtssteuerung fehlt.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record ReportSelection(string ClassId, DateOnly From, DateOnly To, bool ExcusedOnly = false);
    private sealed record AbsencePerson(string Id, string Name);
    private sealed record EditorMessage(string EditorId, string Action, bool SaveRequested, int Width, int Height);
    private sealed class SupplementPage(LoginWindow window, int year) : IDisposable
    {
        public CoreWebView2 Browser => window.Browser;
        public LoginWindow Window => window;
        public int Year { get; } = year;
        public int Status { get; set; }
        public bool Redirected { get; set; }
        public AbsenceListEntry[]? Absences { get; set; }
        public AbsenceNote[]? AbsenceNotes { get; set; }
        public void Dispose() => window.Dispose();
    }

    private sealed record LessonChoice(string Key, string[] Classes, string Group, string Subject);
    private sealed record PeriodRow(int Number, string Start, string End)
    {
        public SchoolPeriod? ToPeriod() => Number > 0 && CsvReport.ReadTime(Start) is { } from &&
            CsvReport.ReadTime(End) is { } to && from < to ? new SchoolPeriod(Number, from, to) : null;
    }

    private sealed class ReportState
    {
        public string Stage { get; set; } = "";
        public string? Error { get; set; }
        public string? ClassId { get; set; }
        public bool AtReports { get; set; }
        public bool HasFrame { get; set; }
        public bool HasStudentFilter { get; set; }
        public bool AllStudents { get; set; }
        public bool HasDateInputs { get; set; }
        public string? FormStamp { get; set; }
        public string? RosterStamp { get; set; }
        public bool RangeReady { get; set; }
        public bool FlagsReady { get; set; }
        public string? ChangedField { get; set; }
        public bool ReloadExpected { get; set; }
        public bool ExportPrepared { get; set; }
        public bool ExportClicked { get; set; }
        public double? ExportX { get; set; }
        public double? ExportY { get; set; }
        public string? CsvUrl { get; set; }
        public bool AmbiguousCsvLinks { get; set; }
        public bool EmptyReport { get; set; }
        public bool SchoolYearSelected { get; set; }
        public string? GlobalYear { get; set; }
        public bool GlobalYearOption { get; set; }
        public bool GlobalYearUnavailable { get; set; }
        public bool SchoolYearRequested { get; set; }
        public bool SchoolYearOption { get; set; }
        public bool SchoolYearUnavailable { get; set; }
        public double? SchoolYearX { get; set; }
        public double? SchoolYearY { get; set; }
        public string? SchoolYearFrom { get; set; }
        public string? SchoolYearTo { get; set; }
        public bool NotesReady { get; set; }
        public bool NotesUnavailable { get; set; }
        public AbsenceNote[] AbsenceNotes { get; set; } = [];
        public bool AbsenceActionsReady { get; set; }
        public AbsenceListEntry[] EditableAbsences { get; set; } = [];
        public bool AbsenceEditorRequested { get; set; }
        public bool AbsenceEditorVisible { get; set; }
        public int EditorWidth { get; set; }
        public int EditorHeight { get; set; }
        public bool TimesReady { get; set; }
        public AbsenceTimeRow[] Times { get; set; } = [];
        public PeriodRow[] Periods { get; set; } = [];
        public LessonChoice[] Lessons { get; set; } = [];
        public Student[] Classes { get; set; } = [];
        public Student[] Students { get; set; } = [];
    }

    private sealed record AbsenceTimeRow(string Name, string Day, string Subject, string Start, string End, string Units,
        string Minutes, string Status, string Reason, string Text, bool Counts)
    {
        public AttendanceEntry? ToEntry(ReportKind kind)
        {
            if (CsvReport.ReadDay(Day) is not { } day || !int.TryParse(Minutes, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes < 0)
                return null;
            var late = kind == ReportKind.Lateness;
            return new AttendanceEntry(Name, "", day, late ? EntryKind.Late : EntryKind.Absent, CsvReport.ReadExcuse(Status),
                CsvReport.ReadTime(Start), CsvReport.ReadTime(End), Subject: Subject, Minutes: minutes,
                Units: late ? null : CsvReport.ReadDecimal(Units), Counts: Counts, Reason: Reason, AbsenceText: Text);
        }
    }

    private sealed record ReportPage(string? Notice, string? CsvUrl);

    private sealed class PendingDownload : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public CancellationToken Token { get; }

        public List<LoginWindow> Windows { get; } = [];
        public TaskCompletionSource<byte[]?> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ClickReceived { get; set; }
        public bool PointerUnavailable { get; set; }
        public bool PopupRequested { get; set; }
        public bool ReportNavigation { get; set; }
        public bool ReadingInline { get; set; }
        public bool FollowedCsvLink { get; set; }
        public bool FollowedNotificationLink { get; set; }
        public bool Transferring { get; set; }
        public bool BrowserDownload { get; set; }
        public int? HttpStatus { get; set; }
        public string HttpMediaType { get; set; } = "";
        public bool HasBrowserActivity => BrowserDownload || PopupRequested || ReportNavigation || HttpStatus.HasValue;

        public PendingDownload(CancellationToken token)
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = cancellation.Token;
        }

        public string TimeoutMessage()
        {
            if (Transferring) return "Die CSV wurde angefordert, aber nicht rechtzeitig übertragen.";
            if (FollowedNotificationLink) return "WebUntis hat den CSV-Link angezeigt. Der Download wurde nicht abgeschlossen.";
            if (BrowserDownload) return "WebUntis hat einen Download geöffnet. Der Bericht ließ sich nicht im Arbeitsspeicher einlesen.";
            if (HttpStatus is { } status)
            {
                if (status is >= 200 and < 300 && HttpMediaType == "application/json")
                    return "WebUntis hat die Berichtsanforderung beantwortet. Der CSV-Link wurde nicht angezeigt.";
                var format = HttpMediaType.Length > 0 ? $" ({HttpMediaType})" : "";
                return $"WebUntis hat mit HTTP {status}{format} geantwortet. Der CSV-Bericht blieb aus.";
            }
            if (PopupRequested) return "WebUntis hat ein Berichtsfenster geöffnet. Das Fenster lieferte keine CSV-Datei.";
            if (ReportNavigation) return "WebUntis hat den Bericht angefordert. Eine Serverantwort blieb aus.";
            if (ClickReceived) return "Der CSV-Klick wurde bestätigt. WebUntis hat darauf keinen Bericht geöffnet oder übertragen.";
            if (PointerUnavailable) return "Die Browser-Eingabe konnte den CSV-Knopf nicht erreichen.";
            return "Der Browser hat den Klick auf «CSV-Ausgabe» nicht bestätigt.";
        }

        public void Cancel()
        {
            cancellation.Cancel();
            foreach (var popup in Windows) popup.Dispose();
        }

        public void Dispose() => cancellation.Dispose();
    }
}

internal sealed record ReportCsv(ReportKind Kind, byte[] Content);
internal sealed record LessonEmailBatch(RosterEmail[] Emails, int Read, int Total, string? Failure);

internal sealed record AbsenceListEntry(string Id, string StudentId, string StudentName, DateOnly From, DateOnly To,
    string Start, string End, string Status, string Reason, bool CanExcuse);

internal sealed class ReportBatch(Student[] students, string classId)
{
    public string ClassId { get; } = classId;
    public Student[] Students { get; } = students;
    public List<ReportCsv> Csvs { get; } = [];
    public List<ReportKind> EmptyReports { get; } = [];
    public List<string> Notes { get; } = [];

}
