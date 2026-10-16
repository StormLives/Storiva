using Storiva.App.Main;
using Storiva.App.Theme;
using Storiva.Core.Settings;

namespace Storiva.Tests;

public class WindowLayoutRulesTests
{
    private static readonly Size Minimum = new(720, 480);
    private static readonly Rectangle Primary = new(0, 0, 1920, 1040);
    private static readonly Rectangle LeftMonitor = new(-1600, 0, 1600, 860);

    private static WindowLayout At(int x, int y, int width = 1000, int height = 700) => new() { X = x, Y = y, Width = width, Height = height };

    [Fact]
    public void A_window_on_a_screen_keeps_its_place()
    {
        Assert.Equal(new Rectangle(100, 80, 1000, 700), WindowLayoutRules.VisibleBounds(At(100, 80), [Primary], Minimum));
        Assert.Equal(new Rectangle(-1500, 50, 1000, 700), WindowLayoutRules.VisibleBounds(At(-1500, 50), [Primary, LeftMonitor], Minimum));
    }

    [Fact]
    public void A_window_whose_monitor_was_removed_is_not_placed_off_screen()
    {
        Assert.Null(WindowLayoutRules.VisibleBounds(At(-1500, 50), [Primary], Minimum));
        Assert.Null(WindowLayoutRules.VisibleBounds(At(2500, 100), [Primary], Minimum));
        Assert.Null(WindowLayoutRules.VisibleBounds(At(100, -650), [Primary], Minimum)); // title bar above the screen
    }

    [Fact]
    public void A_window_partly_off_screen_stays_when_its_title_bar_can_still_be_grabbed()
    {
        Assert.NotNull(WindowLayoutRules.VisibleBounds(At(1600, 500), [Primary], Minimum));
        Assert.Null(WindowLayoutRules.VisibleBounds(At(1880, 500), [Primary], Minimum)); // only 40 px of it left on screen
    }

    [Fact]
    public void The_size_fits_the_largest_screen_and_respects_the_minimum()
    {
        Assert.Equal(new Size(1920, 1040), WindowLayoutRules.SafeSize(At(0, 0, 5000, 3000), [Primary, LeftMonitor], Minimum));
        Assert.Equal(Minimum, WindowLayoutRules.SafeSize(At(0, 0, 200, 100), [Primary], Minimum));
    }

    [Fact]
    public void Implausible_values_are_rejected()
    {
        Assert.True(WindowLayoutRules.IsPlausible(At(-1500, 50)));
        Assert.False(WindowLayoutRules.IsPlausible(At(0, 0, 0, 700)));
        Assert.False(WindowLayoutRules.IsPlausible(At(int.MinValue, 0)));
        Assert.False(WindowLayoutRules.IsPlausible(At(0, 0) with { TaskPanelHeight = 50000 }));
        Assert.False(WindowLayoutRules.IsPlausible(At(0, 0) with { BottomTab = new string('x', 100) }));
    }
}

[Collection("Palette")] // ThemeService sets the process-wide palette
public class SettingsSessionTests
{
    [Fact]
    public void Theme_and_window_layout_share_one_file_without_overwriting_each_other()
    {
        var layout = new WindowLayout { X = 10, Y = 20, Width = 900, Height = 600, BottomTab = "Tags" };
        var store = new InMemorySettingsStore(new AppSettings { Window = layout });
        var session = new SettingsSession(store);
        using var theme = new ThemeService(session, new ManualThemeSource(false));

        theme.SetPreference(ThemePreference.Dark);
        Assert.Equal(layout, store.Load().Value.Window); // the theme save kept the layout

        Assert.Equal(SettingsSaveOutcome.Saved, session.Update(s => s with { Window = layout with { Maximized = true } }, out _));
        Assert.Equal(ThemePreference.Dark, store.Load().Value.Theme); // and the layout save kept the theme
        Assert.True(store.Load().Value.Window!.Maximized);
        theme.SetPreference(ThemePreference.Light);
        StorivaPalette.Current = StorivaPalette.Light;
    }

    [Fact]
    public void A_read_only_file_is_never_written()
    {
        var store = new ReadOnlyStore();
        var session = new SettingsSession(store);
        Assert.Equal(SettingsSaveOutcome.ReadOnly, session.Update(s => s with { LastConnectionId = Guid.NewGuid() }, out var error));
        Assert.Null(error);
        Assert.Equal(0, store.Saves);
        Assert.NotNull(session.Current.LastConnectionId); // still applies to this session
    }

    [Fact]
    public void A_failed_write_reports_the_error_type_only()
    {
        var session = new SettingsSession(new FailingStore());
        Assert.Equal(SettingsSaveOutcome.Failed, session.Update(s => s with { Theme = ThemePreference.Dark }, out var error));
        Assert.Equal(nameof(UnauthorizedAccessException), error);
    }

    private sealed class ReadOnlyStore : ISettingsStore
    {
        public int Saves { get; private set; }

        public string Location => "test";

        public LoadResult<AppSettings> Load() => new(AppSettings.Default, ["newer"], ReadOnly: true);

        public void Save(AppSettings settings) => Saves++;
    }

    private sealed class FailingStore : ISettingsStore
    {
        public string Location => "test";

        public LoadResult<AppSettings> Load() => LoadResult<AppSettings>.Ok(AppSettings.Default);

        public void Save(AppSettings settings) => throw new UnauthorizedAccessException(@"C:\private\path denied");
    }
}

public class ExplorerDropTests
{
    [Fact]
    public void Dropped_paths_are_sorted_into_files_and_folders()
    {
        using var temp = new TempDataFolder();
        var file = Path.Combine(temp.Root, "photo.jpg");
        var folder = Path.Combine(temp.Root, "album");
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, "x");
        var missing = Path.Combine(temp.Root, "gone.txt");

        var drop = ExplorerDrop.Classify([file, folder + Path.DirectorySeparatorChar, missing, file.ToUpperInvariant(), "  "]);

        Assert.Equal(new[] { file }, drop.Files);       // duplicates (Windows paths ignore case) are uploaded once
        Assert.Equal(new[] { folder }, drop.Folders);   // trailing separator removed: the folder name is the last segment
        var skipped = Assert.Single(drop.Skipped);
        Assert.Equal(missing, skipped.Path);
    }

    [Fact]
    public void A_whole_drive_is_never_uploaded()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var drop = ExplorerDrop.Classify([root]);
        Assert.Empty(drop.Folders);
        Assert.Contains("drive", Assert.Single(drop.Skipped).Reason, StringComparison.Ordinal);
    }
}
