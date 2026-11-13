using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WebUntisAuswertung;

internal static class GitHubUpdates
{
    private const string Repository = "vothmarkus/WebUntis-FehlzeitenMX";
    private const string RepositoryUrl = "https://github.com/" + Repository;
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        MaxResponseContentBufferSize = 1024 * 1024,
        DefaultRequestHeaders = { { "User-Agent", UpdateRelease.Product } }
    };

    public static async Task<(Version Version, UpdateRelease? Setup)?> CheckAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await Client.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.Contains("X-RateLimit-Reset")))
                throw new IOException("GitHub begrenzt gerade die Zahl der Abfragen. Versuche es später erneut.");
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false),
                cancellationToken: deadline.Token).ConfigureAwait(false);
            return ReadRelease(document.RootElement);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub hat auf die Versionsprüfung nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException)
        {
            throw new IOException("Die Versionsprüfung konnte GitHub nicht erreichen. Versuche es später erneut.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Die Antwort von GitHub konnte nicht als Release gelesen werden.");
        }
    }

    internal static (Version Version, UpdateRelease? Setup) ReadRelease(JsonElement root)
    {
        var tag = ReadString(root, "tag_name");
        if (tag is null || !Version.TryParse(tag.AsSpan().TrimStart('v'), out var parsed) ||
            parsed.Major > ushort.MaxValue || parsed.Minor > ushort.MaxValue ||
            parsed.Build > ushort.MaxValue || parsed.Revision > ushort.MaxValue ||
            (tag != parsed.ToString() && tag != "v" + parsed))
            throw new InvalidDataException("GitHub meldet eine ungültige Versionsnummer.");

        var version = UpdateRelease.Normalize(parsed);
        var fileName = UpdateRelease.FileNameFor(version);
        var expectedUrl = $"{ReleasesUrl}/download/{tag}/{fileName}";
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (ReadString(asset, "name") != fileName ||
                    ReadString(asset, "state") != "uploaded" ||
                    ReadString(asset, "browser_download_url") != expectedUrl ||
                    !asset.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number ||
                    !sizeValue.TryGetInt64(out var size) || size is <= 0 or > UpdateRelease.MaxSetupBytes ||
                    ReadString(asset, "digest") is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                    !digest[7..].All(Uri.IsHexDigit))
                    continue;
                return (version, new UpdateRelease(version, size, Convert.FromHexString(digest[7..]), new Uri(expectedUrl)));
            }
        }
        return (version, null);
    }

    public static async Task<string> DownloadAsync(UpdateRelease release, string folder, IProgress<int> progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        var downloadToken = deadline.Token;
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, release.SetupFileName);
        var partial = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var response = await OpenAssetAsync(release.Url, downloadToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw WrongSize();
            using var content = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false);
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, useAsync: true))
            {
                var buffer = new byte[131072];
                var previous = 0;
                int count;
                while ((count = await content.ReadAsync(buffer.AsMemory(), downloadToken).ConfigureAwait(false)) > 0)
                {
                    if (file.Position + count > release.Size) throw WrongSize();
                    await file.WriteAsync(buffer.AsMemory(0, count), downloadToken).ConfigureAwait(false);
                    var percent = (int)(file.Position * 100 / release.Size);
                    if (percent == previous) continue;
                    progress.Report(percent);
                    previous = percent;
                }
                file.Position = 0;
                await release.VerifySetupHashAsync(file, downloadToken).ConfigureAwait(false);
            }
            downloadToken.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: true);
            return destination;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Das Setup konnte nicht innerhalb von acht Minuten heruntergeladen werden.");
        }
        catch (HttpRequestException)
        {
            throw new IOException("GitHub konnte das Setup nicht liefern. Prüfe die Version erneut und wiederhole den Download.");
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
            var location = response.Headers.Location;
            response.Dispose();
            if (location is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } ||
                location.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("GitHub hat auf eine unerwartete Downloadadresse verwiesen.");
            response = await Client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException($"GitHub meldet HTTP {(int)status}.");
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) &&
        child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    private static InvalidDataException WrongSize() => new("Der Download hat eine andere Größe als das veröffentlichte Setup.");
}
