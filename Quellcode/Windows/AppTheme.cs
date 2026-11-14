using System.Drawing.Drawing2D;

namespace WebUntisAuswertung;

internal static class AppTheme
{
    public static readonly Color Ink = Color.FromArgb(26, 38, 65);
    public static readonly Color Accent = Color.FromArgb(36, 88, 218);
    public static readonly Color AccentHover = Color.FromArgb(30, 73, 184);
    public static readonly Color Tint = Color.FromArgb(235, 241, 255);
    public static readonly Color TintHover = Color.FromArgb(220, 230, 252);
    public static readonly Color TintPressed = Color.FromArgb(206, 220, 250);
    public static readonly Color Surface = Color.FromArgb(244, 246, 251);
    public static readonly Color Line = Color.FromArgb(226, 232, 243);
    public static readonly Color Muted = Color.FromArgb(102, 115, 139);
    public static readonly string HeadingFont = FontFamily.Families.Any(font =>
        font.Name == "Frutiger 57Cn" && font.IsStyleAvailable(FontStyle.Bold))
        ? "Frutiger 57Cn" : "Segoe UI";
    public static Size MeasureLine(IDeviceContext context, string text, Font font, TextFormatFlags flags) =>
        TextRenderer.MeasureText(context, text, font, new Size(int.MaxValue, int.MaxValue), flags);

    public static void PaintRounded(Graphics graphics, Rectangle bounds, Color fill, int radius, Color? outline = null)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        var corners = new Size(Math.Min(radius, bounds.Width / 2), Math.Min(radius, bounds.Height / 2));
        var oldMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(fill);
        graphics.FillRoundedRectangle(brush, bounds, corners);
        if (outline is { } color)
        {
            using var pen = new Pen(color);
            graphics.DrawRoundedRectangle(pen, bounds, corners);
        }
        graphics.SmoothingMode = oldMode;
    }
}

internal sealed class BrandTabs : TabControl
{
    public BrandTabs()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;
        ItemSize = new Size(112, 42);
        SizeMode = TabSizeMode.Normal;
        Padding = new Point(16, 6);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetTabHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (IsHandleCreated) SetTabHeight();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        SetTabHeight();
    }

    private void SetTabHeight()
    {
        using var graphics = CreateGraphics();
        var lineHeight = AppTheme.MeasureLine(graphics, "Einstellungen", Font, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Height;
        var height = Math.Max(LogicalToDeviceUnits(42), lineHeight + LogicalToDeviceUnits(16));
        if (ItemSize.Height != height) ItemSize = new Size(ItemSize.Width, height);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= TabCount) return;
        var selected = e.Index == SelectedIndex;
        var highContrast = SystemInformation.HighContrast;
        var outer = e.Bounds;
        using var background = new SolidBrush(highContrast ? SystemColors.Control : AppTheme.Surface);
        e.Graphics.FillRectangle(background, outer);
        var bounds = Rectangle.Inflate(outer, -2, -3);
        var fill = highContrast ? (selected ? SystemColors.Highlight : SystemColors.Control) : selected ? Color.White : AppTheme.Line;
        AppTheme.PaintRounded(e.Graphics, bounds, fill, LogicalToDeviceUnits(10), selected && !highContrast ? AppTheme.Line : null);
        var ink = highContrast ? (selected ? SystemColors.HighlightText : SystemColors.ControlText) :
            selected ? AppTheme.Accent : AppTheme.Muted;
        TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, bounds, ink, TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (selected && Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4), ink, fill);
    }
}

internal sealed class SchoolWordmark : Button
{
    private const string FullName = "Berufskolleg für TECHNIK Düren";
    private const string ShortName = "BKT Düren";
    private const TextFormatFlags TextFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
    private readonly Image? logo;

