using System.Globalization;
using System.Text;

namespace WebUntisAuswertung.Core;

public enum ReportKind { Absences, Lateness, Presence, Timetable }
public enum EntryKind { Absent, Late, Present, Exempt, Unknown }
public enum Excuse { Unspecified, Open, Excused, Unexcused }
public enum DayMark
{
    Unknown, Present, Absent, Late, AbsentAndLate, Exempt, NoAbsenceRecorded, ExcusedAbsence, ExcusedAbsenceAndLate
}

public sealed record Student(string Id, string Name, string Email = "");
public sealed record AttendanceEntry(
    string Name, string StudentId, DateOnly Day, EntryKind Kind, Excuse Excuse,
    TimeOnly? Start = null, TimeOnly? End = null, string Period = "", string Subject = "",
    int? Minutes = null, decimal? Units = null, bool Counts = true, bool WholeDay = false,
    string Reason = "", string AbsenceText = "", string ExcuseText = "");
public sealed record Lesson(DateOnly Day, TimeOnly? Start = null, TimeOnly? End = null,
    string Period = "", string Subject = "", bool? Checked = null);
public sealed record SchoolPeriod(int Number, TimeOnly Start, TimeOnly End);
public sealed record AbsenceNote(string Name, DateOnly From, DateOnly To, string Reason, string Text, string ExcuseText);
public sealed record Report(ReportKind Kind, AttendanceEntry[] Entries, Lesson[] Lessons,
    string[] Notes, bool Complete = true, Student[]? ListedStudents = null, bool AllStudentsIncluded = false)
{
    public Report WithAbsenceTimes(Report times)
    {
        if (!times.Complete || times.Kind != Kind || Kind is not (ReportKind.Absences or ReportKind.Lateness)) return this;
        static (string Person, DateOnly Day, string Subject, EntryKind Kind) Key(AttendanceEntry entry) =>
            (AttendanceMatrix.NameParts(entry.Name), entry.Day, AttendanceMatrix.Fold(entry.Subject), entry.Kind);
        static bool Matches(AttendanceEntry entry, AttendanceEntry row) =>
            (entry.Start == null || row.Start == entry.Start) && (entry.End == null || row.End == entry.End) &&
            (entry.Minutes == null || entry.Minutes == row.Minutes) && (entry.Units == null || entry.Units == row.Units) &&
            (entry.Excuse == Excuse.Unspecified || row.Excuse == Excuse.Unspecified || entry.Excuse == row.Excuse);
        static AttendanceEntry AddDetails(AttendanceEntry entry, AttendanceEntry row) => entry with
        {
            Start = entry.Start ?? row.Start, End = entry.End ?? row.End,
            Excuse = entry.Excuse == Excuse.Unspecified ? row.Excuse : entry.Excuse,
            Minutes = entry.Minutes ?? row.Minutes, Units = entry.Units ?? row.Units,
            Reason = entry.Reason.Length == 0 ? row.Reason : entry.Reason,
            AbsenceText = entry.AbsenceText.Length == 0 ? row.AbsenceText : entry.AbsenceText
        };

        var matching = times.Entries.ToLookup(Key);
        var enriched = Entries.ToArray();
        foreach (var group in Entries.Select((entry, index) => (Entry: entry, Index: index)).GroupBy(item => Key(item.Entry)))
        {
            var candidates = matching[group.Key].ToList();
            if (candidates.Count != group.Count()) continue;
            var unresolved = new List<(AttendanceEntry Entry, int Index)>();
            foreach (var item in group)
            {
                var usable = candidates.Where(row => Matches(item.Entry, row)).ToArray();
                if (usable.Length == 0 || usable.Any(row => row != usable[0])) unresolved.Add(item);
                else
                {
                    enriched[item.Index] = AddDetails(item.Entry, usable[0]);
                    candidates.Remove(usable[0]);
                }
            }
            // Identical CSV rows can represent distinct hours in the detailed table.
            if (unresolved.Count > 0 && unresolved.All(item => item.Entry == unresolved[0].Entry) &&
                candidates.All(row => Matches(unresolved[0].Entry, row)))
            {
                var ordered = candidates.OrderBy(row => row.Start).ThenBy(row => row.End).ToArray();
                for (var i = 0; i < unresolved.Count; i++)
                    enriched[unresolved[i].Index] = AddDetails(unresolved[i].Entry, ordered[i]);
            }
        }
        return this with { Entries = enriched };
    }

    public Report WithExcusedAbsences(Report filtered)
    {
        if (Kind != ReportKind.Absences || filtered.Kind != ReportKind.Absences || !Complete || !filtered.Complete)
            throw new InvalidDataException("Hauptbericht und gefilterter Fehlzeitenbericht müssen vollständig vorliegen.");

        static AttendanceEntry Key(AttendanceEntry entry) => entry with
        {
            Excuse = Excuse.Unspecified, Reason = "", AbsenceText = "", ExcuseText = ""
        };
        var originals = Entries.Where(entry => entry.Kind == EntryKind.Absent).ToLookup(Key);
        var excused = filtered.Entries.Where(entry => entry.Kind == EntryKind.Absent).ToArray();
        if (excused.Any(entry => entry.Excuse is Excuse.Open or Excuse.Unexcused))
            throw new InvalidDataException("Der gefilterte Bericht enthält offene oder unentschuldigte Fehlzeiten.");

        var confirmed = new HashSet<AttendanceEntry>();
        foreach (var group in excused.GroupBy(Key))
        {
            var matches = originals[group.Key];
            var count = group.Count();
            if (count > matches.Count(entry => entry.Excuse is Excuse.Excused or Excuse.Unspecified))
                throw new InvalidDataException("Der gefilterte Fehlzeitenbericht stimmt nicht mit dem Hauptbericht überein.");

            // Repeated rows can represent several lessons. A partial match cannot identify which of them was excused.
            if (count == matches.Count()) confirmed.Add(group.Key);
        }
        return this with
        {
            Entries = Entries.Select(entry => entry.Kind == EntryKind.Absent && entry.Excuse == Excuse.Unspecified &&
                confirmed.Contains(Key(entry)) ? entry with { Excuse = Excuse.Excused } : entry).ToArray()
        };
    }
}
public sealed record DayCell(DayMark Mark, AttendanceEntry[] Entries, Lesson[] Lessons, AbsenceNote[]? AbsenceNotes = null,
    string UnknownReason = "");
