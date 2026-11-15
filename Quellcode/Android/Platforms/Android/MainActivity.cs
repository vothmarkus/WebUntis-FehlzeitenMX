using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace WebUntisAuswertung.Mobile;

[Activity(Theme = "@style/Maui.MainTheme.NoActionBar", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) SetRecentsScreenshotEnabled(false);
        else Window?.AddFlags(WindowManagerFlags.Secure);
    }
}
