namespace Storiva.Core.Settings;

/// <summary>Appearance chosen in Tools › Options › Interface.</summary>
public enum ThemePreference
{
    /// <summary>Follows the Windows "app mode" (light or dark) and switches when Windows switches.</summary>
    System,
    Light,
    Dark,
}

/// <summary>Theme actually applied to the windows.</summary>
public enum EffectiveTheme
{
    Light,
    Dark,
}

public static class ThemeResolver
{
    /// <summary>
    /// Resolves the preference against the Windows setting. <paramref name="systemPrefersDark"/> is null when the
    /// Windows setting could not be read (older Windows, policy, missing registry value): Storiva then uses Light,
    /// which is also the Windows default.
    /// </summary>
    public static EffectiveTheme Resolve(ThemePreference preference, bool? systemPrefersDark) => preference switch
    {
        ThemePreference.Light => EffectiveTheme.Light,
        ThemePreference.Dark => EffectiveTheme.Dark,
        _ => systemPrefersDark == true ? EffectiveTheme.Dark : EffectiveTheme.Light,
    };

    public static string Label(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => "Light",
        ThemePreference.Dark => "Dark",
        _ => "System (follow Windows)",
    };
}

/// <summary>
/// Non-secret preferences persisted between sessions. Holds no credentials, no account data and no
/// object content. Everything else in <see cref="SessionOptions"/> stays session-only.
/// </summary>
public sealed record AppSettings
{
    /// <summary>File format version written by this build. Increase it when the shape changes.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>Main window size, position and pane layout of the last session; null before the first exit.</summary>
    public WindowLayout? Window { get; init; }

    /// <summary>
    /// Connection opened last. Only highlighted in the "Not connected" panel: Storiva never connects on its own.
    /// The id is not a secret (connections.json holds the same id).
    /// </summary>
    public Guid? LastConnectionId { get; init; }

    public static AppSettings Default { get; } = new();
}

/// <summary>Outcome of loading a settings or connections file: data plus user-facing warnings (never secrets).</summary>
public sealed record LoadResult<T>(T Value, IReadOnlyList<string> Warnings, bool ReadOnly = false)
{
    public static LoadResult<T> Ok(T value) => new(value, []);
}

public interface ISettingsStore
{
    /// <summary>Location shown in the documentation and in Help (e.g. %LOCALAPPDATA%\Storiva\settings.json).</summary>
    string Location { get; }

    LoadResult<AppSettings> Load();

    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    void Save(AppSettings settings);
}

/// <summary>Settings kept in memory only (tests, smoke test).</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private AppSettings _settings;

    public InMemorySettingsStore(AppSettings? initial = null) => _settings = initial ?? AppSettings.Default;

    public string Location => "(memory)";

    public int SaveCount { get; private set; }

    public LoadResult<AppSettings> Load() => LoadResult<AppSettings>.Ok(_settings);

    public void Save(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        SaveCount++;
    }
}

public enum SettingsSaveOutcome
{
    Saved,

    /// <summary>Not written: the file is from a newer Storiva or could not be opened (see <see cref="LoadResult{T}.ReadOnly"/>).</summary>
    ReadOnly,

    /// <summary>Writing failed (I/O error, folder not writable).</summary>
    Failed,
}

/// <summary>
/// settings.json loaded once and shared by everything that keeps a preference in it (theme, window layout), so
/// each part is saved on top of the current values instead of overwriting the others with an older copy.
/// </summary>
public sealed class SettingsSession
{
    private readonly ISettingsStore _store;

    public SettingsSession(ISettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var loaded = store.Load();
        Current = loaded.Value;
        ReadOnly = loaded.ReadOnly;
        LoadWarnings = loaded.Warnings;
    }

    public AppSettings Current { get; private set; }

    public bool ReadOnly { get; }

    public IReadOnlyList<string> LoadWarnings { get; }

    /// <summary>
    /// Applies <paramref name="change"/> for this session and writes the result. <paramref name="error"/> names the
    /// exception type when writing failed (never a path or content).
    /// </summary>
    public SettingsSaveOutcome Update(Func<AppSettings, AppSettings> change, out string? error)
    {
        ArgumentNullException.ThrowIfNull(change);
        Current = change(Current) ?? throw new InvalidOperationException("The settings change returned null.");
        error = null;
        if (ReadOnly)
        {
            return SettingsSaveOutcome.ReadOnly;
        }

        try
        {
            _store.Save(Current);
            return SettingsSaveOutcome.Saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.GetType().Name;
            return SettingsSaveOutcome.Failed;
        }
    }
}
