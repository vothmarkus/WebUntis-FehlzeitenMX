using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Android.Webkit;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Core.WebUntisReports;

namespace WebUntisAuswertung.Mobile;

internal sealed class WebUntisSignInRequiredException()
    : Exception("Melde dich bei WebUntis an, um die Daten zu aktualisieren.");

internal sealed partial class AndroidReports(MobileWebView view, Action<bool> showLogin, Action<bool> showExcuse,
    Func<CancellationToken, Task> sessionConfirmed) : IReportSupplement
{
    private const string LoginPage = Origin + "/WebUntis/?school=bkt";
    private static readonly Uri School = new(Origin);
    private readonly Dictionary<int, SchoolPeriod[]> schoolPeriods = [];
    private readonly SemaphoreSlim supplements = new(1);
    private bool reload = true;
    private bool continueRequested;
    private bool allowMatrixSignIn;
    private Student? currentClass;

    public async Task<(Student[] Classes, ReportRange SchoolYear)> ClassesAsync(IProgress<string> progress, CancellationToken token,
        bool startWithLogin, bool allowSignIn = true)
    {
        try
        {
            var state = await OpenReportsAsync(progress, token, includeClasses: true, startWithLogin: startWithLogin, allowSignIn: allowSignIn);
            var current = SchoolYears.StartYear(DateOnly.FromDateTime(DateTime.Today));
            if (state.GlobalYear is { } shown && shown != current.ToString(CultureInfo.InvariantCulture))
            {
                await SelectYearAsync(current, token);
                await OpenReportsAsync(progress, token, includeClasses: true, allowSignIn: allowSignIn);
            }
            var year = await ReadSchoolYearAsync(token);
            if (year == null || SchoolYears.StartYear(year.From) != current) year = SchoolYears.CalendarRange(current);
            state = await CommandAsync("probe", token, includeClasses: true);
            Check(state);
            var classes = state.Classes.Where(item => item.Name.Length > 0).DistinctBy(item => item.Id)
                .OrderBy(item => item.Name, StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), true)).ToArray();
            if (classes.Length == 0) throw new InvalidOperationException("WebUntis bietet deinem Konto keine Klassen oder Gruppen für Berichte an.");
            return (classes, year);
        }
        catch { reload = true; throw; }
        finally { showLogin(false); }
    }

    public async Task<MatrixData> MatrixAsync(Student choice, ReportRange range, IProgress<string> progress,
        CancellationToken token, bool allowSignIn, bool afterExcuse = false)
    {
        var selection = new ReportSelection(choice.Id, range.From, range.To);
        currentClass = choice;
        allowMatrixSignIn = allowSignIn;
        try
        {
            await PrepareAsync(selection, progress, token);
            var roster = await CommandAsync("readRoster", token, selection, roster: true);
            Check(roster);
            if (roster.ClassId != choice.Id || !roster.AllStudents || !roster.RangeReady)
                throw new InvalidOperationException("WebUntis hat Klasse, Zeitraum oder Namensliste während des Abrufs geändert.");
            var batch = new ReportBatch(roster.Students, choice.Id);
            foreach (var kind in Automatic)
            {
                try
                {
                    if (reload) await PrepareAsync(selection, progress, token);
                    var content = await FetchReportAsync(selection, kind, progress, token);
                    if (content == null) batch.EmptyReports.Add(kind);
                    else batch.Csvs.Add(new ReportCsv(kind, content));
                }
                catch (Exception error) when (ReportFailure(error))
                {
                    batch.Notes.Add($"{CsvReport.Label(kind)} · {error.Message}");
                    reload = true;
                }
            }
            return await ReportAnalysis.ReadAsync(choice.Name, range, batch, this, progress, token, afterExcuse: afterExcuse);
        }
        catch { reload = true; throw; }
        finally
        {
            currentClass = null;
            allowMatrixSignIn = false;
            showLogin(false);
        }
    }

    public async Task SignOutAsync()
    {
        reload = true;
        schoolPeriods.Clear();
        await view.SignOutAsync();
    }

    public void ContinueAfterLogin() => continueRequested = true;

    private async Task<ReportState> OpenReportsAsync(IProgress<string> progress, CancellationToken token,
        bool includeClasses = false, bool startWithLogin = false, bool allowSignIn = true)
    {
        var waiting = Stopwatch.StartNew();
        var waitingForLogin = false;
        var loginShown = false;
        var redirected = false;
        var browserPromptHandled = false;
        continueRequested = false;
        if (startWithLogin) OpenLogin(navigate: true);
        else
        {
            progress.Report("WebUntis-Berichte werden geöffnet …");
            if (reload || !IsPage(Address)) view.Go(Address);
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (view.RendererGone)
                throw new InvalidOperationException("Android WebView wurde beendet. Starte die App erneut.");
            var state = await ReadReportPageAsync(includeClasses, waitingForLogin && !browserPromptHandled, token);
            if (state?.Stage == "reports" && !view.Navigating)
            {
                showLogin(false);
                reload = false;
                await sessionConfirmed(token);
                return state;
            }
            if (!view.Navigating && view.MainStatus >= 500)
                throw new InvalidOperationException(waitingForLogin ? "Die WebUntis-Anmeldeseite ist derzeit nicht erreichbar."
                    : "Die WebUntis-Berichte sind derzeit nicht erreichbar.");

            if (waitingForLogin)
            {
                if (!view.Navigating && state is { BrowserLoginX: { } x, BrowserLoginY: { } y })
                {
                    browserPromptHandled = true;
                    try
                    {
                        if (await view.TapAsync(x, y, token)) continueRequested = false;
                    }
                    catch (TimeoutException) { }
                }
                else if (!view.Navigating && (continueRequested || state is { Stage: "signedIn", AtReports: false }))
                {
                    waitingForLogin = false;
                    OpenReportPage();
                }
            }
            else if (!view.Navigating)
            {
                if (state?.Stage is "login" or "external" || view.MainStatus == 401)
                    OpenLogin(navigate: state?.Stage is not ("login" or "external"));
                else if (view.MainStatus == 403 || state?.Stage == "denied")
                    throw new InvalidOperationException("WebUntis meldet fehlende Berechtigung für die Klassenbuchberichte.");
                else if (state is { Stage: "signedIn", AtReports: false } && !redirected)
                    OpenReportPage();
            }

            if (!waitingForLogin && waiting.Elapsed > TimeSpan.FromSeconds(28))
            {
                if (!allowSignIn || loginShown)
                    throw new TimeoutException("WebUntis hat das Berichtsformular nicht geladen. Melde dich erneut an und tippe danach auf Weiter.");
                OpenLogin(navigate: true);
            }
            await Task.Delay(240, token);
        }

        void OpenLogin(bool navigate)
        {
            if (!allowSignIn) throw new WebUntisSignInRequiredException();
            waitingForLogin = loginShown = true;
            continueRequested = false;
            redirected = false;
            browserPromptHandled = false;
            progress.Report("Melde dich auf der WebUntis-Seite an.");
            showLogin(true);
            if (navigate) view.Go(LoginPage);
        }

        void OpenReportPage()
        {
            continueRequested = false;
            redirected = true;
            progress.Report("WebUntis-Berichte werden geöffnet …");
            view.Go(Address);
            waiting.Restart();
        }
    }

    private async Task<ReportState?> ReadReportPageAsync(bool includeClasses, bool openBrowserLogin, CancellationToken token)
    {
        if (view.Navigating) return null;
        try { return await CommandAsync("probe", token, includeClasses: includeClasses, openBrowserLogin: openBrowserLogin); }
        catch (TimeoutException) { return null; }
    }

    private async Task<ReportRange?> ReadSchoolYearAsync(CancellationToken token)
    {
        var waiting = Stopwatch.StartNew();
        ReportState? selected = null;
        ReportRange? previous = null;
        var opened = false;
        do
        {
            var state = await CommandAsync(selected == null ? "schoolYear" : "schoolYearDates", token);
            if (selected == null)
            {
                if (state.SchoolYearRequested) selected = state;
                else if (state.SchoolYearX is { } x && state.SchoolYearY is { } y && (state.SchoolYearOption || !opened) &&
                    await view.TapAsync(x, y, token))
                {
                    if (state.SchoolYearOption) selected = state;
                    else opened = true;
                }
                else if (state.SchoolYearUnavailable) break;
            }
            else if (state.HasDateInputs && (selected.SchoolYearSelected || selected.FormStamp != state.FormStamp ||
                     state.SchoolYearFrom != selected.SchoolYearFrom || state.SchoolYearTo != selected.SchoolYearTo) &&
                DateOnly.TryParseExact(state.SchoolYearFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) &&
                DateOnly.TryParseExact(state.SchoolYearTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
            {
                var candidate = new ReportRange(from, to);
                if (candidate.IsValid() && from < to)
                {
                    if (candidate == previous) return candidate;
                    previous = candidate;
                }
            }
            await Task.Delay(220, token);
        } while (waiting.Elapsed < TimeSpan.FromSeconds(6));
        return null;
    }

    private async Task SelectYearAsync(int year, CancellationToken token)
    {
        var expected = year.ToString(CultureInfo.InvariantCulture);
        var waiting = Stopwatch.StartNew();
        var opened = false;
        var chosen = false;
        do
        {
            var state = await CommandAsync("globalSchoolYear", token, year: year);
            if (state.GlobalYear == expected && !view.Navigating)
            {
                reload = true;
                return;
            }
            if (state.GlobalYearUnavailable) break;
            if (!chosen && state.SchoolYearX is { } x && state.SchoolYearY is { } y &&
                (state.GlobalYearOption || !opened) && await view.TapAsync(x, y, token))
            {
                if (state.GlobalYearOption) chosen = true;
                else opened = true;
            }
            await Task.Delay(180, token);
        } while (waiting.Elapsed < TimeSpan.FromSeconds(10));
        throw new InvalidOperationException($"WebUntis hat den Wechsel auf das Schuljahr {SchoolYears.Label(year)} nicht bestätigt.");
    }

    private async Task PrepareAsync(ReportSelection selection, IProgress<string> progress, CancellationToken token)
    {
        var start = await OpenReportsAsync(progress, token, allowSignIn: allowMatrixSignIn);
        var year = SchoolYears.StartYear(selection.From);
        if (start.GlobalYear is { } shown && shown != year.ToString(CultureInfo.InvariantCulture))
        {
            await SelectYearAsync(year, token);
            await OpenReportsAsync(progress, token, allowSignIn: allowMatrixSignIn);
        }
        start = await ConfigureRangeAsync(selection, token);
        if (currentClass is { } choice && !start.Classes.Any(item =>
                item.Id == choice.Id && item.Name.Equals(choice.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Die gewählte Klasse fehlt im Berichtsformular für diesen Zeitraum. Aktualisiere die Klassenliste.");
        if (start.ClassId != selection.ClassId)
        {
            Check(await CommandAsync("selectClass", token, selection));
            start = await WaitForFormAsync(selection, state => state.HasStudentFilter &&
                (state.FormStamp != start.FormStamp || state.RosterStamp != start.RosterStamp), token);
        }
        var selected = await ConfigureRangeAsync(selection, token);
        if (selected.ClassId != selection.ClassId)
            throw new InvalidOperationException("WebUntis hat beim Einstellen des Zeitraums die Klasse gewechselt.");
        if (!selected.AllStudents)
        {
            Check(await CommandAsync("selectAll", token, selection));
            await WaitForFormAsync(selection, state => state.AllStudents, token);
        }
    }

    private async Task<ReportState> ConfigureRangeAsync(ReportSelection selection, CancellationToken token)
    {
        var waiting = Stopwatch.StartNew();
        string? changed = null;
        do
        {
            var state = await CommandAsync("configureRange", token, selection, includeClasses: true, waitForFormStamp: changed);
            if (state.Error != null || state.Stage is "login" or "external") Check(state);
            if (state.RangeReady) return state;
            if (state.ChangedField != null) changed = state.FormStamp;
            await Task.Delay(160, token);
        } while (waiting.Elapsed < TimeSpan.FromSeconds(12));
        throw new InvalidOperationException("WebUntis hat den Zeitraum im Berichtsformular nicht bestätigt.");
    }

    private async Task<ReportState> WaitForFormAsync(ReportSelection selection, Func<ReportState, bool> accept, CancellationToken token)
    {
        var waiting = Stopwatch.StartNew();
        do
        {
            var state = await CommandAsync("probe", token);
            if (state.Stage == "reports" && state.ClassId == selection.ClassId && accept(state)) return state;
            if (state.Stage is "login" or "external") Check(state);
            await Task.Delay(160, token);
        } while (waiting.Elapsed < TimeSpan.FromSeconds(8));
        throw new InvalidOperationException("WebUntis hat die Klasse oder die Berichtsoptionen nicht bestätigt.");
    }

    private async Task ConfigureReportAsync(ReportSelection selection, ReportKind kind, CancellationToken token)
    {
        await WaitForFormAsync(selection, state => state.AllStudents && state.HasDateInputs, token);
        for (var step = 0; step < 12; step++)
        {
            var changed = await CommandAsync("configure", token, selection, kind);
            Check(changed);
            if (changed.RangeReady && changed.FlagsReady) return;
            if (changed.ChangedField == null) break;
            if (!changed.ReloadExpected) await Task.Delay(220, token);
            await WaitForFormAsync(selection, state => state.AllStudents && state.HasDateInputs &&
                (!changed.ReloadExpected || changed.FormStamp != state.FormStamp), token);
        }
        throw new InvalidOperationException("WebUntis hat Zeitraum oder Berichtsoptionen nicht übernommen.");
    }

    private async Task<byte[]?> FetchReportAsync(ReportSelection selection, ReportKind kind, IProgress<string> progress, CancellationToken token)
    {
        var label = selection.ExcusedOnly ? "Entschuldigte Fehlzeiten" : CsvReport.Label(kind);
        progress.Report($"{label} · Bericht wird eingestellt …");
        await ConfigureReportAsync(selection, kind, token);
        progress.Report($"{label} · WebUntis erstellt die CSV …");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var export = new MobileWebView.Export(deadline.Token);
        var id = Guid.NewGuid().ToString("N");
        view.PendingExport = export;
        try
        {
            var prepared = await CommandAsync("prepareExport", export.Token, selection, kind, exportId: id);
            Check(prepared);
            if (prepared.ExportX is { } x && prepared.ExportY is { } y && await view.TapAsync(x, y, export.Token))
                await Task.WhenAny(export.Download.Task, Task.Delay(250, export.Token));
            var status = await CommandAsync("exportStatus", export.Token, exportId: id);
            if (!status.ExportClicked && !export.Download.Task.IsCompleted && !export.Empty && export.Error == null)
            {
                var submitted = await CommandAsync("exportFallback", export.Token, selection, kind, exportId: id);
                Check(submitted);
                if (!submitted.ExportClicked && !export.Download.Task.IsCompleted && !export.Empty)
                    throw new InvalidOperationException("WebUntis hat den Klick auf «CSV-Ausgabe» nicht bestätigt.");
            }
            while (true)
            {
                export.Token.ThrowIfCancellationRequested();
                if (export.Error is { } message) throw new InvalidOperationException(message);
                if (export.Download.Task.IsCompletedSuccessfully)
                {
                    var download = await export.Download.Task;
                    progress.Report($"{label} · CSV wird gelesen …");
                    return await DownloadAsync(download, id, export.Token);
                }
                status = await CommandAsync("exportStatus", export.Token, exportId: id);
                if (status.AmbiguousCsvLinks)
                    throw new InvalidOperationException("WebUntis hat mehrere neue CSV-Links angezeigt.");
                if (status.CsvUrl is { } url)
                {
                    if (!IsReadyCsvLink(url)) throw new InvalidOperationException("Der CSV-Link gehört nicht zur Berichtsseite der Schule.");
                    progress.Report($"{label} · CSV wird gelesen …");
                    return await DownloadHttpAsync(url, view.UserAgent, export.Token);
                }
                if (export.Empty || status.EmptyReport) return null;
                if (view.MainStatus is 401 or 403) throw new InvalidOperationException("WebUntis hat den Bericht abgelehnt.");
                await Task.WhenAny(export.Download.Task, Task.Delay(220, export.Token));
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("WebUntis hat die CSV oder Leermeldung nicht rechtzeitig geliefert.");
        }
        finally
        {
            view.PendingExport = null;
            view.CloseReportWindows(export);
            reload |= !IsPage(Address);
            if (!token.IsCancellationRequested && !reload)
            {
                try { await CommandAsync("clearExport", token, exportId: id); }
                catch (Exception error) when (ReportFailure(error)) { reload = true; }
            }
        }
    }

    private static Task<byte[]?> DownloadHttpAsync(string address, string userAgent, CancellationToken token)
    {
        if (!IsSchoolAddress(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Der CSV-Link gehört nicht zum WebUntis-Schulserver.");
        var cookies = CookieManager.Instance?.GetCookie(address) ?? "";
        return ReportTransfer.FetchAsync(uri, School, cookies, userAgent, token);
    }

    private static async Task<byte[]?> DownloadAsync(MobileWebView.Download download, string id, CancellationToken token)
    {
        if (!download.Url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            return await DownloadHttpAsync(download.Url, download.UserAgent, token);

        const string property = "__fehlzeitenMxCsv";
        var key = JsonSerializer.Serialize(id, Json);
        var data = JsonSerializer.Serialize(download.Url, Json);
        var school = JsonSerializer.Serialize(Origin, Json);
        var start = $$"""
            (() => {
                if (location.origin !== {{school}}) return false;
                const slot = { id: {{key}}, done: false, file: null };
                globalThis.{{property}} = slot;
                fetch({{data}}).then(response => {
                    if (!response.ok) throw new Error();
                    return response.blob();
                }).then(blob => {
                    if (blob.size > {{CsvReport.MaxFileBytes}}) throw new Error();
                    slot.file = blob;
                }).catch(() => {}).finally(() => slot.done = true);
                return true;
            })()
            """;
        if (await MobileWebView.EvaluateAsync(download.View, start, token) != "true")
            throw new InvalidOperationException("Die von WebUntis erzeugte CSV kann in diesem Fenster nicht gelesen werden.");
        var test = $"globalThis.{property}?.id === {key} && globalThis.{property}.done ? globalThis.{property}.file?.size ?? -1 : null";
        int size;
        do
        {
            var result = await MobileWebView.EvaluateAsync(download.View, test, token);
            if (int.TryParse(result, CultureInfo.InvariantCulture, out size)) break;
            await Task.Delay(180, token);
        } while (true);
        if (size is < 0 or > CsvReport.MaxFileBytes) throw new InvalidDataException("Die von WebUntis erzeugte CSV konnte nicht eingelesen werden.");
        if (size == 0) return null;

        var bytes = new byte[size];
        const int chunk = 196608;
        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            var index = $"{property}Chunk";
            var begin = $$"""
                (() => {
                    const slot = globalThis.{{property}};
                    if (location.origin !== {{school}} || slot?.id !== {{key}} || !slot.file) return false;
                    const reader = new FileReader();
                    reader.onload = () => slot.{{index}} = String(reader.result).split(',', 2)[1];
                    reader.onerror = () => slot.{{index}} = '';
                    delete slot.{{index}};
                    reader.readAsDataURL(slot.file.slice({{offset}}, {{offset + chunk}}));
                    return true;
                })()
                """;
            if (await MobileWebView.EvaluateAsync(download.View, begin, token) != "true")
                throw new InvalidDataException("Die CSV-Übertragung wurde unterbrochen.");
            string? encoded;
            do
            {
                var result = await MobileWebView.EvaluateAsync(download.View,
                    $"globalThis.{property}?.id === {key} ? globalThis.{property}.{index} ?? null : null", token);
                encoded = JsonSerializer.Deserialize<string>(result, Json);
                if (encoded == null) await Task.Delay(60, token);
            } while (encoded == null);
            if (!Convert.TryFromBase64String(encoded, bytes.AsSpan(offset), out var length) ||
                length != Math.Min(chunk, bytes.Length - offset))
                throw new InvalidDataException("Ein Abschnitt der CSV konnte nicht eingelesen werden.");
        }
        await MobileWebView.EvaluateAsync(download.View, $"if (globalThis.{property}?.id === {key}) delete globalThis.{property};", token);
        return bytes;
    }

    public async Task<byte[]?> FetchExcusedAsync(ReportBatch batch, DateOnly from, DateOnly to, IProgress<string> progress, CancellationToken token)
    {
        var selection = new ReportSelection(batch.ClassId, from, to, ExcusedOnly: true);
        try
        {
            await PrepareAsync(selection, progress, token);
            return await FetchReportAsync(selection, ReportKind.Absences, progress, token);
        }
        catch (Exception error) when (ReportFailure(error))
        {
            reload = true;
            batch.Notes.Add($"Entschuldigte Fehlzeiten · {error.Message}");
            return null;
        }
    }

    public async Task<Report?> ReadAbsenceTimesAsync(string className, ReportBatch batch, ReportRange range, ReportKind kind,
        IProgress<string> progress, CancellationToken token, bool reusePage = false)
    {
        progress.Report(kind == ReportKind.Absences ? "Fehlzeitenliste wird gelesen …" : "Verspätungsliste wird gelesen …");
        var selection = new ReportSelection(batch.ClassId, range.From, range.To);
        var state = await SupplementAsync(AbsenceTimes, "absenceTimes", selection,
            SchoolYears.StartYear(range.From), kind, className, state => state.TimesReady, token, reusePage);
        if (state == null) return null;
        var entries = state.Times.Select(row => row.ToEntry(kind)).ToArray();
        return entries.Any(entry => entry == null) ? null : new Report(kind, entries.Select(entry => entry!).ToArray(), [], []);
    }

    public async Task<AbsenceNote[]> ReadAbsenceNotesAsync(ReportBatch batch, ReportRange range, IProgress<string> progress, CancellationToken token)
    {
        progress.Report("Abwesenheitstexte werden gelesen …");
        var selection = new ReportSelection(batch.ClassId, range.From, range.To);
        var state = await SupplementAsync(Absences, "absenceNotes", selection, SchoolYears.StartYear(range.From),
            ReportKind.Absences, null, state => state.NotesReady, token);
        return state?.AbsenceNotes ?? [];
    }

    public async Task<SchoolPeriod[]> ReadSchoolPeriodsAsync(int year, IProgress<string> progress, CancellationToken token)
    {
        if (schoolPeriods.TryGetValue(year, out var cached)) return cached;
        var state = await SupplementAsync(Timetable, "schoolPeriods", null, year, ReportKind.Timetable, null,
            state => state.Periods.Length > 0, token);
        if (state == null) return [];
        var parsed = state.Periods.Select(row => row.ToPeriod()).ToArray();
        if (parsed.Any(period => period == null)) return [];
        return schoolPeriods[year] = parsed.Select(period => period!).ToArray();
    }

    private async Task<ReportState?> SupplementAsync(string address, string action, ReportSelection? selection, int year,
        ReportKind kind, string? className, Func<ReportState, bool> accept, CancellationToken token, bool reusePage = false)
    {
        await supplements.WaitAsync(token);
        try
        {
            reload = true;
            if (!reusePage || !IsPage(address)) view.Go(address);
            var waiting = Stopwatch.StartNew();
            string? changed = null;
            do
            {
                if (!view.Navigating)
                {
                    var state = await CommandAsync(action, token, selection, kind, waitForFormStamp: changed, year: year, className: className);
                    if (accept(state)) return state;
                    if (state.Stage is "login" or "external" || view.MainStatus == 401)
                        throw new WebUntisSignInRequiredException();
                    if (state.NotesUnavailable || state.Error != null || state.Stage is "denied" or "wrongYear" ||
                        view.MainStatus >= 400) return null;
                    if (state.ChangedField != null) changed = state.FormStamp;
                }
                await Task.Delay(180, token);
            } while (waiting.Elapsed < TimeSpan.FromSeconds(action == "absenceTimes" ? 16 : 8));
            return null;
        }
        catch (Exception error) when (ReportFailure(error)) { return null; }
        finally { supplements.Release(); }
    }

    private async Task<ReportState> CommandAsync(string action, CancellationToken token, ReportSelection? selection = null,
        ReportKind kind = ReportKind.Absences, bool includeClasses = false, bool roster = false, string? exportId = null,
        string? waitForFormStamp = null, int? year = null, string? className = null, bool openBrowserLogin = false,
        AbsencePerson? absencePerson = null, DateOnly? absenceDay = null, AbsenceListEntry? absenceRow = null,
        string? editorId = null, bool classAbsences = false)
    {
        token.ThrowIfCancellationRequested();
        if (!IsSchoolAddress(view.Address))
            return new ReportState { Stage = Uri.TryCreate(view.Address, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps ? "external" : "loading" };

        var request = JsonSerializer.Serialize(new
        {
            action, origin = Origin, classId = selection?.ClassId,
            from = selection?.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = selection?.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            kind = kind.ToString(), excusedOnly = selection?.ExcusedOnly == true, includeClasses, roster, exportId, waitForFormStamp,
            allowLandingAsSignedIn = false, openBrowserLogin,
            targetSchoolYear = year, className, lateOnly = kind == ReportKind.Lateness,
            absencePerson, absenceDay, absenceRow, editorId, classAbsences, mobileEditor = editorId != null
        }, Json);
        var command = $"globalThis.{CommandName}?.({request})";
        var result = await view.EvaluateAsync($"typeof globalThis.{CommandName} === 'function' ? {command} : 'uninitialized'", token);
        if (result == "\"uninitialized\"") result = await view.EvaluateAsync(InstallScript + ";\n" + command, token);
        try { return JsonSerializer.Deserialize<ReportState>(result, Json) ?? new ReportState(); }
        catch (JsonException) when (view.Navigating) { return new ReportState(); }
    }

    private static void Check(ReportState state)
    {
        if (state.Stage is "login" or "external")
            throw new WebUntisSignInRequiredException();
        if (state.Error != null) throw new InvalidOperationException(state.Error);
        if (state.Stage != "reports")
            throw new InvalidOperationException("WebUntis hat das Berichtsformular während des Abrufs verlassen.");
    }

    private bool IsPage(string address) => IsSchoolAddress(view.Address) &&
        Uri.TryCreate(view.Address, UriKind.Absolute, out var active) &&
        active.AbsolutePath.TrimEnd('/').Equals(new Uri(address).AbsolutePath, StringComparison.OrdinalIgnoreCase);

    private static bool ReportFailure(Exception error) =>
        error is InvalidOperationException or TimeoutException or IOException or InvalidDataException or JsonException;
}
