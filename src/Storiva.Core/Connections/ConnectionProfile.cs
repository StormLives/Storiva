namespace Storiva.Core.Connections;

public enum StorageProvider
{
    /// <summary>Amazon S3 (endpoints resolved by the AWS SDK from the region).</summary>
    AwsS3,

    /// <summary>MinIO or another server that implements the S3 API, reached through an explicit endpoint URL.</summary>
    S3Compatible,
}

public enum AuthenticationMethod
{
    /// <summary>Access key ID + secret access key (+ optional session token), protected with DPAPI by Storiva.</summary>
    AccessKey,

    /// <summary>A named profile of the AWS shared config/credentials files (incl. SSO and assume-role profiles).
    /// Storiva stores only the profile name.</summary>
    AwsProfile,

    /// <summary>The AWS SDK default chain (environment, default profile, SSO, container/instance roles).
    /// AWS S3 only: those credentials are never offered to a third-party endpoint.</summary>
    DefaultChain,
}

/// <summary>Last connection test as persisted (the message is Storiva-generated and never contains secrets).</summary>
public sealed record ConnectionTestRecord(ConnectionStatus Status, DateTimeOffset TestedAt, string Summary);

/// <summary>
/// Non-secret part of a connection. Persisted in connections.json. Secrets live in <see cref="ISecretStore"/>,
/// keyed by <see cref="Id"/>, and never appear here.
/// </summary>
public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public StorageProvider Provider { get; init; } = StorageProvider.AwsS3;

    /// <summary>S3-compatible only: absolute http(s) URL, e.g. https://minio.example.internal:9000.</summary>
    public string? Endpoint { get; init; }

    /// <summary>AWS: region code (e.g. eu-west-1). S3-compatible: the region the server expects for signing
    /// (MinIO's default is us-east-1).</summary>
    public string Region { get; init; } = ConnectionRules.DefaultRegion;

    public AuthenticationMethod Authentication { get; init; } = AuthenticationMethod.AccessKey;

    /// <summary>AwsProfile only: profile name in the AWS shared files.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Path-style addressing (https://host/bucket/key) instead of virtual-hosted style
    /// (https://bucket.host/key). Most MinIO deployments need it.</summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>The user explicitly accepted an unencrypted http:// endpoint (S3-compatible only).</summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>
    /// Send the additional request checksums that newer SDKs add by default (CRC32 trailers). On for AWS; off by
    /// default for S3-compatible servers because several implementations reject them. See docs/CONNECTION_SETUP.md.
    /// </summary>
    public bool UseFlexibleChecksums { get; init; } = true;

    public ConnectionTestRecord? LastTest { get; init; }

    /// <summary>Short description for lists, without secrets.</summary>
    public string Describe() => Provider switch
    {
        StorageProvider.AwsS3 => $"AWS S3 · {Region}",
        _ => $"S3-compatible · {SafeEndpointLabel(Endpoint)}",
    };

    public string AuthenticationLabel => Authentication switch
    {
        AuthenticationMethod.AwsProfile => $"AWS profile \"{ProfileName}\"",
        AuthenticationMethod.DefaultChain => "AWS default credential chain",
        _ => "Access key (stored with Windows DPAPI)",
    };

    /// <summary>Scheme, host and port only: user info, path and query of the URL are never shown.</summary>
    public static string SafeEndpointLabel(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "(no endpoint)";
}

public sealed record ConnectionValidationError(string Field, string Message);

/// <summary>Validation of a connection draft. Pure rules; nothing here contacts a server.</summary>
public static class ConnectionRules
{
    public const string DefaultRegion = "us-east-1";
    public const int MaxNameLength = 64;

    /// <summary>
    /// Regions offered in the dialogs (the field also accepts any well-formed code, so newer regions work
    /// without an update).
    /// </summary>
    public static readonly string[] KnownAwsRegions =
    [
        "us-east-1", "us-east-2", "us-west-1", "us-west-2", "ca-central-1", "ca-west-1", "sa-east-1",
        "eu-west-1", "eu-west-2", "eu-west-3", "eu-central-1", "eu-central-2", "eu-north-1", "eu-south-1", "eu-south-2",
        "ap-east-1", "ap-south-1", "ap-south-2", "ap-southeast-1", "ap-southeast-2", "ap-southeast-3", "ap-southeast-4",
        "ap-northeast-1", "ap-northeast-2", "ap-northeast-3", "me-south-1", "me-central-1", "il-central-1", "af-south-1",
    ];

