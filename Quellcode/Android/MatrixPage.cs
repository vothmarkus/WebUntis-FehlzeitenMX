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
    private const string OverlayAnimation = "SlideOverlay";
    private static readonly GridLength BrowserToolbarHeight = new(62);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly BrowserSurface browser = new();
    private readonly Grid browserLayer = new()
    {
        BackgroundColor = Surface,
        InputTransparent = true,
        Opacity = 0,
        RowDefinitions = { new(BrowserToolbarHeight), new(GridLength.Star) }
    };
    private readonly Grid root;
    private readonly Label loginAddress = MobileStyle.Label(WebUntisReports.Origin, 12, Muted);
    private readonly Label browserTitle = MobileStyle.Label("WebUntis-Anmeldung", 14, bold: true);
    private readonly Button browserBack = MobileStyle.Button("‹");
    private readonly Button browserNext = MobileStyle.Button("Weiter", primary: true);
    private readonly Button browserClose = MobileStyle.Button("×");
    private readonly Grid workspace = new() { BackgroundColor = Surface };
    private readonly Grid detailLayer = new() { IsVisible = false, BackgroundColor = Surface };
    private readonly SwipeGrid heading = new()
    {
        Directions = SwipeDirection.Up | SwipeDirection.Down,
        VerticalOptions = LayoutOptions.Start
    };
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
    private readonly Label excuseStatus = MobileStyle.Label("", 13, Muted);
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
    private DayDetailView? dayDetails;
    private DayDetailView? excuseSource;
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
    private bool browserVisible;
    private bool browserTransition;
    private bool detailTransition;
    private bool excuseVisible;
    private bool locked;
    private bool transferTimedOut;
    private double headingHeight = 44;

    private bool ForegroundTransfer => transfer != null && !backgroundTransfer;
    private bool InteractiveTransferVisible => transfer != null && !preloadingAbsences;
    private bool VisibleTransfer => InteractiveTransferVisible || deckRefresh != null;
    private bool ControlsLocked => locked || ForegroundTransfer || restoring || changingSelection;
    private bool ModalVisible => modalOpen || dayDetails != null;

    public MatrixPage()
    {
        Title = "WebUntis-FehlzeitenMX";
        SafeAreaEdges = SafeAreaEdges.All;
        BackgroundColor = Surface;
        matrixCard = CreateMatrixCard();
        matrixCard.Clear("", ChooseClass);
        deck.SelectionChanged += () =>
        {
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

        root = new Grid { IsClippedToBounds = true };
        root.Add(refreshBrowser);
        root.Add(workspace);
        root.Add(detailLayer);
        root.Add(browserLayer);
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
        workspace.Padding = new Thickness(0, 5);
        var sideMargin = new Thickness(WorkspaceSideInset, 0);
        headingHost.Margin = tools.Margin = footer.Margin = sideMargin;
        matrixCard.Margin = empty.Margin = sideMargin;
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
        heading.Swiped += SwipeHeading;
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
        status.MaxLines = refreshStatus.MaxLines = excuseStatus.MaxLines = 1;
        status.LineBreakMode = refreshStatus.LineBreakMode = excuseStatus.LineBreakMode = LineBreakMode.TailTruncation;
        status.VerticalOptions = refreshStatus.VerticalOptions = excuseStatus.VerticalOptions = activity.VerticalOptions = LayoutOptions.Center;
        footer.Add(activity);
        footer.Add(status, 1);
        footer.Add(refreshStatus, 1);
        footer.Add(excuseStatus, 1);
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
        var toolbar = new SwipeGrid
        {
            Directions = SwipeDirection.Down,
            Threshold = 56,
            CanStartSwipe = _ => excuseVisible && !browserTransition,
            Padding = new Thickness(8),
            ColumnSpacing = 8,
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
            BackgroundColor = Surface
        };
        toolbar.Swiped += _ => reports?.CloseExcuse();
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
        browserLayer.Add(toolbar);
        browserLayer.Add(browser, 0, 1);
        AutomationProperties.SetExcludedWithChildren(browserLayer, true);
    }

    protected override bool OnBackButtonPressed()
    {
        if (excuseVisible)
        {
            reports?.CloseExcuse();
            return true;
        }
        if (browserVisible)
        {
            if (browser.Browser?.GoBack() != true) CancelTransfer();
            return true;
        }
        if (dayDetails != null)
        {
            _ = CloseDayDetailsAsync();
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

    private async Task CheckSessionAsync()
    {
        if (!CanCheck()) return;
        if (classes.Length == 0)
        {
            var token = lifetime.Token;
            try
            {
                if (await cache.ReadLoginAsync(token) == null) return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (AndroidMatrixCache.StorageFailure(error)) { return; }
        }
        if (CanCheck()) await LoadClassesAsync(background: true);

        bool CanCheck() => !ControlsLocked && !menuOpen && !ModalVisible && transfer == null && !sessionReady &&
            !signInDismissed && Window != null;
    }

    private async Task ShowMenuAsync()
    {
        if (ControlsLocked || menuOpen || ModalVisible) return;
        string[] actions = ["▤  Meine Klassen", "▦  Tagesmatrix", "↓  Schüler-CSV speichern …", "⚙  Einstellungen"];
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
        () => LoadClassesAsync(), SignOutAsync, ChooseClassesFromSettingsAsync, ConfigureAutomaticLoginAsync));

    private async Task ConfigureAutomaticLoginAsync()
    {
        if (ControlsLocked || !await FinishBackgroundTransferAsync() || locked || Window == null) return;
        var token = lifetime.Token;
        try
        {
            var saved = await cache.ReadLoginAsync(token);
            await ShowModalAsync(new AutomaticLoginPage(saved, StoreAsync, ReconnectAsync));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (AndroidMatrixCache.StorageFailure(error))
        {
            await NoticeAsync("Automatische Anmeldung", "Die gespeicherte Einstellung konnte nicht geöffnet werden.");
        }

        async Task<bool> StoreAsync(WebUntisLogin? login)
        {
            try { return !locked && await TryCacheAsync(() => cache.SaveLoginAsync(login, token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        }

        Task ReconnectAsync()
        {
            signInDismissed = false;
            return CheckSessionAsync();
        }
    }

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
        if (ControlsLocked || ModalVisible || classes.Length == 0) return;
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
        if (ControlsLocked || menuOpen || ModalVisible) return;
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
        if (pendingExcuses.Count > 0) _ = RefreshExcusesAsync();
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
        if (pendingExcuses.Count > 0)
        {
            await RefreshExcusesAsync();
            return;
        }
        excuseSyncIssue = null;
        UpdateFooter();
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
        if (ControlsLocked || ModalVisible || schoolYear is not { } year) return;
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
            absencePreloadTimer.Interval = TimeSpan.FromMilliseconds(700);
            absencePreloadTimer.IsRepeating = false;
            absencePreloadTimer.Tick += async (_, _) => await PreloadActiveAbsencesAsync();
        }
        absencePreloadTimer.Stop();
        absencePreloadTimer.Start();
    }

    private async Task PreloadActiveAbsencesAsync()
    {
        if (!sessionReady || ControlsLocked || menuOpen || transfer != null || modalOpen ||
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
            transferTimedOut = false;
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
                reports = new AndroidReports(view, ShowLogin, ShowExcuseAsync, ConfirmSessionAsync, cache);
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
            if (!quiet)
            {
                transferTimedOut = error is TimeoutException;
                status.Text = browser.Browser is { RendererGone: true } ? MobileWebView.RendererMessage :
                    transferTimedOut ? error.Message : background ? "Aktualisierung momentan fehlgeschlagen" : "Abruf fehlgeschlagen";
            }
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
        if (!InteractiveTransferVisible && !browserVisible)
        {
            deckRefresh?.Cancel();
            cancel.IsEnabled = false;
            return;
        }
        if (browserVisible && !excuseVisible) signInDismissed = true;
        cancel.IsEnabled = false;
        transfer?.Cancel();
        if (browserVisible && browser.Browser is { RendererGone: false } view) view.Go("about:blank");
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

    private async Task ShowExcuseAsync(bool visible)
    {
        if (visible)
        {
            if (locked || Window == null) return;
            var fromDetails = dayDetails != null && ReferenceEquals(dayDetails, excuseSource);
            excuseVisible = true;
            browserTransition = true;
            browserTitle.Text = "WebUntis-Entschuldigung";
            browserBack.IsVisible = browserNext.IsVisible = false;
            SemanticProperties.SetDescription(browserClose, "Entschuldigungsformular schließen");
            Apply(0);
            ShowBrowser(true);
            try { await SlideOverlayAsync(browserLayer, entering: true, Apply); }
            finally
            {
                browserTransition = false;
                if (fromDetails) HideDayDetails();
                else UpdateOverlayInput();
            }

            void Apply(double progress)
            {
                if (fromDetails)
                {
                    var width = Math.Max(0, root.Width);
                    browserLayer.TranslationX = width * (1 - progress);
                    detailLayer.TranslationX = -width * progress;
                }
                else browserLayer.TranslationY = Math.Max(0, root.Height) * (1 - progress);
            }
        }
        else if (excuseVisible)
        {
            browserTransition = true;
            UpdateOverlayInput();
            try
            {
                if (browser.Browser is { RendererGone: false })
                    await SlideOverlayAsync(browserLayer, entering: false,
                        progress => browserLayer.TranslationY = Math.Max(0, root.Height) * progress);
            }
            finally
            {
                ShowBrowser(false);
                browserLayer.TranslationX = browserLayer.TranslationY = 0;
                browserTransition = false;
                excuseVisible = false;
                browserTitle.Text = "WebUntis-Anmeldung";
                browserBack.IsVisible = browserNext.IsVisible = true;
                SemanticProperties.SetDescription(browserClose, "Anmeldung abbrechen");
            }
        }
    }

    private async Task SlideOverlayAsync(VisualElement layer, bool entering, Action<double> apply)
    {
        if (!locked && Window != null && layer.Handler != null && root.Height > 0)
        {
            var duration = entering ? 230u : 190u;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            new Animation(apply, easing: entering ? Easing.CubicOut : Easing.CubicIn)
                .Commit(layer, OverlayAnimation, length: duration, finished: (_, _) => finished.TrySetResult());
            try { await finished.Task.WaitAsync(TimeSpan.FromMilliseconds(duration + 250), lifetime.Token); }
            catch (TimeoutException) { layer.AbortAnimation(OverlayAnimation); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { layer.AbortAnimation(OverlayAnimation); }
        }
        apply(1);
    }

    private void ShowBrowser(bool visible)
    {
        if (browserVisible == visible || visible && Window == null) return;
        browserVisible = visible;
        if (visible) UpdateLoginAddress(browser.Browser?.LoginAddress);
        // Keep the WebView attached and measured so fitting doesn't depend on the panel becoming visible.
        browserLayer.Opacity = visible ? 1 : 0;
        UpdateOverlayInput();
        browser.Browser?.SetLoginVisible(visible);
    }

    private void UpdateOverlayInput()
    {
        var detailsVisible = dayDetails != null && !browserVisible;
        workspace.InputTransparent = dayDetails != null || browserVisible;
        detailLayer.InputTransparent = !detailsVisible || detailTransition;
        browserLayer.InputTransparent = !browserVisible || browserTransition;
        AutomationProperties.SetExcludedWithChildren(workspace, workspace.InputTransparent);
        AutomationProperties.SetExcludedWithChildren(detailLayer, !detailsVisible);
        AutomationProperties.SetExcludedWithChildren(browserLayer, !browserVisible);
    }

    private void SizeBrowser()
    {
        browser.HeightRequest = root.Height > BrowserToolbarHeight.Value
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
            pendingExcuses.Clear();
            excuseSyncIssue = null;
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

    private async Task OpenCellExcuseAsync(MatrixCard card, AttendanceMatrix matrix, Student student, DateOnly day,
        DayDetailView? source = null)
    {
        if (ControlsLocked || menuOpen || modalOpen || editingClassId != null ||
            !classSelection.Teachers.Contains(matrix.ClassId) || !IsCurrent()) return;
        editingClassId = matrix.ClassId;
        excuseSource = source;
        source?.SetOpeningExcuse(true);
        editRevisions[matrix.ClassId] = editRevisions.GetValueOrDefault(matrix.ClassId) + 1;
        try { await RunAsync(EditAsync); }
        finally
        {
            if (ReferenceEquals(source, dayDetails)) source?.SetOpeningExcuse(false);
            excuseSource = null;
            editingClassId = null;
            if (pendingExcuses.Count > 0) _ = RefreshExcusesAsync();
        }

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
            var available = AbsenceActions.Available(rows);
            var choices = available.Where(row => !pendingExcuses.ContainsKey((matrix.ClassId, row.Id))).ToArray();
            if (choices.Length == 0)
            {
                status.Text = available.Length > 0 ? "Diese Entschuldigung wird gerade mit WebUntis abgeglichen." :
                    rows.Length == 0 ? "Keine passende WebUntis-Abwesenheit gefunden." :
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
            var row = choices[index];
            var save = await service.ShowExcuseAsync(matrix, student, day, row, progress, token);
            if (save.Saved) QueueExcuse(matrix, student, day, row, save.Status);
        }

        bool IsCurrent() => !detailTransition && ReferenceEquals(source, dayDetails) &&
            ReferenceEquals(card, ActiveCard) && card.Matrix is { } current && current.ClassId == matrix.ClassId &&
            current.Cells.ContainsKey((student.Id, day));
    }

    private static bool HasExcusableEntries(DayCell cell) =>
        cell.Entries.Any(item => item.Kind is EntryKind.Absent or EntryKind.Late);

    private async Task ShowDayDetailsAsync(MatrixCard card, string studentId, DateOnly day)
    {
        if (ControlsLocked || ModalVisible || browserVisible || menuOpen || Window == null ||
            !ReferenceEquals(card, ActiveCard) || card.Matrix is not { } matrix ||
            matrix.Students.FirstOrDefault(student => student.Id == studentId) is not { } person ||
            !matrix.Cells.TryGetValue((studentId, day), out var cell)) return;

        var detail = DayDetails.Create(person, day, cell, matrix.SchoolPeriods);
        var canEdit = classSelection.Teachers.Contains(matrix.ClassId) && HasExcusableEntries(cell);
        var view = new DayDetailView(person.Name, day, cell, detail, canEdit);
        view.CloseRequested += async () =>
        {
            if (ReferenceEquals(view, dayDetails)) await CloseDayDetailsAsync();
        };
        view.ExcuseRequested += async () => await OpenFromDetailsAsync();
        dayDetails = view;
        detailTransition = true;
        detailLayer.Add(view);
        detailLayer.TranslationY = Math.Max(0, root.Height);
        detailLayer.IsVisible = true;
        UpdateOverlayInput();
        try
        {
            await SlideOverlayAsync(detailLayer, entering: true,
                progress => detailLayer.TranslationY = Math.Max(0, root.Height) * (1 - progress));
        }
        finally
        {
            detailTransition = false;
            UpdateOverlayInput();
        }
        if (canEdit) ScheduleAbsencePreload();

        async Task OpenFromDetailsAsync()
        {
            if (!ReferenceEquals(card, ActiveCard) || card.Matrix is not { } current || current.ClassId != matrix.ClassId ||
                current.Students.FirstOrDefault(student => student.Id == studentId) is not { } selected) return;
            await OpenCellExcuseAsync(card, current, selected, day, view);
        }
    }

    private async Task CloseDayDetailsAsync()
    {
        if (locked || dayDetails == null || detailTransition || browserVisible) return;
        if (ReferenceEquals(dayDetails, excuseSource)) transfer?.Cancel();
        detailTransition = true;
        UpdateOverlayInput();
        try
        {
            await SlideOverlayAsync(detailLayer, entering: false,
                progress => detailLayer.TranslationY = Math.Max(0, root.Height) * progress);
        }
        finally { HideDayDetails(); }
        ScheduleAbsencePreload();
    }

    private void HideDayDetails()
    {
        detailLayer.IsVisible = false;
        dayDetails?.SetOpeningExcuse(false);
        detailLayer.Clear();
        detailLayer.TranslationX = detailLayer.TranslationY = 0;
        dayDetails = null;
        detailTransition = false;
        UpdateOverlayInput();
    }

    private MatrixCard CreateMatrixCard(bool inDeck = false)
    {
        var card = new MatrixCard(inDeck, IsExcusePending);
        card.DaySelected += async (_, student, day, _) => await ShowDayDetailsAsync(card, student.Id, day);
        card.AbsenceRequested += async (matrix, student, day, _) => await OpenCellExcuseAsync(card, matrix, student, day);
        card.NotesRequested += async data =>
        {
            if (!ControlsLocked && !menuOpen && !ModalVisible)
                await NoticeAsync("Abrufhinweise", string.Join(Environment.NewLine + Environment.NewLine, data.Notes));
        };
        card.HeadingSwiped += direction =>
        {
            if (ReferenceEquals(card, ActiveCard)) SwipeHeading(direction);
        };
        card.FitsVerticallyAfterZoom += () =>
        {
            if (ReferenceEquals(card, ActiveCard)) SetHeadingCollapsed(false);
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
        if (locked || ModalVisible || Window == null) return;
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

    private void SwipeHeading(SwipeDirection direction)
    {
        if (locked || ModalVisible || browserVisible) return;
        if (direction == SwipeDirection.Down) SetHeadingCollapsed(false);
        else if (ActiveCard is { HasHeading: true }) SetHeadingCollapsed(true);
    }

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
        var showExcuseStatus = !VisibleTransfer && (pendingExcuses.Count > 0 || excuseSyncIssue != null);
        excuseStatus.Text = excuseSyncIssue ?? "Entschuldigungen warten auf Abgleich …";
        excuseStatus.IsVisible = showExcuseStatus;
        status.IsVisible = !showRefresh && !showExcuseStatus;
        refreshStatus.IsVisible = showRefresh;
        var progress = showRefresh ? deckProgress : transferProgress;
        loadingBar.IsVisible = VisibleTransfer && progress.HasValue;
        if (progress is { } fraction) loadingBar.Progress = fraction;
        activity.IsVisible = activity.IsRunning = VisibleTransfer && !loadingBar.IsVisible;
        var landscape = workspace.Height > 0 && workspace.Width > workspace.Height;
        footer.IsVisible = reconnect.IsVisible || VisibleTransfer || showExcuseStatus || transferTimedOut ||
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
