using System.Globalization;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixCard : ContentView
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private readonly MatrixCanvas canvas = new();
    private readonly bool inDeck;
    private readonly Grid header;
    private readonly Label title = MobileStyle.Label("", 16, bold: true);
    private readonly Label caption = MobileStyle.Label("", 12, Muted);
    private readonly Label message = MobileStyle.Label("", 14, Muted);
    private readonly Button previous = HeaderButton("‹", "Vorherige Klasse", 36);
    private readonly Button next = HeaderButton("›", "Nächste Klasse", 36);
    private readonly Button menu = HeaderButton("⋮", "Menü öffnen", 38);
    private readonly Button notes = MobileStyle.Button("ⓘ");
    private readonly Button zoom = MobileStyle.Button("100 %");
    private string className = "";
    private int deckIndex, deckCount;

    public AttendanceMatrix? Matrix { get; private set; }
    public event Action<AttendanceMatrix, Student, DateOnly, DayCell>? DaySelected;
    public event Action<MatrixCanvas>? ZoomRequested;
    public event Action<AttendanceMatrix>? NotesRequested;
    public event Action<float, bool>? VerticalScrolled;
    public event Action<int>? AdjacentRequested;
    public event Action? ClassPickerRequested;
    public event Action? MenuRequested;

    public MatrixCard(bool inDeck = false)
    {
        this.inDeck = inDeck;
        canvas.DaySelected += (student, day, cell) =>
        {
            if (Matrix is { } current) DaySelected?.Invoke(current, student, day, cell);
        };
        canvas.ZoomChanged += value => zoom.Text = $"{value * 100:0} %";
        canvas.VerticalScrolled += (distance, atTop) => VerticalScrolled?.Invoke(distance, atTop);

        title.LineBreakMode = caption.LineBreakMode = LineBreakMode.TailTruncation;
        title.MaxLines = caption.MaxLines = 1;
        var heading = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Spacing = 1,
            Children = { title, caption }
        };
        if (inDeck)
        {
            // The table has its own horizontal pan for the dates.
            heading.VerticalOptions = LayoutOptions.Fill;
            var pick = new TapGestureRecognizer();
            pick.Tapped += (_, _) => ClassPickerRequested?.Invoke();
            heading.GestureRecognizers.Add(pick);
            var swipe = new SwipeGestureRecognizer { Direction = SwipeDirection.Left | SwipeDirection.Right, Threshold = 40 };
            swipe.Swiped += (_, args) => AdjacentRequested?.Invoke(args.Direction == SwipeDirection.Left ? 1 : -1);
            heading.GestureRecognizers.Add(swipe);
        }
        header = new Grid
        {
            ColumnSpacing = 4,
            ColumnDefinitions =
            {
                new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto),
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)
            }
        };
        header.Add(previous);
        header.Add(heading, 1);
        header.Add(next, 2);
        header.Add(zoom, 3);
        header.Add(notes, 4);
        header.Add(menu, 5);
        menu.IsVisible = false;
        notes.WidthRequest = 40;
        notes.Padding = Thickness.Zero;
        SemanticProperties.SetDescription(zoom, "Matrixgröße ändern");
        SemanticProperties.SetDescription(notes, "Abrufhinweise");
        previous.Clicked += (_, _) => AdjacentRequested?.Invoke(-1);
        next.Clicked += (_, _) => AdjacentRequested?.Invoke(1);
        menu.Clicked += (_, _) => MenuRequested?.Invoke();
        zoom.Clicked += (_, _) => ZoomRequested?.Invoke(canvas);
        notes.Clicked += (_, _) =>
        {
            if (Matrix is { } current) NotesRequested?.Invoke(current);
        };
        message.VerticalOptions = LayoutOptions.Center;
        message.HorizontalTextAlignment = TextAlignment.Center;
        message.Margin = new Thickness(8, 12);
        var body = new Grid { IsClippedToBounds = true };
        body.Add(message);
        body.Add(canvas);
        var layout = new Grid
        {
            RowSpacing = 5,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) }
        };
        layout.Add(header);
        layout.Add(body, 0, 1);
        Content = Card(layout, new Thickness(8));
        Clear("", "");
    }

    public void Clear(string name, string text)
    {
        Matrix = null;
        className = name;
        UpdateHeading();
        message.Text = text;
        canvas.SetMatrix(null, retainOffsets: false);
        UpdateVisibility();
    }

    public void ShowMatrix(AttendanceMatrix value)
    {
        var sameClass = Matrix is { } current && current.ClassId == value.ClassId && current.From == value.From;
        Matrix = value;
        className = value.ClassName;
        UpdateHeading();
        message.Text = "Für diese Klasse liegen im Zeitraum keine Unterrichtstage mit auswertbaren Daten vor.";
        canvas.SetMatrix(value, sameClass);
        UpdateVisibility();
    }

    public void SetDeckPosition(int index, int count, bool navigationEnabled)
    {
        deckIndex = index;
        deckCount = count;
        previous.IsVisible = next.IsVisible = count > 1;
        previous.IsEnabled = next.IsEnabled = navigationEnabled;
        UpdateHeading();
    }

    public void SetCompactMenu(bool show, bool enabled)
    {
        menu.IsVisible = show;
        menu.IsEnabled = enabled;
    }

    private void UpdateHeading()
    {
        title.Text = deckCount > 1 ? $"{className} · {deckIndex + 1} / {deckCount}"
            : Matrix is { } value ? $"{className} · {value.Days.Length} Unterrichtstage" : className;
        if (Matrix is not { } matrix)
            caption.Text = "";
        else
        {
            var stamp = matrix.FetchedAt?.ToLocalTime().ToString("dd.MM. HH:mm", German);
            var days = inDeck && deckCount > 1 ? $" · {matrix.Days.Length} Unterrichtstage" : "";
            caption.Text = $"{matrix.From:dd.MM.yyyy} – {matrix.To:dd.MM.yyyy}{days}" + (stamp == null ? "" : $" · Stand {stamp}");
        }
        caption.IsVisible = caption.Text.Length > 0;
    }

    private void UpdateVisibility()
    {
        header.IsVisible = className.Length > 0;
        canvas.IsVisible = Matrix is { Days.Length: > 0, Students.Length: > 0 };
        message.IsVisible = !canvas.IsVisible;
        notes.IsVisible = Matrix is { Notes.Length: > 0 };
        zoom.IsVisible = canvas.IsVisible;
    }

    private static Button HeaderButton(string text, string description, double width)
    {
        var button = MobileStyle.Button(text);
        button.IsVisible = false;
        button.WidthRequest = width;
        button.FontSize = 21;
        button.Padding = Thickness.Zero;
        SemanticProperties.SetDescription(button, description);
        return button;
    }
}
