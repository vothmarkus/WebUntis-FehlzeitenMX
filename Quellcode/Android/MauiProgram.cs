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
        .ConfigureMauiHandlers(handlers => handlers.AddHandler<BrowserSurface, BrowserSurfaceHandler>())
        .Build();
}

internal sealed class MobileApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new MatrixPage();
        var window = new Window(page);
        window.Stopped += (_, _) => page.PauseRefresh();
        window.Resumed += (_, _) => page.ResumeRefresh();
        return window;
    }
}
