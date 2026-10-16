using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Storiva.Core.Connections;
using Storiva.Core.Remote;

namespace Storiva.Infrastructure.S3;

/// <summary>
/// Builds AWS SDK clients for saved connections. Signing (SigV4), transport, retries and TLS are entirely the SDK's
/// and .NET's: Storiva never replaces certificate validation or the HTTP handler.
/// </summary>
public sealed class S3ClientFactory : IObjectStorageClientFactory
{
    public IObjectStorageClient Create(ConnectionProfile profile, StorageCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = ConnectionRules.Validate(profile, null, []).Where(e => e.Field != nameof(ConnectionProfile.Name)).ToList();
        if (errors.Count > 0)
        {
            throw new StorageOperationException(new StorageError(StorageErrorKind.RegionOrConfiguration, string.Join(" ", errors.Select(e => e.Message))));
        }

        return new S3ObjectStorageClient(profile, ResolveCredentials(profile, credentials), credentials);
    }

    /// <summary>
    /// SDK credentials for the chosen method. Null = let the SDK use its default chain (AWS S3 only; the validator
    /// refuses the default chain for S3-compatible endpoints).
    /// </summary>
    internal static AWSCredentials? ResolveCredentials(ConnectionProfile profile, StorageCredentials? credentials)
    {
        switch (profile.Authentication)
        {
            case AuthenticationMethod.AccessKey:
                if (credentials is null)
                {
                    throw new StorageOperationException(new StorageError(StorageErrorKind.CredentialsUnavailable, "No access key is stored for this connection. Edit it and enter the credentials."));
                }

                return credentials.SessionToken is { } token
                    ? new SessionAWSCredentials(credentials.AccessKeyId, credentials.SecretAccessKey, token)
                    : new BasicAWSCredentials(credentials.AccessKeyId, credentials.SecretAccessKey);

            case AuthenticationMethod.AwsProfile:
                // Reads the user's AWS shared config/credentials files only now, after an explicit Test/Connect.
                var chain = new CredentialProfileStoreChain();
                if (chain.TryGetAWSCredentials(profile.ProfileName, out var fromProfile))
                {
                    return fromProfile;
                }

                throw new StorageOperationException(new StorageError(StorageErrorKind.CredentialsUnavailable,
                    $"The AWS profile \"{profile.ProfileName}\" was not found (or is incomplete) in the AWS shared config/credentials files of this Windows user."));

            case AuthenticationMethod.DefaultChain when profile.Provider == StorageProvider.AwsS3:
                return null;

            default:
                throw new StorageOperationException(new StorageError(StorageErrorKind.CredentialsUnavailable,
                    "The AWS default credential chain is only used with AWS S3."));
        }
    }

    /// <summary>
    /// Names of the profiles in the AWS shared config/credentials files of this Windows user (names only; the
    /// profile contents are never returned or shown). Called only when the user asks for the list.
    /// </summary>
    public static IReadOnlyList<string> ListAwsProfileNames()
    {
        try
        {
            return new CredentialProfileStoreChain().ListProfiles()
                .Select(p => p.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AmazonClientException or FormatException)
        {
            return [];
        }
    }

    /// <summary>SDK configuration for a connection (and optionally another AWS region for a bucket that lives there).</summary>
    public static AmazonS3Config BuildConfig(ConnectionProfile profile, string? regionOverride = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var config = new AmazonS3Config
        {
            ForcePathStyle = profile.ForcePathStyle,
            // Each HTTP request: up to 3 attempts with the SDK's jittered backoff for throttling, 5xx and dropped
            // connections. Set explicitly so AWS_RETRY_MODE / AWS_MAX_ATTEMPTS or the shared config file cannot change
            // it; whole-object retries after a longer pause are TransferRetry's (Storiva.Core), on top of this.
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = 2,
        };

        if (profile.Provider == StorageProvider.AwsS3)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(regionOverride ?? profile.Region);
        }
        else
        {
            // Explicit endpoint and signing region; the scheme of the URL decides http/https.
            config.ServiceURL = profile.Endpoint;
            config.AuthenticationRegion = profile.Region;
            config.UseHttp = Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp;
        }

        if (!profile.UseFlexibleChecksums)
        {
            // Only send/validate checksums when the operation requires them (compatibility with servers that
            // reject the newer default CRC trailers).
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }

        return config;
    }
}
