using System.Text;
using Storiva.Core.Connections;
using Storiva.Core.Settings;
using Storiva.Infrastructure.Persistence;
using Storiva.Infrastructure.Security;

namespace Storiva.Tests;

/// <summary>A unique folder under %TEMP% per test; never the user's real %LOCALAPPDATA%\Storiva.</summary>
internal sealed class TempDataFolder : IDisposable
{
    public TempDataFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "storiva-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new StorivaDataPaths(Root);
    }

    public string Root { get; }

    public StorivaDataPaths Paths { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class DpapiSecretStoreTests
{
    private static readonly StorageCredentials Credentials = new("TESTKEYID0001", "test-secret-value-not-real", "test-session-token");

    [Fact]
    public void Round_trip_protects_the_file_content()
    {
        using var temp = new TempDataFolder();
        var store = new DpapiSecretStore(temp.Paths);
        var id = Guid.NewGuid();
        store.Save(id, Credentials);

        var loaded = store.Load(id);
        Assert.Equal(SecretLoadStatus.Loaded, loaded.Status);
        Assert.Equal(("TESTKEYID0001", "test-secret-value-not-real", "test-session-token"), (loaded.Credentials!.AccessKeyId, loaded.Credentials.SecretAccessKey, loaded.Credentials.SessionToken));

        var file = Assert.Single(Directory.GetFiles(temp.Paths.SecretsFolder));
        var bytes = File.ReadAllBytes(file);
        foreach (var plain in new[] { "test-secret-value-not-real", "TESTKEYID0001", "test-session-token" })
        {
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plain)) < 0, plain + " found in UTF-8");
            Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(plain)) < 0, plain + " found in UTF-16");
        }
    }

    [Fact]
    public void A_blob_copied_to_another_connection_does_not_decrypt()
    {
        using var temp = new TempDataFolder();
        var store = new DpapiSecretStore(temp.Paths);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.Save(a, Credentials);
        File.Copy(Path.Combine(temp.Paths.SecretsFolder, a.ToString("N") + ".bin"), Path.Combine(temp.Paths.SecretsFolder, b.ToString("N") + ".bin"));
        var result = store.Load(b);
        Assert.Equal(SecretLoadStatus.Undecryptable, result.Status);
        Assert.Null(result.Credentials);
    }

    [Fact]
    public void Damaged_files_and_missing_secrets_give_safe_messages()
    {
        using var temp = new TempDataFolder();
        var store = new DpapiSecretStore(temp.Paths);
        var id = Guid.NewGuid();
        Assert.Equal(SecretLoadStatus.NotFound, store.Load(id).Status);
        store.Save(id, Credentials);
        File.WriteAllBytes(Path.Combine(temp.Paths.SecretsFolder, id.ToString("N") + ".bin"), [1, 2, 3, 4]);
        var result = store.Load(id);
        Assert.Equal(SecretLoadStatus.Undecryptable, result.Status);
        Assert.Contains("Windows user", result.Message, StringComparison.Ordinal);
        Assert.True(store.Delete(id));
        Assert.False(store.Exists(id));
        Assert.False(store.Delete(id));
    }
}

