using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace WebUntisAuswertung;

internal sealed class SavedWebUntisSession(string folder)
{
    private static readonly string Host = new Uri(UntisBrowser.Origin).Host;
    private const int MaxSnapshotSize = 512 * 1024;
    private readonly string path = Path.Combine(folder, "WebUntis-Sitzung.bin");

    public async Task<bool> RestoreAsync(CoreWebView2CookieManager manager, CancellationToken token)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaxSnapshotSize) return false;
            var encrypted = await File.ReadAllBytesAsync(path, token);
            var clear = await new DataProtectionProvider().UnprotectAsync(CryptographicBuffer.CreateFromByteArray(encrypted));
            CryptographicBuffer.CopyToByteArray(clear, out var bytes);
            try
            {
                var cookies = JsonSerializer.Deserialize<SavedCookie[]>(bytes) ?? [];
                var restored = false;
                foreach (var saved in cookies.Where(cookie => cookie.Expires == null || cookie.Expires > DateTime.UtcNow))
                {
                    token.ThrowIfCancellationRequested();
                    var cookie = manager.CreateCookie(saved.Name, saved.Value, Host, saved.Path);
                    cookie.IsHttpOnly = saved.HttpOnly;
                    cookie.IsSecure = true;
                    cookie.SameSite = saved.SameSite;
                    if (saved.Expires is { } expires) cookie.Expires = expires;
                    manager.AddOrUpdateCookie(cookie);
                    restored = true;
                }
                return restored;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (StorageFailure(error)) { return false; }
    }

    public async Task<bool> SaveAsync(CoreWebView2CookieManager manager, CancellationToken token)
    {
        var temporary = path + ".tmp";
        try
        {
            var byPath = new Dictionary<(string Name, string Path), SavedCookie>();
            foreach (var endpoint in new[] { UntisBrowser.ReportsUrl, UntisBrowser.Origin + "/WebUntis/embedded.do",
                         UntisBrowser.Origin + "/WebUntis/reports.do" })
            {
                var cookies = await manager.GetCookiesAsync(endpoint).WaitAsync(TimeSpan.FromSeconds(4), token);
                foreach (var cookie in cookies.OrderBy(cookie => cookie.Domain.TrimStart('.').Equals(Host, StringComparison.OrdinalIgnoreCase)))
                {
                    var domain = cookie.Domain.TrimStart('.');
                    if (!domain.Equals(Host, StringComparison.OrdinalIgnoreCase) && !domain.Equals("webuntis.com", StringComparison.OrdinalIgnoreCase)) continue;
                    if (cookie.Name.Length == 0 || cookie.Value.Length == 0 || !cookie.Path.StartsWith('/')) continue;
                    byPath[(cookie.Name, cookie.Path)] = new SavedCookie(cookie.Name, cookie.Value, cookie.Path, cookie.IsHttpOnly,
                        cookie.SameSite, cookie.IsSession ? null : cookie.Expires.ToUniversalTime());
                }
            }
            if (byPath.Count == 0) return false;
            var clear = JsonSerializer.SerializeToUtf8Bytes(byPath.Values);
            try
            {
                var protectedData = await new DataProtectionProvider("LOCAL=user").ProtectAsync(CryptographicBuffer.CreateFromByteArray(clear));
                CryptographicBuffer.CopyToByteArray(protectedData, out var encrypted);
                if (encrypted.Length > MaxSnapshotSize) return false;
                await File.WriteAllBytesAsync(temporary, encrypted, token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception error) when (StorageFailure(error)) { return false; }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Clear()
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
    }

    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or COMException or
        CryptographicException or JsonException or ArgumentException or TimeoutException;

    private sealed record SavedCookie(string Name, string Value, string Path, bool HttpOnly,
        CoreWebView2CookieSameSiteKind SameSite, DateTime? Expires);
}
