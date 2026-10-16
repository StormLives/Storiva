namespace Storiva.Core.Storage;

/// <summary>
/// Local bucket-name validation. The reference dialog states "Should contain only lowercase letters,
/// numbers, periods (.) and dashes (-)" [F]; the remaining rules follow the public S3 naming rules [S].
/// </summary>
public static class BucketNameRules
{
    public const int MinLength = 3;
    public const int MaxLength = 63;
    public const string HelpText = "Should contain only lowercase letters, numbers, periods (.) and dashes (-).";

    /// <summary>Null when valid, otherwise a user-facing message.</summary>
    public static string? Validate(string? name, IEnumerable<string> existingNames)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Enter a bucket name.";
        }

        if (name.Length is < MinLength or > MaxLength)
        {
            return $"Use {MinLength} to {MaxLength} characters.";
        }

        if (!name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-'))
        {
            return "Use only lowercase letters, numbers, periods (.) and dashes (-).";
        }

        if (!char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))
        {
            return "The name must start and end with a letter or a number.";
        }

        if (name.Contains("..", StringComparison.Ordinal) || name.Contains(".-", StringComparison.Ordinal) || name.Contains("-.", StringComparison.Ordinal))
        {
            return "Periods cannot be next to another period or a dash.";
        }

        if (LooksLikeIPv4(name))
        {
            return "The name cannot look like an IP address.";
        }

        if (existingNames.Contains(name, StringComparer.Ordinal))
        {
            return $"A bucket named \"{name}\" already exists.";
        }

        return null;
    }

    private static bool LooksLikeIPv4(string name)
    {
        var parts = name.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is >= 1 and <= 3 && p.All(char.IsAsciiDigit) && int.Parse(p, System.Globalization.CultureInfo.InvariantCulture) <= 255);
    }

    /// <summary>"new-bucket-" + 8 hex digits, like the reference default [F E7]; deterministic per seed.</summary>
    public static string SuggestName(int seed, IEnumerable<string> existingNames)
    {
        var existing = existingNames.ToHashSet(StringComparer.Ordinal);
        for (var attempt = 0; ; attempt++)
        {
            var hash = unchecked((uint)((seed + attempt) * 2654435761u) ^ 0x5bd1e995u);
            var name = $"new-bucket-{hash:x8}";
            if (!existing.Contains(name))
            {
                return name;
            }
        }
    }
}
