using System.Runtime.CompilerServices;
using Storiva.Core.Connections;
using Storiva.Core.Details;
using Storiva.Core.Remote;

namespace Storiva.Tests.Fakes;

/// <summary>In-memory <see cref="IObjectStorageClient"/> with failure injection for unit tests (no network).</summary>
internal sealed class InMemoryObjectStorageClient : IObjectStorageClient
{
    private readonly SortedDictionary<string, SortedDictionary<string, byte[]>> _buckets = new(StringComparer.Ordinal);

    public InMemoryObjectStorageClient(ConnectionProfile? profile = null) =>
        Profile = profile ?? new ConnectionProfile { Name = "fake", Provider = StorageProvider.S3Compatible, Endpoint = "https://fake.example.invalid", ForcePathStyle = true };

    public ConnectionProfile Profile { get; }

    public IReadOnlyList<string> Regions => ["us-east-1"];

    public IReadOnlyList<string> StorageClasses => ["STANDARD"];

    public List<string> Calls { get; } = [];

    public HashSet<string> FailCopyKeys { get; } = new(StringComparer.Ordinal);

    public HashSet<string> FailDeleteKeys { get; } = new(StringComparer.Ordinal);

    public StorageErrorKind? FailEverything { get; set; }

    public Dictionary<string, IReadOnlyList<Tag>> TagsByKey { get; } = new(StringComparer.Ordinal);

    public VersioningStatus Versioning { get; set; } = VersioningStatus.Disabled;

    public AclInfo Acl { get; set; } = new("owner-1", "owner", [new RemoteGrant(RemoteGranteeType.CanonicalUser, "owner-1", "FULL_CONTROL")]);

    /// <summary>Called during downloads after the first chunk (lets a test cancel or fail mid-transfer).</summary>
    public Action? OnDownloadChunk { get; set; }

    public bool Disposed { get; private set; }

    public void Put(string bucket, string key, byte[]? content = null)
    {
        if (!_buckets.TryGetValue(bucket, out var objects))
        {
            objects = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            _buckets[bucket] = objects;
        }

        objects[key] = content ?? [];
    }

    public IReadOnlyList<string> Keys(string bucket) => _buckets.TryGetValue(bucket, out var o) ? o.Keys.ToList() : [];

    public byte[]? Get(string bucket, string key) => _buckets.TryGetValue(bucket, out var o) && o.TryGetValue(key, out var c) ? c : null;

    private void Gate(string call)
    {
        Calls.Add(call);
        if (FailEverything is { } kind)
        {
            throw new StorageOperationException(new StorageError(kind, $"{call} failed ({kind})"));
        }
    }

