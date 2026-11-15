using Microsoft.Maui.Dispatching;
using WebUntisAuswertung.Core;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class MatrixPage : ContentPage
{
    private enum Section { Classes, Matrix }

    private const string LegendText = "• Kein Fehlzeiteintrag\n| Fehlzeit    E Entschuldigt\n+ Verspätet    |+ / E+ Beides\n? Ungeklärt    B Befreit";
    private const string ChooseClass = "Wähle eine Klasse.";
    private const string InitialUpdate = "Für diese Klasse liegt noch kein Datenstand vor.";
    private const string HeadingAnimation = "SlideHeading";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly BrowserSurface browser = new();
    private readonly Grid loginLayer = new() { IsVisible = false, BackgroundColor = Surface, HeightRequest = 62, VerticalOptions = LayoutOptions.Start };
    private readonly Label loginAddress = MobileStyle.Label(WebUntisReports.Origin, 12, Muted);
    private readonly Grid workspace = new() { BackgroundColor = Surface };
    private readonly Grid heading = new() { VerticalOptions = LayoutOptions.Start };
    private readonly Grid headingHost = new() { IsClippedToBounds = true };
    private readonly Label sectionTitle = MobileStyle.Label("Meine Klassen", 12, Muted);
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
    private readonly MatrixDeck deck = new();
    private readonly Grid tools = new();
    private readonly Grid footer = new();
    private readonly MatrixCard matrixCard;
    private readonly Border empty;
    private readonly AndroidMatrixCache cache = new();
    private ClassSelection classSelection = ClassSelection.Load();
    private Section section;
    private AndroidReports? reports;
    private Student[] classes = [];
    private ReportRange? schoolYear;
    private ReportRange? selectedRange;
    private CancellationTokenSource? transfer;
    private TaskCompletionSource? transferFinished;
    private IDispatcherTimer? refreshTimer;
    private bool backgroundTransfer;
    private bool sessionReady;
    private bool profileReady;
    private bool restoreStarted;
    private bool restoring = true;
    private bool changingSelection;
    private bool updatingClasses;
    private bool modalOpen;
    private bool menuOpen;
    private bool headingCollapsed;
    private double headingHeight = 44;

    private bool ForegroundTransfer => transfer != null && !backgroundTransfer;
    private bool ControlsLocked => ForegroundTransfer || restoring || changingSelection;

    public MatrixPage()
    {
        Title = "WebUntis-FehlzeitenMX";
        SafeAreaEdges = SafeAreaEdges.All;
        BackgroundColor = Surface;
        matrixCard = CreateMatrixCard();
        matrixCard.Clear("", ChooseClass);
        deck.SelectionChanged += ShowHeadingIfNoMatrix;
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
        signIn.Clicked += async (_, _) => await LoadClassesAsync(startWithLogin: true);
        chooseClasses.Clicked += async (_, _) => await ShowClassSettingsAsync();
        refresh.Clicked += async (_, _) => await RefreshVisibleAsync();
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
            sessionReady = false;
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
        if (!ForegroundTransfer) return base.OnBackButtonPressed();
        CancelTransfer();
        return true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ResumeRefresh(refreshNow: false);
        if (restoreStarted) return;
        restoreStarted = true;
        try
        {
            if (await cache.ReadClassesAsync() is not { } stored) return;
            schoolYear = stored.SchoolYear;
            classes = stored.Classes;
            FillClassPicker();
            RebuildDeck(firstCard: true);
            UpdateControls();
            var loaded = await LoadSavedMatricesAsync(stored.SchoolYear);
            status.Text = loaded ? "Gespeicherte Daten · zur Aktualisierung anmelden"
                : "Ein gespeicherter Datenstand konnte nicht geöffnet werden.";
        }
        catch (Exception error) when (AndroidMatrixCache.StorageFailure(error))
        {
            status.Text = "Der gespeicherte Klassenstand konnte nicht geöffnet werden.";
        }
        finally
        {
            restoring = false;
            UpdateControls();
        }
    }

    protected override void OnDisappearing()
    {
        refreshTimer?.Stop();
        base.OnDisappearing();
    }

    internal void ResumeRefresh(bool refreshNow = true)
    {
        if (refreshTimer == null)
        {
            refreshTimer = Dispatcher.CreateTimer();
            refreshTimer.Interval = RefreshInterval;
            refreshTimer.IsRepeating = true;
            refreshTimer.Tick += async (_, _) =>
            {
                if (section == Section.Classes) await RefreshDeckAsync(force: true);
            };
        }
        refreshTimer.Start();
        if (refreshNow && section == Section.Classes) _ = RefreshDeckAsync();
    }

    internal void PauseRefresh()
    {
        refreshTimer?.Stop();
        if (backgroundTransfer) transfer?.Cancel();
    }

    private async Task ShowMenuAsync()
    {
        if (ControlsLocked || menuOpen || modalOpen) return;
        const string mine = "▤  Meine Klassen", matrix = "▦  Tagesmatrix";
        const string selection = "☷  Klassen auswählen", legend = "ⓘ  Legende", account = "⚙  WebUntis-Konto";
        menuOpen = true;
        string? action;
        try { action = await DisplayActionSheetAsync("Fehlzeiten MX", "Schließen", null, mine, matrix, selection, legend, account); }
        finally { menuOpen = false; }

        switch (action)
        {
            case mine: await OpenSectionAsync(Section.Classes); break;
            case matrix: await OpenSectionAsync(Section.Matrix); break;
            case selection:
                if (classes.Length == 0) await LoadClassesAsync(startWithLogin: true, refreshView: false);
                await ShowClassSettingsAsync();
                break;
            case legend: await DisplayAlertAsync("Legende", LegendText, "Schließen"); break;
            case account: await ShowAccountAsync(); break;
        }
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
            if (!await FinishBackgroundTransferAsync()) return;
            try { selection.Save(); }
            catch (Exception error) when (AndroidMatrixCache.StorageFailure(error) || error is InvalidOperationException)
            {
                await DisplayAlertAsync("Klassenauswahl", "Die Auswahl konnte nicht gespeichert werden.", "OK");
                return;
            }
            classSelection = selection;
            RebuildDeck(firstCard: true);
            UpdateControls();
            var pruned = await TryCacheAsync(() => cache.PruneAsync(deck.Entries.Select(item => item.Class.Id)));
            var loaded = schoolYear is not { } year || await LoadSavedMatricesAsync(year);
            if (!pruned || !loaded) status.Text = "Der lokale Datenspeicher konnte nicht vollständig aktualisiert werden.";
        }
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
            var choice = await DisplayActionSheetAsync("Klasse öffnen", "Schließen", null, labels);
            if (ReferenceEquals(entries, deck.Entries))
                deck.Select(Array.IndexOf(labels, choice));
        }
        finally { menuOpen = false; }
    }

    private async Task LoadClassesAsync(bool startWithLogin, bool refreshView = true, bool force = false)
    {
        var opened = await RunAsync(async (service, progress, token) =>
        {
            var (available, year) = await service.ClassesAsync(progress, token, startWithLogin);
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
        });
        if (!opened || !refreshView) return;
        if (section == Section.Classes) await RefreshDeckAsync(force);
        else await OpenDailyMatrixAsync(force);
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
        if (!sessionReady)
        {
            await LoadClassesAsync(startWithLogin: true, force: true);
            return;
        }
        if (section == Section.Classes) await RefreshDeckAsync(force: true);
        else await OpenDailyMatrixAsync(force: true);
    }

    private Task RefreshDeckAsync(bool force = false)
    {
        if (!sessionReady || ControlsLocked || modalOpen || menuOpen || transfer != null || schoolYear is not { } year)
            return Task.CompletedTask;
        var annual = year.ThroughToday();
        var pending = deck.Entries.Where(entry => force || NeedsRefresh(entry.Card.Matrix, annual))
            .OrderBy(entry => entry != deck.Active).ToArray();
        return pending.Length == 0 ? Task.CompletedTask : RunAsync(async (service, progress, token) =>
        {
            var updated = 0;
            var incomplete = 0;
            var stored = true;
            foreach (var (choice, card) in pending)
            {
                token.ThrowIfCancellationRequested();
                if (card.Matrix == null) card.Clear(choice.Name, "Berichte werden geladen …");
                try
                {
                    var classProgress = new InlineProgress<string>(message => progress.Report($"{choice.Name} · {message}"));
                    var data = await service.MatrixAsync(choice, annual, classProgress, token, allowSignIn: false);
                    var matrix = await Task.Run(data.Build, token);
                    token.ThrowIfCancellationRequested();
                    if (matrix.HasCompleteReports)
                    {
                        card.ShowMatrix(matrix);
                        updated++;
                        if (!await TryCacheAsync(() => cache.SaveMatrixAsync(data, token))) stored = false;
                    }
                    else
                    {
                        if (card.Matrix is not { HasCompleteReports: true }) card.ShowMatrix(matrix);
                        incomplete++;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (card.Matrix == null) card.Clear(choice.Name, InitialUpdate);
                    throw;
                }
                catch (WebUntisSignInRequiredException)
                {
                    if (card.Matrix == null) card.Clear(choice.Name, InitialUpdate);
                    throw;
                }
                catch (Exception error) when (!token.IsCancellationRequested &&
                    error is InvalidOperationException or TimeoutException or IOException or InvalidDataException)
                {
                    if (card.Matrix == null) card.Clear(choice.Name, "Daten konnten gerade nicht geladen werden.");
                    if (browser.Browser is { RendererGone: true }) throw;
                    incomplete++;
                }
            }
            status.Text = !stored ? "Neue Daten geöffnet · Speichern fehlgeschlagen" :
                incomplete == 0 ? $"{updated} Klassen aktualisiert" : $"{updated} aktualisiert · {incomplete} Abrufe unvollständig";
        }, background: true);
    }

    private static bool NeedsRefresh(AttendanceMatrix? matrix, ReportRange range) =>
        matrix is not { HasCompleteReports: true, FetchedAt: { } fetched } ||
        matrix.From != range.From || matrix.To != range.To || DateTimeOffset.UtcNow - fetched >= RefreshInterval;

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
        if (!sessionReady || !force && !NeedsRefresh(matrixCard.Matrix, range)) return;

        await RunAsync(async (service, progress, token) =>
        {
            try
            {
                var data = await service.MatrixAsync(choice, range, progress, token, allowSignIn: true);
                var matrix = await Task.Run(data.Build, token);
                token.ThrowIfCancellationRequested();
                if (matrix.HasCompleteReports || matrixCard.Matrix is not { HasCompleteReports: true })
                    matrixCard.ShowMatrix(matrix);
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

    private async Task<bool> RunAsync(Func<AndroidReports, IProgress<string>, CancellationToken, Task> action, bool background = false)
    {
        if (transfer != null && (background || !await FinishBackgroundTransferAsync())) return false;
        if (browser.Browser is not { } view) return false;
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transfer = cancellation;
        transferFinished = completion;
        backgroundTransfer = background;
        var progress = new InlineProgress<string>(message => status.Text = message);
        status.Text = background ? "Klassen werden aktualisiert …" : "WebUntis wird geöffnet …";
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
            return true;
        }
        catch (OperationCanceledException) { status.Text = "Abruf abgebrochen"; }
        catch (WebUntisSignInRequiredException error)
        {
            sessionReady = false;
            status.Text = error.Message;
            if (!background && Window != null) await DisplayAlertAsync("WebUntis", error.Message, "OK");
        }
        catch (Exception error)
        {
            status.Text = background ? "Aktualisierung momentan fehlgeschlagen" : "Abruf fehlgeschlagen";
            if (!background && Window != null)
            {
                ShowLogin(false);
                await DisplayAlertAsync("WebUntis-FehlzeitenMX", error.Message, "OK");
            }
        }
        finally
        {
            transfer = null;
            transferFinished = null;
            backgroundTransfer = false;
            if (Window != null)
            {
                ShowLogin(false);
                UpdateControls();
            }
            completion.TrySetResult();
        }
        return false;
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
        var connect = sessionReady ? "Klassenliste aktualisieren" : "Bei WebUntis anmelden";
        menuOpen = true;
        string? action;
        try { action = await DisplayActionSheetAsync("WebUntis-Konto", "Schließen", "Abmelden", connect); }
        finally { menuOpen = false; }

        if (action == connect) await LoadClassesAsync(startWithLogin: !sessionReady);
        else if (action == "Abmelden")
        {
            await RunAsync(async (service, _, _) =>
            {
                sessionReady = false;
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
    }

    private MatrixCard CreateDeckCard() => CreateMatrixCard(inDeck: true);

    private MatrixCard CreateMatrixCard(bool inDeck = false)
    {
        var card = new MatrixCard(inDeck);
        card.DaySelected += async (data, student, day, cell) =>
        {
            if (ControlsLocked || modalOpen || menuOpen) return;
            await ShowModalAsync(new DayDetailPage(student.Name, day, cell, DayDetails.Create(student, day, cell, data.SchoolPeriods)));
        };
        card.NotesRequested += async data =>
        {
            if (!ControlsLocked && !menuOpen && !modalOpen)
                await DisplayAlertAsync("Abrufhinweise", string.Join(Environment.NewLine + Environment.NewLine, data.Notes), "Schließen");
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
        if (modalOpen || Window == null) return;
        modalOpen = true;
        page.Disappearing += (_, _) => modalOpen = false;
        try { await Navigation.PushModalAsync(page); }
        catch (InvalidOperationException) { modalOpen = false; }
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
        refresh.IsEnabled = !ControlsLocked && transfer == null;
        SemanticProperties.SetDescription(refresh, sessionReady ? "Aktualisieren" : "Zum Aktualisieren bei WebUntis anmelden");
        classPicker.IsVisible = rangeButton.IsVisible = matrixVisible;
        classPicker.IsEnabled = rangeButton.IsEnabled = !ControlsLocked;
        tools.IsVisible = matrixVisible;
        activity.IsVisible = activity.IsRunning = transfer != null;
        cancel.IsVisible = cancel.IsEnabled = transfer != null;
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
        var landscape = workspace.Height > 0 && workspace.Width > workspace.Height;
        footer.IsVisible = ForegroundTransfer || transfer != null && ActiveCard?.Matrix == null ||
            (!landscape && !headingCollapsed && !string.IsNullOrWhiteSpace(status.Text));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