    public static IReadOnlyList<ConnectionValidationError> Validate(ConnectionProfile profile, CredentialsDraft? credentials, IEnumerable<string> otherNames)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<ConnectionValidationError>();
        var name = profile.Name.Trim();
        if (name.Length == 0)
        {
            errors.Add(new(nameof(ConnectionProfile.Name), "Enter a name for the connection."));
        }
        else if (name.Length > MaxNameLength)
        {
            errors.Add(new(nameof(ConnectionProfile.Name), $"Use at most {MaxNameLength} characters."));
        }
        else if (otherNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new(nameof(ConnectionProfile.Name), "Another connection already uses this name."));
        }

        if (ValidateRegion(profile.Region) is { } regionError)
        {
            errors.Add(new(nameof(ConnectionProfile.Region), regionError));
        }

        if (profile.Provider == StorageProvider.S3Compatible)
        {
            if (ValidateEndpoint(profile.Endpoint, profile.AllowInsecureHttp) is { } endpointError)
            {
                errors.Add(new(nameof(ConnectionProfile.Endpoint), endpointError));
            }

            if (profile.Authentication == AuthenticationMethod.DefaultChain)
            {
                errors.Add(new(nameof(ConnectionProfile.Authentication),
                    "The AWS default credential chain is offered for AWS S3 only, so AWS credentials are never sent to another server. Use an access key or a named profile."));
            }
        }

        if (profile.Authentication == AuthenticationMethod.AwsProfile && ValidateProfileName(profile.ProfileName) is { } profileError)
        {
            errors.Add(new(nameof(ConnectionProfile.ProfileName), profileError));
        }

        if (profile.Authentication == AuthenticationMethod.AccessKey && credentials is not null)
        {
            errors.AddRange(ValidateCredentials(credentials));
        }

        return errors;
    }

    /// <summary>Region codes are lowercase letters, digits and hyphens (e.g. eu-west-1, or a custom MinIO region).</summary>
    public static string? ValidateRegion(string? region)
    {
        var value = region?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return "Enter a region (for MinIO the default is us-east-1).";
        }

        return value.Length > 32 || !value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') || value[0] == '-' || value[^1] == '-'
            ? "Use lowercase letters, digits and hyphens only (e.g. us-east-1)."
            : null;
    }

    /// <summary>
    /// The endpoint must be an absolute http(s) URL with an explicit scheme and a host, without user info,
    /// query or fragment. http:// is only accepted when the user explicitly allowed unencrypted traffic.
    /// </summary>
    public static string? ValidateEndpoint(string? endpoint, bool allowInsecureHttp)
    {
        var text = endpoint?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return "Enter the endpoint URL, e.g. https://minio.example.internal:9000.";
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            return "Include the scheme: https://host:port (recommended) or http://host:port.";
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return "This is not a valid URL.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return "Use an https:// or http:// URL.";
        }

        if (uri.UserInfo.Length > 0)
        {
            return "Do not put user names or keys in the URL. Use the credential fields.";
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return "Remove the query string (?…) and fragment (#…) from the URL.";
        }

        if (uri.AbsolutePath.Trim('/').Length > 0)
        {
            return "Use the server address only (scheme, host and port), without a path.";
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp)
        {
            return "http:// sends data unencrypted. Use https://, or tick \"Allow unencrypted HTTP\" for an isolated test server.";
        }

        return null;
    }

    public static string? ValidateProfileName(string? profileName)
    {
        var value = profileName?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return "Enter the name of an AWS profile (e.g. default).";
        }

        return value.Length > 128 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '[' or ']')
            ? "Profile names cannot contain spaces or brackets."
            : null;
    }

    public static IEnumerable<ConnectionValidationError> ValidateCredentials(CredentialsDraft draft)
    {
        // An edited connection may keep its stored credentials (both fields left empty).
        if (draft.KeepStored && draft.AccessKeyId.Length == 0 && draft.SecretAccessKey.Length == 0)
        {
            yield break;
        }

        if (draft.AccessKeyId.Trim().Length == 0)
        {
            yield return new(nameof(CredentialsDraft.AccessKeyId), "Enter the access key ID.");
        }
        else if (draft.AccessKeyId.Trim().Length > 128 || draft.AccessKeyId.Trim().Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            yield return new(nameof(CredentialsDraft.AccessKeyId), "The access key ID cannot contain spaces.");
        }

        if (draft.SecretAccessKey.Length == 0)
        {
            yield return new(nameof(CredentialsDraft.SecretAccessKey), "Enter the secret access key.");
        }
        else if (draft.SecretAccessKey.Length > 256 || draft.SecretAccessKey.Any(char.IsControl))
        {
            yield return new(nameof(CredentialsDraft.SecretAccessKey), "The secret access key contains invalid characters.");
        }

        if (draft.SessionToken.Any(char.IsControl))
        {
            yield return new(nameof(CredentialsDraft.SessionToken), "The session token contains invalid characters.");
        }
    }

    /// <summary>Normalises the free-text fields of a draft (trim, provider-specific defaults).</summary>
    public static ConnectionProfile Normalize(ConnectionProfile profile) => profile with
    {
        Name = profile.Name.Trim(),
        Region = profile.Region.Trim(),
        Endpoint = profile.Provider == StorageProvider.S3Compatible ? profile.Endpoint?.Trim().TrimEnd('/') : null,
        ProfileName = profile.Authentication == AuthenticationMethod.AwsProfile ? profile.ProfileName?.Trim() : null,
        AllowInsecureHttp = profile.Provider == StorageProvider.S3Compatible && profile.AllowInsecureHttp,
    };
}
