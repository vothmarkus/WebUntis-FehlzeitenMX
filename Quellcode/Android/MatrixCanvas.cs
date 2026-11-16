using System.Globalization;
using Microsoft.Maui.Graphics;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixCanvas : GraphicsView, IDrawable
{
    private const float CardSwipeEdge = 36;
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private readonly bool inDeck;
    private readonly float horizontalInset;
    private float zoom = 1;
    private float width, height, horizontal, vertical, startX, startY, panX, panY;
    private AttendanceMatrix? matrix;
    private bool atNewest;
    private bool pinching;
    private bool held;
    private bool outsideTable;
    private bool pendingCardDrag;
    private bool draggingCard;
    private (string Id, DateOnly Day)? selected;

    public MatrixCanvas(bool inDeck, float horizontalInset)
    {
        this.inDeck = inDeck;
        this.horizontalInset = horizontalInset;
        Drawable = this;
        BackgroundColor = inDeck ? Colors.Transparent : Colors.White;
        SemanticProperties.SetDescription(this, "Tagesmatrix. Tippe für Tagesdetails oder halte ein Feld für Entschuldigungen gedrückt.");
        var pan = new PanGestureRecognizer { TouchPoints = 1 };
        pan.PanUpdated += (_, args) =>
        {
            if (pinching || outsideTable || draggingCard || pendingCardDrag && args.StatusType != GestureStatus.Started) return;
            switch (args.StatusType)
            {
                case GestureStatus.Started:
                    startX = horizontal;
                    startY = vertical;
                    panX = panY = 0;
                    break;
                case GestureStatus.Running:
                    panX = (float)args.TotalX;
                    panY = (float)args.TotalY;
                    horizontal = Math.Clamp(startX - panX, 0, MaxX);
                    vertical = Math.Clamp(startY - panY, 0, MaxY);
                    Invalidate();
                    break;
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    atNewest = MaxX - horizontal < 1;
                    if (args.StatusType == GestureStatus.Completed && Math.Abs(panY) > Math.Abs(panX))
                    {
                        var distance = panY > 0 ? -panY : vertical - startY;
                        if (distance != 0) VerticalScrolled?.Invoke(distance, vertical < 1);
                    }
                    break;
            }
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, args) =>
        {
            if (!pinching && !held && !outsideTable && !draggingCard && args.GetPosition(this) is { } position) Select(position);
        };
        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += (_, args) =>
        {
            pinching = args.Status is GestureStatus.Started or GestureStatus.Running;
            if (args.Status == GestureStatus.Started) panX = panY = 0;
            if (args.Status == GestureStatus.Running)
                SetZoom(zoom * (float)args.Scale, args.ScaleOrigin);
            else if (args.Status is GestureStatus.Completed or GestureStatus.Canceled && height > 0 && MaxY < 1 &&
                matrix is { Days.Length: > 0, Students.Length: > 0 })
                FitsVerticallyAfterZoom?.Invoke();
        };
        GestureRecognizers.Add(pan);
        GestureRecognizers.Add(tap);
        GestureRecognizers.Add(pinch);
        SizeChanged += (_, _) =>
        {
            if (Width <= 0 || Height <= 0) return;
            width = Math.Max(0, (float)Width - 2 * horizontalInset);
            height = (float)Height;
            horizontal = atNewest ? MaxX : Math.Clamp(horizontal, 0, MaxX);
            vertical = Math.Clamp(vertical, 0, MaxY);
            Invalidate();
        };
    }

    public event Action<Student, DateOnly, DayCell>? DaySelected;
    public event Action<Student, DateOnly, DayCell>? AbsenceRequested;
    public event Action<float, bool>? VerticalScrolled;
    public event Action? FitsVerticallyAfterZoom;
    public event Action<float>? CardDragged;
    public event Action<int?>? CardDragEnded;
    internal AttendanceMatrix? CurrentMatrix => matrix;
    internal bool CanSwitchClass { private get; set; }
    internal float TableWidth => width;

    internal bool BeginPress(Point position)
    {
        held = false;
        draggingCard = false;
        var tableX = position.X - horizontalInset;
        outsideTable = matrix is not { Days.Length: > 0, Students.Length: > 0 } value ||
            inDeck && (tableX < 0 || tableX >= width || position.Y >= Heading + value.Students.Length * Row - vertical);
        pendingCardDrag = inDeck && CanSwitchClass &&
            (outsideTable || tableX <= CardSwipeEdge || tableX >= width - CardSwipeEdge);
        return pendingCardDrag;
    }

    internal void EndCardPress() => pendingCardDrag = false;

    internal void DragCard(float distance)
    {
        draggingCard = true;
        CardDragged?.Invoke(distance);
    }

    internal void EndCardDrag(int? direction) => CardDragEnded?.Invoke(direction);

    internal void Hold(Point position, AttendanceMatrix original)
    {
        if (ReferenceEquals(matrix, original) && !pinching && !outsideTable && !draggingCard) Select(position, longPress: true);
    }
    private float Heading => Math.Min(50 * zoom, Math.Max(24, height - 24));
    private float Row => 40 * zoom;
    private float Column => 49 * zoom;
    private float NameWidth => Math.Min(width * 0.52f, Math.Clamp(width * 0.4f, 103 * zoom, 192 * zoom));
    private float MaxX => Math.Max(0, (matrix?.Days.Length ?? 0) * Column - Math.Max(0, width - NameWidth));
    private float MaxY => Math.Max(0, (matrix?.Students.Length ?? 0) * Row - Math.Max(0, height - Heading));

    private void SetZoom(float value, Point origin)
    {
        if (!float.IsFinite(value)) return;
        value = Math.Clamp(value, 0.65f, 1.8f);
        if (Math.Abs(zoom - value) < 0.001f) return;
        var focusX = (float)Math.Clamp(origin.X * (width + 2 * horizontalInset) - horizontalInset, 0, width);
        var focusY = (float)Math.Clamp(origin.Y, 0, 1) * height;
        var day = (horizontal + Math.Max(0, focusX - NameWidth)) / Column;
        var student = (vertical + Math.Max(0, focusY - Heading)) / Row;
        zoom = value;
        horizontal = Math.Clamp(day * Column - Math.Max(0, focusX - NameWidth), 0, MaxX);
        vertical = Math.Clamp(student * Row - Math.Max(0, focusY - Heading), 0, MaxY);
        atNewest = MaxX - horizontal < 1;
        Invalidate();
    }

    public void SetMatrix(AttendanceMatrix? value, bool retainOffsets)
    {
        matrix = value;
        if (!retainOffsets)
        {
            selected = null;
            atNewest = true;
            vertical = 0;
        }
        horizontal = atNewest ? MaxX : Math.Clamp(horizontal, 0, MaxX);
        vertical = Math.Clamp(vertical, 0, MaxY);
        Invalidate();
    }

    private void Select(Point position, bool longPress = false)
    {
        var tableX = position.X - horizontalInset;
        if (matrix == null || tableX < NameWidth || position.Y < Heading || tableX >= width || position.Y >= height) return;
        var row = (int)((position.Y - Heading + vertical) / Row);
        var index = (int)((tableX - NameWidth + horizontal) / Column);
        if (row >= matrix.Students.Length || index >= matrix.Days.Length) return;
        var student = matrix.Students[row];
        var day = matrix.Days[index];
        if (!matrix.Cells.TryGetValue((student.Id, day), out var cell)) return;
        selected = (student.Id, day);
        Invalidate();
        if (longPress)
        {
            held = true;
            AbsenceRequested?.Invoke(student, day, cell);
        }
        else DaySelected?.Invoke(student, day, cell);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (matrix is not { Days.Length: > 0, Students.Length: > 0 } || width <= 32 || height <= Heading) return;
        canvas.SaveState();
        canvas.Translate(horizontalInset, 0);
        canvas.ClipRectangle(0, 0, width, height);
        var nameWidth = NameWidth;
        var firstColumn = (int)(horizontal / Column);
        var firstRow = (int)(vertical / Row);
        var lastColumn = Math.Min(matrix.Days.Length, (int)Math.Ceiling((horizontal + width - nameWidth) / Column));
        var lastRow = Math.Min(matrix.Students.Length, (int)Math.Ceiling((vertical + height - Heading) / Row));
        Cell(canvas, 0, 0, width, Heading, MobileStyle.Surface);
        Text(canvas, "Name", 10, 0, nameWidth - 16, Heading, MobileStyle.Ink, 13, HorizontalAlignment.Left, bold: true);

        canvas.SaveState();
        canvas.ClipRectangle(nameWidth, 0, width - nameWidth, Heading);
        for (var column = firstColumn; column < lastColumn; column++)
        {
            var day = matrix.Days[column];
            var x = nameWidth + column * Column - horizontal;
            Cell(canvas, x, 0, Column, Heading, MobileStyle.Surface);
            Text(canvas, day.ToString("ddd", German).TrimEnd('.'), x, Heading * 0.1f, Column, Heading * 0.37f,
                MobileStyle.Muted, 11, HorizontalAlignment.Center);
            Text(canvas, day.ToString("dd.MM.", German), x, Heading * 0.45f, Column, Heading * 0.47f,
                MobileStyle.Ink, 12, HorizontalAlignment.Center, bold: true);
        }
        canvas.RestoreState();

        canvas.SaveState();
        canvas.ClipRectangle(0, Heading, width, height - Heading);
        for (var row = firstRow; row < lastRow; row++)
        {
            var y = Heading + row * Row - vertical;
            var student = matrix.Students[row];
            Cell(canvas, 0, y, nameWidth, Row, row % 2 == 0 ? Colors.White : MobileStyle.Surface);
            Text(canvas, student.Name, 8, y, nameWidth - 14, Row, MobileStyle.Ink, 13, HorizontalAlignment.Left);
        }
        canvas.ClipRectangle(nameWidth, Heading, width - nameWidth, height - Heading);
        for (var row = firstRow; row < lastRow; row++)
        {
            var y = Heading + row * Row - vertical;
            var student = matrix.Students[row];
            for (var col = firstColumn; col < lastColumn; col++)
            {
                var day = matrix.Days[col];
                var x = nameWidth + col * Column - horizontal;
                if (!matrix.Cells.TryGetValue((student.Id, day), out var cell)) continue;
                var (fill, ink) = ColorsFor(cell.Mark);
                if (selected == (student.Id, day))
                {
                    fill = MobileStyle.Accent;
                    ink = Colors.White;
                }
                Cell(canvas, x, y, Column, Row, fill);
                Text(canvas, AttendanceMatrix.Symbol(cell.Mark), x, y, Column, Row, ink, 17, HorizontalAlignment.Center, bold: true);
            }
        }
        canvas.RestoreState();
        canvas.RestoreState();
    }

    private static void Cell(ICanvas canvas, float x, float y, float w, float h, Color fill)
    {
        canvas.FillColor = fill;
        canvas.FillRectangle(x, y, w, h);
        canvas.StrokeColor = MobileStyle.Line;
        canvas.StrokeSize = 1;
        canvas.DrawLine(x + w - 0.5f, y, x + w - 0.5f, y + h);
        canvas.DrawLine(x, y + h - 0.5f, x + w, y + h - 0.5f);
    }

    private void Text(ICanvas canvas, string text, float x, float y, float w, float h, Color color, float size,
        HorizontalAlignment alignment, bool bold = false)
    {
        canvas.FontColor = color;
        canvas.FontSize = Math.Min(size * zoom, h * 0.8f);
        canvas.Font = bold ? Microsoft.Maui.Graphics.Font.DefaultBold : Microsoft.Maui.Graphics.Font.Default;
        canvas.DrawString(text, x, y, w, h, alignment, VerticalAlignment.Center, TextFlow.ClipBounds);
    }

    private static (Color Fill, Color Ink) ColorsFor(DayMark mark) => mark switch
    {
        DayMark.Present => (Color.FromArgb("#EBF9F4"), Color.FromArgb("#00624C")),
        DayMark.NoAbsenceRecorded => (MobileStyle.Surface, Color.FromArgb("#165355")),
        DayMark.Absent => (Color.FromArgb("#FEEBEB"), Color.FromArgb("#8D1B25")),
        DayMark.ExcusedAbsence or DayMark.ExcusedAbsenceAndLate => (Color.FromArgb("#DDF2E1"), Color.FromArgb("#125E2B")),
        DayMark.Late => (Color.FromArgb("#FFF6DA"), Color.FromArgb("#774600")),
        DayMark.AbsentAndLate => (Color.FromArgb("#FFE4D3"), Color.FromArgb("#7E2C0D")),
        DayMark.Exempt => (Color.FromArgb("#E5EEFB"), Color.FromArgb("#2A4E84")),
        _ => (MobileStyle.Surface, MobileStyle.Muted)
    };
}
