using Microsoft.Win32;
using Storiva.Core.Settings;

namespace Storiva.App.Theme;

/// <summary>Raised after the effective palette changed; carries both palettes so open windows can be re-coloured.</summary>
internal sealed class ThemeChangedEventArgs(StorivaPalette previous, StorivaPalette current) : EventArgs
{
    public StorivaPalette Previous { get; } = previous;

    public StorivaPalette Current { get; } = current;
}

/// <summary>Central theme state: selected preference, effective theme, Windows detection and change notification.</summary>
internal interface IThemeService : IDisposable
{
    ThemePreference Preference { get; }

    EffectiveTheme Effective { get; }

    StorivaPalette Palette { get; }

    /// <summary>The Windows app-mode setting, or null when it cannot be read.</summary>
    bool? SystemPrefersDark { get; }

    /// <summary>Last problem saving or loading the preference (shown in Options), or null.</summary>
    string? PersistenceWarning { get; }

    /// <summary>Raised on the UI thread when <see cref="Palette"/> changed.</summary>
    event EventHandler<ThemeChangedEventArgs>? Changed;

    /// <summary>Applies and persists the preference.</summary>
    void SetPreference(ThemePreference preference);
}

/// <summary>Source of the Windows light/dark preference (replaceable in tests).</summary>
internal interface ISystemThemeSource : IDisposable
{
    bool? PrefersDark();

    /// <summary>Raised (on any thread) when Windows may have changed the setting.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Reads HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme (0 = dark apps) and
/// listens to <see cref="SystemEvents.UserPreferenceChanged"/>, which Windows raises when the user switches modes.
/// </summary>
internal sealed class WindowsThemeSource : ISystemThemeSource
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public WindowsThemeSource() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    public event EventHandler? Changed;

    public bool? PrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value ? value == 0 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // The light/dark switch arrives as "General" (ImmersiveColorSet); Color/VisualStyle cover theme changes.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // SystemEvents is static: unsubscribe, or the source (and the form behind it) would never be collected.
    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}

/// <summary>Fixed Windows preference for tests and the smoke test.</summary>
internal sealed class ManualThemeSource(bool? prefersDark = false) : ISystemThemeSource
{
    private bool? _value = prefersDark;

    public event EventHandler? Changed;

    public bool? PrefersDark() => _value;

    /// <summary>Simulates Windows switching between light and dark.</summary>
    public void Set(bool? prefersDark)
    {
        _value = prefersDark;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
    }
}

internal sealed class ThemeService : IThemeService
{
    private readonly ISettingsStore _store;
    private readonly ISystemThemeSource _system;
    private readonly SynchronizationContext? _context;
    private AppSettings _settings;
    private bool _readOnly;

    public ThemeService(ISettingsStore store, ISystemThemeSource system)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _context = SynchronizationContext.Current;

        var loaded = store.Load();
        _settings = loaded.Value;
        _readOnly = loaded.ReadOnly;
        PersistenceWarning = loaded.Warnings.Count > 0 ? string.Join(" ", loaded.Warnings) : null;

        Preference = _settings.Theme;
        SystemPrefersDark = system.PrefersDark();
        Effective = ThemeResolver.Resolve(Preference, SystemPrefersDark);
        Palette = For(Effective);
        StorivaPalette.Current = Palette;
        _system.Changed += OnSystemChanged;
    }

    public ThemePreference Preference { get; private set; }

    public EffectiveTheme Effective { get; private set; }

    public StorivaPalette Palette { get; private set; }

    public bool? SystemPrefersDark { get; private set; }

    public string? PersistenceWarning { get; private set; }

    public event EventHandler<ThemeChangedEventArgs>? Changed;

    public static StorivaPalette For(EffectiveTheme theme) => theme == EffectiveTheme.Dark ? StorivaPalette.Dark : StorivaPalette.Light;

    public void SetPreference(ThemePreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        Preference = preference;
        _settings = _settings with { Theme = preference };
        if (_readOnly)
        {
            PersistenceWarning = "The preference applies to this session; settings.json is not overwritten (it was written by a newer Storiva or could not be opened).";
        }
        else
        {
            try
            {
                _store.Save(_settings);
                PersistenceWarning = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PersistenceWarning = $"The theme could not be saved ({ex.GetType().Name}); it applies to this session only.";
            }
        }

        Reevaluate();
    }

    private void OnSystemChanged(object? sender, EventArgs e)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            Reevaluate();
        }
        else
        {
            _context.Post(_ => Reevaluate(), null);
        }
    }

    private void Reevaluate()
    {
        SystemPrefersDark = _system.PrefersDark();
        var effective = ThemeResolver.Resolve(Preference, SystemPrefersDark);
        Effective = effective;
        var next = For(effective);
        if (ReferenceEquals(next, Palette))
        {
            return;
        }

        var previous = Palette;
        Palette = next;
        StorivaPalette.Current = next;
        Changed?.Invoke(this, new ThemeChangedEventArgs(previous, next));
    }

    public void Dispose()
    {
        _system.Changed -= OnSystemChanged;
        _system.Dispose();
    }
}
