using System.Globalization;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class DayDetailPage : ContentPage
{
    public DayDetailPage(string name, DateOnly day, DayCell cell, DayDetails detail)
    {
        SafeAreaEdges = SafeAreaEdges.All;
        BackgroundColor = Surface;
        var close = MobileStyle.Button("Schließen");
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();
        var dayLabel = MobileStyle.Label(day.ToString("dddd, dd.MM.yyyy", CultureInfo.GetCultureInfo("de-DE")), 13, Muted);
        var head = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        head.Add(new VerticalStackLayout { Spacing = 3, Children = { MobileStyle.Label(name, 21, bold: true), dayLabel } });
        head.Add(close, 1);

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
        var layout = new Grid
        {
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) },
            Padding = new Thickness(14, 8, 14, 14),
            RowSpacing = 14
        };
        layout.Add(head);
        layout.Add(new ScrollView { Content = entries }, 0, 1);
        Content = layout;
    }
}
