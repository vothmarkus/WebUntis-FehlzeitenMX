using System.Globalization;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class DayDetailView : ContentView
{
    private readonly Button? excuse;
    private readonly ActivityIndicator? openingExcuse;

    public DayDetailView(string name, DateOnly day, DayCell cell, DayDetails detail, bool canEdit)
    {
        BackgroundColor = Surface;
        var close = ActionButton("×", "Schließen");
        var dayLabel = MobileStyle.Label(day.ToString("dddd, dd.MM.yyyy", CultureInfo.GetCultureInfo("de-DE")), 13, Muted);
        var actions = new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Start };
        close.Clicked += (_, _) => CloseRequested?.Invoke();
        if (canEdit)
        {
            excuse = ActionButton("✎", "Entschuldigung bearbeiten", primary: true);
            excuse.Clicked += (_, _) => ExcuseRequested?.Invoke();
            openingExcuse = new ActivityIndicator { Color = Accent, WidthRequest = 24, HeightRequest = 24, IsVisible = false };
            openingExcuse.HorizontalOptions = openingExcuse.VerticalOptions = LayoutOptions.Center;
            SemanticProperties.SetDescription(openingExcuse, "Entschuldigungsformular wird geöffnet");
            actions.Add(new Grid { WidthRequest = 44, HeightRequest = 44, Children = { excuse, openingExcuse } });
        }
        actions.Add(close);
        var header = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        header.Add(new VerticalStackLayout { Spacing = 3, Children = { MobileStyle.Label(name, 21, bold: true), dayLabel } });
        header.Add(actions, 1);

        var parts = detail.Title.Split("   ·   ");
        var headline = string.Join(" · ", parts.Skip(2));
        var entries = new VerticalStackLayout
        {
            Spacing = 10,
            Children = { MobileStyle.Label(headline.Length > 0 ? headline : DayDetails.MarkText(cell.Mark), 15, bold: true) }
        };
        if (detail.Message.Length > 0) entries.Children.Add(MobileStyle.Label(detail.Message, 14, Muted));
        foreach (var item in detail.Rows)
        {
            var heading = string.Join(" · ", new[] { item.Period.Length == 0 ? "" : $"UE {item.Period}", item.Kind, item.Subject }
                .Where(text => text.Length > 0));
            var row = new VerticalStackLayout { Spacing = 5, Children = { MobileStyle.Label(heading, 15, bold: true) } };
            Add("Dauer", item.Duration);
            Add("Entschuldigung", item.Excuse);
            Add("Grund", item.Reason);
            Add("Abwesenheitstext", item.AbsenceText);
            Add("Entschuldigungstext", item.ExcuseText);
            entries.Children.Add(Card(row));

            void Add(string field, string value)
            {
                if (value.Length > 0) row.Children.Add(MobileStyle.Label($"{field} · {value}", 14));
            }
        }
        var scroll = new ScrollView { Content = entries };
        var layout = new SwipeGrid
        {
            Directions = SwipeDirection.Down,
            Threshold = 56,
            CanStartSwipe = point => point.Y < scroll.Y || scroll.ScrollY < 1,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) },
            Padding = new Thickness(14, 8, 14, 14),
            RowSpacing = 14
        };
        layout.Swiped += _ => CloseRequested?.Invoke();
        layout.Add(header);
        layout.Add(scroll, 0, 1);
        Content = layout;
    }

    public event Action? CloseRequested;
    public event Action? ExcuseRequested;

    public void SetOpeningExcuse(bool opening)
    {
        if (excuse == null || openingExcuse == null) return;
        excuse.IsVisible = !opening;
        openingExcuse.IsVisible = openingExcuse.IsRunning = opening;
    }

    private static Button ActionButton(string symbol, string description, bool primary = false)
    {
        var button = MobileStyle.Button(symbol, primary);
        button.WidthRequest = 44;
        button.FontSize = 20;
        button.Padding = Thickness.Zero;
        SemanticProperties.SetDescription(button, description);
        return button;
    }
}
