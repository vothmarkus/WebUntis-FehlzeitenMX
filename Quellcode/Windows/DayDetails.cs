using System.Globalization;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung;

internal sealed record DayDetailRow(string Period, string Kind, string Subject, string Duration, string Excuse,
    string Reason, string AbsenceText, string ExcuseText);

internal sealed record DayDetails(string Title, DayDetailRow[] Rows, string Message = "")
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    public static DayDetails Empty { get; } = new("Tagesdetails", [], "Wähle eine Tageszelle, um die Einträge zu sehen.");

    public static DayDetails Create(Student student, DateOnly day, DayCell cell, IReadOnlyList<SchoolPeriod>? schoolPeriods = null)
    {
        var title = new List<string> { student.Name, day.ToString("dd.MM.yyyy", German), MarkText(cell.Mark) };
        var absences = cell.Entries.Where(entry => entry.Kind == EntryKind.Absent).ToArray();
        var late = cell.Entries.Where(entry => entry.Kind == EntryKind.Late).ToArray();
        if (absences.Length > 0)
        {
            var units = absences.Select(entry => SchoolUnits(entry, cell.Lessons)).ToArray();
            if (units.All(unit => unit.HasValue) && units.Sum() is > 0 and var hours)
            {
                title.Add(SchoolHours(hours));
                var partial = absences.Where((_, index) => units[index] == 0).ToArray();
                if (partial.All(entry => entry.Minutes.HasValue) && partial.Sum(entry => entry.Minutes) is > 0 and var extraMinutes)
                    title.Add(Minutes(extraMinutes) + " weitere Fehlzeit");
            }
            else if (absences.All(entry => entry.Minutes.HasValue))
                title.Add(Minutes(absences.Sum(entry => entry.Minutes!.Value)) + " Fehlzeit");
        }
        if (late.Length > 0 && late.All(entry => entry.Minutes.HasValue))
            title.Add(Minutes(late.Sum(entry => entry.Minutes!.Value)) + " Verspätung");

        var rows = cell.Entries.OrderBy(entry => entry.Start ?? TimeOnly.MaxValue)
            .Select(entry => MakeRow(entry, cell.Lessons, schoolPeriods ?? [])).ToList();
        foreach (var note in cell.AbsenceNotes ?? [])
        {
            var reason = rows.Any(row => row.Reason == note.Reason) ? "" : note.Reason;
            var absenceText = rows.Any(row => row.AbsenceText == note.Text) ? "" : note.Text;
            var excuseText = rows.Any(row => row.ExcuseText == note.ExcuseText) ? "" : note.ExcuseText;
            if (reason.Length + absenceText.Length + excuseText.Length > 0)
                rows.Add(new DayDetailRow("", "Abwesenheitsliste", "", "", "", Compact(reason), Compact(absenceText), Compact(excuseText)));
        }

        var message = cell.Mark == DayMark.Unknown && cell.UnknownReason.Length > 0 ? cell.UnknownReason : rows.Count > 0 ? "" : cell.Mark switch
        {
            DayMark.Present => "WebUntis weist alle Unterrichtsstunden als kontrolliert aus.",
            DayMark.NoAbsenceRecorded => "Der vollständige Fehlzeitenbericht enthält für diese Person an diesem Tag keinen Eintrag.",
            _ => "Für diesen Tag liegt keine vollständige Anwesenheitsbestätigung vor."
        };
        return new DayDetails(string.Join("   ·   ", title), rows.ToArray(), message);
    }

    private static DayDetailRow MakeRow(AttendanceEntry entry, Lesson[] lessons, IReadOnlyList<SchoolPeriod> schoolPeriods)
    {
        var kind = entry.Kind switch
        {
            EntryKind.Absent => "Abwesenheit", EntryKind.Late => "Verspätung", EntryKind.Present => "Anwesenheit",
            EntryKind.Exempt => "Befreit", _ => "Ungeklärt"
        };
        var units = entry.Kind == EntryKind.Absent ? SchoolUnits(entry, lessons) : null;
        var duration = units is > 0 and var hours ? SchoolHours(hours) : "";
        if (entry.Minutes is > 0 and var minutes)
            duration = duration.Length == 0 ? Minutes(minutes) : $"{duration} ({minutes.ToString(German)}′)";
        if (duration.Length == 0 && entry.Start is { } start)
            duration = entry.End is { } end ? $"{start:HH:mm}–{end:HH:mm}" : $"{start:HH:mm}";

        var excuse = entry.Excuse switch
        {
            Excuse.Open => "Offen", Excuse.Excused => "Entschuldigt", Excuse.Unexcused => "Unentschuldigt",
            _ => entry.Kind == EntryKind.Absent ? "Nicht ausgewiesen" : ""
        };
        if (!entry.Counts)
            excuse = excuse.Length == 0 ? "Nicht zählend" : excuse + " · Nicht zählend";
        return new DayDetailRow(PeriodLabel(entry, lessons, schoolPeriods), kind, Compact(entry.Subject), duration, excuse,
            Compact(entry.Reason), Compact(entry.AbsenceText), Compact(entry.ExcuseText));
    }

    private static string Compact(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string PeriodLabel(AttendanceEntry entry, Lesson[] lessons, IReadOnlyList<SchoolPeriod> schoolPeriods)
    {
        if (entry.Period.Length > 0) return Compact(entry.Period);
        if (entry.Start is not { } start) return "";
        var matching = lessons.Where(lesson => lesson.Period.Length > 0 && lesson.Start is { } from && lesson.End is { } to &&
            (entry.End is { } end && end > start ? from < end && start < to : from <= start && start < to) &&
            (entry.Subject.Length == 0 || lesson.Subject.Length == 0 ||
                AttendanceMatrix.Fold(entry.Subject) == AttendanceMatrix.Fold(lesson.Subject)))
            .OrderBy(lesson => lesson.Start).Select(lesson => lesson.Period).Distinct().ToArray();
        if (matching.Length > 0) return string.Join(" / ", matching);
        return string.Join(" / ", schoolPeriods.Where(period =>
            entry.End is { } end && end > start ? period.Start < end && start < period.End : period.Start <= start && start < period.End)
            .Select(period => period.Number.ToString(German)));
    }

    private static string Minutes(int minutes) => $"{minutes.ToString(German)} {(minutes == 1 ? "Minute" : "Minuten")}";

    private static string SchoolHours(decimal hours) =>
        $"{hours.ToString("0.##", German)} {(hours == 1 ? "Schulstunde" : "Schulstunden")}";

    private static decimal? SchoolUnits(AttendanceEntry entry, Lesson[] lessons)
    {
        if (entry.Units is { } units) return units;
        if (entry.Start is not { } start || entry.End is not { } end || end <= start) return null;
        if (entry.Minutes is { } minutes && minutes != (int)(end - start).TotalMinutes) return null;
        return lessons.Any(lesson => lesson.Start == start && lesson.End == end &&
            (entry.Period.Length == 0 || lesson.Period.Length == 0 || entry.Period == lesson.Period) &&
            (entry.Subject.Length == 0 || lesson.Subject.Length == 0 ||
                AttendanceMatrix.Fold(entry.Subject) == AttendanceMatrix.Fold(lesson.Subject))) ? 1 : null;
    }

    public static string MarkText(DayMark mark) => mark switch
    {
        DayMark.Present => "Anwesend", DayMark.NoAbsenceRecorded => "Kein Fehlzeiteintrag",
        DayMark.Absent => "Ganz oder teilweise abwesend", DayMark.ExcusedAbsence => "Entschuldigte Fehlzeit",
        DayMark.ExcusedAbsenceAndLate => "Entschuldigte Fehlzeit und Verspätung",
        DayMark.Late => "Verspätet", DayMark.AbsentAndLate => "Abwesenheit und Verspätung",
        DayMark.Exempt => "Befreit", _ => "Anwesenheit ungeklärt"
    };
}
