using System.Text;
using WebUntisAuswertung.Core;
using Xunit;

namespace WebUntisAuswertung.Tests;

public sealed class RosterCsvTests
{
    [Fact]
    public void ExportsTheEntireRosterInTheNotenrechnerFormat()
    {
        var lesson = Encoding.UTF8.GetBytes("""
            Vorname;Nachname;Klasse;Schüler-ID;E-Mail
            Alex;Müller;Testklasse;101;alex@schule.example
            """);
        var contacts = RosterCsv.ReadLessonEmails(lesson, ["Testklasse"], "");
        var roster = new (string ClassName, Student Student)[]
        {
            ("Testklasse", new Student("101", "Müller Alex")),
            ("Testklasse", new Student("102", "Jansen Sam"))
        };

        var (csv, emailCount) = RosterCsv.Format(roster, 2026, contacts);

        Assert.Equal(1, emailCount);
        Assert.Equal("Vorname;Nachname;Email;Klasse;Schuljahr;SchuelerID;Kennung\r\n" +
            "\"Alex\";\"Müller\";\"alex@schule.example\";\"Testklasse\";2026/2027;\"\";\"\"\r\n" +
            "\"Sam\";\"Jansen\";\"\";\"Testklasse\";2026/2027;\"bkt.webuntis.com:102\";\"untis:bkt.webuntis.com:102\"\r\n",
            csv);
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, RosterCsv.Encoding.GetPreamble());
    }
}
