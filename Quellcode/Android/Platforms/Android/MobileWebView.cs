using Message = Android.OS.Message;
using SystemClock = Android.OS.SystemClock;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AndroidX.WebKit;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebUntisAuswertung.Core;
using NativeWebView = Android.Webkit.WebView;

namespace WebUntisAuswertung.Mobile;

internal sealed class MobileWebView : IDisposable
{
    private static readonly int[] EditorFitDelays = [0, 140, 320];
    private readonly FrameLayout host;
    private readonly BrowserClient client;
    private readonly BrowserChrome chrome;
    private readonly Dictionary<NativeWebView, DownloadListener> listeners = [];
    private readonly HashSet<NativeWebView> windows = [];
    private readonly NativeWebView reports;
    private readonly EditorListener? editorListener;
    private NativeWebView? loginPopup;
    private bool disposed;
    private bool rendererGone;
    private bool editing;

    public MobileWebView(FrameLayout host)
    {
        this.host = host;
        client = new BrowserClient(this);
        chrome = new BrowserChrome(this);
        reports = AddView();
        if (WebViewFeature.IsFeatureSupported(WebViewFeature.WebMessageListener))
        {
            editorListener = new EditorListener(this);
            WebViewCompat.AddWebMessageListener(reports, "FehlzeitenMxEditor", new HashSet<string> { WebUntisReports.Origin },
                editorListener);
        }
        SetLoginVisible(false);
    }

    public string? Address => reports.Url;
    public string? LoginAddress => (loginPopup ?? reports).Url;
    public int MainStatus { get; private set; }
    public bool Navigating { get; private set; }
    public long NavigationRevision { get; private set; }
    public bool ShowingLogin { get; private set; }
    public bool RendererGone => rendererGone;
    public string UserAgent => reports.Settings.UserAgentString ?? "";
    public event Action<string?>? LoginAddressChanged;
    public event Action<string>? EditorMessage;
    public Export? PendingExport { get; set; }

    public void Go(string address)
    {
        if (rendererGone) throw new InvalidOperationException("Android WebView wurde beendet. Starte die App erneut.");
        MainStatus = 0;
        Navigating = true;
        NavigationRevision++;
        reports.StopLoading();
        reports.LoadUrl(address);
    }

    public void SetEditing(bool value) => editing = value;

