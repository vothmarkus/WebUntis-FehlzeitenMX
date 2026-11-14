using System.ComponentModel;
using System.Diagnostics;
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
    private readonly Button cancel = ActionButton("Abbruch");
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
    private AppSettings settings;
    private ReportRange schoolYear;
    private CancellationTokenSource? running;
    private bool updatingSettings;
    private bool sessionReady;
    private bool closeAfterCancellation;
    private bool checkingUpdate;
    private bool checkedUpdateOnce;
    private UpdateRelease? availableUpdate;
    private string? openedMyClass;

    internal MainForm(string appFolder)
    {
        this.appFolder = appFolder;
        settings = AppSettings.Load(appFolder);
        browser = new UntisBrowser(this, appFolder, settings.RememberLogin);
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
        tabs.Visible = !settings.ProtectWithHello;
        var statusStrip = new StatusStrip { SizingGrip = false, BackColor = AppTheme.Surface, ForeColor = AppTheme.Muted,
            RenderMode = ToolStripRenderMode.System, Dock = DockStyle.Bottom };
        statusStrip.Items.AddRange([status, loadingCount, loading]);
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
        cancel.Enabled = false;
        cancel.Click += (_, _) => running?.Cancel();
        Shown += async (_, _) =>
        {
            PositionHeader();
            await StartAsync();
        };
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            PositionHeader();
            if (running == null) ShowPageStatus();
            if (tabs.SelectedTab == settingsPage && !checkedUpdateOnce) await CheckForUpdateAsync();
            if (tabs.SelectedTab == homePage && sessionReady && homeViews.Keys.Any(name => !loadedHome.Contains(name)))
                await RunAsync(LoadHomeAsync);
        };
        FormClosing += (_, args) =>
        {
            if (running == null) return;
            args.Cancel = true;
            closeAfterCancellation = true;
            running.Cancel();
        };
        FormClosed += (_, _) =>
        {
            homeAbsenceMenu?.Dispose();
            absencePreload?.Cancel();
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
        refresh.Click += async (_, _) => await RunAsync(RefreshMatrixAsync);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = MatrixView.Surface, Padding = new Padding(9, 5, 9, 5) };
        toolbar.Controls.AddRange([Field("Klasse", classes), Field("Von", from), Field("Bis", to), refresh, cancel]);
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
        showLegend.Checked = settings.ShowLegend;
        remember.CheckedChanged += async (_, _) => await ChangeRememberLoginAsync();
        hello.CheckedChanged += async (_, _) => await ChangeHelloAsync();
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
        cancelUpdate.Click += (_, _) => running?.Cancel();
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
        busyControls.AddRange([signIn, signOut, reload, remember, hello, showLegend, classFilter, homeOptions, myClassOptions, schoolYearPicker, csvDownload]);
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
        if (settings.ProtectWithHello)
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
            tabs.Visible = true;
        }
        await RunAsync(LoadSessionAsync);
    }

    private async Task LoadSessionAsync(CancellationToken token, IProgress<string> progress)
    {
        await LoadClassesAsync(token, progress);
        await LoadHomeAsync(token, progress);
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

    private async Task LoadHomeAsync(CancellationToken token, IProgress<string> progress)
    {
        var pendingClasses = settings.FavoriteClasses.Where(name => !loadedHome.Contains(name)).ToArray();
        if (pendingClasses.Length == 0) return;
        var elapsed = Stopwatch.StartNew();
        var failed = 0;
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
            view.SetStatus($"{name}   ·   Wird geladen …");
            var classProgress = new Progress<string>(message => progress.Report($"{name} · {message}"));
            try
            {
                view.SetMatrix(await GetSchoolYearMatrixAsync(name, range, classProgress, token,
                    (completed, _) =>
                    {
                        if (completed > 0 && completed < stepsPerMatrix)
                            ShowProgress(completedBefore + completed, total);
                    }));
                loadedHome.Add(name);
            }
            catch (OperationCanceledException)
            {
                view.SetStatus($"{name}   ·   Abruf abgebrochen");
                throw;
            }
            catch (Exception error)
            {
                view.SetStatus($"{name}   ·   {error.Message.Split('\n')[0].Trim()}");
                failed++;
            }
            ShowProgress(completedBefore + stepsPerMatrix, total);
        }
        SetPageStatus(homePage, failed == 0 ? $"{loadedHome.Count} Klassen auf der Startseite · Abruf {elapsed.Elapsed.TotalSeconds:0.0} s" :
            $"{loadedHome.Count} Klassen geladen · {failed} Abrufe fehlgeschlagen.");
    }

    private async Task OpenMyClassAsync(string name, CancellationToken token, IProgress<string> progress)
    {
        var range = schoolYear;
        SelectMyClass(name);
        myClassView.SetStatus($"{name}   ·   Wird geladen …");
        myClassDetails.ShowDetails(DayDetails.Empty);
        try
        {
            var result = await GetSchoolYearMatrixAsync(name, range, progress, token, ShowProgress);
            myClassView.SetMatrix(result);
            sessionReady = true;
            SetPageStatus(myClassesPage, $"{name} · Tagesmatrix geöffnet");
        }
        catch
        {
            ResetMyClass();
            throw;
        }
    }

    private async Task<AttendanceMatrix> GetSchoolYearMatrixAsync(string name, ReportRange range, IProgress<string> progress,
        CancellationToken token, Action<int, int>? reportProgress = null)
    {
        if (schoolYearMatrices.TryGetValue(name, out var cached)) return cached;
        var result = await ReadMatrixAsync(name, ThroughToday(range), progress, token, reportProgress);
        if (result.HasAbsenceReport && result.HasLatenessReport && result.HasTimetable) schoolYearMatrices[name] = result;
        return result;
    }

    private static ReportRange ThroughToday(ReportRange range)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return range.Contains(today) ? range with { To = today } : range;
    }

    private async Task RefreshMatrixAsync(CancellationToken token, IProgress<string> progress)
    {
        var name = classes.Text.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Wähle eine Klasse oder gib ihr Kürzel ein.");
        var range = CurrentDates();
        if (range.From > range.To) throw new InvalidOperationException("Das Startdatum liegt hinter dem Enddatum.");
        if (!range.IsValid()) throw new InvalidOperationException("Wähle höchstens ein Schuljahr pro Abruf.");

        var elapsed = Stopwatch.StartNew();
        var result = await ReadMatrixAsync(name, range, progress, token, ShowProgress);
        sessionReady = true;
        matrixDetails.ShowDetails(DayDetails.Empty);
        matrixView.SetMatrix(result);
        if (range == schoolYear)
        {
            if (result.HasAbsenceReport && result.HasLatenessReport && result.HasTimetable) schoolYearMatrices[name] = result;
            else schoolYearMatrices.Remove(name);
            if (homeViews.TryGetValue(name, out var home))
            {
                home.SetMatrix(result);
                loadedHome.Add(name);
                homeDetails.ShowDetails(DayDetails.Empty);
            }
            if (ClassComparer.Equals(openedMyClass, name))
            {
                myClassView.SetMatrix(result);
                myClassDetails.ShowDetails(DayDetails.Empty);
            }
        }
        tabs.SelectedTab = matrixPage;
        SetPageStatus(matrixPage, $"{name} · Aktualisiert um {DateTime.Now:HH:mm} Uhr · Abruf {elapsed.Elapsed.TotalSeconds:0.0} s");
    }

    private async Task<AttendanceMatrix> ReadMatrixAsync(string name, ReportRange range, IProgress<string> progress,
        CancellationToken token, Action<int, int>? reportProgress = null, bool afterExcuse = false)
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
            return await ParseMatrixAsync(name, range, batch, preparedNotes, progress, token, reportProgress, afterExcuse);
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
        Action<int, int>? reportProgress, bool afterExcuse)
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
        var matrix = await Task.Run(() => new AttendanceMatrix(name, range.From, range.To, batch.Students,
            reports.Append(new Report(ReportKind.Timetable, [], [], batch.Notes.ToArray(), Complete: false)),
            absenceNotes: absenceNotes, schoolPeriods: schoolPeriods, classId: batch.ClassId), token);
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
        if (running != null || !IsCurrent()) return;
        homeAbsenceMenu?.Close();
        await Task.Yield();
        if (running != null || !IsCurrent()) return;
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
            absencePreload?.Cancel();
            var saved = await browser.ShowExcuseAsync(matrix, student, day, row, progress, token);
            if (saved) await ReloadEditedClassAsync(source, matrix, token, progress);
        }
    }

    private async Task ReloadEditedClassAsync(MatrixView source, AttendanceMatrix previous,
        CancellationToken token, IProgress<string> progress)
    {
        var name = previous.ClassName;
        var range = new ReportRange(previous.From, previous.To);
        var regularMatrix = matrixView.CurrentMatrix;
        loadedHome.Remove(name);
        schoolYearMatrices.Remove(name);
        progress.Report($"{name} · Fehlzeiten werden neu gelesen …");
        var updated = await ReadMatrixAsync(name, range, progress, token, ShowProgress, afterExcuse: true);
        if (!updated.HasAbsenceReport)
            throw new InvalidOperationException("Die aktuellen Fehlzeiten konnten nach dem Speichern nicht gelesen werden. Rufe die Klasse erneut ab.");
        if (updated.HasAbsenceReport && updated.HasLatenessReport && updated.HasTimetable)
            schoolYearMatrices[name] = updated;
        loadedHome.Add(name);
        if (ClassComparer.Equals(openedMyClass, name))
        {
            myClassDetails.ShowDetails(myClassView.SetMatrix(updated, keepPosition: true));
        }
        if (regularMatrix?.ClassId == previous.ClassId && SchoolYears.StartYear(regularMatrix.From) == SchoolYears.StartYear(previous.From))
        {
            if (regularMatrix.From == updated.From && regularMatrix.To == updated.To)
            {
                matrixDetails.ShowDetails(matrixView.SetMatrix(updated, keepPosition: true));
            }
            else ResetMatrix();
        }
        homeDetails.ShowDetails(source.SetMatrix(updated, keepPosition: true));
        SetPageStatus(homePage, $"{name} · Aktuelle Fehlzeiten aus WebUntis geladen");
    }

    private void PreloadHomeAbsences()
    {
        if (!sessionReady || closeAfterCancellation || IsDisposed) return;
        absencePreload?.Cancel();
        var matrices = homeViews.Values.Select(view => view.CurrentMatrix).OfType<AttendanceMatrix>()
            .Where(matrix => !matrix.IsDemo && matrix.ClassId.Length > 0).ToArray();
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
            button.Click += async (_, _) => await RunAsync((token, progress) => OpenMyClassAsync(name, token, progress));
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
        await browser.SignOutAsync(token);
        SetPageStatus(settingsPage, "In der App von WebUntis abgemeldet.");
    }

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
            PreloadHomeAbsences();
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

    private async Task RunAsync(Func<CancellationToken, IProgress<string>, Task> action, bool keepAbsencePreload = false)
    {
        if (running != null || IsDisposed) return;
        var initiatingPage = tabs.SelectedTab;
        using var cancellation = new CancellationTokenSource();
        running = cancellation;
        if (!keepAbsencePreload) absencePreload?.Cancel();
        busyControls.ForEach(control => control.Enabled = false);
        RefreshUpdateButtons();
        cancel.Enabled = true;
        status.Text = "Bitte warten …";
        ShowIndeterminateProgress();
        var progress = new Progress<string>(message =>
        {
            if (running == cancellation && !IsDisposed) status.Text = message;
        });
        try { await action(cancellation.Token, progress); }
        catch (OperationCanceledException)
        {
            if (initiatingPage != null) SetPageStatus(initiatingPage, "Vorgang abgebrochen.");
        }
        catch (Exception error)
        {
            status.Text = "Vorgang nicht abgeschlossen.";
            if (initiatingPage != null) SetPageStatus(initiatingPage, status.Text);
            HideProgress();
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            running = null;
            HideProgress();
            cancel.Enabled = false;
            busyControls.ForEach(control => control.Enabled = true);
            RefreshUpdateButtons();
            UpdateRememberedLoginStatus();
            ShowPageStatus();
            if (closeAfterCancellation) Close();
            else if (!keepAbsencePreload || absencePreload == null || absencePreload.IsCancellationRequested) PreloadHomeAbsences();
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
    }

    private void HideProgress()
    {
        loading.Visible = false;
        loadingCount.Visible = false;
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
