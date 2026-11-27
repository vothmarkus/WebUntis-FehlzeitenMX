using System.Diagnostics;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Core.WebUntisReports;

namespace WebUntisAuswertung.Mobile;

internal sealed partial class AndroidReports
{
    private static readonly TimeSpan LoginConfirmationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReportOpeningTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BrowserOpenRetryDelay = TimeSpan.FromSeconds(2);
    private bool continueRequested;

    public void ContinueAfterLogin() => continueRequested = true;

    private async Task<ReportState> OpenReportsAsync(IProgress<string> progress, CancellationToken token,
        bool includeClasses = false, bool startWithLogin = false, bool allowSignIn = true)
    {
        var waiting = Stopwatch.StartNew();
        var waitingForLogin = false;
        var loginShown = false;
        var redirected = false;
        var retriedReports = false;
        var waitingForBrowser = false;
        var browserOpenAttempts = 0;
        var nextBrowserOpenAttempt = TimeSpan.Zero;
        var automaticConsidered = false;
        WebUntisLogin? automatic = null;
        WebUntisLogin? submitted = null;
        continueRequested = false;
        if (startWithLogin) await OpenLoginAsync(navigate: true);
        else
        {
            progress.Report("WebUntis-Berichte werden geöffnet …");
            if (reload || !IsPage(Address)) view.Go(Address);
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (view.RendererGone)
                throw new InvalidOperationException(MobileWebView.RendererMessage);
            var state = await ReadReportPageAsync(includeClasses, waitingForLogin, token);
            if (state?.Stage == "reports")
            {
                if (submitted != null && !loginShown && loginStore != null)
                {
                    try { await loginStore.SetLoginAttemptAsync(submitted, null, token); }
                    catch (Exception error) when (AndroidMatrixCache.StorageFailure(error)) { }
                }
                showLogin(false);
                reload = false;
                await sessionConfirmed(token);
                return state;
            }
            if (!view.Navigating && view.MainStatus >= 500)
                throw new InvalidOperationException(waitingForLogin ? "Die WebUntis-Anmeldeseite ist derzeit nicht erreichbar."
                    : "Die WebUntis-Berichte sind derzeit nicht erreichbar.");
            if (!view.Navigating && (state?.Stage == "denied" || view.MainStatus == 403 && IsSchoolAddress(view.Address)))
                throw new InvalidOperationException("WebUntis meldet fehlende Berechtigung für die Klassenbuchberichte.");

            var onLoginPage = state?.Stage is "login" or "external" || view.MainStatus == 401;
            if (!view.Navigating && state?.Stage == "browserWelcome")
            {
                if (!waitingForLogin) await OpenLoginAsync(navigate: false);
                if (!waitingForBrowser)
                {
                    waitingForBrowser = true;
                    browserOpenAttempts = 0;
                    nextBrowserOpenAttempt = TimeSpan.Zero;
                    waiting.Restart();
                }
                if (browserOpenAttempts < 2 && waiting.Elapsed >= nextBrowserOpenAttempt &&
                    state is { BrowserLoginX: { } x, BrowserLoginY: { } y })
                {
                    browserOpenAttempts++;
                    nextBrowserOpenAttempt = waiting.Elapsed + BrowserOpenRetryDelay;
                    try { await view.TapAsync(x, y, token); }
                    catch (TimeoutException) { }
                }
                if (!view.ShowingLogin && waiting.Elapsed >= LoginConfirmationTimeout)
                {
                    progress.Report("Tippe auf „Im Browser öffnen“, um die WebUntis-Anmeldung fortzusetzen.");
                    showLogin(true);
                }
            }
            else if (waitingForLogin)
            {
                if (waitingForBrowser && state != null)
                {
                    waitingForBrowser = false;
                    continueRequested = false;
                    waiting.Restart();
                }
                if (!view.Navigating && !onLoginPage && state != null && IsSchoolAddress(view.LoginAddress) &&
                    (state.Stage == "signedIn" || state.AtReports || continueRequested))
                {
                    OpenReportPage(navigate: !state.AtReports);
                }
                else
                {
                    if (!view.Navigating && onLoginPage) continueRequested = false;
                    if (!loginShown && automatic != null)
                    {
                        if (!view.Navigating && state?.Stage == "login" && submitted == null)
                            await SubmitAutomaticLoginAsync();
                        if (!loginShown && (state?.Stage == "external" || waiting.Elapsed >= LoginConfirmationTimeout))
                            ShowManualLogin();
                    }
                }
            }
            else if (!view.Navigating)
            {
                if (onLoginPage)
                    await OpenLoginAsync(navigate: state?.Stage is not ("login" or "external"));
                else if (state is { Stage: "signedIn", AtReports: false } && !redirected)
                    OpenReportPage(navigate: true);
            }

            if (!waitingForLogin && (waiting.Elapsed >= ReportOpeningTimeout || view.MainLoadFailed))
            {
                if (retriedReports)
                    throw new TimeoutException(view.MainLoadFailed ? "Die WebUntis-Berichtsseite ist gerade nicht erreichbar." :
                        state?.AtReports == true ? "Die WebUntis-Berichtsseite ist da. Das Klassenbuchformular wurde nicht geladen." :
                        "WebUntis hat die Klassenbuchberichte nicht geöffnet.");
                retriedReports = true;
                OpenReportPage(navigate: true, retry: true);
            }
            if (waitingForLogin && (view.MainLoadFailed || loginShown && !waitingForBrowser && !onLoginPage &&
                IsSchoolAddress(view.LoginAddress) && waiting.Elapsed >= ReportOpeningTimeout))
                throw new TimeoutException("Die WebUntis-Anmeldeseite ist gerade nicht erreichbar.");
            await Task.Delay(240, token);
        }

        async Task OpenLoginAsync(bool navigate)
        {
            if (!allowSignIn) throw new WebUntisSignInRequiredException();
            waitingForLogin = true;
            continueRequested = false;
            redirected = false;
            retriedReports = false;
            waitingForBrowser = false;
            waiting.Restart();
            if (!automaticConsidered && loginStore != null)
            {
                automaticConsidered = true;
                try
                {
                    if (await loginStore.ReadLoginAsync(token) is { CanRetry: true } saved)
                        automatic = saved;
                }
                catch (Exception error) when (AndroidMatrixCache.StorageFailure(error)) { }
            }
            if (navigate) view.Go(WebUntisLogin.Address);
            if (automatic == null || submitted != null) ShowManualLogin();
            else progress.Report("Die WebUntis-Anmeldung wird automatisch erneuert …");
        }

        async Task SubmitAutomaticLoginAsync()
        {
            if (automatic is not { } login || loginStore == null) return;
            try
            {
                if (!await view.TryLoginFormAsync(null, token)) return;
                var triedAt = DateTimeOffset.UtcNow;
                if (await loginStore.SetLoginAttemptAsync(login, triedAt, token))
                {
                    var pending = login with { LastAttemptAt = triedAt };
                    if (await view.TryLoginFormAsync(login, token))
                    {
                        submitted = pending;
                        waiting.Restart();
                        return;
                    }
                    await loginStore.SetLoginAttemptAsync(pending, null, token);
                }
            }
            catch (Exception error) when (error is TimeoutException || AndroidMatrixCache.StorageFailure(error)) { }
            ShowManualLogin();
        }

        void ShowManualLogin()
        {
            automatic = null;
            loginShown = true;
            progress.Report("Melde dich auf der WebUntis-Seite an.");
            showLogin(true);
        }

        void OpenReportPage(bool navigate, bool retry = false)
        {
            waitingForLogin = false;
            continueRequested = false;
            redirected = true;
            showLogin(false);
            progress.Report(retry ? "WebUntis-Berichte werden erneut geladen …" : "WebUntis-Berichte werden geöffnet …");
            if (navigate) view.Go(Address);
            waiting.Restart();
        }
    }

    private async Task<ReportState?> ReadReportPageAsync(bool includeClasses, bool mobileLogin, CancellationToken token)
    {
        if (view.Navigating) return null;
        var revision = view.NavigationRevision;
        try
        {
            var state = await CommandAsync("probe", token, includeClasses: includeClasses,
                openBrowserLogin: true, mobileLogin: mobileLogin);
            return view.Navigating || revision != view.NavigationRevision ? null : state;
        }
        catch (TimeoutException) { return null; }
    }
}
