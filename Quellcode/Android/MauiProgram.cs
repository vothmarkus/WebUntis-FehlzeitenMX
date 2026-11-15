using Microsoft.Maui.Handlers;

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
        })
        .Build();
}

internal sealed class MobileApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var gate = new AppLockPage();
        var window = new Window(gate);
        var active = false;
        var open = false;
        var prompting = false;
        var autoPrompt = true;
        Task sealing = Task.CompletedTask;

        gate.UnlockRequested += async () => await UnlockAsync();
        window.Activated += (_, _) => Resume();
        window.Resumed += (_, _) => Resume();
        window.Stopped += (_, _) =>
        {
            active = false;
            if (!sealing.IsCompleted || window.Page is not MatrixPage page) return;
            open = false;
            autoPrompt = true;
            sealing = SealAsync(page);
        };
        return window;

        void Resume()
        {
            active = true;
            if (open && window.Page == gate) Enter();
            else if (autoPrompt && window.Page == gate) _ = UnlockAsync();
        }

        async Task UnlockAsync()
        {
            if (prompting || !active || window.Page != gate) return;
            prompting = true;
            autoPrompt = false;
            try
            {
                await sealing;
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
            window.Page = new MatrixPage();
        }

        async Task SealAsync(MatrixPage page)
        {
            await page.SealAsync();
            window.Page = gate;
            if (active && autoPrompt) _ = UnlockAsync();
        }
    }
}
