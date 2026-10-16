using Storiva.Core.Navigation;
using Storiva.Core.Storage;
using Storiva.Tests.Fakes;

namespace Storiva.Tests;

public class BrowserNavigatorTests
{
    private static readonly StoragePath Assets = StoragePath.Parse("assets");
    private static readonly StoragePath Images = StoragePath.Parse("assets/images");
    private static readonly StoragePath Icons = StoragePath.Parse("assets/images/icons");
    private static readonly StoragePath Weekly = StoragePath.Parse("backups/weekly");

    private static BrowserNavigator NewNavigator(InMemoryCatalog? catalog = null) => new(catalog ?? new InMemoryCatalog());

    [Fact]
    public void Starts_at_root_with_no_history()
    {
        var nav = NewNavigator();
        Assert.True(nav.Current.IsRoot);
        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
        Assert.False(nav.CanGoUp);
    }

    [Fact]
    public void NavigateTo_existing_folder_updates_current_and_raises_event()
    {
        var nav = NewNavigator();
        var raised = 0;
        nav.Navigated += (_, _) => raised++;

        Assert.True(nav.NavigateTo(Images));
        Assert.Equal(Images, nav.Current);
        Assert.Equal(1, raised);
        Assert.True(nav.CanGoBack);
        Assert.True(nav.CanGoUp);
    }

    [Fact]
    public void NavigateTo_missing_or_current_path_is_ignored()
    {
        var nav = NewNavigator();
        Assert.False(nav.NavigateTo(StoragePath.Parse("assets/missing")));
        Assert.False(nav.NavigateTo(StoragePath.Root));
        Assert.True(nav.Current.IsRoot);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Back_and_forward_walk_the_history()
    {
        var nav = NewNavigator();
        nav.NavigateTo(Assets);
        nav.NavigateTo(Images);
        nav.NavigateTo(Icons);

        Assert.True(nav.GoBack());
        Assert.Equal(Images, nav.Current);
        Assert.True(nav.GoBack());
        Assert.Equal(Assets, nav.Current);
        Assert.True(nav.CanGoForward);
        Assert.True(nav.GoForward());
        Assert.Equal(Images, nav.Current);
        Assert.True(nav.GoForward());
        Assert.Equal(Icons, nav.Current);
        Assert.False(nav.GoForward());
    }

    [Fact]
    public void New_navigation_clears_forward_history()
    {
        var nav = NewNavigator();
        nav.NavigateTo(Images);
        nav.GoBack();
        Assert.True(nav.CanGoForward);

        nav.NavigateTo(Weekly);
        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void Up_goes_to_parent_and_is_recorded_in_history()
    {
        var nav = NewNavigator();
        nav.NavigateTo(Icons);
        Assert.True(nav.GoUp());
        Assert.Equal(Images, nav.Current);
        Assert.True(nav.GoUp());
        Assert.True(nav.GoUp());
        Assert.True(nav.Current.IsRoot);
        Assert.False(nav.GoUp());

        Assert.True(nav.GoBack());
        Assert.Equal(Assets, nav.Current);
    }

    [Fact]
    public void Breadcrumb_lists_home_and_each_segment()
    {
        var nav = NewNavigator();
        nav.NavigateTo(Icons);
        var crumbs = nav.Breadcrumb;

        Assert.Equal(["Home", "assets", "images", "icons"], crumbs.Select(c => c.Label));
        Assert.Equal([StoragePath.Root, Assets, Images, Icons], crumbs.Select(c => c.Path));
    }

    [Fact]
    public void Breadcrumb_at_root_is_home_only()
    {
        var crumb = Assert.Single(NewNavigator().Breadcrumb);
        Assert.Equal(BrowserNavigator.HomeLabel, crumb.Label);
        Assert.True(crumb.Path.IsRoot);
    }

    [Fact]
    public void Back_skips_entries_that_no_longer_exist()
    {
        var catalog = new InMemoryCatalog();
        var nav = NewNavigator(catalog);
        nav.NavigateTo(Images);
        nav.NavigateTo(Icons);
        nav.NavigateTo(Weekly);

        catalog.Remove(Icons); // deleted meanwhile (e.g. by another client)

        Assert.True(nav.GoBack());
        Assert.Equal(Images, nav.Current);
    }

    [Fact]
    public void Reset_returns_to_root_and_clears_history()
    {
        var nav = NewNavigator();
        nav.NavigateTo(Images);
        nav.NavigateTo(Weekly);
        nav.Reset();

        Assert.True(nav.Current.IsRoot);
        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
    }
}
