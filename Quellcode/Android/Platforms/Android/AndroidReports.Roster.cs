using System.Diagnostics;
using System.Globalization;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Core.WebUntisReports;

namespace WebUntisAuswertung.Mobile;

internal sealed partial class AndroidReports
{
    private const string MyLessons = Origin + "/my-lessons";

    public async Task<Student[]> ReadClassRosterAsync(Student choice, ReportRange range, IProgress<string> progress,
        CancellationToken token)
    {
        var selection = new ReportSelection(choice.Id, range.From, range.To);
        currentClass = choice;
        allowMatrixSignIn = true;
        try
        {
            await PrepareAsync(selection, progress, token);
            return await ReadRosterAsync(selection, token);
        }
        catch { reload = true; throw; }
        finally
        {
            currentClass = null;
            allowMatrixSignIn = false;
            showLogin(false);
        }
    }

    private async Task<Student[]> ReadRosterAsync(ReportSelection selection, CancellationToken token)
    {
        var roster = await CommandAsync("readRoster", token, selection, roster: true);
        Check(roster);
        if (roster.ClassId != selection.ClassId || !roster.AllStudents || !roster.RangeReady)
            throw new InvalidOperationException("WebUntis hat Klasse, Zeitraum oder Namensliste während des Abrufs geändert.");
        return roster.Students;
    }

    public async Task<LessonEmailBatch> ReadLessonEmailsAsync(ReportRange range, IProgress<string> progress,
        Action<int, int> reportProgress, CancellationToken token)
    {
        var year = SchoolYears.StartYear(range.From);
        var selection = new ReportSelection("", range.From, range.To);
        var emails = new List<RosterEmail>();
        var read = 0;
        var total = 0;
        string? failure = null;
        try
        {
            var reports = await OpenReportsAsync(progress, token);
            if (reports.GlobalYear is { } shown && shown != year.ToString(CultureInfo.InvariantCulture))
                await SelectYearAsync(year, token);
            progress.Report("Mein Unterricht · Berichte des Schuljahres werden gesucht …");
            view.Go(MyLessons);
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
                    var opened = await CommandAsync("openLessonReport", token, selection, year: year, lesson: lesson);
                    Check(opened, "lessonList");
                    if (opened.LessonActionX is not { } x || opened.LessonActionY is not { } y || !await view.TapAsync(x, y, token))
                        throw new InvalidOperationException("WebUntis hat die Unterrichtszeile nicht geöffnet.");
                    await WaitForLessonReportAsync(selection, year, token);
                    var content = await FetchReportAsync(selection, ReportKind.Absences, progress, token, lesson);
                    if (content != null)
                        emails.AddRange(await Task.Run(() => RosterCsv.ReadLessonEmails(content, lesson.Classes, lesson.Group), token));
                    read++;
                }
                catch (Exception error) when (ReportFailure(error) && !view.RendererGone)
                {
                    failure ??= error.Message;
                    progress.Report(label + " konnte nicht gelesen werden.");
                }
                reportProgress(index + 1, total);
                if (index + 1 < total) await WaitForLessonListAsync(selection, year, token);
            }
        }
        catch (Exception error) when (error is WebUntisSignInRequiredException || ReportFailure(error))
        {
            failure ??= error.Message;
        }
        finally { reload = true; showLogin(false); }
        return new LessonEmailBatch(emails.Distinct().ToArray(), read, total, failure);
    }

    private async Task<ReportState> WaitForLessonListAsync(ReportSelection selection, int year, CancellationToken token)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var waiting = Stopwatch.StartNew();
            var back = false;
            var openedPreset = false;
            var pickedPreset = false;
            do
            {
                if (!view.Navigating)
                {
                    var state = await CommandAsync(back ? "lessonList" : "lessonBack", token, selection, year: year);
                    if (state.Stage is "login" or "external" || view.MainStatus == 401) throw new WebUntisSignInRequiredException();
                    if (state.Error != null) throw new InvalidOperationException(state.Error);
                    if (state.Stage == "lessonList" && state.RangeReady) return state;
                    if (state.LessonActionX is { } backX && state.LessonActionY is { } backY)
                        back = await view.TapAsync(backX, backY, token);
                    if (!pickedPreset && state.SchoolYearX is { } x && state.SchoolYearY is { } y &&
                        (state.SchoolYearOption || !openedPreset) && await view.TapAsync(x, y, token))
                    {
                        if (state.SchoolYearOption) pickedPreset = true;
                        else openedPreset = true;
                    }
                }
                await Task.Delay(180, token);
            } while (waiting.Elapsed < TimeSpan.FromSeconds(8));
            if (attempt == 0) view.Go(MyLessons);
        }
        throw new TimeoutException("WebUntis hat «Mein Unterricht» für das gewählte Schuljahr nicht geladen.");
    }

    private async Task WaitForLessonReportAsync(ReportSelection selection, int year, CancellationToken token)
    {
        var waiting = Stopwatch.StartNew();
        do
        {
            if (!view.Navigating)
            {
                var state = await CommandAsync("lessonList", token, selection, year: year);
                if (state.Stage == "lessonReport") return;
                if (state.Stage is "login" or "external" || view.MainStatus == 401) throw new WebUntisSignInRequiredException();
                if (state.Error != null) throw new InvalidOperationException(state.Error);
            }
            await Task.Delay(160, token);
        } while (waiting.Elapsed < TimeSpan.FromSeconds(7));
        throw new TimeoutException("WebUntis hat den Unterrichtsbericht nicht geöffnet.");
    }
}
