using System.Net;
using System.Net.Http.Headers;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung;

internal static class ReportTransfer
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    });

    public static async Task<byte[]?> FetchAsync(Uri source, Uri school, string cookies, string userAgent, CancellationToken token)
    {
        if (source.Scheme != Uri.UriSchemeHttps || source.Host != school.Host || source.Port != school.Port || source.UserInfo.Length > 0)
            throw new InvalidOperationException("Die CSV-Adresse gehört nicht zum WebUntis-Schulserver.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (cookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (userAgent.Length > 0) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 401 or 403 || status is >= 300 and < 400)
                throw new InvalidOperationException("WebUntis hat den CSV-Abruf nicht freigegeben. Prüfe die Anmeldung.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Der Schulserver meldet beim CSV-Abruf HTTP {status}.");
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is "text/html" or "application/json" or "application/pdf")
                throw new InvalidOperationException("WebUntis hat auf den CSV-Link mit einem anderen Dateiformat geantwortet.");
            if (response.Content.Headers.ContentLength > CsvReport.MaxFileBytes)
                throw new InvalidDataException("Der Bericht ist größer als 25 MB.");
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await ReadAsync(body, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("WebUntis hat die CSV nicht rechtzeitig übertragen.");
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException("Die CSV-Verbindung zu WebUntis wurde unterbrochen.", error);
        }
    }

    public static async Task<byte[]?> ReadAsync(Stream source, CancellationToken token)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[32768];
        while (true)
        {
            var length = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (length == 0) return destination.Length == 0 ? null : destination.ToArray();
            if (destination.Length + length > CsvReport.MaxFileBytes)
                throw new InvalidDataException("Der Bericht ist größer als 25 MB.");
            destination.Write(buffer, 0, length);
        }
    }
}
