using Microsoft.Maui.Dispatching;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;
using NativeDialog = Android.App.AlertDialog;

namespace WebUntisAuswertung.Mobile;

internal sealed partial class MatrixPage : ContentPage
{
    private enum Section { Classes, Matrix }

    private const string ChooseClass = "Wähle eine Klasse.";
    private const string InitialUpdate = "Für diese Klasse liegt noch kein Datenstand vor.";
    private const string HeadingAnimation = "SlideHeading";
    private static readonly GridLength BrowserToolbarHeight = new(62);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly BrowserSurface browser = new();
    private readonly RowDefinition browserToolbar = new(new GridLength(0));
    private readonly Grid loginLayer = new() { IsVisible = false, BackgroundColor = Surface };
    private readonly Grid root;
    private readonly Label loginAddress = MobileStyle.Label(WebUntisReports.Origin, 12, Muted);
    private readonly Label browserTitle = MobileStyle.Label("WebUntis-Anmeldung", 14, bold: true);
    private readonly Button browserBack = MobileStyle.Button("‹");
    private readonly Button browserNext = MobileStyle.Button("Weiter", primary: true);
    private readonly Button browserClose = MobileStyle.Button("×");
    private readonly Grid workspace = new() { BackgroundColor = Surface };
    private readonly Grid heading = new() { VerticalOptions = LayoutOptions.Start };
    private readonly Grid headingHost = new() { IsClippedToBounds = true };
    private readonly Label sectionTitle = MobileStyle.Label("Meine Klassen", 12, Muted);
    private readonly Button menu = MobileStyle.Button("⋮");
    private readonly Button refresh = MobileStyle.Button("↻");
    private readonly Button rangeButton = MobileStyle.Button("Zeitraum");
    private readonly Button signIn = MobileStyle.Button("Bei WebUntis anmelden", primary: true);
    private readonly Button reconnect = MobileStyle.Button("Anmelden", primary: true);
    private readonly Button chooseClasses = MobileStyle.Button("Klassen auswählen", primary: true);
    private readonly Button cancel = MobileStyle.Button("Abbrechen");
    private readonly ActivityIndicator activity = new() { Color = Accent, HeightRequest = 20, WidthRequest = 20, IsVisible = false };
    private readonly Label status = MobileStyle.Label("", 13, Muted);
    private readonly Label refreshStatus = MobileStyle.Label("", 13, Muted);
    private readonly ProgressBar loadingBar = new() { HeightRequest = 5, ProgressColor = Accent, BackgroundColor = Tint, IsVisible = false };
    private readonly Label emptyText = MobileStyle.Label("", 15, Muted);
    private readonly Picker classPicker = new() { Title = "Klasse auswählen", TextColor = Ink, TitleColor = Muted };
    private readonly MatrixDeck deck = new();
    private readonly Grid tools = new();
    private readonly Grid footer = new();
    private readonly MatrixCard matrixCard;
    private readonly Border empty;
    private readonly AndroidMatrixCache cache = new();
    private readonly CancellationTokenSource lifetime = new();
    private NativeDialog? activeDialog;
    private ClassSelection classSelection = ClassSelection.Load();
    private Section section;
    private AndroidReports? reports;
    private Student[] classes = [];
    private ReportRange? schoolYear;
    private ReportRange? selectedRange;
    private CancellationTokenSource? transfer;
    private TaskCompletionSource? transferFinished;
    private IDispatcherTimer? refreshTimer;
    private IDispatcherTimer? absencePreloadTimer;
    private bool backgroundTransfer;
    private bool preloadingAbsences;
    private double? transferProgress;
    private bool sessionReady;
    private bool signInRequired;
    private bool signInDismissed;
    private bool profileReady;
    private bool restoreStarted;
    private bool restoring = true;
    private bool changingSelection;
    private bool updatingClasses;
    private bool modalOpen;
    private bool menuOpen;
    private bool headingCollapsed;
    private bool excuseVisible;
    private bool locked;
    private double headingHeight = 44;

    private bool ForegroundTransfer => transfer != null && !backgroundTransfer;
    private bool InteractiveTransferVisible => transfer != null && !preloadingAbsences;
    private bool VisibleTransfer => InteractiveTransferVisible || deckRefresh != null;
    private bool ControlsLocked => locked || ForegroundTransfer || restoring || changingSelection;

