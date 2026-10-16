using Storiva.Core.Listing;
using Storiva.Core.Storage;
using Storiva.Tests.Fakes;

namespace Storiva.Tests;

public class ItemQueryTests
{
    private static readonly IReadOnlyList<StorageItem> Images =
        new InMemoryCatalog().GetChildren(StoragePath.Parse("assets/images"));

    private static IEnumerable<string> Names(IEnumerable<StorageItem> items) => items.Select(i => i.Name);

    [Fact]
    public void Default_query_lists_folders_first_then_files_by_natural_name()
    {
        var result = ItemQuery.Default.Apply(Images);
        Assert.Equal(
            ["icons", "banner-2026.png", "logo-draft.svg", "photo-001.jpg", "photo-002.jpg", "photo-010.jpg", "thumbnail.webp"],
            Names(result));
    }

    [Theory]
    [InlineData("photo", 3)]
    [InlineData("PHOTO", 3)]
    [InlineData("  photo  ", 3)]
    [InlineData(".jpg", 3)]
    [InlineData("icon", 1)]
    [InlineData("zzz", 0)]
    [InlineData("", 7)]
    public void Search_is_case_insensitive_contains_on_the_name(string text, int expected) =>
        Assert.Equal(expected, new ItemQuery(SearchText: text).Apply(Images).Count);

    [Fact]
    public void Apply_never_modifies_the_source()
    {
        var source = Images.ToList();
        var snapshot = source.ToList();

        _ = new ItemQuery("photo", ItemTypeFilter.FilesOnly, ItemSortColumn.Size, SortDirection.Descending).Apply(source);

        Assert.Equal(snapshot, source);
    }

    [Fact]
    public void Filters_split_containers_and_files()
    {
        Assert.Equal(["icons"], Names(new ItemQuery(Filter: ItemTypeFilter.FoldersOnly).Apply(Images)));
        Assert.Equal(6, new ItemQuery(Filter: ItemTypeFilter.FilesOnly).Apply(Images).Count);
        Assert.Empty(new ItemQuery("icons", ItemTypeFilter.FilesOnly).Apply(Images));
    }

    [Fact]
    public void Size_sort_orders_files_and_keeps_folders_on_top_in_both_directions()
    {
        var ascending = new ItemQuery(SortColumn: ItemSortColumn.Size).Apply(Images);
        var descending = new ItemQuery(SortColumn: ItemSortColumn.Size, Direction: SortDirection.Descending).Apply(Images);

        Assert.Equal("icons", ascending[0].Name);
        Assert.Equal("icons", descending[0].Name);
        Assert.Equal("logo-draft.svg", ascending[1].Name);
        Assert.Equal("photo-001.jpg", descending[1].Name);
        var sizes = descending.Skip(1).Select(i => i.Size!.Value).ToList();
        Assert.Equal(sizes.OrderDescending(), sizes);
    }

    [Fact]
    public void Date_sort_orders_by_last_modified()
    {
        var result = new ItemQuery(SortColumn: ItemSortColumn.LastModified).Apply(Images).Skip(1).ToList();
        var dates = result.Select(i => i.LastModified!.Value).ToList();
        Assert.Equal(dates.Order(), dates);
        Assert.Equal("logo-draft.svg", result[0].Name);
    }

    [Fact]
    public void Type_and_storage_class_sorts_use_name_as_tie_breaker()
    {
        var archives = new InMemoryCatalog().GetChildren(StoragePath.Parse("assets/archives"));
        var byClass = new ItemQuery(SortColumn: ItemSortColumn.StorageClass).Apply(archives);
        Assert.Equal(["assets-export.tar", "site-backup-2026-08.zip", "site-backup-2026-09.zip"], Names(byClass));

        var byType = new ItemQuery(SortColumn: ItemSortColumn.Type).Apply(Images).Skip(1).Select(i => FileTypes.Describe(i)).ToList();
        Assert.Equal(byType.Order(StringComparer.OrdinalIgnoreCase), byType);
    }

    [Fact]
    public void WithSortToggled_flips_direction_or_switches_column()
    {
        var q = ItemQuery.Default;
        var flipped = q.WithSortToggled(ItemSortColumn.Name);
        Assert.Equal(SortDirection.Descending, flipped.Direction);
        var other = flipped.WithSortToggled(ItemSortColumn.Size);
        Assert.Equal((ItemSortColumn.Size, SortDirection.Ascending), (other.SortColumn, other.Direction));
    }

    [Fact]
    public void Name_sort_descending_reverses_within_each_group()
    {
        var result = new ItemQuery(Direction: SortDirection.Descending).Apply(Images);
        Assert.Equal("icons", result[0].Name);
        Assert.Equal("thumbnail.webp", result[1].Name);
        Assert.Equal("banner-2026.png", result[^1].Name);
    }
}

public class NaturalStringComparerTests
{
    [Theory]
    [InlineData("file2", "file10", -1)]
    [InlineData("file10", "file2", 1)]
    [InlineData("File1", "file1", -1)] // equal ignoring case, ordinal tie-break keeps a stable order
    [InlineData("a", "b", -1)]
    [InlineData("photo-002.jpg", "photo-010.jpg", -1)]
    [InlineData("x007", "x7", -1)]
    [InlineData("abc", "abc", 0)]
    public void Compares_digit_runs_numerically(string x, string y, int expectedSign) =>
        Assert.Equal(expectedSign, Math.Sign(NaturalStringComparer.Instance.Compare(x, y)));
}

public class FileTypesTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1_048_576L, "1 MB")]
    [InlineData(1_610_612_736L, "1.5 GB")]
    public void FormatSize_uses_binary_units_and_invariant_culture(long bytes, string expected) =>
        Assert.Equal(expected, FileTypes.FormatSize(bytes));

    [Fact]
    public void FormatSize_of_containers_is_empty() => Assert.Equal(string.Empty, FileTypes.FormatSize(null));

    [Fact]
    public void FormatDate_is_utc_and_invariant() =>
        Assert.Equal("2026-10-01 06:00 UTC", FileTypes.FormatDate(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(3))));

    [Theory]
    [InlineData("report.PDF", "PDF document", FileCategory.Text)]
    [InlineData("photo.jpg", "JPEG image", FileCategory.Image)]
    [InlineData("backup.tar", "TAR archive", FileCategory.Archive)]
    [InlineData("data.parquet", "PARQUET file", FileCategory.Generic)]
    [InlineData("LICENSE", "File", FileCategory.Generic)]
    public void Describe_and_categorize_files(string name, string description, FileCategory category)
    {
        var item = new StorageItem(StoragePath.Parse("b/" + name), StorageItemKind.File, 1, null, null);
        Assert.Equal(description, FileTypes.Describe(item));
        Assert.Equal(category, FileTypes.Categorize(item));
    }
}
