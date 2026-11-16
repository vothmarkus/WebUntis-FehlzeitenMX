using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixDeck : ContentView
{
    public sealed record Entry(Student Class, MatrixCard Card);

    private const string CardAnimation = "ClassCardTransition";
    private static readonly Easing CardEasing = new(progress => progress * (2 - progress));
    private readonly Grid front = new();
    private readonly Border near = Card(new Grid(), Thickness.Zero);
    private readonly Border far = Card(new Grid(), Thickness.Zero);
    private Entry[] entries = [];
    private int index;
    private MatrixCard? preview;
    private int dragDirection;
    private double dragProgress;
    private int transition;
    private bool settling;

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

    public void SetClasses(IEnumerable<Student> classes, Func<MatrixCard> createCard, bool firstCard = false)
    {
        var activeId = firstCard ? null : Active?.Class.Id;
        var prior = entries.ToDictionary(entry => entry.Class.Id, entry => entry.Card, StringComparer.Ordinal);
        entries = classes.Select(choice =>
        {
            if (!prior.TryGetValue(choice.Id, out var card))
            {
                card = createCard();
                card.Clear(choice.Name, "Für diese Klasse liegt noch kein Datenstand vor.");
                card.AdjacentRequested += direction =>
                {
                    if (Active?.Card == card) Step(direction);
                };
                card.CardDragged += distance => Drag(card, distance);
                card.CardDragEnded += direction => EndDrag(card, direction);
                card.ClassPickerRequested += () =>
                {
                    if (!settling && entries.Length > 1 && Active?.Card == card) ClassPickerRequested?.Invoke();
                };
            }
            return new Entry(choice, card);
        }).ToArray();
        index = Math.Max(0, Array.FindIndex(entries, entry => entry.Class.Id == activeId));
        Present();
    }

    public void Select(int target, int direction = 0)
    {
        if (settling || target < 0 || target >= entries.Length || target == index) return;
        ShowPreview(target, direction != 0 ? direction : Math.Sign(target - index));
        Settle(target, 1);
    }

    private void Step(int direction)
    {
        if (entries.Length > 1)
            Select(AdjacentIndex(direction), direction);
    }

    private int AdjacentIndex(int direction) => (index + direction + entries.Length) % entries.Length;

    private void Drag(MatrixCard card, float distance)
    {
        if (settling || entries.Length < 2 || Active?.Card != card || front.Width <= 0 || !float.IsFinite(distance)) return;
        var direction = distance <= 0 ? 1 : -1;
        ShowPreview(AdjacentIndex(direction), direction);
        ApplyFrame(Math.Min(1, Math.Abs(distance) / front.Width));
    }

    private void EndDrag(MatrixCard card, int? direction)
    {
        if (settling || Active?.Card != card) return;
        if (direction is null)
        {
            if (preview != null) Present();
        }
        else if (direction != 0) Step(direction.Value);
        else if (preview != null) Settle(index, 0);
    }

    private void ShowPreview(int target, int direction)
    {
        var card = entries[target].Card;
        if (preview == card && dragDirection == direction) return;
        if (preview != null && preview != card)
        {
            ResetPose(preview);
            front.Remove(preview);
        }
        preview = card;
        preview.SetDeckPosition(target, entries.Length);
        preview.InputTransparent = true;
        AutomationProperties.SetExcludedWithChildren(preview, true);
        preview.ZIndex = direction < 0 ? 1 : -1;
        preview.AnchorY = 0;
        if (!front.Children.Contains(preview)) front.Add(preview);
        dragDirection = direction;
        ApplyFrame(0);
    }

    private void ApplyFrame(double progress)
    {
        if (preview == null || Active?.Card is not { } current) return;
        dragProgress = progress;
        var incoming = dragDirection < 0;
        var width = Math.Max(0, front.Width);
        current.TranslationX = incoming ? 0 : -width * progress;
        current.Rotation = incoming ? 0 : -3 * progress;
        current.Scale = incoming ? 1 - 0.02 * progress : 1;
        current.TranslationY = incoming ? 4 * progress : 0;
        preview.TranslationX = incoming ? -width * (1 - progress) : 0;
        preview.Rotation = incoming ? -3 * (1 - progress) : 0;
        preview.Scale = incoming ? 1 : 0.98 + 0.02 * progress;
        preview.TranslationY = incoming ? 0 : 4 * (1 - progress);
        preview.Opacity = incoming ? 1 : 0.85 + 0.15 * progress;
    }

    private void Settle(int target, double end)
    {
        if (!IsVisible || front.Handler == null || front.Width <= 0 || Math.Abs(dragProgress - end) < 0.001)
        {
            Finish();
            return;
        }
        settling = true;
        var running = ++transition;
        new Animation(progress =>
        {
            if (running == transition) ApplyFrame(progress);
        }, dragProgress, end, CardEasing).Commit(front, CardAnimation, length: 160, finished: (_, _) =>
        {
            // Android may interrupt the animation. The gesture has already chosen the target card.
            if (running == transition) Finish();
        });

        void Finish()
        {
            var changed = target != index;
            index = target;
            Present();
            if (changed) SelectionChanged?.Invoke();
        }
    }

    private void Present()
    {
        transition++;
        settling = false;
        front.AbortAnimation(CardAnimation);
        preview = null;
        dragDirection = 0;
        dragProgress = 0;
        var depth = Math.Min(2, Math.Max(0, entries.Length - 1));
        var offset = depth * 5;
        near.IsVisible = depth >= 1;
        far.IsVisible = depth >= 2;
        near.Margin = new Thickness(WorkspaceSideInset + 6, 5, WorkspaceSideInset + 6, depth == 2 ? 5 : 0);
        far.Margin = new Thickness(WorkspaceSideInset + 12, 10, WorkspaceSideInset + 12, 0);
        front.Margin = new Thickness(0, 0, 0, offset);
        var card = Active?.Card;
        foreach (var shown in front.Children.OfType<MatrixCard>().ToArray())
        {
            ResetPose(shown);
            if (shown != card) front.Remove(shown);
        }
        if (card == null) return;
        card.SetDeckPosition(index, entries.Length);
        if (!front.Children.Contains(card)) front.Add(card);
    }

    private static void ResetPose(MatrixCard card)
    {
        card.TranslationX = card.TranslationY = card.Rotation = 0;
        card.Scale = card.Opacity = 1;
        card.AnchorY = 0.5;
        card.ZIndex = 0;
        card.InputTransparent = false;
        AutomationProperties.SetExcludedWithChildren(card, false);
    }
}
