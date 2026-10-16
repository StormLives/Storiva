using Storiva.Core.Storage;

namespace Storiva.Core.Remote;

/// <summary>
/// <see cref="IStorageCatalog"/> over a real object store: an in-memory cache of what has been listed so far, filled
/// by <see cref="LoadAsync"/> page by page (never the whole bucket at once). The members of the interface only read
/// the cache, so navigation, tree, search, filter and sort work on it directly. Every change goes through
/// <see cref="IObjectStorageClient"/> asynchronously.
/// </summary>
public sealed class RemoteStorageCatalog : IStorageCatalog
{
    public const int DefaultPageSize = 1000;
    private const int MaxBucketPages = 20;
    private const string BlankName = "(blank name)";

    private readonly IObjectStorageClient _client;
    private readonly int _pageSize;
    private readonly Dictionary<StoragePath, Listing> _listings = new();
    private readonly Dictionary<StoragePath, string> _prefixes = new();
    private readonly Dictionary<string, StorageItem> _externalBuckets = new(StringComparer.Ordinal);
    private Listing? _buckets;

    public RemoteStorageCatalog(IObjectStorageClient client, int pageSize = DefaultPageSize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 1000);
        _pageSize = pageSize;
    }

    public IObjectStorageClient Client => _client;

    public string SourceDescription => $"Connection \"{_client.Profile.Name}\" ({_client.Profile.Describe()})";

    public IReadOnlyList<string> Regions => _client.Regions;

    public IReadOnlyList<string> StorageClasses => _client.StorageClasses;

    /// <summary>True once <paramref name="path"/> has at least one page in the cache.</summary>
    public bool IsLoaded(StoragePath path) => path.IsRoot ? _buckets is not null : _listings.ContainsKey(path);

    /// <summary>True when the server reported more entries than have been loaded for <paramref name="path"/>.</summary>
    public bool HasMore(StoragePath path) => (path.IsRoot ? _buckets : _listings.GetValueOrDefault(path))?.Token is not null;

    public int LoadedCount(StoragePath path) => (path.IsRoot ? _buckets : _listings.GetValueOrDefault(path))?.Items.Count ?? 0;

    /// <summary>Root, known buckets and any prefix inside a known bucket (S3 prefixes exist virtually).
    /// Locations known to be files are not navigable.</summary>
    public bool Exists(StoragePath path)
    {
        if (path.IsRoot)
        {
            return true;
        }

        if (FindBucket(path.Bucket!) is null)
        {
            return false;
        }

        if (path.IsBucket || path.Parent is not { } parent || !_listings.TryGetValue(parent, out var listing))
        {
            return true;
        }

        // A loaded parent listing settles a name that is only a file; anything else may be on a later page or
        // be an empty prefix, which S3 treats as an empty folder.
        var matches = listing.Items.Where(i => i.Path == path).ToList();
        return matches.Count == 0 || matches.Any(i => i.IsContainer);
    }

    public StorageItem? Find(StoragePath path)
    {
        if (path.IsRoot)
        {
            return null;
        }

        if (path.IsBucket)
        {
            return FindBucket(path.Name);
        }

        return path.Parent is { } parent && _listings.TryGetValue(parent, out var listing)
            ? listing.Items.FirstOrDefault(i => i.Path == path && i.IsContainer) ?? listing.Items.FirstOrDefault(i => i.Path == path)
            : null;
    }

    /// <summary>The server decides permissions; nothing is read-only on the client side.</summary>
    public bool IsReadOnly(StoragePath path) => false;

    /// <summary>Cached entries for <paramref name="path"/>; empty until <see cref="LoadAsync"/> ran for it.</summary>
    public IReadOnlyList<StorageItem> GetChildren(StoragePath path)
    {
        if (path.IsRoot)
        {
            var buckets = _buckets?.Items ?? [];
            return buckets.Concat(_externalBuckets.Values.Where(e => buckets.All(b => b.Name != e.Name))).ToList();
        }

        return _listings.TryGetValue(path, out var listing) ? listing.Items : [];
    }

    public bool MayHaveChildContainers(StoragePath path) =>
        !IsLoaded(path) || GetChildren(path).Any(c => c.IsContainer) || HasMore(path);

    /// <summary>Exact prefix of a location: "" for a bucket, "a/b/" for a folder (the server's own key when listed).</summary>
    public string PrefixOf(StoragePath path)
    {
        if (path.Segments.Count <= 1)
        {
            return string.Empty;
        }

        return _prefixes.TryGetValue(path, out var prefix) ? prefix : string.Join('/', path.Segments.Skip(1)) + "/";
    }

    /// <summary>Exact key of an item (folders: prefix ending with "/").</summary>
    public string KeyOf(StorageItem item) => item.Key ?? (item.IsContainer ? PrefixOf(item.Path) : string.Join('/', item.Path.Segments.Skip(1)));

    /// <summary>
    /// Loads the first page of <paramref name="path"/> (or, with <paramref name="nextPage"/>, the next one).
    /// Returns the number of entries added. The root loads every page of the bucket list (bounded).
    /// </summary>
    public async Task<int> LoadAsync(StoragePath path, bool nextPage, CancellationToken cancellationToken)
    {
        if (path.IsRoot)
        {
            return await LoadBucketsAsync(cancellationToken).ConfigureAwait(true);
        }

        var bucket = path.Bucket!;
        var prefix = PrefixOf(path);
        string? token = null;
        if (nextPage && _listings.TryGetValue(path, out var existing))
        {
            if (existing.Token is null)
            {
                return 0;
            }

            token = existing.Token;
        }

        var page = await _client.ListObjectsAsync(bucket, prefix, token, _pageSize, cancellationToken).ConfigureAwait(true);
        var listing = nextPage && _listings.TryGetValue(path, out var current) ? current : new Listing();
        var before = listing.Items.Count;
        AddPage(path, listing, page);

        // Re-opening after a change: read the pages that were open before (at most MaxReloadPages more).
        if (!nextPage && _reloadTargets.Remove(path, out var target))
        {
            for (var extra = 0; extra < MaxReloadPages && listing.Token is { } next && listing.Items.Count < target; extra++)
            {
                AddPage(path, listing, await _client.ListObjectsAsync(bucket, prefix, next, _pageSize, cancellationToken).ConfigureAwait(true));
            }
        }

        _listings[path] = listing;
        return listing.Items.Count - before;
    }

    /// <summary>
    /// Forgets the cached listing of <paramref name="path"/> (and everything below it). The number of entries that
    /// were loaded is remembered, so the next <see cref="LoadAsync"/> re-reads as many pages as the user had already
    /// opened (bounded), instead of collapsing the view to the first page after every change.
    /// </summary>
    public void Invalidate(StoragePath path)
    {
        if (path.IsRoot)
        {
            _buckets = null;
        }

        foreach (var key in _listings.Keys.Where(k => k.IsWithin(path)).ToList())
        {
            if (_listings[key].Items.Count > _pageSize)
            {
                _reloadTargets[key] = _listings[key].Items.Count;
            }

            _listings.Remove(key);
        }
    }

    private const int MaxReloadPages = 10;
    private readonly Dictionary<StoragePath, int> _reloadTargets = new();

    public void InvalidateAll()
    {
        _buckets = null;
        _listings.Clear();
        _prefixes.Clear();
        _reloadTargets.Clear();
    }

    /// <summary>Remembers a bucket the user can reach although it is not in the bucket list (e.g. another account's).</summary>
    public StorageItem AddExternalBucket(string name, string? region)
    {
        var item = new StorageItem(StoragePath.Root.Append(name), StorageItemKind.Bucket, null, null, null, region, IsExternal: true);
        _externalBuckets[name] = item;
        return item;
    }

    public bool RemoveExternalBucket(string name) => _externalBuckets.Remove(name);

    /// <summary>Removes a bucket from the cache after it was deleted on the server.</summary>
    public void ForgetBucket(string name)
    {
        _externalBuckets.Remove(name);
        _buckets?.Items.RemoveAll(b => b.Name == name);
        Invalidate(StoragePath.Root.Append(name));
    }

    public string? RegionOf(string bucket) => FindBucket(bucket)?.Region;

    private StorageItem? FindBucket(string name) =>
        _buckets?.Items.FirstOrDefault(b => b.Name == name) ?? _externalBuckets.GetValueOrDefault(name);

    private async Task<int> LoadBucketsAsync(CancellationToken cancellationToken)
    {
        var listing = new Listing();
        string? token = null;
        for (var page = 0; page < MaxBucketPages; page++)
        {
            var result = await _client.ListBucketsAsync(token, cancellationToken).ConfigureAwait(true);
            foreach (var bucket in result.Buckets)
            {
                if (bucket.Name.Length > 0 && !bucket.Name.Contains('/') && listing.Items.All(b => b.Name != bucket.Name))
                {
                    listing.Items.Add(new StorageItem(StoragePath.Root.Append(bucket.Name), StorageItemKind.Bucket, null, bucket.CreatedAt, null, bucket.Region));
                }
            }

            token = result.ContinuationToken;
            if (token is null)
            {
                break;
            }
        }

        listing.Token = token; // non-null only when the bound was reached
        _buckets = listing;
        return listing.Items.Count;
    }

    private void AddPage(StoragePath path, Listing listing, ObjectListing page)
    {
        var used = listing.Items.Select(i => (i.Name, i.IsContainer)).ToHashSet();
        foreach (var commonPrefix in page.CommonPrefixes)
        {
            if (!commonPrefix.StartsWith(page.Prefix, StringComparison.Ordinal) || commonPrefix.Length <= page.Prefix.Length)
            {
                continue;
            }

            var name = UniqueName(DisplayName(commonPrefix[page.Prefix.Length..].TrimEnd('/')), used, container: true);
            var child = path.Append(name);
            _prefixes[child] = commonPrefix;
            listing.Items.Add(new StorageItem(child, StorageItemKind.Folder, null, null, null, Key: commonPrefix));
        }

        foreach (var obj in page.Objects)
        {
            // The zero-byte marker of the folder itself ("photos/" when listing "photos/") is not an entry.
            if (obj.Key.Length <= page.Prefix.Length || !obj.Key.StartsWith(page.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = UniqueName(DisplayName(obj.Key[page.Prefix.Length..]), used, container: false);
            listing.Items.Add(new StorageItem(path.Append(name), StorageItemKind.File, obj.Size, obj.LastModified, obj.StorageClass, Key: obj.Key, ETag: obj.ETag));
        }

        listing.Token = page.ContinuationToken;
    }

    /// <summary>Display names must be non-empty and free of "/" (a path segment); the exact key is kept separately.</summary>
    private static string DisplayName(string raw) => raw.Length == 0 ? BlankName : raw.Replace('/', '∕');

    private static string UniqueName(string name, HashSet<(string, bool)> used, bool container)
    {
        var candidate = name;
        for (var i = 2; !used.Add((candidate, container)); i++)
        {
            candidate = $"{name} #{i}";
        }

        return candidate;
    }

    /// <summary>Clears the cache (it never changes the server).</summary>
    public void Reset() => InvalidateAll();

    private sealed class Listing
    {
        public List<StorageItem> Items { get; } = [];

        public string? Token { get; set; }
    }
}
