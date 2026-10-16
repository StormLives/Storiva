namespace Storiva.Core.Storage;

/// <summary>
/// Location inside the storage hierarchy. The root ("Home") has no segments; the first
/// segment is the bucket name, the following ones are folder names.
/// </summary>
public sealed class StoragePath : IEquatable<StoragePath>
{
    public const char Separator = '/';

    public static StoragePath Root { get; } = new(Array.Empty<string>());

    private readonly string[] _segments;

    private StoragePath(string[] segments) => _segments = segments;

    public IReadOnlyList<string> Segments => _segments;

    public bool IsRoot => _segments.Length == 0;

    public bool IsBucket => _segments.Length == 1;

    public string Name => IsRoot ? string.Empty : _segments[^1];

    public string? Bucket => IsRoot ? null : _segments[0];

    public StoragePath? Parent => IsRoot ? null : new StoragePath(_segments[..^1]);

    public static StoragePath Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Root;
        }

        var parts = text.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? Root : new StoragePath(parts);
    }

    public StoragePath Append(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains(Separator))
        {
            throw new ArgumentException("A path segment must be non-empty and must not contain '/'.", nameof(name));
        }

        var next = new string[_segments.Length + 1];
        _segments.CopyTo(next, 0);
        next[^1] = name;
        return new StoragePath(next);
    }

    /// <summary>True when this path equals <paramref name="other"/> or is below it.</summary>
    public bool IsWithin(StoragePath other)
    {
        if (other._segments.Length > _segments.Length)
        {
            return false;
        }

        for (var i = 0; i < other._segments.Length; i++)
        {
            if (!string.Equals(_segments[i], other._segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Path as shown to the user, e.g. "/my-bucket/images/".</summary>
    public string ToDisplayString() => IsRoot ? "/" : "/" + string.Join(Separator, _segments) + "/";

    public override string ToString() => string.Join(Separator, _segments);

    public bool Equals(StoragePath? other)
    {
        if (other is null || other._segments.Length != _segments.Length)
        {
            return false;
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            if (!string.Equals(_segments[i], other._segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is StoragePath other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in _segments)
        {
            hash.Add(segment, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(StoragePath? left, StoragePath? right) => Equals(left, right);

    public static bool operator !=(StoragePath? left, StoragePath? right) => !Equals(left, right);
}
