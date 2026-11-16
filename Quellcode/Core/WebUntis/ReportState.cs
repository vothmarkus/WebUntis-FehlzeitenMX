using System.Globalization;

namespace WebUntisAuswertung.Core;

internal sealed record ReportSelection(string ClassId, DateOnly From, DateOnly To, bool ExcusedOnly = false);
internal sealed record AbsencePerson(string Id, string Name);

internal sealed record LessonChoice(string Key, string[] Classes, string Group, string Subject);
internal sealed record PeriodRow(int Number, string Start, string End)
{
    public SchoolPeriod? ToPeriod() => Number > 0 && CsvReport.ReadTime(Start) is { } from &&
        CsvReport.ReadTime(End) is { } to && from < to ? new SchoolPeriod(Number, from, to) : null;
}

internal sealed class ReportState
{
    public string Stage { get; set; } = "";
    public string? Error { get; set; }
    public string? ClassId { get; set; }
    public bool AtReports { get; set; }
    public double? BrowserLoginX { get; set; }
    public double? BrowserLoginY { get; set; }
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
    public double? LessonActionX { get; set; }
    public double? LessonActionY { get; set; }
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
    public bool AbsenceEditorClosed { get; set; }
    public bool AbsenceEditorSaveRequested { get; set; }
    public int EditorWidth { get; set; }
    public int EditorHeight { get; set; }
    public bool TimesReady { get; set; }
    public AbsenceTimeRow[] Times { get; set; } = [];
    public PeriodRow[] Periods { get; set; } = [];
    public LessonChoice[] Lessons { get; set; } = [];
    public Student[] Classes { get; set; } = [];
    public Student[] Students { get; set; } = [];
}

internal sealed record AbsenceTimeRow(string Name, string Day, string Subject, string Start, string End, string Units,
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

internal sealed record AbsenceListEntry(string Id, string StudentId, string StudentName, DateOnly From, DateOnly To,
    string Start, string End, string Status, string Reason, bool CanExcuse);

internal sealed record ReportCsv(ReportKind Kind, byte[] Content);

internal sealed class ReportBatch(Student[] students, string classId)
{
    public string ClassId { get; } = classId;
    public Student[] Students { get; } = students;
    public List<ReportCsv> Csvs { get; } = [];
    public List<ReportKind> EmptyReports { get; } = [];
    public List<string> Notes { get; } = [];

}
