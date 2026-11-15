using System.Globalization;
using System.Text.Json;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Core.WebUntisReports;

namespace WebUntisAuswertung.Mobile;

internal sealed partial class AndroidReports
{
    private static readonly TimeSpan ExcuseTimeout = TimeSpan.FromSeconds(45);
    private bool closeExcuse;

    public void CloseExcuse() => closeExcuse = true;

    public async Task<AbsenceListEntry[]?> PrepareAbsencesAsync(Student choice, ReportRange range, CancellationToken token)
    {
        // The matrix just verified the class and school year. Persist only a complete, verified list.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            reload = true;
            view.Go(Absences);
            var state = await WaitForAbsencesAsync(choice.Name, new ReportSelection(choice.Id, range.From, range.To),
                deadline.Token, entireClass: true);
            return state.EditableAbsences;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (ReportFailure(error)) { return null; }
    }

    public async Task<AbsenceListEntry[]> ReadAbsenceActionsAsync(AttendanceMatrix matrix, Student student, DateOnly day,
        IProgress<string> progress, CancellationToken token, bool allowSignIn)
    {
        var person = AbsenceActions.RequirePerson(matrix, student);
        await PrepareAbsencePageAsync(matrix, progress, token, allowSignIn);
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

    // WebUntis validates the original form and receives the teacher's own save action.
    public async Task<bool> ShowExcuseAsync(AttendanceMatrix matrix, Student student, DateOnly day, AbsenceListEntry row,
        IProgress<string> progress, CancellationToken token, bool reuseList)
    {
        var person = AbsenceActions.RequirePerson(matrix, student);
        var selection = new ReportSelection(matrix.ClassId, day, day);
        if (!reuseList || view.Navigating || !IsPage(Absences))
            await PrepareAbsencePageAsync(matrix, progress, token, allowSignIn: true);
        progress.Report("Entschuldigungsformular in WebUntis wird geöffnet …");
        var editorId = Guid.NewGuid().ToString("N");
        var year = SchoolYears.StartYear(day);
        var saved = false;
        var closed = false;
        closeExcuse = false;
        view.SetEditing(true);
        view.EditorMessage += OnEditorMessage;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ExcuseTimeout);
            try
            {
                var state = await WaitForAbsencesAsync(matrix.ClassName, selection, deadline.Token, person, day, row, editorId);
                if (!state.AbsenceEditorRequested)
                    throw new InvalidOperationException("WebUntis hat das Entschuldigungsformular nicht geöffnet.");
                while (!closed)
                {
                    var form = await ReadEditorAsync();
                    if (form.AbsenceEditorVisible) break;
                    await Task.Delay(200, deadline.Token);
                }
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                if (!closed) showExcuse(true);
                while (!closed && !closeExcuse)
                {
                    var form = await ReadEditorAsync();
                    if (!closed && !form.AbsenceEditorVisible && !view.Navigating)
                    {
                        // A document replacement can outlive the old WebView's close signal.
                        // Re-read the reports in that case as well.
                        saved = true;
                        break;
                    }
                    if (!closed && !closeExcuse) await Task.Delay(220, token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("WebUntis hat das Entschuldigungsformular nicht rechtzeitig angezeigt.");
            }
            token.ThrowIfCancellationRequested();
            return saved;

            async Task<ReportState> ReadEditorAsync()
            {
                if (view.Navigating) return new ReportState();
                if (closed) return new ReportState();
                var state = await CommandAsync("excuseState", deadline.Token, year: year, editorId: editorId);
                saved |= state.AbsenceEditorSaveRequested;
                closed |= state.AbsenceEditorClosed;
                if (!closed && !saved) CheckAbsencePage(state);
                return state;
            }
        }
        finally
        {
            view.EditorMessage -= OnEditorMessage;
            showExcuse(false);
            if (!view.RendererGone && !token.IsCancellationRequested) view.Go("about:blank");
            view.SetEditing(false);
            reload = true;
        }

        void OnEditorMessage(string json)
        {
            EditorMessage? message;
            try { message = JsonSerializer.Deserialize<EditorMessage>(json, Json); }
            catch (JsonException) { return; }
            if (message?.EditorId != editorId || message.Action is not ("save" or "closed")) return;
            saved |= message.SaveRequested;
            closed |= message.Action == "closed";
        }
    }

    private async Task PrepareAbsencePageAsync(AttendanceMatrix matrix, IProgress<string> progress, CancellationToken token,
        bool allowSignIn)
    {
        var year = SchoolYears.StartYear(matrix.From);
        var state = await OpenReportsAsync(progress, token, allowSignIn: allowSignIn);
        if (state.GlobalYear != year.ToString(CultureInfo.InvariantCulture))
        {
            await SelectYearAsync(year, token);
            await OpenReportsAsync(progress, token, allowSignIn: allowSignIn);
        }
        reload = true;
        view.Go(Absences);
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
                throw new InvalidOperationException("Android WebView wurde beendet. Starte die App erneut.");
            if (!view.Navigating)
            {
                var state = await CommandAsync(action, token, selection, waitForFormStamp: changed, year: year,
                    className: className, absencePerson: person, absenceDay: day, absenceRow: row, editorId: editorId,
                    classAbsences: entireClass);
                CheckAbsencePage(state);
                if (state.AbsenceActionsReady || state.AbsenceEditorRequested) return state;
                if (state.ChangedField != null) changed = state.FormStamp;
            }
            await Task.Delay(200, token);
        }
    }

    private void CheckAbsencePage(ReportState state)
    {
        if (state.Stage is "login" or "external" || view.MainStatus == 401) throw new WebUntisSignInRequiredException();
        if (state.Stage == "denied" || view.MainStatus == 403)
            throw new InvalidOperationException("WebUntis meldet fehlende Berechtigung für die Abwesenheitsliste.");
        if (state.Error is { } error) throw new InvalidOperationException(error);
        if (state.Stage == "wrongYear")
            throw new InvalidOperationException("WebUntis hat das Schuljahr der Abwesenheitsliste nicht bestätigt.");
        if (view.RendererGone || view.MainStatus >= 400)
            throw new InvalidOperationException("Die WebUntis-Abwesenheitsliste ist derzeit nicht erreichbar.");
    }

    private sealed record EditorMessage(string EditorId, string Action, bool SaveRequested);
}
