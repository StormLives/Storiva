using System.Text;
using Storiva.Core.Details;
using Storiva.Core.Remote;
using Storiva.Core.Storage;
using Storiva.Tests.Fakes;

namespace Storiva.Tests;

public class RemoteStorageCatalogTests
{
    private static async Task<(RemoteStorageCatalog Catalog, InMemoryObjectStorageClient Client)> CreateAsync(int pageSize = 1000)
    {
        var client = new InMemoryObjectStorageClient();
        client.Put("photos", "2026/a.jpg", [1]);
        client.Put("photos", "2026/b.jpg", [1, 2]);
        client.Put("photos", "2026/", []);                // folder marker
        client.Put("photos", "readme.txt", Encoding.UTF8.GetBytes("hi"));
        client.Put("photos", "odd//double.txt", []);      // empty segment
        client.Put("photos", "a b+c%d.txt", []);          // unusual characters
        client.Put("logs", "x.log", []);
        var catalog = new RemoteStorageCatalog(client, pageSize);
        await catalog.LoadAsync(StoragePath.Root, nextPage: false, CancellationToken.None);
        return (catalog, client);
    }

    [Fact]
    public async Task Root_lists_the_buckets_from_the_server()
    {
        var (catalog, _) = await CreateAsync();
        Assert.Equal(["logs", "photos"], catalog.GetChildren(StoragePath.Root).Select(b => b.Name));
        Assert.True(catalog.Exists(StoragePath.Parse("photos")));
        Assert.False(catalog.Exists(StoragePath.Parse("unknown-bucket")));
    }

    [Fact]
    public async Task Listing_uses_prefixes_and_keeps_exact_keys()
    {
        var (catalog, _) = await CreateAsync();
        var bucket = StoragePath.Parse("photos");
        Assert.False(catalog.IsLoaded(bucket));
        Assert.Empty(catalog.GetChildren(bucket)); // nothing invented before the server answered
        await catalog.LoadAsync(bucket, false, CancellationToken.None);

        var items = catalog.GetChildren(bucket);
        Assert.Equal(["2026", "odd", "a b+c%d.txt", "readme.txt"], items.Select(i => i.Name));
        Assert.Equal("2026/", items[0].Key);
        Assert.Equal(StorageItemKind.Folder, items[0].Kind);
        Assert.Equal("a b+c%d.txt", catalog.KeyOf(items[2]));

        var odd = StoragePath.Parse("photos/odd");
        await catalog.LoadAsync(odd, false, CancellationToken.None);
        var blank = Assert.Single(catalog.GetChildren(odd));
        Assert.Equal("(blank name)", blank.Name);  // "odd//double.txt": a folder with an empty name
        Assert.Equal("odd//", blank.Key);
        Assert.Equal("odd//", catalog.PrefixOf(blank.Path));
    }

    [Fact]
    public async Task The_folder_marker_is_not_listed_as_a_file()
    {
        var (catalog, _) = await CreateAsync();
        var folder = StoragePath.Parse("photos/2026");
        await catalog.LoadAsync(folder, false, CancellationToken.None);
        Assert.Equal(["a.jpg", "b.jpg"], catalog.GetChildren(folder).Select(i => i.Name));
    }

    [Fact]
    public async Task Pages_are_loaded_one_at_a_time()
    {
        var (catalog, client) = await CreateAsync(pageSize: 2);
        var bucket = StoragePath.Parse("photos");
        await catalog.LoadAsync(bucket, false, CancellationToken.None);
        Assert.Equal(2, catalog.LoadedCount(bucket));
        Assert.True(catalog.HasMore(bucket));
        await catalog.LoadAsync(bucket, true, CancellationToken.None);
        Assert.Equal(4, catalog.LoadedCount(bucket));
        Assert.False(catalog.HasMore(bucket));
        Assert.Equal(0, await catalog.LoadAsync(bucket, true, CancellationToken.None));
        Assert.Equal(2, client.Calls.Count(c => c == "List photos/"));
    }

