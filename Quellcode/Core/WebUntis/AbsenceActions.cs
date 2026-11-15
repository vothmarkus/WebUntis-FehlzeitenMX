namespace WebUntisAuswertung.Core;

internal static class AbsenceActions
{
    public static AbsencePerson? Person(AttendanceMatrix matrix, Student student)
    {
        var id = student.Id.Length > 0 && student.Id.All(char.IsAsciiDigit) ? student.Id : "";
        var name = NameKey(student.Name);
        return id.Length > 0 || matrix.Students.Count(candidate => NameKey(candidate.Name) == name) == 1
            ? new AbsencePerson(id, student.Name) : null;
    }

    public static AbsencePerson RequirePerson(AttendanceMatrix matrix, Student student) => Person(matrix, student) ??
        throw new InvalidOperationException("Für diese Person gibt es keine eindeutige WebUntis-Schülerkennung.");

    public static AbsenceListEntry[]? ForDay(AbsenceListEntry[] rows, AbsencePerson person, DateOnly day)
    {
        var name = NameKey(person.Name);
        var matching = rows.Where(row => row.From <= day && day <= row.To && NameKey(row.StudentName) == name).ToArray();
        return matching.Any(row => row.StudentId.Length == 0) ? null :
            matching.Where(row => person.Id.Length == 0 || row.StudentId == person.Id).ToArray();
    }

    public static AbsenceListEntry[] Available(IEnumerable<AbsenceListEntry> rows) => rows.Where(row => row.CanExcuse)
        .OrderBy(row => row.From).ThenBy(row => CsvReport.ReadTime(row.Start)).ThenBy(row => row.To)
        .ThenBy(row => CsvReport.ReadTime(row.End)).ToArray();

    public static string Period(AbsenceListEntry row)
    {
        var time = row.From == row.To ? $"{row.From:dd.MM.} · {row.Start}–{row.End}" :
            $"{row.From:dd.MM.} {row.Start} – {row.To:dd.MM.} {row.End}";
        return row.Status.Length > 0 ? $"{time} · {row.Status}" : time;
    }

    private static string NameKey(string name) => string.Join(' ', name.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).Normalize().ToLowerInvariant();
}
