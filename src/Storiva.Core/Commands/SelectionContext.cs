using Storiva.Core.Storage;

namespace Storiva.Core.Commands;

public enum FocusArea
{
    /// <summary>Bucket tree (bucket commands win ambiguous shortcuts).</summary>
    Tree,

    /// <summary>Object list.</summary>
    List,

    /// <summary>Text boxes, tabs and anything else.</summary>
    Other,
}

/// <summary>Snapshot of the UI state that decides which commands are enabled.</summary>
/// <param name="Location">Current location of the object list.</param>
/// <param name="Selected">Items selected in the object list.</param>
/// <param name="TreeBucket">Bucket selected in the tree (or containing the tree selection).</param>
/// <param name="ClipboardCount">Items in Storiva's internal clipboard (Cut/Copy).</param>
/// <param name="Connected">True while a connection is open. Without one only account, window and help commands work.</param>
public sealed record SelectionContext(
    StoragePath Location,
    IReadOnlyList<StorageItem> Selected,
    StoragePath? TreeBucket,
    FocusArea Focus,
    int ClipboardCount,
    bool CanGoBack = false,
    bool CanGoForward = false,
    int ListCount = 0,
    bool Connected = true)
{
    public bool InBucket => !Location.IsRoot;

    public int FileCount => Selected.Count(i => i.Kind == StorageItemKind.File);

    public int ContainerCount => Selected.Count(i => i.Kind != StorageItemKind.File);

    public bool HasSelection => Selected.Count > 0;

    public bool SingleSelection => Selected.Count == 1;

    /// <summary>
    /// The bucket the Buckets menu acts on: the tree selection, or a single bucket selected in the Home list.
    /// </summary>
    public StoragePath? ActiveBucket =>
        TreeBucket ?? (Location.IsRoot && Selected is [{ Kind: StorageItemKind.Bucket } bucket] ? bucket.Path : null);

    /// <summary>Only folders and files (not buckets) can be cut, copied, renamed or deleted from the Files menu.</summary>
    public bool SelectionIsObjects => HasSelection && Selected.All(i => i.Kind != StorageItemKind.Bucket);

    public static SelectionContext Empty { get; } = new(StoragePath.Root, [], null, FocusArea.List, 0);
}