    [Fact]
    public async Task A_refresh_after_a_change_reloads_the_pages_that_were_open()
    {
        var (catalog, client) = await CreateAsync(pageSize: 2);
        var bucket = StoragePath.Parse("photos");
        await catalog.LoadAsync(bucket, false, CancellationToken.None);
        await catalog.LoadAsync(bucket, true, CancellationToken.None);
        client.Put("photos", "zzz-new.txt", []);
        catalog.Invalidate(bucket);
        await catalog.LoadAsync(bucket, false, CancellationToken.None);
        Assert.True(catalog.LoadedCount(bucket) >= 4, $"{catalog.LoadedCount(bucket)} loaded");

        catalog.Invalidate(bucket); // only one page needed now? no: 4+ items were open, so pages are re-read again
        catalog.InvalidateAll();
        await catalog.LoadAsync(StoragePath.Root, false, CancellationToken.None);
        await catalog.LoadAsync(bucket, false, CancellationToken.None);
        Assert.Equal(2, catalog.LoadedCount(bucket)); // a full reset starts from the first page
    }

    [Fact]
    public async Task Files_are_not_navigable_and_unknown_prefixes_are()
    {
        var (catalog, _) = await CreateAsync();
        await catalog.LoadAsync(StoragePath.Parse("photos"), false, CancellationToken.None);
        Assert.False(catalog.Exists(StoragePath.Parse("photos/readme.txt")));
        Assert.True(catalog.Exists(StoragePath.Parse("photos/2026")));
        Assert.True(catalog.Exists(StoragePath.Parse("photos/not-listed-yet")));
        Assert.Equal(StorageItemKind.File, catalog.Find(StoragePath.Parse("photos/readme.txt"))!.Kind);
    }

    [Fact]
    public async Task Invalidate_forgets_a_subtree_and_writes_are_async_only()
    {
        var (catalog, _) = await CreateAsync();
        var bucket = StoragePath.Parse("photos");
        await catalog.LoadAsync(bucket, false, CancellationToken.None);
        await catalog.LoadAsync(StoragePath.Parse("photos/2026"), false, CancellationToken.None);
        catalog.Invalidate(bucket);
        Assert.False(catalog.IsLoaded(bucket));
        Assert.False(catalog.IsLoaded(StoragePath.Parse("photos/2026")));
        Assert.True(catalog.IsLoaded(StoragePath.Root));
        Assert.True(catalog.MayHaveChildContainers(bucket)); // unknown until listed
        Assert.False(catalog.IsReadOnly(bucket));
    }

    [Fact]
    public async Task External_buckets_join_the_root_listing()
    {
        var (catalog, _) = await CreateAsync();
        catalog.AddExternalBucket("partner-data", "eu-west-1");
        Assert.Contains(catalog.GetChildren(StoragePath.Root), b => b.Name == "partner-data" && b.IsExternal);
        catalog.ForgetBucket("partner-data");
        Assert.DoesNotContain(catalog.GetChildren(StoragePath.Root), b => b.Name == "partner-data");
    }
}

