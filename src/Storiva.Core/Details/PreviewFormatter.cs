using System.Globalization;
using System.Text;
using System.Text.Json;
using Storiva.Core.Listing;
using Storiva.Core.Storage;

namespace Storiva.Core.Details;

public enum PreviewKind
{
    NotAFile,
    Text,
    Json,
    Hex,
    Unsupported,
}

public sealed record PreviewResult(PreviewKind Kind, string Content, string Note);

/// <summary>Decides how the Preview tab shows a file and formats it. Never executes or interprets content.</summary>
public static class PreviewFormatter
{
    public const int SampleBytes = 4096;

    // The preview is plain text in a text box (never HTML), so characters are shown as they are instead of the
    // default \uXXXX escapes for non-ASCII letters and HTML-sensitive characters such as < and &.
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".html", ".css", ".js", ".sql", ".log", ".xml", ".yaml", ".yml", ".ini", ".conf", ".toml",
    };

    public static PreviewKind Decide(StorageItem? item)
    {
        if (item is null || item.Kind != StorageItemKind.File)
        {
            return PreviewKind.NotAFile;
        }

        var extension = Path.GetExtension(item.Name);
        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.Json;
        }

        if (TextExtensions.Contains(extension))
        {
            return PreviewKind.Text;
        }

        return FileTypes.Categorize(item) is FileCategory.Image or FileCategory.Video or FileCategory.Audio
            ? PreviewKind.Unsupported
            : PreviewKind.Hex;
    }

    /// <summary>Formats the first bytes read from the server (a ranged read of at most <see cref="SampleBytes"/>), as text
    /// or hex; nothing is rendered or executed.</summary>
    public static PreviewResult Build(StorageItem? item, Func<StorageItem, byte[]> sample)
    {
        var kind = Decide(item);
        if (kind == PreviewKind.NotAFile)
        {
            return new(kind, string.Empty, "Select a file to preview it.");
        }

        var file = item!;
        if (kind == PreviewKind.Unsupported)
        {
            return new(kind, string.Empty,
                $"Previewing {FileTypes.Describe(file).ToLowerInvariant()} content is not supported. Download the file to open it with another program.");
        }

        var bytes = sample(file);
        var truncated = file.Size > bytes.Length;
        var note = "Content from the server, shown as plain text (never rendered or executed)" + (truncated ? $" — first {bytes.Length:N0} of {file.Size:N0} bytes." : ".");
        return kind switch
        {
            PreviewKind.Json => new(kind, PrettyJson(Encoding.UTF8.GetString(bytes)), note),
            PreviewKind.Text => new(kind, Encoding.UTF8.GetString(bytes).ReplaceLineEndings("\r\n"), note),
            _ => new(kind, HexDump(bytes), note),
        };
    }

    /// <summary>
    /// Reference layout [F E1]: 8-digit hex offset, 16 bytes in two groups of 8, ASCII column with '.' for
    /// non-printable bytes.
    /// </summary>
    public static string HexDump(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var line = bytes.Slice(offset, Math.Min(16, bytes.Length - offset));
            sb.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                sb.Append(i < line.Length ? line[i].ToString("X2", CultureInfo.InvariantCulture) : "  ");
                sb.Append(i == 7 ? "  " : " ");
            }

            sb.Append(' ');
            foreach (var b in line)
            {
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Re-indents JSON; returns the input unchanged when it is not valid JSON.</summary>
    public static string PrettyJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson).ReplaceLineEndings("\r\n");
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
