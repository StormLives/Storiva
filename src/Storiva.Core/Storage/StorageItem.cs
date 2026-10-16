namespace Storiva.Core.Storage;

public enum StorageItemKind
{
    Bucket,
    Folder,
    File,
}

/// <summary>An entry shown in the navigation tree or the object list.</summary>
/// <param name="Region">Buckets only: the bucket's region, when the server reports it.</param>
/// <param name="IsExternal">Buckets only: opened by name with "Add External Bucket" (not in the bucket list). The server
/// decides what is allowed in it.</param>
/// <param name="Key">Objects only: the exact key (files) or prefix ending with "/" (folders) returned by the
/// server. A display path cannot represent every key (e.g. "a//b"), so remote operations always use this.</param>
/// <param name="ETag">Files only: entity tag from the listing.</param>
public sealed record StorageItem(
    StoragePath Path,
    StorageItemKind Kind,
    long? Size,
    DateTimeOffset? LastModified,
    string? StorageClass,
    string? Region = null,
    bool IsExternal = false,
    string? Key = null,
    string? ETag = null)
{
    public string Name => Path.Name;

    /// <summary>Buckets and folders can be opened; files cannot.</summary>
    public bool IsContainer => Kind != StorageItemKind.File;
}
