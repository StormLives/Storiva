using System.Text.RegularExpressions;

namespace Storiva.Core.Connections;

/// <summary>
/// Static S3 credentials. <see cref="ToString"/> is redacted so an accidental log line or exception message
/// never prints them; record-style value printing is deliberately not used.
/// </summary>
public sealed class StorageCredentials
{
    public StorageCredentials(string accessKeyId, string secretAccessKey, string? sessionToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrEmpty(secretAccessKey);
        AccessKeyId = accessKeyId.Trim();
        SecretAccessKey = secretAccessKey;
        SessionToken = string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken.Trim();
    }

    public string AccessKeyId { get; }

    public string SecretAccessKey { get; }

    public string? SessionToken { get; }

    /// <summary>Hint shown in the edit dialog: first 4 and last 4 characters of the key ID only.</summary>
    public string MaskedAccessKeyId => Mask(AccessKeyId);

    public override string ToString() => $"StorageCredentials({MaskedAccessKeyId}, secret: [redacted])";

    public static string Mask(string value) =>
        value.Length <= 8 ? new string('•', value.Length) : value[..4] + "…" + value[^4..];
}

/// <summary>What the user typed in the credential fields (kept only until the dialog closes).</summary>
/// <param name="KeepStored">Editing an existing connection: empty fields mean "keep the stored credentials".</param>
public sealed record CredentialsDraft(string AccessKeyId, string SecretAccessKey, string SessionToken, bool KeepStored = false)
{
    public bool IsEmpty => AccessKeyId.Trim().Length == 0 && SecretAccessKey.Length == 0 && SessionToken.Trim().Length == 0;

    public StorageCredentials ToCredentials() => new(AccessKeyId, SecretAccessKey, SessionToken);

    // Never print the values.
    public override string ToString() => "CredentialsDraft([redacted])";
}

public enum SecretLoadStatus
{
    Loaded,
    NotFound,

    /// <summary>The protected file exists but cannot be decrypted: other Windows user, other computer, reset
    /// password/DPAPI keys, or a damaged file.</summary>
    Undecryptable,

    /// <summary>The file could not be read (permissions, I/O).</summary>
    AccessError,
}

public sealed record SecretLoadResult(SecretLoadStatus Status, StorageCredentials? Credentials, string? Message = null)
{
    public static SecretLoadResult NotFound { get; } = new(SecretLoadStatus.NotFound, null,
        "No stored credentials were found for this connection. Edit the connection and enter them again.");
}

/// <summary>Protected storage of connection secrets, separate from the non-secret connection settings.</summary>
public interface ISecretStore
{
    /// <summary>Human-readable description of where and how secrets are kept (no secret values).</summary>
    string Description { get; }

    void Save(Guid connectionId, StorageCredentials credentials);

    SecretLoadResult Load(Guid connectionId);

    bool Exists(Guid connectionId);

    /// <summary>Removes the protected secret; returns false when there was none.</summary>
    bool Delete(Guid connectionId);
}

/// <summary>Secrets kept in memory (tests and smoke test). Never written anywhere.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<Guid, StorageCredentials> _secrets = new();

    public string Description => "memory (not persisted)";

    public void Save(Guid connectionId, StorageCredentials credentials) => _secrets[connectionId] = credentials ?? throw new ArgumentNullException(nameof(credentials));

    public SecretLoadResult Load(Guid connectionId) =>
        _secrets.TryGetValue(connectionId, out var c) ? new SecretLoadResult(SecretLoadStatus.Loaded, c) : SecretLoadResult.NotFound;

    public bool Exists(Guid connectionId) => _secrets.ContainsKey(connectionId);

    public bool Delete(Guid connectionId) => _secrets.Remove(connectionId);
}

/// <summary>
/// Removes secrets from text before it is shown or logged: the known values of the current credentials, plus
/// anything shaped like an AWS access key ID or a long base64 secret. Defence in depth: Storiva builds its own
/// messages and does not log configuration objects.
/// </summary>
public static partial class SecretRedactor
{
    public const string Placeholder = "[redacted]";

    [GeneratedRegex(@"\b(AKIA|ASIA|AGPA|AIDA|AROA|ANPA|ANVA|AIPA)[A-Z0-9]{12,}\b")]
    private static partial Regex AwsKeyIdPattern();

    // 40 characters of base64 alphabet: the shape of an AWS secret access key.
    [GeneratedRegex(@"(?<![A-Za-z0-9/+=])[A-Za-z0-9/+]{40}(?![A-Za-z0-9/+=])")]
    private static partial Regex AwsSecretPattern();

    [GeneratedRegex(@"(?i)(x-amz-security-token|x-amz-credential|signature|credential|authorization|aws_secret_access_key|aws_session_token)\s*[=:]\s*[^\s,;&]+")]
    private static partial Regex KeyValuePattern();

    public static string Redact(string? text, StorageCredentials? known = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        if (known is not null)
        {
            foreach (var secret in new[] { known.SecretAccessKey, known.SessionToken, known.AccessKeyId })
            {
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
                {
                    result = result.Replace(secret, Placeholder, StringComparison.Ordinal);
                }
            }
        }

        result = KeyValuePattern().Replace(result, m => m.Groups[1].Value + "=" + Placeholder);
        result = AwsKeyIdPattern().Replace(result, Placeholder);
        result = AwsSecretPattern().Replace(result, Placeholder);
        return result;
    }
}