public class RemoteDetailsStoreTests
{
    private static async Task<(RemoteDetailsStore Store, InMemoryObjectStorageClient Client, StorageItem File, StorageItem Bucket)> CreateAsync()
    {
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "notes.txt", Encoding.UTF8.GetBytes("hello from the server"));
        var catalog = new RemoteStorageCatalog(client);
        await catalog.LoadAsync(StoragePath.Root, false, CancellationToken.None);
        await catalog.LoadAsync(StoragePath.Parse("b"), false, CancellationToken.None);
        var file = catalog.GetChildren(StoragePath.Parse("b")).Single();
        return (new RemoteDetailsStore(client, catalog), client, file, catalog.Find(StoragePath.Parse("b"))!);
    }

    [Fact]
    public async Task Aspects_load_on_demand_and_report_their_state()
    {
        var (store, client, file, _) = await CreateAsync();
        Assert.Equal("Loading from the server…", store.StateMessage(file.Path, DetailsAspect.Metadata));
        Assert.Empty(store.GetHeaders(file.Path));
        Assert.DoesNotContain(client.Calls, c => c.StartsWith("Head", StringComparison.Ordinal));

        await store.LoadAsync(file, DetailsAspect.Metadata | DetailsAspect.Preview, CancellationToken.None);
        Assert.Null(store.StateMessage(file.Path, DetailsAspect.Metadata));
        Assert.Contains(store.GetHeaders(file.Path), h => h.Name == "Content-Type" && h.Value == "text/plain");
        Assert.Contains(store.GetHeaders(file.Path), h => h.Name == "x-amz-meta-owner");
        Assert.Equal("hello from the server", Encoding.UTF8.GetString(store.GetContentSample(file, 4096)));
        Assert.Equal("\"etag\"", store.GetETag(file));

        await store.LoadAsync(file, DetailsAspect.Metadata, CancellationToken.None);
        Assert.Single(client.Calls, c => c.StartsWith("Head", StringComparison.Ordinal)); // cached
    }

    [Fact]
    public async Task Acl_grants_map_to_the_three_rows_and_others_are_counted()
    {
        var (store, client, file, _) = await CreateAsync();
        client.Acl = new AclInfo("owner-1", "Owner Name",
        [
            new RemoteGrant(RemoteGranteeType.CanonicalUser, "owner-1", "FULL_CONTROL"),
            new RemoteGrant(RemoteGranteeType.Group, RemoteDetailsStore.AllUsersUri, "READ"),
            new RemoteGrant(RemoteGranteeType.CanonicalUser, "someone-else", "READ"),
        ]);
        await store.LoadAsync(file, DetailsAspect.Acl, CancellationToken.None);
        var grants = store.GetGrants(file.Path);
        Assert.Equal(Permission.FullControl, grants.Single(g => g.Grantee == Grantee.Owner).Permissions);
        Assert.Equal(Permission.Read, grants.Single(g => g.Grantee == Grantee.AllUsers).Permissions);
        Assert.Equal(Permission.None, grants.Single(g => g.Grantee == Grantee.AuthenticatedUsers).Permissions);
        Assert.Single(store.OtherGrants(file.Path));
        Assert.Equal("Owner Name", store.OwnerName(file.Path));
    }

    [Fact]
    public async Task Failures_are_kept_as_safe_messages()
    {
        var (store, client, file, _) = await CreateAsync();
        client.FailEverything = StorageErrorKind.AccessDenied;
        await store.LoadAsync(file, DetailsAspect.Tags, CancellationToken.None);
        Assert.StartsWith("Access denied", store.StateMessage(file.Path, DetailsAspect.Tags), StringComparison.Ordinal);
        Assert.Empty(store.GetTags(file.Path));
    }

    [Fact]
    public async Task Versions_are_only_listed_when_versioning_is_on()
    {
        var (store, client, file, bucket) = await CreateAsync();
        await store.LoadAsync(file, DetailsAspect.Versions, CancellationToken.None);
        Assert.Empty(store.GetVersions(file.Path));
        Assert.DoesNotContain(client.Calls, c => c.StartsWith("Versions", StringComparison.Ordinal));

        await store.SetVersioningAsync("b", VersioningStatus.Enabled, CancellationToken.None);
        await store.LoadAsync(file, DetailsAspect.Versions, CancellationToken.None);
        Assert.Equal(2, store.GetVersions(file.Path).Count);
        Assert.Equal(VersioningStatus.Enabled, store.GetVersioning(bucket.Path));
    }

    [Fact]
    public async Task Tags_are_saved_explicitly_on_the_server()
    {
        var (store, client, file, _) = await CreateAsync();
        await store.SaveTagsAsync(file, [new Tag("env", "test")], CancellationToken.None);
        Assert.Equal("test", client.TagsByKey["notes.txt"].Single().Value);
        Assert.Equal("env", store.GetTags(file.Path).Single().Key);
    }

    [Fact]
    public void Folders_have_no_object_details()
    {
        var folder = new StorageItem(StoragePath.Parse("b/dir"), StorageItemKind.Folder, null, null, null, Key: "dir/");
        Assert.Equal(DetailsAspect.None, RemoteDetailsStore.Applicable(folder));
        Assert.Null(new RemoteDetailsStore(new InMemoryObjectStorageClient(), new RemoteStorageCatalog(new InMemoryObjectStorageClient())).StateMessage(folder.Path, DetailsAspect.None));
    }
}

