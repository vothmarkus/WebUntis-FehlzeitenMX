using System.Globalization;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung;

internal sealed class MatrixView : UserControl
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    internal static Color Ink => AppTheme.Ink;
    internal static Color Surface => AppTheme.Surface;
    internal static Color Line => AppTheme.Line;

    private readonly Label caption = new()
    {
        Dock = DockStyle.Top, Height = 38, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 0, 0), ForeColor = Ink
    };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, GridColor = Line,
        VirtualMode = true, ReadOnly = true, RowHeadersVisible = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, MultiSelect = false, EnableHeadersVisualStyles = false,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = AppTheme.Muted, WrapMode = DataGridViewTriState.True },
        DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Ink, SelectionBackColor = AppTheme.TintPressed, SelectionForeColor = Ink },
        AccessibleName = "Anwesenheitsmatrix"
    };
    private readonly bool stacked;
    private readonly bool throughToday;
    private AttendanceMatrix? matrix;
    private DateOnly[] days = [];
    private bool scrollPending;

    public event Action<MatrixView, DayDetails>? DaySelected;

    public MatrixView(bool stacked, bool throughToday = false)
    {
        this.stacked = stacked;
        this.throughToday = stacked || throughToday;
        Dock = stacked ? DockStyle.Top : DockStyle.Fill;
        BackColor = Color.White;
        caption.Font = new Font(caption.Font, FontStyle.Bold);
        if (stacked) grid.ScrollBars = ScrollBars.Horizontal;
        grid.RowTemplate.Height = 30;
        grid.CellValueNeeded += (_, args) =>
        {
            if (matrix == null || args.RowIndex < 0 || args.RowIndex >= matrix.Students.Length) return;
            var student = matrix.Students[args.RowIndex];
            args.Value = args.ColumnIndex == 0 ? student.Name : args.ColumnIndex <= days.Length
                ? AttendanceMatrix.Symbol(matrix.Cells[(student.Id, days[args.ColumnIndex - 1])].Mark) : null;
        };
        grid.CellFormatting += (_, args) =>
        {
            if (CellAt(args.ColumnIndex, args.RowIndex) is { } cell)
                (args.CellStyle.BackColor, args.CellStyle.ForeColor) = MarkColors(cell.Mark);
        };
        grid.CellToolTipTextNeeded += (_, args) =>
        {
            if (CellAt(args.ColumnIndex, args.RowIndex) is { } cell)
                args.ToolTipText = cell.UnknownReason.Length > 0 ? cell.UnknownReason : DayDetails.MarkText(cell.Mark);
        };
        grid.CurrentCellChanged += (_, _) => SelectDay();
        grid.CellClick += (_, _) => SelectDay();
        grid.VisibleChanged += (_, _) => ScrollToLatest();
        grid.HandleCreated += (_, _) => ScrollToLatest();
        Controls.Add(grid);
        Controls.Add(caption);
    }

    public void SetStatus(string text)
    {
        matrix = null;
        days = [];
        grid.RowCount = 0;
        grid.Columns.Clear();
        grid.Visible = false;
        caption.Text = text;
        if (stacked) Height = caption.Height;
    }

    public void SetMatrix(AttendanceMatrix value)
    {
        matrix = value;
        var today = DateOnly.FromDateTime(DateTime.Today);
        days = throughToday && value.From <= today ? value.Days.Where(day => day <= today).ToArray() : value.Days;
        caption.Text = $"{value.ClassName}   ·   {value.From:dd.MM.yyyy} – {value.To:dd.MM.yyyy}   ·   {days.Length} Unterrichtstage";
        grid.SuspendLayout();
        grid.ColumnHeadersHeight = Math.Max(48, grid.Font.Height * 2 + 9);
        grid.RowCount = 0;
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Width = 218, Frozen = true,
            SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var day in days)
        {
            var column = new DataGridViewTextBoxColumn
            {
                HeaderText = day.ToString("ddd", German) + Environment.NewLine + day.ToString("dd.MM.", German),
                Width = 59, SortMode = DataGridViewColumnSortMode.NotSortable, Resizable = DataGridViewTriState.False,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
            };
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            column.HeaderCell.ToolTipText = day.ToString("dddd, dd.MM.yyyy", German);
            grid.Columns.Add(column);
        }
        grid.RowCount = value.Students.Length;
        ClearSelection();
        grid.Visible = true;
        grid.ResumeLayout();
        if (stacked)
        {
            Height = caption.Height + grid.ColumnHeadersHeight + grid.Rows.GetRowsHeight(DataGridViewElementStates.Visible) +
                SystemInformation.HorizontalScrollBarHeight + 3;
        }
        scrollPending = throughToday && value.From <= today;
        ScrollToLatest();
    }

    public void ClearSelection()
    {
        grid.ClearSelection();
        grid.CurrentCell = null;
    }

    private void ScrollToLatest()
    {
        if (!scrollPending || !grid.Visible || !grid.IsHandleCreated) return;
        grid.BeginInvoke(() =>
        {
            if (!scrollPending || !grid.Visible || grid.IsDisposed) return;
            scrollPending = false;
            var width = grid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible);
            grid.HorizontalScrollingOffset = Math.Max(0, width - grid.ClientSize.Width + 2);
        });
    }

    private DayCell? CellAt(int column, int row) => matrix != null && row >= 0 && row < matrix.Students.Length &&
        column > 0 && column <= days.Length ? matrix.Cells[(matrix.Students[row].Id, days[column - 1])] : null;

    private void SelectDay()
    {
        if (grid.CurrentCell is { } current && CellAt(current.ColumnIndex, current.RowIndex) is { } cell)
            DaySelected?.Invoke(this, DayDetails.Create(matrix!.Students[current.RowIndex], days[current.ColumnIndex - 1], cell, matrix.SchoolPeriods));
    }

    private static (Color Background, Color Foreground) MarkColors(DayMark mark) => mark switch
    {
        DayMark.Present => (Color.FromArgb(235, 249, 244), Color.FromArgb(0, 98, 76)),
        DayMark.NoAbsenceRecorded => (Surface, Color.FromArgb(22, 83, 85)),
        DayMark.Absent => (Color.FromArgb(254, 235, 235), Color.FromArgb(141, 27, 37)),
        DayMark.ExcusedAbsence or DayMark.ExcusedAbsenceAndLate => (Color.FromArgb(221, 242, 225), Color.FromArgb(18, 94, 43)),
        DayMark.Late => (Color.FromArgb(255, 246, 218), Color.FromArgb(119, 70, 0)),
        DayMark.AbsentAndLate => (Color.FromArgb(255, 228, 211), Color.FromArgb(126, 44, 13)),
        DayMark.Exempt => (Color.FromArgb(229, 238, 251), Color.FromArgb(42, 78, 132)),
        _ => (Surface, Color.FromArgb(83, 98, 109))
    };
}

