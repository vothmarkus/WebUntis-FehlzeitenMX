using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WebUntisAuswertung.Mobile;

internal sealed record AndroidRelease(Version Version, long Size, byte[] Sha256, Uri Url)
{
    public string Label => Version.ToString(3);
}

internal static class AndroidUpdates
{
    private const string Repository = "vothmarkus/WebUntis-FehlzeitenMX";
    private const string Product = "WebUntis-FehlzeitenMX";
    private const string TagPrefix = "android-v";
    private const int PageSize = 50;
    private const long MaxApkBytes = 256L * 1024 * 1024;
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    public const string CacheFolder = "updates";
    public static bool Supported => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        MaxResponseContentBufferSize = 8 * 1024 * 1024,
        DefaultRequestHeaders = { { "User-Agent", Product } }
    };

    public static async Task<AndroidRelease?> CheckAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var checkToken = deadline.Token;
        try
        {
            AndroidRelease? latest = null;
            for (var page = 1; ; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{Repository}/releases?per_page={PageSize}&page={page}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using var response = await Client.SendAsync(request, checkToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.Contains("X-RateLimit-Reset")))
                    throw new IOException("GitHub begrenzt gerade die Zahl der Abfragen. Versuche es später erneut.");
                response.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(checkToken).ConfigureAwait(false),
                    cancellationToken: checkToken).ConfigureAwait(false);
                if (ReadLatestRelease(document.RootElement) is { } candidate &&
                    (latest == null || candidate.Version > latest.Version))
                    latest = candidate;
                if (document.RootElement.GetArrayLength() < PageSize) return latest;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub hat auf die Versionsprüfung nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException error)
        {
            throw new IOException("Die Versionsprüfung konnte GitHub nicht erreichen.", error);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Die Versionsinformationen von GitHub konnten nicht gelesen werden.", error);
        }
    }

    internal static AndroidRelease? ReadLatestRelease(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Die Versionsinformationen von GitHub konnten nicht gelesen werden.");

        AndroidRelease? latest = null;
        foreach (var entry in releases.EnumerateArray())
        {
            var tag = ReadString(entry, "tag_name");
            if (tag == null || !tag.StartsWith(TagPrefix, StringComparison.Ordinal) ||
                !Version.TryParse(tag.AsSpan(TagPrefix.Length), out var version) || version.Build < 0 ||
                tag != TagPrefix + version.ToString(3) || latest is { } previous && previous.Version >= version ||
                !entry.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                !entry.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;

            var filename = $"{Product}-Android-{version.ToString(3)}-arm64.apk";
            var url = $"{ReleasesUrl}/download/{tag}/{filename}";
            foreach (var asset in assets.EnumerateArray())
            {
                if (ReadString(asset, "name") != filename || ReadString(asset, "state") != "uploaded" ||
                    ReadString(asset, "browser_download_url") != url ||
                    !asset.TryGetProperty("size", out var value) || value.ValueKind != JsonValueKind.Number ||
                    !value.TryGetInt64(out var size) || size is <= 0 or > MaxApkBytes ||
                    ReadString(asset, "digest") is not { Length: 71 } digest ||
                    !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(Uri.IsHexDigit))
                    continue;

                latest = new AndroidRelease(version, size, Convert.FromHexString(digest[7..]), new Uri(url));
                break;
            }
        }
        return latest;
    }

    public static async Task<string> DownloadAsync(AndroidRelease release, string cache, IProgress<int> progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        var downloadToken = deadline.Token;
        var folder = Path.Combine(cache, CacheFolder);
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "WebUntis-FehlzeitenMX-Update.apk");
        var partial = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            if (File.Exists(destination))
            {
                await using var saved = File.OpenRead(destination);
                if (await VerifyHashAsync(saved, release, downloadToken).ConfigureAwait(false)) return destination;
            }
            using var response = await OpenAssetAsync(release.Url, downloadToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw BadDownload();
            using var data = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false);
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, useAsync: true))
            {
                var buffer = new byte[131072];
                var previous = 0;
                int count;
                while ((count = await data.ReadAsync(buffer, downloadToken).ConfigureAwait(false)) > 0)
                {
                    if (file.Position + count > release.Size) throw BadDownload();
                    await file.WriteAsync(buffer.AsMemory(0, count), downloadToken).ConfigureAwait(false);
                    var percent = (int)(file.Position * 100 / release.Size);
                    if (percent != previous) progress.Report(percent);
                    previous = percent;
                }
                file.Position = 0;
                if (!await VerifyHashAsync(file, release, downloadToken).ConfigureAwait(false)) throw BadDownload();
            }
            downloadToken.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: true);
            return destination;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Der APK-Download hat zu lange gedauert.");
        }
        catch (HttpRequestException error)
        {
            throw new IOException("GitHub konnte die APK nicht liefern. Prüfe die Version erneut und wiederhole den Download.", error);
        }
        finally
        {
            try { File.Delete(partial); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<HttpResponseMessage> OpenAssetAsync(Uri url, CancellationToken token)
    {
        var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var redirect = response.Headers.Location;
            response.Dispose();
            if (redirect is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } ||
                redirect.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("GitHub hat auf eine unerwartete Downloadadresse verwiesen.");
            response = await Client.GetAsync(redirect, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException($"GitHub meldet HTTP {(int)status}.");
    }

    private static async Task<bool> VerifyHashAsync(FileStream file, AndroidRelease release, CancellationToken token) =>
        file.Length == release.Size &&
        CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, token).ConfigureAwait(false), release.Sha256);

    private static InvalidDataException BadDownload() => new("Größe oder Prüfsumme der APK weicht vom veröffentlichten Update ab.");

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) &&
        child.ValueKind == JsonValueKind.String ? child.GetString() : null;
}
