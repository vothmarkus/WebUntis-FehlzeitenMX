using Android.Content;
using Android.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixCanvasHandler : GraphicsViewHandler
{
    protected override PlatformTouchGraphicsView CreatePlatformView() =>
        new PressView(Context, (MatrixCanvas)VirtualView);

    private sealed class PressView(Context context, MatrixCanvas canvas) : PlatformTouchGraphicsView(context)
    {
        private readonly float scale = context.Resources?.DisplayMetrics?.Density ?? 1;
        private readonly int slop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 8;
        private CancellationTokenSource? press;
        private float x;
        private float y;

        public override bool DispatchTouchEvent(MotionEvent? motion)
        {
            if (motion == null) return false;
            switch (motion.ActionMasked)
            {
                case MotionEventActions.Down:
                    CancelPress();
                    canvas.BeginPress();
                    x = motion.GetX();
                    y = motion.GetY();
                    if (Enabled && canvas.CurrentMatrix is { } original)
                    {
                        press = new CancellationTokenSource();
                        _ = WaitForHoldAsync(original, new Point(x / scale, y / scale), press);
                    }
                    break;
                case MotionEventActions.Move:
                    if (motion.PointerCount != 1 || Math.Abs(motion.GetX() - x) > slop ||
                        Math.Abs(motion.GetY() - y) > slop) CancelPress();
                    break;
                default:
                    CancelPress();
                    break;
            }
            return base.DispatchTouchEvent(motion);
        }

        protected override void OnDetachedFromWindow()
        {
            CancelPress();
            base.OnDetachedFromWindow();
        }

        private void CancelPress()
        {
            var pending = press;
            press = null;
            pending?.Cancel();
        }

        private async Task WaitForHoldAsync(AttendanceMatrix original, Point position, CancellationTokenSource pending)
        {
            try
            {
                await Task.Delay(ViewConfiguration.LongPressTimeout, pending.Token);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (ReferenceEquals(press, pending)) canvas.Hold(position, original);
                });
            }
            catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(press, pending)) press = null;
                pending.Dispose();
            }
        }
    }
}
