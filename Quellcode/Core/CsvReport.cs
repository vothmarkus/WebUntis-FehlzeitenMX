using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace WebUntisAuswertung.Core;

public enum CsvField
{
    Day, Name, LastName, FirstName, StudentId, Kind, Excuse, Start, End,
    Period, Subject, Minutes, Duration, Units, Late, Checked, Counts, WholeDay, Reason, AbsenceText, ExcuseText, TimeRange
}

public sealed record CsvLayout(int Header, Dictionary<CsvField, int> Columns)
{
    public bool Has(CsvField field) => Columns.ContainsKey(field);
    public bool CanRead(ReportKind kind) => Has(CsvField.Day) &&
        (kind == ReportKind.Timetable || Has(CsvField.Name) || Has(CsvField.LastName) || Has(CsvField.StudentId));
}

public sealed class CsvReport
{
    public const int MaxFileBytes = 25 * 1024 * 1024;
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly Regex DateInCell = new(
        @"(?<!\d)(\d{4}-\d{1,2}-\d{1,2}|\d{1,2}\.\d{1,2}\.\d{4}|\d{1,2}\.\d{1,2}\.\d{2}|\d{8})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] DateFormats = ["d.M.yyyy", "d.M.yy", "yyyy-M-d", "yyyyMMdd"];
    private static readonly Dictionary<CsvField, string[]> Aliases = new Dictionary<CsvField, string[]>()
    {
        [CsvField.Day] = ["Datum", "Tag", "Unterrichtstag", "Unterrichtsdatum", "Datum des Unterrichts", "Fehldatum", "Anwesenheitsdatum", "Date", "Startdatum", "Von Datum", "Datum von"],
        [CsvField.Name] = ["Schüler", "Schülerin", "Schüler/in", "Schüler*in", "Schülername", "Schüler*innen", "Schüler/-in", "Name", "Name des Schülers", "Name der Schülerin", "Name, Vorname", "Nach- und Vorname", "Schüler (Name)", "Schüler/in Name", "Student", "Student name"],
        [CsvField.LastName] = ["Nachname", "Familienname", "Last name", "Surname"],
        [CsvField.FirstName] = ["Vorname", "First name", "Given name"],
        [CsvField.StudentId] = ["Schüler ID", "Student ID", "Schülernummer"],
        [CsvField.Kind] = ["Art", "Typ", "Anwesenheitsstatus", "Anwesenheit", "Fehlzeittyp", "Abwesenheitsart", "Fehlzeitenart", "Status der Anwesenheit", "Type", "Unterrichtsstatus"],
        [CsvField.Excuse] = ["Entschuldigungsstatus", "Entschuldigung", "Entschuldigt", "Status Entschuldigung", "Status", "Excuse status"],
        [CsvField.Start] = ["Beginn", "Start", "Von", "Beginnzeit", "Startzeit", "Uhrzeit von", "Start time", "Von Uhrzeit", "Anfang"],
        [CsvField.End] = ["Ende", "Bis", "Endzeit", "Uhrzeit bis", "End time", "Bis Uhrzeit"],
        [CsvField.Period] = ["Stunde", "Unterrichtsstunde", "Schulstunde", "Std. Nr.", "Stundennummer", "Einheit", "Period",
            "UE", "UE Nr.", "Unterrichtseinheit", "Nr. Unterrichtsstunde"],
        [CsvField.Subject] = ["Fach", "Unterricht", "Gegenstand", "Subject", "Fachkürzel", "Fachkurzname"],
        [CsvField.Minutes] = ["Minuten", "Fehlminuten", "Fehlmin.", "Dauer in Minuten", "Dauer (Min.)", "Fehlzeit in Minuten", "Minutes", "Fehlzeit (Min.)", "Fehlzeit (Minuten)"],
        [CsvField.Duration] = ["Dauer", "Zeitdauer", "Fehlzeit", "Fehlzeiten", "Zeitstunden", "Zeitst.", "Std.", "Zeit", "hh:mm", "Duration"],
        [CsvField.TimeRange] = ["Zeit", "Uhrzeit", "Unterrichtszeit", "Zeit von bis", "Uhrzeit von bis"],
        [CsvField.Units] = ["Fehlstunden", "Fehlstd.", "Fehlst.", "Fehlschulstunden", "Schulstunden", "Schulstd.", "Schulst.", "Fehlstunden (Schulst.)", "Anzahl Stunden"],
        [CsvField.Late] = ["Verspätung", "Verspätungen", "Verspätungsminuten", "Verspätung (Min.)", "Verspätung in Minuten", "Delay", "Late"],
        [CsvField.Checked] = ["Kontrolliert", "Abwesenheitskontrolle", "Anwesenheitskontrolle", "Abwesenheiten kontrolliert", "Anwesenheiten kontrolliert", "Abwesenheitskontrolle durchgeführt", "Anwesenheitskontrolle durchgeführt", "Kontrolle abgeschlossen", "Attendance checked"],
        [CsvField.Counts] = ["Zählend", "Zählt", "Wird gezählt", "Zählende Fehlzeit", "Abwesenheit zählt", "Counts"],
        [CsvField.WholeDay] = ["Ganztägig", "Ganzer Tag", "Whole day"],
        [CsvField.Reason] = ["Abwesenheitsgrund", "Grund", "Fehlgrund", "Absence reason"],
        [CsvField.AbsenceText] = ["Abwesenheitstext", "Abwesenheit Text", "Text zur Abwesenheit", "Text der Abwesenheit", "Bemerkung zur Abwesenheit", "Anmerkung", "Anmerkungen", "Bemerkung", "Absence text"],
        [CsvField.ExcuseText] = ["Entschuldigungstext", "Entschuldigung Text", "Entschuldigungs-Text", "Entsch. Text", "Text zur Entschuldigung", "Text der Entschuldigung", "Bemerkung zur Entschuldigung", "Excuse text"]
    }.ToDictionary(pair => pair.Key, pair => pair.Value.Select(AttendanceMatrix.Fold).Distinct().ToArray());

    public string[][] Rows { get; }

    private CsvReport(string[][] rows) => Rows = rows;

    public static CsvReport Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxFileBytes)
            throw new InvalidDataException("Die CSV ist größer als 25 MB. Wähle einen kürzeren Zeitraum.");
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    public static CsvReport Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxFileBytes)
            throw new InvalidDataException("Die CSV ist größer als 25 MB. Wähle einen kürzeren Zeitraum.");
        string text;
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
            text = Encoding.Unicode.GetString(bytes[2..]);
        else if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
            text = Encoding.BigEndianUnicode.GetString(bytes[2..]);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding(1252).GetString(bytes);
            }
        }
        text = text.TrimStart('\uFEFF');
        if (text.TrimStart().StartsWith('<') || text.IndexOf('\0') >= 0)
            throw new InvalidDataException("Die Datei enthält keine CSV-Tabelle. Prüfe die Anmeldung und wähle CSV-Ausgabe.");
        return Parse(text);
    }

    public static CsvReport Parse(string text)
    {
        var candidates = new List<(string[][] Rows, int Score)>();
        foreach (var delimiter in new[] { ";", "\t", "," })
        {
            try
            {
                using var parser = new TextFieldParser(new StringReader(text));
                parser.SetDelimiters(delimiter);
                parser.HasFieldsEnclosedInQuotes = true;
                var rows = new List<string[]>();
                while (!parser.EndOfData)
                {
                    var fields = parser.ReadFields();
                    if (fields is { Length: > 0 } && !(fields.Length == 1 && fields[0].StartsWith("sep=", StringComparison.OrdinalIgnoreCase)))
                        rows.Add(fields.Select(value => value.Trim().Trim('\uFEFF')).ToArray());
                }
                var sample = rows.Take(45).ToArray();
                var aliases = sample.Select(row => GuessColumns(row).Count).DefaultIfEmpty().Max();
                var multi = sample.Count(row => row.Length > 1);
                var score = aliases * 50 + multi * 2 + sample.Select(row => row.Length).DefaultIfEmpty().Max();
                candidates.Add((rows.ToArray(), score));
            }
            catch (MalformedLineException) { }
        }
        if (candidates.Count == 0)
            throw new InvalidDataException("Trennzeichen oder Anführungszeichen der CSV konnten nicht gelesen werden.");
        return new CsvReport(candidates.OrderByDescending(candidate => candidate.Score).First().Rows);
    }

    public CsvLayout Detect(ReportKind kind)
    {
        var layouts = Rows.Take(45).Select((row, index) => new CsvLayout(index, GuessColumns(row))).ToArray();
        return layouts.Where(layout => layout.CanRead(kind)).OrderByDescending(layout => layout.Columns.Count).FirstOrDefault()
            ?? layouts.OrderByDescending(layout => layout.Columns.Count).FirstOrDefault()
            ?? new CsvLayout(-1, []);
    }

    public bool IsMonthlyPresenceSummary(CsvLayout layout)
    {
        if (layout.Header < 0 || layout.Header >= Rows.Length || layout.Has(CsvField.Day) || !layout.Has(CsvField.Name))
            return false;
        var headings = Rows[layout.Header].Select(AttendanceMatrix.Fold).ToHashSet();
        return headings.Contains("klasse") && headings.Contains("monat") && headings.Contains("jahr");
    }

    private static Dictionary<CsvField, int> GuessColumns(string[] row)
    {
        var normalized = row.Select(AttendanceMatrix.Fold).ToArray();
        var found = new Dictionary<CsvField, int>();
        foreach (var pair in Aliases)
        {
            var index = Array.FindIndex(normalized, label => pair.Value.Contains(label));
            if (index >= 0) found[pair.Key] = index;
        }
        // WebUntis places a pupil's general text before Datum and the absence text after Abwesenheitsgrund.
        if (!found.ContainsKey(CsvField.AbsenceText))
        {
            var text = Array.FindLastIndex(normalized, label => label == "text");
            var studentText = normalized.Contains("externeid") && found.TryGetValue(CsvField.Day, out var day) && text < day;
            if (text >= 0 && !studentText) found[CsvField.AbsenceText] = text;
        }
        return found;
    }

    public Report Import(ReportKind kind, CsvLayout layout)
    {
        if (IsMonthlyPresenceSummary(layout))
            throw new InvalidDataException("Der WebUntis-Bericht enthält Monatswerte. Die Tagesmatrix benötigt Berichte mit Tagesdatum.");
        if (!layout.CanRead(kind))
            throw new InvalidDataException("Im Bericht fehlt die Spalte für das Tagesdatum oder den Namen.");
        var entries = new List<AttendanceEntry>();
        var lessons = new List<Lesson>();
        var notes = new List<string>();
        var listed = new Dictionary<string, Student>();
        var skipped = 0;
        var countableRows = 0;
        DateOnly? dateContext = null;
        var studentContext = "";

        foreach (var row in Rows.Skip(layout.Header + 1))
        {
            string Get(CsvField field) => layout.Columns.TryGetValue(field, out var column) && column < row.Length
                ? row[column] : "";
            var dateText = Get(CsvField.Day);
            var date = ReadDay(dateText);
            var name = Get(CsvField.Name);
            if (name.Length == 0)
                name = string.Join(" ", new[] { Get(CsvField.LastName), Get(CsvField.FirstName) }.Where(value => value.Length > 0));
            var studentId = Get(CsvField.StudentId);
            var studentKey = studentId.Length > 0 ? studentId : "csv:" + AttendanceMatrix.Fold(name);
            var subject = Get(CsvField.Subject);
            var period = Get(CsvField.Period);
            var entryType = Get(CsvField.Kind);
            var foldedType = AttendanceMatrix.Fold(entryType);
            var start = ReadTime(Get(CsvField.Start));
            var end = ReadTime(Get(CsvField.End));
            var timeRange = Get(CsvField.TimeRange).Split(['-', '–', '—'], StringSplitOptions.TrimEntries);
            if (timeRange is [var fromTime, var toTime] && ReadTime(fromTime) is { } rangeStart && ReadTime(toTime) is { } rangeEnd)
            {
                start ??= rangeStart;
                end ??= rangeEnd;
            }
            var hasTime = start.HasValue && end.HasValue && end > start;
            var scheduleData = subject.Length > 0 || period.Length > 0 || hasTime || Get(CsvField.Checked).Length > 0;
            var detailData = Get(CsvField.Units).Length > 0 || Get(CsvField.Duration).Length > 0 || Get(CsvField.Minutes).Length > 0 ||
                Get(CsvField.Late).Length > 0 || Get(CsvField.Excuse).Length > 0 || entryType.Length > 0 || scheduleData;
            var isHeader = GuessColumns(row).ContainsKey(CsvField.Day) && !date.HasValue;
            if (isHeader || row.All(string.IsNullOrWhiteSpace) || IsSummary(dateText) || IsSummary(name) || IsSummary(row.FirstOrDefault() ?? "")) continue;
            var isRosterTotal = kind is ReportKind.Absences or ReportKind.Lateness && dateText.Length == 0 && !scheduleData &&
                entryType.Length == 0 && Get(CsvField.Reason).Length == 0 && Get(CsvField.Excuse).Length == 0 &&
                Get(CsvField.Late).Length == 0 && Get(CsvField.AbsenceText).Length == 0 && Get(CsvField.ExcuseText).Length == 0 &&
                (ReadDecimal(Get(CsvField.Units)).HasValue || ReadNonnegativeInt(Get(CsvField.Minutes)).HasValue ||
                    ReadDuration(Get(CsvField.Minutes)).HasValue || ReadDuration(Get(CsvField.Duration)).HasValue);
            if (date.HasValue)
            {
                dateContext = date;
                studentContext = studentKey;
            }
            else if (dateText.Length == 0 && detailData && !isRosterTotal &&
                (kind == ReportKind.Timetable || studentKey == studentContext)) date = dateContext;

            if (kind == ReportKind.Timetable)
            {
                if (foldedType.Contains("entfall") || foldedType.Contains("entfaell") || foldedType.Contains("ausfall") ||
                    AttendanceMatrix.Fold(subject) is "entfall" or "entfaellt" or "ausfall") continue;
                if (date is { } teachingDay && (scheduleData || row.Count(value => value.Length > 0) == 1))
                    lessons.Add(new Lesson(teachingDay, start, end, period, subject, ReadCheck(Get(CsvField.Checked))));
                else if (scheduleData) skipped++;
                continue;
            }

            if (name.Length == 0 && studentId.Length == 0) continue;
            if (name.Length > 0)
            {
                listed.TryAdd(studentKey, new Student(studentKey, name));
            }
            if (isRosterTotal) continue;
            if (date == null)
            {
                if (detailData || dateText.Length > 0) skipped++;
                continue;
            }
            countableRows++;
            var minuteText = Get(CsvField.Minutes);
            var minutes = ReadNonnegativeInt(minuteText) ?? ReadDuration(minuteText) ?? ReadDuration(Get(CsvField.Duration));
            var units = ReadDecimal(Get(CsvField.Units));
            var lateValue = Get(CsvField.Late);
            var lateMinutes = ReadNonnegativeInt(lateValue) ?? ReadDuration(lateValue);
            var reason = Get(CsvField.Reason);
            var mark = entryType switch
            {
                "+" => EntryKind.Late, "•" => EntryKind.Present, "│" or "|" => EntryKind.Absent,
                "?" => EntryKind.Unknown, _ => ReadKind(foldedType)
            };
            var statusKind = ReadKind(AttendanceMatrix.Fold(Get(CsvField.Excuse)));
            if (mark == null && statusKind is EntryKind.Present or EntryKind.Absent or EntryKind.Late or EntryKind.Exempt)
                mark = statusKind;
            var explicitPresence = mark == EntryKind.Present;
            if (lateMinutes > 0 || IsYes(lateValue) ||
                (mark == null && units.GetValueOrDefault() == 0 && AttendanceMatrix.Fold(reason).Contains("verspaet")))
                mark = EntryKind.Late;
            var wholeDay = IsYes(Get(CsvField.WholeDay)) || foldedType.Contains("ganztaegig");
            if (kind == ReportKind.Lateness)
            {
                if (mark != EntryKind.Late && !hasTime && lateMinutes.GetValueOrDefault() == 0 &&
                    minutes.GetValueOrDefault() == 0 && units.GetValueOrDefault() == 0) continue;
                mark = EntryKind.Late;
                units = null;
            }
            if (mark == null)
            {
                if (kind == ReportKind.Absences)
                {
                    if ((minutes.HasValue || units.HasValue) && minutes.GetValueOrDefault() == 0 && units.GetValueOrDefault() == 0) continue;
                    mark = EntryKind.Absent;
                }
                else if (hasTime || period.Length > 0 || wholeDay || IsYes(Get(CsvField.Checked)))
                    mark = EntryKind.Present;
                else
                    mark = EntryKind.Unknown;
            }
            wholeDay |= mark == EntryKind.Present && !start.HasValue && !end.HasValue && period.Length == 0 &&
                subject.Length == 0 && (explicitPresence || IsYes(Get(CsvField.Checked)));
            if (mark == EntryKind.Late) minutes = lateMinutes > 0 ? lateMinutes : minutes;
            if (!minutes.HasValue && hasTime) minutes = (int)(end!.Value - start!.Value).TotalMinutes;
            var excuse = ReadExcuse(Get(CsvField.Excuse));
            var counts = !IsNo(Get(CsvField.Counts));
            entries.Add(new AttendanceEntry(name, studentId, date.Value, mark.Value, excuse,
                start, end, period, subject, minutes, units, counts, wholeDay,
                reason, Get(CsvField.AbsenceText), Get(CsvField.ExcuseText)));
        }
        if (skipped > 0)
            notes.Add($"{Label(kind)}: {skipped} Datenzeile(n) konnten nicht vollständig gelesen werden.");
        if (kind == ReportKind.Presence && countableRows > 0 && entries.All(entry => entry.Kind != EntryKind.Present))
            notes.Add("Der Anwesenheitsbericht enthält keine erkennbaren Bestätigungen.");
        var concreteDays = lessons.Where(lesson => lesson.Period.Length > 0 || lesson.Subject.Length > 0 ||
            lesson.Start.HasValue || lesson.End.HasValue || lesson.Checked.HasValue).Select(lesson => lesson.Day).ToHashSet();
        var uniqueLessons = lessons.Where(lesson => !concreteDays.Contains(lesson.Day) || lesson.Period.Length > 0 ||
            lesson.Subject.Length > 0 || lesson.Start.HasValue || lesson.End.HasValue || lesson.Checked.HasValue).Distinct().ToArray();
        return new Report(kind, entries.ToArray(), uniqueLessons, notes.ToArray(), skipped == 0, listed.Values.ToArray());
    }

    public static string Label(ReportKind kind) => kind switch
    {
        ReportKind.Absences => "Fehlzeiten pro Klasse",
        ReportKind.Lateness => "Verspätungen",
        ReportKind.Presence => "Anwesenheiten",
        _ => "Tagesübersicht Klasse"
    };

    private static bool IsSummary(string text)
    {
        var value = AttendanceMatrix.Fold(text);
        return value.StartsWith("summe") || value.StartsWith("gesamt") || value.StartsWith("davon") ||
            value.StartsWith("schuljahr") || value.StartsWith("zeitraum");
    }

    public static DateOnly? ReadDay(string text)
    {
        var match = DateInCell.Match(text);
        return match.Success && DateOnly.TryParseExact(match.Value, DateFormats, German, DateTimeStyles.None, out var date)
            ? date : null;
    }

    public static TimeOnly? ReadTime(string text) =>
        TimeOnly.TryParseExact(text, ["H:mm", "HH:mm:ss", "H.mm"], German, DateTimeStyles.None, out var time) ? time : null;

    public static decimal? ReadDecimal(string text) =>
        (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, German, out var value) ||
         decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) ? value : null;

    private static int? ReadNonnegativeInt(string text) =>
        int.TryParse(text.TrimEnd(' ', 'm', 'i', 'n', '.'), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : null;

    private static int? ReadDuration(string text)
    {
        var parts = text.Split(':');
        if (parts.Length is not (2 or 3) || !int.TryParse(parts[0], out var hours) || !int.TryParse(parts[1], out var minutes) ||
            hours is < 0 or > 10000 || minutes is < 0 or >= 60) return null;
        return hours * 60 + minutes;
    }

    private static EntryKind? ReadKind(string value)
    {
        if (value.Contains("verspaet") || value is "late" or "delay") return EntryKind.Late;
        if (value.Contains("nichtanwesend") || value.Contains("abwesend") || value.StartsWith("fehl") ||
            value is "a" or "absent" or "absence" || value.Contains("ganztaegigabwes")) return EntryKind.Absent;
        if (value.Contains("anwesend") || value is "anwesendheit" or "anwesenheit" or "present" or "presence" or "p") return EntryKind.Present;
        if (value.StartsWith("befrei") || value.StartsWith("befreiung") || value == "b") return EntryKind.Exempt;
        if (value is "offen" or "unklar" or "unbekannt" or "nichtkontrolliert" or "unknown") return EntryKind.Unknown;
        return null;
    }

    public static Excuse ReadExcuse(string value)
    {
        var key = AttendanceMatrix.Fold(value);
        if (key.StartsWith("nichtentsch") || key.StartsWith("nichtakzep") || key.StartsWith("unentsch") || key == "unexcused") return Excuse.Unexcused;
        if (key.StartsWith("offen") || key.StartsWith("ausstehend") || key == "pending") return Excuse.Open;
        if (key.StartsWith("entsch") || key.StartsWith("akzeptiert") || key == "excused" || IsYes(value)) return Excuse.Excused;
        return Excuse.Unspecified;
    }

    private static bool IsYes(string value) =>
        AttendanceMatrix.Fold(value) is "1" or "x" or "ja" or "true" or "yes" or "kontrolliert" or "abgeschlossen" or "zaehlend" || value is "✓" or "+";
    private static bool IsNo(string value) =>
        AttendanceMatrix.Fold(value) is "0" or "nein" or "false" or "no" or "nichtzaehlend";

    private static bool? ReadCheck(string value)
    {
        if (IsYes(value)) return true;
        if (IsNo(value) || AttendanceMatrix.Fold(value) is "offen" or "nichtkontrolliert" or "nichtabgeschlossen" or "ausstehend")
            return false;
        return null;
    }
}
