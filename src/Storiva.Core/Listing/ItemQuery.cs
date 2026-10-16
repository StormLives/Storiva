using Storiva.Core.Storage;

namespace Storiva.Core.Listing;

public enum ItemTypeFilter
{
    All,
    FoldersOnly,
    FilesOnly,
}

public enum ItemSortColumn
{
    Name,
    Type,
    Size,
    LastModified,
    StorageClass,
}

public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// Search, filter and sort settings for the object list. <see cref="Apply"/> always returns a
/// new list and never modifies the source collection.
/// </summary>
public sealed record ItemQuery(
    string SearchText = "",
    ItemTypeFilter Filter = ItemTypeFilter.All,
    ItemSortColumn SortColumn = ItemSortColumn.Name,
    SortDirection Direction = SortDirection.Ascending)
{
    public static ItemQuery Default { get; } = new();

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Same column toggles the direction; a new column starts ascending.</summary>
    public ItemQuery WithSortToggled(ItemSortColumn column) =>
        column == SortColumn
            ? this with { Direction = Direction == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending }
            : this with { SortColumn = column, Direction = SortDirection.Ascending };

    public IReadOnlyList<StorageItem> Apply(IEnumerable<StorageItem> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var term = SearchText.Trim();

        var filtered = source.Where(item =>
            Filter switch
            {
                ItemTypeFilter.FoldersOnly => item.IsContainer,
                ItemTypeFilter.FilesOnly => !item.IsContainer,
                _ => true,
            }
            && (term.Length == 0 || item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));

        // Containers stay above files whatever the direction (file-browser convention).
        var ordered = filtered.OrderBy(item => item.IsContainer ? 0 : 1);
        var comparer = new ItemComparer(SortColumn);
        var sorted = Direction == SortDirection.Ascending
            ? ordered.ThenBy(item => item, comparer)
            : ordered.ThenByDescending(item => item, comparer);

        return sorted.ToList();
    }

    private sealed class ItemComparer(ItemSortColumn column) : IComparer<StorageItem>
    {
        public int Compare(StorageItem? x, StorageItem? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var result = column switch
            {
                ItemSortColumn.Type => string.Compare(FileTypes.Describe(x), FileTypes.Describe(y), StringComparison.OrdinalIgnoreCase),
                ItemSortColumn.Size => Nullable.Compare(x.Size, y.Size),
                ItemSortColumn.LastModified => Nullable.Compare(x.LastModified, y.LastModified),
                ItemSortColumn.StorageClass => string.Compare(x.StorageClass, y.StorageClass, StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };

            // Name is the primary key for Name and the stable tie-breaker for the rest.
            return result != 0 ? result : NaturalStringComparer.Instance.Compare(x.Name, y.Name);
        }
    }
}
