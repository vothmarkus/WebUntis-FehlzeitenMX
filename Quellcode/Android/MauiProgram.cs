using Microsoft.Maui.Handlers;
using AndroidSettings = Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;

namespace WebUntisAuswertung.Mobile;

internal static class MauiProgram
{
    static MauiProgram()
    {
        // MAUI's Android batch mapper can convert an unset minimum size (NaN) to int.MaxValue.
        ViewHandler.ViewMapper.AppendToMapping("_InitializeBatchedProperties", static (handler, view) =>
        {
            if (handler.PlatformView is not global::Android.Views.View platformView) return;
            if (double.IsNaN(view.MinimumWidth) && platformView.MinimumWidth == int.MaxValue)
                platformView.SetMinimumWidth(0);
            if (double.IsNaN(view.MinimumHeight) && platformView.MinimumHeight == int.MaxValue)
                platformView.SetMinimumHeight(0);
        });
    }

    public static MauiApp CreateMauiApp() => MauiApp.CreateBuilder()
        .UseMauiApp<MobileApp>()
        .ConfigureMauiHandlers(handlers =>
        {
            handlers.AddHandler<BrowserSurface, BrowserSurfaceHandler>();
            handlers.AddHandler<MatrixCanvas, MatrixCanvasHandler>();
            handlers.AddHandler<SwipeGrid, SwipeGridHandler>();
        })
        .Build();
}

internal sealed class MobileApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var lockPage = new AppLockPage();
        var window = new Window(lockPage);
        AndroidSettings.Application.SetWindowSoftInputModeAdjust(window, AndroidSettings.WindowSoftInputModeAdjust.Resize);
        var active = false;
        var open = false;
        var prompting = false;
        var autoPrompt = true;
        Task locking = Task.CompletedTask;
        AutomaticLoginPage? suspendedLogin = null;

        lockPage.Gate.UnlockRequested += RequestUnlock;
        window.Activated += (_, _) => Resume();
        window.Resumed += (_, _) => Resume();
        window.Stopped += (_, _) =>
        {
            active = false;
            if (suspendedLogin != null || !locking.IsCompleted || window.Page is not MatrixPage page) return;
            open = false;
            autoPrompt = true;
            if (page.Navigation.ModalStack.LastOrDefault() is AutomaticLoginPage login && login.TryLock())
            {
                suspendedLogin = login;
                login.Gate.UnlockRequested += RequestUnlock;
                locking = page.SuspendForLoginAsync();
            }
            else locking = SealAsync(page);
        };
        return window;

        AppLockView? CurrentGate() => suspendedLogin?.Gate ?? (window.Page == lockPage ? lockPage.Gate : null);

        void RequestUnlock() => _ = UnlockAsync();

        void Resume()
        {
            active = true;
            if (CurrentGate() == null) return;
            if (open) Enter();
            else if (autoPrompt) RequestUnlock();
        }

        async Task UnlockAsync()
        {
            if (prompting || !active || CurrentGate() is not { } gate) return;
            prompting = true;
            autoPrompt = false;
            try
            {
                await locking;
                await Task.Yield();
                if (!active) return;
                open = await gate.AuthenticateAsync();
                if (open && active) Enter();
            }
            finally { prompting = false; }
        }

        void Enter()
        {
            open = false;
            if (suspendedLogin is { } login && window.Page is MatrixPage page)
            {
                suspendedLogin = null;
                login.Gate.UnlockRequested -= RequestUnlock;
                page.ResumeAfterLoginUnlock();
                login.Unlock();
                return;
            }
            window.Page = new MatrixPage();
        }

        async Task SealAsync(MatrixPage page)
        {
            await page.SealAsync();
            window.Page = lockPage;
            if (active && autoPrompt) RequestUnlock();
        }
    }
}
