using System.Text.Json;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed record ClassSelection(string[] Mine, string[] Teachers)
{
    private const string StorageKey = "ClassSelectionV2";
    private const string PreviousStorageKey = "ClassSelection";

    public static ClassSelection Load()
    {
        try
        {
            var saved = Preferences.Default.Get(StorageKey, "");
            if (saved.Length > 0 && JsonSerializer.Deserialize<ClassSelection>(saved) is { Mine: not null, Teachers: not null } selection)
                return selection with { Mine = selection.Mine.Union(selection.Teachers, StringComparer.Ordinal).ToArray() };
            var previous = Preferences.Default.Get(PreviousStorageKey, "");
            if (previous.Length > 0 && JsonSerializer.Deserialize<PreviousSelection>(previous) is { Home: not null, Mine: not null } old)
                return new(old.Home.Union(old.Mine, StringComparer.Ordinal).ToArray(), []);
        }
        catch (JsonException) { }
        return new([], []);
    }

    public void Save() => Preferences.Default.Set(StorageKey, JsonSerializer.Serialize(this));

    public IEnumerable<Student> OrderClasses(IEnumerable<Student> available)
    {
        var mine = Mine.ToHashSet(StringComparer.Ordinal);
        var teachers = Teachers.ToHashSet(StringComparer.Ordinal);
        mine.UnionWith(teachers);
        return available.Where(item => mine.Contains(item.Id)).OrderByDescending(item => teachers.Contains(item.Id));
    }

    private sealed record PreviousSelection(string[] Home, string[] Mine);
}

internal sealed class ClassSelectionPage : ContentPage
{
    private readonly Student[] classes;
    private readonly HashSet<string> mine;
    private readonly HashSet<string> teachers;
    private readonly VerticalStackLayout rows = new();
    private bool closing;

    public ClassSelectionPage(Student[] classes, ClassSelection selection, Func<ClassSelection, Task> apply)
    {
        this.classes = BktClassOrder.Sort(classes);
        mine = new(selection.Mine, StringComparer.Ordinal);
        teachers = new(selection.Teachers, StringComparer.Ordinal);
        SafeAreaEdges = SafeAreaEdges.All;
        BackgroundColor = Surface;
        var title = MobileStyle.Label("Klassen auswählen", 21, bold: true);
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
        var search = new SearchBar { Placeholder = "Klassen suchen", TextColor = Ink, PlaceholderColor = Muted };
        search.TextChanged += (_, args) => FillRows(args.NewTextValue?.Trim() ?? "");
        var head = NewRow();
        head.Add(MobileStyle.Label("Klasse", 13, bold: true));
        head.Add(ColumnTitle("Meine Klassen"), 1);
        head.Add(ColumnTitle("Klassenlehrer"), 2);
        var cancel = MobileStyle.Button("Abbrechen");
        var save = MobileStyle.Button("Übernehmen", primary: true);
        cancel.Clicked += async (_, _) => await CloseAsync(null);
        save.Clicked += async (_, _) => await CloseAsync(new ClassSelection(mine.ToArray(), teachers.ToArray()));
        var actions = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) } };
        actions.Add(cancel);
        actions.Add(save, 1);
        var layout = new Grid
        {
            RowSpacing = 8,
            Padding = new Thickness(12, 8),
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }
        };
        layout.Add(title);
        layout.Add(search, 0, 1);
        layout.Add(head, 0, 2);
        layout.Add(Card(new ScrollView { Content = rows }, new Thickness(0)), 0, 3);
        layout.Add(actions, 0, 4);
        Content = layout;
        FillRows("");

        async Task CloseAsync(ClassSelection? value)
        {
            if (closing) return;
            closing = true;
            try
            {
                await Navigation.PopModalAsync();
                if (value != null) await apply(value);
            }
            finally { closing = false; }
        }
    }

    protected override bool OnBackButtonPressed() => closing || base.OnBackButtonPressed();

    private void FillRows(string query)
    {
        rows.Children.Clear();
        foreach (var item in classes.Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            var row = NewRow();
            row.BackgroundColor = rows.Children.Count % 2 == 0 ? Colors.White : Surface;
            var name = MobileStyle.Label(item.Name, 14, bold: true);
            name.VerticalOptions = LayoutOptions.Center;
            row.Add(name);
            var own = Check(item, mine, "Meine Klassen");
            var teacher = Check(item, teachers, "Klassenlehrer");
            own.CheckedChanged += (_, args) =>
            {
                if (!args.Value) teacher.IsChecked = false;
            };
            teacher.CheckedChanged += (_, args) =>
            {
                if (args.Value) own.IsChecked = true;
            };
            row.Add(own, 1);
            row.Add(teacher, 2);
            rows.Children.Add(row);
        }
        if (rows.Children.Count == 0)
        {
            var label = MobileStyle.Label("Keine passenden Klassen.", 14, Muted);
            label.Margin = new Thickness(8);
            rows.Children.Add(label);
        }
    }

    private static Grid NewRow() => new()
    {
        Padding = new Thickness(8, 2),
        ColumnSpacing = 4,
        ColumnDefinitions = { new(GridLength.Star), new(new GridLength(86)), new(new GridLength(90)) }
    };

    private static Label ColumnTitle(string text)
    {
        var label = MobileStyle.Label(text, 13, bold: true);
        label.HorizontalTextAlignment = TextAlignment.Center;
        label.VerticalOptions = LayoutOptions.Center;
        return label;
    }

    private static CheckBox Check(Student item, HashSet<string> selection, string group)
    {
        var check = new CheckBox { IsChecked = selection.Contains(item.Id), Color = Accent, HorizontalOptions = LayoutOptions.Center };
        SemanticProperties.SetDescription(check, $"{item.Name}, {group}");
        check.CheckedChanged += (_, args) =>
        {
            if (args.Value) selection.Add(item.Id);
            else selection.Remove(item.Id);
        };
        return check;
    }
}
