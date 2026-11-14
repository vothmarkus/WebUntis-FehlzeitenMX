using System.Globalization;
using Microsoft.Maui.Graphics;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixCanvas : GraphicsView, IDrawable
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private const float Heading = 50;
    private const float Row = 40;
    private const float Column = 49;
    private float width, height, horizontal, vertical, startX, startY;
    private AttendanceMatrix? matrix;
    private bool atNewest;
    private (string Id, DateOnly Day)? selected;

    public MatrixCanvas()
    {
        Drawable = this;
        BackgroundColor = Colors.White;
        SemanticProperties.SetDescription(this, "Tagesmatrix. Wische für weitere Tage und Namen. Tippe auf eine Zelle für die Tagesdetails.");
        var pan = new PanGestureRecognizer { TouchPoints = 1 };
        pan.PanUpdated += (_, args) =>
        {
            switch (args.StatusType)
            {
                case GestureStatus.Started:
                    startX = horizontal;
                    startY = vertical;
                    break;
                case GestureStatus.Running:
                    horizontal = Math.Clamp(startX - (float)args.TotalX, 0, MaxX);
                    vertical = Math.Clamp(startY - (float)args.TotalY, 0, MaxY);
                    Invalidate();
                    break;
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    atNewest = MaxX - horizontal < 1;
                    break;
            }
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, args) =>
        {
            if (args.GetPosition(this) is { } position) Select(position);
        };
        GestureRecognizers.Add(pan);
        GestureRecognizers.Add(tap);
        SizeChanged += (_, _) =>
        {
            if (Width <= 0 || Height <= 0) return;
            width = (float)Width;
            height = (float)Height;
            horizontal = atNewest ? MaxX : Math.Clamp(horizontal, 0, MaxX);
            vertical = Math.Clamp(vertical, 0, MaxY);
            Invalidate();
        };
    }

    public event Action<Student, DateOnly, DayCell>? DaySelected;
    private float NameWidth => Math.Clamp(width * 0.4f, 103, 192);
    private float MaxX => Math.Max(0, (matrix?.Days.Length ?? 0) * Column - Math.Max(0, width - NameWidth));
    private float MaxY => Math.Max(0, (matrix?.Students.Length ?? 0) * Row - Math.Max(0, height - Heading));

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

    private void Select(Point position)
    {
        if (matrix == null || position.X < NameWidth || position.Y < Heading || position.X >= width || position.Y >= height) return;
        var row = (int)((position.Y - Heading + vertical) / Row);
        var index = (int)((position.X - NameWidth + horizontal) / Column);
        if (row >= matrix.Students.Length || index >= matrix.Days.Length) return;
        var student = matrix.Students[row];
        var day = matrix.Days[index];
        if (!matrix.Cells.TryGetValue((student.Id, day), out var cell)) return;
        selected = (student.Id, day);
        Invalidate();
        DaySelected?.Invoke(student, day, cell);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (matrix == null || width <= 0 || height <= Heading) return;
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
            Text(canvas, day.ToString("ddd", German).TrimEnd('.'), x, 6, Column, 17, MobileStyle.Muted, 11, HorizontalAlignment.Center);
            Text(canvas, day.ToString("dd.MM.", German), x, 22, Column, 23, MobileStyle.Ink, 12, HorizontalAlignment.Center, bold: true);
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

    private static void Text(ICanvas canvas, string text, float x, float y, float w, float h, Color color, float size,
        HorizontalAlignment alignment, bool bold = false)
    {
        canvas.FontColor = color;
        canvas.FontSize = size;
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
