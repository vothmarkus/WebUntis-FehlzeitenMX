namespace WebUntisAuswertung.Core;

internal static class ReportCsvReader
{
    public static (Report Report, bool HasAbsenceText) Read(byte[] content, ReportKind kind)
    {
        var csv = CsvReport.Read(content);
        if (csv.Rows.Length == 0) throw new InvalidDataException("Die CSV-Datei ist leer.");
        var layout = csv.Detect(kind);
        var report = csv.Import(kind, layout) with { AllStudentsIncluded = kind is ReportKind.Absences or ReportKind.Lateness };
        return (report, layout.Has(CsvField.AbsenceText));
    }
}
