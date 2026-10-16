using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Storiva.Infrastructure.Persistence;

/// <summary>
/// Locations of Storiva's local data. Default root: %LOCALAPPDATA%\Storiva (per Windows user, not roaming).
/// <code>
/// settings.json      non-secret preferences (theme)
/// connections.json   non-secret connection settings
/// secrets\*.bin      DPAPI-protected credentials, one file per connection
/// </code>
/// </summary>
public sealed class StorivaDataPaths
{
    public StorivaDataPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Storiva"));
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string ConnectionsFile => Path.Combine(Root, "connections.json");

    public string SecretsFolder => Path.Combine(Root, "secrets");
}

/// <summary>Writes files atomically (temporary file + rename), so a crash never leaves half a settings file.</summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Moves an unreadable file aside ("name.corrupt-20261015-171131") and returns the new name.</summary>
    public static string? QuarantineCorrupt(string path, Func<DateTimeOffset> clock)
    {
        try
        {
            var target = $"{path}.corrupt-{clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
            File.Move(path, target, overwrite: true);
            return Path.GetFileName(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal static class StorivaJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
