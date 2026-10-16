using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Storiva.Core.Connections;
using Storiva.Infrastructure.Persistence;

namespace Storiva.Infrastructure.Security;

/// <summary>
/// Credentials protected with the Windows Data Protection API (DPAPI, <see cref="ProtectedData"/>), scope
/// CurrentUser: only the same Windows user on the same computer can decrypt them. One file per connection in
/// %LOCALAPPDATA%\Storiva\secrets\, separate from the non-secret settings. The entropy binds each blob to its
/// connection ID, so a blob copied onto another connection does not decrypt.
/// <para>Limits (see docs/CREDENTIAL_SECURITY.md): DPAPI protects against reading the files from another account or
/// another computer; it does not protect against malware running as the same user, and it does not synchronise
/// secrets between computers.</para>
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private const string EntropyPrefix = "Storiva.ConnectionSecret.v1:";
    private readonly string _folder;

    public DpapiSecretStore(StorivaDataPaths paths)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        _folder = (paths ?? throw new ArgumentNullException(nameof(paths))).SecretsFolder;
    }

    public string Description => $"Windows DPAPI (current user) in {_folder}";

    public void Save(Guid connectionId, StorageCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new SecretDto
        {
            Version = 1,
            AccessKeyId = credentials.AccessKeyId,
            SecretAccessKey = credentials.SecretAccessKey,
            SessionToken = credentials.SessionToken,
        });
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, Entropy(connectionId), DataProtectionScope.CurrentUser);
            AtomicFile.WriteAllBytes(PathFor(connectionId), protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public SecretLoadResult Load(Guid connectionId)
    {
        var path = PathFor(connectionId);
        if (!File.Exists(path))
        {
            return SecretLoadResult.NotFound;
        }

        byte[] blob;
        try
        {
            blob = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SecretLoadResult(SecretLoadStatus.AccessError, null, $"The protected credentials file could not be read ({ex.GetType().Name}).");
        }

        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(blob, Entropy(connectionId), DataProtectionScope.CurrentUser);
            var dto = JsonSerializer.Deserialize<SecretDto>(plain);
            if (dto is null || string.IsNullOrWhiteSpace(dto.AccessKeyId) || string.IsNullOrEmpty(dto.SecretAccessKey))
            {
                return Undecryptable();
            }

            return new SecretLoadResult(SecretLoadStatus.Loaded, new StorageCredentials(dto.AccessKeyId, dto.SecretAccessKey, dto.SessionToken));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Never include the exception text: it is not needed and could echo input.
            return Undecryptable();
        }
        finally
        {
            if (plain is not null)
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    public bool Exists(Guid connectionId) => File.Exists(PathFor(connectionId));

    public bool Delete(Guid connectionId)
    {
        var path = PathFor(connectionId);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static SecretLoadResult Undecryptable() => new(SecretLoadStatus.Undecryptable, null,
        "The stored credentials cannot be decrypted. DPAPI only works for the Windows user and computer that saved them " +
        "(they do not move with a copied profile or to another PC). Edit the connection and enter the credentials again.");

    private string PathFor(Guid id) => Path.Combine(_folder, id.ToString("N") + ".bin");

    private static byte[] Entropy(Guid id) => Encoding.UTF8.GetBytes(EntropyPrefix + id.ToString("N"));

    private sealed class SecretDto
    {
        public int Version { get; set; }

        public string AccessKeyId { get; set; } = string.Empty;

        public string SecretAccessKey { get; set; } = string.Empty;

        public string? SessionToken { get; set; }
    }
}
