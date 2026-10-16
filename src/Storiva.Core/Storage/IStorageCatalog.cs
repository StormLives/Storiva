namespace Storiva.Core.Storage;

/// <summary>
/// Read access to a storage hierarchy for the navigation, tree and object list. The implementation of a real
/// connection (<c>RemoteStorageCatalog</c>) is a cache filled asynchronously; changes are made through
/// <c>IObjectStorageClient</c>, never through this interface.
/// </summary>
public interface IStorageCatalog
{
    /// <summary>Human readable description of the data source, shown in the status bar.</summary>
    string SourceDescription { get; }

    /// <summary>Regions offered by the "Create New Bucket" dialog.</summary>
    IReadOnlyList<string> Regions { get; }

    /// <summary>Storage classes offered by "Change Storage Class to".</summary>
    IReadOnlyList<string> StorageClasses { get; }

    /// <summary>True for navigable locations only: the root, buckets and folders (not files).</summary>
    bool Exists(StoragePath path);

    /// <summary>The item at <paramref name="path"/>, or null for the root and for unknown paths.</summary>
    StorageItem? Find(StoragePath path);

    /// <summary>True when the location cannot be modified from the client side.</summary>
    bool IsReadOnly(StoragePath path);

    /// <summary>Children of <paramref name="path"/> known so far. The root returns the buckets.</summary>
    IReadOnlyList<StorageItem> GetChildren(StoragePath path);

    /// <summary>Whether the tree should show an expand glyph for <paramref name="path"/>.</summary>
    bool MayHaveChildContainers(StoragePath path) => GetChildren(path).Any(c => c.IsContainer);
}

/// <summary>What the window shows while no connection is open: nothing.</summary>
public sealed class EmptyStorageCatalog : IStorageCatalog
{
    public static EmptyStorageCatalog Instance { get; } = new();

    public string SourceDescription => "No connection";

    public IReadOnlyList<string> Regions => [];

    public IReadOnlyList<string> StorageClasses => [];

    public bool Exists(StoragePath path) => path.IsRoot;

    public StorageItem? Find(StoragePath path) => null;

    public bool IsReadOnly(StoragePath path) => true;

    public IReadOnlyList<StorageItem> GetChildren(StoragePath path) => [];

    public bool MayHaveChildContainers(StoragePath path) => false;
}

/// <summary>Catalog whose implementation is swapped when a connection opens or closes, so the navigator and the
/// main window keep a single reference.</summary>
public sealed class SwitchableStorageCatalog : IStorageCatalog
{
    public SwitchableStorageCatalog(IStorageCatalog inner) => Inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public IStorageCatalog Inner { get; set; }

    public string SourceDescription => Inner.SourceDescription;

    public IReadOnlyList<string> Regions => Inner.Regions;

    public IReadOnlyList<string> StorageClasses => Inner.StorageClasses;

    public bool Exists(StoragePath path) => Inner.Exists(path);

    public StorageItem? Find(StoragePath path) => Inner.Find(path);

    public bool IsReadOnly(StoragePath path) => Inner.IsReadOnly(path);

    public IReadOnlyList<StorageItem> GetChildren(StoragePath path) => Inner.GetChildren(path);

    public bool MayHaveChildContainers(StoragePath path) => Inner.MayHaveChildContainers(path);
}