public class JsonStoreTests
{
    [Fact]
    public void Theme_preference_round_trips()
    {
        using var temp = new TempDataFolder();
        var store = new JsonSettingsStore(temp.Paths);
        Assert.Equal(ThemePreference.System, store.Load().Value.Theme);
        store.Save(new AppSettings { Theme = ThemePreference.Dark });
        Assert.Equal(ThemePreference.Dark, new JsonSettingsStore(temp.Paths).Load().Value.Theme);
        Assert.Contains("\"Dark\"", File.ReadAllText(temp.Paths.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Window_layout_and_last_connection_round_trip()
    {
        using var temp = new TempDataFolder();
        var id = Guid.NewGuid();
        var layout = new WindowLayout { X = -1200, Y = 40, Width = 1100, Height = 760, Maximized = true, NavigationPaneWidth = 300, TaskPanelHeight = 220, BottomTab = "Event log" };
        new JsonSettingsStore(temp.Paths).Save(new AppSettings { Theme = ThemePreference.Dark, Window = layout, LastConnectionId = id });

        var loaded = new JsonSettingsStore(temp.Paths).Load();
        Assert.Empty(loaded.Warnings);
        Assert.Equal(ThemePreference.Dark, loaded.Value.Theme);
        Assert.Equal(layout, loaded.Value.Window);
        Assert.Equal(id, loaded.Value.LastConnectionId);
        var json = File.ReadAllText(temp.Paths.SettingsFile);
        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal); // additive fields: the format version is unchanged
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_written_before_the_window_layout_existed_still_load()
    {
        using var temp = new TempDataFolder();
        File.WriteAllText(temp.Paths.SettingsFile, "{\n  \"format\": \"storiva-settings\",\n  \"version\": 1,\n  \"theme\": \"Dark\"\n}");
        var result = new JsonSettingsStore(temp.Paths).Load();
        Assert.Empty(result.Warnings);
        Assert.False(result.ReadOnly);
        Assert.Equal(ThemePreference.Dark, result.Value.Theme);
        Assert.Null(result.Value.Window);
        Assert.Null(result.Value.LastConnectionId);
    }

    [Theory]
    [InlineData("{\"x\": 0, \"y\": 0, \"width\": -5, \"height\": 600}")]
    [InlineData("{\"x\": 0, \"y\": 0, \"width\": 999999, \"height\": 600}")]
    [InlineData("{\"x\": 0, \"y\": 0, \"width\": 900, \"height\": 600, \"navigationPaneWidth\": -1}")]
    public void Implausible_window_geometry_is_dropped_and_the_theme_kept(string window)
    {
        using var temp = new TempDataFolder();
        File.WriteAllText(temp.Paths.SettingsFile, $"{{\"version\": 1, \"theme\": \"Light\", \"window\": {window}, \"lastConnectionId\": \"00000000-0000-0000-0000-000000000000\"}}");
        var result = new JsonSettingsStore(temp.Paths).Load();
        Assert.Equal(ThemePreference.Light, result.Value.Theme);
        Assert.Null(result.Value.Window);
        Assert.Null(result.Value.LastConnectionId);
    }

    [Fact]
    public void Corrupt_settings_are_moved_aside_and_defaults_used()
    {
        using var temp = new TempDataFolder();
        File.WriteAllText(temp.Paths.SettingsFile, "{ not json");
        var result = new JsonSettingsStore(temp.Paths, () => new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)).Load();
        Assert.Equal(ThemePreference.System, result.Value.Theme);
        Assert.NotEmpty(result.Warnings);
        Assert.True(File.Exists(temp.Paths.SettingsFile + ".corrupt-20261015-120000"));
        Assert.False(File.Exists(temp.Paths.SettingsFile));
    }

    [Fact]
    public void Settings_from_a_newer_version_are_read_but_flagged_read_only()
    {
        using var temp = new TempDataFolder();
        File.WriteAllText(temp.Paths.SettingsFile, "{\"version\": 99, \"theme\": \"Light\"}");
        var result = new JsonSettingsStore(temp.Paths).Load();
        Assert.True(result.ReadOnly);
        Assert.Equal(ThemePreference.Light, result.Value.Theme);
    }

    [Fact]
    public void Connections_round_trip_without_any_secret_field()
    {
        using var temp = new TempDataFolder();
        var store = new JsonConnectionStore(temp.Paths);
        var profile = new ConnectionProfile
        {
            Name = "MinIO lab",
            Provider = StorageProvider.S3Compatible,
            Endpoint = "https://minio.example.internal:9000",
            Region = "us-east-1",
            ForcePathStyle = true,
            UseFlexibleChecksums = false,
            LastTest = new ConnectionTestRecord(ConnectionStatus.Connected, DateTimeOffset.UnixEpoch, "Connected. 2 bucket(s)."),
        };
        store.Save([profile]);
        var json = File.ReadAllText(temp.Paths.ConnectionsFile);
        // Field names only: "AccessKey" legitimately appears as the authentication method value.
        foreach (var field in new[] { "secret", "accessKeyId", "sessionToken", "password" })
        {
            Assert.DoesNotContain("\"" + field, json, StringComparison.OrdinalIgnoreCase);
        }
        var loaded = Assert.Single(new JsonConnectionStore(temp.Paths).Load().Value);
        Assert.Equal(profile, loaded);
    }

    [Fact]
    public void Invalid_and_unknown_entries_are_skipped_not_fatal()
    {
        using var temp = new TempDataFolder();
        var good = Guid.NewGuid();
        File.WriteAllText(temp.Paths.ConnectionsFile, $$"""
            {
              "format": "storiva-connections", "version": 1,
              "connections": [
                { "id": "{{good}}", "name": "AWS", "provider": "AwsS3", "region": "eu-west-1", "authentication": "AccessKey" },
                { "id": "{{Guid.NewGuid()}}", "name": "Future", "provider": "QuantumStorage", "region": "x", "authentication": "AccessKey" },
                { "id": "{{Guid.NewGuid()}}", "name": "Bad URL", "provider": "S3Compatible", "endpoint": "javascript:alert(1)", "region": "us-east-1", "authentication": "AccessKey" },
                { "name": "No id", "provider": "AwsS3", "authentication": "AccessKey" },
                null
              ]
            }
            """);
        var result = new JsonConnectionStore(temp.Paths).Load();
        Assert.Equal(good, Assert.Single(result.Value).Id);
        Assert.Contains(result.Warnings, w => w.Contains("4 invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void Corrupt_connections_file_is_kept_aside_and_newer_versions_are_not_overwritten()
    {
        using var temp = new TempDataFolder();
        File.WriteAllText(temp.Paths.ConnectionsFile, "[[[");
        var corrupt = new JsonConnectionStore(temp.Paths).Load();
        Assert.Empty(corrupt.Value);
        Assert.Single(Directory.GetFiles(temp.Root, "connections.json.corrupt-*"));

        File.WriteAllText(temp.Paths.ConnectionsFile, "{\"version\": 7, \"connections\": []}");
        var store = new JsonConnectionStore(temp.Paths);
        Assert.True(store.Load().ReadOnly);
        Assert.Throws<InvalidOperationException>(() => store.Save([]));
    }

    [Fact]
    public void Default_data_root_is_local_app_data()
    {
        var paths = new StorivaDataPaths();
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), paths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Storiva", paths.Root, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(paths.Root, "secrets"), paths.SecretsFolder);
    }
}
