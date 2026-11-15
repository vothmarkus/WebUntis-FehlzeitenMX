using System.Text;
using WebUntisAuswertung.Core;
using Xunit;

namespace WebUntisAuswertung.Tests;

public sealed class ReportAnalysisTests
{
    private static readonly DateOnly Day = new(2026, 9, 8);
    private static readonly ReportRange Range = new(Day, Day);
    private static readonly Student Alex = new("101", "Alex Beispiel");
    private static readonly Student Sam = new("102", "Sam Muster");

    [Fact]
    public async Task CombinesCsvReportsIntoDailyMarksAndDetails()
    {
        var batch = new ReportBatch([Alex, Sam], "202");
        batch.Csvs.Add(new ReportCsv(ReportKind.Absences, Encoding.Latin1.GetBytes("""
            Schüler ID;Name;Datum;Stunde;Fach;Entschuldigungsstatus;Fehlstunden;Abwesenheitstext
            101;Alex Beispiel;08.09.2026;1;MAT;entschuldigt;1;Ärztlicher Termin
            """)));
        batch.Csvs.Add(new ReportCsv(ReportKind.Lateness, Encoding.UTF8.GetBytes("""
            Schüler ID;Name;Datum;Stunde;Fach;Verspätung in Minuten
            101;Alex Beispiel;08.09.2026;2;INF;5
            """)));
        batch.Csvs.Add(new ReportCsv(ReportKind.Timetable, Encoding.UTF8.GetBytes("""
            Datum;Stunde;Fach;Kontrolliert
            08.09.2026;1;MAT;Ja
            08.09.2026;2;INF;Ja
            """)));

        var matrix = await AnalyzeAsync(batch);

        Assert.True(matrix.HasCompleteReports);
        Assert.Equal(Day, Assert.Single(matrix.Days));
        Assert.Equal(new[] { Alex, Sam }, matrix.Students);
        Assert.Equal(DayMark.Present, matrix.Cells[(Sam.Id, Day)].Mark);
        var cell = matrix.Cells[(Alex.Id, Day)];
        Assert.Equal(DayMark.ExcusedAbsenceAndLate, cell.Mark);
        var details = DayDetails.Create(Alex, Day, cell);
        Assert.Contains("5 Minuten Verspätung", details.Title);
        Assert.Contains(details.Rows, row => row.Excuse == "Entschuldigt" && row.AbsenceText == "Ärztlicher Termin");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DistinguishesAConfirmedEmptyTimetableFromAMissingReport(bool timetableReturned)
    {
        var batch = new ReportBatch([Alex], "202");
        batch.EmptyReports.AddRange([ReportKind.Absences, ReportKind.Lateness]);
        if (timetableReturned) batch.EmptyReports.Add(ReportKind.Timetable);

        var matrix = await AnalyzeAsync(batch);

        Assert.Empty(matrix.Days);
        Assert.False(matrix.HasTimetable);
        Assert.Equal(timetableReturned, matrix.HasCompleteReports);
        Assert.Equal(timetableReturned, matrix.Notes.Length == 0);
    }

    private static async Task<AttendanceMatrix> AnalyzeAsync(ReportBatch batch)
    {
        var data = await ReportAnalysis.ReadAsync("Testklasse", Range, batch, new UnexpectedSupplement(),
            new Progress<string>(), CancellationToken.None);
        return data.Build();
    }

    private sealed class UnexpectedSupplement : IReportSupplement
    {
        public Task<Report?> ReadAbsenceTimesAsync(string className, ReportBatch batch, ReportRange range, ReportKind kind,
            IProgress<string> progress, CancellationToken token, bool reusePage = false) => throw Unexpected();

        public Task<byte[]?> FetchExcusedAsync(ReportBatch batch, DateOnly from, DateOnly to,
            IProgress<string> progress, CancellationToken token) => throw Unexpected();

        public Task<AbsenceNote[]> ReadAbsenceNotesAsync(ReportBatch batch, ReportRange range,
            IProgress<string> progress, CancellationToken token) => throw Unexpected();

        public Task<SchoolPeriod[]> ReadSchoolPeriodsAsync(int year, IProgress<string> progress, CancellationToken token) => throw Unexpected();

        private static InvalidOperationException Unexpected() => new("Die Testberichte enthalten bereits alle Angaben.");
    }
}
