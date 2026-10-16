using Storiva.Core.Connections;
using Storiva.Core.Details;

namespace Storiva.Core.Remote;

public sealed record RemoteBucket(string Name, DateTimeOffset? CreatedAt, string? Region);

public sealed record BucketListing(IReadOnlyList<RemoteBucket> Buckets, string? ContinuationToken);

/// <param name="Key">Exact object key as stored by the server (may contain unusual characters).</param>
public sealed record RemoteObject(string Key, long? Size, DateTimeOffset? LastModified, string? StorageClass, string? ETag);

/// <summary>One page of a delimiter ("/") listing below <see cref="Prefix"/>.</summary>
/// <param name="CommonPrefixes">Exact "sub-folder" prefixes, each ending with "/".</param>
/// <param name="ContinuationToken">Non-null when more entries exist (the listing is truncated).</param>
public sealed record ObjectListing(string Prefix, IReadOnlyList<string> CommonPrefixes, IReadOnlyList<RemoteObject> Objects, string? ContinuationToken)
{
    public bool IsTruncated => ContinuationToken is not null;
}

/// <summary>Metadata returned by a HEAD request. Absent values stay null: Storiva never invents them.</summary>
public sealed record ObjectMetadata(
    string Key,
    long ContentLength,
    DateTimeOffset? LastModified,
    string? ETag,
    string? ContentType,
    string? StorageClass,
    string? VersionId,
    string? ServerSideEncryption,
    IReadOnlyList<Header> Headers,
    IReadOnlyList<Header> UserMetadata);

public enum RemoteGranteeType
{
    CanonicalUser,
    Group,
    Email,
    Unknown,
}

/// <summary>One ACL grant as returned by the server.</summary>
/// <param name="Permission">FULL_CONTROL, READ, WRITE, READ_ACP or WRITE_ACP.</param>
public sealed record RemoteGrant(RemoteGranteeType Type, string Grantee, string Permission);

public sealed record AclInfo(string? OwnerId, string? OwnerName, IReadOnlyList<RemoteGrant> Grants);

/// <param name="Failed">Keys the server refused to delete, with a safe reason.</param>
public sealed record DeleteObjectsResult(IReadOnlyList<string> Deleted, IReadOnlyList<(string Key, string Reason)> Failed);

/// <summary>Bytes transferred so far and the total when the provider reports it (null = unknown).</summary>
public readonly record struct TransferProgress(long Transferred, long? Total)
{
    /// <summary>0–100 when the total is known; null for an indeterminate transfer.</summary>
    public int? Percent => Total is > 0 ? (int)Math.Clamp(Transferred * 100 / Total.Value, 0, 100) : Total == 0 ? 100 : null;

    /// <summary>A short note that replaces the task message (e.g. "retrying 2/3…"); null keeps the current one.</summary>
    public string? Status { get; init; }
}

/// <summary>
/// Provider-neutral access to an object store. Every method is asynchronous, cancellable and throws
/// <see cref="StorageOperationException"/> with a safe message on failure. No method retries a write on its own
/// beyond the transport retries of the underlying SDK (whole-object retries are <see cref="TransferRetry"/>'s, in
/// <see cref="RemoteOperations"/>), and none deletes anything that was not asked for.
/// </summary>
public interface IObjectStorageClient : IDisposable
{
    ConnectionProfile Profile { get; }

    /// <summary>Regions offered when creating a bucket.</summary>
    IReadOnlyList<string> Regions { get; }

    /// <summary>Storage classes offered by "Change Storage Class". Empty when the provider has no choice.</summary>
    IReadOnlyList<string> StorageClasses { get; }

