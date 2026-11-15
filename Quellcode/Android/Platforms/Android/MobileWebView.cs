using Message = Android.OS.Message;
using SystemClock = Android.OS.SystemClock;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebUntisAuswertung.Core;
using NativeWebView = Android.Webkit.WebView;

namespace WebUntisAuswertung.Mobile;

internal sealed class MobileWebView : IDisposable
{
    private readonly FrameLayout host;
    private readonly BrowserClient client;
    private readonly BrowserChrome chrome;
    private readonly Dictionary<NativeWebView, DownloadListener> listeners = [];
    private readonly HashSet<NativeWebView> windows = [];
    private readonly NativeWebView reports;
    private NativeWebView? loginPopup;
    private bool disposed;
    private bool rendererGone;

    public MobileWebView(FrameLayout host)
    {
        this.host = host;
        client = new BrowserClient(this);
        chrome = new BrowserChrome(this);
        reports = AddView();
        SetLoginVisible(false);
    }

    public string? Address => reports.Url;
    public string? LoginAddress => (loginPopup ?? reports).Url;
    public int MainStatus { get; private set; }
    public bool Navigating { get; private set; }
    public bool ShowingLogin { get; private set; }
    public bool RendererGone => rendererGone;
    public string UserAgent => reports.Settings.UserAgentString ?? "";
    public event Action<string?>? LoginAddressChanged;
    public Export? PendingExport { get; set; }

    public void Go(string address)
    {
        if (rendererGone) throw new InvalidOperationException("Android WebView wurde beendet. Starte die App erneut.");
        MainStatus = 0;
        Navigating = true;
        reports.StopLoading();
        reports.LoadUrl(address);
    }

    public void SetLoginVisible(bool visible)
    {
        ShowingLogin = visible;
        host.ImportantForAccessibility = visible ? ImportantForAccessibility.Auto : ImportantForAccessibility.NoHideDescendants;
        host.DescendantFocusability = visible ? DescendantFocusability.AfterDescendants : DescendantFocusability.BlockDescendants;
        if (visible) (loginPopup ?? reports).RequestFocus();
        else
        {
            reports.ClearFocus();
            CloseLoginPopup();
        }
    }

    public bool GoBack()
    {
        var view = loginPopup ?? reports;
        if (!view.CanGoBack())
        {
            if (loginPopup == null) return false;
            CloseLoginPopup();
        }
        else view.GoBack();
        return true;
    }

    public Task<string> EvaluateAsync(string script, CancellationToken token) => EvaluateAsync(reports, script, token);

    public async Task<bool> TapAsync(double x, double y, CancellationToken token)
    {
        var json = await EvaluateAsync("({width: visualViewport.width, x: visualViewport.offsetLeft, y: visualViewport.offsetTop})", token);
        if (JsonNode.Parse(json) is not JsonObject viewport) return false;
        var width = viewport["width"]?.GetValue<double>() ?? 0;
        if (width <= 0) return false;
        var scale = reports.Width / width;
        var px = (float)((x - (viewport["x"]?.GetValue<double>() ?? 0)) * scale);
        var py = (float)((y - (viewport["y"]?.GetValue<double>() ?? 0)) * scale);
        if (px < 0 || py < 0 || px >= reports.Width || py >= reports.Height) return false;
        token.ThrowIfCancellationRequested();
        var now = SystemClock.UptimeMillis();
        using var down = MotionEvent.Obtain(now, now, MotionEventActions.Down, px, py, MetaKeyStates.None);
        using var up = MotionEvent.Obtain(now, now, MotionEventActions.Up, px, py, MetaKeyStates.None);
        if (down == null || up == null) return false;
        reports.DispatchTouchEvent(down);
        reports.DispatchTouchEvent(up);
        return true;
    }

