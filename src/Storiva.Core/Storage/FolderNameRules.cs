namespace Storiva.Core.Storage;

/// <summary>Local validation for new folder names (Storiva rules, not provider rules).</summary>
public static class FolderNameRules
{
    public const int MaxLength = 128;

    private static readonly char[] Forbidden = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Returns null when the name is valid, otherwise a user-facing error message.</summary>
    public static string? Validate(string? name, IEnumerable<string> existingNames)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a folder name.";
        }

        if (name != name.Trim())
        {
            return "The name cannot start or end with a space.";
        }

        if (name.Length > MaxLength)
        {
            return $"The name cannot be longer than {MaxLength} characters.";
        }

        if (name is "." or "..")
        {
            return "\".\" and \"..\" are reserved names.";
        }

        if (name.IndexOfAny(Forbidden) >= 0 || name.Any(char.IsControl))
        {
            return "The name cannot contain / \\ : * ? \" < > | or control characters.";
        }

        if (existingNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return $"An item named \"{name}\" already exists here.";
        }

        return null;
    }
}
