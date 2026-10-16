using Storiva.Core.Details;
using Storiva.Core.Storage;

namespace Storiva.Core.Remote;

/// <summary>Information a lower tab needs; each one is fetched only when its tab is shown.</summary>
[Flags]
public enum DetailsAspect
{
    None = 0,
    Acl = 1,
    Metadata = 2,
    Tags = 4,
    Versions = 8,
    Preview = 16,
}

/// <summary>
/// <see cref="IObjectDetailsStore"/> for a real connection: a cache filled by <see cref="LoadAsync"/>. Absent
/// values are reported as absent, never invented. Changes are explicit asynchronous calls
/// (<see cref="SaveTagsAsync"/>, <see cref="SetVersioningAsync"/>).
/// </summary>
public sealed class RemoteDetailsStore : IObjectDetailsStore
{
    public const string AllUsersUri = "http://acs.amazonaws.com/groups/global/AllUsers";
    public const string AuthenticatedUsersUri = "http://acs.amazonaws.com/groups/global/AuthenticatedUsers";
    public const int MaxVersions = 200;

    private readonly IObjectStorageClient _client;
    private readonly RemoteStorageCatalog _catalog;
    private readonly Dictionary<StoragePath, Entry> _entries = new();
    private readonly Dictionary<string, VersioningStatus> _versioning = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StorageError> _versioningErrors = new(StringComparer.Ordinal);

    public RemoteDetailsStore(IObjectStorageClient client, RemoteStorageCatalog catalog)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>Which aspects make sense for an item (folders are prefixes: no ACL, headers, tags or versions).</summary>
    public static DetailsAspect Applicable(StorageItem item) => item.Kind switch
    {
        StorageItemKind.File => DetailsAspect.Acl | DetailsAspect.Metadata | DetailsAspect.Tags | DetailsAspect.Versions | DetailsAspect.Preview,
        StorageItemKind.Bucket => DetailsAspect.Acl | DetailsAspect.Metadata | DetailsAspect.Tags | DetailsAspect.Versions,
        _ => DetailsAspect.None,
    };

    public bool IsLoaded(StoragePath path, DetailsAspect aspect) =>
        aspect == DetailsAspect.None || _entries.TryGetValue(path, out var e) && (e.Loaded & aspect) == aspect;

    /// <summary>Null when the aspect is ready; otherwise "Loading…" or the reason it is unavailable.</summary>
    public string? StateMessage(StoragePath path, DetailsAspect aspect)
    {
        if (aspect == DetailsAspect.None || IsLoaded(path, aspect))
        {
            return null;
        }

        if (_entries.TryGetValue(path, out var entry) && entry.Errors.TryGetValue(aspect, out var error))
        {
            return error.Kind switch
            {
                StorageErrorKind.AccessDenied => $"Access denied: these credentials may not read this information. {error.Describe()}",
                StorageErrorKind.NotSupported => $"This server does not support it. {error.Describe()}",
                _ => $"{StorageError.Title(error.Kind)}: {error.Describe()}",
            };
        }

        return "Loading from the server…";
    }

    /// <summary>Fetches <paramref name="aspect"/> for <paramref name="item"/> unless already cached. Failures are kept
    /// (see <see cref="StateMessage"/>) instead of thrown, except cancellation.</summary>
    public async Task LoadAsync(StorageItem item, DetailsAspect aspect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        aspect &= Applicable(item);
        foreach (var single in Enum.GetValues<DetailsAspect>().Where(a => a != DetailsAspect.None && aspect.HasFlag(a)))
        {
            if (IsLoaded(item.Path, single))
            {
                continue;
            }

            var entry = EntryFor(item.Path);
            entry.Errors.Remove(single);
            try
            {
                await LoadOneAsync(item, entry, single, cancellationToken).ConfigureAwait(true);
                entry.Loaded |= single;
            }
            catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
            {
                entry.Errors[single] = ex.Error;
            }
        }
    }

