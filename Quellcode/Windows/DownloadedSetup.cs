using System.Runtime.InteropServices;

namespace WebUntisAuswertung;

internal static class DownloadedSetup
{
    private static readonly Guid AttachmentServices = new("4125dd96-e03a-4103-8f70-e0597d803b9c");

    public static void RegisterInternetDownload(string path, Uri source)
    {
        var type = Type.GetTypeFromCLSID(AttachmentServices, throwOnError: true)!;
        var attachment = (IAttachmentExecute)Activator.CreateInstance(type)!;
        try
        {
            attachment.SetLocalPath(path);
            attachment.SetSource(source.AbsoluteUri);
            attachment.Save();
        }
        catch (COMException error)
        {
            throw new IOException("Windows konnte den Internetdownload nicht zum Öffnen freigeben.", error);
        }
        finally { Marshal.ReleaseComObject(attachment); }
    }

    [ComImport]
    [Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttachmentExecute
    {
        void SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetClientGuid(in Guid guid);
        void SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
        void SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
        [PreserveSig] int CheckPolicy();
        void Prompt(nint parent, uint prompt, out uint action);
        void Save();
    }
}
