using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixDeck : ContentView
{
    public sealed record Entry(Student Class, MatrixCard Card);

    private readonly Grid front = new();
    private readonly Border near = Card(new Grid(), Thickness.Zero);
    private readonly Border far = Card(new Grid(), Thickness.Zero);
    private Entry[] entries = [];
    private int index;
    private bool navigationEnabled = true;

    public IReadOnlyList<Entry> Entries => entries;
    public Entry? Active => entries.Length == 0 ? null : entries[index];
    public event Action? SelectionChanged;
    public event Action? ClassPickerRequested;

    public MatrixDeck()
    {
        near.InputTransparent = far.InputTransparent = true;
        near.BackgroundColor = Tint;
        far.BackgroundColor = Surface;
        Content = new Grid { IsClippedToBounds = true, Children = { far, near, front } };
    }

    public void SetClasses(IEnumerable<Student> classes, Func<MatrixCard> createCard)
    {
        var activeId = Active?.Class.Id;
        var prior = entries.ToDictionary(entry => entry.Class.Id, entry => entry.Card, StringComparer.Ordinal);
        entries = classes.Select(choice =>
        {
            if (!prior.TryGetValue(choice.Id, out var card))
            {
                card = createCard();
                card.Clear(choice.Name, "Berichte werden geladen …");
                card.AdjacentRequested += Step;
                card.ClassPickerRequested += () =>
                {
                    if (navigationEnabled && entries.Length > 1) ClassPickerRequested?.Invoke();
                };
            }
            return new Entry(choice, card);
        }).ToArray();
        index = Math.Max(0, Array.FindIndex(entries, entry => entry.Class.Id == activeId));
        Present();
    }

    public void SetNavigationEnabled(bool enabled)
    {
        navigationEnabled = enabled;
        Active?.Card.SetDeckPosition(index, entries.Length, enabled);
    }

    public void Select(int target, int direction = 0)
    {
        if (!navigationEnabled || target < 0 || target >= entries.Length || target == index) return;
        index = target;
        Present(direction);
        SelectionChanged?.Invoke();
    }

    private void Step(int direction)
    {
        if (entries.Length > 1)
            Select((index + direction + entries.Length) % entries.Length, direction);
    }

    private void Present(int direction = 0)
    {
        front.CancelAnimations();
        var depth = Math.Min(2, Math.Max(0, entries.Length - 1));
        var offset = depth * 5;
        near.IsVisible = depth >= 1;
        far.IsVisible = depth >= 2;
        near.Margin = new Thickness(6, 5, 6, depth == 2 ? 5 : 0);
        far.Margin = new Thickness(12, 10, 12, 0);
        front.Margin = new Thickness(0, 0, 0, offset);
        front.TranslationX = direction * 25;
        if (Active?.Card is { } card)
        {
            card.SetDeckPosition(index, entries.Length, navigationEnabled);
            if (!front.Children.Contains(card))
            {
                front.Children.Clear();
                front.Add(card);
            }
        }
        else front.Children.Clear();
        if (direction != 0) _ = front.TranslateToAsync(0, 0, 140, Easing.CubicOut);
    }
}
