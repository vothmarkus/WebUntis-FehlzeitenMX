using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using WebUntisAuswertung.Core;
using CipherMode = Javax.Crypto.CipherMode;

namespace WebUntisAuswertung.Mobile;

internal sealed record CachedClasses(Student[] Classes, ReportRange SchoolYear);

internal sealed class AndroidMatrixCache
{
    private const string Provider = "AndroidKeyStore";
    private const string KeyAlias = "fehlzeitenmx-matrix-v1";
    private const string Transformation = "AES/GCM/NoPadding";
    private const string ClassesFile = "classes.bin";
    private const string SessionFile = "session.bin";
    private const string LoginFile = "login.bin";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaxFileSize = 32 * 1024 * 1024;
    private static readonly byte[] Magic = "FehlzeitenMX-Android-1"u8.ToArray();
    private static readonly object Gate = new();

    private static string Folder => Path.Combine(global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
        ?? throw new IOException("Der private App-Speicher ist nicht verfügbar."), "Matrices");

    public Task<CachedClasses?> ReadClassesAsync(CancellationToken token = default) => Run(() =>
    {
        var cached = Read<CachedClasses>(ClassesFile);
        return cached is { Classes.Length: > 0, SchoolYear: { } year } && year.IsValid() &&
            cached.Classes.All(item => item != null && !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name)) &&
            cached.Classes.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == cached.Classes.Length
            ? cached : null;
    }, token);

    public Task<AttendanceMatrix?> ReadMatrixAsync(Student choice, ReportRange annual, CancellationToken token = default) => Run(() =>
    {
        var data = Read<MatrixData>(MatrixFile(choice.Id));
        if (data == null || data.ClassId != choice.Id || data.Range?.IsValid() != true || data.Range.From != annual.From ||
            data.Range.To > annual.To || data.FetchedAt == default || data.Students == null || data.Reports == null ||
            data.AbsenceNotes == null || data.SchoolPeriods == null) return null;
        var matrix = (data with { ClassName = choice.Name }).Build();
        return matrix.HasCompleteReports ? matrix : null;
    }, token);

    public Task SaveClassesAsync(CachedClasses data, CancellationToken token) =>
        Run(() => Write(ClassesFile, data, token), token);

    public Task<SavedWebUntisCookie[]?> ReadSessionAsync(CancellationToken token) =>
        Run(() => Read<SavedWebUntisCookie[]>(SessionFile), token);

    public Task SaveSessionAsync(SavedWebUntisCookie[] cookies, CancellationToken token) =>
        Run(() => Write(SessionFile, cookies, token), token);

    public Task<WebUntisLogin?> ReadLoginAsync(CancellationToken token) =>
        Run(() => Read<WebUntisLogin>(LoginFile) is { IsValid: true } saved ? saved : null, token);

    public Task SaveLoginAsync(WebUntisLogin? login, CancellationToken token) => Run(() =>
    {
        if (login == null) File.Delete(Path.Combine(Folder, LoginFile));
        else if (login.IsValid) Write(LoginFile, login, token);
        else throw new InvalidDataException("Die WebUntis-Zugangsdaten sind unvollständig.");
    }, token);

    public Task<bool> SetLoginAttemptAsync(WebUntisLogin expected, DateTimeOffset? at, CancellationToken token) => Run(() =>
    {
        if (Read<WebUntisLogin>(LoginFile) != expected) return false;
        Write(LoginFile, expected with { LastAttemptAt = at }, token);
        return true;
    }, token);

    public Task SaveMatrixAsync(MatrixData data, CancellationToken token) =>
        Run(() => Write(MatrixFile(data.ClassId), data, token), token);

    public Task PruneAsync(IEnumerable<string> classIds, CancellationToken token = default)
    {
        var keep = classIds.Select(MatrixFile).ToHashSet(StringComparer.Ordinal);
        return Run(() =>
        {
            var folder = Folder;
            if (!Directory.Exists(folder)) return;
            foreach (var path in Directory.EnumerateFiles(folder, "matrix-*.bin"))
                if (!keep.Contains(Path.GetFileName(path))) File.Delete(path);
            foreach (var path in Directory.EnumerateFiles(folder, "*.tmp")) File.Delete(path);
        }, token);
    }

    public Task ClearAsync() => Run(() =>
    {
        try
        {
            using var store = OpenKeyStore();
            store.DeleteEntry(KeyAlias);
        }
        finally
        {
            var folder = Folder;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }, CancellationToken.None);

    public static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidDataException or JsonException or Java.Lang.Exception;

    private Task<T> Run<T>(Func<T> action, CancellationToken token) => Task.Run(() =>
    {
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            return action();
        }
    }, token);

    private Task Run(Action action, CancellationToken token) => Run(() =>
    {
        action();
        return true;
    }, token);

    private static T? Read<T>(string filename) where T : class
    {
        byte[] packet;
        var prefix = Magic.Length + NonceSize;
        try
        {
            using var stream = File.OpenRead(Path.Combine(Folder, filename));
            if (stream.Length <= prefix + TagSize || stream.Length > MaxFileSize)
                throw new InvalidDataException("Der verschlüsselte Datenstand hat eine ungültige Größe.");
            packet = new byte[stream.Length];
            stream.ReadExactly(packet);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        if (!packet.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Der verschlüsselte Datenstand hat ein unbekanntes Format.");

        using var store = OpenKeyStore();
        using var key = store.GetKey(KeyAlias, null);
        if (key == null) return null;
        using var cipher = OpenCipher();
        using var parameters = new GCMParameterSpec(TagSize * 8, packet.AsSpan(Magic.Length, NonceSize).ToArray());
        cipher.Init(CipherMode.DecryptMode, key, parameters);
        AuthenticateFile(cipher, filename);
        var clear = cipher.DoFinal(packet, prefix, packet.Length - prefix)
            ?? throw new InvalidDataException("Der verschlüsselte Datenstand ist leer.");
        try { return JsonSerializer.Deserialize<T>(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static void Write<T>(string filename, T data, CancellationToken token)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(data);
        try
        {
            if (clear.Length > MaxFileSize - Magic.Length - NonceSize - TagSize)
                throw new IOException("Der Datenstand ist zu groß für den lokalen Speicher.");
            using var store = OpenKeyStore();
            using var key = store.GetKey(KeyAlias, null) ?? GenerateKey();
            using var cipher = OpenCipher();
            cipher.Init(CipherMode.EncryptMode, key);
            AuthenticateFile(cipher, filename);
            var nonce = cipher.GetIV();
            var encrypted = cipher.DoFinal(clear);
            if (nonce?.Length != NonceSize || encrypted == null)
                throw new IOException("Der Datenstand konnte nicht verschlüsselt werden.");

            var folder = Folder;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, filename);
            var temporary = path + ".tmp";
            try
            {
                token.ThrowIfCancellationRequested();
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Magic);
                    stream.Write(nonce);
                    stream.Write(encrypted);
                    stream.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static KeyStore OpenKeyStore()
    {
        var store = KeyStore.GetInstance(Provider) ?? throw new IOException("Android Keystore ist nicht verfügbar.");
        try
        {
            store.Load(null, null);
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    private static IKey GenerateKey()
    {
        using var generator = KeyGenerator.GetInstance("AES", Provider)
            ?? throw new IOException("Android Keystore kann keinen AES-Schlüssel erzeugen.");
        using var builder = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt);
        using var settings = builder.SetKeySize(256).SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone).SetRandomizedEncryptionRequired(true).Build();
        generator.Init(settings);
        return generator.GenerateKey() ?? throw new IOException("Android Keystore hat keinen AES-Schlüssel erzeugt.");
    }

    private static Cipher OpenCipher() =>
        Cipher.GetInstance(Transformation) ?? throw new IOException("AES-GCM ist auf diesem Gerät nicht verfügbar.");

    private static void AuthenticateFile(Cipher cipher, string name)
    {
        cipher.UpdateAAD(Magic);
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(name));
    }

    private static string MatrixFile(string id) => "matrix-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".bin";
}
