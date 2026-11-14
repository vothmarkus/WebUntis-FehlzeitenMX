using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]

namespace WebUntisAuswertung;

// WebAuthn PRF supplies a stable secret from a user-verified, built-in Windows Hello credential.
// This relying party is used only by the native app. No WebUntis credentials or web requests are involved.
internal static partial class HelloCacheKey
{
    private const string RelyingParty = "webuntis-fehlzeitenmx.vothmarkus.github.io";
    private const string PublicKey = "public-key";
    private const int Platform = 1;
    private const int RequireUserVerification = 1;
    private const int InternalTransport = 0x10;
    private const int NoAttestation = 1;
    private const int P256 = -7;
    private const int TimeoutMilliseconds = 120000;
    internal const int KeySize = 32;
    internal const int MaxCredentialIdSize = 1024;

    public static Task<(byte[] Credential, byte[] Key)> CreateAsync(nint window, byte[] salt, CancellationToken token) =>
        Task.Run(() => Create(window, salt, token), token);

    public static Task<byte[]> OpenAsync(nint window, byte[] credential, byte[] salt, CancellationToken token) =>
        Task.Run(() => Open(window, credential, salt, token), token);

    public static Task DeleteAsync(byte[] credential) => Task.Run(() => Delete(credential));

    private static unsafe (byte[] Credential, byte[] Key) Create(nint window, byte[] salt, CancellationToken token)
    {
        CheckAvailability();
        Check(WebAuthNGetCancellationId(out var cancelId));
        using var cancellation = token.Register(() => WebAuthNCancelCurrentOperation(in cancelId));
        var clientBytes = ClientDataBytes("webauthn.create");
        var userId = AccountId();
        var operationId = cancelId;
        byte[]? credentialId = null;
        byte[]? key = null;
        try
        {
            fixed (byte* client = clientBytes, user = userId, input = salt)
            fixed (char* id = RelyingParty, name = UpdateRelease.Product, label = "Lokaler Datenspeicher",
                keyType = PublicKey, sha256 = "SHA-256")
            {
                var rp = new RpEntity { Version = 1, Id = id, Name = name };
                var account = new UserEntity { Version = 1, IdSize = userId.Length, Id = user, Name = label, DisplayName = label };
                var algorithm = new CoseParameter { Version = 1, Type = keyType, Algorithm = P256 };
                var parameters = new NativeList { Count = 1, Items = &algorithm };
                var data = new ClientData { Version = 1, Size = clientBytes.Length, Json = client, HashAlgorithm = sha256 };
                var prf = new PrfValues { FirstSize = salt.Length, First = input };
                var options = new MakeOptions
                {
                    Version = 8, Timeout = TimeoutMilliseconds, Attachment = Platform, Verification = RequireUserVerification,
                    Attestation = NoAttestation, ResidentKey = 1, CancellationId = &operationId, EnablePrf = 1, Prf = &prf
                };
                token.ThrowIfCancellationRequested();
                var result = WebAuthNAuthenticatorMakeCredential(window, in rp, in account, in parameters, in data, in options,
                    out var attestation);
                try
                {
                    Check(result);
                    if (attestation == null) throw new CryptographicException("Windows Hello hat keinen Passkey geliefert.");
                    credentialId = ReadCredential(attestation->CredentialId, attestation->CredentialSize);
                    if (attestation->Version < 7 || attestation->PrfEnabled == 0 || attestation->Transport != InternalTransport ||
                        !VerifiedLocally(attestation->AuthenticatorData, attestation->AuthenticatorDataSize))
                        throw Unsupported();
                    if (attestation->Prf != null) key = ReadSecret(attestation->Prf);
                }
                finally { if (attestation != null) WebAuthNFreeCredentialAttestation(attestation); }
            }
            token.ThrowIfCancellationRequested();
            key ??= Open(window, credentialId, salt, token);
            return (credentialId, key);
        }
        catch
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (credentialId != null) Delete(credentialId);
            throw;
        }
    }

    private static unsafe byte[] Open(nint window, byte[] credential, byte[] salt, CancellationToken token)
    {
        CheckAvailability();
        Check(WebAuthNGetCancellationId(out var cancelId));
        using var cancellation = token.Register(() => WebAuthNCancelCurrentOperation(in cancelId));
        var clientBytes = ClientDataBytes("webauthn.get");
        var operationId = cancelId;
        fixed (byte* client = clientBytes, allowed = credential, input = salt)
        fixed (char* id = RelyingParty, keyType = PublicKey, sha256 = "SHA-256")
        {
            var data = new ClientData { Version = 1, Size = clientBytes.Length, Json = client, HashAlgorithm = sha256 };
            var item = new NativeCredential { Version = 1, IdSize = credential.Length, Id = allowed, Type = keyType, Transports = InternalTransport };
            var itemPointer = &item;
            var allowList = new NativeList { Count = 1, Items = &itemPointer };
            var prf = new PrfValues { FirstSize = salt.Length, First = input };
            var prfInputs = new PrfInputs { Global = &prf };
            var options = new GetOptions
            {
                Version = 6, Timeout = TimeoutMilliseconds, Attachment = Platform, Verification = RequireUserVerification,
                CancellationId = &operationId, AllowList = &allowList, Prf = &prfInputs
            };
            token.ThrowIfCancellationRequested();
            var result = WebAuthNAuthenticatorGetAssertion(window, id, in data, in options, out var assertion);
            try
            {
                Check(result);
                token.ThrowIfCancellationRequested();
                if (assertion == null || assertion->Version < 3 || !VerifiedLocally(assertion->AuthenticatorData, assertion->AuthenticatorDataSize) ||
                    !credential.AsSpan().SequenceEqual(ReadCredential(assertion->Credential.Id, assertion->Credential.IdSize)))
                    throw new CryptographicException("Windows Hello hat den zugeordneten Passkey nicht bestätigt.");
                return ReadSecret(assertion->Prf);
            }
            finally { if (assertion != null) WebAuthNFreeAssertion(assertion); }
        }
    }

    private static void CheckAvailability()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || WebAuthNGetApiVersionNumber() < 8) throw Unsupported();
        Check(WebAuthNIsUserVerifyingPlatformAuthenticatorAvailable(out var available));
        if (available == 0) throw new InvalidOperationException("Richte zuerst Windows Hello für dieses Windows-Konto ein.");
    }

    private static byte[] AccountId()
    {
        if (!OperatingSystem.IsWindows()) throw Unsupported();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Das aktuelle Windows-Konto konnte nicht ermittelt werden.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(RelyingParty + "/" + sid));
    }

    private static InvalidOperationException Unsupported() => new(
        "Der lokale Datenspeicher benötigt Windows 11 mit Unterstützung für Windows-Hello-Passkeys mit Schlüsselableitung (PRF). Prüfe Windows Update und die Windows-Hello-Einrichtung.");

    private static byte[] ClientDataBytes(string type) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        type, challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        origin = "https://" + RelyingParty, crossOrigin = false
    });

    private static unsafe byte[] ReadCredential(byte* buffer, int size) =>
        buffer != null && size is > 0 and <= MaxCredentialIdSize ? new ReadOnlySpan<byte>(buffer, size).ToArray() :
            throw new CryptographicException("Windows Hello hat eine ungültige Passkey-Kennung geliefert.");

    private static unsafe bool VerifiedLocally(byte* data, int size) => data != null && size >= 37 &&
        (data[32] & 0x04) != 0 && (data[32] & 0x08) == 0; // UV set and credential not eligible for backup.

    private static unsafe byte[] ReadSecret(PrfValues* prf)
    {
        if (prf == null || prf->First == null || prf->FirstSize != KeySize) throw Unsupported();
        return new ReadOnlySpan<byte>(prf->First, KeySize).ToArray();
    }

    private static void Check(int result)
    {
        if (result == 0) return;
        var error = Marshal.PtrToStringUni(WebAuthNGetErrorName(result));
        if (error == "NotAllowedError" || result == unchecked((int)0x800704C7)) throw new OperationCanceledException();
        if (error == "NotSupportedError") throw Unsupported();
        throw new CryptographicException($"Windows Hello konnte den lokalen Datenspeicher nicht entsperren (0x{result:X8}).");
    }

    private static unsafe void Delete(byte[] credential)
    {
        fixed (byte* id = credential) WebAuthNDeletePlatformCredential(credential.Length, id);
    }

    // Layouts follow Microsoft's webauthn.h. Each output ends at the last field we consume.
    private unsafe struct RpEntity { public int Version; public char* Id, Name, Icon; }
    private unsafe struct UserEntity { public int Version, IdSize; public byte* Id; public char* Name, Icon, DisplayName; }
    private unsafe struct CoseParameter { public int Version; public char* Type; public int Algorithm; }
    private unsafe struct NativeList { public int Count; public void* Items; }
    private unsafe struct ClientData { public int Version, Size; public byte* Json; public char* HashAlgorithm; }
    private unsafe struct NativeCredential { public int Version, IdSize; public byte* Id; public char* Type; public int Transports; }
    private unsafe struct PrfValues { public int FirstSize; public byte* First; public int SecondSize; public byte* Second; }
    private unsafe struct PrfInputs { public PrfValues* Global; public int Count; public void* PerCredential; }

    private unsafe struct MakeOptions
    {
        public int Version, Timeout;
        public NativeList Credentials, Extensions;
        public int Attachment, ResidentKey, Verification, Attestation, Flags;
        public Guid* CancellationId;
        public void* ExcludeList;
        public int EnterpriseAttestation, LargeBlobSupport, PreferResident, InPrivate, EnablePrf;
        public void* LinkedDevice;
        public int JsonExtensionSize;
        public byte* JsonExtensions;
        public PrfValues* Prf;
        public int HintCount;
        public char** Hints;
        public int ThirdPartyPayment;
    }

    private unsafe struct GetOptions
    {
        public int Version, Timeout;
        public NativeList Credentials, Extensions;
        public int Attachment, Verification, Flags;
        public char* U2fAppId;
        public int* UseU2fAppId;
        public Guid* CancellationId;
        public NativeList* AllowList;
        public int LargeBlobOperation, LargeBlobSize;
        public byte* LargeBlob;
        public PrfInputs* Prf;
        public int InPrivate;
    }

    #pragma warning disable CS0649 // Native output fields are initialized by webauthn.dll.
    private unsafe struct CredentialHeader { public int Version, IdSize; public byte* Id; public char* Type; }

    private unsafe struct Attestation
    {
        public int Version;
        public char* Format;
        public int AuthenticatorDataSize;
        public byte* AuthenticatorData;
        public int AttestationSize;
        public byte* AttestationBytes;
        public int DecodeType;
        public void* Decoded;
        public int ObjectSize;
        public byte* Object;
        public int CredentialSize;
        public byte* CredentialId;
        public NativeList Extensions;
        public int Transport, EnterpriseAttestation, LargeBlobSupported, Resident, PrfEnabled, UnsignedExtensionsSize;
        public byte* UnsignedExtensions;
        public PrfValues* Prf;
    }

    private unsafe struct Assertion
    {
        public int Version, AuthenticatorDataSize;
        public byte* AuthenticatorData;
        public int SignatureSize;
        public byte* Signature;
        public CredentialHeader Credential;
        public int UserIdSize;
        public byte* UserId;
        public NativeList Extensions;
        public int LargeBlobSize;
        public byte* LargeBlob;
        public int LargeBlobStatus;
        public PrfValues* Prf;
    }
    #pragma warning restore CS0649

    [LibraryImport("webauthn.dll")]
    private static partial int WebAuthNGetApiVersionNumber();
    [LibraryImport("webauthn.dll")]
    private static partial int WebAuthNIsUserVerifyingPlatformAuthenticatorAvailable(out int available);
    [LibraryImport("webauthn.dll")]
    private static partial int WebAuthNGetCancellationId(out Guid cancellationId);
    [LibraryImport("webauthn.dll")]
    private static partial int WebAuthNCancelCurrentOperation(in Guid cancellationId);
    [LibraryImport("webauthn.dll")]
    private static unsafe partial int WebAuthNAuthenticatorMakeCredential(nint window, in RpEntity rp, in UserEntity user,
        in NativeList parameters, in ClientData data, in MakeOptions options, out Attestation* result);
    [LibraryImport("webauthn.dll")]
    private static unsafe partial int WebAuthNAuthenticatorGetAssertion(nint window, char* rp, in ClientData data,
        in GetOptions options, out Assertion* result);
    [LibraryImport("webauthn.dll")]
    private static unsafe partial void WebAuthNFreeCredentialAttestation(Attestation* attestation);
    [LibraryImport("webauthn.dll")]
    private static unsafe partial void WebAuthNFreeAssertion(Assertion* assertion);
    [LibraryImport("webauthn.dll")]
    private static unsafe partial int WebAuthNDeletePlatformCredential(int size, byte* credentialId);
    [LibraryImport("webauthn.dll")]
    private static partial nint WebAuthNGetErrorName(int result);
}
