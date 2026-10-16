using Storiva.Core.Storage;

namespace Storiva.Tests.Fakes;

/// <summary>
/// Small fixed hierarchy for the navigator and listing tests (test fixture only; the app never shows sample data).
/// Folders are implied by the paths of their contents; <see cref="Remove"/> drops an item and everything below it.
/// </summary>
internal sealed class InMemoryCatalog : IStorageCatalog
{
    private static readonly DateTimeOffset Base = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly List<StorageItem> _items = [];

    public InMemoryCatalog()
    {
        Bucket("assets");
        Bucket("backups");
        Folder("assets/images");
        Folder("assets/images/icons");
        Folder("assets/archives");
        Folder("backups/weekly");
        File("assets/images/banner-2026.png", 245_760, 5);
        File("assets/images/logo-draft.svg", 2_048, 1);
        File("assets/images/photo-001.jpg", 3_145_728, 3);
        File("assets/images/photo-002.jpg", 2_097_152, 4);
        File("assets/images/photo-010.jpg", 1_572_864, 6);
        File("assets/images/thumbnail.webp", 40_960, 2);
        File("assets/images/icons/icon-16.png", 512, 2);
        File("assets/archives/site-backup-2026-09.zip", 52_428_800, 9, "STANDARD");
        File("assets/archives/assets-export.tar", 104_857_600, 7, "GLACIER");
        File("assets/archives/site-backup-2026-08.zip", 50_331_648, 8, "STANDARD");
        File("backups/weekly/full-2026-W36.tar", 1_961_612_736, 10, "STANDARD_IA");
    }

    public string SourceDescription => "Test fixture";

    public IReadOnlyList<string> Regions => [];

    public IReadOnlyList<string> StorageClasses => ["STANDARD", "STANDARD_IA", "GLACIER"];

    public bool Exists(StoragePath path) => path.IsRoot || _items.Any(i => i.Path == path && i.IsContainer);

    public StorageItem? Find(StoragePath path) => _items.FirstOrDefault(i => i.Path == path);

    public bool IsReadOnly(StoragePath path) => false;

    public IReadOnlyList<StorageItem> GetChildren(StoragePath path) => _items.Where(i => i.Path.Parent == path).ToList();

    public void Remove(StoragePath path) => _items.RemoveAll(i => i.Path == path || i.Path.IsWithin(path));

    private void Bucket(string name) => _items.Add(new StorageItem(StoragePath.Parse(name), StorageItemKind.Bucket, null, Base, null, Region: "us-east-1"));

    private void Folder(string path) => _items.Add(new StorageItem(StoragePath.Parse(path), StorageItemKind.Folder, null, null, null));

    private void File(string path, long size, int day, string storageClass = "STANDARD") =>
        _items.Add(new StorageItem(StoragePath.Parse(path), StorageItemKind.File, size, Base.AddDays(day), storageClass));
}
