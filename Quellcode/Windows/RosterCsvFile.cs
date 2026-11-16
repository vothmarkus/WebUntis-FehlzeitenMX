using System.Runtime.InteropServices;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung;

internal static class RosterCsvFile
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string DownloadsFolder()
    {
        var id = DownloadsFolderId;
        var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer);
        try
        {
            if (result >= 0 && Marshal.PtrToStringUni(pointer) is { Length: > 0 } path) return path;
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static async Task SaveAsync(string path, string csv, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".Schuelernamen-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, csv, RosterCsv.Encoding, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