    private async Task LoadOneAsync(StorageItem item, Entry entry, DetailsAspect aspect, CancellationToken ct)
    {
        var bucket = item.Path.Bucket!;
        var key = item.Kind == StorageItemKind.File ? _catalog.KeyOf(item) : null;
        switch (aspect)
        {
            case DetailsAspect.Acl:
                entry.Acl = await _client.GetAclAsync(bucket, key, ct).ConfigureAwait(true);
                break;
            case DetailsAspect.Metadata:
                if (key is not null)
                {
                    entry.Metadata = await _client.GetObjectMetadataAsync(bucket, key, ct).ConfigureAwait(true);
                }
                else
                {
                    await EnsureVersioningAsync(bucket, ct).ConfigureAwait(true);
                }

                break;
            case DetailsAspect.Tags:
                entry.Tags = await _client.GetTagsAsync(bucket, key, ct).ConfigureAwait(true);
                break;
            case DetailsAspect.Versions:
                var status = await EnsureVersioningAsync(bucket, ct).ConfigureAwait(true);
                entry.Versions = key is not null && status != VersioningStatus.Disabled
                    ? await _client.ListVersionsAsync(bucket, key, MaxVersions, ct).ConfigureAwait(true)
                    : [];
                break;
            case DetailsAspect.Preview:
                entry.Preview = await _client.ReadObjectStartAsync(bucket, key!, PreviewFormatter.SampleBytes, ct).ConfigureAwait(true);
                break;
        }
    }

    private async Task<VersioningStatus> EnsureVersioningAsync(string bucket, CancellationToken ct)
    {
        if (_versioning.TryGetValue(bucket, out var known))
        {
            return known;
        }

        try
        {
            var status = await _client.GetVersioningAsync(bucket, ct).ConfigureAwait(true);
            _versioning[bucket] = status;
            _versioningErrors.Remove(bucket);
            return status;
        }
        catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
        {
            _versioningErrors[bucket] = ex.Error;
            throw;
        }
    }

    /// <summary>The cached HEAD result of a file, if loaded.</summary>
    public ObjectMetadata? GetMetadata(StoragePath file) => _entries.GetValueOrDefault(file)?.Metadata;

    /// <summary>Known versioning state of a bucket; null when not (yet) read or not readable.</summary>
    public VersioningStatus? KnownVersioning(string bucket) => _versioning.TryGetValue(bucket, out var s) ? s : null;

    /// <summary>Grants that do not fit the three rows of the Permissions grid (other accounts, log delivery…).</summary>
    public IReadOnlyList<RemoteGrant> OtherGrants(StoragePath path) =>
        _entries.GetValueOrDefault(path)?.Acl is { } acl ? acl.Grants.Where(g => Classify(g, acl) is null).ToList() : [];

    public string? OwnerName(StoragePath path) => _entries.GetValueOrDefault(path)?.Acl is { } acl ? acl.OwnerName ?? acl.OwnerId : null;

    // ---------- IObjectDetailsStore (reads from the cache) ----------

    public IReadOnlyList<Grant> GetGrants(StoragePath path)
    {
        if (_entries.GetValueOrDefault(path)?.Acl is not { } acl)
        {
            return [];
        }

        var map = new Dictionary<Grantee, Permission> { [Grantee.Owner] = Permission.None, [Grantee.AuthenticatedUsers] = Permission.None, [Grantee.AllUsers] = Permission.None };
        foreach (var grant in acl.Grants)
        {
            if (Classify(grant, acl) is { } grantee)
            {
                map[grantee] |= ToPermission(grant.Permission);
            }
        }

        return map.Select(p => new Grant(p.Key, p.Value)).ToList();
    }

    public IReadOnlyList<Header> GetHeaders(StoragePath file) =>
        _entries.GetValueOrDefault(file)?.Metadata is { } meta
            ? meta.Headers.Concat(meta.UserMetadata).ToList()
            : [];

    public IReadOnlyList<Tag> GetTags(StoragePath path) => _entries.GetValueOrDefault(path)?.Tags ?? [];

    public VersioningStatus GetVersioning(StoragePath bucket) =>
        bucket.Bucket is { } name && _versioning.TryGetValue(name, out var status) ? status : VersioningStatus.Disabled;

    public IReadOnlyList<ObjectVersion> GetVersions(StoragePath file) => _entries.GetValueOrDefault(file)?.Versions ?? [];

    public byte[] GetContentSample(StorageItem file, int maxBytes)
    {
        var sample = _entries.GetValueOrDefault(file.Path)?.Preview ?? [];
        return sample.Length <= maxBytes ? sample : sample[..maxBytes];
    }

    public string GetETag(StorageItem file) => GetMetadata(file.Path)?.ETag ?? file.ETag ?? "—";

