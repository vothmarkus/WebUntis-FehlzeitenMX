using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung;

internal sealed class MatrixCache(string appFolder) : IDisposable
{
    private const int Schema = 1;
    private const int MaxFileSize = 32 * 1024 * 1024;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SaltSize = 32;
    private static readonly byte[] Magic = "FehlzeitenMX-Matrix-1"u8.ToArray();
    private static readonly byte[] KeyInfo = "WebUntis-FehlzeitenMX / local matrix cache / AES-256-GCM v1"u8.ToArray();
    private readonly string folder = Path.Combine(appFolder, "Datenschutzspeicher");
    private byte[]? key;
    private byte[]? credentialId;

    public bool IsOpen => key != null;
    private string KeyPath => Path.Combine(folder, "Hello.json");

    public async Task UnlockAsync(nint window, CancellationToken token)
    {
        if (!File.Exists(KeyPath)) throw InvalidKey();
        using var stream = File.OpenRead(KeyPath);
        if (stream.Length is <= 0 or > 8192) throw InvalidKey();
        var info = await JsonSerializer.DeserializeAsync<KeyInfoFile>(stream, cancellationToken: token);
        if (info is not { Version: Schema, Credential.Length: > 0 and <= HelloCacheKey.MaxCredentialIdSize, Salt.Length: SaltSize })
            throw InvalidKey();
        var secret = await HelloCacheKey.OpenAsync(window, info.Credential, info.Salt, token);
        try
        {
            token.ThrowIfCancellationRequested();
            key = DeriveKey(secret, info.Salt);
            credentialId = info.Credential;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task CreateAsync(nint window, CancellationToken token)
    {
        if (File.Exists(KeyPath))
        {
            await UnlockAsync(window, token);
            return;
        }
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var (credential, secret) = await HelloCacheKey.CreateAsync(window, salt, token);
        try
        {
            var info = JsonSerializer.SerializeToUtf8Bytes(new KeyInfoFile(Schema, credential, salt));
            var encryptionKey = DeriveKey(secret, salt);
            try
            {
                await ReplaceAsync(KeyPath, info, token);
                key = encryptionKey;
                credentialId = credential;
            }
            finally { if (key != encryptionKey) CryptographicOperations.ZeroMemory(encryptionKey); }
        }
        catch
        {
            await HelloCacheKey.DeleteAsync(credential);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<AttendanceMatrix?> ReadAsync(string name, ReportRange range, bool schoolYear, CancellationToken token)
    {
        if (key is not { } encryptionKey) return null;
        var path = FilePath(name, range, schoolYear);
        var packet = await ReadFileAsync(path, token);
        // An exact full-year request can also populate the initial home view.
        if (packet == null && schoolYear)
        {
            path = FilePath(name, range, schoolYear: false);
            packet = await ReadFileAsync(path, token);
        }
        if (packet == null) return null;
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var data = Unseal(packet, encryptionKey, Path.GetFileName(path));
            if (!StringComparer.OrdinalIgnoreCase.Equals(data.ClassName, name) || data.Range?.IsValid() != true ||
                data.Range.From != range.From || (schoolYear ? data.Range.To > range.To : data.Range.To != range.To) ||
                data.FetchedAt == default || string.IsNullOrWhiteSpace(data.ClassId) ||
                data.Students == null || data.Reports == null || data.AbsenceNotes == null || data.SchoolPeriods == null)
                throw new InvalidDataException("Der gespeicherte Datenstand passt nicht zur gewählten Klasse und zum Zeitraum.");
            return data.Build();
        }, token);
    }

    public async Task SaveAsync(MatrixData data, ReportRange? schoolYearRange, CancellationToken token)
    {
        if (key is not { } encryptionKey) return;
        var path = schoolYearRange is { } year ? FilePath(data.ClassName, year, schoolYear: true) : FilePath(data.ClassName, data.Range);
        var clear = await Task.Run(() => JsonSerializer.SerializeToUtf8Bytes(data), token);
        try
        {
            if (clear.Length > MaxFileSize - Magic.Length - NonceSize - TagSize)
                throw new IOException("Die Matrix ist zu groß für den Datenspeicher.");
            var packet = await Task.Run(() => Seal(clear, encryptionKey, Path.GetFileName(path)), token);
            await ReplaceAsync(path, packet, token);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void ForgetClass(string name)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder, ClassPrefix(name) + "-*.bin")) File.Delete(path);
    }

    public async Task ClearAsync()
    {
        var registered = credentialId;
        Dispose();
        if (Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.bin")) File.Delete(path);
            foreach (var path in Directory.EnumerateFiles(folder, "*.tmp")) File.Delete(path);
            File.Delete(KeyPath);
        }
        if (registered != null) await HelloCacheKey.DeleteAsync(registered);
    }

    public void Dispose()
    {
        if (key != null) CryptographicOperations.ZeroMemory(key);
        key = null;
        credentialId = null;
    }

    private static byte[] DeriveKey(byte[] secret, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, HelloCacheKey.KeySize, salt, KeyInfo);

    private static byte[] Seal(byte[] clear, byte[] encryptionKey, string filename)
    {
        var packet = new byte[Magic.Length + NonceSize + TagSize + clear.Length];
        Magic.CopyTo(packet, 0);
        var nonce = packet.AsSpan(Magic.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(encryptionKey, TagSize);
        aes.Encrypt(nonce, clear, packet.AsSpan(Magic.Length + NonceSize + TagSize),
            packet.AsSpan(Magic.Length + NonceSize, TagSize), Encoding.ASCII.GetBytes(filename));
        return packet;
    }

    private static MatrixData Unseal(byte[] packet, byte[] encryptionKey, string filename)
    {
        var start = Magic.Length + NonceSize + TagSize;
        if (packet.Length <= start || !packet.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Der gespeicherte Datenstand hat ein unbekanntes Format.");
        var clear = new byte[packet.Length - start];
        try
        {
            using var aes = new AesGcm(encryptionKey, TagSize);
            aes.Decrypt(packet.AsSpan(Magic.Length, NonceSize), packet.AsSpan(start),
                packet.AsSpan(Magic.Length + NonceSize, TagSize), clear, Encoding.ASCII.GetBytes(filename));
            return JsonSerializer.Deserialize<MatrixData>(clear) ?? throw new InvalidDataException("Der gespeicherte Datenstand ist leer.");
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxFileSize) throw new InvalidDataException("Die Größe des gespeicherten Datenstands ist ungültig.");
            var result = new byte[stream.Length];
            await stream.ReadExactlyAsync(result, token);
            return result;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static async Task ReplaceAsync(string path, byte[] content, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
                await stream.WriteAsync(content, token);
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private string FilePath(string name, ReportRange range, bool schoolYear = false) => Path.Combine(folder, ClassPrefix(name) + "-" +
        Hash(FormattableString.Invariant($"{range.From:yyyyMMdd}/{range.To:yyyyMMdd}/{schoolYear}")) + ".bin");

    private static string ClassPrefix(string name) => Hash(name.Trim().Normalize().ToUpperInvariant());
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static InvalidDataException InvalidKey() => new("Die Zuordnung zum Windows-Hello-Passkey fehlt oder ist ungültig. Du kannst den lokalen Datenspeicher in den Einstellungen neu einrichten.");
    private sealed record KeyInfoFile(int Version, byte[] Credential, byte[] Salt);
}