    public SchoolWordmark(Image? logo)
    {
        this.logo = logo;
        Text = FullName;
        AccessibleName = $"Website öffnen · {FullName}";
        AccessibleRole = AccessibleRole.Link;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Font = new Font(AppTheme.HeadingFont, 11, FontStyle.Bold);
        BackColor = SystemInformation.HighContrast ? SystemColors.Control : AppTheme.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public int WidthFor(int available)
    {
        var logoWidth = logo == null ? 0 : LogicalToDeviceUnits(26);
        var horizontal = 2 * LogicalToDeviceUnits(4);
        var gap = logo == null ? 0 : LogicalToDeviceUnits(9);
        if (available < logoWidth + horizontal) return 0;
        using var graphics = CreateGraphics();
        foreach (var name in new[] { FullName, ShortName })
        {
            var width = AppTheme.MeasureLine(graphics, name, Font, TextFlags).Width + logoWidth + horizontal + gap;
            if (width <= available) return width;
        }
        return logo != null ? logoWidth + horizontal : 0;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var margin = LogicalToDeviceUnits(4);
        var iconSize = logo == null ? 0 : LogicalToDeviceUnits(26);
        var gap = logo == null ? 0 : LogicalToDeviceUnits(9);
        var widthForText = ClientSize.Width - iconSize - gap - 2 * margin;
        var heightForText = ClientSize.Height - 2 * margin;
        var wordmark = FullName;
        var measured = AppTheme.MeasureLine(e.Graphics, wordmark, Font, TextFlags);
        if (measured.Width > widthForText || measured.Height > heightForText)
        {
            wordmark = ShortName;
            measured = AppTheme.MeasureLine(e.Graphics, wordmark, Font, TextFlags);
        }
        var fits = measured.Width <= widthForText && measured.Height <= heightForText;
        var right = ClientSize.Width - margin;
        if (fits)
        {
            var textBounds = new Rectangle(right - measured.Width, 0, measured.Width, ClientSize.Height);
            TextRenderer.DrawText(e.Graphics, wordmark, Font, textBounds,
                SystemInformation.HighContrast ? SystemColors.ControlText : AppTheme.Ink,
                TextFlags | TextFormatFlags.VerticalCenter);
            right = textBounds.Left - gap;
        }
        if (logo != null && iconSize <= heightForText && right - iconSize >= margin)
            e.Graphics.DrawImage(logo, new Rectangle(right - iconSize, (ClientSize.Height - iconSize) / 2, iconSize, iconSize));
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) logo?.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class BrandButton(bool primary) : Button
{
    private bool hovered;
    private bool pressed;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var highContrast = SystemInformation.HighContrast;
        var fill = highContrast ? SystemColors.Control : Enabled
            ? (primary ? (hovered || pressed ? AppTheme.AccentHover : AppTheme.Accent) :
                pressed ? AppTheme.TintPressed : hovered ? AppTheme.TintHover : AppTheme.Tint)
            : AppTheme.Surface;
        var ink = highContrast ? (Enabled ? SystemColors.ControlText : SystemColors.GrayText) :
            Enabled ? (primary ? Color.White : AppTheme.Accent) : AppTheme.Muted;
        AppTheme.PaintRounded(e.Graphics, bounds, fill, LogicalToDeviceUnits(11), highContrast ? SystemColors.ControlText : null);
        var textBounds = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ink, TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4), ink, fill);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        pressed = e.Button == MouseButtons.Left;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) pressed = false;
        Invalidate();
        base.OnMouseCaptureChanged(e);
    }
}

internal sealed class TransferCancelButton : ToolStripButton
{
    public TransferCancelButton()
    {
        Text = "Übertragung abbrechen";
        AccessibleName = Text;
        ToolTipText = Text;
        DisplayStyle = ToolStripItemDisplayStyle.None;
        AutoSize = false;
        Visible = false;
        Enabled = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var diameter = Math.Max(0, Math.Min(Width, Height) - 4);
        if (diameter < 8) return;
        var circle = new Rectangle((Width - diameter) / 2, (Height - diameter) / 2, diameter, diameter);
        var highContrast = SystemInformation.HighContrast;
        var fill = !Enabled ? SystemColors.GrayText : highContrast ? SystemColors.Highlight :
            Pressed ? Color.FromArgb(139, 29, 41) : Color.FromArgb(190, 41, 57);
        AppTheme.PaintRounded(e.Graphics, circle, fill, diameter / 2);
        var center = new Point(circle.X + diameter / 2, circle.Y + diameter / 2);
        var arm = diameter / 5;
        var oldMode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(highContrast ? SystemColors.HighlightText : Color.White, Math.Max(1.5f, diameter / 9f));
        e.Graphics.DrawLine(pen, center.X - arm, center.Y - arm, center.X + arm, center.Y + arm);
        e.Graphics.DrawLine(pen, center.X - arm, center.Y + arm, center.X + arm, center.Y - arm);
        e.Graphics.SmoothingMode = oldMode;
    }
}

internal sealed class BrandCard : FlowLayoutPanel
{
    public BrandCard()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var bounds = Rectangle.FromLTRB(0, 0, Width - 1, Height - LogicalToDeviceUnits(8));
        var highContrast = SystemInformation.HighContrast;
        AppTheme.PaintRounded(e.Graphics, bounds, highContrast ? SystemColors.Window : Color.White,
            LogicalToDeviceUnits(14), highContrast ? SystemColors.WindowText : AppTheme.Line);
    }
}
