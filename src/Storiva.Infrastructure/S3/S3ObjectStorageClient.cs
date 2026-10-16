using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Storiva.Core.Connections;
using Storiva.Core.Details;
using Storiva.Core.Remote;
using Tag = Storiva.Core.Details.Tag;
using S3Tag = Amazon.S3.Model.Tag;

namespace Storiva.Infrastructure.S3;

/// <summary>
/// <see cref="IObjectStorageClient"/> on the official AWS SDK for .NET. Works with AWS S3 (one client per bucket
/// region, discovered from the bucket list or a redirect) and with S3-compatible servers (one client on the
/// configured endpoint). Every failure is mapped by <see cref="S3ErrorMapper"/>.
/// </summary>
public sealed class S3ObjectStorageClient : IObjectStorageClient
{
    /// <summary>Storage classes offered for AWS. Archive classes (GLACIER, DEEP_ARCHIVE) are left out on purpose:
    /// objects there cannot be read until restored.</summary>
    public static readonly string[] AwsStorageClasses = ["STANDARD", "INTELLIGENT_TIERING", "STANDARD_IA", "ONEZONE_IA", "GLACIER_IR"];

    private readonly AWSCredentials? _credentials;
    private readonly StorageCredentials? _raw;
    private readonly bool _isAws;
    private readonly Dictionary<string, AmazonS3Client> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _bucketRegions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    internal S3ObjectStorageClient(ConnectionProfile profile, AWSCredentials? credentials, StorageCredentials? raw)
    {
        Profile = profile;
        _credentials = credentials;
        _raw = raw;
        _isAws = profile.Provider == StorageProvider.AwsS3;
    }

    public ConnectionProfile Profile { get; }

    public IReadOnlyList<string> Regions => _isAws ? ConnectionRules.KnownAwsRegions : [Profile.Region];

    public IReadOnlyList<string> StorageClasses => _isAws ? AwsStorageClasses : [];

