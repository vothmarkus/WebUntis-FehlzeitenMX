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
        private enum CardGesture { None, Pending, Horizontal }

        private const float HorizontalBias = 1.35f;
        private readonly float scale = context.Resources?.DisplayMetrics?.Density ?? 1;
        private readonly int slop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 8;
        private CancellationTokenSource? press;
        private CardGesture cardGesture;
        private VelocityTracker? velocity;
        private float x;
        private float y;

        public override bool DispatchTouchEvent(MotionEvent? motion)
        {
            if (motion == null) return false;
            switch (motion.ActionMasked)
            {
                case MotionEventActions.Down:
                    CancelPress();
                    EndCardGesture();
                    x = motion.RawX;
                    y = motion.RawY;
                    var position = new Point(motion.GetX() / scale, motion.GetY() / scale);
                    if (canvas.BeginPress(position) && Enabled)
                    {
                        cardGesture = CardGesture.Pending;
                        velocity = VelocityTracker.Obtain();
                        velocity?.AddMovement(motion);
                    }
                    if (Enabled && canvas.CurrentMatrix is { Days.Length: > 0, Students.Length: > 0 } original)
                    {
                        press = new CancellationTokenSource();
                        _ = WaitForHoldAsync(original, position, press);
                    }
                    break;
                case MotionEventActions.Move when motion.PointerCount == 1:
                    var dx = motion.RawX - x;
                    var dy = motion.RawY - y;
                    if (Math.Abs(dx) > slop || Math.Abs(dy) > slop) CancelPress();
                    ContinueCardGesture(motion, dx, dy);
                    break;
                case MotionEventActions.Up:
                    CancelPress();
                    var dragged = cardGesture == CardGesture.Horizontal;
                    var dispatched = base.DispatchTouchEvent(motion);
                    EndCardGesture(motion);
                    return dispatched || dragged;
                default:
                    CancelPress();
                    EndCardGesture();
                    break;
            }
            return base.DispatchTouchEvent(motion) || cardGesture != CardGesture.None;
        }

        protected override void OnDetachedFromWindow()
        {
            CancelPress();
            EndCardGesture();
            base.OnDetachedFromWindow();
        }

        private void ContinueCardGesture(MotionEvent motion, float dx, float dy)
        {
            if (cardGesture == CardGesture.None) return;
            velocity?.AddMovement(motion);
            if (cardGesture == CardGesture.Pending)
            {
                if (Math.Abs(dy) > slop && Math.Abs(dy) * HorizontalBias >= Math.Abs(dx))
                {
                    EndCardGesture();
                    return;
                }
                if (Math.Abs(dx) <= slop || Math.Abs(dx) < Math.Abs(dy) * HorizontalBias) return;
                cardGesture = CardGesture.Horizontal;
            }
            canvas.DragCard(dx / scale);
        }

        private void EndCardGesture(MotionEvent? up = null)
        {
            var dragged = cardGesture == CardGesture.Horizontal;
            cardGesture = CardGesture.None;
            canvas.EndCardPress();
            int? direction = null;
            if (dragged && up != null)
            {
                velocity?.AddMovement(up);
                velocity?.ComputeCurrentVelocity(1000);
                var dx = up.RawX - x;
                var dy = up.RawY - y;
                var speed = velocity?.XVelocity ?? 0;
                var distance = Math.Abs(dx);
                var threshold = Math.Clamp(Width * 0.22f, 48 * scale, 120 * scale);
                var flick = distance >= 28 * scale && float.IsFinite(speed) &&
                    Math.Abs(speed) >= 600 * scale && Math.Sign(speed) == Math.Sign(dx);
                direction = distance >= Math.Abs(dy) * HorizontalBias && (distance >= threshold || flick)
                    ? (dx < 0 ? 1 : -1) : 0;
            }
            velocity?.Recycle();
            velocity?.Dispose();
            velocity = null;
            if (dragged) canvas.EndCardDrag(direction);
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
