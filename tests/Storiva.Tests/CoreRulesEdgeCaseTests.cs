using Storiva.Core.Listing;
using Storiva.Core.Storage;

namespace Storiva.Tests;

/// <summary>Edge cases of the pure Core rules (paths, names, list queries) that the feature tests do not reach.</summary>
public class StoragePathEdgeCaseTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("/")]
    [InlineData("///")]
    public void Empty_inputs_parse_to_the_root(string? text) => Assert.Same(StoragePath.Root, StoragePath.Parse(text));

    [Fact]
    public void A_single_segment_is_a_bucket_whose_parent_is_the_root()
    {
        var bucket = StoragePath.Parse("/photos/");
        Assert.True(bucket.IsBucket);
        Assert.False(bucket.IsRoot);
        Assert.Equal("photos", bucket.Name);
        Assert.Equal("photos", bucket.Bucket);
        Assert.Equal(StoragePath.Root, bucket.Parent);
        Assert.Equal("photos", bucket.ToString());
        Assert.Equal(string.Empty, StoragePath.Root.ToString());
        Assert.Equal(string.Empty, StoragePath.Root.Name);
        Assert.Null(StoragePath.Root.Bucket);
    }

    [Fact]
    public void Within_compares_whole_segments_case_sensitively()
    {
        var path = StoragePath.Parse("team-assets/images");
        Assert.True(path.IsWithin(StoragePath.Root));
        Assert.True(path.IsWithin(path));
        Assert.False(path.IsWithin(StoragePath.Parse("team")));            // prefix of the name, not a parent
        Assert.False(path.IsWithin(StoragePath.Parse("Team-Assets")));
        Assert.False(StoragePath.Parse("team-assets/imagesX").IsWithin(path));
        Assert.True(StoragePath.Root.IsWithin(StoragePath.Root));
    }

    [Fact]
    public void Append_returns_a_new_path_and_keeps_the_original()
    {
        var bucket = StoragePath.Parse("b");
        var child = bucket.Append("x");
        Assert.Equal("b", bucket.ToString());
        Assert.Equal("b/x", child.ToString());
        Assert.Equal(["b", "x"], child.Segments);
    }

    [Fact]
    public void Equality_handles_null_and_other_types_and_works_as_a_key()
    {
        StoragePath? none = null;
        Assert.True(none == null);
        Assert.True(StoragePath.Root != none);
        Assert.False(StoragePath.Root.Equals((object)"/"));
        Assert.False(StoragePath.Parse("a").Equals(StoragePath.Parse("a/b")));
        Assert.Equal(StoragePath.Parse("a//b"), StoragePath.Parse("a/b"));

        var set = new HashSet<StoragePath> { StoragePath.Parse("a/b") };
        Assert.Contains(StoragePath.Root.Append("a").Append("b"), set);
        Assert.DoesNotContain(StoragePath.Parse("a/B"), set);
    }
}

public class BucketNameRulesEdgeCaseTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("1bucket")]
    [InlineData("bucket1")]
    [InlineData("a-b.c-d")]
    [InlineData("1.2.3")]       // three parts: not an IPv4 address
    [InlineData("v1.2.3.4")]
    public void Valid_names(string name) => Assert.Null(BucketNameRules.Validate(name, []));

    [Fact]
    public void Length_limits_are_inclusive()
    {
        Assert.Null(BucketNameRules.Validate(new string('a', BucketNameRules.MaxLength), []));
        Assert.Equal("Use 3 to 63 characters.", BucketNameRules.Validate("ab", []));
        Assert.Equal("Enter a bucket name.", BucketNameRules.Validate(null, []));
        Assert.Equal("Enter a bucket name.", BucketNameRules.Validate(string.Empty, []));
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("my_bucket")]
    [InlineData("bücket")]   // non-ASCII letter
    [InlineData("has space")]
    [InlineData(".start")]
    [InlineData("end.")]
    [InlineData("a-.b")]
    [InlineData("10.0.0.1")]
    public void Invalid_names(string name) => Assert.NotNull(BucketNameRules.Validate(name, []));

    [Fact]
    public void Suggestions_skip_every_name_already_taken()
    {
        var taken = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            taken.Add(BucketNameRules.SuggestName(7, taken));
        }

        Assert.Equal(5, taken.Distinct(StringComparer.Ordinal).Count());
        Assert.All(taken, name => Assert.Null(BucketNameRules.Validate(name, [])));
    }
}

