using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung;

internal sealed record RosterEmail(string Given, string Family, string ClassName, string UntisId, string Email);

internal static class RosterCsv
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Regex SchoolEmail = new(@"^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?\.[a-z]{2,63}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string DownloadsFolder()
    {
        var id = DownloadsFolderId;
        var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer);
        try
        {
            if (result >= 0 && Marshal.PtrToStringUni(pointer) is { Length: > 0 } path) return path;
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static (string Content, int EmailCount) Format(IReadOnlyList<(string ClassName, Student Student)> roster, int year,
        IEnumerable<RosterEmail> contacts)
    {
        var available = contacts.ToArray();
        var byId = available.Where(contact => contact.UntisId.Length > 0).ToLookup(contact => contact.UntisId);
        var byName = available.Where(contact => contact.ClassName.Length > 0)
            .ToLookup(contact => (NameKey(contact.Given + " " + contact.Family), contact.ClassName.ToUpperInvariant()));
        var ambiguousNames = roster.GroupBy(row => (NameKey(row.Student.Name), row.ClassName.ToUpperInvariant()))
            .Where(group => group.Select(row => row.Student.Id).Distinct().Skip(1).Any()).Select(group => group.Key).ToHashSet();
        var matched = roster.Select(row =>
        {
            var key = (NameKey(row.Student.Name), row.ClassName.ToUpperInvariant());
            var idMatches = byId[row.Student.Id].Where(contact => NameKey(contact.Given + " " + contact.Family) == key.Item1);
            var matches = idMatches.Any() ? idMatches : ambiguousNames.Contains(key) ? [] : byName[key];
            var samePerson = matches.Where(contact => NameKey(contact.Given + " " + contact.Family) == key.Item1 &&
                (contact.UntisId.Length == 0 || contact.UntisId == row.Student.Id)).Distinct().ToArray();
            var addresses = samePerson.Select(contact => contact.Email).Append(CleanEmail(row.Student.Email))
                .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return (row.ClassName, row.Student, Addresses: addresses, Contacts: samePerson);
        }).ToArray();
        var candidates = matched.SelectMany(row => row.Addresses.Select(mail => (row.Student.Id, Mail: mail))).Distinct().ToArray();
        var sharedAddresses = candidates.GroupBy(row => row.Mail, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(row => row.Id).Distinct().Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addressById = candidates.GroupBy(row => row.Id)
            .Where(group => group.Count() == 1 && !sharedAddresses.Contains(group.First().Mail))
            .ToDictionary(group => group.Key, group => group.Single().Mail);
        var namesById = matched.GroupBy(row => row.Student.Id).ToDictionary(group => group.Key, group =>
            group.SelectMany(row => row.Contacts).FirstOrDefault(contact => contact.Email == addressById.GetValueOrDefault(group.Key)));

        var csv = new StringBuilder("Vorname;Nachname;Email;Klasse;Schuljahr;SchuelerID;Kennung\r\n");
        var emailCount = 0;
        foreach (var (className, student, _, _) in matched)
        {
            var email = addressById.GetValueOrDefault(student.Id) ?? "";
            if (email.Length > 0) emailCount++;
            var names = namesById[student.Id];
            var (given, family) = names != null ? (names.Given, names.Family) : SplitWebUntisName(student.Name);
            // Both importers derive the same key; Windows adds its own untis: prefix to the ID column.
            var id = email.Length == 0 ? $"bkt.webuntis.com:{student.Id}" : "";
            var key = email.Length == 0 ? $"untis:{id}" : "";
            csv.Append(Quote(given)).Append(';').Append(Quote(family)).Append(';').Append(Quote(email)).Append(';')
                .Append(Quote(className)).Append(';').Append($"{year}/{year + 1}").Append(';').Append(Quote(id)).Append(';')
                .Append(Quote(key)).Append("\r\n");
        }
        return (csv.ToString(), emailCount);
    }

    public static RosterEmail[] ReadLessonEmails(byte[] content, string[] lessonClasses, string lessonGroup)
    {
        var rows = CsvReport.Read(content).Rows;
        for (var index = 0; index < Math.Min(45, rows.Length); index++)
        {
            var headings = rows[index].Select(AttendanceMatrix.Fold).ToArray();
            int Column(params string[] labels) => labels.Select(label => Array.IndexOf(headings, label)).FirstOrDefault(index => index >= 0, -1);
            var first = Column("vorname", "vornamen", "firstname", "givenname", "schuelervorname");
            var last = Column("nachname", "familienname", "lastname", "surname", "schuelernachname", "schuelerfamilienname");
            var full = Column("namevorname", "nachundvorname", "schuelername", "schuelerinnen", "schuelerin", "schueler", "name");
            if (last < 0 && first >= 0) last = Column("langname", "name");
            if (last < 0 && full < 0) continue;
            var email = Column("schulemail", "schulmail", "schulemailadresse", "schueleremail", "schueleremailadresse",
                "emailadresse1", "email1", "email", "emailadresse", "emailaddress", "mailadresse", "mail",
                "emailschueler", "emailschuelerin", "emailschuelerinnen", "schuelerinemail", "schuelerinnenemail",
                "emailadresseschueler", "emailadresseschuelerin", "emailadresseschuelerinnen", "schuelerinemailadresse", "schuelerinnenemailadresse");
            if (email < 0) continue;
            var classroom = Column("klasse", "klassen", "klassenname", "class", "classroom");
            var id = Column("webuntisid", "schuelerid", "studentid");
            return ParseLessonEmails(rows.Skip(index + 1), email, first, last, full, classroom, id, lessonClasses, lessonGroup);
        }
        throw new InvalidDataException("Der Unterrichtsbericht enthält keine erkennbaren Namens- und E-Mail-Spalten.");
    }

    private static RosterEmail[] ParseLessonEmails(IEnumerable<string[]> rows, int email, int first, int last, int full,
        int classroom, int id, string[] lessonClasses, string lessonGroup)
    {
        var contacts = new List<RosterEmail>();
        foreach (var row in rows)
        {
            string Cell(int index) => index >= 0 && index < row.Length ? row[index] : "";
            var address = CleanEmail(Cell(email));
            if (address.Length == 0) continue;
            var (given, family) = last >= 0 ? (Unprotect(Cell(first)), Unprotect(Cell(last))) : SplitWebUntisName(Unprotect(Cell(full)));
            if (family.Length == 0) continue;
            var studentId = Cell(id);
            if (!studentId.All(char.IsAsciiDigit)) studentId = "";
            var classes = Classes(Unprotect(Cell(classroom)));
            if (classes.Length == 0 && lessonClasses.Length == 1) classes = lessonClasses;
            foreach (var name in classes.Append(lessonGroup).Where(name => name.Length > 0).DefaultIfEmpty(""))
                contacts.Add(new RosterEmail(given, family, name, studentId, address));
        }
        return contacts.Distinct().ToArray();
    }

    private static string[] Classes(string text) => text.Split(['|', ',', ';', '\r', '\n'],
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string CleanEmail(string value)
    {
        var address = Unprotect(value.Trim()).ToLowerInvariant();
        return address.Length <= 254 && !address.Contains("..") && SchoolEmail.IsMatch(address) ? address : "";
    }

    private static string Unprotect(string value) => value is ['\'', '=' or '+' or '-' or '@', ..] ? value[1..] : value;

    private static string NameKey(string name) => string.Join(' ', name.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Normalize().ToUpperInvariant()).Order(StringComparer.Ordinal));

    private static (string Given, string Family) SplitWebUntisName(string name)
    {
        if (name.IndexOf(',') is >= 0 and var comma) return (name[(comma + 1)..].Trim(), name[..comma].Trim());
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return ("", name);
        var familyWordCount = 1;
        while (familyWordCount < words.Length - 1 && (IsFamilyPrefix(words[familyWordCount - 1]) ||
            (familyWordCount > 1 && IsFamilyPrefix(words[0]) && IsFamilyConnector(words[familyWordCount - 1]))))
            familyWordCount++;
        return (string.Join(' ', words.Skip(familyWordCount)), string.Join(' ', words.Take(familyWordCount)));
    }

    private static bool IsFamilyPrefix(string word) => word.ToLowerInvariant() is "von" or "v." or "van" or "de" or "del" or "della" or
        "delle" or "degli" or "dei" or "da" or "das" or "do" or "dos" or "di" or "du" or "des" or "la" or "le" or "al" or "el" or
        "bin" or "ibn" or "zu" or "zur" or "zum" or "ten" or "ter" or "te";

    private static bool IsFamilyConnector(string word) => word.ToLowerInvariant() is "der" or "den" or "dem" or "het" or "las" or "los" or "d." or "'t" or "’t";

    public static async Task SaveAsync(string path, string csv, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".Schuelernamen-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Quote(string value)
    {
        if (value.TrimStart() is [var first, ..] && first is '=' or '+' or '-' or '@') value = "'" + value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
