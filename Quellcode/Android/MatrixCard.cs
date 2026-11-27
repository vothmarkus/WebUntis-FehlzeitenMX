using System.Globalization;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixCard : ContentView
{
    private const float CardInset = 8;
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private readonly MatrixCanvas canvas;
    private readonly bool inDeck;
    private readonly Grid header;
    private readonly Label title = MobileStyle.Label("", 16, bold: true);
    private readonly Label caption = MobileStyle.Label("", 12, Muted);
    private readonly Label dataStand = MobileStyle.Label("", 12, Muted);
    private readonly Label message = MobileStyle.Label("", 14, Muted);
    private readonly Button previous = HeaderButton("‹", "Vorherige Klasse", 36);
    private readonly Button next = HeaderButton("›", "Nächste Klasse", 36);
    private readonly Button notes = MobileStyle.Button("ⓘ");
    private bool fromDevice;
    private string className = "";
    private int deckIndex, deckCount;

    public AttendanceMatrix? Matrix { get; private set; }
    public bool IsFromDevice => fromDevice;
    public bool HasHeading => header.IsVisible;
    public event Action<AttendanceMatrix, Student, DateOnly, DayCell>? DaySelected;
    public event Action<AttendanceMatrix, Student, DateOnly, DayCell>? AbsenceRequested;
    public event Action<AttendanceMatrix>? NotesRequested;
    public event Action<float, bool>? VerticalScrolled;
    public event Action? FitsVerticallyAfterZoom;
    public event Action<SwipeDirection>? HeadingSwiped;
    public event Action<int>? AdjacentRequested;
    public event Action<float>? CardDragged;
    public event Action<int?>? CardDragEnded;
    public event Action? ClassPickerRequested;

    public MatrixCard(bool inDeck, Func<string, string, DateOnly, bool> isExcusePending)
    {
        this.inDeck = inDeck;
        var deckTableInset = inDeck ? WorkspaceSideInset + CardInset : 0;
        canvas = new MatrixCanvas(inDeck, deckTableInset) { IsExcusePending = isExcusePending };
        canvas.DaySelected += (student, day, cell) =>
        {
            if (Matrix is { } current) DaySelected?.Invoke(current, student, day, cell);
        };
        canvas.AbsenceRequested += (student, day, cell) =>
        {
            if (Matrix is { } current) AbsenceRequested?.Invoke(current, student, day, cell);
        };
        canvas.VerticalScrolled += (distance, atTop) => VerticalScrolled?.Invoke(distance, atTop);
        canvas.FitsVerticallyAfterZoom += () => FitsVerticallyAfterZoom?.Invoke();
        canvas.CardDragged += distance => CardDragged?.Invoke(distance);
        canvas.CardDragEnded += direction => CardDragEnded?.Invoke(direction);

        title.LineBreakMode = caption.LineBreakMode = dataStand.LineBreakMode = LineBreakMode.TailTruncation;
        title.MaxLines = caption.MaxLines = dataStand.MaxLines = 1;
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
        }
        var swipe = new SwipeGrid
        {
            Directions = SwipeDirection.Up | SwipeDirection.Down |
                (inDeck ? SwipeDirection.Left | SwipeDirection.Right : 0),
            ColumnSpacing = 4,
            RowSpacing = 2,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto) },
            ColumnDefinitions =
            {
                new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)
            }
        };
        swipe.Swiped += direction =>
        {
            if (direction is SwipeDirection.Left or SwipeDirection.Right)
                AdjacentRequested?.Invoke(direction == SwipeDirection.Left ? 1 : -1);
            else HeadingSwiped?.Invoke(direction);
        };
        header = swipe;
        header.Add(previous);
        header.Add(heading, 1);
        header.Add(next, 2);
        header.Add(notes, 3);
        header.Add(dataStand, 0, 1);
        Grid.SetColumnSpan(dataStand, 4);
        notes.WidthRequest = 40;
        notes.Padding = Thickness.Zero;
        SemanticProperties.SetDescription(notes, "Abrufhinweise");
        previous.Clicked += (_, _) => AdjacentRequested?.Invoke(-1);
        next.Clicked += (_, _) => AdjacentRequested?.Invoke(1);
        notes.Clicked += (_, _) =>
        {
            if (Matrix is { } current) NotesRequested?.Invoke(current);
        };
        message.VerticalOptions = LayoutOptions.Center;
        message.HorizontalTextAlignment = TextAlignment.Center;
        message.Margin = new Thickness(deckTableInset + CardInset, 12);
        message.InputTransparent = true;
        var body = new Grid { IsClippedToBounds = true };
        body.Add(canvas);
        body.Add(message);
        var layout = new Grid
        {
            RowSpacing = 5,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) }
        };
        layout.Add(header);
        layout.Add(body, 0, 1);
        if (inDeck)
        {
            // The canvas spans the side margins so a card drag can start outside the drawn table.
            var face = Card(new Grid(), Thickness.Zero);
            face.Margin = new Thickness(WorkspaceSideInset, 0);
            face.InputTransparent = true;
            header.Margin = new Thickness(deckTableInset, 0);
            layout.Padding = new Thickness(0, CardInset);
            Content = new Grid { Children = { face, layout } };
        }
        else Content = Card(layout, new Thickness(CardInset));
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

    public void ShowMatrix(AttendanceMatrix value, bool fromDevice = false)
    {
        var sameClass = Matrix is { } current && current.ClassId == value.ClassId && current.From == value.From;
        Matrix = value;
        this.fromDevice = fromDevice;
        className = value.ClassName;
        UpdateHeading();
        message.Text = "Für diese Klasse liegen im Zeitraum keine Unterrichtstage mit auswertbaren Daten vor.";
        canvas.SetMatrix(value, sameClass);
        UpdateVisibility();
    }

    public void RedrawCells() => canvas.Invalidate();

    public void SetDeckPosition(int index, int count)
    {
        deckIndex = index;
        deckCount = count;
        canvas.CanSwitchClass = count > 1;
        previous.IsVisible = next.IsVisible = count > 1;
        UpdateHeading();
    }

    private void UpdateHeading()
    {
        title.Text = deckCount > 1 ? $"{className} · {deckIndex + 1} / {deckCount}"
            : Matrix is { } value ? $"{className} · {value.Days.Length} Unterrichtstage" : className;
        caption.Text = dataStand.Text = "";
        if (Matrix is { } matrix)
        {
            var days = inDeck && deckCount > 1 ? $" · {matrix.Days.Length} Unterrichtstage" : "";
            caption.Text = $"{matrix.From:dd.MM.yyyy} – {matrix.To:dd.MM.yyyy}{days}";
            if (matrix.FetchedAt is { } fetched)
            {
                var stamp = fetched.ToLocalTime().ToString("dd.MM.yyyy HH:mm 'Uhr'", German);
                var label = fromDevice ? "Gespeicherter Stand" : matrix.HasCompleteReports ? "Stand" : "Teilstand";
                dataStand.Text = $"{label} {stamp}";
            }
        }
        caption.IsVisible = caption.Text.Length > 0;
        dataStand.IsVisible = dataStand.Text.Length > 0;
    }

    private void UpdateVisibility()
    {
        header.IsVisible = className.Length > 0;
        var hasRows = Matrix is { Days.Length: > 0, Students.Length: > 0 };
        canvas.IsVisible = inDeck || hasRows;
        message.IsVisible = !hasRows;
        AutomationProperties.SetIsInAccessibleTree(canvas, hasRows);
        notes.IsVisible = !inDeck && Matrix is { Notes.Length: > 0 };
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
