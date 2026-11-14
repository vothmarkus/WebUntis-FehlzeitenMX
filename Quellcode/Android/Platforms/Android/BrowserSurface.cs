using Android.Widget;
using Microsoft.Maui.Handlers;

namespace WebUntisAuswertung.Mobile;

internal sealed class BrowserSurface : View
{
    internal MobileWebView? Browser { get; set; }
}

internal sealed class BrowserSurfaceHandler() : ViewHandler<BrowserSurface, FrameLayout>(Mapper)
{
    private static readonly PropertyMapper<BrowserSurface, BrowserSurfaceHandler> Mapper = new(ViewMapper);

    protected override FrameLayout CreatePlatformView()
    {
        var frame = new FrameLayout(Context);
        VirtualView.Browser = new MobileWebView(frame);
        return frame;
    }

    protected override void DisconnectHandler(FrameLayout platformView)
    {
        VirtualView.Browser?.Dispose();
        VirtualView.Browser = null;
        base.DisconnectHandler(platformView);
    }
}
