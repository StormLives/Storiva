using Storiva.Core.Storage;

namespace Storiva.Core.Details;

/// <summary>Grantee rows of the Permissions tab. The reference shows Owner · Any AWS Users · All Users [F];
/// Storiva uses provider-neutral names [S].</summary>
public enum Grantee
{
    Owner,
    AuthenticatedUsers,
    AllUsers,
}

/// <summary>Permission columns of the Permissions tab, in the reference order [F]:
/// Full Control · Read · Write · Read Permissions · Write Permissions.</summary>
[Flags]
public enum Permission
{
    None = 0,
    FullControl = 1,
    Read = 2,
    Write = 4,
    ReadAcl = 8,
    WriteAcl = 16,
    All = FullControl | Read | Write | ReadAcl | WriteAcl,
}

public sealed record Grant(Grantee Grantee, Permission Permissions);

public sealed record Header(string Name, string Value);

public sealed record Tag(string Key, string Value);

public enum VersioningStatus
{
    Disabled,
    Enabled,
    Suspended,
}

public sealed record ObjectVersion(string VersionId, DateTimeOffset LastModified, long Size, string StorageClass, bool IsLatest, bool IsDeleteMarker);

/// <summary>Per-object information shown by the lower tabs (read side). Changes are explicit asynchronous calls of
/// the real implementation (<c>RemoteDetailsStore</c>).</summary>
public interface IObjectDetailsStore
{
    IReadOnlyList<Grant> GetGrants(StoragePath path);

    IReadOnlyList<Header> GetHeaders(StoragePath file);

    IReadOnlyList<Tag> GetTags(StoragePath path);

    VersioningStatus GetVersioning(StoragePath bucket);

    IReadOnlyList<ObjectVersion> GetVersions(StoragePath file);

    /// <summary>First bytes of a file (at most <paramref name="maxBytes"/>) for the Preview tab.</summary>
    byte[] GetContentSample(StorageItem file, int maxBytes);

    /// <summary>Entity tag shown in Properties.</summary>
    string GetETag(StorageItem file);
}

/// <summary>Nothing to show (no connection).</summary>
public sealed class EmptyDetailsStore : IObjectDetailsStore
{
    public static EmptyDetailsStore Instance { get; } = new();

    public IReadOnlyList<Grant> GetGrants(StoragePath path) => [];

    public IReadOnlyList<Header> GetHeaders(StoragePath file) => [];

    public IReadOnlyList<Tag> GetTags(StoragePath path) => [];

    public VersioningStatus GetVersioning(StoragePath bucket) => VersioningStatus.Disabled;

    public IReadOnlyList<ObjectVersion> GetVersions(StoragePath file) => [];

    public byte[] GetContentSample(StorageItem file, int maxBytes) => [];

    public string GetETag(StorageItem file) => "—";
}
