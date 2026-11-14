namespace WebUntisAuswertung.Core;

internal interface IReportSupplement
{
    Task<Report?> ReadAbsenceTimesAsync(string className, ReportBatch batch, ReportRange range, ReportKind kind,
        IProgress<string> progress, CancellationToken token, bool reusePage = false);
    Task<byte[]?> FetchExcusedAsync(ReportBatch batch, DateOnly from, DateOnly to,
        IProgress<string> progress, CancellationToken token);
    Task<AbsenceNote[]> ReadAbsenceNotesAsync(ReportBatch batch, ReportRange range, IProgress<string> progress, CancellationToken token);
    Task<SchoolPeriod[]> ReadSchoolPeriodsAsync(int year, IProgress<string> progress, CancellationToken token);
}

internal static class ReportAnalysis
{
    public static async Task<MatrixData> ReadAsync(string name, ReportRange range, ReportBatch batch, IReportSupplement browser,
        IProgress<string> progress, CancellationToken token, Task<AbsenceNote[]?>? preparedNotes = null, bool afterExcuse = false)
    {
        progress.Report("Berichte werden ausgewertet …");
        var reports = batch.EmptyReports.Select(kind => new Report(kind, [], [], [])).ToList();
        var reportHasAbsenceText = false;
        var parsed = await Task.WhenAll(batch.Csvs.Select(csv => Task.Run(() =>
        {
            try
            {
                var (report, hasAbsenceText) = ReportCsvReader.Read(csv.Content, csv.Kind);
                return (csv.Kind, Report: (Report?)report, HasAbsenceText: hasAbsenceText, Error: (string?)null);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return (csv.Kind, Report: (Report?)null, HasAbsenceText: false, Error: (string?)error.Message);
            }
        }, token)));
        batch.Csvs.Clear();
        foreach (var item in parsed)
        {
            if (item.Report != null)
            {
                reports.Add(item.Report);
                if (item.Kind == ReportKind.Absences) reportHasAbsenceText = item.HasAbsenceText;
            }
            else batch.Notes.Add($"{CsvReport.Label(item.Kind)} · {item.Error}");
        }
        (Report Original, Report Details)? absenceEnrichment = null;
        var absenceTimesLoaded = false;
        foreach (var kind in new[] { ReportKind.Absences, ReportKind.Lateness })
        {
            var index = reports.FindIndex(report => report.Kind == kind);
            if (index >= 0 && reports[index].Complete && reports[index].Entries.All(entry =>
                (entry.Period.Length > 0 || entry.Start.HasValue) &&
                (kind != ReportKind.Absences || entry.Excuse != Excuse.Unspecified))) continue;
            var times = await browser.ReadAbsenceTimesAsync(name, batch, range, kind, progress, token, reusePage: absenceTimesLoaded);
            if (times == null) continue;
            absenceTimesLoaded = true;
            if (index < 0) reports.Add(times);
            else if (!reports[index].Complete) reports[index] = times with { ListedStudents = reports[index].ListedStudents };
            else
            {
                if (kind == ReportKind.Absences) absenceEnrichment = (reports[index], times);
                reports[index] = reports[index].WithAbsenceTimes(times);
            }
        }
        if (reports.Count == 0)
            throw new InvalidOperationException("Es konnte kein Bericht eingelesen werden." + Environment.NewLine +
                string.Join(Environment.NewLine, batch.Notes));

        var absenceIndex = reports.FindIndex(report => report.Kind == ReportKind.Absences && report.Complete &&
            report.Entries.Any(entry => entry.Kind == EntryKind.Absent && entry.Excuse == Excuse.Unspecified));
        if (absenceIndex >= 0)
        {
            var csv = await browser.FetchExcusedAsync(batch, range.From, range.To, progress, token);
            token.ThrowIfCancellationRequested();
            if (csv != null)
            {
                try
                {
                    var (filtered, _) = await Task.Run(() => ReportCsvReader.Read(csv, ReportKind.Absences), token);
                    reports[absenceIndex] = absenceEnrichment is { } enrichment
                        ? enrichment.Original.WithExcusedAbsences(filtered).WithAbsenceTimes(enrichment.Details)
                        : reports[absenceIndex].WithExcusedAbsences(filtered);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    batch.Notes.Add($"Entschuldigte Fehlzeiten · {error.Message}");
                }
            }
        }
        token.ThrowIfCancellationRequested();
        var notes = (afterExcuse || !reportHasAbsenceText) && reports.Any(report => report.Entries.Any(entry => entry.Kind == EntryKind.Absent))
            ? ReadNotesAsync() : Task.FromResult<AbsenceNote[]>([]);
        var periods = reports.Any(report => report.Entries.Any(entry => entry.Period.Length == 0 && entry.Start.HasValue))
            ? browser.ReadSchoolPeriodsAsync(SchoolYears.StartYear(range.From), progress, token) : Task.FromResult<SchoolPeriod[]>([]);
        await Task.WhenAll(notes, periods);
        var absenceNotes = await notes;
        var schoolPeriods = await periods;
        token.ThrowIfCancellationRequested();
        return new MatrixData(name, batch.ClassId, range, DateTimeOffset.UtcNow, batch.Students,
            [.. reports, new Report(ReportKind.Timetable, [], [], batch.Notes.ToArray(), Complete: false)], absenceNotes, schoolPeriods);

        async Task<AbsenceNote[]> ReadNotesAsync()
        {
            if (preparedNotes != null && await preparedNotes is { } ready) return ready;
            return await browser.ReadAbsenceNotesAsync(batch, range, progress, token);
        }
    }
}
