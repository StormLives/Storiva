using Storiva.Core.Storage;

namespace Storiva.Core.Navigation;

/// <summary>Session-only bookmarks (star in the path bar, Bookmarks menu). Nothing is written to disk.</summary>
public sealed class BookmarkList
{
    public const int MaxBookmarks = 30;

    private readonly List<StoragePath> _items = [];

    public event EventHandler? Changed;

    public IReadOnlyList<StoragePath> Items => _items;

    public bool Contains(StoragePath path) => _items.Contains(path);

    /// <summary>Adds or removes <paramref name="path"/>. Returns true when it is bookmarked afterwards.</summary>
    public bool Toggle(StoragePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot)
        {
            throw new ArgumentException("Home cannot be bookmarked.", nameof(path));
        }

        if (_items.Remove(path))
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (_items.Count >= MaxBookmarks)
        {
            throw new InvalidOperationException($"At most {MaxBookmarks} bookmarks are allowed.");
        }

        _items.Add(path);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes bookmarks whose location no longer exists (after deletes or a reset).</summary>
    public int Prune(Func<StoragePath, bool> exists)
    {
        var removed = _items.RemoveAll(p => !exists(p));
        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public void Clear()
    {
        if (_items.Count == 0)
        {
            return;
        }

        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
