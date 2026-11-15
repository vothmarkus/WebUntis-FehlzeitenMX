using System.Globalization;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixCard : ContentView
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private readonly MatrixCanvas canvas;
    private readonly Grid header;
    private readonly Grid body = new() { IsClippedToBounds = true };
    private readonly Label title = MobileStyle.Label("", 16, bold: true);
    private readonly Label caption = MobileStyle.Label("", 12, Muted);
    private readonly Label message = MobileStyle.Label("", 14, Muted);
    private readonly Button toggle = MobileStyle.Button("⌃");
    private readonly Button notes = MobileStyle.Button("ⓘ");
    private readonly Button zoom = MobileStyle.Button("100 %");
    private bool collapsed;

    public AttendanceMatrix? Matrix { get; private set; }
    public event Action<AttendanceMatrix, Student, DateOnly, DayCell>? DaySelected;
    public event Action<MatrixCanvas>? ZoomRequested;
    public event Action<AttendanceMatrix>? NotesRequested;

    public MatrixCard(bool stacked = false)
    {
        canvas = new MatrixCanvas(stacked);
        canvas.DaySelected += (student, day, cell) =>
        {
            if (Matrix is { } current) DaySelected?.Invoke(current, student, day, cell);
        };
        canvas.ZoomChanged += value => zoom.Text = $"{value * 100:0} %";

        title.LineBreakMode = caption.LineBreakMode = LineBreakMode.TailTruncation;
        title.MaxLines = caption.MaxLines = 1;
        var heading = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Spacing = 1,
            Children = { title, caption }
        };
        header = new Grid
        {
            ColumnSpacing = 5,
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) }
        };
        header.Add(heading);
        header.Add(zoom, 1);
        header.Add(notes, 2);
        header.Add(toggle, 3);
        toggle.IsVisible = stacked;
        toggle.WidthRequest = notes.WidthRequest = 44;
        SemanticProperties.SetDescription(zoom, "Matrixgröße ändern");
        SemanticProperties.SetDescription(notes, "Abrufhinweise");
        toggle.Clicked += (_, _) =>
        {
            collapsed = !collapsed;
            UpdateVisibility();
        };
        zoom.Clicked += (_, _) => ZoomRequested?.Invoke(canvas);
        notes.Clicked += (_, _) =>
        {
            if (Matrix is { } current) NotesRequested?.Invoke(current);
        };
        message.VerticalOptions = LayoutOptions.Center;
        message.HorizontalTextAlignment = TextAlignment.Center;
        message.Margin = new Thickness(8, 12);
        body.Add(message);
        body.Add(canvas);
        var layout = new Grid
        {
            RowSpacing = 5,
            RowDefinitions = { new(GridLength.Auto), new(stacked ? GridLength.Auto : GridLength.Star) }
        };
        layout.Add(header);
        layout.Add(body, 0, 1);
        Content = Card(layout, new Thickness(8));
        Clear("", "");
    }

    public void Clear(string name, string text)
    {
        Matrix = null;
        title.Text = name;
        caption.Text = "";
        message.Text = text;
        canvas.SetMatrix(null, retainOffsets: false);
        UpdateVisibility();
    }

    public void ShowMatrix(AttendanceMatrix value)
    {
        var sameClass = Matrix is { } current && current.ClassId == value.ClassId && current.From == value.From;
        Matrix = value;
        title.Text = $"{value.ClassName} · {value.Days.Length} Unterrichtstage";
        var stamp = value.FetchedAt?.ToLocalTime().ToString("dd.MM. HH:mm", German);
        caption.Text = $"{value.From:dd.MM.yyyy} – {value.To:dd.MM.yyyy}" + (stamp == null ? "" : $" · Stand {stamp}");
        message.Text = "Für diese Klasse liegen im Zeitraum keine Unterrichtstage mit auswertbaren Daten vor.";
        canvas.SetMatrix(value, sameClass);
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        header.IsVisible = title.Text.Length > 0;
        caption.IsVisible = caption.Text.Length > 0;
        body.IsVisible = !collapsed;
        canvas.IsVisible = !collapsed && Matrix is { Days.Length: > 0, Students.Length: > 0 };
        message.IsVisible = !canvas.IsVisible;
        notes.IsVisible = !collapsed && Matrix is { Notes.Length: > 0 };
        zoom.IsVisible = canvas.IsVisible;
        toggle.Text = collapsed ? "⌄" : "⌃";
        SemanticProperties.SetDescription(toggle, collapsed ? "Matrix ausklappen" : "Matrix einklappen");
    }
}