    /// <summary>Read-only check: resolves credentials and lists the first page of buckets. Never writes.</summary>
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken);

    Task<BucketListing> ListBucketsAsync(string? continuationToken, CancellationToken cancellationToken);

    /// <summary>HEAD bucket: succeeds when the bucket exists and is accessible (used by "Add External Bucket").</summary>
    Task<string?> GetBucketRegionAsync(string bucket, CancellationToken cancellationToken);

    /// <summary>One page of the objects and sub-prefixes directly below <paramref name="prefix"/> (delimiter "/").</summary>
    Task<ObjectListing> ListObjectsAsync(string bucket, string prefix, string? continuationToken, int maxKeys, CancellationToken cancellationToken);

    /// <summary>Every object below <paramref name="prefix"/> (no delimiter), page by page.</summary>
    IAsyncEnumerable<RemoteObject> ListAllObjectsAsync(string bucket, string prefix, CancellationToken cancellationToken);

    /// <param name="region">AWS: region of the new bucket. S3-compatible: ignored (the server decides).</param>
    Task CreateBucketAsync(string bucket, string? region, CancellationToken cancellationToken);

    /// <summary>Deletes an empty bucket. A bucket with objects fails with <see cref="StorageErrorKind.Conflict"/>.</summary>
    Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken);

    /// <summary>Creates the zero-byte "folder" marker <paramref name="folderKey"/> (ends with "/").</summary>
    Task CreateFolderAsync(string bucket, string folderKey, CancellationToken cancellationToken);

    /// <summary>Uploads a local file, streaming it from disk (multipart for large files).</summary>
    Task UploadObjectAsync(string bucket, string key, string localPath, string? contentType, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Streams an object into <paramref name="destination"/> without buffering it in memory.</summary>
    Task DownloadObjectAsync(string bucket, string key, Stream destination, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);

    Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken);

    /// <summary>Deletes up to 1000 keys in one request; per-key failures are reported, not thrown.</summary>
    Task<DeleteObjectsResult> DeleteObjectsAsync(string bucket, IReadOnlyList<string> keys, CancellationToken cancellationToken);

    /// <summary>Server-side copy (objects up to 5 GB). <paramref name="storageClass"/> null keeps the class.</summary>
    Task CopyObjectAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, string? storageClass, CancellationToken cancellationToken);

    /// <summary>
    /// Copy followed by delete. S3 has no atomic move: when the copy succeeds but the delete fails, the result says
    /// so and the object exists in both places.
    /// </summary>
    async Task<MoveResult> MoveObjectAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken)
    {
        await CopyObjectAsync(sourceBucket, sourceKey, destinationBucket, destinationKey, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await DeleteObjectAsync(sourceBucket, sourceKey, CancellationToken.None).ConfigureAwait(false);
            return new MoveResult(true, null);
        }
        catch (StorageOperationException ex)
        {
            return new MoveResult(false, ex.Error);
        }
    }

    Task<ObjectMetadata> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken);

    /// <summary>First <paramref name="maxBytes"/> bytes of an object (ranged GET) for the Preview tab.</summary>
    Task<byte[]> ReadObjectStartAsync(string bucket, string key, int maxBytes, CancellationToken cancellationToken);

    /// <param name="key">Null for the bucket ACL.</param>
    Task<AclInfo> GetAclAsync(string bucket, string? key, CancellationToken cancellationToken);

    /// <param name="key">Null for bucket tags.</param>
    Task<IReadOnlyList<Tag>> GetTagsAsync(string bucket, string? key, CancellationToken cancellationToken);

    /// <summary>Replaces every tag (an empty list removes them).</summary>
    Task SetTagsAsync(string bucket, string? key, IReadOnlyList<Tag> tags, CancellationToken cancellationToken);

    /// <summary><see cref="VersioningStatus.Disabled"/> = versioning was never enabled.</summary>
    Task<VersioningStatus> GetVersioningAsync(string bucket, CancellationToken cancellationToken);

    Task SetVersioningAsync(string bucket, VersioningStatus status, CancellationToken cancellationToken);

    /// <summary>Versions and delete markers of exactly <paramref name="key"/> (newest first, at most <paramref name="max"/>).</summary>
    Task<IReadOnlyList<ObjectVersion>> ListVersionsAsync(string bucket, string key, int max, CancellationToken cancellationToken);
}

/// <param name="SourceDeleted">False when the copy exists but the source could not be removed.</param>
public sealed record MoveResult(bool SourceDeleted, StorageError? DeleteError);

/// <summary>Creates clients for saved connections.</summary>
public interface IObjectStorageClientFactory
{
    /// <param name="credentials">Required for <see cref="AuthenticationMethod.AccessKey"/>; ignored otherwise.</param>
    /// <exception cref="StorageOperationException">Credentials cannot be obtained (missing profile, …).</exception>
    IObjectStorageClient Create(ConnectionProfile profile, StorageCredentials? credentials);
}
