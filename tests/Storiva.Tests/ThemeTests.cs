using Storiva.App.Theme;
using Storiva.Core.Settings;

namespace Storiva.Tests;

public class ThemeResolverTests
{
    [Theory]
    [InlineData(ThemePreference.Light, true, EffectiveTheme.Light)]
    [InlineData(ThemePreference.Dark, false, EffectiveTheme.Dark)]
    [InlineData(ThemePreference.System, true, EffectiveTheme.Dark)]
    [InlineData(ThemePreference.System, false, EffectiveTheme.Light)]
    [InlineData(ThemePreference.System, null, EffectiveTheme.Light)]
    public void Preference_resolves_against_windows(ThemePreference preference, bool? windowsDark, EffectiveTheme expected) =>
        Assert.Equal(expected, ThemeResolver.Resolve(preference, windowsDark));
}

[Collection("Palette")] // the palette is process-wide UI state
public class ThemeServiceTests
{
    [Fact]
    public void Starts_from_the_saved_preference_and_persists_changes()
    {
        var store = new InMemorySettingsStore(new AppSettings { Theme = ThemePreference.Dark });
        using var service = new ThemeService(store, new ManualThemeSource(false));
        Assert.Equal(EffectiveTheme.Dark, service.Effective);
        Assert.Same(StorivaPalette.Dark, StorivaPalette.Current);

        ThemeChangedEventArgs? changed = null;
        service.Changed += (_, e) => changed = e;
        service.SetPreference(ThemePreference.Light);
        Assert.Equal(ThemePreference.Light, store.Load().Value.Theme);
        Assert.Same(StorivaPalette.Dark, changed!.Previous);
        Assert.Same(StorivaPalette.Light, changed.Current);
        Assert.Same(StorivaPalette.Light, StorivaPalette.Current);
    }

    [Fact]
    public void System_mode_follows_windows_and_fixed_modes_ignore_it()
    {
        var windows = new ManualThemeSource(false);
        using var service = new ThemeService(new InMemorySettingsStore(), windows);
        var changes = 0;
        service.Changed += (_, _) => changes++;
        windows.Set(true);
        Assert.Equal(EffectiveTheme.Dark, service.Effective);
        windows.Set(false);
        Assert.Equal(EffectiveTheme.Light, service.Effective);
        Assert.Equal(2, changes);

        service.SetPreference(ThemePreference.Light);
        windows.Set(true);
        Assert.Equal(EffectiveTheme.Light, service.Effective);
        Assert.Equal(2, changes);
        StorivaPalette.Current = StorivaPalette.Light;
    }

    [Fact]
    public void Windows_notifications_from_another_thread_are_posted_to_the_ui_context()
    {
        var previous = SynchronizationContext.Current;
        var ui = new RecordingContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var windows = new ManualThemeSource(false);
            using var service = new ThemeService(new InMemorySettingsStore(), windows);
            var raisedOn = new List<SynchronizationContext?>();
            service.Changed += (_, _) => raisedOn.Add(SynchronizationContext.Current);

            var worker = new Thread(() => windows.Set(true)); // like SystemEvents, on another thread
            worker.Start();
            worker.Join();
            Assert.Empty(raisedOn);                              // nothing ran on the worker thread
            Assert.Single(ui.Posted);

            ui.RunPosted();
            Assert.Same(ui, Assert.Single(raisedOn));
            Assert.Equal(EffectiveTheme.Dark, service.Effective);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            StorivaPalette.Current = StorivaPalette.Light;
        }
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public List<(SendOrPostCallback Callback, object? State)> Posted { get; } = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (Posted)
            {
                Posted.Add((d, state));
            }
        }

        public void RunPosted()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                foreach (var (callback, state) in Posted.ToList())
                {
                    callback(state);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    [Fact]
    public void Save_failures_keep_the_session_theme_and_report_a_warning()
    {
        using var service = new ThemeService(new FailingStore(), new ManualThemeSource(false));
        service.SetPreference(ThemePreference.Dark);
        Assert.Equal(EffectiveTheme.Dark, service.Effective);
        Assert.Contains("could not be saved", service.PersistenceWarning, StringComparison.Ordinal);
        service.SetPreference(ThemePreference.Light);
    }

    private sealed class FailingStore : ISettingsStore
    {
        public string Location => "test";

        public LoadResult<AppSettings> Load() => LoadResult<AppSettings>.Ok(AppSettings.Default);

        public void Save(AppSettings settings) => throw new UnauthorizedAccessException("denied");
    }
}

