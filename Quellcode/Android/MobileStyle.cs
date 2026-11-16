using Microsoft.Maui.Controls.Shapes;

namespace WebUntisAuswertung.Mobile;

internal static class MobileStyle
{
    public const float WorkspaceSideInset = 10;
    public static readonly Color Ink = Color.FromArgb("#1A2641");
    public static readonly Color Accent = Color.FromArgb("#2458DA");
    public static readonly Color Surface = Color.FromArgb("#F4F6FB");
    public static readonly Color Tint = Color.FromArgb("#EBF1FF");
    public static readonly Color Line = Color.FromArgb("#E2E8F3");
    public static readonly Color Muted = Color.FromArgb("#52617C");
    public static readonly Color Error = Color.FromArgb("#952631");

    public static Label Label(string text, double size = 14, Color? color = null, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        TextColor = color ?? Ink,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
        LineBreakMode = LineBreakMode.WordWrap
    };

    public static Button Button(string text, bool primary = false) => new()
    {
        Text = text,
        FontSize = 14,
        FontAttributes = FontAttributes.Bold,
        TextColor = primary ? Colors.White : Accent,
        BackgroundColor = primary ? Accent : Tint,
        HeightRequest = 44,
        CornerRadius = 8,
        Padding = new Thickness(12, 0),
        TextTransform = TextTransform.None
    };

    public static Border Card(View content, Thickness? padding = null) => new()
    {
        BackgroundColor = Colors.White,
        Stroke = Line,
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 12 },
        Padding = padding ?? new Thickness(12),
        Content = content
    };
}
