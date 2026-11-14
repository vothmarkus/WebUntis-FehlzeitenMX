namespace WebUntisAuswertung.Mobile;

internal static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp.CreateBuilder()
        .UseMauiApp<MobileApp>()
        .ConfigureMauiHandlers(handlers => handlers.AddHandler<BrowserSurface, BrowserSurfaceHandler>())
        .Build();
}

internal sealed class MobileApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new(new MatrixPage());
}
