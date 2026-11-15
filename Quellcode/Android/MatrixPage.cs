using System.Globalization;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixPage : ContentPage
{
    private enum Section { Home, Mine, Matrix }

    private const string LegendText = "• Kein Fehlzeiteintrag\n| Fehlzeit    E Entschuldigt\n+ Verspätet    |+ / E+ Beides\n? Ungeklärt    B Befreit";
    private const string ChooseClass = "Wähle eine Klasse.";
    private readonly BrowserSurface browser = new();
    private readonly Grid loginLayer = new() { IsVisible = false, BackgroundColor = Surface, HeightRequest = 62, VerticalOptions = LayoutOptions.Start };
    private readonly Label loginAddress = MobileStyle.Label(WebUntisReports.Origin, 12, Muted);
    private readonly Grid workspace = new() { BackgroundColor = Surface };
    private readonly Grid heading = new();
    private readonly Label sectionTitle = MobileStyle.Label("Startseite", 12, Muted);
    private readonly Button menu = MobileStyle.Button("⋮");
    private readonly Button refresh = MobileStyle.Button("↻");
    private readonly Button rangeButton = MobileStyle.Button("Zeitraum");
    private readonly Button signIn = MobileStyle.Button("Bei WebUntis anmelden", primary: true);
    private readonly Button chooseClasses = MobileStyle.Button("Klassen auswählen", primary: true);
    private readonly Button cancel = MobileStyle.Button("Abbrechen");
    private readonly ActivityIndicator activity = new() { Color = Accent, HeightRequest = 20, WidthRequest = 20, IsVisible = false };
    private readonly Label status = MobileStyle.Label("", 13, Muted);
    private readonly Label emptyText = MobileStyle.Label("", 15, Muted);
    private readonly Picker classPicker = new() { Title = "Klasse auswählen", TextColor = Ink, TitleColor = Muted };
    private readonly MatrixDeck homeDeck = new();
    private readonly MatrixDeck myDeck = new();
    private readonly Grid tools = new();
    private readonly Grid footer = new();
    private readonly MatrixCard matrixCard;
    private readonly Border empty;
    private readonly Dictionary<string, AttendanceMatrix> yearlyMatrices = new(StringComparer.Ordinal);
    private ClassSelection classSelection = ClassSelection.Load();
    private Section section;
    private AndroidReports? reports;
    private Student[] classes = [];
    private ReportRange? schoolYear;
    private ReportRange? selectedRange;
    private CancellationTokenSource? transfer;
    private bool profileReady;
    private bool updatingClasses;
    private bool modalOpen;
    private bool menuOpen;
    private bool headingCollapsed;

    public MatrixPage()
    {
        Title = "WebUntis-FehlzeitenMX";
        SafeAreaEdges = SafeAreaEdges.All;
        BackgroundColor = Surface;
        matrixCard = CreateMatrixCard();
        matrixCard.Clear("", ChooseClass);
        homeDeck.SelectionChanged += UpdateHeading;
        myDeck.SelectionChanged += async () =>
        {
            UpdateHeading();
            await LoadViewAsync(refreshFromWeb: false);
        };
        homeDeck.ClassPickerRequested += async () => await ShowDeckPickerAsync(homeDeck);
        myDeck.ClassPickerRequested += async () => await ShowDeckPickerAsync(myDeck);
        emptyText.HorizontalTextAlignment = TextAlignment.Center;
        empty = Card(new VerticalStackLayout { Spacing = 12, Children = { emptyText, signIn, chooseClasses } });
        empty.VerticalOptions = LayoutOptions.Center;
        classPicker.ItemDisplayBinding = new Binding(nameof(Student.Name));
        classPicker.SelectedIndexChanged += async (_, _) =>
        {
            if (updatingClasses || transfer != null) return;
            if (classPicker.SelectedItem is not Student choice)
                matrixCard.Clear("", ChooseClass);
            else if (matrixCard.Matrix?.ClassId != choice.Id)
                matrixCard.Clear(choice.Name, "Berichte werden geladen …");
            UpdateControls();
            await LoadViewAsync(refreshFromWeb: false);
        };
        signIn.Clicked += async (_, _) => await LoadClassesAsync(startWithLogin: true);
        chooseClasses.Clicked += async (_, _) => await ShowClassSettingsAsync();
        refresh.Clicked += async (_, _) => await LoadViewAsync(refreshFromWeb: true);
        rangeButton.Clicked += async (_, _) => await ShowRangeAsync();
        menu.Clicked += async (_, _) => await ShowMenuAsync();
        cancel.Clicked += (_, _) => CancelTransfer();

        var root = new Grid();
        root.Add(browser);
        browser.InputTransparent = true;
        root.Add(workspace);
        root.Add(loginLayer);
        BuildWorkspace();
        BuildLogin();
        Content = root;
        UpdateControls();

        browser.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler == null) return;
            transfer?.Cancel();
            if (browser.Browser is { } previous) previous.LoginAddressChanged -= UpdateLoginAddress;
            reports = null;
        };
    }

    private void BuildWorkspace()
    {
        workspace.Padding = new Thickness(10, 5);
        workspace.RowSpacing = 5;
        workspace.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        workspace.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        workspace.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        workspace.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        workspace.SizeChanged += (_, _) => UpdateFooter();

        var logo = new Image { Source = "bkt_logo.png", HeightRequest = 34, WidthRequest = 34, VerticalOptions = LayoutOptions.Center };
        SemanticProperties.SetDescription(logo, "Webseite des Berufskollegs für Technik Düren");
        var schoolLink = new TapGestureRecognizer();
        schoolLink.Tapped += async (_, _) => await Launcher.Default.OpenAsync(new Uri("https://bkt-dn.de"));
        logo.GestureRecognizers.Add(schoolLink);
        var title = MobileStyle.Label("Fehlzeiten MX", 18, bold: true);
        title.MaxLines = sectionTitle.MaxLines = 1;
        title.LineBreakMode = sectionTitle.LineBreakMode = LineBreakMode.TailTruncation;
        var brand = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Children = { title, sectionTitle } };
        menu.WidthRequest = refresh.WidthRequest = 44;
        menu.FontSize = 24;
        refresh.FontSize = 21;
        SemanticProperties.SetDescription(menu, "Menü öffnen");
        SemanticProperties.SetDescription(refresh, "Aktualisieren");
        heading.ColumnSpacing = 7;
        heading.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        heading.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        heading.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        heading.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        heading.Add(logo);
        heading.Add(brand, 1);
        heading.Add(refresh, 2);
        heading.Add(menu, 3);
        workspace.Add(heading);

        tools.ColumnSpacing = 6;
        tools.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        tools.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        tools.Add(classPicker);
        tools.Add(rangeButton, 1);
        workspace.Add(tools, 0, 1);

        var views = new Grid();
        views.Add(homeDeck);
        views.Add(myDeck);
        views.Add(matrixCard);
        views.Add(empty);
        workspace.Add(views, 0, 2);

        footer.ColumnSpacing = 6;
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        status.MaxLines = 1;
        status.LineBreakMode = LineBreakMode.TailTruncation;
        status.VerticalOptions = activity.VerticalOptions = LayoutOptions.Center;
        footer.Add(activity);
        footer.Add(status, 1);
        footer.Add(cancel, 2);
        workspace.Add(footer, 0, 3);
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

    private async Task ShowMenuAsync()
    {
        if (transfer != null || menuOpen || modalOpen) return;
        menuOpen = true;
        const string home = "⌂  Startseite", mine = "▤  Meine Klassen", matrix = "▦  Tagesmatrix";
        const string selection = "☷  Klassen auswählen", legend = "ⓘ  Legende", account = "⚙  WebUntis-Konto";
        const string reload = "↻  Aktualisieren";
        try
        {
            string[] options = [home, mine, matrix, selection, legend, account];
            if (headingCollapsed && CanLoadView()) options = [..options, reload];
            var action = await DisplayActionSheetAsync("Fehlzeiten MX", "Schließen", null, options);
            switch (action)
            {
                case home: await OpenSectionAsync(Section.Home); break;
                case mine: await OpenSectionAsync(Section.Mine); break;
                case matrix: await OpenSectionAsync(Section.Matrix); break;
                case selection:
                    if (classes.Length == 0) await LoadClassesAsync(startWithLogin: true);
                    await ShowClassSettingsAsync();
                    break;
                case legend: await DisplayAlertAsync("Legende", LegendText, "Schließen"); break;
                case account: await ShowAccountAsync(); break;
                case reload: await LoadViewAsync(refreshFromWeb: true); break;
            }
        }
        finally { menuOpen = false; }
    }

    private async Task OpenSectionAsync(Section next)
    {
        if (next != section)
        {
            section = next;
            headingCollapsed = false;
            status.Text = "";
            UpdateControls();
        }
        await LoadViewAsync(refreshFromWeb: false);
    }

    private async Task ShowClassSettingsAsync()
    {
        if (transfer != null || modalOpen || classes.Length == 0) return;
        await ShowModalAsync(new ClassSelectionPage(classes, classSelection, ApplyClassSelectionAsync));
    }

    private async Task ApplyClassSelectionAsync(ClassSelection selection)
    {
        try { selection.Save(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            await DisplayAlertAsync("Klassenauswahl", "Die Auswahl konnte nicht gespeichert werden.", "OK");
            return;
        }
        classSelection = selection;
        SynchronizeClassViews();
        UpdateControls();
        await LoadViewAsync(refreshFromWeb: false);
    }

    private async Task ShowDeckPickerAsync(MatrixDeck deck)
    {
        if (menuOpen || modalOpen || loginLayer.IsVisible) return;
        menuOpen = true;
        try
        {
            var entries = deck.Entries;
            var names = entries.Select(entry => entry.Class.Name).ToArray();
            var labels = names.Select((name, i) => names.Count(other => other == name) == 1 ? name : $"{name} · {i + 1}").ToArray();
            var choice = await DisplayActionSheetAsync("Klasse öffnen", "Schließen", null, labels);
            if (ReferenceEquals(entries, deck.Entries))
                deck.Select(Array.IndexOf(labels, choice));
        }
        finally { menuOpen = false; }
    }

    private async Task LoadClassesAsync(bool startWithLogin)
    {
        await RunAsync(async (service, progress, token) =>
        {
            var (available, year) = await service.ClassesAsync(progress, token, startWithLogin);
            token.ThrowIfCancellationRequested();
            if (schoolYear != year)
            {
                yearlyMatrices.Clear();
                homeDeck.SetClasses([], CreateDeckCard);
                myDeck.SetClasses([], CreateDeckCard);
                matrixCard.Clear("", ChooseClass);
            }
            if (selectedRange is { } saved && (!year.Contains(saved.From) || !year.Contains(saved.To)))
            {
                selectedRange = null;
                matrixCard.Clear("", ChooseClass);
            }
            schoolYear = year;
            classes = available;
            FillClassPicker();
            SynchronizeClassViews();
            status.Text = $"{classes.Length} Klassen und Gruppen verfügbar";
            UpdateControls();
            if (CanLoadView()) await LoadActiveViewAsync(service, progress, token, refreshFromWeb: false);
        });
    }

    private void FillClassPicker()
    {
        var prior = classPicker.SelectedItem as Student;
        updatingClasses = true;
        try
        {
            classPicker.ItemsSource = classes;
            var current = classes.FirstOrDefault(item => item.Id == prior?.Id);
            classPicker.SelectedItem = current;
            if (current == null) matrixCard.Clear("", ChooseClass);
        }
        finally { updatingClasses = false; }
    }

    private void SynchronizeClassViews()
    {
        homeDeck.SetClasses(classes.Where(item => classSelection.Home.Contains(item.Id)), CreateDeckCard);
        myDeck.SetClasses(classes.Where(item => classSelection.Mine.Contains(item.Id)), CreateDeckCard);
        var availableIds = homeDeck.Entries.Concat(myDeck.Entries).Select(entry => entry.Class.Id);
        foreach (var id in yearlyMatrices.Keys.Except(availableIds).ToArray())
            yearlyMatrices.Remove(id);
    }

    private bool CanLoadView() => schoolYear != null && (section switch
    {
        Section.Home => homeDeck.Active != null,
        Section.Mine => myDeck.Active != null,
        _ => classPicker.SelectedItem is Student
    });

    private Task LoadViewAsync(bool refreshFromWeb) => CanLoadView()
        ? RunAsync((service, progress, token) => LoadActiveViewAsync(service, progress, token, refreshFromWeb))
        : Task.CompletedTask;

    private async Task LoadActiveViewAsync(AndroidReports service, IProgress<string> progress, CancellationToken token, bool refreshFromWeb)
    {
        if (schoolYear is not { } year) return;
        var annual = year.ThroughToday();
        if (section == Section.Home)
        {
            await LoadHomeAsync(annual, service, progress, token, refreshFromWeb);
            return;
        }

        var isMine = section == Section.Mine;
        var mine = isMine ? myDeck.Active : null;
        var choice = isMine ? mine?.Class : classPicker.SelectedItem as Student;
        var card = isMine ? mine?.Card : matrixCard;
        if (choice == null || card == null) return;
        var range = isMine ? annual : selectedRange ?? annual;
        var complete = await FetchCardAsync(card, choice, range, service, progress, token, refreshFromWeb, yearly: isMine);
        status.Text = complete ? $"{choice.Name} · Tagesmatrix geöffnet" : "WebUntis hat Berichte nur teilweise geliefert.";
    }

    private async Task LoadHomeAsync(ReportRange range, AndroidReports service, IProgress<string> progress,
        CancellationToken token, bool refreshFromWeb)
    {
        var incomplete = 0;
        foreach (var (choice, card) in homeDeck.Entries.OrderBy(entry => entry != homeDeck.Active))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var classProgress = new InlineProgress<string>(message => progress.Report($"{choice.Name} · {message}"));
                if (!await FetchCardAsync(card, choice, range, service, classProgress, token, refreshFromWeb, yearly: true))
                    incomplete++;
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is InvalidOperationException or TimeoutException or IOException or InvalidDataException)
            {
                if (browser.Browser is { RendererGone: true }) throw;
                if (card.Matrix == null) card.Clear(choice.Name, error.Message.Split('\n')[0]);
                incomplete++;
            }
        }
        var loaded = homeDeck.Entries.Count(entry => entry.Card.Matrix != null);
        status.Text = incomplete == 0 ? $"{loaded} Klassen geladen" : $"{loaded} Klassen geladen · {incomplete} Abrufe unvollständig";
    }

    private async Task<bool> FetchCardAsync(MatrixCard card, Student choice, ReportRange range, AndroidReports service,
        IProgress<string> progress, CancellationToken token, bool refreshFromWeb, bool yearly)
    {
        if (card.Matrix is not { } current || current.ClassId != choice.Id || current.From != range.From || current.To > range.To)
            card.Clear(choice.Name, "Berichte werden geladen …");
        if (!refreshFromWeb)
        {
            if (yearly && yearlyMatrices.TryGetValue(choice.Id, out var stored) && stored.From == range.From && stored.To == range.To)
            {
                if (!ReferenceEquals(card.Matrix, stored)) card.ShowMatrix(stored);
                return true;
            }
            if (card.Matrix is { HasCompleteReports: true } shown && shown.To == range.To) return true;
        }

        try
        {
            var next = await service.MatrixAsync(choice, range, progress, token);
            token.ThrowIfCancellationRequested();
            if (next.HasCompleteReports && yearly) yearlyMatrices[choice.Id] = next;
            if (next.HasCompleteReports || card.Matrix is not { HasCompleteReports: true }) card.ShowMatrix(next);
            return next.HasCompleteReports;
        }
        catch (OperationCanceledException)
        {
            if (card.Matrix == null) card.Clear(choice.Name, "Abruf abgebrochen.");
            throw;
        }
        catch
        {
            if (card.Matrix == null) card.Clear(choice.Name, "Daten konnten gerade nicht geladen werden.");
            throw;
        }
    }

    private async Task ShowRangeAsync()
    {
        if (transfer != null || modalOpen || schoolYear is not { } year) return;
        await ShowModalAsync(new ReportRangePage(year, selectedRange ?? year.ThroughToday(), ApplyRangeAsync));
    }

    private async Task ApplyRangeAsync(ReportRange? value)
    {
        if (value == selectedRange || schoolYear is not { } year) return;
        selectedRange = value;
        var range = value ?? year.ThroughToday();
        if (matrixCard.Matrix is { } previous && (previous.From != range.From || previous.To != range.To))
            matrixCard.Clear(previous.ClassName, "Berichte werden geladen …");
        UpdateControls();
        await LoadViewAsync(refreshFromWeb: false);
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
        catch (OperationCanceledException) { status.Text = "Abruf abgebrochen"; }
        catch (Exception error)
        {
            status.Text = "Abruf fehlgeschlagen";
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
        if (classes.Length == 0)
        {
            await LoadClassesAsync(startWithLogin: true);
            return;
        }
        var action = await DisplayActionSheetAsync("WebUntis-Konto", "Schließen", "Abmelden", "Klassenliste aktualisieren");
        if (action == "Klassenliste aktualisieren") await LoadClassesAsync(startWithLogin: false);
        else if (action == "Abmelden")
        {
            await RunAsync(async (service, _, _) =>
            {
                schoolYear = selectedRange = null;
                classes = [];
                yearlyMatrices.Clear();
                FillClassPicker();
                SynchronizeClassViews();
                UpdateControls();
                await service.SignOutAsync();
                status.Text = "Von WebUntis abgemeldet";
            });
        }
    }

    private MatrixCard CreateDeckCard() => CreateMatrixCard(inDeck: true);

    private MatrixCard CreateMatrixCard(bool inDeck = false)
    {
        var card = new MatrixCard(inDeck);
        card.DaySelected += async (data, student, day, cell) =>
        {
            if (transfer != null || modalOpen) return;
            await ShowModalAsync(new DayDetailPage(student.Name, day, cell, DayDetails.Create(student, day, cell, data.SchoolPeriods)));
        };
        card.NotesRequested += async data =>
        {
            if (transfer == null)
                await DisplayAlertAsync("Abrufhinweise", string.Join(Environment.NewLine + Environment.NewLine, data.Notes), "Schließen");
        };
        card.ZoomRequested += async canvas => await ShowZoomAsync(canvas);
        card.MenuRequested += async () => await ShowMenuAsync();
        card.VerticalScrolled += (distance, atTop) =>
        {
            if (!ReferenceEquals(card, ActiveCard)) return;
            var collapse = distance >= 24 && !atTop;
            var expand = distance <= -8 || atTop;
            if (headingCollapsed ? !expand : !collapse) return;
            headingCollapsed = collapse;
            UpdateHeading();
            UpdateFooter();
        };
        return card;
    }

    private async Task ShowZoomAsync(MatrixCanvas canvas)
    {
        if (transfer != null || modalOpen) return;
        var zoom = await DisplayActionSheetAsync("Matrixgröße", "Schließen", null, "65 %", "80 %", "100 %", "125 %", "150 %", "180 %");
        if (zoom?.EndsWith(" %", StringComparison.Ordinal) == true &&
            int.TryParse(zoom.AsSpan(0, zoom.Length - 2), CultureInfo.InvariantCulture, out var percent))
            canvas.SetZoom(percent / 100f);
    }

    private async Task ShowModalAsync(ContentPage page)
    {
        if (modalOpen || Window == null) return;
        modalOpen = true;
        page.Disappearing += (_, _) => modalOpen = false;
        try { await Navigation.PushModalAsync(page); }
        catch (InvalidOperationException) { modalOpen = false; }
    }

    private void UpdateControls()
    {
        var busy = transfer != null;
        var signedIn = classes.Length > 0;
        var homeVisible = signedIn && section == Section.Home && homeDeck.Active != null;
        var mineVisible = signedIn && section == Section.Mine && myDeck.Active != null;
        var matrixVisible = signedIn && section == Section.Matrix;
        sectionTitle.Text = signedIn ? section switch
        {
            Section.Home => "Startseite",
            Section.Mine => "Meine Klassen",
            _ => "Tagesmatrix"
        } : "Berufskolleg für TECHNIK Düren";

        homeDeck.IsVisible = homeVisible;
        myDeck.IsVisible = mineVisible;
        matrixCard.IsVisible = matrixVisible;
        empty.IsVisible = !homeVisible && !mineVisible && !matrixVisible;
        emptyText.Text = !signedIn ? "Melde dich bei WebUntis an, um deine Klassenliste zu laden."
            : $"Wähle Klassen für „{sectionTitle.Text}“ aus.";
        signIn.IsVisible = !signedIn;
        chooseClasses.IsVisible = signedIn;
        menu.IsEnabled = signIn.IsEnabled = chooseClasses.IsEnabled = !busy;
        refresh.IsVisible = signedIn && CanLoadView();
        refresh.IsEnabled = !busy;
        classPicker.IsVisible = rangeButton.IsVisible = matrixVisible;
        classPicker.IsEnabled = rangeButton.IsEnabled = !busy;
        tools.IsVisible = matrixVisible;
        myDeck.SetNavigationEnabled(!busy);
        activity.IsVisible = activity.IsRunning = busy;
        cancel.IsVisible = cancel.IsEnabled = busy;
        if (!(homeVisible || mineVisible || matrixVisible && classPicker.SelectedItem != null)) headingCollapsed = false;
        UpdateHeading();
        UpdateFooter();
    }

    private MatrixCard? ActiveCard => section switch
    {
        Section.Home => homeDeck.Active?.Card,
        Section.Mine => myDeck.Active?.Card,
        _ => matrixCard
    };

    private void UpdateHeading()
    {
        heading.IsVisible = !headingCollapsed;
        ActiveCard?.SetCompactMenu(headingCollapsed, transfer == null);
    }

    private void UpdateFooter()
    {
        var landscape = workspace.Height > 0 && workspace.Width > workspace.Height;
        footer.IsVisible = transfer != null || (!landscape && !headingCollapsed && !string.IsNullOrWhiteSpace(status.Text));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
