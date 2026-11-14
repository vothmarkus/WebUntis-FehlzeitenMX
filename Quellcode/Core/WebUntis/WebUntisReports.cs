using System.Text.Json;

namespace WebUntisAuswertung.Core;

internal static class WebUntisReports
{
    public const string Origin = "https://bkt.webuntis.com";
    public const string Address = Origin + "/class-register-reports";
    public const string AbsenceTimes = Origin + "/absence-times";
    public const string Absences = Origin + "/absences";
    public const string Timetable = Origin + "/timetable/my-teacher";
    public const string CommandName = "__fehlzeitenMxReport";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static IReadOnlyList<ReportKind> Automatic { get; } =
        [ReportKind.Absences, ReportKind.Lateness, ReportKind.Timetable];
    public static readonly string InstallScript = "(() => { if (location.origin !== " + JsonSerializer.Serialize(Origin) +
        ") return; " + ReadScript() + "\nglobalThis." + CommandName + " = reportCommand; })()";

    public static bool IsSchoolAddress(string? value)
    {
        if (value?.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) == true) value = value[5..];
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 &&
            uri.GetLeftPart(UriPartial.Authority).Equals(Origin, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReportRoute(Uri uri) =>
        uri.AbsolutePath.StartsWith("/WebUntis/", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.EndsWith(".do", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Contains("report", StringComparison.OrdinalIgnoreCase);

    public static bool IsReadyCsvLink(string? address)
    {
        if (!IsSchoolAddress(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            !uri.AbsolutePath.Equals("/WebUntis/reports.do", StringComparison.OrdinalIgnoreCase))
            return false;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return string.Equals(query["format"], "csv", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(query["msgId"]);
    }

    private static string ReadScript()
    {
        using var stream = typeof(WebUntisReports).Assembly.GetManifestResourceStream("Reports.js")
            ?? throw new InvalidOperationException("Die WebUntis-Berichtssteuerung fehlt.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
