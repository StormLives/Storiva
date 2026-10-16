using Storiva.Core.Storage;

namespace Storiva.Tests;

public class StoragePathTests
{
    [Fact]
    public void Root_has_no_segments_and_displays_as_slash()
    {
        Assert.True(StoragePath.Root.IsRoot);
        Assert.Equal("/", StoragePath.Root.ToDisplayString());
        Assert.Null(StoragePath.Root.Parent);
    }

    [Theory]
    [InlineData("team-assets/images", "/team-assets/images/", 2)]
    [InlineData("/team-assets//images/", "/team-assets/images/", 2)]
    [InlineData("", "/", 0)]
    [InlineData("   ", "/", 0)]
    public void Parse_normalises_separators(string input, string display, int count)
    {
        var path = StoragePath.Parse(input);
        Assert.Equal(display, path.ToDisplayString());
        Assert.Equal(count, path.Segments.Count);
    }

    [Fact]
    public void Equality_and_hash_are_value_based()
    {
        var a = StoragePath.Parse("team-assets/images");
        var b = StoragePath.Root.Append("team-assets").Append("images");
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, StoragePath.Parse("team-assets/Images"));
    }

    [Fact]
    public void Parent_bucket_and_name()
    {
        var path = StoragePath.Parse("team-assets/images/icons");
        Assert.Equal("icons", path.Name);
        Assert.Equal("team-assets", path.Bucket);
        Assert.Equal(StoragePath.Parse("team-assets/images"), path.Parent);
        Assert.True(path.IsWithin(StoragePath.Parse("team-assets")));
        Assert.False(StoragePath.Parse("team-assets").IsWithin(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    public void Append_rejects_invalid_segments(string name) =>
        Assert.Throws<ArgumentException>(() => StoragePath.Root.Append(name));
}

public class EmptyCatalogTests
{
    [Fact]
    public void Without_a_connection_nothing_is_listed()
    {
        var empty = EmptyStorageCatalog.Instance;
        Assert.Empty(empty.GetChildren(StoragePath.Root));
        Assert.True(empty.Exists(StoragePath.Root));
        Assert.False(empty.Exists(StoragePath.Parse("any-bucket")));
        Assert.Null(empty.Find(StoragePath.Parse("any-bucket/file.txt")));
        Assert.True(empty.IsReadOnly(StoragePath.Root));
        Assert.Empty(empty.Regions);
    }

    [Fact]
    public void Switchable_catalog_follows_the_current_connection()
    {
        var switchable = new SwitchableStorageCatalog(EmptyStorageCatalog.Instance);
        Assert.Empty(switchable.GetChildren(StoragePath.Root));
        switchable.Inner = new Fakes.InMemoryCatalog();
        Assert.Equal(["assets", "backups"], switchable.GetChildren(StoragePath.Root).Select(b => b.Name));
        Assert.True(switchable.Exists(StoragePath.Parse("assets/images")));
        switchable.Inner = EmptyStorageCatalog.Instance;
        Assert.False(switchable.Exists(StoragePath.Parse("assets/images")));
    }
}

public class FolderNameRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("what?")]
    [InlineData("tab\tname")]
    public void Invalid_names_are_rejected(string? name) =>
        Assert.NotNull(FolderNameRules.Validate(name, []));

    [Fact]
    public void Too_long_names_are_rejected() =>
        Assert.NotNull(FolderNameRules.Validate(new string('a', FolderNameRules.MaxLength + 1), []));

    [Fact]
    public void Duplicates_are_rejected_case_insensitively() =>
        Assert.NotNull(FolderNameRules.Validate("Reports", ["reports"]));

    [Theory]
    [InlineData("reports")]
    [InlineData("2026-10 backups")]
    [InlineData("café")]
    public void Valid_names_pass(string name) => Assert.Null(FolderNameRules.Validate(name, ["other"]));
}
