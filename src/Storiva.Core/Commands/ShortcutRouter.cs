namespace Storiva.Core.Commands;

/// <summary>
/// Resolves a keyboard shortcut to a command. Several shortcuts appear in both Files and Buckets in the
/// reference [F E5/E6]; Storiva resolves them by focus: the bucket tree selects the Buckets command,
/// anything else the Files command [H/S].
/// </summary>
public static class ShortcutRouter
{
    private static readonly Dictionary<string, List<CommandDefinition>> ByShortcut = CommandCatalog.Definitions
        .Where(d => d.Shortcut is not null and not "Enter" and not "Alt+F4")
        .GroupBy(d => Normalize(d.Shortcut!))
        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    public static IReadOnlyCollection<string> KnownShortcuts => ByShortcut.Keys;

    /// <summary>Shortcuts that map to more than one command (resolved by focus).</summary>
    public static IEnumerable<string> AmbiguousShortcuts =>
        ByShortcut.Where(kv => kv.Value.Select(d => d.Scope).Distinct().Count() > 1).Select(kv => kv.Key);

    public static CommandId? Resolve(string shortcut, FocusArea focus)
    {
        var key = Normalize(shortcut);
        if (!ByShortcut.TryGetValue(key, out var candidates))
        {
            return null;
        }

        // Outside the tree and the list (text boxes, detail tabs), plain keys such as Del or F2 belong to the
        // focused control: deleting a tag row must never delete the selected object. Only F1/F5 and
        // Ctrl/Alt combinations are routed there.
        if (focus == FocusArea.Other && !key.Contains("Ctrl+", StringComparison.Ordinal) && !key.Contains("Alt+", StringComparison.Ordinal) && key is not ("F1" or "F5"))
        {
            return null;
        }

        var preferred = focus == FocusArea.Tree ? ShortcutScope.Buckets : ShortcutScope.Files;
        return (candidates.FirstOrDefault(d => d.Scope == preferred)
                ?? candidates.FirstOrDefault(d => d.Scope == ShortcutScope.Global)
                ?? candidates[0]).Id;
    }

    /// <summary>"ctrl + shift+n" → "Ctrl+Shift+N"; modifiers ordered Ctrl, Shift, Alt.</summary>
    public static string Normalize(string shortcut)
    {
        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = new List<string>();
        string? key = null;
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers.Add("Ctrl"); break;
                case "SHIFT": modifiers.Add("Shift"); break;
                case "ALT": modifiers.Add("Alt"); break;
                case "DEL" or "DELETE": key = "Del"; break;
                default: key = part.Length == 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]; break;
            }
        }

        var ordered = new[] { "Ctrl", "Shift", "Alt" }.Where(modifiers.Contains);
        return string.Join("+", ordered.Append(key ?? string.Empty));
    }
}
