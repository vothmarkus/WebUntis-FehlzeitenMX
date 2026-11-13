using System.Diagnostics;
using System.Security.Cryptography;

namespace WebUntisAuswertung;

internal sealed class UpdateRelease
{
    public const long MaxSetupBytes = 512L * 1024 * 1024;
    public const string Product = "WebUntis-FehlzeitenMX";

    private readonly byte[] sha256;

    public UpdateRelease(Version version, long size, byte[] sha256, Uri url)
    {
        Version = version;
        Size = size;
        this.sha256 = sha256;
        Url = url;
    }

    public Version Version { get; }
    public long Size { get; }
    public Uri Url { get; }
    public string Label => VersionLabel(Version);
    public string SetupFileName => FileNameFor(Version);

    public static string FileNameFor(Version version) => $"{Product}-{VersionLabel(version)}-Setup-x64.exe";

    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public static string VersionLabel(Version version) =>
        version.Revision > 0 ? version.ToString(4) : version.Build > 0 ? version.ToString(3) : version.ToString(2);

    public async Task VerifySetupHashAsync(FileStream file, CancellationToken token)
    {
        if (file.Length != Size ||
            !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, token).ConfigureAwait(false), sha256))
            throw new InvalidDataException($"Die Prüfsumme des Setups für Version {Label} stimmt nicht mit GitHub überein.");
    }

    public void VerifySetupVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName != Product || info.ProductMajorPart != Version.Major || info.ProductMinorPart != Version.Minor ||
            info.ProductBuildPart != Version.Build || info.ProductPrivatePart != Version.Revision)
            throw new InvalidDataException($"Das Setup konnte nicht als {Product} {Label} bestätigt werden.");
    }
}