    // ---------- connection ----------

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var page = await ListBucketsAsync(null, cancellationToken).ConfigureAwait(false);
            var count = page.Buckets.Count;
            var more = page.ContinuationToken is not null ? "+" : string.Empty;
            return new ConnectionTestResult(ConnectionStatus.Connected,
                $"Connected. {count}{more} bucket(s) visible to these credentials.", count, Duration: stopwatch.Elapsed);
        }
        catch (StorageOperationException ex) when (ex.Kind == StorageErrorKind.AccessDenied)
        {
            // AccessDenied (not InvalidAccessKeyId/SignatureDoesNotMatch) means the signature was accepted.
            return new ConnectionTestResult(ConnectionStatus.ConnectedLimited,
                "The credentials were accepted, but listing buckets is not allowed (e.g. no s3:ListAllMyBuckets). " +
                "Open a bucket by name with Buckets › Add External Bucket.", Error: ex.Error, Duration: stopwatch.Elapsed);
        }
    }

    public async Task<BucketListing> ListBucketsAsync(string? continuationToken, CancellationToken cancellationToken)
    {
        var request = new ListBucketsRequest();
        if (_isAws)
        {
            // Paginated form: also returns each bucket's region.
            request.MaxBuckets = 1000;
        }

        if (continuationToken is not null)
        {
            request.ContinuationToken = continuationToken;
        }

        var response = await RunAsync("Listing buckets", null, c => c.ListBucketsAsync(request, cancellationToken)).ConfigureAwait(false);
        var buckets = (response.Buckets ?? []).Select(b =>
        {
            var region = NullIfEmpty(b.BucketRegion);
            if (region is not null)
            {
                RememberRegion(b.BucketName, region);
            }

            return new RemoteBucket(b.BucketName, ToOffset(b.CreationDate), _isAws ? region : Profile.Region);
        }).ToList();
        return new BucketListing(buckets, NullIfEmpty(response.ContinuationToken));
    }

    public async Task<string?> GetBucketRegionAsync(string bucket, CancellationToken cancellationToken)
    {
        var response = await RunAsync("Opening the bucket", bucket, c => c.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }, cancellationToken)).ConfigureAwait(false);
        var region = NullIfEmpty(response.BucketRegion);
        if (region is not null)
        {
            RememberRegion(bucket, region);
        }

        return _isAws ? region ?? RegionOf(bucket) : Profile.Region;
    }

    // ---------- listing ----------

    public async Task<ObjectListing> ListObjectsAsync(string bucket, string prefix, string? continuationToken, int maxKeys, CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = prefix,
            Delimiter = "/",
            MaxKeys = Math.Clamp(maxKeys, 1, 1000),
            ContinuationToken = continuationToken,
        };
        var response = await RunAsync("Listing objects", bucket, c => c.ListObjectsV2Async(request, cancellationToken)).ConfigureAwait(false);
        return new ObjectListing(
            prefix,
            response.CommonPrefixes ?? [],
            (response.S3Objects ?? []).Select(ToRemote).ToList(),
            response.IsTruncated == true ? NullIfEmpty(response.NextContinuationToken) : null);
    }

    public async IAsyncEnumerable<RemoteObject> ListAllObjectsAsync(string bucket, string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? token = null;
        do
        {
            var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, MaxKeys = 1000, ContinuationToken = token };
            var response = await RunAsync("Listing objects", bucket, c => c.ListObjectsV2Async(request, cancellationToken)).ConfigureAwait(false);
            foreach (var obj in response.S3Objects ?? [])
            {
                yield return ToRemote(obj);
            }

            token = response.IsTruncated == true ? NullIfEmpty(response.NextContinuationToken) : null;
        }
        while (token is not null);
    }

    // ---------- buckets ----------

    public async Task CreateBucketAsync(string bucket, string? region, CancellationToken cancellationToken)
    {
        if (_isAws)
        {
            var target = string.IsNullOrWhiteSpace(region) ? Profile.Region : region.Trim();
            var client = ClientForRegion(target);
            try
            {
                await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket, UseClientRegion = true }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw S3ErrorMapper.Map(ex, "Creating the bucket", _raw, _isAws);
            }

            RememberRegion(bucket, target);
            return;
        }

        await RunAsync("Creating the bucket", null, c => c.PutBucketAsync(new PutBucketRequest { BucketName = bucket, UseClientRegion = false }, cancellationToken)).ConfigureAwait(false);
    }

    public Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken) =>
        RunAsync("Deleting the bucket", bucket, c => c.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, cancellationToken));

    // ---------- objects ----------

    public Task CreateFolderAsync(string bucket, string folderKey, CancellationToken cancellationToken)
    {
        if (!folderKey.EndsWith('/'))
        {
            throw new ArgumentException("A folder key ends with '/'.", nameof(folderKey));
        }

        return RunAsync("Creating the folder", bucket, c => c.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = folderKey, ContentBody = string.Empty }, cancellationToken));
    }

    public async Task UploadObjectAsync(string bucket, string key, string localPath, string? contentType, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var length = new FileInfo(localPath).Length;
        await RunAsync("Uploading", bucket, async c =>
        {
            // TransferUtility streams from disk and switches to multipart uploads for large files.
            using var transfer = new TransferUtility(c);
            var request = new TransferUtilityUploadRequest
            {
                BucketName = bucket,
                Key = key,
                FilePath = localPath,
                ContentType = contentType,
            };
            if (progress is not null)
            {
                request.UploadProgressEvent += (_, e) => progress.Report(new TransferProgress(e.TransferredBytes, e.TotalBytes > 0 ? e.TotalBytes : length));
            }

            await transfer.UploadAsync(request, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        progress?.Report(new TransferProgress(length, length));
    }

    public async Task DownloadObjectAsync(string bucket, string key, Stream destination, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        await RunAsync("Downloading", bucket, async c =>
        {
            using var response = await c.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, cancellationToken).ConfigureAwait(false);
            long? total = response.ContentLength > 0 ? response.ContentLength : response.Headers.ContentLength > 0 ? response.Headers.ContentLength : null;
            var buffer = new byte[81920];
            long copied = 0;
            await using var source = response.ResponseStream;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                progress?.Report(new TransferProgress(copied, total));
            }

            if (total is { } expected && copied != expected)
            {
                throw new StorageOperationException(new StorageError(StorageErrorKind.EndpointUnreachable,
                    $"The download was interrupted: {copied} of {expected} bytes received."));
            }

            progress?.Report(new TransferProgress(copied, total ?? copied));
            return true;
        }).ConfigureAwait(false);
    }

    public Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken) =>
        RunAsync("Deleting", bucket, c => c.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, cancellationToken));

    public async Task<DeleteObjectsResult> DeleteObjectsAsync(string bucket, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return new DeleteObjectsResult([], []);
        }

        if (keys.Count > 1000)
        {
            throw new ArgumentException("At most 1000 keys per request.", nameof(keys));
        }

        var request = new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = keys.Select(k => new KeyVersion { Key = k }).ToList(),
            Quiet = false,
        };
        DeleteObjectsResponse response;
        try
        {
            response = await RunAsync("Deleting", bucket, c => c.DeleteObjectsAsync(request, cancellationToken)).ConfigureAwait(false);
        }
        catch (StorageOperationException ex) when (ex.InnerException is DeleteObjectsException partial && partial.Response is { } r)
        {
            response = r;
        }

        var deleted = (response.DeletedObjects ?? []).Select(d => d.Key).ToList();
        var failed = (response.DeleteErrors ?? []).Select(e => (e.Key, $"{e.Code}: {SecretRedactor.Redact(e.Message, _raw)}")).ToList();
        return new DeleteObjectsResult(deleted, failed);
    }

    public Task CopyObjectAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, string? storageClass, CancellationToken cancellationToken)
    {
        var request = new CopyObjectRequest
        {
            SourceBucket = sourceBucket,
            SourceKey = sourceKey,
            DestinationBucket = destinationBucket,
            DestinationKey = destinationKey,
            MetadataDirective = S3MetadataDirective.COPY,
        };
        if (storageClass is not null)
        {
            request.StorageClass = S3StorageClass.FindValue(storageClass);
        }

        // The copy request is sent to the destination bucket's region.
        return RunAsync(storageClass is null ? "Copying" : "Changing the storage class", destinationBucket, c => c.CopyObjectAsync(request, cancellationToken));
    }

    public async Task<ObjectMetadata> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        var response = await RunAsync("Reading the object properties", bucket, c => c.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, cancellationToken)).ConfigureAwait(false);
        var headers = new List<Header>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value) && headers.All(h => !h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(new Header(name, value));
            }
        }

        Add("Content-Type", response.Headers.ContentType);
        Add("Content-Length", response.ContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("Cache-Control", response.Headers.CacheControl);
        Add("Content-Disposition", response.Headers.ContentDisposition);
        Add("Content-Encoding", response.Headers.ContentEncoding);
        Add("Content-Language", response.Headers.ContentLanguage);
        Add("Expires", response.Headers.Expires?.ToUniversalTime().ToString("R"));
        Add("ETag", response.ETag);
        Add("Last-Modified", response.LastModified?.ToUniversalTime().ToString("R"));
        Add("x-amz-version-id", response.VersionId);
        Add("x-amz-storage-class", response.StorageClass?.Value);
        Add("x-amz-server-side-encryption", response.ServerSideEncryptionMethod?.Value);
        foreach (var name in response.Headers.Keys)
        {
            Add(name, response.Headers[name]);
        }

        var userMetadata = response.Metadata.Keys
            .Select(k => new Header(k.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase) ? k : "x-amz-meta-" + k, response.Metadata[k]))
            .ToList();

        return new ObjectMetadata(
            key,
            response.ContentLength,
            ToOffset(response.LastModified),
            NullIfEmpty(response.ETag),
            NullIfEmpty(response.Headers.ContentType),
            // S3 omits the header for STANDARD objects.
            NullIfEmpty(response.StorageClass?.Value) ?? (_isAws ? "STANDARD" : null),
            NullIfEmpty(response.VersionId),
            NullIfEmpty(response.ServerSideEncryptionMethod?.Value),
            headers,
            userMetadata);
    }

    public async Task<byte[]> ReadObjectStartAsync(string bucket, string key, int maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync("Reading the preview", bucket, async c =>
            {
                using var response = await c.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ByteRange = new ByteRange(0, maxBytes - 1) }, cancellationToken).ConfigureAwait(false);
                var buffer = new byte[maxBytes];
                var total = 0;
                await using var stream = response.ResponseStream;
                int read;
                while (total < maxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                }

                return buffer[..total];
            }).ConfigureAwait(false);
        }
        catch (StorageOperationException ex) when (ex.Error.Code == "InvalidRange")
        {
            return []; // empty object
        }
    }

    // ---------- ACL, tags, versioning ----------

    public async Task<AclInfo> GetAclAsync(string bucket, string? key, CancellationToken cancellationToken)
    {
        List<S3Grant>? grants;
        Owner? owner;
        if (key is null)
        {
            var response = await RunAsync("Reading permissions", bucket, c => c.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucket }, cancellationToken)).ConfigureAwait(false);
            (grants, owner) = (response.Grants, response.Owner);
        }
        else
        {
            var response = await RunAsync("Reading permissions", bucket, c => c.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key }, cancellationToken)).ConfigureAwait(false);
            (grants, owner) = (response.Grants, response.Owner);
        }

        var mapped = (grants ?? []).Select(g =>
        {
            var grantee = g.Grantee;
            var type = grantee?.Type == GranteeType.CanonicalUser ? RemoteGranteeType.CanonicalUser
                : grantee?.Type == GranteeType.Group ? RemoteGranteeType.Group
                : grantee?.Type == GranteeType.Email ? RemoteGranteeType.Email
                : RemoteGranteeType.Unknown;
            var id = type switch
            {
                RemoteGranteeType.Group => grantee?.URI,
                RemoteGranteeType.Email => grantee?.EmailAddress,
                _ => grantee?.CanonicalUser,
            };
            return new RemoteGrant(type, id ?? grantee?.DisplayName ?? "(unknown)", g.Permission?.Value ?? string.Empty);
        }).ToList();
        return new AclInfo(owner?.Id, NullIfEmpty(owner?.DisplayName), mapped);
    }

    public async Task<IReadOnlyList<Tag>> GetTagsAsync(string bucket, string? key, CancellationToken cancellationToken)
    {
        if (key is not null)
        {
            var response = await RunAsync("Reading tags", bucket, c => c.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key }, cancellationToken)).ConfigureAwait(false);
            return (response.Tagging ?? []).Select(t => new Tag(t.Key, t.Value ?? string.Empty)).ToList();
        }

        try
        {
            var response = await RunAsync("Reading tags", bucket, c => c.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket }, cancellationToken)).ConfigureAwait(false);
            return (response.TagSet ?? []).Select(t => new Tag(t.Key, t.Value ?? string.Empty)).ToList();
        }
        catch (StorageOperationException ex) when (ex.Error.Code is "NoSuchTagSet" or "NoSuchTagSetError")
        {
            return [];
        }
    }

    public Task SetTagsAsync(string bucket, string? key, IReadOnlyList<Tag> tags, CancellationToken cancellationToken)
    {
        var tagSet = tags.Select(t => new S3Tag { Key = t.Key, Value = t.Value }).ToList();
        if (key is not null)
        {
            return RunAsync("Saving tags", bucket, c => c.PutObjectTaggingAsync(new PutObjectTaggingRequest { BucketName = bucket, Key = key, Tagging = new Tagging { TagSet = tagSet } }, cancellationToken));
        }

        return tagSet.Count == 0
            ? RunAsync("Removing tags", bucket, c => c.DeleteBucketTaggingAsync(new DeleteBucketTaggingRequest { BucketName = bucket }, cancellationToken))
            : RunAsync("Saving tags", bucket, c => c.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = tagSet }, cancellationToken));
    }

    public async Task<VersioningStatus> GetVersioningAsync(string bucket, CancellationToken cancellationToken)
    {
        var response = await RunAsync("Reading versioning", bucket, c => c.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket }, cancellationToken)).ConfigureAwait(false);
        var status = response.VersioningConfig?.Status;
        return status == VersionStatus.Enabled ? VersioningStatus.Enabled
            : status == VersionStatus.Suspended ? VersioningStatus.Suspended
            : VersioningStatus.Disabled;
    }

    public Task SetVersioningAsync(string bucket, VersioningStatus status, CancellationToken cancellationToken)
    {
        if (status == VersioningStatus.Disabled)
        {
            throw new ArgumentException("Versioning cannot be turned off once enabled; it can only be suspended.", nameof(status));
        }

        var config = new S3BucketVersioningConfig { Status = status == VersioningStatus.Enabled ? VersionStatus.Enabled : VersionStatus.Suspended };
        return RunAsync("Changing versioning", bucket, c => c.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = config }, cancellationToken));
    }

    public async Task<IReadOnlyList<ObjectVersion>> ListVersionsAsync(string bucket, string key, int max, CancellationToken cancellationToken)
    {
        var result = new List<ObjectVersion>();
        string? keyMarker = null;
        string? versionMarker = null;
        while (result.Count < max)
        {
            var request = new ListVersionsRequest { BucketName = bucket, Prefix = key, MaxKeys = 1000, KeyMarker = keyMarker, VersionIdMarker = versionMarker };
            var response = await RunAsync("Listing versions", bucket, c => c.ListVersionsAsync(request, cancellationToken)).ConfigureAwait(false);
            var versions = response.Versions ?? [];
            foreach (var v in versions.Where(v => v.Key == key))
            {
                result.Add(new ObjectVersion(
                    v.VersionId ?? "null",
                    ToOffset(v.LastModified) ?? DateTimeOffset.MinValue,
                    v.Size ?? 0,
                    v.StorageClass?.Value ?? string.Empty,
                    v.IsLatest == true,
                    v.IsDeleteMarker == true));
            }

            // Versions are sorted by key: once a later key appears, every version of this key has been seen.
            if (response.IsTruncated != true || versions.Any(v => string.CompareOrdinal(v.Key, key) > 0))
            {
                break;
            }

            keyMarker = response.NextKeyMarker;
            versionMarker = response.NextVersionIdMarker;
        }

        return result.Take(max).ToList();
    }

    // ---------- links ----------

    public async Task<PresignedUrl> CreatePresignedGetUrlAsync(string bucket, string key, TimeSpan validFor, CancellationToken cancellationToken)
    {
        ShareLinks.ValidateValidity(validFor);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var client = ClientFor(bucket);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Protocol = UsesHttp ? Protocol.HTTP : Protocol.HTTPS,
        };

        try
        {
            // The SDK signs locally (SigV4 query string) with the client of the bucket's region; nothing is sent. It
            // measures X-Amz-Expires from its own clock, corrected with the Date header of earlier responses, which
            // can be a second or more behind this computer's: a link asked for exactly 7 days would then be refused
            // (SigV4 allows at most 604800 s). A probe signature measures that offset first.
            var target = (long)validFor.TotalSeconds;
            var probe = await SignAsync(client, request, DateTime.UtcNow.AddSeconds(ProbeSeconds)).ConfigureAwait(false);
            var offset = ExpiresSeconds(probe) - ProbeSeconds;
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string url;
                try
                {
                    url = await SignAsync(client, request, DateTime.UtcNow + validFor - TimeSpan.FromSeconds(offset)).ConfigureAwait(false);
                }
                catch (ArgumentException) when (attempt < 2)
                {
                    offset++; // one second over the maximum after a second boundary
                    continue;
                }

                var actual = ExpiresSeconds(url);
                if (actual <= target || attempt >= 2)
                {
                    if (actual > ShareLinks.MaxValidity.TotalSeconds || actual <= 0)
                    {
                        throw new StorageOperationException(new StorageError(StorageErrorKind.RegionOrConfiguration,
                            "Generating the link failed: the link could not be signed for the chosen time. Check this computer's clock and try again."));
                    }

                    var temporary = url.Contains("X-Amz-Security-Token=", StringComparison.Ordinal);
                    return new PresignedUrl(bucket, key, url, DateTimeOffset.UtcNow.AddSeconds(actual), temporary);
                }

                offset += actual - target;
            }
        }
        catch (Exception ex) when (ex is not StorageOperationException)
        {
            // Mapped to a safe message; no link is part of it.
            throw S3ErrorMapper.Map(ex, "Generating the link", _raw, _isAws);
        }
    }

    /// <summary>Validity of the calibration signature (well below the 7-day maximum whatever the clock offset).</summary>
    private const int ProbeSeconds = 3600;

    private static Task<string> SignAsync(AmazonS3Client client, GetPreSignedUrlRequest request, DateTime expires)
    {
        request.Expires = expires;
        return client.GetPreSignedURLAsync(request);
    }

    /// <summary>X-Amz-Expires of a signed URL, in seconds.</summary>
    internal static long ExpiresSeconds(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(url, @"[?&]X-Amz-Expires=(\d+)");
        return match.Success ? long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    public string GetObjectUrl(string bucket, string key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Same endpoint rules as real requests (region, path-style or virtual-hosted), resolved locally by the SDK.
        var endpoint = ClientFor(bucket).DetermineServiceOperationEndpoint(new GetObjectRequest { BucketName = bucket, Key = key });
        var baseUrl = endpoint.URL.TrimEnd('/');
        return baseUrl + "/" + EscapeKey(key);
    }

    /// <summary>Percent-encodes each segment of a key, keeping "/" as the separator (S3 URL path rules).</summary>
    internal static string EscapeKey(string key) => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    private bool UsesHttp => !_isAws && Uri.TryCreate(Profile.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp;

    // ---------- plumbing ----------

    /// <summary>
    /// Runs one SDK call with the right regional client. On AWS, a "wrong region" answer for a bucket triggers one
    /// lookup of the bucket's region and one retry there (the first attempt was rejected, so nothing was changed).
    /// </summary>
    private async Task<T> RunAsync<T>(string operation, string? bucket, Func<AmazonS3Client, Task<T>> call)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return await call(ClientFor(bucket)).ConfigureAwait(false);
        }
        catch (Exception ex) when (_isAws && bucket is not null && S3ErrorMapper.IsRegionMismatch(ex))
        {
            var region = await DiscoverRegionAsync(bucket, ex).ConfigureAwait(false);
            if (region is null || region == RegionOf(bucket))
            {
                throw S3ErrorMapper.Map(ex, operation, _raw, _isAws);
            }

            RememberRegion(bucket, region);
            try
            {
                return await call(ClientFor(bucket)).ConfigureAwait(false);
            }
            catch (Exception retry)
            {
                throw S3ErrorMapper.Map(retry, operation, _raw, _isAws);
            }
        }
        catch (Exception ex) when (ex is not StorageOperationException)
        {
            throw S3ErrorMapper.Map(ex, operation, _raw, _isAws);
        }
    }

    private async Task<string?> DiscoverRegionAsync(string bucket, Exception failure)
    {
        if (S3ErrorMapper.RegionHint(failure) is { Length: > 0 } hint)
        {
            return hint;
        }

        try
        {
            // GetBucketLocation can be asked from us-east-1 for any bucket the caller may read.
            var response = await ClientForRegion("us-east-1").GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket }).ConfigureAwait(false);
            var location = response.Location?.Value;
            return string.IsNullOrEmpty(location) ? "us-east-1" : location == "EU" ? "eu-west-1" : location;
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            return null;
        }
    }

    private string? RegionOf(string bucket)
    {
        lock (_gate)
        {
            return _bucketRegions.GetValueOrDefault(bucket);
        }
    }

    private void RememberRegion(string bucket, string region)
    {
        lock (_gate)
        {
            _bucketRegions[bucket] = region;
        }
    }

    private AmazonS3Client ClientFor(string? bucket) =>
        ClientForRegion(_isAws && bucket is not null ? RegionOf(bucket) ?? Profile.Region : Profile.Region);

    private AmazonS3Client ClientForRegion(string region)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = _isAws ? region : "endpoint";
            if (!_clients.TryGetValue(key, out var client))
            {
                var config = S3ClientFactory.BuildConfig(Profile, _isAws ? region : null);
                client = _credentials is null ? new AmazonS3Client(config) : new AmazonS3Client(_credentials, config);
                _clients[key] = client;
            }

            return client;
        }
    }

    private static RemoteObject ToRemote(S3Object o) =>
        new(o.Key, o.Size, ToOffset(o.LastModified), NullIfEmpty(o.StorageClass?.Value), NullIfEmpty(o.ETag));

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } v ? new DateTimeOffset(v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime()) : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }
}