    public MatrixPage()
    {
        Title = "WebUntis-FehlzeitenMX";
        SafeAreaEdges = SafeAreaEdges.All;
        BackgroundColor = Surface;
        matrixCard = CreateMatrixCard();
        matrixCard.Clear("", ChooseClass);
        deck.SelectionChanged += () =>
        {
            if (preloadingAbsences) transfer?.Cancel();
            ShowHeadingIfNoMatrix();
            ScheduleAbsencePreload();
        };
        deck.ClassPickerRequested += async () => await ShowDeckPickerAsync();
        emptyText.HorizontalTextAlignment = TextAlignment.Center;
        empty = Card(new VerticalStackLayout { Spacing = 12, Children = { emptyText, signIn, chooseClasses } });
        empty.VerticalOptions = LayoutOptions.Center;
        classPicker.ItemDisplayBinding = new Binding(nameof(Student.Name));
        classPicker.SelectedIndexChanged += async (_, _) =>
        {
            if (updatingClasses || ControlsLocked) return;
            UpdateControls();
            await OpenDailyMatrixAsync();
        };
        signIn.Clicked += async (_, _) => await LoadClassesAsync();
        reconnect.Clicked += async (_, _) => await LoadClassesAsync();
        SemanticProperties.SetDescription(reconnect, "Bei WebUntis anmelden");
        chooseClasses.Clicked += async (_, _) => await ShowClassSettingsAsync();
        refresh.Clicked += async (_, _) => await RefreshVisibleAsync();
        rangeButton.Clicked += async (_, _) => await ShowRangeAsync();
        menu.Clicked += async (_, _) => await ShowMenuAsync();
        cancel.Clicked += (_, _) => CancelTransfer();

        root = new Grid { RowDefinitions = { browserToolbar, new(GridLength.Star) } };
        root.Add(refreshBrowser, 0, 1);
        root.Add(browser, 0, 1);
        browser.InputTransparent = true;
        root.Add(workspace);
        Grid.SetRowSpan(workspace, 2);
        root.Add(loginLayer);
        BuildWorkspace();
        BuildLogin();
        root.SizeChanged += (_, _) => SizeBrowser();
        Content = root;
        UpdateControls();

        browser.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler == null) return;
            transfer?.Cancel();
            deckRefresh?.Cancel();
            refreshTimer?.Stop();
            sessionReady = false;
            profileReady = false;
            if (browser.Browser is { } previous) previous.LoginAddressChanged -= UpdateLoginAddress;
            reports = null;
        };
        refreshBrowser.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler == null) return;
            deckRefresh?.Cancel();
            refreshReports = null;
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
        headingHost.Add(heading);
        workspace.Add(headingHost);

        tools.ColumnSpacing = 6;
        tools.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        tools.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        tools.Add(classPicker);
        tools.Add(rangeButton, 1);
        workspace.Add(tools, 0, 1);

        var views = new Grid();
        views.Add(deck);
        views.Add(matrixCard);
        views.Add(empty);
        workspace.Add(views, 0, 2);

        footer.ColumnSpacing = 6;
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        footer.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        footer.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        status.MaxLines = refreshStatus.MaxLines = 1;
        status.LineBreakMode = refreshStatus.LineBreakMode = LineBreakMode.TailTruncation;
        status.VerticalOptions = refreshStatus.VerticalOptions = activity.VerticalOptions = LayoutOptions.Center;
        footer.Add(activity);
        footer.Add(status, 1);
        footer.Add(refreshStatus, 1);
        footer.Add(cancel, 2);
        footer.Add(reconnect, 2);
        loadingBar.Margin = new Thickness(0, 3, 0, 0);
        SemanticProperties.SetDescription(loadingBar, "Fortschritt des Abrufs");
        footer.Add(loadingBar, 0, 1);
        Grid.SetColumnSpan(loadingBar, 3);
        workspace.Add(footer, 0, 3);
    }

    private void BuildLogin()
    {
        browserBack.WidthRequest = browserClose.WidthRequest = 44;
        SemanticProperties.SetDescription(browserBack, "Im Anmeldefenster zurück");
        browserBack.Clicked += (_, _) =>
        {
            if (browser.Browser?.GoBack() != true) CancelTransfer();
        };
        SemanticProperties.SetDescription(browserNext, "Nach der Anmeldung fortfahren");
        browserNext.Clicked += (_, _) => reports?.ContinueAfterLogin();
        SemanticProperties.SetDescription(browserClose, "Anmeldung abbrechen");
        browserClose.Clicked += (_, _) =>
        {
            if (excuseVisible) reports?.CloseExcuse();
            else CancelTransfer();
        };
        var toolbar = new Grid
        {
            Padding = new Thickness(8),
            ColumnSpacing = 8,
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
            BackgroundColor = Surface
        };
        loginAddress.VerticalOptions = LayoutOptions.Center;
        loginAddress.LineBreakMode = LineBreakMode.TailTruncation;
        browserTitle.MaxLines = 1;
        browserTitle.LineBreakMode = LineBreakMode.TailTruncation;
        toolbar.Add(browserBack);
        toolbar.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Children = { browserTitle, loginAddress }
        }, 1);
        toolbar.Add(browserNext, 2);
        toolbar.Add(browserClose, 3);
        loginLayer.Add(toolbar);
    }

    protected override bool OnBackButtonPressed()
    {
        if (excuseVisible)
        {
            reports?.CloseExcuse();
            return true;
        }
        if (loginLayer.IsVisible)
        {
            if (browser.Browser?.GoBack() != true) CancelTransfer();
            return true;
        }
        if (!ForegroundTransfer) return base.OnBackButtonPressed();
        CancelTransfer();
        return true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (locked) return;
        ResumeRefresh();
        if (restoreStarted)
        {
            ScheduleAbsencePreload();
            return;
        }
        restoreStarted = true;
        var token = lifetime.Token;
        try
        {
            if (await cache.ReadClassesAsync(token) is { } stored)
            {
                token.ThrowIfCancellationRequested();
                schoolYear = stored.SchoolYear;
                classes = stored.Classes;
                FillClassPicker();
                RebuildDeck(firstCard: true);
                UpdateControls();
                var loaded = await LoadSavedMatricesAsync(stored.SchoolYear, token);
                status.Text = loaded ? "Gespeicherte Daten" : "Ein gespeicherter Datenstand konnte nicht geöffnet werden.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error) when (AndroidMatrixCache.StorageFailure(error))
        {
            status.Text = "Der gespeicherte Klassenstand konnte nicht geöffnet werden.";
        }
        finally
        {
            restoring = false;
            UpdateControls();
        }
        await CheckSessionAsync();
    }

    protected override void OnDisappearing()
    {
        absencePreloadTimer?.Stop();
        base.OnDisappearing();
    }

    private void ResumeRefresh()
    {
        if (refreshTimer == null)
        {
            refreshTimer = Dispatcher.CreateTimer();
            refreshTimer.Interval = RefreshInterval;
            refreshTimer.IsRepeating = true;
            refreshTimer.Tick += async (_, _) =>
            {
                if (!sessionReady) await CheckSessionAsync();
                else await RefreshDeckAsync(force: true);
            };
        }
        refreshTimer.Start();
    }

    internal async Task SealAsync()
    {
        if (locked) return;
        locked = true;
        Content.IsVisible = false;
        activeDialog?.Dismiss();
        refreshTimer?.Stop();
        absencePreloadTimer?.Stop();
        var finished = Task.WhenAll(transferFinished?.Task ?? Task.CompletedTask, deckRefreshFinished?.Task ?? Task.CompletedTask);
        lifetime.Cancel();
        try
        {
            while (Navigation.ModalStack.Count > 0) await Navigation.PopModalAsync(animated: false);
        }
        catch (InvalidOperationException) { }
        await finished;
        try
        {
            MobileWebView.PersistProfile();
            if (sessionReady && browser.Browser != null) await SaveSessionAsync(CancellationToken.None);
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or Java.Lang.Exception) { }
        finally
        {
            browser.Handler?.DisconnectHandler();
            refreshBrowser.Handler?.DisconnectHandler();
            lifetime.Dispose();
        }
    }

    private Task CheckSessionAsync()
    {
        if (ControlsLocked || menuOpen || modalOpen || transfer != null || sessionReady ||
            signInDismissed || classes.Length == 0 || Window == null) return Task.CompletedTask;
        return LoadClassesAsync(background: true);
    }

    private async Task ShowMenuAsync()
    {
        if (ControlsLocked || menuOpen || modalOpen) return;
        string[] actions = ["▤  Meine Klassen", "▦  Tagesmatrix", "↓  Notenrechner-CSV speichern …", "⚙  Einstellungen"];
        menuOpen = true;
        int action;
        try { action = await ChooseAsync("Fehlzeiten MX", actions); }
        finally { menuOpen = false; }

        if (locked) return;
        switch (action)
        {
            case 0: await OpenSectionAsync(Section.Classes); break;
            case 1: await OpenSectionAsync(Section.Matrix); break;
            case 2: await ExportRosterAsync(); break;
            case 3: await ShowSettingsAsync(); break;
        }
    }

    private Task ShowSettingsAsync() => ShowModalAsync(new SettingsPage(sessionReady, classes.Length > 0,
        () => LoadClassesAsync(), SignOutAsync, ChooseClassesFromSettingsAsync));

    private async Task ChooseClassesFromSettingsAsync()
    {
        if (classes.Length == 0) await LoadClassesAsync(refreshView: false);
        await ShowClassSettingsAsync();
    }

    private async Task OpenSectionAsync(Section next)
    {
        if (next != section)
        {
            section = next;
            SetHeadingCollapsed(false, animate: false);
            if (transfer == null) status.Text = "";
            UpdateControls();
        }
        if (section == Section.Classes) await RefreshDeckAsync();
        else await OpenDailyMatrixAsync();
    }

    private async Task ShowClassSettingsAsync()
    {
        if (ControlsLocked || modalOpen || classes.Length == 0) return;
        await ShowModalAsync(new ClassSelectionPage(classes, classSelection, ApplyClassSelectionAsync));
    }

    private async Task ApplyClassSelectionAsync(ClassSelection selection)
    {
        if (ControlsLocked) return;
        changingSelection = true;
        UpdateControls();
        try
        {
            await StopDeckRefreshAsync();
            if (!await FinishBackgroundTransferAsync() || locked) return;
            try { selection.Save(); }
            catch (Exception error) when (AndroidMatrixCache.StorageFailure(error) || error is InvalidOperationException)
            {
                await NoticeAsync("Klassenauswahl", "Die Auswahl konnte nicht gespeichert werden.");
                return;
            }
            classSelection = selection;
            RebuildDeck(firstCard: true);
            UpdateControls();
            var token = lifetime.Token;
            var pruned = await TryCacheAsync(() => cache.PruneAsync(deck.Entries.Select(item => item.Class.Id), token));
            var loaded = schoolYear is not { } year || await LoadSavedMatricesAsync(year, token);
            if (!pruned || !loaded) status.Text = "Der lokale Datenspeicher konnte nicht vollständig aktualisiert werden.";
        }
        catch (OperationCanceledException) when (locked) { }
        finally
        {
            changingSelection = false;
            UpdateControls();
        }
        await RefreshDeckAsync();
    }

    private async Task ShowDeckPickerAsync()
    {
        if (ControlsLocked || menuOpen || modalOpen) return;
        menuOpen = true;
        try
        {
            var entries = deck.Entries;
            var names = entries.Select(entry => entry.Class.Name).ToArray();
            var labels = names.Select((name, i) => names.Count(other => other == name) == 1 ? name : $"{name} · {i + 1}").ToArray();
            var choice = await ChooseAsync("Klasse öffnen", labels);
            if (!locked && ReferenceEquals(entries, deck.Entries)) deck.Select(choice);
        }
        finally { menuOpen = false; }
    }

    private async Task LoadClassesAsync(bool refreshView = true, bool force = false, bool background = false)
    {
        if (changingSession) return;
        if (!background) signInDismissed = false;
        changingSession = true;
        bool opened;
        try { opened = await RunAsync(LoadAsync, background); }
        finally { changingSession = false; }
        if (!opened || !refreshView) return;
        if (section == Section.Classes) await RefreshDeckAsync(force);
        else await OpenDailyMatrixAsync(force);

        async Task LoadAsync(AndroidReports service, IProgress<string> progress, CancellationToken token)
        {
            await StopDeckRefreshAsync();
            token.ThrowIfCancellationRequested();
            var (available, year) = await service.ClassesAsync(progress, token, allowSignIn: !signInDismissed);
            token.ThrowIfCancellationRequested();
            if (schoolYear != year)
            {
                deck.SetClasses([], CreateDeckCard);
                matrixCard.Clear("", ChooseClass);
            }
            if (selectedRange is { } saved && (!year.Contains(saved.From) || !year.Contains(saved.To)))
                selectedRange = null;
            sessionReady = true;
            schoolYear = year;
            classes = available;
            FillClassPicker();
            RebuildDeck();
            UpdateControls();
            var savedList = await TryCacheAsync(() => cache.SaveClassesAsync(new CachedClasses(available, year), token));
            var pruned = await TryCacheAsync(() => cache.PruneAsync(deck.Entries.Select(item => item.Class.Id), token));
            var loaded = await LoadSavedMatricesAsync(year, token);
            status.Text = savedList && pruned && loaded ? $"{classes.Length} Klassen und Gruppen verfügbar"
                : "Klassen geöffnet · lokaler Datenspeicher nicht vollständig verfügbar";
        }
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

    private void RebuildDeck(bool firstCard = false)
    {
        deck.SetClasses(classSelection.OrderClasses(classes), CreateDeckCard, firstCard);
        ShowHeadingIfNoMatrix();
    }

    private async Task<bool> LoadSavedMatricesAsync(ReportRange year, CancellationToken token = default)
    {
        var annual = year.ThroughToday();
        var readable = true;
        foreach (var (choice, card) in deck.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (card.Matrix != null) continue;
            try
            {
                var stored = await cache.ReadMatrixAsync(choice, annual, token);
                token.ThrowIfCancellationRequested();
                if (stored != null) card.ShowMatrix(stored, fromDevice: true);
            }
            catch (Exception error) when (AndroidMatrixCache.StorageFailure(error))
            {
                readable = false;
            }
        }
        return readable;
    }

    private bool CanLoadView() => schoolYear != null && (section == Section.Classes
        ? deck.Active != null : classPicker.SelectedItem is Student);

    private async Task RefreshVisibleAsync()
    {
        if (preloadingAbsences && !await FinishBackgroundTransferAsync()) return;
        if (!sessionReady)
        {
            await LoadClassesAsync(force: true);
            return;
        }
        if (section == Section.Classes) await RefreshDeckAsync(force: true);
        else await OpenDailyMatrixAsync(force: true);
    }

    private async Task OpenDailyMatrixAsync(bool force = false)
    {
        if (section != Section.Matrix || classPicker.SelectedItem is not Student choice || schoolYear is not { } year)
        {
            if (section == Section.Matrix) matrixCard.Clear("", ChooseClass);
            return;
        }
        var range = selectedRange ?? year.ThroughToday();
        bool Matches(AttendanceMatrix? matrix) => matrix != null && matrix.ClassId == choice.Id && matrix.From == range.From &&
            (selectedRange == null ? matrix.To <= range.To : matrix.To == range.To);

        if (!Matches(matrixCard.Matrix))
        {
            var other = deck.Entries.FirstOrDefault(entry => entry.Class.Id == choice.Id)?.Card;
            if (other?.Matrix is { } snapshot && Matches(snapshot)) matrixCard.ShowMatrix(snapshot, other.IsFromDevice);
            else matrixCard.Clear(choice.Name, sessionReady ? "Berichte werden geladen …" :
                "Für diesen Zeitraum liegt kein gespeicherter Stand vor. Tippe oben auf Aktualisieren, um die Daten abzurufen.");
        }
        ShowHeadingIfNoMatrix();
        if (!sessionReady || !force && !NeedsRefresh(matrixCard.Matrix, range))
        {
            ScheduleAbsencePreload();
            return;
        }

        await RunAsync(async (service, progress, token) =>
        {
            try
            {
                SetTransferProgress(0, AndroidReports.MatrixSteps);
                var data = await service.MatrixAsync(choice, range, progress, token, allowSignIn: true,
                    reportProgress: SetTransferProgress);
                var matrix = await Task.Run(data.Build, token);
                token.ThrowIfCancellationRequested();
                if (matrix.HasCompleteReports || matrixCard.Matrix is not { HasCompleteReports: true })
                    matrixCard.ShowMatrix(matrix);
                SetTransferProgress(AndroidReports.MatrixSteps, AndroidReports.MatrixSteps);
                status.Text = matrix.HasCompleteReports ? $"{choice.Name} · Tagesmatrix geöffnet" : "WebUntis hat Berichte nur teilweise geliefert.";
            }
            catch
            {
                if (matrixCard.Matrix == null) matrixCard.Clear(choice.Name, "Daten konnten gerade nicht geladen werden.");
                throw;
            }
        });
    }

    private async Task ShowRangeAsync()
    {
        if (ControlsLocked || modalOpen || schoolYear is not { } year) return;
        await ShowModalAsync(new ReportRangePage(year, selectedRange ?? year.ThroughToday(), ApplyRangeAsync));
    }

    private async Task ApplyRangeAsync(ReportRange? value)
    {
        if (value == selectedRange || schoolYear == null) return;
        selectedRange = value;
        matrixCard.Clear("", ChooseClass);
        await OpenDailyMatrixAsync();
    }

    private void ScheduleAbsencePreload()
    {
        if (locked || !sessionReady || Window == null) return;
        if (absencePreloadTimer == null)
        {
            absencePreloadTimer = Dispatcher.CreateTimer();
            absencePreloadTimer.Interval = TimeSpan.FromMilliseconds(400);
            absencePreloadTimer.IsRepeating = false;
            absencePreloadTimer.Tick += async (_, _) => await PreloadActiveAbsencesAsync();
        }
        absencePreloadTimer.Stop();
        absencePreloadTimer.Start();
    }

    private async Task PreloadActiveAbsencesAsync()
    {
        if (!sessionReady || ControlsLocked || menuOpen || transfer != null ||
            (modalOpen && Navigation.ModalStack.LastOrDefault() is not DayDetailPage) ||
            ActiveCard?.Matrix is not { } matrix || !classSelection.Teachers.Contains(matrix.ClassId) ||
            reports?.HasPreparedAbsences(matrix) == true || !matrix.Cells.Values.Any(HasExcusableEntries)) return;

        await RunAsync(async (service, _, token) =>
        {
            var rows = await service.PrepareAbsencesAsync(new Student(matrix.ClassId, matrix.ClassName),
                new ReportRange(matrix.From, matrix.To), token);
            token.ThrowIfCancellationRequested();
            if (rows != null) matrix.PreparedAbsences = rows;
        }, background: true, quiet: true);
        if (!ReferenceEquals(ActiveCard?.Matrix, matrix)) ScheduleAbsencePreload();
    }

    private async Task<bool> RunAsync(Func<AndroidReports, IProgress<string>, CancellationToken, Task> action,
        bool background = false, bool quiet = false)
    {
        if (locked || transfer != null && (background || !await FinishBackgroundTransferAsync())) return false;
        if (locked || transfer != null || Window == null) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transfer = cancellation;
        transferFinished = completion;
        backgroundTransfer = background;
        preloadingAbsences = quiet;
        transferProgress = null;
        var progress = new InlineProgress<string>(message => { if (!quiet) status.Text = message; });
        if (!quiet)
        {
            status.Text = background ? "Klassen werden aktualisiert …" : "WebUntis wird geöffnet …";
            UpdateControls();
        }
        try
        {
            var previous = browser.Browser;
            if (browser.GetBrowser() is not { } view) return false;
            if (!ReferenceEquals(view, previous))
            {
                if (previous != null) previous.LoginAddressChanged -= UpdateLoginAddress;
                reports = null;
            }
            if (!profileReady)
            {
                var token = cancellation.Token;
                try
                {
                    if (await cache.ReadSessionAsync(token) is { Length: > 0 } saved)
                        await MobileWebView.RestoreMissingCookiesAsync(saved, token);
                }
                catch (Exception error) when (AndroidMatrixCache.StorageFailure(error) || error is TimeoutException) { }
                token.ThrowIfCancellationRequested();
                profileReady = true;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (reports == null)
            {
                reports = new AndroidReports(view, ShowLogin, ShowExcuse, ConfirmSessionAsync);
                view.LoginAddressChanged += UpdateLoginAddress;
            }
            await action(reports, progress, cancellation.Token);
            if (sessionReady && !quiet) await SaveSessionAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!locked && !quiet) status.Text = signInRequired ? "Anmeldung erforderlich" : "Abruf abgebrochen";
        }
        catch (WebUntisSignInRequiredException)
        {
            sessionReady = false;
            signInRequired = true;
            status.Text = "Anmeldung erforderlich";
        }
        catch (Exception error)
        {
            if (!quiet) status.Text = browser.Browser is { RendererGone: true } ? MobileWebView.RendererMessage :
                background ? "Aktualisierung momentan fehlgeschlagen" : "Abruf fehlgeschlagen";
            if (!background && !locked)
            {
                ShowLogin(false);
                await NoticeAsync("WebUntis-FehlzeitenMX", error.Message);
            }
        }
        finally
        {
            transfer = null;
            transferFinished = null;
            backgroundTransfer = false;
            preloadingAbsences = false;
            transferProgress = null;
            try
            {
                if (!locked && Window != null && !quiet)
                {
                    ShowLogin(false);
                    UpdateControls();
                    ScheduleAbsencePreload();
                }
            }
            finally { completion.TrySetResult(); }
        }
        return false;
    }

    private async Task ConfirmSessionAsync(CancellationToken token)
    {
        var wasReady = sessionReady;
        sessionReady = true;
        signInRequired = signInDismissed = false;
        if (wasReady) return;
        MobileWebView.PersistProfile();
        await SaveSessionAsync(token);
    }

    private async Task SaveSessionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var cookies = MobileWebView.CaptureSession();
            if (cookies.Length > 0) await cache.SaveSessionAsync(cookies, token);
        }
        catch (Exception error) when (AndroidMatrixCache.StorageFailure(error))
        {
            status.Text = "Die WebUntis-Sitzung konnte nicht auf dem Gerät gespeichert werden.";
        }
    }

    private async Task<bool> FinishBackgroundTransferAsync()
    {
        if (transfer == null) return true;
        if (!backgroundTransfer || transferFinished == null) return false;
        var finished = transferFinished.Task;
        transfer.Cancel();
        await finished;
        return transfer == null;
    }

    private static async Task<bool> TryCacheAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception error) when (AndroidMatrixCache.StorageFailure(error)) { return false; }
    }

    private void CancelTransfer()
    {
        if (!InteractiveTransferVisible && !loginLayer.IsVisible)
        {
            deckRefresh?.Cancel();
            cancel.IsEnabled = false;
            return;
        }
        if (loginLayer.IsVisible && !excuseVisible) signInDismissed = true;
        cancel.IsEnabled = false;
        transfer?.Cancel();
        if (loginLayer.IsVisible && browser.Browser is { RendererGone: false } view) view.Go("about:blank");
        ShowLogin(false);
    }

    private void ShowLogin(bool visible)
    {
        if (locked || !visible && excuseVisible) return;
        if (visible)
        {
            sessionReady = false;
            signInRequired = true;
        }
        ShowBrowser(visible);
    }

    private void ShowExcuse(bool visible)
    {
        excuseVisible = visible;
        if (locked || !visible && !loginLayer.IsVisible) return;
        browserTitle.Text = visible ? "WebUntis-Entschuldigung" : "WebUntis-Anmeldung";
        browserBack.IsVisible = browserNext.IsVisible = !visible;
        SemanticProperties.SetDescription(browserClose, visible ? "Entschuldigungsformular schließen" : "Anmeldung abbrechen");
        ShowBrowser(visible);
    }

    private void ShowBrowser(bool visible)
    {
        if (Window == null) return;
        if (visible && !loginLayer.IsVisible) UpdateLoginAddress(browser.Browser?.LoginAddress);
        browserToolbar.Height = visible ? BrowserToolbarHeight : new GridLength(0);
        browser.InputTransparent = !visible;
        browser.ZIndex = visible ? 1 : 0;
        loginLayer.IsVisible = visible;
        loginLayer.ZIndex = 2;
        workspace.Opacity = visible ? 0 : 1;
        workspace.InputTransparent = visible;
        AutomationProperties.SetExcludedWithChildren(workspace, visible);
        SizeBrowser();
        browser.Browser?.SetLoginVisible(visible);
    }

    private void SizeBrowser()
    {
        browser.HeightRequest = loginLayer.IsVisible && root.Height > BrowserToolbarHeight.Value
            ? root.Height - BrowserToolbarHeight.Value : -1;
    }

    private void UpdateLoginAddress(string? address)
    {
        loginAddress.Text = Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.GetLeftPart(UriPartial.Authority) : WebUntisReports.Origin;
    }

    private async Task SignOutAsync()
    {
        await RunAsync(async (service, _, _) =>
        {
            sessionReady = false;
            signInRequired = false;
            signInDismissed = true;
            await StopDeckRefreshAsync();
            refreshReports = null;
            if (refreshBrowser.Browser is { RendererGone: false } refreshView) refreshView.Go("about:blank");
            schoolYear = selectedRange = null;
            classes = [];
            FillClassPicker();
            RebuildDeck();
            UpdateControls();
            var erased = await TryCacheAsync(cache.ClearAsync);
            await service.SignOutAsync();
            status.Text = erased ? "Von WebUntis abgemeldet"
                : "Abgemeldet · lokaler Datenspeicher konnte nicht vollständig gelöscht werden";
        });
    }

    private MatrixCard CreateDeckCard() => CreateMatrixCard(inDeck: true);

    private async Task OpenCellExcuseAsync(MatrixCard card, AttendanceMatrix matrix, Student student, DateOnly day)
    {
        if (ControlsLocked || menuOpen || modalOpen || editingClassId != null ||
            !classSelection.Teachers.Contains(matrix.ClassId) || !IsCurrent()) return;
        editingClassId = matrix.ClassId;
        editRevisions[matrix.ClassId] = editRevisions.GetValueOrDefault(matrix.ClassId) + 1;
        try { await RunAsync(EditAsync); }
        finally { editingClassId = null; }

        async Task EditAsync(AndroidReports service, IProgress<string> progress, CancellationToken token)
        {
            if (!IsCurrent()) return;
            var person = AbsenceActions.RequirePerson(matrix, student);
            var deckCard = deck.Entries.FirstOrDefault(entry => entry.Class.Id == matrix.ClassId)?.Card;
            var prepared = matrix.PreparedAbsences ?? deckCard?.Matrix?.PreparedAbsences;
            var cached = prepared is { Length: > 0 } ? AbsenceActions.ForDay(prepared, person, day) : null;
            var live = service.PreparedAbsenceActions(matrix, student, day);
            var rows = live is { Length: > 0 } ? live : cached is { Length: > 0 } ? cached :
                await service.ReadAbsenceActionsAsync(matrix, student, day, progress, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return;
            var choices = AbsenceActions.Available(rows);
            if (choices.Length == 0)
            {
                status.Text = rows.Length == 0 ? "Keine passende WebUntis-Abwesenheit gefunden." :
                    "WebUntis bietet für diese Einträge keinen Entschuldigungsdialog an.";
                await NoticeAsync("Entschuldigung", status.Text);
                return;
            }

            var index = 0;
            if (choices.Length > 1)
            {
                var labels = choices.Select(row => row.Reason.Length == 0 ? AbsenceActions.Period(row) :
                    $"{AbsenceActions.Period(row)}\n{row.Reason}").ToArray();
                index = await ChooseAsync($"Abwesenheit auswählen · {day:dd.MM.yyyy}", labels, token);
            }
            token.ThrowIfCancellationRequested();
            if (index < 0 || !IsCurrent()) return;
            if (!await service.ShowExcuseAsync(matrix, student, day, choices[index], progress, token)) return;

            matrix.PreparedAbsences = null;
            if (deckCard?.Matrix is { } existing) existing.PreparedAbsences = null;
            progress.Report($"{matrix.ClassName} · Fehlzeiten werden neu gelesen …");
            var choice = new Student(matrix.ClassId, matrix.ClassName);
            var range = new ReportRange(matrix.From, matrix.To);
            SetTransferProgress(0, AndroidReports.MatrixSteps);
            var data = await service.MatrixAsync(choice, range, progress, token, allowSignIn: false, afterExcuse: true,
                reportProgress: SetTransferProgress);
            var updated = await Task.Run(data.Build, token);
            token.ThrowIfCancellationRequested();
            if (!updated.HasAbsenceReport)
                throw new InvalidOperationException("Die aktualisierten Fehlzeiten konnten noch nicht gelesen werden. Aktualisiere die Klasse erneut.");
            var sameDeckRange = deckCard?.Matrix is { } deckMatrix && deckMatrix.From == range.From && deckMatrix.To == range.To;
            if (updated.HasCompleteReports && sameDeckRange && deckCard != null)
                await RememberDeckMatrixAsync(service, choice, deckCard, updated, data, token);
            if (!ReferenceEquals(card, deckCard) || !updated.HasCompleteReports) card.ShowMatrix(updated);
            if (matrixCard != card && matrixCard.Matrix is { } daily && daily.ClassId == matrix.ClassId)
            {
                if (daily.From == range.From && daily.To == range.To) matrixCard.ShowMatrix(updated);
                else matrixCard.Clear(choice.Name, "Aktualisiere die Tagesmatrix, um die neuen Fehlzeiten zu laden.");
            }
            SetTransferProgress(AndroidReports.MatrixSteps, AndroidReports.MatrixSteps);
            status.Text = $"{choice.Name} · Aktuelle Fehlzeiten aus WebUntis geladen";
        }

        bool IsCurrent() => ReferenceEquals(card, ActiveCard) && ReferenceEquals(card.Matrix, matrix);
    }

    private static bool HasExcusableEntries(DayCell cell) =>
        cell.Entries.Any(item => item.Kind is EntryKind.Absent or EntryKind.Late);

    private async Task ShowDayDetailsAsync(MatrixCard card, string studentId, DateOnly day)
    {
        if (ControlsLocked || modalOpen || menuOpen || !ReferenceEquals(card, ActiveCard) || card.Matrix is not { } matrix ||
            matrix.Students.FirstOrDefault(student => student.Id == studentId) is not { } person ||
            !matrix.Cells.TryGetValue((studentId, day), out var cell)) return;

        var detail = DayDetails.Create(person, day, cell, matrix.SchoolPeriods);
        var canEdit = classSelection.Teachers.Contains(matrix.ClassId) && HasExcusableEntries(cell);
        await ShowModalAsync(new DayDetailPage(person.Name, day, cell, detail, canEdit ? OpenFromDetailsAsync : null));
        if (canEdit) ScheduleAbsencePreload();

        async Task OpenFromDetailsAsync()
        {
            if (!ReferenceEquals(card, ActiveCard) || card.Matrix is not { } current || current.ClassId != matrix.ClassId ||
                current.Students.FirstOrDefault(student => student.Id == studentId) is not { } selected) return;
            await OpenCellExcuseAsync(card, current, selected, day);
        }
    }

    private MatrixCard CreateMatrixCard(bool inDeck = false)
    {
        var card = new MatrixCard(inDeck);
        card.DaySelected += async (_, student, day, _) => await ShowDayDetailsAsync(card, student.Id, day);
        card.AbsenceRequested += async (matrix, student, day, _) => await OpenCellExcuseAsync(card, matrix, student, day);
        card.NotesRequested += async data =>
        {
            if (!ControlsLocked && !menuOpen && !modalOpen)
                await NoticeAsync("Abrufhinweise", string.Join(Environment.NewLine + Environment.NewLine, data.Notes));
        };
        card.VerticalScrolled += (distance, atTop) =>
        {
            if (!ReferenceEquals(card, ActiveCard)) return;
            if (!headingCollapsed && distance >= 24 && !atTop) SetHeadingCollapsed(true);
            else if (headingCollapsed && (distance <= -8 || atTop)) SetHeadingCollapsed(false);
        };
        return card;
    }

    private async Task ShowModalAsync(ContentPage page)
    {
        if (locked || modalOpen || Window == null) return;
        modalOpen = true;
        page.Disappearing += (_, _) => modalOpen = false;
        try { await Navigation.PushModalAsync(page); }
        catch (InvalidOperationException) { modalOpen = false; }
    }

    private Task<int> ChooseAsync(string title, string[] labels, CancellationToken token = default) =>
        ShowDialogAsync(title, labels, null, token);

    private Task NoticeAsync(string title, string message) => locked
        ? Task.CompletedTask : ShowDialogAsync(title, null, message, lifetime.Token);

    private async Task<int> ShowDialogAsync(string title, string[]? items, string? message, CancellationToken token)
    {
        if (locked || token.IsCancellationRequested || activeDialog != null ||
            Platform.CurrentActivity is not { IsFinishing: false, IsDestroyed: false } activity) return -1;
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var builder = new NativeDialog.Builder(activity);
        builder.SetTitle(title);
        builder.SetNegativeButton("Schließen", (_, _) => result.TrySetResult(-1));
        if (items != null) builder.SetItems(items, (_, args) => result.TrySetResult(args.Which));
        else builder.SetMessage(message);
        using var dialog = builder.Create();
        if (dialog == null) return -1;
        activeDialog = dialog;
        dialog.DismissEvent += (_, _) => result.TrySetResult(-1);
        try
        {
            dialog.Show();
            return await result.Task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return -1; }
        catch (global::Android.Views.WindowManagerBadTokenException) { return -1; }
        finally
        {
            activeDialog = null;
            dialog.Dismiss();
        }
    }

    private void UpdateControls()
    {
        var hasClasses = classes.Length > 0;
        var deckVisible = hasClasses && section == Section.Classes && deck.Active != null;
        var matrixVisible = hasClasses && section == Section.Matrix;
        sectionTitle.Text = !hasClasses ? "Berufskolleg für TECHNIK Düren" :
            section == Section.Classes ? "Meine Klassen" : "Tagesmatrix";

        deck.IsVisible = deckVisible;
        matrixCard.IsVisible = matrixVisible;
        empty.IsVisible = !deckVisible && !matrixVisible;
        emptyText.Text = restoring ? "Gespeicherte Klassen werden geöffnet …" :
            hasClasses ? "Wähle die Klassen für deinen Stapel aus." : "Melde dich bei WebUntis an, um deine Klassenliste zu laden.";
        signIn.IsVisible = !restoring && !hasClasses;
        chooseClasses.IsVisible = !restoring && hasClasses;
        menu.IsEnabled = signIn.IsEnabled = chooseClasses.IsEnabled = !ControlsLocked;
        refresh.IsVisible = !restoring && CanLoadView();
        refresh.IsEnabled = !ControlsLocked && !VisibleTransfer;
        SemanticProperties.SetDescription(refresh, sessionReady ? "Aktualisieren" : "Zum Aktualisieren bei WebUntis anmelden");
        classPicker.IsVisible = rangeButton.IsVisible = matrixVisible;
        classPicker.IsEnabled = rangeButton.IsEnabled = !ControlsLocked;
        tools.IsVisible = matrixVisible;
        cancel.IsVisible = cancel.IsEnabled = VisibleTransfer;
        reconnect.IsVisible = hasClasses && signInRequired && !VisibleTransfer;
        ShowHeadingIfNoMatrix();
        UpdateFooter();
    }

    private MatrixCard? ActiveCard => section == Section.Classes ? deck.Active?.Card : matrixCard;

    private void ShowHeadingIfNoMatrix()
    {
        if (ActiveCard?.Matrix is not { Days.Length: > 0, Students.Length: > 0 })
            SetHeadingCollapsed(false, animate: false);
    }

    private void SetHeadingCollapsed(bool collapsed, bool animate = true)
    {
        if (headingCollapsed == collapsed) return;
        headingCollapsed = collapsed;
        headingHost.AbortAnimation(HeadingAnimation);
        if (collapsed && headingHost.HeightRequest < 0) headingHeight = Math.Max(44, heading.Height);
        var start = headingHost.HeightRequest < 0 ? headingHeight : headingHost.HeightRequest;
        var end = collapsed ? 0 : headingHeight;
        heading.InputTransparent = collapsed;
        if (!animate || headingHost.Handler == null || Math.Abs(start - end) < 1)
        {
            Finish();
            return;
        }

        headingHost.IsVisible = true;
        heading.HeightRequest = headingHeight;
        Apply(start);
        new Animation(Apply, start, end, Easing.CubicOut).Commit(headingHost, HeadingAnimation, length: 180,
            finished: (_, cancelled) => { if (!cancelled) Finish(); });
        UpdateFooter();

        void Apply(double height)
        {
            headingHost.HeightRequest = height;
            heading.TranslationY = height - headingHeight;
        }

        void Finish()
        {
            headingHost.IsVisible = !collapsed;
            headingHost.HeightRequest = collapsed ? 0 : -1;
            heading.HeightRequest = -1;
            heading.TranslationY = 0;
            UpdateFooter();
        }
    }

    private void UpdateFooter()
    {
        var showRefresh = deckRefresh != null && !InteractiveTransferVisible;
        status.IsVisible = !showRefresh;
        refreshStatus.IsVisible = showRefresh;
        var progress = showRefresh ? deckProgress : transferProgress;
        loadingBar.IsVisible = VisibleTransfer && progress.HasValue;
        if (progress is { } fraction) loadingBar.Progress = fraction;
        activity.IsVisible = activity.IsRunning = VisibleTransfer && !loadingBar.IsVisible;
        var landscape = workspace.Height > 0 && workspace.Width > workspace.Height;
        footer.IsVisible = reconnect.IsVisible || VisibleTransfer ||
            (!landscape && !headingCollapsed && !string.IsNullOrWhiteSpace(status.Text));
    }

    private void SetTransferProgress(int completed, int total)
    {
        transferProgress = total > 0 ? Math.Clamp((double)completed / total, 0, 1) : null;
        UpdateFooter();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
