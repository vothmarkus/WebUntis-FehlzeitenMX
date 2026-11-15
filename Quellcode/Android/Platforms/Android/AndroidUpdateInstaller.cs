using Android.Content;
using Android.Content.PM;
using Android.Runtime;
using FileProvider = AndroidX.Core.Content.FileProvider;

namespace WebUntisAuswertung.Mobile;

internal static class AndroidUpdateInstaller
{
    public static void Open(string path, AndroidRelease release)
    {
        if (Platform.CurrentActivity is not { IsFinishing: false } activity ||
            activity.PackageManager is not { } packages || activity.PackageName is not { } packageName)
            throw new InvalidOperationException("Die Android-Installation konnte nicht geöffnet werden.");

        var hasSigningInfo = OperatingSystem.IsAndroidVersionAtLeast(28);
#pragma warning disable CS0618, CA1422 // Older Android versions still use the original signature and version APIs.
        var flags = hasSigningInfo ? PackageInfoFlags.SigningCertificates : PackageInfoFlags.Signatures;
        using var installed = packages.GetPackageInfo(packageName, flags);
        using var update = packages.GetPackageArchiveInfo(path, flags);
#pragma warning restore CS0618, CA1422
        if (installed == null || update == null || update.PackageName != packageName || update.VersionName != release.Label ||
            VersionCode(update) <= VersionCode(installed) || !SameSigners(Signers(installed), Signers(update)))
            throw new InvalidDataException("Android konnte das Paket nicht als passendes, signiertes Update bestätigen.");

        using var file = new Java.IO.File(path);
        using var uri = FileProvider.GetUriForFile(activity, packageName + ".updates", file);
        using var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        try { activity.StartActivity(intent); }
        catch (ActivityNotFoundException error)
        {
            throw new InvalidOperationException("Auf diesem Gerät konnte die Android-Installation nicht geöffnet werden.", error);
        }
    }

#pragma warning disable CS0618, CA1422 // Fallback for Android 6 and 7.
    private static long VersionCode(PackageInfo info) =>
        OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;

    private static IList<Signature> Signers(PackageInfo info) =>
        (OperatingSystem.IsAndroidVersionAtLeast(28) ? info.SigningInfo?.GetApkContentsSigners() : info.Signatures) ?? [];
#pragma warning restore CS0618, CA1422

    private static bool SameSigners(IList<Signature> installed, IList<Signature> update) =>
        installed.Count > 0 && installed.Count == update.Count &&
        installed.Select(SignerId).ToHashSet(StringComparer.Ordinal).SetEquals(update.Select(SignerId));

    private static string SignerId(Signature signature) => Convert.ToBase64String(signature.ToByteArray()!);
}

[Register("de/markusvoth/webuntisfehlzeitenmx/UpdateFileProvider")]
public sealed class UpdateFileProvider : FileProvider { }