public sealed record Alert(string StudentId, string Name, string Label, string Detail, DateOnly Day);

public sealed class AttendanceMatrix
{
    public string ClassName { get; }
    public string ClassId { get; }
    public DateOnly From { get; }
    public DateOnly To { get; }
    public Student[] Students { get; }
    public DateOnly[] Days { get; }
    public string[] Notes { get; }
    public bool HasTimetable { get; }
    public bool HasAbsenceReport { get; }
    public bool HasLatenessReport { get; }
    internal bool HasCompleteReports { get; }
    internal AbsenceListEntry[]? PreparedAbsences { get; set; }
    public bool IsDemo { get; }
    public DateTimeOffset? FetchedAt { get; }
    public IReadOnlyList<SchoolPeriod> SchoolPeriods { get; }
    public Dictionary<(string Id, DateOnly Day), DayCell> Cells { get; } = new();

    public AttendanceMatrix(string className, DateOnly from, DateOnly to,
        IEnumerable<Student> roster, IEnumerable<Report> sources, bool isDemo = false, IEnumerable<AbsenceNote>? absenceNotes = null,
        IReadOnlyList<SchoolPeriod>? schoolPeriods = null, string classId = "", DateTimeOffset? fetchedAt = null)
    {
        ClassName = className;
        ClassId = classId;
        From = from;
        To = to;
        IsDemo = isDemo;
        FetchedAt = fetchedAt;
        SchoolPeriods = schoolPeriods ?? [];
        var reports = sources.ToArray();
        var notes = reports.SelectMany(report => report.Notes).ToList();
        var students = roster.DistinctBy(student => student.Id).ToList();
        var rosterStudents = students.Select(student => student.Id).ToHashSet();
        var coveredStudents = new HashSet<string>(rosterStudents);
        var entries = new Dictionary<(string Id, DateOnly Day), List<AttendanceEntry>>();
        var unresolvedCells = new HashSet<(string Id, DateOnly Day)>();
        var unresolvedDays = new HashSet<DateOnly>();
        var lessons = reports.SelectMany(report => report.Lessons)
            .Where(lesson => lesson.Day >= from && lesson.Day <= to)
            .Distinct().GroupBy(lesson => lesson.Day).ToDictionary(group => group.Key, group => group.ToArray());
        HasAbsenceReport = reports.Any(report => report.Kind == ReportKind.Absences && report.Complete);
        HasLatenessReport = reports.Any(report => report.Kind == ReportKind.Lateness && report.Complete);
        foreach (var report in reports)
        {
            foreach (var person in report.ListedStudents ?? [])
            {
                var known = students.FirstOrDefault(student => student.Id == person.Id || NameParts(student.Name) == NameParts(person.Name));
                if (known == null) students.Add(known = person);
                if (report.Complete && report.AllStudentsIncluded) coveredStudents.Add(known.Id);
            }
        }
        var timetables = reports.Where(report => report.Kind == ReportKind.Timetable && report.Complete).ToArray();
        HasTimetable = lessons.Count > 0 && timetables.Length > 0;
        var emptyTimetable = timetables.Any(report => report.Lessons.Length == 0);
        HasCompleteReports = HasAbsenceReport && HasLatenessReport && (HasTimetable || emptyTimetable);
        var unmatched = 0;

        foreach (var entry in reports.SelectMany(report => report.Entries)
            .Where(entry => entry.Day >= from && entry.Day <= to))
        {
            var matches = students.Where(student => entry.StudentId.Length > 0 && student.Id == entry.StudentId).ToArray();
            if (matches.Length == 0)
                matches = students.Where(student => Fold(student.Name) == Fold(entry.Name)).ToArray();
            if (matches.Length == 0)
                matches = students.Where(student => NameParts(student.Name) == NameParts(entry.Name)).ToArray();
            if (matches.Length > 1 || (matches.Length == 0 && string.IsNullOrWhiteSpace(entry.Name)))
            {
                unmatched++;
                if (matches.Length == 0) unresolvedDays.Add(entry.Day);
                else foreach (var match in matches) unresolvedCells.Add((match.Id, entry.Day));
                continue;
            }
            var student = matches.SingleOrDefault();
            if (student == null)
            {
                student = new Student(entry.StudentId.Length > 0 ? entry.StudentId : "csv:" + Fold(entry.Name), entry.Name);
                students.Add(student);
            }
            var key = (student.Id, entry.Day);
            if (!entries.TryGetValue(key, out var list))
                entries.Add(key, list = new List<AttendanceEntry>());
            list.Add(entry);
        }

        Days = lessons.Keys.Concat(entries.Keys.Select(key => key.Day)).Distinct().Order().ToArray();
        Students = students.OrderBy(student => student.Name, StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), true)).ToArray();
        var notesByName = (absenceNotes ?? []).Where(note => !string.IsNullOrWhiteSpace(note.Name) && note.From <= note.To)
            .Distinct().ToLookup(note => Fold(note.Name));
        var notesByStudent = Students.GroupBy(student => Fold(student.Name))
            .Where(group => group.Count() == 1 && notesByName.Contains(group.Key))
            .ToDictionary(group => group.Single().Id, group => notesByName[group.Key].ToArray());
        if (unmatched > 0)
            notes.Add($"{unmatched} Zeile(n) konnten keiner Person eindeutig zugeordnet werden.");
        if (!HasTimetable && (!emptyTimetable || Days.Length > 0))
            notes.Add("Unterrichtstage werden aus den verfügbaren Einträgen angezeigt.");
        if (!HasAbsenceReport)
            notes.Add("Der Fehlzeitenbericht liegt nicht vollständig vor. Kennzahlen beziehen sich auf die importierten Einträge.");
        if (!HasLatenessReport)
            notes.Add("Ein vollständiger separater Verspätungsbericht fehlt. Verspätungen können in anderen importierten Berichten enthalten sein.");
        if (entries.Values.SelectMany(day => day).Any(entry => entry.Kind == EntryKind.Absent && entry.Excuse == Excuse.Unspecified))
            notes.Add("Bei einigen Fehlzeiten ist der Entschuldigungsstatus in den eingelesenen Berichten unbekannt.");
        Notes = notes.Distinct().ToArray();

        var teachingDays = lessons.Keys.Concat(entries.Where(item => item.Value.Any(entry =>
            entry.Kind is EntryKind.Present or EntryKind.Absent or EntryKind.Late)).Select(item => item.Key.Day)).ToHashSet();
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var day in Days)
        {
            var dayLessons = lessons.GetValueOrDefault(day) ?? [];
            foreach (var student in Students)
            {
                var events = entries.GetValueOrDefault((student.Id, day))?.ToArray() ?? [];
                var knownStudent = coveredStudents.Contains(student.Id);
                var unresolved = unresolvedDays.Contains(day) || unresolvedCells.Contains((student.Id, day));
                var coverage = HasAbsenceReport && knownStudent && !unresolved;
                var kind = GetMark(events, dayLessons, coverage, rosterStudents.Contains(student.Id), teachingDays.Contains(day), day <= today);
                var dayNotes = events.Any(entry => entry.Kind == EntryKind.Absent) && notesByStudent.TryGetValue(student.Id, out var personNotes)
                    ? personNotes.Where(note => note.From <= day && day <= note.To).ToArray() : [];
                var reason = kind == DayMark.Unknown ? UnknownReason(events, dayLessons, knownStudent, unresolved, day, today) : "";
                Cells[(student.Id, day)] = new DayCell(kind, events, dayLessons, dayNotes, reason);
            }
        }
    }

    private DayMark GetMark(AttendanceEntry[] events, Lesson[] lessons, bool hasClassCoverage, bool onRoster, bool teachingDay, bool started)
    {
        var absent = events.Any(entry => entry.Kind == EntryKind.Absent);
        var late = events.Any(entry => entry.Kind == EntryKind.Late);
        if (absent)
        {
            var allExcused = events.Where(entry => entry.Kind == EntryKind.Absent).All(entry => entry.Excuse == Excuse.Excused);
            if (allExcused) return late ? DayMark.ExcusedAbsenceAndLate : DayMark.ExcusedAbsence;
            return late ? DayMark.AbsentAndLate : DayMark.Absent;
        }
        if (late) return DayMark.Late;
        if (events.Any(entry => entry.Kind == EntryKind.Exempt)) return DayMark.Exempt;
        if (events.Any(entry => entry.Kind == EntryKind.Unknown)) return DayMark.Unknown;
        var present = events.Where(entry => entry.Kind == EntryKind.Present).ToArray();
        if (present.Any(entry => entry.WholeDay)) return DayMark.Present;
        if (HasTimetable && lessons.Length > 0 && lessons.All(lesson => present.Any(entry => Covers(entry, lesson))))
            return DayMark.Present;
        if (hasClassCoverage && onRoster && HasTimetable && lessons.Length > 0 && lessons.All(lesson => lesson.Checked == true))
            return DayMark.Present;
        if (hasClassCoverage && started && teachingDay && lessons.All(lesson => lesson.Checked != false))
            return DayMark.NoAbsenceRecorded;
        return DayMark.Unknown;
    }

    private string UnknownReason(AttendanceEntry[] events, Lesson[] lessons, bool knownStudent, bool unresolved, DateOnly day, DateOnly today)
    {
        if (events.Any(entry => entry.Kind == EntryKind.Unknown)) return "WebUntis liefert für diese Person einen ungeklärten Eintrag.";
        if (unresolved) return "Ein Eintrag für diesen Tag konnte keiner Person eindeutig zugeordnet werden.";
        if (!HasAbsenceReport) return "Der Fehlzeitenbericht der Klasse fehlt oder ist unvollständig.";
        if (!knownStudent) return "Für diese Person fehlt eine bestätigte Zuordnung zur Namensliste der Klasse.";
        if (day > today) return "Der Unterrichtstag liegt in der Zukunft.";
        if (lessons.Any(lesson => lesson.Checked == false)) return "WebUntis meldet mindestens eine noch offene Anwesenheitskontrolle.";
        if (day == today) return "Für heute liegt noch keine vollständige Anwesenheitsbestätigung vor.";
        return "Für diesen Tag liegt keine vollständige Anwesenheitsbestätigung vor.";
    }

    private static bool Covers(AttendanceEntry entry, Lesson lesson)
    {
        if (entry.Start is { } start && entry.End is { } end &&
            lesson.Start is { } lessonStart && lesson.End is { } lessonEnd)
            return start <= lessonStart && end >= lessonEnd;
        return entry.Period.Length > 0 && entry.Period == lesson.Period &&
            (entry.Subject.Length == 0 || lesson.Subject.Length == 0 || Fold(entry.Subject) == Fold(lesson.Subject));
    }

    public Alert[] GetAlerts(int lateThreshold, int sequenceThreshold)
    {
        var alerts = new List<Alert>();
        var firstRecent = To.AddDays(-27);
        foreach (var student in Students)
        {
            var events = Days.SelectMany(day => Cells[(student.Id, day)].Entries).Where(entry => entry.Counts).ToArray();
            var absences = events.Where(entry => entry.Kind == EntryKind.Absent).ToArray();
            foreach (var status in new[] { Excuse.Open, Excuse.Unexcused })
            {
                var matching = absences.Where(entry => entry.Excuse == status).ToArray();
                if (matching.Length > 0)
                    alerts.Add(new Alert(student.Id, student.Name, status == Excuse.Open ? "Entschuldigung offen" : "Unentschuldigt",
                        DescribeAbsences(matching), matching.Max(entry => entry.Day)));
            }
            var lateDays = events.Where(entry => entry.Kind == EntryKind.Late && entry.Day >= firstRecent)
                .Select(entry => entry.Day).Distinct().Order().ToArray();
            if (lateDays.Length >= lateThreshold)
                alerts.Add(new Alert(student.Id, student.Name, "Wiederholt verspätet",
                    $"{lateDays.Length} Unterrichtstage mit Verspätung in den letzten vier Wochen des Zeitraums", lateDays[^1]));

            if (!HasTimetable) continue;
            int streak = 0, longest = 0;
            var lastDay = From;
            foreach (var day in Days)
            {
                var cell = Cells[(student.Id, day)];
                streak = cell.Lessons.Length > 0 && cell.Entries.Any(entry => entry.Kind == EntryKind.Absent && entry.Counts) ? streak + 1 : 0;
                if (streak <= longest) continue;
                longest = streak;
                lastDay = day;
            }
            if (longest >= sequenceThreshold)
                alerts.Add(new Alert(student.Id, student.Name, "Fehlzeiten in Folge",
                    $"{longest} aufeinanderfolgende Unterrichtstage mit Fehlzeit", lastDay));
        }
        return alerts.OrderByDescending(alert => alert.Day).ThenBy(alert => alert.Name).ToArray();
    }

    public static string DescribeAbsences(IEnumerable<AttendanceEntry> records)
    {
        var entries = records.ToArray();
        var days = entries.Select(entry => entry.Day).Distinct().Count();
        var units = entries.All(entry => entry.Units.HasValue) && entries.Length > 0
            ? $" · {entries.Sum(entry => entry.Units):0.##} Fehlstunden" : "";
        return $"{days} Unterrichtstag(e){units}";
    }

    public static string Fold(string text)
    {
        var folded = text.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss")
            .Normalize(NormalizationForm.FormD);
        return string.Concat(folded.Where(char.IsLetterOrDigit));
    }

    internal static string NameParts(string name) =>
        string.Join(" ", name.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(Fold).Order());

    public static string Symbol(DayMark mark) => mark switch
    {
        DayMark.Present or DayMark.NoAbsenceRecorded => "•", DayMark.Absent => "│", DayMark.Late => "+",
        DayMark.AbsentAndLate => "│+", DayMark.ExcusedAbsence => "E", DayMark.ExcusedAbsenceAndLate => "E+",
        DayMark.Exempt => "B", _ => "?"
    };
}
