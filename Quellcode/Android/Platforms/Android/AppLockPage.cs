using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
using static WebUntisAuswertung.Mobile.MobileStyle;

namespace WebUntisAuswertung.Mobile;

internal sealed class AppLockPage : ContentPage
{
    private const string Locked = "Fehlzeiten MX ist gesperrt.";
    private readonly Button unlock = MobileStyle.Button("Entsperren", primary: true);
    private readonly Label message = MobileStyle.Label(Locked, 14, Muted);

    public AppLockPage()
    {
        Title = "Fehlzeiten MX";
        BackgroundColor = Surface;
        SafeAreaEdges = SafeAreaEdges.All;
        unlock.Clicked += (_, _) => UnlockRequested?.Invoke();
        var card = Card(new VerticalStackLayout
        {
            Spacing = 14,
            Children = { MobileStyle.Label("WebUntis-FehlzeitenMX", 19, bold: true), message, unlock }
        }, new Thickness(20));
        card.MaximumWidthRequest = 420;
        card.VerticalOptions = LayoutOptions.Center;
        Content = new Grid { Padding = 20, Children = { card } };
    }

    public event Action? UnlockRequested;

    public async Task<bool> AuthenticateAsync()
    {
        if (Platform.CurrentActivity is not FragmentActivity activity || activity.IsFinishing) return false;
        var allowed = BiometricManager.Authenticators.BiometricWeak | BiometricManager.Authenticators.DeviceCredential;
        using var manager = BiometricManager.From(activity);
        if (manager == null || manager.CanAuthenticate(allowed) != BiometricManager.BiometricSuccess)
        {
            message.Text = "Richte in den Android-Einstellungen eine Displaysperre ein, um die App zu entsperren.";
            return false;
        }

        unlock.IsEnabled = false;
        message.Text = Locked;
        try
        {
            using var callback = new UnlockCallback();
            using var executor = ContextCompat.GetMainExecutor(activity);
            using var prompt = new BiometricPrompt(activity, executor!, callback);
            using var builder = new BiometricPrompt.PromptInfo.Builder();
            using var info = builder.SetTitle("Fehlzeiten MX entsperren")
                .SetSubtitle("Mit Biometrie oder deiner Displaysperre")
                .SetAllowedAuthenticators(allowed).Build();
            prompt.Authenticate(info);
            return await callback.Result.Task;
        }
        catch (Java.Lang.IllegalStateException) { return false; }
        finally { unlock.IsEnabled = true; }
    }

    private sealed class UnlockCallback : BiometricPrompt.AuthenticationCallback
    {
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result) => Result.TrySetResult(true);
        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString) => Result.TrySetResult(false);
    }
}
