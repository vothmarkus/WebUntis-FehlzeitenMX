using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class AutomaticLoginPage : ContentPage
{
    private readonly Entry username = new()
    {
        Placeholder = "WebUntis-Benutzername",
        MaxLength = 512,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        TextColor = Ink
    };
    private readonly Entry password = new()
    {
        Placeholder = "WebUntis-Passwort",
        MaxLength = 4096,
        IsPassword = true,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        TextColor = Ink
    };
    private readonly Button save = MobileStyle.Button("Speichern", primary: true);
    private readonly Button forget = MobileStyle.Button("Zugangsdaten löschen");
    private readonly Button close = MobileStyle.Button("Schließen");
    private readonly Label status = MobileStyle.Label("", 13, Muted);
    private readonly Func<WebUntisLogin?, Task<bool>> store;
    private readonly Func<Task> reconnect;
    private bool working;

    public AutomaticLoginPage(WebUntisLogin? saved, Func<WebUntisLogin?, Task<bool>> store, Func<Task> reconnect)
    {
        this.store = store;
        this.reconnect = reconnect;
        Title = "Automatische Anmeldung";
        BackgroundColor = Surface;
        SafeAreaEdges = SafeAreaEdges.All;
        username.Text = saved?.Username;
        forget.IsVisible = saved != null;
        status.Text = saved switch
        {
            { CanRetry: false } => "Die letzte automatische Anmeldung wurde bisher nicht bestätigt. Prüfe bei Bedarf deine Zugangsdaten.",
            not null => "Automatische Anmeldung ist eingerichtet.",
            _ => ""
        };
        status.IsVisible = status.Text.Length > 0;
        SemanticProperties.SetDescription(username, "WebUntis-Benutzername");
        SemanticProperties.SetDescription(password, "WebUntis-Passwort");
        var title = MobileStyle.Label(Title, 21, bold: true);
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
        var header = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        header.Add(title);
        header.Add(close, 1);
        var fields = MobileStyle.Card(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                MobileStyle.Label("Hinterlege deine WebUntis-Zugangsdaten, damit die App eine abgelaufene Sitzung erneuern kann. Sie werden verschlüsselt auf diesem Gerät gespeichert.", 14),
                username, password, status, save, forget
            }
        });
        var layout = new Grid
        {
            Padding = new Thickness(12, 8),
            RowSpacing = 12,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) }
        };
        layout.Add(header);
        layout.Add(new ScrollView { Content = fields }, 0, 1);
        Content = layout;
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();
        save.Clicked += async (_, _) =>
        {
            var login = new WebUntisLogin(username.Text?.Trim() ?? "", password.Text ?? "");
            if (login.IsValid) await ChangeAsync(login);
            else ShowStatus("Gib deinen WebUntis-Benutzernamen und dein Passwort ein.");
        };
        forget.Clicked += async (_, _) => await ChangeAsync(null);
    }

    protected override bool OnBackButtonPressed() => working || base.OnBackButtonPressed();

    protected override void OnDisappearing()
    {
        password.Text = "";
        base.OnDisappearing();
    }

    private async Task ChangeAsync(WebUntisLogin? login)
    {
        if (working || Navigation.ModalStack.LastOrDefault() != this) return;
        working = true;
        save.IsEnabled = forget.IsEnabled = close.IsEnabled = false;
        try
        {
            if (!await store(login))
            {
                ShowStatus("Die Einstellung konnte nicht auf dem Gerät gespeichert werden.");
                return;
            }
            password.Text = "";
            if (Navigation.ModalStack.LastOrDefault() != this) return;
            await Navigation.PopModalAsync();
            if (login != null) await reconnect();
        }
        finally
        {
            working = false;
            save.IsEnabled = forget.IsEnabled = close.IsEnabled = true;
        }
    }

    private void ShowStatus(string message)
    {
        status.Text = message;
        status.IsVisible = true;
    }
}