public class PaletteTests
{
    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Ch(int v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }

        var (la, lb) = (L(a), L(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static TheoryData<string> Palettes => new() { "Light", "Dark" };

    private static StorivaPalette Get(string name) => name == "Dark" ? StorivaPalette.Dark : StorivaPalette.Light;

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Text_contrast_meets_wcag_aa(string name)
    {
        var p = Get(name);
        Assert.True(Contrast(p.TextPrimary, p.Surface) >= 7, "primary text");
        Assert.True(Contrast(p.TextSecondary, p.Surface) >= 4.5, "secondary text");
        Assert.True(Contrast(p.TextPrimary, p.SelectionFocused) >= 4.5, "selected row text");
        Assert.True(Contrast(p.Link, p.Surface) >= 4.5, "links");
        Assert.True(Contrast(p.Danger, p.Surface) >= 4.5, "error text");
        Assert.True(Contrast(p.Warning, p.Surface) >= 3, "warning text");
        Assert.True(Contrast(p.TextOnAccent, p.Accent) >= 4.5, "primary button text");
        Assert.True(Contrast(p.IconDefault, p.Surface) >= 3, "icons");
    }

    [Fact]
    public void Dark_palette_separates_surfaces_and_avoids_pure_black()
    {
        var p = StorivaPalette.Dark;
        Assert.True(p.IsDark);
        var surfaces = new[] { p.Chrome, p.Surface, p.Toolbar, p.HeaderBackground, p.SelectionFocused, p.SelectionUnfocused, p.Border };
        Assert.Equal(surfaces.Length, surfaces.Select(c => c.ToArgb()).Distinct().Count());
        Assert.DoesNotContain(surfaces, c => c.R == 0 && c.G == 0 && c.B == 0);
        Assert.True(Contrast(p.Border, p.Surface) > 1.2, "borders visible");
        Assert.True(Contrast(p.SelectionFocused, p.Surface) > 1.4, "selection visible");
    }
}

[Collection("Palette")]
public class ThemeApplierTests
{
    [Fact]
    public void Light_to_dark_and_back_restores_the_exact_light_colours()
    {
        StorivaPalette.Current = StorivaPalette.Light;
        using var panel = new Panel { BackColor = StorivaPalette.Light.Surface };
        var label = new Label { ForeColor = StorivaPalette.Light.TextSecondary };
        var plain = new TextBox(); // WinForms defaults (SystemColors)
        var grid = new DataGridView();
        ControlStyling.ApplyGrid(grid, StorivaPalette.Light);
        panel.Controls.AddRange([label, plain, grid]);
        var plainBack = plain.BackColor;

        ThemeApplier.Apply(panel, StorivaPalette.Light, StorivaPalette.Dark);
        Assert.Equal(StorivaPalette.Dark.Surface, panel.BackColor);
        Assert.Equal(StorivaPalette.Dark.TextSecondary, label.ForeColor);
        Assert.Equal(StorivaPalette.Dark.Surface, plain.BackColor);
        Assert.Equal(StorivaPalette.Dark.Surface, grid.BackgroundColor);
        Assert.Equal(StorivaPalette.Dark.TextPrimary, grid.DefaultCellStyle.ForeColor);

        ThemeApplier.Apply(panel, StorivaPalette.Dark, StorivaPalette.Light);
        Assert.Equal(StorivaPalette.Light.Surface, panel.BackColor);
        Assert.Equal(StorivaPalette.Light.TextSecondary, label.ForeColor);
        Assert.Equal(plainBack, plain.BackColor); // the WinForms default comes back, not an approximation
        Assert.Equal(StorivaPalette.Light.Surface, grid.BackgroundColor);
        Assert.Equal(StorivaPalette.Light.GridLine, grid.GridColor);
    }

    [Fact]
    public void Colours_that_are_not_from_the_palette_are_left_alone()
    {
        using var panel = new Panel { BackColor = Color.FromArgb(255, 1, 2, 3) };
        ThemeApplier.Apply(panel, StorivaPalette.Light, StorivaPalette.Dark);
        Assert.Equal(Color.FromArgb(255, 1, 2, 3), panel.BackColor);
    }
}
