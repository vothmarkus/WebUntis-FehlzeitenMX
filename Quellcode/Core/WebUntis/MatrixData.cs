namespace WebUntisAuswertung.Core;

internal sealed record MatrixData(string ClassName, string ClassId, ReportRange Range, DateTimeOffset FetchedAt,
    Student[] Students, Report[] Reports, AbsenceNote[] AbsenceNotes, SchoolPeriod[] SchoolPeriods)
{
    public AbsenceListEntry[]? PreparedAbsences { get; init; }

    public AttendanceMatrix Build() => new(ClassName, Range.From, Range.To, Students, Reports,
        absenceNotes: AbsenceNotes, schoolPeriods: SchoolPeriods, classId: ClassId, fetchedAt: FetchedAt)
    {
        PreparedAbsences = PreparedAbsences
    };
}
