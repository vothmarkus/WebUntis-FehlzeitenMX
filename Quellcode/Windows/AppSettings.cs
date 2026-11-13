using System.Globalization;
using System.Text.Json;

namespace WebUntisAuswertung;

internal sealed record ReportRange(DateOnly From, DateOnly To)
{
    public bool IsValid() => From.Year >= 1900 && To.Year < 9998 && To.DayNumber - From.DayNumber is >= 0 and <= 366;
    public bool Contains(DateOnly date) => From <= date && date <= To;
}

internal static class SchoolYears
{
    public static int StartYear(DateOnly date) => date.Month >= 8 ? date.Year : date.Year - 1;
    public static ReportRange CalendarRange(int startYear) => new(new DateOnly(startYear, 8, 1), new DateOnly(startYear + 1, 7, 31));
    public static string Label(int startYear) => $"{startYear}/{(startYear + 1) % 100:00}";
}

internal sealed record AppSettings
{
    public string[] FavoriteClasses { get; init; } = [];
    public string[]? MyClasses { get; init; }
    public string[] ClassNames { get; init; } = [];
    public ReportRange? SchoolYear { get; init; }
    public int? SelectedSchoolYear { get; init; }
    public string? CsvDownloadFolder { get; init; }
    public bool RememberLogin { get; init; }
    public bool ProtectWithHello { get; init; }
    public bool ShowLegend { get; init; } = true;

    public static string[] ClassList(IEnumerable<string?> values) => values
        .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), true)).ToArray();

    public static AppSettings Load(string folder)
    {
        var stored = Read<AppSettings>(Path.Combine(folder, "Einstellungen.json"));
        if (stored == null)
        {
            var legacy = ClassList(Read<string?[]>(Path.Combine(folder, "Startseite.json")) ?? []);
            return new AppSettings { FavoriteClasses = legacy, MyClasses = legacy };
        }
        var favorites = ClassList(stored.FavoriteClasses ?? []);
        return stored with
        {
            FavoriteClasses = favorites,
            MyClasses = stored.MyClasses == null ? favorites : ClassList(stored.MyClasses),
            ClassNames = ClassList(stored.ClassNames ?? []),
            SchoolYear = stored.SchoolYear?.IsValid() == true ? stored.SchoolYear : null,
            SelectedSchoolYear = stored.SelectedSchoolYear is >= 1900 and < 9997 ? stored.SelectedSchoolYear : null
        };
    }

    public void Save(string folder)
    {
        var path = Path.Combine(folder, "Einstellungen.json");
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static T? Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
}
