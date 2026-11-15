using System.Globalization;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixPage : ContentPage
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private readonly BrowserSurface browser = new();
    private readonly Grid loginLayer = new() { IsVisible = false, BackgroundColor = Surface, HeightRequest = 62, VerticalOptions = LayoutOptions.Start };
    private readonly Label loginAddress = MobileStyle.Label(WebUntisReports.Origin, 12, Muted);
    private readonly Grid workspace = new() { BackgroundColor = Surface };
    private readonly RowDefinition matrixRow = new(GridLength.Star);
    private readonly Picker classPicker = new() { Title = "Klasse auswählen", TextColor = Ink, TitleColor = Muted };
    private readonly Button refresh = MobileStyle.Button("Aktualisieren", primary: true);
    private readonly Button rangeButton = MobileStyle.Button("Zeitraum");
    private readonly Button signIn = MobileStyle.Button("Bei WebUntis anmelden", primary: true);
    private readonly Button account = MobileStyle.Button("Konto");
    private readonly Button cancel = MobileStyle.Button("Abbrechen");
    private readonly ActivityIndicator activity = new() { Color = Accent, HeightRequest = 22, WidthRequest = 22, IsVisible = false };
    private readonly Label status = MobileStyle.Label("", 13, Muted);
    private readonly Label matrixTitle = MobileStyle.Label("", 17, bold: true);
    private readonly Label matrixCaption = MobileStyle.Label("", 12, Muted);
    private readonly Label empty = MobileStyle.Label("Melde dich an, um deine Klassenliste zu laden.", 15, Muted);
    private readonly MatrixCanvas canvas = new() { IsVisible = false };
    private readonly Label legend = MobileStyle.Label("• Kein Fehlzeiteintrag   | Fehlzeit   E Entschuldigt\n+ Verspätet   |+ / E+ Beides   ? Ungeklärt   B Befreit", 12, Muted);
    private readonly VerticalStackLayout scrollHeader = new() { Spacing = 4 };
    private readonly Button collapse = MobileStyle.Button("−");
    private readonly Button notices = MobileStyle.Button("Abrufhinweise");
    private AndroidReports? reports;
    private Student[] classes = [];
    private ReportRange? schoolYear;
    private ReportRange? selectedRange;
    private AttendanceMatrix? matrix;
    private CancellationTokenSource? transfer;
    private bool profileReady;
    private bool updatingClasses;
    private bool collapsed;
    private bool detailsOpen;
    private bool rangeOpen;

    public MatrixPage()
    {
        Title = "WebUntis-FehlzeitenMX";
        SafeAreaEdges = SafeAreaEdges.All;
        BackgroundColor = Surface;
        classPicker.ItemDisplayBinding = new Binding(nameof(Student.Name));
        classPicker.SelectedIndexChanged += async (_, _) =>
        {
            if (!updatingClasses && transfer == null) await LoadSelectedAsync();
        };
        signIn.Clicked += async (_, _) => await LoadClassesAsync(startWithLogin: true);
        refresh.Clicked += async (_, _) => await LoadSelectedAsync();
        rangeButton.Clicked += async (_, _) => await ShowRangeAsync();
        account.Clicked += async (_, _) => await ShowAccountAsync();
        cancel.Clicked += (_, _) => CancelTransfer();
        collapse.Clicked += (_, _) =>
        {
            collapsed = !collapsed;
            UpdateMatrixVisibility();
        };
        notices.Clicked += async (_, _) =>
        {
            if (matrix is { Notes.Length: > 0 } current)
                await DisplayAlertAsync("Abrufhinweise", string.Join(Environment.NewLine + Environment.NewLine, current.Notes), "Schließen");
        };
        canvas.DaySelected += async (student, day, cell) => await ShowDetailsAsync(student, day, cell);
        var root = new Grid();
        root.Add(browser);
        browser.InputTransparent = true;
        root.Add(workspace);
        root.Add(loginLayer);
        Content = root;
        BuildWorkspace();
        BuildLogin();
        UpdateControls();

        browser.HandlerChanging += (_, eventArgs) =>
        {
            if (eventArgs.OldHandler == null) return;
            transfer?.Cancel();
            if (browser.Browser is { } previous) previous.LoginAddressChanged -= UpdateLoginAddress;
            reports = null;
        };
    }

    private void BuildWorkspace()
    {
        workspace.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        workspace.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        workspace.RowDefinitions.Add(matrixRow);
        workspace.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        workspace.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        workspace.Padding = new Thickness(12, 8);
        workspace.RowSpacing = 8;

        var brandTitle = MobileStyle.Label("WebUntis-FehlzeitenMX", 18, bold: true);
        brandTitle.LineBreakMode = LineBreakMode.WordWrap;
        var brand = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { brandTitle, MobileStyle.Label("Berufskolleg für TECHNIK Düren", 12, Muted) }
        };
        var logo = new Image { Source = "bkt_logo.png", HeightRequest = 36, WidthRequest = 36, VerticalOptions = LayoutOptions.Center };
        SemanticProperties.SetDescription(logo, "Webseite des Berufskollegs für Technik Düren");
        var schoolLink = new TapGestureRecognizer();
        schoolLink.Tapped += async (_, _) => await Launcher.Default.OpenAsync(new Uri("https://bkt-dn.de"));
        logo.GestureRecognizers.Add(schoolLink);
        var header = new Grid
        {
            ColumnSpacing = 9,
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }
        };
        header.Add(logo);
        header.Add(brand, 1);
        header.Add(account, 2);
        workspace.Add(header);

        var tools = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
            ColumnSpacing = 10
        };
        tools.Add(classPicker);
        tools.Add(refresh, 1);
        rangeButton.HorizontalOptions = LayoutOptions.Start;
        var actions = new VerticalStackLayout { Spacing = 4, Children = { signIn, tools, rangeButton } };
        workspace.Add(actions, 0, 1);

        var matrixHeaderText = new VerticalStackLayout { Spacing = 3, Children = { matrixTitle, matrixCaption } };
        var matrixHeader = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        matrixHeader.Add(matrixHeaderText);
        matrixHeader.Add(collapse, 1);
        collapse.WidthRequest = 44;
        collapse.VerticalOptions = LayoutOptions.Center;
        scrollHeader.Children.Add(matrixHeader);
        notices.FontSize = 12;
        notices.HorizontalOptions = LayoutOptions.Start;
        scrollHeader.Children.Add(notices);
        var body = new Grid
        {
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) },
            RowSpacing = 8
        };
        body.Add(scrollHeader);
        empty.HorizontalOptions = LayoutOptions.Center;
        empty.VerticalOptions = LayoutOptions.Center;
        empty.HorizontalTextAlignment = TextAlignment.Center;
        empty.Margin = new Thickness(10, 0);
        body.Add(canvas, 0, 1);
        body.Add(empty, 0, 1);
        workspace.Add(Card(body, new Thickness(9)), 0, 2);

        workspace.Add(legend, 0, 3);
        var footer = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }
        };
        status.VerticalOptions = LayoutOptions.Center;
        activity.VerticalOptions = LayoutOptions.Center;
        footer.Add(activity);
        footer.Add(status, 1);
        footer.Add(cancel, 2);
        workspace.Add(footer, 0, 4);
    }

    private void BuildLogin()
    {
        var back = MobileStyle.Button("‹");
        back.WidthRequest = 44;
        SemanticProperties.SetDescription(back, "Im Anmeldefenster zurück");
        back.Clicked += (_, _) =>
        {
            if (browser.Browser?.GoBack() != true) CancelTransfer();
        };
        var next = MobileStyle.Button("Weiter", primary: true);
        SemanticProperties.SetDescription(next, "Nach der Anmeldung die Klassenberichte öffnen");
        next.Clicked += (_, _) => reports?.ContinueAfterLogin();
        var close = MobileStyle.Button("×");
        close.WidthRequest = 44;
        SemanticProperties.SetDescription(close, "Anmeldung abbrechen");
        close.Clicked += (_, _) => CancelTransfer();
        var toolbar = new Grid
        {
            Padding = new Thickness(8),
            ColumnSpacing = 8,
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
            BackgroundColor = Surface
        };
        loginAddress.VerticalOptions = LayoutOptions.Center;
        loginAddress.LineBreakMode = LineBreakMode.TailTruncation;
        toolbar.Add(back);
        toolbar.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Children = { MobileStyle.Label("WebUntis-Anmeldung", 14, bold: true), loginAddress }
        }, 1);
        toolbar.Add(next, 2);
        toolbar.Add(close, 3);
        loginLayer.Add(toolbar);
    }

    protected override bool OnBackButtonPressed()
    {
        if (loginLayer.IsVisible)
        {
            if (browser.Browser?.GoBack() != true) CancelTransfer();
            return true;
        }
        if (transfer == null) return base.OnBackButtonPressed();
        CancelTransfer();
        return true;
    }

    private async Task LoadClassesAsync(bool startWithLogin)
    {
        await RunAsync(async (service, progress, token) =>
        {
            var (available, year) = await service.ClassesAsync(progress, token, startWithLogin);
            token.ThrowIfCancellationRequested();
            var prior = classPicker.SelectedItem as Student;
            if (selectedRange is { } saved && (!year.Contains(saved.From) || !year.Contains(saved.To)))
                selectedRange = null;
            schoolYear = year;
            classes = available;
            updatingClasses = true;
            try
            {
                classPicker.ItemsSource = classes;
                classPicker.SelectedItem = classes.FirstOrDefault(item => item.Id == prior?.Id);
            }
            finally { updatingClasses = false; }

            if (classPicker.SelectedItem is Student chosen)
                await FetchAsync(chosen, service, progress, token);
            else
            {
                ShowMatrix(null);
                empty.Text = "Wähle eine Klasse.";
                status.Text = $"{classes.Length} Klassen und Gruppen verfügbar";
            }
        });
    }

    private async Task LoadSelectedAsync()
    {
        if (classPicker.SelectedItem is Student choice)
            await RunAsync((service, progress, token) => FetchAsync(choice, service, progress, token));
    }

    private async Task ShowRangeAsync()
    {
        if (transfer != null || rangeOpen || schoolYear is not { } year || Window == null) return;
        var page = new ReportRangePage(year, selectedRange ?? year.ThroughToday(), ApplyRangeAsync);
        rangeOpen = true;
        page.Disappearing += (_, _) => rangeOpen = false;
        try { await Navigation.PushModalAsync(page); }
        catch (InvalidOperationException) { rangeOpen = false; }
    }

    private async Task ApplyRangeAsync(ReportRange? value)
    {
        if (value == selectedRange || schoolYear is not { } year) return;
        selectedRange = value;
        var range = value ?? year.ThroughToday();
        if (matrix is { } previous && (previous.From != range.From || previous.To != range.To)) ShowMatrix(null);
        UpdateControls();
        await LoadSelectedAsync();
    }

    private async Task FetchAsync(Student choice, AndroidReports service, IProgress<string> progress, CancellationToken token)
    {
        if (schoolYear is not { } year) return;
        var range = selectedRange ?? year.ThroughToday();
        if (matrix?.ClassId != choice.Id || matrix.From != range.From) ShowMatrix(null);
        empty.Text = $"{choice.Name} wird geladen …";
        var next = await service.MatrixAsync(choice, range, progress, token);
        token.ThrowIfCancellationRequested();
        if (matrix is { HasCompleteReports: true } && !next.HasCompleteReports)
        {
            status.Text = "WebUntis hat neue Berichte nur teilweise geliefert. Der letzte vollständige Stand bleibt sichtbar.";
            return;
        }
        ShowMatrix(next);
        status.Text = next.HasCompleteReports ? "Aus WebUntis aktualisiert" : "WebUntis hat Berichte nur teilweise geliefert.";
    }

    private async Task RunAsync(Func<AndroidReports, IProgress<string>, CancellationToken, Task> action)
    {
        if (transfer != null || browser.Browser is not { } view) return;
        using var cancellation = new CancellationTokenSource();
        transfer = cancellation;
        var progress = new InlineProgress<string>(message => status.Text = message);
        status.Text = "WebUntis wird geöffnet …";
        UpdateControls();
        try
        {
            if (!profileReady)
            {
                await MobileWebView.ResetProfileAsync();
                profileReady = true;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (reports == null)
            {
                reports = new AndroidReports(view, ShowLogin);
                view.LoginAddressChanged += UpdateLoginAddress;
            }
            await action(reports, progress, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            status.Text = "Abruf abgebrochen";
            if (matrix == null) empty.Text = "Wähle eine Klasse oder tippe auf Aktualisieren.";
        }
        catch (Exception error)
        {
            status.Text = "Abruf fehlgeschlagen";
            if (matrix == null) empty.Text = "Daten konnten gerade nicht geladen werden.";
            if (Window != null)
            {
                ShowLogin(false);
                await DisplayAlertAsync("WebUntis-FehlzeitenMX", error.Message, "OK");
            }
        }
        finally
        {
            transfer = null;
            if (Window != null)
            {
                ShowLogin(false);
                UpdateControls();
            }
        }
    }

    private void CancelTransfer()
    {
        cancel.IsEnabled = false;
        transfer?.Cancel();
        if (loginLayer.IsVisible && browser.Browser is { RendererGone: false } view) view.Go("about:blank");
        ShowLogin(false);
    }

    private void ShowLogin(bool visible)
    {
        if (Window == null) return;
        browser.Browser?.SetLoginVisible(visible);
        if (visible && !loginLayer.IsVisible)
        {
            UpdateLoginAddress(browser.Browser?.LoginAddress);
            browser.InputTransparent = false;
            loginLayer.IsVisible = true;
            workspace.IsVisible = false;
            browser.Margin = new Thickness(0, 62, 0, 0);
        }
        else if (!visible)
        {
            workspace.IsVisible = true;
            browser.InputTransparent = true;
            loginLayer.IsVisible = false;
            browser.Margin = Thickness.Zero;
        }
    }

    private void UpdateLoginAddress(string? address)
    {
        loginAddress.Text = Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.GetLeftPart(UriPartial.Authority) : WebUntisReports.Origin;
    }

    private async Task ShowAccountAsync()
    {
        if (transfer != null) return;
        var action = await DisplayActionSheetAsync("WebUntis", "Schließen", "Abmelden", "Klassenliste aktualisieren");
        if (action == "Klassenliste aktualisieren") await LoadClassesAsync(startWithLogin: classes.Length == 0);
        else if (action == "Abmelden")
        {
            if (reports == null) return;
            await RunAsync(async (service, _, _) =>
            {
                ShowMatrix(null);
                schoolYear = null;
                selectedRange = null;
                classes = [];
                classPicker.ItemsSource = classes;
                empty.Text = "Melde dich an, um deine Klassenliste zu laden.";
                await service.SignOutAsync();
                status.Text = "Von WebUntis abgemeldet";
            });
        }
    }

    private void ShowMatrix(AttendanceMatrix? value)
    {
        var sameClass = value != null && matrix?.ClassId == value.ClassId && matrix.From == value.From;
        if (!sameClass) collapsed = false;
        matrix = value;
        canvas.SetMatrix(value, sameClass);
        if (value != null)
        {
            matrixTitle.Text = $"{value.ClassName} · {value.Days.Length} Unterrichtstage";
            var stamp = value.FetchedAt?.ToLocalTime().ToString("dd.MM. HH:mm", German);
            matrixCaption.Text = $"{value.From:dd.MM.yyyy} – {value.To:dd.MM.yyyy}" + (stamp == null ? "" : $" · Stand {stamp}");
        }
        UpdateMatrixVisibility();
    }

    private void UpdateMatrixVisibility()
    {
        scrollHeader.IsVisible = matrix != null;
        matrixRow.Height = collapsed ? GridLength.Auto : GridLength.Star;
        canvas.IsVisible = matrix is { Days.Length: > 0, Students.Length: > 0 } && !collapsed;
        empty.IsVisible = matrix == null || (!collapsed && !canvas.IsVisible);
        if (matrix != null && empty.IsVisible) empty.Text = "Für diese Klasse liegen im Zeitraum keine Unterrichtstage mit auswertbaren Daten vor.";
        notices.IsVisible = !collapsed && matrix is { Notes.Length: > 0 };
        collapse.Text = collapsed ? "+" : "−";
        SemanticProperties.SetDescription(collapse, collapsed ? "Matrix ausklappen" : "Matrix einklappen");
        legend.IsVisible = canvas.IsVisible;
    }

    private void UpdateControls()
    {
        var busy = transfer != null;
        signIn.IsVisible = classes.Length == 0;
        signIn.IsEnabled = !busy;
        account.IsVisible = reports != null;
        account.IsEnabled = !busy;
        classPicker.IsVisible = classes.Length > 0;
        classPicker.IsEnabled = !busy;
        refresh.IsVisible = classes.Length > 0;
        refresh.IsEnabled = !busy && classPicker.SelectedItem != null;
        var range = selectedRange ?? schoolYear?.ThroughToday();
        rangeButton.IsVisible = range != null;
        rangeButton.IsEnabled = !busy;
        if (range != null) rangeButton.Text = $"Zeitraum · {range.From:dd.MM.} – {range.To:dd.MM.}";
        activity.IsVisible = activity.IsRunning = busy;
        cancel.IsVisible = busy;
        cancel.IsEnabled = busy;
        UpdateMatrixVisibility();
    }

    private async Task ShowDetailsAsync(Student student, DateOnly day, DayCell cell)
    {
        if (detailsOpen || matrix == null) return;
        detailsOpen = true;
        try
        {
            var detail = DayDetails.Create(student, day, cell, matrix.SchoolPeriods);
            var page = new DayDetailPage(student.Name, day, cell, detail);
            page.Disappearing += (_, _) => detailsOpen = false;
            await Navigation.PushModalAsync(page);
        }
        catch (InvalidOperationException) { detailsOpen = false; }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
