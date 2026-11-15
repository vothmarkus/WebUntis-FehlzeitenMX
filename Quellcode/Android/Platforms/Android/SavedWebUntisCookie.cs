using System.Globalization;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung.Mobile;

internal sealed record SavedWebUntisCookie(string Name, string Domain, string Path, string Header, DateTimeOffset? Expires)
{
    private static readonly string SchoolHost = new Uri(WebUntisReports.Origin).Host;

    // CookieManagerCompat returns Set-Cookie fields, including HttpOnly and path information.
    public static SavedWebUntisCookie? Read(string header, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Any(char.IsControl)) return null;
        var fields = header.Split(';', StringSplitOptions.TrimEntries);
        var equals = fields[0].IndexOf('=');
        if (equals <= 0) return null;
        var name = fields[0][..equals];
        if (name.Any(c => c <= ' ' || c >= '\u007f' || "()<>@,;:\\\"/[]?={}".Contains(c))) return null;

        var domain = "";
        var path = "/";
        var httpOnly = false;
        var partitioned = false;
        string? sameSite = null;
        DateTimeOffset? expires = null;
        long? maxAge = null;
        foreach (var field in fields.Skip(1))
        {
            var pair = field.Split('=', 2, StringSplitOptions.TrimEntries);
            var value = pair.ElementAtOrDefault(1) ?? "";
            switch (pair[0].ToLowerInvariant())
            {
                case "domain":
                    domain = value.TrimStart('.').ToLowerInvariant();
                    if (domain != SchoolHost && domain != "webuntis.com") return null;
                    break;
                case "path":
                    if (!value.StartsWith('/')) return null;
                    path = value;
                    break;
                case "httponly": httpOnly = true; break;
                case "partitioned": partitioned = true; break;
                case "samesite" when value.Equals("Lax", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("Strict", StringComparison.OrdinalIgnoreCase) || value.Equals("None", StringComparison.OrdinalIgnoreCase):
                    sameSite = value;
                    break;
                case "expires" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var expiry):
                    expires = expiry.ToUniversalTime();
                    break;
                case "max-age" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds):
                    maxAge = seconds;
                    break;
            }
        }
        if (maxAge is <= 0) return null;
        if (maxAge is { } age) expires = now.AddSeconds(Math.Min(age, 400L * 24 * 60 * 60));
        if (expires <= now) return null;
        var attributes = new List<string> { fields[0], "Path=" + path, "Secure" };
        if (domain.Length > 0) attributes.Add("Domain=" + domain);
        if (httpOnly) attributes.Add("HttpOnly");
        if (partitioned) attributes.Add("Partitioned");
        if (sameSite != null) attributes.Add("SameSite=" + sameSite);
        if (expires is { } date) attributes.Add("Expires=" + date.ToString("R", CultureInfo.InvariantCulture));
        return new SavedWebUntisCookie(name, domain, path, string.Join("; ", attributes), expires);
    }
}