public class FolderNameRulesEdgeCaseTests
{
    [Fact]
    public void The_maximum_length_is_inclusive() =>
        Assert.Null(FolderNameRules.Validate(new string('f', FolderNameRules.MaxLength), []));

    [Theory]
    [InlineData("a|b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a*b")]
    [InlineData("a:b")]
    [InlineData("line\nbreak")]
    [InlineData("bell\u0007")]
    [InlineData(" nbsp")]   // a non-breaking space is a space too
    public void Forbidden_characters_and_spaces_are_rejected(string name) =>
        Assert.NotNull(FolderNameRules.Validate(name, []));

    [Theory]
    [InlineData("v1.2")]
    [InlineData(".hidden")]
    [InlineData("名前")]
    [InlineData("with space")]
    public void Ordinary_names_pass(string name) => Assert.Null(FolderNameRules.Validate(name, ["other"]));
}

public class ItemQueryEdgeCaseTests
{
    private static readonly StoragePath Bucket = StoragePath.Parse("b");

    private static StorageItem File(string name, long? size = 1, int day = 1, string? storageClass = null) =>
        new(Bucket.Append(name), StorageItemKind.File, size, new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), storageClass);

    private static StorageItem Folder(string name) => new(Bucket.Append(name), StorageItemKind.Folder, null, null, null);

    private static IEnumerable<string> Names(IEnumerable<StorageItem> items) => items.Select(i => i.Name);

    [Fact]
    public void Empty_and_null_sources()
    {
        Assert.Empty(ItemQuery.Default.Apply([]));
        Assert.Throws<ArgumentNullException>(() => ItemQuery.Default.Apply(null!));
    }

    [Fact]
    public void Whitespace_search_is_no_search_and_wildcards_are_literal()
    {
        var items = new[] { File("a.txt"), File("b.txt") };
        var blank = new ItemQuery(SearchText: "   ");
        Assert.False(blank.HasSearch);
        Assert.Equal(2, blank.Apply(items).Count);
        Assert.Empty(new ItemQuery(SearchText: "*").Apply(items));
        Assert.Empty(new ItemQuery(SearchText: "?.txt").Apply(items));
    }

    [Fact]
    public void Buckets_count_as_containers_for_the_filters()
    {
        var bucket = new StorageItem(StoragePath.Parse("other"), StorageItemKind.Bucket, null, null, null);
        var items = new[] { File("f"), bucket, Folder("d") };
        Assert.Equal(["d", "other"], Names(new ItemQuery(Filter: ItemTypeFilter.FoldersOnly).Apply(items)));
        Assert.Equal(["f"], Names(new ItemQuery(Filter: ItemTypeFilter.FilesOnly).Apply(items)));
    }

    [Fact]
    public void Missing_values_sort_first_and_equal_values_fall_back_to_the_natural_name()
    {
        var items = new[] { File("file10", size: 5), File("file2", size: 5), File("unknown", size: null), File("big", size: 9) };
        Assert.Equal(["unknown", "file2", "file10", "big"], Names(new ItemQuery(SortColumn: ItemSortColumn.Size).Apply(items)));
        Assert.Equal(["big", "file10", "file2", "unknown"], Names(new ItemQuery(SortColumn: ItemSortColumn.Size, Direction: SortDirection.Descending).Apply(items)));

        var classes = new[] { File("x", storageClass: "STANDARD"), File("y"), File("z", storageClass: "glacier_ir") };
        Assert.Equal(["y", "z", "x"], Names(new ItemQuery(SortColumn: ItemSortColumn.StorageClass).Apply(classes)));
    }

    [Fact]
    public void Sorting_is_deterministic_for_equal_keys()
    {
        var items = new[] { File("b", day: 3), File("a", day: 3), File("c", day: 1) };
        var query = new ItemQuery(SortColumn: ItemSortColumn.LastModified);
        Assert.Equal(["c", "a", "b"], Names(query.Apply(items)));
        Assert.Equal(Names(query.Apply(items)), Names(query.Apply(items.Reverse())));
    }

    [Fact]
    public void Toggling_from_descending_and_record_equality()
    {
        var descending = new ItemQuery(SortColumn: ItemSortColumn.Size, Direction: SortDirection.Descending);
        Assert.Equal(SortDirection.Ascending, descending.WithSortToggled(ItemSortColumn.Size).Direction);
        Assert.Equal(new ItemQuery(SortColumn: ItemSortColumn.Name), descending.WithSortToggled(ItemSortColumn.Name));
        Assert.Equal(ItemQuery.Default, new ItemQuery());
    }
}
