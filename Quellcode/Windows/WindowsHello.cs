using System.Runtime.InteropServices;
using Windows.Security.Credentials.UI;

namespace WebUntisAuswertung;

internal static class WindowsHello
{
    public static async Task<bool> VerifyAsync(nint window, string prompt)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            throw new InvalidOperationException("Die Entsperrung mit Windows Hello ist ab Windows 11 verfügbar.");
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability != UserConsentVerifierAvailability.Available)
                throw new InvalidOperationException("Windows Hello ist für dieses Windows-Konto gerade nicht verfügbar.");
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(window, prompt);
            return result == UserConsentVerificationResult.Verified;
        }
        catch (COMException error)
        {
            throw new InvalidOperationException("Die Windows-Hello-Abfrage konnte nicht gestartet werden.", error);
        }
    }
}