    public static async Task ResetProfileAsync()
    {
        using var done = new JavascriptResult();
        var cookies = CookieManager.Instance;
        if (cookies != null)
        {
            cookies.RemoveAllCookies(done);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(8));
            cookies.Flush();
        }
        WebStorage.Instance?.DeleteAllData();
    }

    public async Task SignOutAsync()
    {
        CloseLoginPopup();
        Go("about:blank");
        reports.ClearHistory();
        reports.ClearCache(true);
        await ResetProfileAsync();
    }

    internal static async Task<string> EvaluateAsync(NativeWebView view, string script, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var result = new JavascriptResult();
        view.EvaluateJavascript(script, result);
        return await result.Task.WaitAsync(TimeSpan.FromSeconds(6), token);
    }

    private NativeWebView AddView()
    {
        var web = new NativeWebView(host.Context!);
        web.Settings.JavaScriptEnabled = true;
        web.Settings.DomStorageEnabled = true;
        web.Settings.CacheMode = CacheModes.NoCache;
        web.Settings.AllowFileAccess = false;
        web.Settings.AllowContentAccess = false;
        web.Settings.MixedContentMode = MixedContentHandling.NeverAllow;
        web.Settings.SetSupportMultipleWindows(true);
        web.Settings.SaveFormData = false;
        web.Settings.UseWideViewPort = true;
        web.Settings.LoadWithOverviewMode = true;
        web.Settings.SetSupportZoom(true);
        web.Settings.BuiltInZoomControls = true;
        web.Settings.DisplayZoomControls = false;
        web.SetWebViewClient(client);
        web.SetWebChromeClient(chrome);
        var listener = new DownloadListener(this, web);
        listeners.Add(web, listener);
        web.SetDownloadListener(listener);
        CookieManager.Instance?.SetAcceptThirdPartyCookies(web, true);
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            web.ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants;
        web.SetBackgroundColor(global::Android.Graphics.Color.White);
        host.AddView(web, new FrameLayout.LayoutParams(-1, -1));
        return web;
    }

    private void CloseLoginPopup()
    {
        if (loginPopup == null) return;
        RemoveView(loginPopup);
        loginPopup = null;
        LoginAddressChanged?.Invoke(LoginAddress);
    }

    public void CloseReportWindows(Export export)
    {
        foreach (var view in export.Windows.ToArray()) RemoveView(view);
        export.Windows.Clear();
    }

    private void RemoveView(NativeWebView view)
    {
        windows.Remove(view);
        if (!listeners.Remove(view, out var listener)) return;
        view.SetDownloadListener(null);
        view.StopLoading();
        host.RemoveView(view);
        view.Destroy();
        view.Dispose();
        listener.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var popup in windows.ToArray()) RemoveView(popup);
        RemoveView(reports);
        chrome.Dispose();
        client.Dispose();
    }

    private bool HandleNavigation(NativeWebView view, string? address)
    {
        if (address is "about:blank") return true;
        var export = PendingExport;
        if (export != null && address?.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!WebUntisReports.IsSchoolAddress(address)) return false;
            export.Download.TrySetResult(new Download(view, address, view.Settings.UserAgentString ?? ""));
            return false;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            return false;
        if (WebUntisReports.IsSchoolAddress(address)) return true;
        return export == null && (ReferenceEquals(view, reports) || ReferenceEquals(view, loginPopup));
    }

    private void OnNavigation(NativeWebView view, string? address, bool finished)
    {
        if (ReferenceEquals(view, reports))
        {
            Navigating = !finished;
            if (!finished) MainStatus = 0;
        }
        if (ReferenceEquals(view, loginPopup ?? reports)) LoginAddressChanged?.Invoke(address);
        if (finished && PendingExport is { } export && export.Windows.Contains(view) && WebUntisReports.IsSchoolAddress(address))
            _ = InspectPopupAsync(view, export);
    }

    private async Task InspectPopupAsync(NativeWebView view, Export export)
    {
        try
        {
            var json = await EvaluateAsync(view, """
                (() => {
                    if (location.origin !== EXPECTED_ORIGIN) return null;
                    const text = (document.body?.innerText ?? "").trim();
                    if (document.querySelector('input[type="password"]')) return {kind: "login"};
                    if (text.length < 1500 && /keine berechtigung|kein zugriff|nicht berechtigt/i.test(text)) return {kind: "denied"};
                    if (text.length < 1500 && /keine\s+(daten|einträge|ergebnisse|fehlzeiten|abwesenheiten|verspätungen)|no\s+(data|results)/i.test(text))
                        return {kind: "empty"};
                    const links = [...document.querySelectorAll("a[href]")].filter(link =>
                        link.getClientRects().length && (/\.csv$/i.test(link.download) || /\.csv$/i.test(new URL(link.href).pathname)));
                    return links.length === 1 ? {url: links[0].href} : null;
                })()
                """.Replace("EXPECTED_ORIGIN", System.Text.Json.JsonSerializer.Serialize(WebUntisReports.Origin)), export.Token);
            if (PendingExport != export || JsonNode.Parse(json) is not JsonObject response) return;
            switch ((string?)response["kind"])
            {
                case "empty":
                    export.Empty = true;
                    break;
                case "login":
                    export.Error = "WebUntis verlangt eine neue Anmeldung.";
                    break;
                case "denied":
                    export.Error = "WebUntis meldet fehlende Berechtigung für diesen Bericht.";
                    break;
                default:
                    var address = (string?)response["url"];
                    if (WebUntisReports.IsSchoolAddress(address))
                        export.Download.TrySetResult(new Download(view, address!, view.Settings.UserAgentString ?? ""));
                    break;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or System.Text.Json.JsonException or ObjectDisposedException) { }
    }

    private sealed class BrowserClient(MobileWebView owner) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(NativeWebView? view, IWebResourceRequest? request) =>
            view == null || !owner.HandleNavigation(view, request?.Url?.ToString());

        public override void OnPageStarted(NativeWebView? view, string? url, global::Android.Graphics.Bitmap? favicon)
        {
            if (view != null) owner.OnNavigation(view, url, finished: false);
        }

        public override void OnPageFinished(NativeWebView? view, string? url)
        {
            if (view != null) owner.OnNavigation(view, url, finished: true);
        }

        public override void OnReceivedHttpError(NativeWebView? view, IWebResourceRequest? request, WebResourceResponse? errorResponse)
        {
            if (ReferenceEquals(view, owner.reports) && request?.IsForMainFrame == true)
                owner.MainStatus = errorResponse?.StatusCode ?? 0;
        }

        public override bool OnRenderProcessGone(NativeWebView? view, RenderProcessGoneDetail? detail)
        {
            if (owner.PendingExport is { } export)
                export.Error = "Android WebView wurde beendet. Starte die App erneut.";
            owner.rendererGone = true;
            owner.MainStatus = 503;
            owner.Navigating = false;
            owner.host.RemoveAllViews();
            return true;
        }
    }

    private sealed class BrowserChrome(MobileWebView owner) : WebChromeClient
    {
        public override bool OnCreateWindow(NativeWebView? view, bool isDialog, bool isUserGesture, Message? resultMsg)
        {
            var export = owner.PendingExport;
            if (view == null || resultMsg?.Obj is not NativeWebView.WebViewTransport transport || owner.disposed ||
                (export == null && (!owner.ShowingLogin || !isUserGesture))) return false;
            if (export == null && owner.loginPopup != null) return false;
            var popup = owner.AddView();
            owner.windows.Add(popup);
            if (export == null)
            {
                owner.loginPopup = popup;
                popup.RequestFocus();
            }
            else
            {
                export.Windows.Add(popup);
                // Keep the report editor above invisible report windows so input goes to its form.
                owner.reports.BringToFront();
            }
            transport.WebView = popup;
            resultMsg.SendToTarget();
            return true;
        }

        public override void OnCloseWindow(NativeWebView? window)
        {
            if (window == null || !owner.windows.Contains(window)) return;
            if (ReferenceEquals(window, owner.loginPopup)) owner.CloseLoginPopup();
            else if (owner.PendingExport is not { } export || !export.Windows.Contains(window)) owner.RemoveView(window);
        }

        public override bool OnJsAlert(NativeWebView? view, string? url, string? message, JsResult? result)
        {
            if (owner.ShowingLogin) return false;
            if (owner.PendingExport is { } export && WebUntisReports.IsSchoolAddress(url))
            {
                if (message != null && !Regex.IsMatch(message, "keine Berechtigung|kein Zugriff|nicht berechtigt", RegexOptions.IgnoreCase) &&
                    Regex.IsMatch(message, @"keine\s+(Daten|Einträge|Ergebnisse|Fehlzeiten|Abwesenheiten|Anwesenheiten|Verspätungen)|no\s+(data|results)", RegexOptions.IgnoreCase))
                    export.Empty = true;
                else export.Error = "WebUntis hat beim Erstellen des Berichts eine Meldung angezeigt.";
            }
            result?.Confirm();
            return true;
        }

        public override bool OnJsConfirm(NativeWebView? view, string? url, string? message, JsResult? result)
        {
            if (owner.ShowingLogin) return false;
            result?.Cancel();
            return true;
        }
    }

    private sealed class DownloadListener(MobileWebView owner, NativeWebView view) : Java.Lang.Object, IDownloadListener
    {
        public NativeWebView View { get; } = view;
        public void OnDownloadStart(string? url, string? userAgent, string? contentDisposition, string? mimeType, long contentLength)
        {
            var export = owner.PendingExport;
            if (export == null || !WebUntisReports.IsSchoolAddress(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (!WebUntisReports.IsReadyCsvLink(url) && !WebUntisReports.IsReportRoute(uri) &&
                !uri.AbsolutePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && uri.Scheme != "blob") return;
            if (contentLength > CsvReport.MaxFileBytes)
            {
                export.Error = "Der Bericht ist größer als 25 MB. Wähle einen kürzeren Zeitraum.";
                return;
            }
            if (View != owner.reports && !export.Windows.Contains(View)) return;
            export.Download.TrySetResult(new Download(View, url!, userAgent ?? ""));
        }
    }

    private sealed class JavascriptResult : Java.Lang.Object, IValueCallback
    {
        private readonly TaskCompletionSource<string> value = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> Task => value.Task;
        public void OnReceiveValue(Java.Lang.Object? result) => value.TrySetResult(result?.ToString() ?? "null");
    }

    internal sealed class Export(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<Download> Download { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<NativeWebView> Windows { get; } = [];
        public string? Error { get; set; }
        public bool Empty { get; set; }
    }

    internal sealed record Download(NativeWebView View, string Url, string UserAgent);
}