    // ---------- explicit remote changes ----------

    /// <summary>Replaces the tags on the server (PutObjectTagging / PutBucketTagging), then updates the cache.</summary>
    public async Task SaveTagsAsync(StorageItem item, IReadOnlyList<Tag> tags, CancellationToken cancellationToken)
    {
        var key = item.Kind == StorageItemKind.File ? _catalog.KeyOf(item) : null;
        await _client.SetTagsAsync(item.Path.Bucket!, key, tags, cancellationToken).ConfigureAwait(true);
        var entry = EntryFor(item.Path);
        entry.Tags = tags.ToList();
        entry.Loaded |= DetailsAspect.Tags;
    }

    public async Task SetVersioningAsync(string bucket, VersioningStatus status, CancellationToken cancellationToken)
    {
        await _client.SetVersioningAsync(bucket, status, cancellationToken).ConfigureAwait(true);
        _versioning[bucket] = status;
        foreach (var path in _entries.Keys.Where(p => p.Bucket == bucket).ToList())
        {
            _entries[path].Loaded &= ~DetailsAspect.Versions;
        }
    }

    /// <summary>Drops cached details of <paramref name="path"/> and everything below it.</summary>
    public void Invalidate(StoragePath path)
    {
        foreach (var key in _entries.Keys.Where(k => k.IsWithin(path)).ToList())
        {
            _entries.Remove(key);
        }

        if (path.IsBucket || path.IsRoot)
        {
            foreach (var bucket in _versioning.Keys.Where(b => path.IsRoot || b == path.Bucket).ToList())
            {
                _versioning.Remove(bucket);
            }
        }
    }

    public void Reset()
    {
        _entries.Clear();
        _versioning.Clear();
        _versioningErrors.Clear();
    }

    private Entry EntryFor(StoragePath path)
    {
        if (!_entries.TryGetValue(path, out var entry))
        {
            entry = new Entry();
            _entries[path] = entry;
        }

        return entry;
    }

    private static Grantee? Classify(RemoteGrant grant, AclInfo acl) => grant.Type switch
    {
        RemoteGranteeType.Group when grant.Grantee == AllUsersUri => Grantee.AllUsers,
        RemoteGranteeType.Group when grant.Grantee == AuthenticatedUsersUri => Grantee.AuthenticatedUsers,
        RemoteGranteeType.CanonicalUser when acl.OwnerId is not null && grant.Grantee == acl.OwnerId => Grantee.Owner,
        _ => null,
    };

    public static Permission ToPermission(string permission) => permission.ToUpperInvariant() switch
    {
        "FULL_CONTROL" => Permission.FullControl,
        "READ" => Permission.Read,
        "WRITE" => Permission.Write,
        "READ_ACP" => Permission.ReadAcl,
        "WRITE_ACP" => Permission.WriteAcl,
        _ => Permission.None,
    };

    private sealed class Entry
    {
        public DetailsAspect Loaded { get; set; }

        public Dictionary<DetailsAspect, StorageError> Errors { get; } = new();

        public AclInfo? Acl { get; set; }

        public ObjectMetadata? Metadata { get; set; }

        public IReadOnlyList<Tag>? Tags { get; set; }

        public IReadOnlyList<ObjectVersion>? Versions { get; set; }

        public byte[]? Preview { get; set; }
    }
}

/// <summary>Details store whose implementation is swapped when a connection opens or closes.</summary>
public sealed class SwitchableDetailsStore : IObjectDetailsStore
{
    public SwitchableDetailsStore(IObjectDetailsStore inner) => Inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public IObjectDetailsStore Inner { get; set; }

    public IReadOnlyList<Grant> GetGrants(StoragePath path) => Inner.GetGrants(path);

    public IReadOnlyList<Header> GetHeaders(StoragePath file) => Inner.GetHeaders(file);

    public IReadOnlyList<Tag> GetTags(StoragePath path) => Inner.GetTags(path);

    public VersioningStatus GetVersioning(StoragePath bucket) => Inner.GetVersioning(bucket);

    public IReadOnlyList<ObjectVersion> GetVersions(StoragePath file) => Inner.GetVersions(file);

    public byte[] GetContentSample(StorageItem file, int maxBytes) => Inner.GetContentSample(file, maxBytes);

    public string GetETag(StorageItem file) => Inner.GetETag(file);
}
