using System.Security.Cryptography;
using System.Text;

namespace WebUntisAuswertung;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var installation = new Mutex(false, @"Local\WebUntisAuswertung-Installation");
        try
        {
            if (!installation.WaitOne(0))
            {
                MessageBox.Show($"{Application.ProductName} ist bereits geöffnet oder wird gerade installiert.", Application.ProductName);
                return;
            }
        }
        catch (AbandonedMutexException) { }
        try { RunApplication(); }
        finally { installation.ReleaseMutex(); }
    }

    private static void RunApplication()
    {
        var userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
        using var instance = new Mutex(true, @"Local\WebUntisAuswertung-" + userKey, out var first);
        if (!first)
        {
            MessageBox.Show($"{Application.ProductName} ist bereits geöffnet.", Application.ProductName);
            return;
        }

        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebUntis-Auswertung");
        Directory.CreateDirectory(dataFolder);
        foreach (var folder in Directory.EnumerateDirectories(dataFolder, "Abruf-*"))
        {
            var name = Path.GetFileName(folder);
            if (!Guid.TryParseExact(name.AsSpan("Abruf-".Length), "N", out _)) continue;
            try
            {
                var info = new DirectoryInfo(folder);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0 && info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    info.Delete(true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Application.Run(new MainForm(dataFolder));
    }
}
