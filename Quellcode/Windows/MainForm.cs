using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung;

internal sealed class MainForm : Form
{
    private static readonly StringComparer ClassComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly ReportRange ShownSchoolYear = new(new DateOnly(2026, 8, 31), new DateOnly(2027, 7, 18));
    private static readonly Version InstalledVersion = UpdateRelease.Normalize(typeof(MainForm).Assembly.GetName().Version!);
    private static readonly string DisplayVersion = UpdateRelease.VersionLabel(InstalledVersion);
    private static int MatrixLoadSteps => UntisBrowser.AutomaticReports.Count + 1;

    private readonly TabControl tabs = new BrandTabs { Dock = DockStyle.Fill };
    private readonly TabPage homePage = new("Startseite") { BackColor = AppTheme.Surface };
    private readonly TabPage myClassesPage = new("Meine Klassen") { BackColor = AppTheme.Surface };
    private readonly TabPage matrixPage = new("Tagesmatrix") { BackColor = AppTheme.Surface };
    private readonly TabPage settingsPage = new("Einstellungen") { BackColor = AppTheme.Surface, Padding = new Padding(16) };
    private readonly TableLayoutPanel homeRows = new()
    {
        Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
    };
    private readonly Dictionary<string, MatrixView> homeViews = new(ClassComparer);
    private readonly HashSet<string> loadedHome = new(ClassComparer);
    private readonly Dictionary<string, AttendanceMatrix> schoolYearMatrices = new(ClassComparer);
    private readonly DetailsView homeDetails = new();
    private ContextMenuStrip? homeAbsenceMenu;
    private CancellationTokenSource? absencePreload;
    private readonly FlowLayoutPanel myClassButtons = new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(0, 126),
        AutoScroll = true, Padding = new Padding(8, 5, 8, 5), BackColor = MatrixView.Surface
    };
    private readonly MatrixView myClassView = new(stacked: false, throughToday: true);
    private readonly DetailsView myClassDetails = new();
    private readonly MatrixView matrixView = new(stacked: false);
    private readonly DetailsView matrixDetails = new();
    private readonly Control[] legends = [CreateLegend(), CreateLegend(), CreateLegend()];
    private readonly ComboBox classes = new() { Width = 140, AccessibleName = "Klasse oder Gruppe" };
    private readonly DateTimePicker from = DatePicker("Zeitraum von");
    private readonly DateTimePicker to = DatePicker("Zeitraum bis");
    private readonly TransferCancelButton cancel = new();
    private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel loadingCount = new() { Visible = false, ForeColor = MatrixView.Ink };
    private readonly ToolStripProgressBar loading = new() { Visible = false, AutoSize = false,
        Size = new Size(140, 14), MarqueeAnimationSpeed = 28, AccessibleName = "Ladefortschritt" };
    private readonly CheckedListBox homeOptions = ClassOptions("Klassen für die Startseite auswählen");
    private readonly CheckedListBox myClassOptions = ClassOptions("Klassen für Meine Klassen auswählen");
    private readonly TextBox classFilter = new() { Dock = DockStyle.Top, PlaceholderText = "Beide Listen filtern", AccessibleName = "Beide Klassenlisten filtern" };
    private readonly CheckBox remember = new() { Text = "WebUntis-Anmeldung merken", AutoSize = true };
    private readonly Label rememberedLogin = new() { AutoSize = true, MaximumSize = new Size(320, 0),
        ForeColor = AppTheme.Muted, Margin = new Padding(5, 0, 0, 10) };
    private readonly CheckBox hello = new() { Text = "Beim Start mit Windows Hello entsperren", AutoSize = true };
    private readonly CheckBox storeData = new() { Text = "Daten auf dem Gerät speichern", AutoSize = true };
    private readonly Label storageStatus = new() { AutoSize = true, ForeColor = AppTheme.Muted, MaximumSize = new Size(410, 0) };
    private readonly Button unlockStore = ActionButton("Datenspeicher entsperren …");
    private readonly System.Windows.Forms.Timer autoUpdate = new() { Interval = 5 * 60 * 1000 };
    private readonly ComboBox schoolYearPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 245, AccessibleName = "Schuljahr auswählen" };
    private readonly Label schoolYearPeriod = new() { AutoSize = true, ForeColor = AppTheme.Muted };
    private readonly CheckBox showLegend = new() { Text = "Legende anzeigen", AutoSize = true };
    private readonly Button checkUpdate = ActionButton("Auf Updates prüfen");
    private readonly Button installUpdate = ActionButton("Update installieren", primary: true);
    private readonly Label updateStatus = new() { Text = "Noch nicht geprüft", AutoSize = true, ForeColor = AppTheme.Muted };
    private readonly Button cancelUpdate = ActionButton("Abbrechen");
    private readonly CancellationTokenSource updateCheckLifetime = new();
    private readonly List<Control> busyControls = [];
    private readonly Dictionary<TabPage, string> pageStatuses = [];
    private readonly string appFolder;
    private readonly UntisBrowser browser;
    private readonly MatrixCache matrixCache;
    private AppSettings settings;
    private ReportRange schoolYear;
    private CancellationTokenSource? running;
    private bool backgroundRefresh;
    private Task? backgroundTask;
    private int waitingForBackground;
    private bool closing;
    private bool resumeAutomaticRefresh = true;
    private bool refreshOnIdle;
    private bool updatingSettings;
    private bool sessionReady;
    private bool closeAfterCancellation;
    private bool checkingUpdate;
    private bool checkedUpdateOnce;
    private UpdateRelease? availableUpdate;
    private string? openedMyClass;
    private MatrixRequest? matrixRequest;

    internal MainForm(string appFolder)
    {
        this.appFolder = appFolder;
        settings = AppSettings.Load(appFolder);
        browser = new UntisBrowser(this, appFolder, settings.RememberLogin);
        matrixCache = new MatrixCache(appFolder);
        schoolYear = SelectedSchoolYearRange();
        SetDates(schoolYear);

        Text = $"{Application.ProductName} {DisplayVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(860, 560);
        ClientSize = new Size(1220, 770);
        Font = new Font("Segoe UI", 10);
        using var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("BktIcon");
        if (iconStream != null)
        {
            using var icon = new Icon(iconStream);
            Icon = (Icon)icon.Clone();
        }
        ForeColor = AppTheme.Ink;
        BackColor = AppTheme.Surface;

        BuildHome();
        BuildMyClasses();
        BuildMatrix();
        BuildSettings();
        SetLegendVisibility();
        tabs.TabPages.AddRange([homePage, myClassesPage, matrixPage, settingsPage]);
        tabs.SelectedTab = homePage;
        tabs.Visible = !settings.ProtectWithHello && !settings.StoreDataOnDevice;
        var statusStrip = new StatusStrip { SizingGrip = false, BackColor = AppTheme.Surface, ForeColor = AppTheme.Muted,
            RenderMode = ToolStripRenderMode.System, Dock = DockStyle.Bottom };
        statusStrip.ShowItemToolTips = true;
        statusStrip.Items.AddRange([status, loadingCount, loading, cancel]);
        var header = new SchoolWordmark(Icon?.ToBitmap()) { Visible = false };
        var headerTip = new ToolTip();
        headerTip.SetToolTip(header, "Website des Berufskollegs öffnen · bkt-dn.de");
        header.Click += (_, _) => OpenWebsite("https://bkt-dn.de/");
        header.Disposed += (_, _) => headerTip.Dispose();
        var workspace = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Surface };
        workspace.Controls.Add(header);
        workspace.Controls.Add(tabs);
        Controls.Add(workspace);
        Controls.Add(statusStrip);
        void PositionHeader()
        {
            if (!tabs.Visible || !tabs.IsHandleCreated || tabs.RowCount != 1)
            {
                header.Hide();
                return;
            }
            var firstTab = tabs.GetTabRect(0);
            var lastTab = tabs.GetTabRect(tabs.TabCount - 1);
            var inset = LogicalToDeviceUnits(10);
            var right = workspace.ClientSize.Width - inset;
            var width = header.WidthFor(right - lastTab.Right - inset);
            if (firstTab.Height <= 2 || width == 0)
            {
                header.Hide();
                return;
            }
            header.Bounds = Rectangle.FromLTRB(right - width, firstTab.Top + 1, right, firstTab.Bottom - 1);
            header.Show();
        }
        workspace.Resize += (_, _) => PositionHeader();
        tabs.Layout += (_, _) => PositionHeader();
        tabs.FontChanged += (_, _) => PositionHeader();
        tabs.VisibleChanged += (_, _) => PositionHeader();
        tabs.HandleCreated += (_, _) => PositionHeader();
        DpiChanged += (_, _) => BeginInvoke(PositionHeader);

        FillClassList();
        UpdateRememberedLoginStatus();
        SynchronizeHomeViews();
        SynchronizeMyClassButtons();
        ResetMyClass();
        ResetMatrix();
        classes.TextChanged += (_, _) => SetMatrixFilterStatus();
        from.ValueChanged += (_, _) => SetMatrixFilterStatus();
        to.ValueChanged += (_, _) => SetMatrixFilterStatus();
        cancel.Click += (_, _) =>
        {
            resumeAutomaticRefresh = false;
            refreshOnIdle = false;
            autoUpdate.Stop();
            running?.Cancel();
            absencePreload?.Cancel();
            cancel.Enabled = false;
        };
        KeyPreview = true;
        KeyDown += (_, args) =>
        {
            if (args.KeyData != Keys.Escape || !cancel.Available || !cancel.Enabled) return;
            args.SuppressKeyPress = true;
            cancel.PerformClick();
        };
        autoUpdate.Tick += (_, _) => RefreshVisibleInBackground();
        Shown += async (_, _) =>
        {
            PositionHeader();
            await StartAsync();
        };
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            PositionHeader();
            if (matrixCache.IsOpen && running != null) refreshOnIdle = true;
            if (running == null) ShowPageStatus();
            if (tabs.SelectedTab == settingsPage && !checkedUpdateOnce) await CheckForUpdateAsync();
            if (tabs.SelectedTab == homePage && settings.StoreDataOnDevice && matrixCache.IsOpen)
                await OpenCachedHomeAsync();
            else if (tabs.SelectedTab == homePage && sessionReady && homeViews.Keys.Any(name => !loadedHome.Contains(name)))
                await RunAsync(LoadHomeAsync);
            else if (matrixCache.IsOpen && (tabs.SelectedTab == myClassesPage || tabs.SelectedTab == matrixPage))
                RefreshVisibleInBackground();
        };
        FormClosing += (_, args) =>
        {
            closing = true;
            autoUpdate.Stop();
            if (running == null) return;
            args.Cancel = true;
            closeAfterCancellation = true;
            running.Cancel();
        };
        FormClosed += (_, _) =>
        {
            homeAbsenceMenu?.Dispose();
            absencePreload?.Cancel();
            autoUpdate.Dispose();
            matrixCache.Dispose();
            updateCheckLifetime.Cancel();
            updateCheckLifetime.Dispose();
            browser.Dispose();
        };
    }

    private void BuildHome()
    {
        homeRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), BackColor = AppTheme.Surface };
        scroll.Controls.Add(homeRows);
        homePage.Controls.Add(WithDetails(scroll, homeDetails));
        homePage.Controls.Add(legends[0]);
    }

    private void BuildMyClasses()
    {
        var upper = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 6) };
        upper.Controls.Add(myClassView);
        myClassesPage.Controls.Add(WithDetails(upper, myClassDetails));
        myClassesPage.Controls.Add(legends[1]);
        myClassesPage.Controls.Add(myClassButtons);
        myClassView.DaySelected += (_, details) => myClassDetails.ShowDetails(details);
        busyControls.Add(myClassButtons);
    }

    private void BuildMatrix()
    {
        var refresh = ActionButton("Aktualisieren", primary: true);
        refresh.Click += async (_, _) => await OpenRequestedMatrixAsync();
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = MatrixView.Surface, Padding = new Padding(9, 5, 9, 5) };
        toolbar.Controls.AddRange([Field("Klasse", classes), Field("Von", from), Field("Bis", to), refresh]);
        var upper = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 6) };
        upper.Controls.Add(matrixView);
        matrixPage.Controls.Add(WithDetails(upper, matrixDetails));
        matrixPage.Controls.Add(legends[2]);
        matrixPage.Controls.Add(toolbar);
        matrixView.DaySelected += (_, details) => matrixDetails.ShowDetails(details);
        busyControls.AddRange([classes, from, to, refresh]);
    }

    private void BuildSettings()
    {
        var signIn = ActionButton("Anmelden", primary: true);
        var signOut = ActionButton("Abmelden");
        var reload = ActionButton("Klassenliste aktualisieren");
        var csvDownload = ActionButton("Notenrechner-CSV speichern …");
        csvDownload.Click += async (_, _) => await RunAsync(ExportRosterCsvAsync);
        signIn.Click += async (_, _) => await RunAsync(LoadSessionAsync);
        signOut.Click += async (_, _) => await RunAsync(SignOutAsync);
        reload.Click += async (_, _) => await RunAsync(async (token, progress) =>
        {
            await LoadClassesAsync(token, progress);
            if (tabs.SelectedTab == homePage) await LoadHomeAsync(token, progress);
        });
        remember.Checked = settings.RememberLogin;
        hello.Checked = settings.ProtectWithHello;
        storeData.Checked = settings.StoreDataOnDevice;
        ShowStorageStatus();
        showLegend.Checked = settings.ShowLegend;
        remember.CheckedChanged += async (_, _) => await ChangeRememberLoginAsync();
        hello.CheckedChanged += async (_, _) => await ChangeHelloAsync();
        storeData.CheckedChanged += async (_, _) => await ChangeDeviceStorageAsync();
        unlockStore.Click += async (_, _) => await UnlockStoreAsync();
        showLegend.CheckedChanged += async (_, _) => await ChangeLegendAsync();
        classFilter.TextChanged += (_, _) => FillClassOptions();
        homeOptions.ItemCheck += (_, args) => SaveClassChoice(homeOptions, args, forHome: true);
        myClassOptions.ItemCheck += (_, args) => SaveClassChoice(myClassOptions, args, forHome: false);
        FillSchoolYears();
        schoolYearPicker.SelectedIndexChanged += async (_, _) => await ChangeSchoolYearAsync();

        var accountButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 10) };
        accountButtons.Controls.AddRange([signIn, signOut]);

        var installedVersion = new Label { Text = $"Installierte Version {DisplayVersion}", AutoSize = true, ForeColor = AppTheme.Muted };
        var updateButtons = new FlowLayoutPanel { AutoSize = true };
        updateButtons.Controls.AddRange([checkUpdate, installUpdate, cancelUpdate]);
        checkUpdate.Click += async (_, _) => await CheckForUpdateAsync();
        installUpdate.Click += async (_, _) => await RunAsync(InstallUpdateAsync);
        cancelUpdate.Visible = false;
        cancelUpdate.Click += (_, _) => cancel.PerformClick();
        var releases = new LinkLabel { Text = "Releases auf GitHub", AutoSize = true, LinkColor = AppTheme.Accent };
        releases.LinkClicked += (_, _) => OpenWebsite(GitHubUpdates.ReleasesUrl);
        RefreshUpdateButtons();
        var imprint = new Label
        {
            Text = """
                Markus Voth · Entwicklung und Kontakt
                Berufskolleg für Technik Düren
                Nideggener Straße 43 · 52349 Düren
                markus.voth@bkt-dn.de
                """,
            AutoSize = true
        };
        var readme = ActionButton("Kurzanleitung öffnen …");
        readme.Click += (_, _) => ShowReadme();

        var left = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 0, 18, 0) };
        left.Controls.AddRange([
            SettingsBlock("README", readme),
            SettingsBlock("Impressum", imprint),
            SettingsBlock("Update", installedVersion, updateStatus, updateButtons, releases),
            SettingsBlock("CSV-Download", csvDownload),
            SettingsBlock("Schuljahr auswählen", schoolYearPicker, schoolYearPeriod),
            SettingsBlock("Ansicht", showLegend),
            SettingsBlock("Datenschutzmodus", storeData, storageStatus, unlockStore),
            SettingsBlock("WebUntis-Anmeldung", accountButtons, remember, rememberedLogin, hello)
        ]);

        var classTools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        classTools.Controls.Add(reload);
        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 5, 0, 0) };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        lists.Controls.Add(ClassListPanel("Startseite (Klassenlehrer)", homeOptions), 0, 0);
        lists.Controls.Add(ClassListPanel("Meine Klassen", myClassOptions), 0, 1);
        var choices = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        choices.Controls.Add(lists);
        choices.Controls.Add(classFilter);
        choices.Controls.Add(classTools);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 316));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(left, 0, 0);
        layout.Controls.Add(choices, 1, 0);
        settingsPage.Controls.Add(layout);
        busyControls.AddRange([signIn, signOut, reload, remember, hello, storeData, unlockStore, showLegend, classFilter, homeOptions, myClassOptions,
            schoolYearPicker, csvDownload]);
    }

    private void RefreshUpdateButtons()
    {
        checkUpdate.Enabled = !checkingUpdate && running == null;
        installUpdate.Enabled = !checkingUpdate && running == null && availableUpdate != null;
        installUpdate.Text = availableUpdate is { } release ? $"Update auf {release.Label} installieren" : "Update installieren";
    }

    private async Task CheckForUpdateAsync()
    {
        if (checkingUpdate || IsDisposed) return;
        checkedUpdateOnce = true;
        checkingUpdate = true;
        availableUpdate = null;
        RefreshUpdateButtons();
        updateStatus.Text = "GitHub wird nach Updates gefragt …";
        var token = updateCheckLifetime.Token;
        try
        {
            var latest = await GitHubUpdates.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            if (latest is not { } release)
            {
                updateStatus.Text = "Auf GitHub ist noch kein Release verfügbar.";
            }
            else if (release.Version > InstalledVersion)
            {
                availableUpdate = release.Setup;
                var label = UpdateRelease.VersionLabel(release.Version);
                updateStatus.Text = availableUpdate == null
                    ? $"Version {label} · kein geprüftes x64-Setup verfügbar"
                    : $"Version {label} ist verfügbar.";
            }
            else
            {
                updateStatus.Text = release.Version == InstalledVersion
                    ? $"Aktuell · Version {DisplayVersion}"
                    : $"Installierte Version ist neuer · veröffentlicht ist {UpdateRelease.VersionLabel(release.Version)}";
            }
            SetPageStatus(settingsPage, updateStatus.Text);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException)
        {
            if (!token.IsCancellationRequested)
            {
                updateStatus.Text = error.Message;
                SetPageStatus(settingsPage, "Versionsprüfung nicht abgeschlossen.");
            }
        }
        finally
        {
            checkingUpdate = false;
            if (!IsDisposed) RefreshUpdateButtons();
        }
    }

    private async Task InstallUpdateAsync(CancellationToken token, IProgress<string> _)
    {
        if (availableUpdate is not { } release) return;
        updateStatus.Text = $"Setup für {release.Label} wird heruntergeladen …";
        cancelUpdate.Visible = true;
        ShowProgress(0, 100);
        var progress = new Progress<int>(percent =>
        {
            if (running != null && running.Token == token && !IsDisposed) ShowProgress(percent, 100);
        });
        try
        {
            var path = await GitHubUpdates.DownloadAsync(release, Path.Combine(appFolder, "Updates"), progress, token);
            token.ThrowIfCancellationRequested();
            updateStatus.Text = $"Setup für {release.Label} wird geöffnet …";
            DownloadedSetup.RegisterInternetDownload(path, release.Url);
            using var file = File.OpenRead(path);
            await release.VerifySetupHashAsync(file, token);
            release.VerifySetupVersion(path);
            token.ThrowIfCancellationRequested();
            using var setup = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                ErrorDialog = true,
                ErrorDialogParentHandle = Handle,
                WorkingDirectory = Environment.SystemDirectory
            });
            updateStatus.Text = $"Setup für {release.Label} geöffnet.";
            SetPageStatus(settingsPage, updateStatus.Text);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            updateStatus.Text = $"Start des Setups für {release.Label} abgebrochen.";
            SetPageStatus(settingsPage, updateStatus.Text);
        }
        catch
        {
            updateStatus.Text = $"Installation von Version {release.Label} nicht gestartet.";
            throw;
        }
        finally { cancelUpdate.Visible = false; }
    }

    private void OpenWebsite(string url)
    {
        try { using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "Die Webseite konnte nicht geöffnet werden.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ShowReadme()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("README.txt");
        if (stream == null) return;
        using var reader = new StreamReader(stream);
        MessageBox.Show(this, reader.ReadToEnd().TrimEnd(), $"README · {Application.ProductName}", MessageBoxButtons.OK);
    }

    private async Task StartAsync()
    {
        if (settings.StoreDataOnDevice)
        {
            status.Text = "Windows Hello · Datenspeicher entsperren";
            try { await matrixCache.UnlockAsync(Handle, updateCheckLifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (StorageFailure(error) || error is InvalidOperationException)
            {
                if (!IsDisposed) storageStatus.Text = "Datenspeicher gesperrt. Du kannst ihn in den Einstellungen entsperren oder neu einrichten.";
            }
            if (IsDisposed || closing) return;
            ShowStorageStatus();
        }
        if (settings.ProtectWithHello && !matrixCache.IsOpen)
        {
            status.Text = "Windows Hello · App entsperren";
            try
            {
                if (!await WindowsHello.VerifyAsync(Handle, $"{Application.ProductName} entsperren"))
                {
                    Close();
                    return;
                }
            }
            catch (InvalidOperationException error)
            {
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
                return;
            }
            if (IsDisposed) return;
        }
        tabs.Visible = true;
        if (matrixCache.IsOpen)
        {
            await OpenCachedHomeAsync();
            return;
        }
        if (settings.StoreDataOnDevice)
        {
            ShowPageStatus();
            return;
        }
        await RunAsync(LoadSessionAsync);
    }

    private async Task LoadSessionAsync(CancellationToken token, IProgress<string> progress)
    {
        await LoadClassesAsync(token, progress);
        await LoadHomeAsync(token, progress, refreshFromWeb: matrixCache.IsOpen);
    }

    private Task LoadClassesAsync(CancellationToken token, IProgress<string> progress) =>
        LoadClassesForYearAsync(settings.SelectedSchoolYear, token, progress);

    private async Task LoadClassesForYearAsync(int? selected, CancellationToken token, IProgress<string> progress)
    {
        ShowIndeterminateProgress();
        var (available, year) = await browser.ClassesAsync(progress, token, selected);
        token.ThrowIfCancellationRequested();
        var names = AppSettings.ClassList(available.Select(item => item.Name));
        var changedYear = settings.SelectedSchoolYear != selected;
        var next = settings with { ClassNames = names, SelectedSchoolYear = selected, SchoolYear = year ?? settings.SchoolYear };
        var saved = true;
        if (changedYear || !settings.ClassNames.SequenceEqual(names) || settings.SchoolYear != next.SchoolYear)
        {
            try { SaveSettings(next); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                settings = next;
                saved = false;
            }
        }
        SetSchoolYearRange(SelectedSchoolYearRange(), resetDates: changedYear);
        sessionReady = true;
        FillClassList();
        var message = saved ? $"{names.Length} Klassen und Gruppen in WebUntis verfügbar." :
            "Die Klassenliste wurde geladen. Beim lokalen Speichern ist ein Fehler aufgetreten.";
        SetPageStatus(settingsPage, message);
        if (loadedHome.Count == 0) SetPageStatus(homePage, message);
    }

    private ReportRange SelectedSchoolYearRange()
    {
        var current = SchoolYears.StartYear(DateOnly.FromDateTime(DateTime.Today));
        var selected = settings.SelectedSchoolYear ?? current;
        if (settings.SchoolYear is { } confirmed && SchoolYears.StartYear(confirmed.From) == selected) return confirmed;
        if (selected == current)
        {
            if (SchoolYears.StartYear(ShownSchoolYear.From) == current) return ShownSchoolYear;
        }
        return SchoolYears.CalendarRange(selected);
    }

    private void FillSchoolYears()
    {
        var current = SchoolYears.StartYear(DateOnly.FromDateTime(DateTime.Today));
        var choices = new List<SchoolYearChoice> { new(null, $"Aktuell ({SchoolYears.Label(current)})") };
        var years = new HashSet<int> { current - 1, current + 1 };
        if (settings.SelectedSchoolYear is { } selected) years.Add(selected);
        choices.AddRange(years.Order().Select(year => new SchoolYearChoice(year, SchoolYears.Label(year))));
        updatingSettings = true;
        try
        {
            schoolYearPicker.Items.Clear();
            schoolYearPicker.Items.AddRange(choices.Cast<object>().ToArray());
            schoolYearPicker.SelectedItem = choices.Single(choice => choice.StartYear == settings.SelectedSchoolYear);
            UpdateSchoolYearPeriod();
        }
        finally { updatingSettings = false; }
    }

    private async Task ChangeSchoolYearAsync()
    {
        if (updatingSettings || schoolYearPicker.SelectedItem is not SchoolYearChoice choice || choice.StartYear == settings.SelectedSchoolYear)
            return;
        await RunAsync(async (token, progress) =>
        {
            await LoadClassesForYearAsync(choice.StartYear, token, progress);
            if (tabs.SelectedTab == homePage) await LoadHomeAsync(token, progress);
        });
        FillSchoolYears();
    }

    private void SetSchoolYearRange(ReportRange range, bool resetDates)
    {
        if (schoolYear != range || resetDates)
        {
            var datesReset = resetDates || CurrentDates() == schoolYear;
            if (datesReset) SetDates(range);
            schoolYear = range;
            schoolYearMatrices.Clear();
            ResetHome();
            ResetMyClass();
            if (datesReset) ResetMatrix();
        }
        UpdateSchoolYearPeriod();
    }

    private void UpdateSchoolYearPeriod() => schoolYearPeriod.Text = $"{schoolYear.From:dd.MM.yyyy} – {schoolYear.To:dd.MM.yyyy}";

    private async Task ExportRosterCsvAsync(CancellationToken token, IProgress<string> progress)
    {
        var names = settings.MyClasses ?? [];
        if (names.Length == 0) throw new InvalidOperationException("Wähle unter Meine Klassen mindestens eine Klasse aus.");

        var range = schoolYear;
        var rows = new List<(string ClassName, Student Student)>();
        ShowProgress(0, names.Length);
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            progress.Report($"{name} · Namensliste wird geladen …");
            var roster = await browser.ReadClassRosterAsync(name, range, progress, token);
            if (roster.Length == 0)
                throw new InvalidOperationException($"WebUntis hat für «{name}» im gewählten Schuljahr keine Namensliste geliefert.");
            rows.AddRange(roster.DistinctBy(student => student.Id).Select(student => (name, student)));
            ShowProgress(index + 1, names.Length);
        }
        token.ThrowIfCancellationRequested();

        var startYear = SchoolYears.StartYear(range.From);
        var yearLabel = SchoolYears.Label(startYear).Replace('/', '-');
        using var dialog = new SaveFileDialog
        {
            Title = "Schülerliste für den Notenrechner speichern", Filter = "CSV-Dateien (*.csv)|*.csv", DefaultExt = "csv",
            FileName = $"Notenrechner-Schuelerliste-{yearLabel}.csv", AddExtension = true, CheckPathExists = true, OverwritePrompt = true,
            InitialDirectory = Directory.Exists(settings.CsvDownloadFolder) ? settings.CsvDownloadFolder! : RosterCsv.DownloadsFolder()
        };
        HideProgress();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            SetPageStatus(settingsPage, "CSV-Speicherung abgebrochen.");
            return;
        }

        ShowIndeterminateProgress();
        var (emails, read, total, failure) = await browser.ReadLessonEmailsAsync(range, progress, ShowProgress, token);
        var export = RosterCsv.Format(rows, startYear, emails);
        await RosterCsv.SaveAsync(dialog.FileName, export.Content, token);
        var folder = Path.GetDirectoryName(dialog.FileName);
        try
        {
            if (settings.CsvDownloadFolder != folder) SaveSettings(settings with { CsvDownloadFolder = folder });
            SetPageStatus(settingsPage, $"{rows.Count} Zeilen · {export.EmailCount} mit E-Mail · {read}/{total} Unterrichtsberichte gelesen");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetPageStatus(settingsPage, "CSV gespeichert. Der gewählte Ordner konnte nicht gemerkt werden.");
        }
        if (failure != null || read < total || (rows.Count > 0 && export.EmailCount == 0))
        {
            HideProgress();
            var detail = $"{read} von {total} Unterrichtsberichten gelesen. {export.EmailCount} von {rows.Count} Zeilen enthalten eine E-Mail.";
            var note = failure == null ? "" : Environment.NewLine + Environment.NewLine + failure;
            MessageBox.Show(this, $"Die CSV wurde gespeichert. {detail} Für die übrigen Zeilen enthält sie die WebUntis-ID.{note}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private Task LoadHomeAsync(CancellationToken token, IProgress<string> progress) => LoadHomeAsync(token, progress, refreshFromWeb: false);

    private async Task LoadHomeAsync(CancellationToken token, IProgress<string> progress, bool refreshFromWeb)
    {
        var pendingClasses = settings.FavoriteClasses.Where(name => refreshFromWeb || !loadedHome.Contains(name)).ToArray();
        if (pendingClasses.Length == 0) return;
        var elapsed = Stopwatch.StartNew();
        var failed = 0;
        var retained = 0;
        var stepsPerMatrix = MatrixLoadSteps;
        var total = pendingClasses.Length * stepsPerMatrix;
        ShowProgress(0, total);
        for (var index = 0; index < pendingClasses.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var name = pendingClasses[index];
            var view = homeViews[name];
            var range = schoolYear;
            var completedBefore = index * stepsPerMatrix;
            if (view.CurrentMatrix == null) view.SetStatus($"{name}   ·   Wird geladen …");
            var classProgress = new Progress<string>(message => progress.Report($"{name} · {message}"));
            try
            {
                var next = await GetSchoolYearMatrixAsync(name, range, classProgress, token,
                    (completed, _) =>
                    {
                        if (completed > 0 && completed < stepsPerMatrix)
                            ShowProgress(completedBefore + completed, total);
                    }, refreshFromWeb);
                ApplyFreshMatrix(RequireCompleteIfKeeping(next, view), isSchoolYear: true);
            }
            catch (OperationCanceledException)
            {
                if (view.CurrentMatrix == null) view.SetStatus($"{name}   ·   Abruf abgebrochen");
                throw;
            }
            catch (Exception error)
            {
                if (view.CurrentMatrix == null) view.SetStatus($"{name}   ·   {error.Message.Split('\n')[0].Trim()}");
                else retained++;
                if (backgroundRefresh && !browser.SessionConfirmed) throw;
                failed++;
            }
            ShowProgress(completedBefore + stepsPerMatrix, total);
        }
        SetPageStatus(homePage, failed == 0 ? $"{loadedHome.Count} Klassen auf der Startseite · Abruf {elapsed.Elapsed.TotalSeconds:0.0} s" :
            $"{loadedHome.Count} Klassen sichtbar · {failed} Abrufe fehlgeschlagen" + (retained > 0 ? $" · {retained} bisherige Stände beibehalten." : "."));
    }

    private async Task OpenMyClassAsync(string name)
    {
        if (running != null && !backgroundRefresh) return;
        resumeAutomaticRefresh = true;
        var range = schoolYear;
        SelectMyClass(name);
        var current = myClassView.CurrentMatrix;
        var keepPosition = current != null && ClassComparer.Equals(current.ClassName, name) && current.From == range.From;
        if (!keepPosition)
        {
            myClassView.SetStatus($"{name}   ·   Wird geladen …");
            myClassDetails.ShowDetails(DayDetails.Empty);
        }
        schoolYearMatrices.TryGetValue(name, out var inMemory);
        var home = homeViews.GetValueOrDefault(name);
        var inheritedFromDevice = inMemory == null && home?.IsShowingDeviceCopy == true;
        if (inheritedFromDevice) inMemory = home!.CurrentMatrix;
        var stored = matrixCache.IsOpen && !keepPosition && inMemory == null ? await ReadCachedMatrixAsync(name, range, schoolYearRequest: true) : null;
        if (IsDisposed || closing || !ClassComparer.Equals(name, openedMyClass) || range != schoolYear) return;
        if (stored != null && myClassView.CurrentMatrix == null)
            myClassDetails.ShowDetails(myClassView.SetMatrix(stored, fromDevice: true));
        else if (!keepPosition && inMemory != null) myClassDetails.ShowDetails(myClassView.SetMatrix(inMemory, fromDevice: inheritedFromDevice));
        if (matrixCache.IsOpen && myClassView.CurrentMatrix != null)
        {
            SetPageStatus(myClassesPage, $"{name} · Matrix geöffnet");
            RefreshVisibleInBackground();
            return;
        }
        await RunAsync(async (token, progress) =>
        {
            if (!ClassComparer.Equals(openedMyClass, name) || range != schoolYear) return;
            try
            {
                var result = await GetSchoolYearMatrixAsync(name, range, progress, token, ShowProgress);
                myClassDetails.ShowDetails(myClassView.SetMatrix(result, keepPosition));
                sessionReady = true;
                SetPageStatus(myClassesPage, $"{name} · Tagesmatrix geöffnet");
            }
            catch
            {
                if (myClassView.CurrentMatrix == null) ResetMyClass();
                throw;
            }
        });
    }

    private async Task<AttendanceMatrix> GetSchoolYearMatrixAsync(string name, ReportRange range, IProgress<string> progress,
        CancellationToken token, Action<int, int>? reportProgress = null, bool refreshFromWeb = false)
    {
        if (!refreshFromWeb && schoolYearMatrices.TryGetValue(name, out var cached)) return cached;
        var result = await ReadMatrixAsync(name, ThroughToday(range), progress, token, reportProgress, storageSchoolYear: range);
        if (result.HasAbsenceReport && result.HasLatenessReport && result.HasTimetable) schoolYearMatrices[name] = result;
        return result;
    }

    private async Task<AttendanceMatrix?> ReadCachedMatrixAsync(string name, ReportRange range, bool schoolYearRequest)
    {
        try { return await matrixCache.ReadAsync(name, range, schoolYearRequest, updateCheckLifetime.Token); }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) when (StorageFailure(error))
        {
            if (!IsDisposed) storageStatus.Text = "Ein gespeicherter Datenstand konnte nicht geöffnet werden. WebUntis wird erneut abgefragt.";
            return null;
        }
    }

    private async Task OpenCachedHomeAsync()
    {
        if (closing || !matrixCache.IsOpen) return;
        resumeAutomaticRefresh = true;
        await LoadCachedHomeViewsAsync();
        RefreshVisibleInBackground();
    }

    private void ApplyHomeMatrix(MatrixView view, AttendanceMatrix matrix, bool fromDevice = false)
    {
        var details = view.SetMatrix(matrix, keepPosition: view.CurrentMatrix != null, fromDevice: fromDevice);
        if (details != DayDetails.Empty) homeDetails.ShowDetails(details);
        loadedHome.Add(matrix.ClassName);
    }

    private static AttendanceMatrix RequireCompleteIfKeeping(AttendanceMatrix matrix, MatrixView view)
    {
        if (!IsComplete(matrix) && view.CurrentMatrix is { } previous && IsComplete(previous))
            throw new InvalidDataException("WebUntis hat keinen vollständigen neuen Datenstand geliefert. Der letzte vollständige Stand bleibt sichtbar.");
        return matrix;
    }

    private static bool IsComplete(AttendanceMatrix matrix) =>
        matrix.HasAbsenceReport && matrix.HasLatenessReport && matrix.HasTimetable;

    private void RefreshVisibleInBackground()
    {
        if (!matrixCache.IsOpen || closing || IsDisposed || !resumeAutomaticRefresh) return;
        if (running != null || waitingForBackground != 0)
        {
            refreshOnIdle = true;
            return;
        }
        var page = tabs.SelectedTab;
        var opened = openedMyClass;
        var request = matrixRequest;
        if (page != homePage && (page != myClassesPage || opened == null) && (page != matrixPage || request == null)) return;
        if (page == homePage && homeViews.Count == 0 && sessionReady) return;
        refreshOnIdle = false;
        backgroundTask = RefreshAsync();

        async Task RefreshAsync()
        {
            await RunAsync(async (token, progress) =>
            {
                if (!sessionReady || !browser.SessionConfirmed)
                {
                    await LoadClassesAsync(token, progress);
                    if (page == homePage) await LoadCachedHomeViewsAsync();
                }
                if (page == homePage) await LoadHomeAsync(token, progress, refreshFromWeb: true);
                else if (page == myClassesPage && opened != null && ClassComparer.Equals(opened, openedMyClass))
                {
                    var updated = await GetSchoolYearMatrixAsync(opened, schoolYear, progress, token, ShowProgress, refreshFromWeb: true);
                    ApplyFreshMatrix(RequireCompleteIfKeeping(updated, myClassView), isSchoolYear: true);
                    SetPageStatus(myClassesPage, $"{opened} · Aus WebUntis aktualisiert");
                }
                else if (page == matrixPage && request != null) await RefreshRequestedMatrixAsync(request, token, progress);
            }, background: true);
        }
    }

    private async Task LoadCachedHomeViewsAsync()
    {
        var range = schoolYear;
        foreach (var (name, view) in homeViews.ToArray())
        {
            if (view.CurrentMatrix != null) continue;
            var stored = await ReadCachedMatrixAsync(name, range, schoolYearRequest: true);
            if (closing || !matrixCache.IsOpen || range != schoolYear) return;
            if (stored != null && !view.IsDisposed && view.CurrentMatrix == null) ApplyHomeMatrix(view, stored, fromDevice: true);
        }
    }

    private static ReportRange ThroughToday(ReportRange range)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return range.Contains(today) ? range with { To = today } : range;
    }

    private async Task OpenRequestedMatrixAsync()
    {
        if (running != null && !backgroundRefresh) return;
        resumeAutomaticRefresh = true;
        var name = classes.Text.Trim();
        var range = CurrentDates();
        var error = name.Length == 0 ? "Wähle eine Klasse oder gib ihr Kürzel ein." : range.From > range.To
            ? "Das Startdatum liegt hinter dem Enddatum." : !range.IsValid() ? "Wähle höchstens ein Schuljahr pro Abruf." : null;
        if (error != null)
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var request = new MatrixRequest(name, range);
        if (!Equals(matrixRequest, request))
        {
            matrixRequest = request;
            matrixView.SetStatus($"{name}   ·   Wird geladen …");
            matrixDetails.ShowDetails(DayDetails.Empty);
        }
        if (matrixCache.IsOpen && matrixView.CurrentMatrix == null)
        {
            var stored = await ReadCachedMatrixAsync(name, range, schoolYearRequest: false);
            if (stored == null && range == schoolYear)
                stored = await ReadCachedMatrixAsync(name, range, schoolYearRequest: true);
            if (closing || matrixRequest != request) return;
            if (stored != null && matrixView.CurrentMatrix == null)
                matrixDetails.ShowDetails(matrixView.SetMatrix(stored, fromDevice: true));
            else if (matrixView.CurrentMatrix == null)
            {
                var exact = range == schoolYear ? ThroughToday(range) : range;
                if (schoolYearMatrices.TryGetValue(name, out var current) && exact == new ReportRange(current.From, current.To))
                    matrixDetails.ShowDetails(matrixView.SetMatrix(current));
            }
        }

        // The explicit refresh action may show the WebUntis login dialog. A stored matrix stays visible during the request.
        await RunAsync((token, progress) => RefreshRequestedMatrixAsync(request, token, progress));
    }

    private async Task RefreshRequestedMatrixAsync(MatrixRequest request, CancellationToken token, IProgress<string> progress)
    {
        if (matrixRequest != request) return;
        var (name, range) = request;
        var elapsed = Stopwatch.StartNew();
        var result = RequireCompleteIfKeeping(await ReadMatrixAsync(name, range, progress, token, ShowProgress), matrixView);
        sessionReady = true;
        ApplyFreshMatrix(result, isSchoolYear: range == schoolYear);
        SetPageStatus(matrixPage, $"{name} · Aktualisiert um {DateTime.Now:HH:mm} Uhr · Abruf {elapsed.Elapsed.TotalSeconds:0.0} s");
    }

    private void ApplyFreshMatrix(AttendanceMatrix result, bool isSchoolYear, bool allowPartial = false)
    {
        var name = result.ClassName;
        var range = new ReportRange(result.From, result.To);
        if (isSchoolYear)
        {
            if (IsComplete(result)) schoolYearMatrices[name] = result;
            if (homeViews.TryGetValue(name, out var home) && CanReplace(home))
                ApplyHomeMatrix(home, result);
            if (ClassComparer.Equals(openedMyClass, name) && CanReplace(myClassView))
                myClassDetails.ShowDetails(myClassView.SetMatrix(result, keepPosition: myClassView.CurrentMatrix != null));
        }
        if (matrixRequest is { } request && ClassComparer.Equals(request.Name, name) && request.Range == range &&
            CanReplace(matrixView))
            matrixDetails.ShowDetails(matrixView.SetMatrix(result, keepPosition: matrixView.CurrentMatrix != null));

        bool CanReplace(MatrixView target) => allowPartial || IsComplete(result) || target.CurrentMatrix is not { } old || !IsComplete(old);
    }

    private async Task<AttendanceMatrix> ReadMatrixAsync(string name, ReportRange range, IProgress<string> progress,
        CancellationToken token, Action<int, int>? reportProgress = null, bool afterExcuse = false, ReportRange? storageSchoolYear = null)
    {
        var steps = MatrixLoadSteps;
        reportProgress?.Invoke(0, steps);
        Action<int>? reportCompleted = reportProgress == null ? null : count => reportProgress(count, steps);
        var batch = await browser.FetchAsync(name, range.From, range.To, progress, token, reportCompleted);
        token.ThrowIfCancellationRequested();
        var homeClass = homeViews.ContainsKey(name) && (afterExcuse || schoolYear == range || ThroughToday(schoolYear) == range);
        var prepareAbsences = batch.ClassId.Length > 0 && homeClass &&
            (afterExcuse || batch.Csvs.Any(csv => csv.Kind is ReportKind.Absences or ReportKind.Lateness));
        using var preloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var preparedNotes = prepareAbsences
            ? browser.PrepareClassAbsencesAsync(name, batch.ClassId, range, preloadCancellation.Token, refresh: true)
            : Task.FromResult<AbsenceNote[]?>(null);
        try
        {
            return await ParseMatrixAsync(name, range, batch, preparedNotes, progress, token, reportProgress, afterExcuse, storageSchoolYear);
        }
        finally
        {
            preloadCancellation.Cancel();
            try { await preparedNotes; }
            catch (OperationCanceledException) when (preloadCancellation.IsCancellationRequested) { }
        }
    }

    private async Task<AttendanceMatrix> ParseMatrixAsync(string name, ReportRange range, ReportBatch batch,
        Task<AbsenceNote[]?> preparedNotes, IProgress<string> progress, CancellationToken token,
        Action<int, int>? reportProgress, bool afterExcuse, ReportRange? storageSchoolYear)
    {
        progress.Report("Berichte werden ausgewertet …");
        var reports = batch.EmptyReports.Select(kind => new Report(kind, [], [], [])).ToList();
        var reportHasAbsenceText = false;
        var parsed = await Task.WhenAll(batch.Csvs.Select(csv => Task.Run(() =>
        {
            try
            {
                var (report, hasAbsenceText) = ReadReport(csv.Content, csv.Kind);
                return (csv.Kind, Report: (Report?)report, HasAbsenceText: hasAbsenceText, Error: (string?)null);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return (csv.Kind, Report: (Report?)null, HasAbsenceText: false, Error: (string?)error.Message);
            }
        }, token)));
        batch.Csvs.Clear();
        foreach (var item in parsed)
        {
            if (item.Report != null)
            {
                reports.Add(item.Report);
                if (item.Kind == ReportKind.Absences) reportHasAbsenceText = item.HasAbsenceText;
            }
            else batch.Notes.Add($"{CsvReport.Label(item.Kind)} · {item.Error}");
        }
        (Report Original, Report Details)? absenceEnrichment = null;
        var absenceTimesLoaded = false;
        foreach (var kind in new[] { ReportKind.Absences, ReportKind.Lateness })
        {
            var index = reports.FindIndex(report => report.Kind == kind);
            if (index >= 0 && reports[index].Complete && reports[index].Entries.All(entry =>
                (entry.Period.Length > 0 || entry.Start.HasValue) &&
                (kind != ReportKind.Absences || entry.Excuse != Excuse.Unspecified))) continue;
            var times = await browser.ReadAbsenceTimesAsync(name, batch, range, kind, progress, token, reusePage: absenceTimesLoaded);
            if (times == null) continue;
            absenceTimesLoaded = true;
            if (index < 0) reports.Add(times);
            else if (!reports[index].Complete) reports[index] = times with { ListedStudents = reports[index].ListedStudents };
            else
            {
                if (kind == ReportKind.Absences) absenceEnrichment = (reports[index], times);
                reports[index] = reports[index].WithAbsenceTimes(times);
            }
        }
        if (reports.Count == 0)
            throw new InvalidOperationException("Es konnte kein Bericht eingelesen werden." + Environment.NewLine +
                string.Join(Environment.NewLine, batch.Notes));

        var absenceIndex = reports.FindIndex(report => report.Kind == ReportKind.Absences && report.Complete &&
            report.Entries.Any(entry => entry.Kind == EntryKind.Absent && entry.Excuse == Excuse.Unspecified));
        if (absenceIndex >= 0)
        {
            var csv = await browser.FetchExcusedAsync(batch, range.From, range.To, progress, token);
            token.ThrowIfCancellationRequested();
            if (csv != null)
            {
                try
                {
                    var (filtered, _) = await Task.Run(() => ReadReport(csv, ReportKind.Absences), token);
                    reports[absenceIndex] = absenceEnrichment is { } enrichment
                        ? enrichment.Original.WithExcusedAbsences(filtered).WithAbsenceTimes(enrichment.Details)
                        : reports[absenceIndex].WithExcusedAbsences(filtered);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    batch.Notes.Add($"Entschuldigte Fehlzeiten · {error.Message}");
                }
            }
        }
        token.ThrowIfCancellationRequested();
        var notes = (afterExcuse || !reportHasAbsenceText) && reports.Any(report => report.Entries.Any(entry => entry.Kind == EntryKind.Absent))
            ? ReadNotesAsync() : Task.FromResult<AbsenceNote[]>([]);
        var periods = reports.Any(report => report.Entries.Any(entry => entry.Period.Length == 0 && entry.Start.HasValue))
            ? browser.ReadSchoolPeriodsAsync(SchoolYears.StartYear(range.From), progress, token) : Task.FromResult<SchoolPeriod[]>([]);
        await Task.WhenAll(notes, periods);
        var absenceNotes = await notes;
        var schoolPeriods = await periods;
        token.ThrowIfCancellationRequested();
        var data = new MatrixData(name, batch.ClassId, range, DateTimeOffset.UtcNow, batch.Students,
            [.. reports, new Report(ReportKind.Timetable, [], [], batch.Notes.ToArray(), Complete: false)], absenceNotes, schoolPeriods);
        var matrix = await Task.Run(data.Build, token);
        if (matrixCache.IsOpen && matrix.ClassId.Length > 0 && IsComplete(matrix))
        {
            try
            {
                await matrixCache.SaveAsync(data, storageSchoolYear, token);
            }
            catch (Exception error) when (StorageFailure(error))
            {
                storageStatus.Text = "Die neuen Daten sind sichtbar. Der verschlüsselte Stand konnte gerade nicht gespeichert werden.";
            }
        }
        reportProgress?.Invoke(MatrixLoadSteps, MatrixLoadSteps);
        return matrix;

        async Task<AbsenceNote[]> ReadNotesAsync() => await preparedNotes ?? await browser.ReadAbsenceNotesAsync(batch, range, progress, token);
    }

    private static (Report Report, bool HasAbsenceText) ReadReport(byte[] content, ReportKind kind)
    {
        var csv = CsvReport.Read(content);
        if (csv.Rows.Length == 0) throw new InvalidDataException("Die CSV-Datei ist leer.");
        var layout = csv.Detect(kind);
        var report = csv.Import(kind, layout) with { AllStudentsIncluded = kind is ReportKind.Absences or ReportKind.Lateness };
        return (report, layout.Has(CsvField.AbsenceText));
    }

    private void SynchronizeHomeViews()
    {
        var favorites = settings.FavoriteClasses;
        homeRows.SuspendLayout();
        foreach (var removed in homeViews.Keys.Except(favorites, ClassComparer).ToArray())
        {
            homeViews[removed].Dispose();
            homeViews.Remove(removed);
            loadedHome.Remove(removed);
        }
        foreach (var empty in homeRows.Controls.OfType<Label>().ToArray()) empty.Dispose();
        homeRows.Controls.Clear();
        homeRows.RowStyles.Clear();
        homeRows.RowCount = Math.Max(1, favorites.Length);
        for (var i = 0; i < favorites.Length; i++)
        {
            var name = favorites[i];
            if (!homeViews.TryGetValue(name, out var view))
            {
                view = new MatrixView(stacked: true, collapsibleClass: name) { Margin = new Padding(0, 0, 0, 14) };
                view.SetStatus(name);
                view.DaySelected += ShowHomeDetails;
                view.AbsenceRequested += OpenHomeAbsence;
                homeViews.Add(name, view);
            }
            homeRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            homeRows.Controls.Add(view, 0, i);
        }
        if (favorites.Length == 0)
            homeRows.Controls.Add(new Label { Text = "Wähle deine Klassen unter Einstellungen aus.", AutoSize = true,
                Margin = new Padding(3, 8, 3, 0) }, 0, 0);
        homeRows.ResumeLayout();
    }

    private void ShowHomeDetails(MatrixView source, DayDetails details)
    {
        foreach (var view in homeViews.Values.Where(view => view != source)) view.ClearSelection();
        homeDetails.ShowDetails(details);
    }

    private async void OpenHomeAbsence(MatrixView source, AttendanceMatrix matrix, Student student, DateOnly day,
        Point position, bool direct)
    {
        if ((running != null && !backgroundRefresh) || !IsCurrent()) return;
        homeAbsenceMenu?.Close();
        await Task.Yield();
        if ((running != null && !backgroundRefresh) || !IsCurrent()) return;
        if (browser.PreparedAbsenceActions(matrix, student, day) is { Length: > 0 } rows)
        {
            var available = Available(rows);
            if (direct && available.Length == 1) await RunAsync((token, progress) => EditAsync(available[0], token, progress));
            else ShowChoices(rows, available);
            return;
        }

        var menu = direct ? null : CreateMenu();
        var waiting = menu == null ? null : AddMessage(menu, "WebUntis-Einträge werden gesucht …");
        menu?.Show(position);
        await RunAsync(async (token, progress) =>
        {
            if (!IsCurrent() || menu is { Visible: false }) return;
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(token);
            ToolStripDropDownClosedEventHandler closed = (_, _) => lookup.Cancel();
            if (menu != null) menu.Closed += closed;
            try
            {
                var found = await browser.ReadAbsenceActionsAsync(matrix, student, day, progress, lookup.Token);
                if (!IsCurrent() || menu is { Visible: false }) return;
                var available = Available(found);
                if (direct && available.Length == 1) await EditAsync(available[0], token, progress);
                else ShowChoices(found, available, menu, waiting);
            }
            catch (OperationCanceledException) when (lookup.IsCancellationRequested && !token.IsCancellationRequested) { }
            catch (Exception error) when (menu != null && (error is InvalidOperationException or IOException or TimeoutException or
                InvalidDataException or System.Runtime.InteropServices.COMException or System.Text.Json.JsonException))
            {
                if (menu.Visible && waiting is { IsDisposed: false }) waiting.Text = error.Message;
                else if (tabs.SelectedTab == homePage) SetPageStatus(homePage, "Die WebUntis-Abwesenheiten konnten nicht geladen werden.");
            }
            finally { if (menu != null) menu.Closed -= closed; }
        }, keepAbsencePreload: true);

        bool IsCurrent() => !IsDisposed && tabs.SelectedTab == homePage && !source.IsDisposed &&
            homeViews.GetValueOrDefault(matrix.ClassName) == source && source.CurrentMatrix == matrix;

        static AbsenceListEntry[] Available(AbsenceListEntry[] rows) => rows.Where(row => row.CanExcuse).OrderBy(row => row.From)
            .ThenBy(row => CsvReport.ReadTime(row.Start)).ThenBy(row => row.To).ThenBy(row => CsvReport.ReadTime(row.End)).ToArray();

        ContextMenuStrip CreateMenu()
        {
            var result = new ContextMenuStrip { ShowImageMargin = false, ShowItemToolTips = true, AccessibleName = "WebUntis-Abwesenheiten" };
            homeAbsenceMenu = result;
            result.Closed += (_, _) =>
            {
                if (homeAbsenceMenu == result) homeAbsenceMenu = null;
                if (!IsDisposed) BeginInvoke(result.Dispose);
            };
            return result;
        }

        static ToolStripMenuItem AddMessage(ContextMenuStrip target, string text)
        {
            var item = new ToolStripMenuItem(text) { Enabled = false };
            target.Items.Add(item);
            return item;
        }

        void ShowChoices(AbsenceListEntry[] found, AbsenceListEntry[] available, ContextMenuStrip? target = null,
            ToolStripMenuItem? placeholder = null)
        {
            var show = target == null;
            target ??= CreateMenu();
            if (placeholder != null)
            {
                target.Items.Remove(placeholder);
                placeholder.Dispose();
            }
            if (available.Length == 0)
                AddMessage(target, found.Length == 0 ? "Keine passende WebUntis-Abwesenheit gefunden." :
                    "WebUntis bietet für diese Einträge keinen Entschuldigungsdialog an.");
            foreach (var row in available)
            {
                var period = row.From == row.To ? $"{row.From:dd.MM.} · {row.Start}–{row.End}" :
                    $"{row.From:dd.MM.} {row.Start} – {row.To:dd.MM.} {row.End}";
                if (row.Status.Length > 0) period += $" · {row.Status}";
                var item = new ToolStripMenuItem($"Entschuldigung · {period}")
                {
                    ToolTipText = row.Reason.Length > 0 ? row.Reason : "Entschuldigung in WebUntis öffnen"
                };
                item.Click += (_, _) => BeginInvoke(async () =>
                {
                    if (IsCurrent()) await RunAsync((token, progress) => EditAsync(row, token, progress));
                });
                target.Items.Add(item);
            }
            if (show) target.Show(position);
        }

        async Task EditAsync(AbsenceListEntry row, CancellationToken token, IProgress<string> progress)
        {
            if (!IsCurrent()) return;
            absencePreload?.Cancel();
            var saved = await browser.ShowExcuseAsync(matrix, student, day, row, progress, token);
            if (saved) await ReloadEditedClassAsync(source, matrix, token, progress);
        }
    }

    private async Task ReloadEditedClassAsync(MatrixView source, AttendanceMatrix previous,
        CancellationToken token, IProgress<string> progress)
    {
        resumeAutomaticRefresh = true;
        var name = previous.ClassName;
        var currentYear = previous.From == schoolYear.From && previous.To <= schoolYear.To;
        var range = currentYear && matrixCache.IsOpen ? ThroughToday(schoolYear) : new ReportRange(previous.From, previous.To);
        var regularMatrix = matrixView.CurrentMatrix;
        loadedHome.Remove(name);
        schoolYearMatrices.Remove(name);
        if (settings.StoreDataOnDevice)
        {
            try { matrixCache.ForgetClass(name); }
            catch (Exception error) when (StorageFailure(error))
            {
                storageStatus.Text = "Ein älterer gespeicherter Datenstand konnte nicht entfernt werden.";
            }
            source.MarkForRefresh();
            if (ClassComparer.Equals(openedMyClass, name)) myClassView.MarkForRefresh();
            if (regularMatrix?.ClassId == previous.ClassId) matrixView.MarkForRefresh();
        }
        progress.Report($"{name} · Fehlzeiten werden neu gelesen …");
        var updated = await ReadMatrixAsync(name, range, progress, token, ShowProgress, afterExcuse: true,
            storageSchoolYear: currentYear ? schoolYear : null);
        if (!updated.HasAbsenceReport)
            throw new InvalidOperationException("Die aktuellen Fehlzeiten konnten nach dem Speichern nicht gelesen werden. Rufe die Klasse erneut ab.");
        ApplyFreshMatrix(updated, isSchoolYear: true, allowPartial: true);
        if (source.CurrentMatrix != updated)
            homeDetails.ShowDetails(source.SetMatrix(updated, keepPosition: true));
        if (regularMatrix?.ClassId == previous.ClassId && matrixView.CurrentMatrix == regularMatrix &&
            SchoolYears.StartYear(regularMatrix.From) == SchoolYears.StartYear(previous.From)) ResetMatrix();
        SetPageStatus(homePage, $"{name} · Aktuelle Fehlzeiten aus WebUntis geladen");
    }

    private void PreloadHomeAbsences()
    {
        if (!sessionReady || closeAfterCancellation || IsDisposed) return;
        absencePreload?.Cancel();
        var matrices = homeViews.Values.Where(view => !view.IsShowingDeviceCopy).Select(view => view.CurrentMatrix)
            .OfType<AttendanceMatrix>().Where(matrix => !matrix.IsDemo && matrix.ClassId.Length > 0).ToArray();
        browser.KeepPreparedAbsences(matrices);
        if (matrices.Length > 0) _ = PreloadAsync();

        async Task PreloadAsync()
        {
            using var cancellation = new CancellationTokenSource();
            absencePreload = cancellation;
            try
            {
                foreach (var matrix in matrices)
                    await browser.PrepareClassAbsencesAsync(matrix.ClassName, matrix.ClassId, new ReportRange(matrix.From, matrix.To), cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally { if (absencePreload == cancellation) absencePreload = null; }
        }
    }

    private void SynchronizeMyClassButtons()
    {
        myClassButtons.SuspendLayout();
        foreach (Control control in myClassButtons.Controls.Cast<Control>().ToArray()) control.Dispose();
        foreach (var name in settings.MyClasses ?? [])
        {
            var button = new RadioButton
            {
                Text = name, Appearance = Appearance.Button, AutoCheck = false, AutoSize = true,
                MinimumSize = new Size(0, 34), Padding = new Padding(6, 0, 6, 0), Margin = new Padding(4, 3, 4, 3),
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, BackColor = Color.White, ForeColor = AppTheme.Ink,
                Checked = ClassComparer.Equals(name, openedMyClass)
            };
            button.FlatAppearance.CheckedBackColor = AppTheme.TintPressed;
            button.FlatAppearance.BorderColor = AppTheme.Line;
            button.FlatAppearance.MouseOverBackColor = AppTheme.Tint;
            button.Click += async (_, _) => await OpenMyClassAsync(name);
            myClassButtons.Controls.Add(button);
        }
        if (openedMyClass != null && !(settings.MyClasses ?? []).Contains(openedMyClass, ClassComparer)) ResetMyClass();
        myClassButtons.ResumeLayout();
    }

    private void ResetMyClass()
    {
        SelectMyClass(null);
        myClassView.SetStatus(settings.MyClasses is { Length: > 0 } ? "Öffne eine Klasse." : "Wähle unter Einstellungen Klassen für diesen Reiter aus.");
        myClassDetails.ShowDetails(DayDetails.Empty);
        SetPageStatus(myClassesPage, "");
    }

    private void SelectMyClass(string? name)
    {
        openedMyClass = name;
        foreach (var button in myClassButtons.Controls.OfType<RadioButton>())
            button.Checked = ClassComparer.Equals(button.Text, name);
    }

    private void ResetHome()
    {
        loadedHome.Clear();
        homeDetails.ShowDetails(DayDetails.Empty);
        foreach (var (name, view) in homeViews) view.SetStatus(name);
        SetPageStatus(homePage, "");
    }

    private void ResetMatrix()
    {
        matrixRequest = null;
        matrixView.SetStatus("Wähle eine Klasse und klicke auf Aktualisieren.");
        matrixDetails.ShowDetails(DayDetails.Empty);
        SetMatrixFilterStatus();
    }

    private void SetMatrixFilterStatus()
    {
        var name = classes.Text.Trim();
        SetPageStatus(matrixPage, name.Length > 0 ? $"{name} · Auswahl · Klicke auf Aktualisieren." : "Wähle eine Klasse.");
    }

    private void ClearSessionViews()
    {
        sessionReady = false;
        schoolYearMatrices.Clear();
        ResetHome();
        ResetMyClass();
        ResetMatrix();
    }

    private async Task SignOutAsync(CancellationToken token, IProgress<string> progress)
    {
        ClearSessionViews();
        try { await browser.SignOutAsync(token); }
        finally
        {
            if (settings.StoreDataOnDevice)
            {
                try
                {
                    await matrixCache.ClearAsync();
                    SaveSettings(settings with { StoreDataOnDevice = false });
                }
                finally
                {
                    SyncAccountChoices();
                    ShowStorageStatus();
                }
            }
        }
        SetPageStatus(settingsPage, "In der App von WebUntis abgemeldet.");
    }

    private async Task ChangeDeviceStorageAsync()
    {
        if (updatingSettings || storeData.Checked == settings.StoreDataOnDevice) return;
        var requested = storeData.Checked;
        await RunAsync(async (token, progress) =>
        {
            HideProgress();
            if (requested)
            {
                resumeAutomaticRefresh = true;
                progress.Report("Windows Hello · Lokalen Datenspeicher einrichten …");
                await matrixCache.CreateAsync(Handle, token);
                try { SaveSettings(settings with { StoreDataOnDevice = true }); }
                catch
                {
                    await matrixCache.ClearAsync();
                    throw;
                }
                SetPageStatus(settingsPage, "Daten werden verschlüsselt auf diesem Gerät gespeichert.");
                ShowStorageStatus();
                if (sessionReady)
                {
                    if (openedMyClass is { } opened && !homeViews.ContainsKey(opened))
                        ApplyFreshMatrix(await GetSchoolYearMatrixAsync(opened, schoolYear, progress, token, ShowProgress,
                            refreshFromWeb: true), isSchoolYear: true);
                    if (matrixRequest is { } requestedMatrix && (requestedMatrix.Range != ThroughToday(schoolYear) ||
                        (!homeViews.ContainsKey(requestedMatrix.Name) && !ClassComparer.Equals(requestedMatrix.Name, openedMyClass))))
                        await RefreshRequestedMatrixAsync(requestedMatrix, token, progress);
                    await LoadHomeAsync(token, progress, refreshFromWeb: true);
                }
            }
            else
            {
                autoUpdate.Stop();
                ClearDeviceCopiesOnScreen();
                await matrixCache.ClearAsync();
                SaveSettings(settings with { StoreDataOnDevice = false });
                SetPageStatus(settingsPage, "Der lokale Datenspeicher wurde gelöscht.");
            }
            ShowStorageStatus();
        });
        if (!IsDisposed && !closing)
        {
            SyncAccountChoices();
            ShowStorageStatus();
        }
    }

    private void ShowStorageStatus()
    {
        unlockStore.Visible = settings.StoreDataOnDevice && !matrixCache.IsOpen;
        storageStatus.Text = matrixCache.IsOpen ? "Mit Windows Hello entsperrt · verschlüsselte Speicherung" : settings.StoreDataOnDevice
            ? "Datenspeicher gesperrt."
            : "Optionaler Datenspeicher · geschützt mit Windows Hello";
    }

    private async Task UnlockStoreAsync()
    {
        if (!settings.StoreDataOnDevice || matrixCache.IsOpen) return;
        await RunAsync(async (token, progress) =>
        {
            HideProgress();
            progress.Report("Windows Hello · Datenspeicher entsperren …");
            await matrixCache.UnlockAsync(Handle, token);
            resumeAutomaticRefresh = true;
            ShowStorageStatus();
            SetPageStatus(settingsPage, "Datenspeicher entsperrt.");
        });
    }

    private void ClearDeviceCopiesOnScreen()
    {
        foreach (var (name, view) in homeViews)
        {
            if (!view.IsShowingDeviceCopy) continue;
            view.SetStatus(name);
            loadedHome.Remove(name);
            homeDetails.ShowDetails(DayDetails.Empty);
        }
        if (myClassView.IsShowingDeviceCopy) ResetMyClass();
        if (matrixView.IsShowingDeviceCopy) ResetMatrix();
    }

    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or CryptographicException or JsonException or System.Runtime.InteropServices.COMException or EntryPointNotFoundException;

    private sealed record MatrixRequest(string Name, ReportRange Range);

    private async Task ChangeRememberLoginAsync()
    {
        if (updatingSettings || remember.Checked == settings.RememberLogin) return;
        var requested = remember.Checked;
        await RunAsync(async (token, progress) =>
        {
            await browser.SetRememberLoginAsync(requested, token);
            try { SaveSettings(settings with { RememberLogin = requested }); }
            catch
            {
                await browser.SetRememberLoginAsync(settings.RememberLogin, CancellationToken.None);
                throw;
            }
            if (requested)
            {
                if (!browser.SessionConfirmed) await LoadSessionAsync(token, progress);
                SetPageStatus(settingsPage, browser.HasSavedLogin ? "Die WebUntis-Sitzung wurde gespeichert." : "Die WebUntis-Sitzung konnte nicht gespeichert werden.");
            }
            else SetPageStatus(settingsPage, "Die gespeicherte WebUntis-Anmeldung wurde entfernt.");
        });
        SyncAccountChoices();
    }

    private async Task ChangeHelloAsync()
    {
        if (updatingSettings || hello.Checked == settings.ProtectWithHello) return;
        var requested = hello.Checked;
        await RunAsync(async (token, _) =>
        {
            if (requested && !await WindowsHello.VerifyAsync(Handle, $"Windows Hello für {Application.ProductName} aktivieren"))
                throw new OperationCanceledException();
            token.ThrowIfCancellationRequested();
            SaveSettings(settings with { ProtectWithHello = requested });
            SetPageStatus(settingsPage, requested ? "Windows Hello schützt den Programmstart." : "Windows Hello ist deaktiviert.");
        });
        SyncAccountChoices();
    }

    private async Task ChangeLegendAsync()
    {
        var visible = showLegend.Checked;
        if (visible == settings.ShowLegend) return;
        await RunAsync((_, _) =>
        {
            SaveSettings(settings with { ShowLegend = visible });
            SetLegendVisibility();
            SetPageStatus(settingsPage, visible ? "Legende eingeblendet." : "Legende ausgeblendet.");
            return Task.CompletedTask;
        });
        showLegend.Checked = settings.ShowLegend;
    }

    private void SetLegendVisibility()
    {
        foreach (var legend in legends) legend.Visible = settings.ShowLegend;
    }

    private void SyncAccountChoices()
    {
        updatingSettings = true;
        try
        {
            remember.Checked = settings.RememberLogin;
            hello.Checked = settings.ProtectWithHello;
            storeData.Checked = settings.StoreDataOnDevice;
        }
        finally { updatingSettings = false; }
        UpdateRememberedLoginStatus();
    }

    private void UpdateRememberedLoginStatus()
    {
        rememberedLogin.Visible = settings.RememberLogin;
        rememberedLogin.Text = browser.HasSavedLogin ? "WebUntis-Sitzung gespeichert" : browser.SessionConfirmed ?
            "Sitzung konnte nicht gespeichert werden" : "Sitzung wird nach der Anmeldung gespeichert";
    }

    private void FillClassList()
    {
        var previous = classes.Text;
        classes.Items.Clear();
        classes.Items.AddRange(settings.ClassNames.Cast<object>().ToArray());
        classes.Text = previous.Length > 0 ? previous : settings.FavoriteClasses.FirstOrDefault() ?? settings.ClassNames.FirstOrDefault() ?? "";
        FillClassOptions();
    }

    private void FillClassOptions()
    {
        updatingSettings = true;
        try
        {
            var choices = AppSettings.ClassList(settings.ClassNames.Concat(settings.FavoriteClasses).Concat(settings.MyClasses ?? []))
                .Where(name => name.Contains(classFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            FillOptions(homeOptions, settings.FavoriteClasses);
            FillOptions(myClassOptions, settings.MyClasses ?? []);

            void FillOptions(CheckedListBox list, string[] selected)
            {
                list.BeginUpdate();
                try
                {
                    list.Items.Clear();
                    foreach (var name in choices) list.Items.Add(name, selected.Contains(name, ClassComparer));
                }
                finally { list.EndUpdate(); }
            }
        }
        finally { updatingSettings = false; }
    }

    private void SaveClassChoice(CheckedListBox list, ItemCheckEventArgs args, bool forHome)
    {
        if (updatingSettings || list.Items[args.Index] is not string name) return;
        var checkedClasses = new HashSet<string>(forHome ? settings.FavoriteClasses : settings.MyClasses ?? [], ClassComparer);
        if (args.NewValue == CheckState.Checked) checkedClasses.Add(name);
        else checkedClasses.Remove(name);
        var selection = AppSettings.ClassList(checkedClasses);
        try { SaveSettings(forHome ? settings with { FavoriteClasses = selection } : settings with { MyClasses = selection }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            args.NewValue = args.CurrentValue;
            MessageBox.Show(this, "Die Klassenauswahl konnte nicht gespeichert werden. " + error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (forHome)
        {
            homeDetails.ShowDetails(DayDetails.Empty);
            SynchronizeHomeViews();
            if (running == null) PreloadHomeAbsences();
        }
        else
        {
            SynchronizeMyClassButtons();
            if (openedMyClass == null) ResetMyClass();
        }
    }

    private void SaveSettings(AppSettings next)
    {
        next.Save(appFolder);
        settings = next;
    }

    private ReportRange CurrentDates() => new(DateOnly.FromDateTime(from.Value), DateOnly.FromDateTime(to.Value));

    private void SetDates(ReportRange range)
    {
        from.Value = range.From.ToDateTime(TimeOnly.MinValue);
        to.Value = range.To.ToDateTime(TimeOnly.MinValue);
    }

    private async Task RunAsync(Func<CancellationToken, IProgress<string>, Task> action, bool keepAbsencePreload = false,
        bool background = false)
    {
        if (!background && backgroundRefresh && backgroundTask is { } pending)
        {
            waitingForBackground++;
            try
            {
                running?.Cancel();
                await pending;
            }
            finally { waitingForBackground--; }
        }
        if (running != null || IsDisposed || closing) return;
        var initiatingPage = tabs.SelectedTab;
        using var cancellation = new CancellationTokenSource();
        if (!background) refreshOnIdle = false;
        autoUpdate.Stop();
        running = cancellation;
        backgroundRefresh = background;
        browser.AllowLoginPrompt = !background;
        if (!keepAbsencePreload) absencePreload?.Cancel();
        if (!background) busyControls.ForEach(control => control.Enabled = false);
        else schoolYearPicker.Enabled = false;
        RefreshUpdateButtons();
        status.Text = "Bitte warten …";
        ShowIndeterminateProgress();
        var progress = new Progress<string>(message =>
        {
            if (running == cancellation && !IsDisposed) status.Text = message;
        });
        try { await action(cancellation.Token, progress); }
        catch (OperationCanceledException)
        {
            if (initiatingPage != null && waitingForBackground == 0) SetPageStatus(initiatingPage, "Vorgang abgebrochen.");
        }
        catch (Exception) when (closing || cancellation.IsCancellationRequested && waitingForBackground > 0) { }
        catch (Exception error)
        {
            if (background)
            {
                if (initiatingPage != null) SetPageStatus(initiatingPage, browser.SessionConfirmed
                    ? "Aktualisierung derzeit nicht möglich. Der letzte Datenstand bleibt sichtbar."
                    : "Aktualisierung derzeit nicht möglich. Prüfe die Verbindung oder melde dich bei WebUntis an.");
            }
            else
            {
                status.Text = "Vorgang nicht abgeschlossen.";
                if (initiatingPage != null) SetPageStatus(initiatingPage, status.Text);
                HideProgress();
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        finally
        {
            running = null;
            backgroundRefresh = false;
            browser.AllowLoginPrompt = true;
            HideProgress();
            if (!background) busyControls.ForEach(control => control.Enabled = true);
            else schoolYearPicker.Enabled = true;
            RefreshUpdateButtons();
            UpdateRememberedLoginStatus();
            ShowPageStatus();
            if (closeAfterCancellation) Close();
            else if (waitingForBackground == 0 && !cancellation.IsCancellationRequested)
            {
                if (matrixCache.IsOpen && resumeAutomaticRefresh)
                {
                    autoUpdate.Start();
                    if (refreshOnIdle) BeginInvoke(RefreshVisibleInBackground);
                }
                if (!keepAbsencePreload || absencePreload == null || absencePreload.IsCancellationRequested) PreloadHomeAbsences();
            }
        }
    }

    private void SetPageStatus(TabPage page, string message)
    {
        pageStatuses[page] = message;
        if (running == null && tabs.SelectedTab == page) status.Text = message;
    }

    private void ShowPageStatus() => status.Text = tabs.SelectedTab is { } page ? pageStatuses.GetValueOrDefault(page, "") : "";

    private void ShowIndeterminateProgress()
    {
        loadingCount.Visible = false;
        loading.Style = ProgressBarStyle.Marquee;
        loading.Visible = true;
        ShowTransferCancel();
    }

    private void ShowProgress(int completed, int total)
    {
        if (total <= 0) return;
        loading.Style = ProgressBarStyle.Continuous;
        loading.Maximum = total;
        loading.Value = completed;
        loadingCount.Text = $"{completed}/{total}";
        loadingCount.Visible = true;
        loading.Visible = true;
        ShowTransferCancel();
    }

    private void ShowTransferCancel()
    {
        cancel.Size = new Size(LogicalToDeviceUnits(25), LogicalToDeviceUnits(24));
        cancel.Enabled = running?.IsCancellationRequested == false;
        cancel.Visible = running != null;
    }

    private void HideProgress()
    {
        loading.Visible = false;
        loadingCount.Visible = false;
        cancel.Visible = false;
    }

    private static SplitContainer WithDetails(Control upper, DetailsView details)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2,
            Size = new Size(1000, 540), SplitterWidth = 7, SplitterDistance = 334, Panel1MinSize = 90, Panel2MinSize = 100,
            BackColor = MatrixView.Line };
        split.Panel1.BackColor = AppTheme.Surface;
        split.Panel1.Controls.Add(upper);
        split.Panel2.Controls.Add(details);
        return split;
    }

    private static FlowLayoutPanel CreateLegend()
    {
        var legend = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(9, 2, 9, 2), AccessibleName = "Legende" };
        foreach (var label in new[] { "• Anwesend / kein Fehlzeiteintrag", "│ Fehlzeit", "E Entschuldigt", "+ Verspätet",
            "│+ / E+ Beides", "? Ungeklärt", "B Befreit" })
            legend.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(2, 2, 8, 2) });
        return legend;
    }

    private static DateTimePicker DatePicker(string name) => new()
    {
        Width = 146, Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy", AccessibleName = name
    };

    private static FlowLayoutPanel SettingsBlock(string title, params Control[] controls)
    {
        var block = new BrandCard { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 10, 12, 18) };
        block.Controls.Add(Heading(title));
        block.Controls.AddRange(controls);
        return block;
    }

    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font(AppTheme.HeadingFont, 10, FontStyle.Bold), ForeColor = MatrixView.Ink
    };

    private static CheckedListBox ClassOptions(string name) => new()
    {
        Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true,
        BorderStyle = BorderStyle.FixedSingle, AccessibleName = name
    };

    private static Panel ClassListPanel(string heading, CheckedListBox list)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 6) };
        var label = Heading(heading);
        label.Dock = DockStyle.Top;
        label.Padding = new Padding(0, 3, 0, 4);
        panel.Controls.Add(list);
        panel.Controls.Add(label);
        return panel;
    }

    private static Button ActionButton(string text, bool primary = false) => new BrandButton(primary)
    {
        Text = text, AutoSize = true, MinimumSize = new Size(0, 34), Padding = new Padding(9, 0, 9, 0),
        Margin = new Padding(4, 3, 4, 3), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
        BackColor = Color.Transparent, ForeColor = primary ? Color.White : AppTheme.Accent
    };

    private static FlowLayoutPanel Field(string label, Control input)
    {
        var field = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 8, 0) };
        field.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
        input.Margin = Padding.Empty;
        field.Controls.Add(input);
        return field;
    }

    private sealed record SchoolYearChoice(int? StartYear, string Text)
    {
        public override string ToString() => Text;
    }
}