    private SortedDictionary<string, byte[]> Bucket(string name) =>
        _buckets.TryGetValue(name, out var objects) ? objects : throw new StorageOperationException(new StorageError(StorageErrorKind.NotFound, "No such bucket", "NoSuchBucket", 404));

    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        Gate("Test");
        return Task.FromResult(new ConnectionTestResult(ConnectionStatus.Connected, $"Connected. {_buckets.Count} bucket(s)."));
    }

    public Task<BucketListing> ListBucketsAsync(string? continuationToken, CancellationToken cancellationToken)
    {
        Gate("ListBuckets");
        return Task.FromResult(new BucketListing(_buckets.Keys.Select(b => new RemoteBucket(b, null, "us-east-1")).ToList(), null));
    }

    public Task<string?> GetBucketRegionAsync(string bucket, CancellationToken cancellationToken)
    {
        Gate("HeadBucket");
        Bucket(bucket);
        return Task.FromResult<string?>("us-east-1");
    }

    public Task<ObjectListing> ListObjectsAsync(string bucket, string prefix, string? continuationToken, int maxKeys, CancellationToken cancellationToken)
    {
        Gate($"List {bucket}/{prefix}");
        var entries = new List<(string Name, bool IsPrefix, string Key)>();
        foreach (var key in Bucket(bucket).Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = key[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                var common = prefix + rest[..(slash + 1)];
                if (entries.Count == 0 || entries[^1].Name != common)
                {
                    entries.Add((common, true, common));
                }
            }
            else
            {
                entries.Add((key, false, key));
            }
        }

        var page = entries.Where(e => continuationToken is null || string.CompareOrdinal(e.Name, continuationToken) > 0).Take(maxKeys + 1).ToList();
        var truncated = page.Count > maxKeys;
        page = page.Take(maxKeys).ToList();
        var objects = Bucket(bucket);
        return Task.FromResult(new ObjectListing(
            prefix,
            page.Where(e => e.IsPrefix).Select(e => e.Name).ToList(),
            page.Where(e => !e.IsPrefix).Select(e => new RemoteObject(e.Key, objects[e.Key].Length, DateTimeOffset.UnixEpoch, "STANDARD", "\"etag\"")).ToList(),
            truncated ? page[^1].Name : null));
    }

    public async IAsyncEnumerable<RemoteObject> ListAllObjectsAsync(string bucket, string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Gate($"ListAll {bucket}/{prefix}");
        foreach (var (key, content) in Bucket(bucket).Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new RemoteObject(key, content.Length, DateTimeOffset.UnixEpoch, "STANDARD", "\"etag\"");
        }
    }

    public Task CreateBucketAsync(string bucket, string? region, CancellationToken cancellationToken)
    {
        Gate("CreateBucket " + bucket);
        _buckets.TryAdd(bucket, new SortedDictionary<string, byte[]>(StringComparer.Ordinal));
        return Task.CompletedTask;
    }

    public Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        Gate("DeleteBucket " + bucket);
        if (Bucket(bucket).Count > 0)
        {
            throw new StorageOperationException(new StorageError(StorageErrorKind.Conflict, "The bucket is not empty", "BucketNotEmpty", 409));
        }

        _buckets.Remove(bucket);
        return Task.CompletedTask;
    }

    public Task CreateFolderAsync(string bucket, string folderKey, CancellationToken cancellationToken)
    {
        Gate("CreateFolder " + folderKey);
        Put(bucket, folderKey);
        return Task.CompletedTask;
    }

    public Task UploadObjectAsync(string bucket, string key, string localPath, string? contentType, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        Gate("Upload " + key);
        var content = File.ReadAllBytes(localPath);
        progress?.Report(new TransferProgress(content.Length / 2, content.Length));
        Put(bucket, key, content);
        progress?.Report(new TransferProgress(content.Length, content.Length));
        return Task.CompletedTask;
    }

    public async Task DownloadObjectAsync(string bucket, string key, Stream destination, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        Gate("Download " + key);
        var content = Bucket(bucket).TryGetValue(key, out var c) ? c : throw new StorageOperationException(new StorageError(StorageErrorKind.NotFound, "No such key", "NoSuchKey", 404));
        var half = content.Length / 2;
        await destination.WriteAsync(content.AsMemory(0, half), cancellationToken);
        progress?.Report(new TransferProgress(half, content.Length));
        OnDownloadChunk?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        await destination.WriteAsync(content.AsMemory(half), cancellationToken);
        progress?.Report(new TransferProgress(content.Length, content.Length));
    }

    public Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        Gate("Delete " + key);
        if (FailDeleteKeys.Contains(key))
        {
            throw new StorageOperationException(new StorageError(StorageErrorKind.AccessDenied, "Access denied", "AccessDenied", 403));
        }

        Bucket(bucket).Remove(key);
        return Task.CompletedTask;
    }

    public Task<DeleteObjectsResult> DeleteObjectsAsync(string bucket, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        Gate($"DeleteObjects {keys.Count}");
        if (keys.Count > 1000)
        {
            throw new ArgumentException("too many keys");
        }

        var deleted = new List<string>();
        var failed = new List<(string, string)>();
        foreach (var key in keys)
        {
            if (FailDeleteKeys.Contains(key))
            {
                failed.Add((key, "AccessDenied: Access denied"));
            }
            else
            {
                Bucket(bucket).Remove(key);
                deleted.Add(key);
            }
        }

        return Task.FromResult(new DeleteObjectsResult(deleted, failed));
    }

    public Task CopyObjectAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, string? storageClass, CancellationToken cancellationToken)
    {
        Gate($"Copy {sourceKey} -> {destinationKey}");
        if (FailCopyKeys.Contains(sourceKey))
        {
            throw new StorageOperationException(new StorageError(StorageErrorKind.AccessDenied, "Access denied", "AccessDenied", 403));
        }

        var content = Bucket(sourceBucket).TryGetValue(sourceKey, out var c) ? c : throw new StorageOperationException(new StorageError(StorageErrorKind.NotFound, "No such key"));
        Put(destinationBucket, destinationKey, content);
        return Task.CompletedTask;
    }

    public Task<ObjectMetadata> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        Gate("Head " + key);
        var content = Bucket(bucket).TryGetValue(key, out var c) ? c : throw new StorageOperationException(new StorageError(StorageErrorKind.NotFound, "No such key"));
        return Task.FromResult(new ObjectMetadata(key, content.Length, DateTimeOffset.UnixEpoch, "\"etag\"", "text/plain", "STANDARD", null, null,
            [new Header("Content-Type", "text/plain")], [new Header("x-amz-meta-owner", "tests")]));
    }

    public Task<byte[]> ReadObjectStartAsync(string bucket, string key, int maxBytes, CancellationToken cancellationToken)
    {
        Gate("Range " + key);
        var content = Bucket(bucket)[key];
        return Task.FromResult(content.Take(maxBytes).ToArray());
    }

    public Task<AclInfo> GetAclAsync(string bucket, string? key, CancellationToken cancellationToken)
    {
        Gate("Acl " + key);
        return Task.FromResult(Acl);
    }

    public Task<IReadOnlyList<Tag>> GetTagsAsync(string bucket, string? key, CancellationToken cancellationToken)
    {
        Gate("Tags " + key);
        return Task.FromResult(TagsByKey.TryGetValue(key ?? string.Empty, out var t) ? t : (IReadOnlyList<Tag>)[]);
    }

    public Task SetTagsAsync(string bucket, string? key, IReadOnlyList<Tag> tags, CancellationToken cancellationToken)
    {
        Gate("SetTags " + key);
        TagsByKey[key ?? string.Empty] = tags.ToList();
        return Task.CompletedTask;
    }

    public Task<VersioningStatus> GetVersioningAsync(string bucket, CancellationToken cancellationToken)
    {
        Gate("Versioning");
        return Task.FromResult(Versioning);
    }

    public Task SetVersioningAsync(string bucket, VersioningStatus status, CancellationToken cancellationToken)
    {
        Gate("SetVersioning");
        Versioning = status;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ObjectVersion>> ListVersionsAsync(string bucket, string key, int max, CancellationToken cancellationToken)
    {
        Gate("Versions " + key);
        return Task.FromResult<IReadOnlyList<ObjectVersion>>([new ObjectVersion("v2", DateTimeOffset.UnixEpoch, 2, "STANDARD", true, false), new ObjectVersion("v1", DateTimeOffset.UnixEpoch, 1, "STANDARD", false, false)]);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Factory returning prepared clients; records what it was asked for.</summary>
internal sealed class FakeClientFactory(Func<ConnectionProfile, StorageCredentials?, IObjectStorageClient>? create = null) : IObjectStorageClientFactory
{
    public List<(ConnectionProfile Profile, StorageCredentials? Credentials)> Created { get; } = [];

    public IObjectStorageClient Create(ConnectionProfile profile, StorageCredentials? credentials)
    {
        Created.Add((profile, credentials));
        return create?.Invoke(profile, credentials) ?? new InMemoryObjectStorageClient(profile);
    }
}
