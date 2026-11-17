using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class SettingsPage : ContentPage
{
    private const string LegendText = "•  Kein Fehlzeiteintrag\n|  Fehlzeit\nE  Entschuldigt\n+  Verspätet\n|+ / E+  Fehlzeit und Verspätung\n?  Ungeklärt\nB  Befreit";
    private const string Imprint = """
        Markus Voth · Entwicklung und Kontakt
        Berufskolleg für Technik Düren
        Nideggener Straße 43 · 52349 Düren
        markus.voth@bkt-dn.de
        """;

    private readonly Label updateStatus = MobileStyle.Label("", 13, Muted);
    private readonly Button check = MobileStyle.Button("Nach Updates suchen");
    private readonly Button install = MobileStyle.Button("Update installieren", primary: true);
    private readonly Button cancel = MobileStyle.Button("Download abbrechen");
    private CancellationTokenSource? operation;
    private AndroidRelease? available;
    private bool checkedOnce;
    private bool leaving;

    public SettingsPage(bool signedIn, bool hasClasses, Func<Task> connect, Func<Task> signOut, Func<Task> selectClasses,
        Func<Task> automaticLogin)
    {
        Title = "Einstellungen";
        BackgroundColor = Surface;
        SafeAreaEdges = SafeAreaEdges.All;

        var title = MobileStyle.Label(Title, 21, bold: true);
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
        title.VerticalOptions = LayoutOptions.Center;
        var close = MobileStyle.Button("Schließen");
        close.Clicked += async (_, _) => await LeaveAsync();
        var header = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        header.Add(title);
        header.Add(close, 1);

        var signIn = MobileStyle.Button(signedIn ? "Klassenliste aktualisieren" : "Bei WebUntis anmelden", primary: !signedIn);
        signIn.Clicked += async (_, _) => await LeaveAsync(connect);
        var remember = MobileStyle.Button("Automatische Anmeldung");
        remember.Clicked += async (_, _) => await LeaveAsync(automaticLogin);
        var disconnect = MobileStyle.Button("Abmelden");
        disconnect.IsVisible = signedIn || hasClasses;
        disconnect.Clicked += async (_, _) => await LeaveAsync(signOut);
        var accountStatus = MobileStyle.Label("Bei WebUntis angemeldet", 13, Muted);
        accountStatus.IsVisible = signedIn;

        var classes = MobileStyle.Button("Klassenauswahl");
        classes.Clicked += async (_, _) => await LeaveAsync(selectClasses);
        check.Clicked += async (_, _) => await CheckUpdatesAsync();
        install.Clicked += async (_, _) => await InstallUpdateAsync();
        cancel.Clicked += (_, _) => operation?.Cancel();
        var releases = MobileStyle.Button("Releases auf GitHub");
        releases.Clicked += async (_, _) => await Launcher.Default.OpenAsync(new Uri(AndroidUpdates.ReleasesUrl));
        var update = Section("App-Update", MobileStyle.Label($"Installierte Version {AppInfo.Current.VersionString}", 13, Muted),
            updateStatus, check, install, cancel, releases);
        var body = new VerticalStackLayout
        {
            Spacing = 10,
            Padding = new Thickness(0, 0, 0, 8),
            Children =
            {
                Section("Anmeldung", accountStatus, signIn, remember, disconnect),
                Card(classes),
                Section("Legende", MobileStyle.Label(LegendText)),
                update,
                Section("Impressum", MobileStyle.Label(Imprint, 13))
            }
        };
        var layout = new Grid { RowSpacing = 10, Padding = new Thickness(12, 8), RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
        layout.Add(header);
        layout.Add(new ScrollView { Content = body }, 0, 1);
        Content = layout;
        UpdateButtons();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (checkedOnce) return;
        checkedOnce = true;
        if (AndroidUpdates.Supported) await CheckUpdatesAsync();
        else updateStatus.Text = "Der Update-Kanal stellt APKs für ARM64 bereit.";
    }

    protected override void OnDisappearing()
    {
        operation?.Cancel();
        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed() => leaving || base.OnBackButtonPressed();

    private async Task LeaveAsync(Func<Task>? next = null)
    {
        if (leaving || Navigation.ModalStack.LastOrDefault() != this) return;
        leaving = true;
        try
        {
            await Navigation.PopModalAsync();
            if (next != null) await next();
        }
        finally { leaving = false; }
    }

    private Task CheckUpdatesAsync() => RunUpdateAsync(async token =>
    {
        available = null;
        UpdateButtons();
        updateStatus.Text = "GitHub wird nach Updates gefragt …";
        var latest = await AndroidUpdates.CheckAsync(token);
        token.ThrowIfCancellationRequested();
        if (latest == null)
        {
            updateStatus.Text = "Auf GitHub ist noch kein Android-Update verfügbar.";
            return;
        }
        if (latest.Version > AppInfo.Current.Version)
        {
            available = latest;
            updateStatus.Text = $"Version {latest.Label} ist verfügbar.";
        }
        else updateStatus.Text = "Die installierte Version ist aktuell.";
    });

    private Task InstallUpdateAsync() => available is not { } release ? Task.CompletedTask : RunUpdateAsync(async token =>
    {
        cancel.IsVisible = true;
        updateStatus.Text = $"Version {release.Label} wird heruntergeladen …";
        var progress = new Progress<int>(percent =>
        {
            if (!token.IsCancellationRequested && cancel.IsVisible) updateStatus.Text = $"APK wird geladen · {percent} %";
        });
        var path = await AndroidUpdates.DownloadAsync(release, FileSystem.CacheDirectory, progress, token);
        cancel.IsVisible = false;
        token.ThrowIfCancellationRequested();
        updateStatus.Text = "Bestätige das Update im Android-Installationsfenster.";
        AndroidUpdateInstaller.Open(path, release);
    }, downloading: true);

    private async Task RunUpdateAsync(Func<CancellationToken, Task> run, bool downloading = false)
    {
        if (operation != null || leaving) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        UpdateButtons();
        try { await run(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (downloading) updateStatus.Text = "Download abgebrochen.";
        }
        catch (Exception error) when (error is InvalidDataException or TimeoutException or InvalidOperationException)
        {
            updateStatus.Text = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Java.Lang.Exception)
        {
            updateStatus.Text = downloading ? "Das Update konnte nicht geladen oder geöffnet werden."
                : "Die Versionsprüfung konnte GitHub nicht erreichen.";
        }
        finally
        {
            cancel.IsVisible = false;
            operation = null;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        check.IsVisible = AndroidUpdates.Supported;
        check.IsEnabled = operation == null;
        install.IsVisible = available != null;
        install.IsEnabled = operation == null;
        install.Text = available is { } release ? $"Update auf {release.Label} installieren" : "Update installieren";
        cancel.IsVisible = cancel.IsVisible && operation != null;
    }

    private static Border Section(string name, params View[] views)
    {
        var title = MobileStyle.Label(name, 16, bold: true);
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level2);
        var content = new VerticalStackLayout { Spacing = 8, Children = { title } };
        foreach (var view in views) content.Children.Add(view);
        return Card(content);
    }
}