internal sealed class DetailsView : UserControl
{
    private readonly Label title = new() { Dock = DockStyle.Top, Height = 32, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft, ForeColor = MatrixView.Ink };
    private readonly Label message = new() { Dock = DockStyle.Fill, Padding = new Padding(2, 5, 0, 3), AutoEllipsis = true };
    private readonly DataGridView table = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
        GridColor = MatrixView.Line, RowHeadersVisible = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, EnableHeadersVisualStyles = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = AppTheme.Surface, ForeColor = AppTheme.Muted,
            Padding = new Padding(4), WrapMode = DataGridViewTriState.True },
        DefaultCellStyle = new DataGridViewCellStyle { ForeColor = MatrixView.Ink, SelectionBackColor = AppTheme.TintPressed,
            SelectionForeColor = MatrixView.Ink, Padding = new Padding(4), WrapMode = DataGridViewTriState.True,
            Alignment = DataGridViewContentAlignment.TopLeft },
        AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(247, 249, 253) },
        AccessibleName = "Tagesdetails"
    };

    public DetailsView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Padding = new Padding(9, 3, 9, 3);
        title.Font = new Font(title.Font, FontStyle.Bold);
        table.Columns.AddRange(Column("UE", 56, 40), Column("Eintrag", 91, 100), Column("Fach", 54, 55), Column("Dauer", 114, 120),
            Column("Entschuldigung", 100, 115), Column("Grund", 82, 115), Column("Abwesenheitstext", 104, 155),
            Column("Entschuldigungstext", 113, 168));
        Controls.Add(table);
        Controls.Add(message);
        Controls.Add(title);
        ShowDetails(DayDetails.Empty);
    }

    public void ShowDetails(DayDetails details)
    {
        title.Text = details.Title;
        message.Text = details.Message;
        var hasRows = details.Rows.Length > 0;
        table.Visible = hasRows;
        message.Dock = hasRows ? DockStyle.Top : DockStyle.Fill;
        if (hasRows) message.Height = message.Font.Height + 12;
        message.Visible = details.Message.Length > 0;
        table.SuspendLayout();
        table.Rows.Clear();
        foreach (var row in details.Rows)
            table.Rows.Add(row.Period, row.Kind, row.Subject, row.Duration, row.Excuse, row.Reason, row.AbsenceText, row.ExcuseText);
        table.ClearSelection();
        table.ResumeLayout();
    }

    private static DataGridViewTextBoxColumn Column(string heading, int minimumWidth, float weight) => new()
    {
        HeaderText = heading, MinimumWidth = minimumWidth, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable
    };
}