public class LocalPathMapperTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("..", "_")]
    [InlineData(".", "_")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("...", "_")]
    [InlineData(". .", "_")]
    [InlineData("CON .txt", "_CON .txt")]
    [InlineData("aux.tar.gz", "_aux.tar.gz")]
    [InlineData("com0", "_com0")]
    [InlineData("LPT\u00B9.log", "_LPT\u00B9.log")]
    [InlineData("CONOUT$", "_CONOUT$")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("COM10", "COM10")]
    [InlineData("C:", "C_")]
    [InlineData("\\\\server\\share", "__server_share")]
    [InlineData("tab\there\u0000", "tab_here_")]
    [InlineData("invoice\u202Efdp.exe", "invoice_fdp.exe")]
    [InlineData("a\u2066b\u2069c\u200F", "a_b_c_")]
    public void Names_are_made_safe_for_windows(string segment, string expected) => Assert.Equal(expected, LocalPathMapper.SafeName(segment));

    [Fact]
    public void Long_names_are_cut_without_trailing_dots_or_split_surrogates()
    {
        Assert.Equal(new string('a', 200), LocalPathMapper.SafeName(new string('a', 300)));
        Assert.Equal(new string('a', 198), LocalPathMapper.SafeName(new string('a', 198) + ". " + new string('b', 50)));

        var emoji = char.ConvertFromUtf32(0x1F600); // two UTF-16 units
        var cut = LocalPathMapper.SafeName(new string('a', 199) + emoji + "tail");
        Assert.Equal(new string('a', 199), cut);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Theory]
    [InlineData("/abs/path.txt")]
    [InlineData("C:/Windows/system32/x.dll")]
    [InlineData("\\\\?\\C:\\x")]
    [InlineData("a/./../b")]
    [InlineData(".../x")]
    [InlineData("NUL/x.txt")]
    public void Absolute_drive_and_device_keys_stay_inside_the_download_folder(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "storiva-root");
        var path = LocalPathMapper.ToLocalPath(root, key);
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        Assert.StartsWith(fullRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.All(path[fullRoot.Length..].Split(Path.DirectorySeparatorChar), segment =>
        {
            Assert.NotEqual("..", segment);
            Assert.DoesNotContain(':', segment);
        });
    }

    [Theory]
    [InlineData("../../evil.exe")]
    [InlineData("a/../../b")]
    [InlineData("..\\..\\x")]
    public void Keys_never_escape_the_download_folder(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "storiva-root");
        var path = LocalPathMapper.ToLocalPath(root, key);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Folders_are_kept_and_empty_segments_dropped()
    {
        var root = Path.Combine(Path.GetTempPath(), "storiva-root");
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "a", "b", "c.txt"), LocalPathMapper.ToLocalPath(root, "a//b/c.txt"));
        Assert.Throws<ArgumentException>(() => LocalPathMapper.ToLocalPath(root, "///"));
    }

    [Fact]
    public void Unique_names_add_a_counter()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\d\r.pdf", @"C:\d\r (1).pdf" };
        Assert.Equal(@"C:\d\r (2).pdf", LocalPathMapper.UniquePath(@"C:\d\r.pdf", existing.Contains));
    }

    [Fact]
    public void Upload_keys_use_forward_slashes_below_the_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "up");
        Assert.Equal("docs/up/sub/f.txt", LocalPathMapper.ToKey("docs/up/", root, Path.Combine(root, "sub", "f.txt")));
        Assert.Throws<ArgumentException>(() => LocalPathMapper.ToKey("x/", root, Path.Combine(Path.GetTempPath(), "other.txt")));
        Assert.Throws<ArgumentException>(() => LocalPathMapper.ToKey("x/", root, Path.Combine(root, "..", "up-sibling.txt")));
        Assert.Throws<ArgumentException>(() => LocalPathMapper.ToKey("x/", root, @"Z:\elsewhere\f.txt"));
        Assert.Equal("x/..notes.txt", LocalPathMapper.ToKey("x/", root, Path.Combine(root, "..notes.txt")));
    }
}
