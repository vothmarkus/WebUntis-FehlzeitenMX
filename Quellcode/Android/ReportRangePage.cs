using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class ReportRangePage : ContentPage
{
    private readonly DatePicker from;
    private readonly DatePicker to;
    private readonly Label error = MobileStyle.Label("", 14, Error);
    private readonly Func<ReportRange?, Task> applyRange;
    private bool closing;

    public ReportRangePage(ReportRange schoolYear, ReportRange selection, Func<ReportRange?, Task> applyRange)
    {
        this.applyRange = applyRange;
        Title = "Zeitraum";
        SafeAreaEdges = SafeAreaEdges.All;
        BackgroundColor = Surface;
        from = Picker("Startdatum", selection.From, schoolYear);
        to = Picker("Enddatum", selection.To, schoolYear);
        from.DateSelected += (_, _) => error.IsVisible = false;
        to.DateSelected += (_, _) => error.IsVisible = false;
        error.IsVisible = false;

        var apply = MobileStyle.Button("Anwenden", primary: true);
        var reset = MobileStyle.Button("Schuljahr bis heute");
        var cancel = MobileStyle.Button("Abbrechen");
        apply.Clicked += async (_, _) => await ApplyAsync();
        reset.Clicked += async (_, _) => await CloseAsync(apply: true);
        cancel.Clicked += async (_, _) => await CloseAsync(apply: false);

        var heading = MobileStyle.Label("Zeitraum", 22, bold: true);
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
        var dates = new VerticalStackLayout
        {
            Spacing = 6,
            Children = { MobileStyle.Label("Von", 14, bold: true), from, MobileStyle.Label("Bis", 14, bold: true), to, error }
        };
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16),
                Spacing = 12,
                Children = { heading, Card(dates), apply, reset, cancel }
            }
        };
    }

    protected override bool OnBackButtonPressed() => closing || base.OnBackButtonPressed();

    private async Task ApplyAsync()
    {
        if (from.Date is not { } start || to.Date is not { } end || start > end)
        {
            error.Text = "Wähle ein Startdatum bis einschließlich Enddatum.";
            error.IsVisible = true;
            return;
        }

        await CloseAsync(apply: true, new ReportRange(DateOnly.FromDateTime(start), DateOnly.FromDateTime(end)));
    }

    private async Task CloseAsync(bool apply, ReportRange? range = null)
    {
        if (closing) return;
        closing = true;
        try
        {
            await Navigation.PopModalAsync();
            if (apply) await applyRange(range);
        }
        finally { closing = false; }
    }

    private static DatePicker Picker(string description, DateOnly date, ReportRange schoolYear)
    {
        var picker = new DatePicker
        {
            MinimumDate = schoolYear.From.ToDateTime(TimeOnly.MinValue),
            MaximumDate = schoolYear.To.ToDateTime(TimeOnly.MinValue),
            Date = date.ToDateTime(TimeOnly.MinValue),
            Format = "dd.MM.yyyy",
            TextColor = Ink,
            MinimumHeightRequest = 44
        };
        SemanticProperties.SetDescription(picker, description);
        return picker;
    }
}
