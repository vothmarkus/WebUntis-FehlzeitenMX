using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using WebUntisAuswertung.Core;
using DocumentUri = Android.Net.Uri;

namespace WebUntisAuswertung.Mobile;

[Activity(Theme = "@style/Maui.MainTheme.NoActionBar", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int CreateRosterDocument = 1711;
    private TaskCompletionSource<DocumentUri?>? rosterDestination;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) SetRecentsScreenshotEnabled(false);
        else Window?.AddFlags(WindowManagerFlags.Secure);
    }

    internal async Task<bool> SaveRosterCsvAsync(string fileName, string csv)
    {
        if (rosterDestination != null || IsFinishing || IsDestroyed) return false;
        var completion = new TaskCompletionSource<DocumentUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rosterDestination = completion;
        try
        {
            using var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("text/csv");
            intent.PutExtra(Intent.ExtraTitle, fileName);
#pragma warning disable CS0618, CA1422 // The platform delivers the document URI through OnActivityResult.
            StartActivityForResult(intent, CreateRosterDocument);
#pragma warning restore CS0618, CA1422
            using var destination = await completion.Task;
            if (destination == null) return false;
            await using var output = ContentResolver?.OpenOutputStream(destination, "wt")
                ?? throw new IOException("Der Speicherort konnte nicht geöffnet werden.");
            await using var writer = new StreamWriter(output, RosterCsv.Encoding, leaveOpen: true);
            await writer.WriteAsync(csv);
            return true;
        }
        finally { rosterDestination = null; }
    }

#pragma warning disable CS0672, CS0618, CA1422
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == CreateRosterDocument)
            rosterDestination?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }
#pragma warning restore CS0672, CS0618, CA1422

    protected override void OnDestroy()
    {
        rosterDestination?.TrySetResult(null);
        base.OnDestroy();
    }
}
