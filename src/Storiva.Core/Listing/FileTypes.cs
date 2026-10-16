using System.Globalization;
using Storiva.Core.Storage;

namespace Storiva.Core.Listing;

public enum FileCategory
{
    Bucket,
    Folder,
    Generic,
    Text,
    Image,
    Archive,
    Spreadsheet,
    Code,
    Video,
    Audio,
}

/// <summary>Presentation helpers for list columns: type names, categories, sizes and dates.</summary>
public static class FileTypes
{
    private static readonly Dictionary<string, (string Description, FileCategory Category)> ByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = ("Text document", FileCategory.Text),
            [".md"] = ("Markdown document", FileCategory.Text),
            [".pdf"] = ("PDF document", FileCategory.Text),
            [".docx"] = ("Word document", FileCategory.Text),
            [".png"] = ("PNG image", FileCategory.Image),
            [".jpg"] = ("JPEG image", FileCategory.Image),
            [".svg"] = ("SVG image", FileCategory.Image),
            [".webp"] = ("WebP image", FileCategory.Image),
            [".zip"] = ("ZIP archive", FileCategory.Archive),
            [".gz"] = ("GZip archive", FileCategory.Archive),
            [".tar"] = ("TAR archive", FileCategory.Archive),
            [".csv"] = ("CSV data", FileCategory.Spreadsheet),
            [".xlsx"] = ("Excel workbook", FileCategory.Spreadsheet),
            [".json"] = ("JSON data", FileCategory.Code),
            [".html"] = ("HTML page", FileCategory.Code),
            [".css"] = ("Stylesheet", FileCategory.Code),
            [".js"] = ("JavaScript file", FileCategory.Code),
            [".mp4"] = ("MP4 video", FileCategory.Video),
            [".mp3"] = ("MP3 audio", FileCategory.Audio),
        };

    public static string Describe(StorageItem item) => item.Kind switch
    {
        StorageItemKind.Bucket => "Bucket",
        StorageItemKind.Folder => "Folder",
        _ => ByExtension.TryGetValue(Path.GetExtension(item.Name), out var info)
            ? info.Description
            : DescribeUnknown(item.Name),
    };

    public static FileCategory Categorize(StorageItem item) => item.Kind switch
    {
        StorageItemKind.Bucket => FileCategory.Bucket,
        StorageItemKind.Folder => FileCategory.Folder,
        _ => ByExtension.TryGetValue(Path.GetExtension(item.Name), out var info) ? info.Category : FileCategory.Generic,
    };

    /// <summary>Binary units with one decimal, invariant culture: "0 B", "1.5 KB", "12 MB". Null for containers.</summary>
    public static string FormatSize(long? bytes)
    {
        if (bytes is null)
        {
            return string.Empty;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(bytes.Value);
        if (bytes.Value < 1024)
        {
            return bytes.Value.ToString(CultureInfo.InvariantCulture) + " B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes.Value;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>"2026-10-01 14:30 UTC": dates are shown in UTC, as S3 reports them.</summary>
    public static string FormatDate(DateTimeOffset? value) =>
        value is null
            ? string.Empty
            : value.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>MIME type by extension (default application/octet-stream).</summary>
    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".html" => "text/html",
        ".css" => "text/css",
        ".js" => "text/javascript",
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".gz" => "application/gzip",
        ".tar" => "application/x-tar",
        ".mp4" => "video/mp4",
        ".mp3" => "audio/mpeg",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream",
    };

    private static string DescribeUnknown(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length > 1 ? extension[1..].ToUpperInvariant() + " file" : "File";
    }
}
