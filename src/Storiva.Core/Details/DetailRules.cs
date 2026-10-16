namespace Storiva.Core.Details;

/// <summary>Helpers for the (read-only) Permissions grid.</summary>
public static class AclRules
{
    /// <summary>One row per grantee, in display order; Full Control implies every permission.</summary>
    public static IReadOnlyList<Grant> Normalize(IReadOnlyList<Grant> grants) =>
        Enum.GetValues<Grantee>()
            .Select(g => new Grant(g, Expand(grants.Where(x => x.Grantee == g).Aggregate(Permission.None, (acc, x) => acc | x.Permissions))))
            .ToList();

    private static Permission Expand(Permission p) => (p & Permission.FullControl) != 0 ? Permission.All : p;

    public static bool IsPublic(IReadOnlyList<Grant> grants) => grants.Any(g => g.Grantee == Grantee.AllUsers && g.Permissions != Permission.None);
}

/// <summary>Validation for tags (Tags tab). Limits follow common S3-compatible limits [S].</summary>
public static class TagRules
{
    public const int MaxKeyLength = 128;
    public const int MaxValueLength = 256;
    public const int MaxObjectTags = 10;
    public const int MaxBucketTags = 50;

    public static string? Validate(string? key, string? value, IEnumerable<string> otherKeys, int maxTags)
    {
        var others = otherKeys.ToList();
        if (string.IsNullOrWhiteSpace(key))
        {
            return "Enter a tag key.";
        }

        if (key.Length > MaxKeyLength)
        {
            return $"Tag keys can have at most {MaxKeyLength} characters.";
        }

        if ((value ?? string.Empty).Length > MaxValueLength)
        {
            return $"Tag values can have at most {MaxValueLength} characters.";
        }

        if ((key + value).Any(char.IsControl))
        {
            return "Tags cannot contain control characters.";
        }

        if (others.Contains(key, StringComparer.Ordinal))
        {
            return $"The key \"{key}\" is already used.";
        }

        return others.Count >= maxTags ? $"At most {maxTags} tags are allowed here." : null;
    }
}

/// <summary>
/// Pending edits of a tab: the user changes <see cref="Current"/>; Apply commits it, Reload discards it
/// (reference: "Apply changes" / "Reload" [F]).
/// </summary>
public sealed class EditSession<T>
{
    private readonly Func<IReadOnlyList<T>> _load;
    private readonly Action<IReadOnlyList<T>> _save;

    public EditSession(Func<IReadOnlyList<T>> load, Action<IReadOnlyList<T>> save)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        Reload();
    }

    public IReadOnlyList<T> Original { get; private set; } = [];

    public IReadOnlyList<T> Current { get; private set; } = [];

    public bool IsDirty => !Original.SequenceEqual(Current);

    public void Update(IReadOnlyList<T> next) => Current = next.ToList();

    public void Apply()
    {
        _save(Current);
        Original = Current.ToList();
    }

    public void Reload()
    {
        Original = _load().ToList();
        Current = Original.ToList();
    }
}
