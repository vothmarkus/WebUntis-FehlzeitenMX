using Android.Content;
using Android.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace WebUntisAuswertung.Mobile;

internal sealed class SwipeGrid : Grid
{
    public SwipeDirection Directions { get; init; }
    public double Threshold { get; init; } = 40;
    public Func<Point, bool>? CanStartSwipe { get; init; }
    public event Action<SwipeDirection>? Swiped;

    internal void SendSwipe(SwipeDirection direction) => Swiped?.Invoke(direction);
}

internal sealed class SwipeGridHandler : LayoutHandler
{
    protected override LayoutViewGroup CreatePlatformView()
    {
        var view = new SwipeLayout(Context, (SwipeGrid)VirtualView) { CrossPlatformLayout = VirtualView };
        view.SetClipChildren(false);
        return view;
    }

    private sealed class SwipeLayout(Context context, SwipeGrid grid) : LayoutViewGroup(context)
    {
        private const double AxisBias = 1.25;
        private readonly float density = context.Resources?.DisplayMetrics?.Density ?? 1;
        private Point? start;

        public override bool DispatchTouchEvent(MotionEvent? motion)
        {
            if (motion == null) return false;
            switch (motion.ActionMasked)
            {
                case MotionEventActions.Down:
                    var point = new Point(motion.GetX() / density, motion.GetY() / density);
                    start = grid.IsEnabled && !grid.InputTransparent && grid.CanStartSwipe?.Invoke(point) != false
                        ? ScreenPosition(motion) : null;
                    break;
                case MotionEventActions.PointerDown:
                case MotionEventActions.Cancel:
                    start = null;
                    break;
                case MotionEventActions.Up:
                    var origin = start;
                    start = null;
                    if (origin is { } down && GetDirection(down, ScreenPosition(motion)) is { } direction &&
                        (grid.Directions & direction) != 0)
                    {
                        // A tap and ordinary scrolling still reach the child. A completed swipe cancels its tap.
                        using var cancel = MotionEvent.Obtain(motion);
                        if (cancel != null)
                        {
                            cancel.Action = MotionEventActions.Cancel;
                            base.DispatchTouchEvent(cancel);
                        }
                        grid.Dispatcher.Dispatch(() =>
                        {
                            if (IsAttachedToWindow && grid.IsEnabled && !grid.InputTransparent) grid.SendSwipe(direction);
                        });
                        return true;
                    }
                    break;
            }
            return base.DispatchTouchEvent(motion) || start.HasValue;
        }

        private Point ScreenPosition(MotionEvent motion) => new(motion.RawX / density, motion.RawY / density);

        private SwipeDirection? GetDirection(Point down, Point up)
        {
            var dx = up.X - down.X;
            var dy = up.Y - down.Y;
            var horizontal = Math.Abs(dx);
            var vertical = Math.Abs(dy);
            if (vertical >= grid.Threshold && vertical >= horizontal * AxisBias)
                return dy > 0 ? SwipeDirection.Down : SwipeDirection.Up;
            if (horizontal >= grid.Threshold && horizontal >= vertical * AxisBias)
                return dx > 0 ? SwipeDirection.Right : SwipeDirection.Left;
            return null;
        }
    }
}
