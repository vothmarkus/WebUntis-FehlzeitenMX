namespace WebUntisAuswertung.Core;

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