    public void SetLoginVisible(bool visible)
    {
        ShowingLogin = visible;
        host.ImportantForAccessibility = visible ? ImportantForAccessibility.Auto : ImportantForAccessibility.NoHideDescendants;
        host.DescendantFocusability = visible ? DescendantFocusability.AfterDescendants : DescendantFocusability.BlockDescendants;
        if (visible)
        {
            if (!editing) (loginPopup ?? reports).RequestFocus();
        }
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

    public async Task FitEditorOnOpenAsync(CancellationToken token)
    {
        if (!editing || disposed || rendererGone || !WebUntisReports.IsSchoolAddress(Address)) return;
        var revision = NavigationRevision;
        var touched = false;
        const string fit = """
            (() => {
                const editor = document.documentElement.fehlzeitenMxAbsenceEditor;
                if (editor?.closed || !editor?.dialog?.isConnected || !window.visualViewport) return null;
                return window.visualViewport.width / editor.dialog.getBoundingClientRect().width;
            })()
            """;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(1));
        var startupToken = startup.Token;
        reports.Touch += OnTouch;
        try
        {
            // Wait through the first layout pass after the editor toolbar has resized the WebView.
            var layout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reports.PostOnAnimation(() =>
            {
                if (CanFit()) reports.PostOnAnimation(() => layout.TrySetResult());
                else layout.TrySetResult();
            });
            await Task.WhenAny(layout.Task, Task.Delay(120, startupToken));

            // WebView can apply the new viewport scale after the first layout. Recheck during opening only.
            foreach (var delay in EditorFitDelays)
            {
                if (!CanFit()) return;
                await Task.Delay(delay, startupToken);
                if (!CanFit()) return;
                var json = await EvaluateAsync(fit, startupToken);
                if (!CanFit()) return;
                if (JsonNode.Parse(json) is JsonValue value && value.TryGetValue<double>(out var factor) &&
                    double.IsFinite(factor) && factor > 0 && Math.Abs(factor - 1) > 0.01)
                    reports.ZoomBy((float)Math.Clamp(factor, 0.01, 100));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or ObjectDisposedException or
            System.Text.Json.JsonException or Java.Lang.Exception) { }
        finally { if (!disposed) reports.Touch -= OnTouch; }

        bool CanFit() => !startupToken.IsCancellationRequested && !touched && editing && !disposed && !rendererGone &&
            NavigationRevision == revision;

        void OnTouch(object? sender, global::Android.Views.View.TouchEventArgs args)
        {
            touched = true;
            args.Handled = false;
        }
    }

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

    private static async Task ResetProfileAsync()
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

    public static void PersistProfile() => CookieManager.Instance?.Flush();

    public static SavedWebUntisCookie[] CaptureSession()
    {
        var manager = CookieManager.Instance;
        if (manager == null || !WebViewFeature.IsFeatureSupported(WebViewFeature.GetCookieInfo)) return [];
        const string root = WebUntisReports.Origin;
        string[] pages = [root + "/", root + "/WebUntis/", root + "/WebUntis/embedded.do", root + "/WebUntis/reports.do",
            WebUntisReports.Address, WebUntisReports.Absences, WebUntisReports.AbsenceTimes, WebUntisReports.Timetable];
        var cookies = new Dictionary<(string, string, string), SavedWebUntisCookie>();
        var now = DateTimeOffset.UtcNow;
        foreach (var page in pages)
            foreach (var header in CookieManagerCompat.GetCookieInfo(manager, page) ?? [])
                if (SavedWebUntisCookie.Read(header, now) is { } cookie)
                    cookies[(cookie.Name, cookie.Domain, cookie.Path)] = cookie;
        return [.. cookies.Values];
    }

    public static async Task RestoreMissingCookiesAsync(SavedWebUntisCookie[] saved, CancellationToken token)
    {
        var manager = CookieManager.Instance;
        if (manager == null) return;
        var now = DateTimeOffset.UtcNow;
        var canReadScopes = WebViewFeature.IsFeatureSupported(WebViewFeature.GetCookieInfo);
        var current = CaptureSession().Select(item => (item.Name, item.Domain, item.Path)).ToHashSet();
        // Set shorter paths first so the restored jar keeps all distinct scopes.
        foreach (var cookie in saved.Select(item => SavedWebUntisCookie.Read(item.Header, now)).OfType<SavedWebUntisCookie>()
                     .OrderBy(item => item.Path.Length))
        {
            token.ThrowIfCancellationRequested();
            if (!current.Add((cookie.Name, cookie.Domain, cookie.Path))) continue;
            if (!canReadScopes && manager.GetCookie(WebUntisReports.Origin + cookie.Path)?.Split(';')
                .Any(pair => pair.TrimStart().StartsWith(cookie.Name + "=", StringComparison.Ordinal)) == true) continue;
            using var result = new JavascriptResult();
            manager.SetCookie(WebUntisReports.Origin + "/", cookie.Header, result);
            await result.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
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
        if (editorListener != null)
        {
            if (!rendererGone) WebViewCompat.RemoveWebMessageListener(reports, "FehlzeitenMxEditor");
            editorListener.Dispose();
        }
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
        return !editing && export == null && (ReferenceEquals(view, reports) || ReferenceEquals(view, loginPopup));
    }

    private void OnNavigation(NativeWebView view, string? address, bool finished)
    {
        if (ReferenceEquals(view, reports))
        {
            Navigating = !finished;
            if (!finished)
            {
                MainStatus = 0;
                NavigationRevision++;
            }
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
            if (view == null || resultMsg?.Obj is not NativeWebView.WebViewTransport transport || owner.disposed || owner.editing ||
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
            if (owner.ShowingLogin || owner.editing) return false;
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
            if (owner.ShowingLogin || owner.editing) return false;
            result?.Cancel();
            return true;
        }
    }

    private sealed class EditorListener(MobileWebView owner) : Java.Lang.Object, WebViewCompat.IWebMessageListener
    {
        public void OnPostMessage(NativeWebView? view, WebMessageCompat? message, global::Android.Net.Uri? sourceOrigin,
            bool isMainFrame, JavaScriptReplyProxy? replyProxy)
        {
            if (owner.editing && isMainFrame && sourceOrigin?.ToString() == WebUntisReports.Origin &&
                message?.Type == WebMessageCompat.TypeString && message.Data is { Length: <= 256 } data)
                owner.EditorMessage?.Invoke(data);
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
