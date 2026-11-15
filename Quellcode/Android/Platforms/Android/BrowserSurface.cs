using Android.Widget;
using Microsoft.Maui.Handlers;
using NativeContext = Android.Content.Context;
using NativeMeasureMode = Android.Views.MeasureSpecMode;

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
        var frame = new BrowserFrame(Context);
        VirtualView.Browser = new MobileWebView(frame);
        return frame;
    }

    protected override void DisconnectHandler(FrameLayout platformView)
    {
        VirtualView.Browser?.Dispose();
        VirtualView.Browser = null;
        base.DisconnectHandler(platformView);
    }

    private sealed class BrowserFrame(NativeContext context) : FrameLayout(context)
    {
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) =>
            base.OnMeasure(Fill(widthMeasureSpec), Fill(heightMeasureSpec));

        private static int Fill(int spec) => MeasureSpec.GetMode(spec) == NativeMeasureMode.AtMost
            ? MeasureSpec.MakeMeasureSpec(MeasureSpec.GetSize(spec), NativeMeasureMode.Exactly) : spec;
    }
}
