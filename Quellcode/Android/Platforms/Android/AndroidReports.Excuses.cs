using System.Globalization;
using System.Text.Json;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Core.WebUntisReports;

namespace WebUntisAuswertung.Mobile;

internal sealed partial class AndroidReports
{
    private static readonly TimeSpan ExcuseTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PreparedListLifetime = TimeSpan.FromMinutes(5);
    private PreparedAbsencePage? preparedAbsences;
    private bool closeExcuse;

    public void CloseExcuse() => closeExcuse = true;

    public bool HasPreparedAbsences(AttendanceMatrix matrix) =>
        PreparedPage is { } page && page.Selection == new ReportSelection(matrix.ClassId, matrix.From, matrix.To);

    public AbsenceListEntry[]? PreparedAbsenceActions(AttendanceMatrix matrix, Student student, DateOnly day) =>
        MatchingPreparedPage(matrix, day) is { } page && AbsenceActions.Person(matrix, student) is { } person
            ? AbsenceActions.ForDay(page.Rows, person, day) : null;

    public async Task<AbsenceListEntry[]?> PrepareAbsencesAsync(Student choice, ReportRange range, CancellationToken token)
    {
        var selection = new ReportSelection(choice.Id, range.From, range.To);
        if (PreparedPage is { } existing && existing.Selection == selection) return existing.Rows;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            OpenAbsenceList();
            preparedAbsences = null;
            var state = await WaitForAbsencesAsync(choice.Name, selection, deadline.Token, entireClass: true);
            preparedAbsences = new PreparedAbsencePage(selection, state.EditableAbsences, view.NavigationRevision, DateTimeOffset.UtcNow);
            return state.EditableAbsences;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is WebUntisSignInRequiredException || ReportFailure(error)) { return null; }
    }

    public async Task<AbsenceListEntry[]> ReadAbsenceActionsAsync(AttendanceMatrix matrix, Student student, DateOnly day,
        IProgress<string> progress, CancellationToken token)
    {
        if (PreparedAbsenceActions(matrix, student, day) is { Length: > 0 } ready) return ready;

        var person = AbsenceActions.RequirePerson(matrix, student);
        var year = SchoolYears.StartYear(matrix.From);
        return await WithAbsenceListAsync(year, progress, token, LookupAsync);

        async Task<AbsenceListEntry[]> LookupAsync()
        {
            preparedAbsences = null;
            progress.Report("Passende WebUntis-Abwesenheiten werden gesucht …");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ExcuseTimeout);
            try
            {
                var state = await WaitForAbsencesAsync(matrix.ClassName, new ReportSelection(matrix.ClassId, day, day),
                    deadline.Token, person, day);
                return state.EditableAbsences;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("WebUntis hat die Abwesenheitsliste nicht bestätigt. Versuche es erneut.");
            }
        }
    }

    // WebUntis validates the original form and receives the teacher's own save action.
    public async Task<bool> ShowExcuseAsync(AttendanceMatrix matrix, Student student, DateOnly day, AbsenceListEntry row,
        IProgress<string> progress, CancellationToken token)
    {
        var person = AbsenceActions.RequirePerson(matrix, student);
        var editorId = Guid.NewGuid().ToString("N");
        var year = SchoolYears.StartYear(matrix.From);
        var refresh = false;
        var closed = false;
        closeExcuse = false;
        view.EditorMessage += OnEditorMessage;
        try
        {
            // Recover sign-in while opening the form, before the teacher can enter or save anything.
            await WithAbsenceListAsync(year, progress, token, OpenEditorAsync);

            if (!closed && !closeExcuse)
            {
                view.SetEditing(true);
                var fitted = await view.FitEditorOnOpenAsync(token);
                token.ThrowIfCancellationRequested();
                var form = await ReadEditorAsync(token);
                if (!closed && !closeExcuse)
                {
                    if (!form.AbsenceEditorVisible)
                        throw new InvalidOperationException("Das Entschuldigungsformular ist nicht mehr geöffnet. Öffne es erneut.");
                    if (!fitted) throw new InvalidOperationException("Das Entschuldigungsformular konnte nicht an die Bildschirmbreite angepasst werden.");
                    await showExcuse(true);
                }
            }
            while (!closed && !closeExcuse)
            {
                var form = await ReadEditorAsync(token);
                if (!closed && !form.AbsenceEditorVisible && !view.Navigating)
                {
                    // A document replacement can outlive the old WebView's close signal.
                    refresh = true;
                    break;
                }
                if (!closed && !closeExcuse) await Task.Delay(220, token);
            }
            token.ThrowIfCancellationRequested();
            return refresh;
        }
        finally
        {
            view.EditorMessage -= OnEditorMessage;
            try { await showExcuse(false); }
            finally
            {
                view.SetEditing(false);
                preparedAbsences = null;
                reload = true;
                if (!view.RendererGone && !token.IsCancellationRequested) view.Go("about:blank");
            }
        }

        async Task<bool> OpenEditorAsync()
        {
            var page = MatchingPreparedPage(matrix, day);
            var dayRows = page == null ? null : AbsenceActions.ForDay(page.Rows, person, day);
            var entireClass = dayRows?.Any(candidate => candidate.Id == row.Id && candidate.CanExcuse) == true;
            var selection = entireClass ? page!.Selection : new ReportSelection(matrix.ClassId, day, day);
            preparedAbsences = null;

            progress.Report("Entschuldigungsformular in WebUntis wird geöffnet …");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ExcuseTimeout);
            try
            {
                var state = await WaitForAbsencesAsync(matrix.ClassName, selection, deadline.Token, person, day, row, editorId, entireClass);
                if (!state.AbsenceEditorRequested)
                    throw new InvalidOperationException("WebUntis hat das Entschuldigungsformular nicht geöffnet.");
                while (!closed)
                {
                    var form = await ReadEditorAsync(deadline.Token);
                    if (form.AbsenceEditorVisible) return true;
                    await Task.Delay(200, deadline.Token);
                }
                return false;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("WebUntis hat das Entschuldigungsformular nicht rechtzeitig angezeigt.");
            }
        }

        async Task<ReportState> ReadEditorAsync(CancellationToken readingToken)
        {
            if (view.Navigating || closed) return new ReportState();
            var state = await CommandAsync("excuseState", readingToken, year: year, editorId: editorId);
            refresh |= state.AbsenceEditorSaveRequested;
            closed |= state.AbsenceEditorClosed;
            if (!closed && !refresh) CheckAbsencePage(state);
            return state;
        }

        void OnEditorMessage(string json)
        {
            EditorMessage? message;
            try { message = JsonSerializer.Deserialize<EditorMessage>(json, Json); }
            catch (JsonException) { return; }
            if (message?.EditorId != editorId || message.Action is not ("save" or "closed")) return;
            refresh |= message.SaveRequested;
            closed |= message.Action == "closed";
        }
    }

    private PreparedAbsencePage? PreparedPage => preparedAbsences is { } page && IsPage(Absences) && !view.Navigating &&
        !view.RendererGone && view.MainStatus < 400 && view.NavigationRevision == page.NavigationRevision &&
        DateTimeOffset.UtcNow - page.LoadedAt < PreparedListLifetime ? page : null;

    private PreparedAbsencePage? MatchingPreparedPage(AttendanceMatrix matrix, DateOnly day) =>
        PreparedPage is { } page && page.Selection.ClassId == matrix.ClassId &&
        page.Selection.From <= day && day <= page.Selection.To ? page : null;

    private void OpenAbsenceList()
    {
        reload = true;
        if (IsPage(Absences) && !view.Navigating && view.MainStatus < 400) return;
        preparedAbsences = null;
        view.Go(Absences);
    }

    private async Task<T> WithAbsenceListAsync<T>(int year, IProgress<string> progress, CancellationToken token, Func<Task<T>> action)
    {
        token.ThrowIfCancellationRequested();
        OpenAbsenceList();
        try { return await action(); }
        catch (WebUntisSignInRequiredException) { await ConfirmAbsenceYearAsync(year, progress, token, startWithLogin: true); }
        catch (AbsenceSchoolYearException) { await ConfirmAbsenceYearAsync(year, progress, token, startWithLogin: false); }
        token.ThrowIfCancellationRequested();
        OpenAbsenceList();
        return await action();
    }

    private async Task ConfirmAbsenceYearAsync(int year, IProgress<string> progress, CancellationToken token, bool startWithLogin)
    {
        var state = await OpenReportsAsync(progress, token, startWithLogin: startWithLogin);
        if (state.GlobalYear != year.ToString(CultureInfo.InvariantCulture))
        {
            await SelectYearAsync(year, token);
            await OpenReportsAsync(progress, token);
        }
    }

    private async Task<ReportState> WaitForAbsencesAsync(string className, ReportSelection selection, CancellationToken token,
        AbsencePerson? person = null, DateOnly? day = null, AbsenceListEntry? row = null, string? editorId = null,
        bool entireClass = false)
    {
        var action = row == null ? "absenceActions" : "openExcuse";
        var year = SchoolYears.StartYear(selection.From);
        string? changed = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (view.RendererGone)
                throw new InvalidOperationException(MobileWebView.RendererMessage);
            if (!view.Navigating)
            {
                var state = await CommandAsync(action, token, selection, waitForFormStamp: changed, year: year,
                    className: className, absencePerson: person, absenceDay: day, absenceRow: row, editorId: editorId,
                    classAbsences: entireClass, openBrowserLogin: !IsPage(Absences));
                CheckAbsencePage(state);
                if (state.AbsenceActionsReady || state.AbsenceEditorRequested)
                {
                    await sessionConfirmed(token);
                    return state;
                }
                if (state.ChangedField != null) changed = state.FormStamp;
            }
            await Task.Delay(200, token);
        }
    }

    private void CheckAbsencePage(ReportState state)
    {
        if (state.Stage is "login" or "external" or "browserWelcome" || view.MainStatus == 401)
            throw new WebUntisSignInRequiredException();
        if (state.Stage == "denied" || view.MainStatus == 403)
            throw new InvalidOperationException("WebUntis meldet fehlende Berechtigung für die Abwesenheitsliste.");
        if (state.Error is { } error) throw new InvalidOperationException(error);
        if (state.Stage == "wrongYear") throw new AbsenceSchoolYearException();
        if (view.RendererGone || view.MainStatus >= 400)
            throw new InvalidOperationException("Die WebUntis-Abwesenheitsliste ist derzeit nicht erreichbar.");
    }

    private sealed record PreparedAbsencePage(ReportSelection Selection, AbsenceListEntry[] Rows,
        long NavigationRevision, DateTimeOffset LoadedAt);
    private sealed class AbsenceSchoolYearException()
        : InvalidOperationException("WebUntis hat das Schuljahr der Abwesenheitsliste nicht bestätigt.");
    private sealed record EditorMessage(string EditorId, string Action, bool SaveRequested);
}
