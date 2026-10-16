using System.Text.Json;
using Storiva.Core.Connections;
using Storiva.Core.Settings;

namespace Storiva.Infrastructure.Persistence;

/// <summary>settings.json: theme preference only (no credentials, no account data).</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;

    public JsonSettingsStore(StorivaDataPaths paths, Func<DateTimeOffset>? clock = null)
    {
        _path = (paths ?? throw new ArgumentNullException(nameof(paths))).SettingsFile;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string Location => _path;

    public LoadResult<AppSettings> Load()
    {
        if (!File.Exists(_path))
        {
            return LoadResult<AppSettings>.Ok(AppSettings.Default);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllBytes(_path), StorivaJson.Options) ?? throw new JsonException("empty");
            var warnings = new List<string>();
            var readOnly = false;
            if (dto.Version > AppSettings.CurrentVersion)
            {
                readOnly = true;
                warnings.Add($"settings.json was written by a newer Storiva (format {dto.Version}). It is read but not overwritten.");
            }

            var theme = Enum.IsDefined(dto.Theme) ? dto.Theme : ThemePreference.System;
            return new LoadResult<AppSettings>(new AppSettings { Theme = theme }, warnings, readOnly);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var moved = AtomicFile.QuarantineCorrupt(_path, _clock);
            return new LoadResult<AppSettings>(AppSettings.Default,
                [$"settings.json could not be read and defaults are used.{(moved is null ? string.Empty : $" The damaged file was kept as {moved}.")}"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadResult<AppSettings>(AppSettings.Default, [$"settings.json could not be opened ({ex.GetType().Name}); defaults are used."], ReadOnly: true);
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var dto = new SettingsDto { Version = AppSettings.CurrentVersion, Theme = settings.Theme };
        AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(dto, StorivaJson.Options));
    }

    private sealed class SettingsDto
    {
        public string Format { get; set; } = "storiva-settings";

        public int Version { get; set; } = 1;

        public ThemePreference Theme { get; set; } = ThemePreference.System;
    }
}

/// <summary>
/// connections.json: the non-secret part of each connection. Unknown or invalid entries are skipped with a
/// warning; a damaged file is moved aside; a file from a newer Storiva is read but never overwritten.
/// </summary>
public sealed class JsonConnectionStore : IConnectionStore
{
    public const int CurrentVersion = 1;

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private bool _readOnly;

    public JsonConnectionStore(StorivaDataPaths paths, Func<DateTimeOffset>? clock = null)
    {
        _path = (paths ?? throw new ArgumentNullException(nameof(paths))).ConnectionsFile;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string Location => _path;

    public LoadResult<IReadOnlyList<ConnectionProfile>> Load()
    {
        if (!File.Exists(_path))
        {
            return LoadResult<IReadOnlyList<ConnectionProfile>>.Ok([]);
        }

        FileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllBytes(_path), StorivaJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var moved = AtomicFile.QuarantineCorrupt(_path, _clock);
            return new LoadResult<IReadOnlyList<ConnectionProfile>>([],
                [$"connections.json could not be read; no connections were loaded.{(moved is null ? string.Empty : $" The damaged file was kept as {moved}.")} Stored credentials were not deleted."]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _readOnly = true;
            return new LoadResult<IReadOnlyList<ConnectionProfile>>([], [$"connections.json could not be opened ({ex.GetType().Name})."], ReadOnly: true);
        }

        var warnings = new List<string>();
        if (dto is null)
        {
            return new LoadResult<IReadOnlyList<ConnectionProfile>>([], ["connections.json is empty."]);
        }

        if (dto.Version > CurrentVersion)
        {
            _readOnly = true;
            warnings.Add($"connections.json was written by a newer Storiva (format {dto.Version}). Connections are shown but changes are not saved.");
        }

        var result = new List<ConnectionProfile>();
        var skipped = 0;
        foreach (var entry in dto.Connections ?? [])
        {
            if (entry is null || entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.Name) || result.Any(r => r.Id == entry.Id)
                || !Enum.TryParse<StorageProvider>(entry.Provider, ignoreCase: false, out var provider) || !Enum.IsDefined(provider)
                || !Enum.TryParse<AuthenticationMethod>(entry.Authentication, ignoreCase: false, out var authentication) || !Enum.IsDefined(authentication))
            {
                skipped++;
                continue;
            }

            var profile = new ConnectionProfile
            {
                Id = entry.Id,
                Name = entry.Name.Trim(),
                Provider = provider,
                Endpoint = entry.Endpoint,
                Region = string.IsNullOrWhiteSpace(entry.Region) ? ConnectionRules.DefaultRegion : entry.Region,
                Authentication = authentication,
                ProfileName = entry.ProfileName,
                ForcePathStyle = entry.ForcePathStyle,
                AllowInsecureHttp = entry.AllowInsecureHttp,
                UseFlexibleChecksums = entry.UseFlexibleChecksums ?? provider == StorageProvider.AwsS3,
                LastTest = entry.LastTest is { } t && Enum.TryParse<ConnectionStatus>(t.Status, out var status) && Enum.IsDefined(status)
                    ? new ConnectionTestRecord(status, t.TestedAt, t.Summary ?? string.Empty)
                    : null,
            };

            // A hand-edited file must not smuggle in an invalid endpoint (validated again before every use).
            if (ConnectionRules.Validate(profile, null, result.Select(r => r.Name)).Any(e => e.Field != nameof(ConnectionProfile.ProfileName)))
            {
                skipped++;
                continue;
            }

            result.Add(profile);
        }

        if (skipped > 0)
        {
            warnings.Add($"{skipped} invalid connection entr{(skipped == 1 ? "y was" : "ies were")} skipped in connections.json.");
        }

        return new LoadResult<IReadOnlyList<ConnectionProfile>>(result, warnings, _readOnly);
    }

    public void Save(IReadOnlyList<ConnectionProfile> connections)
    {
        if (_readOnly)
        {
            throw new InvalidOperationException("connections.json is read-only for this version of Storiva.");
        }

        var dto = new FileDto
        {
            Version = CurrentVersion,
            Connections = connections.Select(c => (ConnectionDto?)new ConnectionDto
            {
                Id = c.Id,
                Name = c.Name,
                Provider = c.Provider.ToString(),
                Endpoint = c.Endpoint,
                Region = c.Region,
                Authentication = c.Authentication.ToString(),
                ProfileName = c.ProfileName,
                ForcePathStyle = c.ForcePathStyle,
                AllowInsecureHttp = c.AllowInsecureHttp,
                UseFlexibleChecksums = c.UseFlexibleChecksums,
                LastTest = c.LastTest is { } t ? new TestDto { Status = t.Status.ToString(), TestedAt = t.TestedAt, Summary = t.Summary } : null,
            }).ToList(),
        };
        AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(dto, StorivaJson.Options));
    }

    // DTOs: the file shape is explicit and contains no secret field at all.
    private sealed class FileDto
    {
        public string Format { get; set; } = "storiva-connections";

        public int Version { get; set; } = CurrentVersion;

        public List<ConnectionDto?>? Connections { get; set; }
    }

    private sealed class ConnectionDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        // Enums are kept as text so a value from a newer version skips one entry instead of the whole file.
        public string? Provider { get; set; }

        public string? Endpoint { get; set; }

        public string? Region { get; set; }

        public string? Authentication { get; set; }

        public string? ProfileName { get; set; }

        public bool ForcePathStyle { get; set; }

        public bool AllowInsecureHttp { get; set; }

        public bool? UseFlexibleChecksums { get; set; }

        public TestDto? LastTest { get; set; }
    }

    private sealed class TestDto
    {
        public string? Status { get; set; }

        public DateTimeOffset TestedAt { get; set; }

        public string? Summary { get; set; }
    }
}
