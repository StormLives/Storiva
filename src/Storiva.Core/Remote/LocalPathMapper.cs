namespace Storiva.Core.Remote;

/// <summary>What to do when a downloaded file already exists locally.</summary>
public enum ConflictPolicy
{
    /// <summary>Keep the existing file and save the new one as "name (1).ext".</summary>
    KeepBoth,
    Overwrite,
    Skip,
}

/// <summary>
/// Maps object keys to local file paths. Keys are arbitrary strings: they may contain "..", characters Windows does
/// not allow, reserved device names or trailing dots. The mapping never escapes the chosen folder.
/// </summary>
public static class LocalPathMapper
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>A file or folder name Windows accepts, derived from one key segment.</summary>
    public static string SafeName(string segment)
    {
        var chars = segment.Select(c => c < 32 || Invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).TrimEnd('.', ' ');
        if (name.Length == 0 || name is "." or "..")
        {
            name = "_" + name.Replace('.', '_');
        }

        var stem = name.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            name = "_" + name;
        }

        return name.Length > 200 ? name[..200] : name;
    }

    /// <summary>
    /// Local path for <paramref name="relativeKey"/> (key without the prefix being downloaded) below
    /// <paramref name="root"/>. Empty segments ("a//b") are dropped.
    /// </summary>
    /// <exception cref="ArgumentException">The key has no usable name.</exception>
    public static string ToLocalPath(string root, string relativeKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var segments = relativeKey.Split('/').Where(s => s.Length > 0).Select(SafeName).ToList();
        if (segments.Count == 0)
        {
            throw new ArgumentException("The object key has no file name.", nameof(relativeKey));
        }

        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine([fullRoot, .. segments]));
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The object key would be saved outside the chosen folder.", nameof(relativeKey));
        }

        return path;
    }

    /// <summary>"report.pdf" → "report (1).pdf", "report (2).pdf"… for the first name that does not exist.</summary>
    public static string UniquePath(string path, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        if (!exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not find a free file name.");
    }

    /// <summary>Object key for a local file below <paramref name="localRoot"/>, placed under <paramref name="prefix"/>.</summary>
    public static string ToKey(string prefix, string localRoot, string localFile)
    {
        var relative = Path.GetRelativePath(localRoot, localFile);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ArgumentException("The file is not inside the selected folder.", nameof(localFile));
        }

        return prefix + relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
