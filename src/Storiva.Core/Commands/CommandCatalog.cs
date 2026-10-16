namespace Storiva.Core.Commands;

/// <summary>Which menu "owns" a shortcut when the same key appears in Files and Buckets [F E5/E6].</summary>
public enum ShortcutScope
{
    Files,
    Buckets,
    Global,
}

/// <summary>Text, shortcut and behaviour of one command.</summary>
/// <param name="Evidence">"F" = text/shortcut observed in a reference capture; "H" = hypothesis; "S" = Storiva addition.</param>
public sealed record CommandDefinition(
    CommandId Id,
    string Text,
    string? Shortcut,
    CommandBehavior Behavior,
    string? Icon,
    ShortcutScope Scope,
    string Evidence,
    string? UnavailableReason = null);

public abstract record MenuEntry;

public sealed record CommandEntry(CommandId Id) : MenuEntry;

public sealed record SeparatorEntry : MenuEntry;

/// <summary>A submenu whose enabled state follows <see cref="Id"/>; children are static entries or a dynamic list.</summary>
public sealed record SubmenuEntry(CommandId Id, IReadOnlyList<MenuEntry> Children, string? DynamicList = null, string? Title = null) : MenuEntry;

/// <summary>Items generated at run time (accounts, bookmarks).</summary>
public sealed record DynamicEntry(string ListName) : MenuEntry;

public sealed record MenuDefinition(string Title, IReadOnlyList<MenuEntry> Entries);

/// <summary>
/// Menu bar of the reference (Accounts · Buckets · Files · Bookmarks · Tools · Help [F S1/E1]).
/// Files, Buckets and Tools are transcribed from E5, E6 and E4; Accounts, Bookmarks and Help were not
/// observed and are hypotheses.
/// </summary>
public static class CommandCatalog
{
    public const string AccountsList = "accounts";
    public const string BookmarksList = "bookmarks";
    public const string StorageClassList = "storage-classes";

    private const string NeedsProvider = "Storiva does not implement it yet.";
    private const string NotImplemented = "Storiva does not implement it yet.";
    private const string NoNetwork = "Storiva never contacts an update server; it only connects to the storage endpoints you configure.";

    private static readonly CommandDefinition[] All =
    [
        // Accounts [H]
        Def(CommandId.AccountAdd, "Add New Account..", null, CommandBehavior.Local, "user-plus", ShortcutScope.Global, "H"),
        Def(CommandId.AccountManage, "Manage Accounts..", null, CommandBehavior.Local, null, ShortcutScope.Global, "H"),
        Def(CommandId.AccountExit, "Exit", "Alt+F4", CommandBehavior.Local, "log-out", ShortcutScope.Global, "H"),

        // Buckets [F E6]
        Def(CommandId.BucketCreate, "Create New Bucket..", "Ctrl+N", CommandBehavior.Local, "plus", ShortcutScope.Buckets, "F"),
        Def(CommandId.BucketDelete, "Delete Bucket", "Del", CommandBehavior.Local, "trash-2", ShortcutScope.Buckets, "F"),
        Def(CommandId.BucketRefresh, "Refresh Buckets list", "F5", CommandBehavior.Local, "refresh-cw", ShortcutScope.Buckets, "F"),
        Def(CommandId.BucketCopyAllTo, "Copy all files to..", "Ctrl+Shift+C", CommandBehavior.Unavailable, "copy", ShortcutScope.Buckets, "F", NotImplemented),
        Def(CommandId.BucketMoveAllTo, "Move all files to..", "Ctrl+Shift+M", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NotImplemented),
        Def(CommandId.BucketDownloadAllTo, "Download all files to..", "Ctrl+Shift+D", CommandBehavior.Local, "download", ShortcutScope.Buckets, "F"),
        Def(CommandId.BucketEditAcl, "Edit Access Control List (ACL)..", "Ctrl+L", CommandBehavior.OpensTab, null, ShortcutScope.Buckets, "F"),
        Def(CommandId.BucketAddExternal, "Add External Bucket..", "Ctrl+E", CommandBehavior.Local, "plus", ShortcutScope.Buckets, "F"),
        Def(CommandId.BucketMountDrive, "Mount as Windows Drive..", "Ctrl+M", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", "It would change Windows drive mappings, which Storiva does not do."),
        Def(CommandId.BucketSharingWizard, "Bucket Sharing Wizard..", "Ctrl+Shift+B", CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NeedsProvider),
        Def(CommandId.BucketEditTags, "Edit Tags..", "Ctrl+T", CommandBehavior.OpensTab, null, ShortcutScope.Buckets, "F"),
        Def(CommandId.BucketEditPolicy, "Edit Bucket Policy..", "Ctrl+O", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketEditLogging, "Edit Logging Settings..", "Ctrl+G", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketEditWebsite, "Edit Website Configuration..", "Ctrl+W", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketEditVersioning, "Edit Versioning Settings..", "Ctrl+Shift+V", CommandBehavior.Local, null, ShortcutScope.Buckets, "F"),
        Def(CommandId.BucketDefaultEncryption, "Default Encryption Configuration..", "Ctrl+S", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketPublicAccessBlock, "Public Access Block Configuration..", "Ctrl+B", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketObjectOwnership, "Object Ownership Configuration..", "Ctrl+Shift+O", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketObjectLock, "Object Lock", null, CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketTransferAcceleration, "Transfer Acceleration..", "Ctrl+Shift+T", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketReplication, "Cross-Region Replication..", "Ctrl+Shift+R", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketLifecycle, "Lifecycle Configuration..", "Ctrl+I", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketCors, "CORS Configuration..", "Ctrl+R", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketIntelligentTiering, "Intelligent Tiering Configuration..", "Ctrl+Shift+I", CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketGenerateUrls, "Generate Web URLs..", "Ctrl+U", CommandBehavior.Local, null, ShortcutScope.Buckets, "F"),
        Def(CommandId.BucketChangeStorageClass, "Change Storage Class to ..", null, CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NotImplemented),
        Def(CommandId.BucketServerSideEncryption, "Server Side Encryption ..", null, CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketCloudFront, "CloudFront", null, CommandBehavior.Unavailable, null, ShortcutScope.Buckets, "F", NeedsProvider),
        Def(CommandId.BucketProperties, "Properties", "Ctrl+P", CommandBehavior.OpensTab, "info", ShortcutScope.Buckets, "F"),

        // Files [F E5]
        Def(CommandId.FileOpen, "Open", "Enter", CommandBehavior.Local, "external-link", ShortcutScope.Files, "F"),
        Def(CommandId.FileDownload, "Download", "Ctrl+D", CommandBehavior.Local, "download", ShortcutScope.Files, "F"),
        Def(CommandId.FileUploadFiles, "Upload file(s)", "Ctrl+U", CommandBehavior.Local, "upload", ShortcutScope.Files, "F"),
        Def(CommandId.FileUploadFolders, "Upload folder(s)", "Ctrl+Shift+U", CommandBehavior.Local, "upload", ShortcutScope.Files, "F"),
        Def(CommandId.FileCut, "Cut", "Ctrl+X", CommandBehavior.Local, null, ShortcutScope.Files, "F"),
        Def(CommandId.FileCopy, "Copy", "Ctrl+C", CommandBehavior.Local, "copy", ShortcutScope.Files, "F"),
        Def(CommandId.FilePaste, "Paste", "Ctrl+V", CommandBehavior.Local, null, ShortcutScope.Files, "F"),
        Def(CommandId.FileCopyTo, "Copy to..", "Ctrl+Shift+C", CommandBehavior.Unavailable, null, ShortcutScope.Files, "F", "Use Copy and Paste instead; a destination picker is not implemented yet."),
        Def(CommandId.FileMoveTo, "Move to..", "Ctrl+Shift+X", CommandBehavior.Unavailable, null, ShortcutScope.Files, "F", "Use Cut and Paste instead; a destination picker is not implemented yet."),
        Def(CommandId.FileRename, "Rename..", "F2", CommandBehavior.Local, "pencil", ShortcutScope.Files, "F"),
        Def(CommandId.FileDelete, "Delete", "Del", CommandBehavior.Local, "trash-2", ShortcutScope.Files, "F"),
        Def(CommandId.FileCreateFolder, "Create New Folder..", "Ctrl+N", CommandBehavior.Local, "folder-plus", ShortcutScope.Files, "F"),
        Def(CommandId.FileRefresh, "Refresh", "F5", CommandBehavior.Local, "refresh-cw", ShortcutScope.Files, "F"),
        Def(CommandId.FileEditAcl, "Edit Permissions (ACL)", "Ctrl+L", CommandBehavior.OpensTab, null, ShortcutScope.Files, "F"),
        Def(CommandId.FileEditHeaders, "Edit HTTP Headers", "Ctrl+H", CommandBehavior.OpensTab, null, ShortcutScope.Files, "F"),
        Def(CommandId.FileEditTags, "Edit Object Tags", "Ctrl+T", CommandBehavior.OpensTab, null, ShortcutScope.Files, "F"),
        Def(CommandId.FileGenerateUrl, "Generate Web URL..", "Ctrl+W", CommandBehavior.Local, null, ShortcutScope.Files, "F"),
        Def(CommandId.FileSelectAll, "Select All", "Ctrl+A", CommandBehavior.Local, null, ShortcutScope.Files, "F"),
        Def(CommandId.FileInvertSelection, "Invert Selection", "Ctrl+I", CommandBehavior.Local, null, ShortcutScope.Files, "F"),
        Def(CommandId.FileChangeStorageClass, "Change Storage Class to ..", null, CommandBehavior.Local, null, ShortcutScope.Files, "F"),
        Def(CommandId.FileServerSideEncryption, "Server Side Encryption ..", null, CommandBehavior.Unavailable, null, ShortcutScope.Files, "F", NeedsProvider),
        Def(CommandId.FileObjectLock, "Object Lock", null, CommandBehavior.Unavailable, null, ShortcutScope.Files, "F", NeedsProvider),
        Def(CommandId.FileAdvanced, "Advanced", null, CommandBehavior.Unavailable, null, ShortcutScope.Files, "F", "Its content was not observed in the reference and is not implemented."),
        Def(CommandId.FileProperties, "Properties", "Ctrl+P", CommandBehavior.OpensTab, "info", ShortcutScope.Files, "F"),

        // Bookmarks [H]
        Def(CommandId.BookmarkToggle, "Bookmark Current Location", null, CommandBehavior.Local, null, ShortcutScope.Global, "H"),
        Def(CommandId.BookmarkClear, "Remove All Bookmarks", null, CommandBehavior.Local, null, ShortcutScope.Global, "H"),

        // Tools [F E4]
        Def(CommandId.ToolsDefaultHeaders, "Default HTTP Headers..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NotImplemented),
        Def(CommandId.ToolsDefaultTags, "Default Object Tags..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NotImplemented),
        Def(CommandId.ToolsDefaultStorageClasses, "Default Storage Classes..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NotImplemented),
        Def(CommandId.ToolsUploadFilters, "Uploading Filters..", null, CommandBehavior.Unavailable, "list-filter", ShortcutScope.Global, "F", NotImplemented),
        Def(CommandId.ToolsCompressionEncryption, "Compression and Encryption..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NotImplemented),
        Def(CommandId.ToolsServerSideEncryption, "Server-Side Encryption..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NeedsProvider),
        Def(CommandId.ToolsObjectLockRules, "Object Lock Retention Rules..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NeedsProvider),
        Def(CommandId.ToolsAccessManager, "Access Manager (IAM)..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NeedsProvider),
        Def(CommandId.ToolsBucketSharingWizard, "Bucket Sharing Wizard..", "Ctrl+Shift+B", CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NeedsProvider),
        Def(CommandId.ToolsCloudFrontManager, "CloudFront Manager..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NeedsProvider),
        Def(CommandId.ToolsFolderSync, "Folder Sync Tool..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", "Folder synchronisation is not implemented yet."),
        Def(CommandId.ToolsUncompletedUploads, "Uncompleted Multipart Uploads..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NeedsProvider),
        Def(CommandId.ToolsSecurityScan, "Security Scan..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NeedsProvider),
        Def(CommandId.ToolsDiagnostics, "Diagnostics..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "F", NoNetwork),
        Def(CommandId.ToolsOptions, "Options..", "Alt+O", CommandBehavior.Local, "settings", ShortcutScope.Global, "F"),

        // Storiva extensions [S]
        Def(CommandId.ViewBucketPane, "Bucket Pane", null, CommandBehavior.Local, "panel-left", ShortcutScope.Global, "S"),
        Def(CommandId.ViewTaskPanel, "Task Panel", null, CommandBehavior.Local, "panel-bottom", ShortcutScope.Global, "S"),
        Def(CommandId.ViewStatusBar, "Status Bar", null, CommandBehavior.Local, null, ShortcutScope.Global, "S"),
        Def(CommandId.NavigateBack, "Back", "Alt+Left", CommandBehavior.Local, "arrow-left", ShortcutScope.Global, "S"),
        Def(CommandId.NavigateForward, "Forward", "Alt+Right", CommandBehavior.Local, "arrow-right", ShortcutScope.Global, "S"),
        Def(CommandId.NavigateUp, "Up One Level", "Alt+Up", CommandBehavior.Local, "arrow-up", ShortcutScope.Global, "S"),
        Def(CommandId.Find, "Find..", "Ctrl+F", CommandBehavior.Local, "search", ShortcutScope.Global, "S"),
        Def(CommandId.EditPath, "Edit Location", "Alt+D", CommandBehavior.Local, "pencil", ShortcutScope.Global, "S"),
        Def(CommandId.CopyS3Uri, "Copy S3 URI", null, CommandBehavior.Local, "copy", ShortcutScope.Files, "S"),
        Def(CommandId.CopyObjectUrl, "Copy Object URL", null, CommandBehavior.Local, null, ShortcutScope.Files, "S"),

        // Help [H]
        Def(CommandId.HelpTopics, "Help Topics..", "F1", CommandBehavior.Local, "info", ShortcutScope.Global, "H"),
        Def(CommandId.HelpCheckUpdates, "Check for Updates..", null, CommandBehavior.Unavailable, null, ShortcutScope.Global, "H", NoNetwork),
        Def(CommandId.HelpNotices, "Third-party Notices..", null, CommandBehavior.Local, null, ShortcutScope.Global, "S"),
        Def(CommandId.HelpAbout, "About Storiva..", null, CommandBehavior.Local, "info", ShortcutScope.Global, "H"),
    ];

    private static readonly Dictionary<CommandId, CommandDefinition> ById = All.ToDictionary(d => d.Id);

    public static IReadOnlyList<CommandDefinition> Definitions => All;

    public static CommandDefinition Get(CommandId id) => ById[id];

    /// <summary>Menu bar in the reference order [F S1/E1].</summary>
    public static IReadOnlyList<MenuDefinition> Menus { get; } =
    [
        new("&Accounts",
        [
            Cmd(CommandId.AccountAdd), Cmd(CommandId.AccountManage), Sep(), new DynamicEntry(AccountsList), Sep(), Cmd(CommandId.AccountExit),
        ]),
        new("&Buckets",
        [
            Cmd(CommandId.BucketCreate), Cmd(CommandId.BucketDelete), Cmd(CommandId.BucketRefresh), Sep(),
            Cmd(CommandId.BucketCopyAllTo), Cmd(CommandId.BucketMoveAllTo), Cmd(CommandId.BucketDownloadAllTo), Sep(),
            Cmd(CommandId.BucketEditAcl), Cmd(CommandId.BucketAddExternal), Cmd(CommandId.BucketMountDrive), Sep(),
            Cmd(CommandId.BucketSharingWizard), Sep(),
            Cmd(CommandId.BucketEditTags), Cmd(CommandId.BucketEditPolicy), Cmd(CommandId.BucketEditLogging), Cmd(CommandId.BucketEditWebsite),
            Cmd(CommandId.BucketEditVersioning), Cmd(CommandId.BucketDefaultEncryption), Cmd(CommandId.BucketPublicAccessBlock),
            Cmd(CommandId.BucketObjectOwnership), Unobserved(CommandId.BucketObjectLock), Sep(),
            Cmd(CommandId.BucketTransferAcceleration), Sep(),
            Cmd(CommandId.BucketReplication), Cmd(CommandId.BucketLifecycle), Cmd(CommandId.BucketCors), Cmd(CommandId.BucketIntelligentTiering), Sep(),
            Cmd(CommandId.BucketGenerateUrls), Sep(),
            Unobserved(CommandId.BucketChangeStorageClass), Unobserved(CommandId.BucketServerSideEncryption), Unobserved(CommandId.BucketCloudFront), Sep(),
            Cmd(CommandId.BucketProperties),
        ]),
        new("&Files",
        [
            Cmd(CommandId.FileOpen), Sep(),
            Cmd(CommandId.FileDownload), Cmd(CommandId.FileUploadFiles), Cmd(CommandId.FileUploadFolders), Sep(),
            Cmd(CommandId.FileCut), Cmd(CommandId.FileCopy), Cmd(CommandId.FilePaste), Sep(),
            Cmd(CommandId.FileCopyTo), Cmd(CommandId.FileMoveTo), Sep(),
            Cmd(CommandId.FileRename), Cmd(CommandId.FileDelete), Sep(),
            Cmd(CommandId.FileCreateFolder), Cmd(CommandId.FileRefresh), Sep(),
            Cmd(CommandId.FileEditAcl), Cmd(CommandId.FileEditHeaders), Cmd(CommandId.FileEditTags), Sep(),
            Cmd(CommandId.FileGenerateUrl), Sep(),
            Cmd(CommandId.FileSelectAll), Cmd(CommandId.FileInvertSelection), Sep(),
            new SubmenuEntry(CommandId.FileChangeStorageClass, [], StorageClassList),
            Unobserved(CommandId.FileServerSideEncryption), Unobserved(CommandId.FileObjectLock), Unobserved(CommandId.FileAdvanced), Sep(),
            Cmd(CommandId.FileProperties),
        ]),
        new("B&ookmarks",
        [
            Cmd(CommandId.BookmarkToggle), Cmd(CommandId.BookmarkClear), Sep(), new DynamicEntry(BookmarksList),
        ]),
        new("&Tools",
        [
            Cmd(CommandId.ToolsDefaultHeaders), Cmd(CommandId.ToolsDefaultTags), Cmd(CommandId.ToolsDefaultStorageClasses), Cmd(CommandId.ToolsUploadFilters),
            Cmd(CommandId.ToolsCompressionEncryption), Cmd(CommandId.ToolsServerSideEncryption), Cmd(CommandId.ToolsObjectLockRules), Sep(),
            Cmd(CommandId.ToolsAccessManager), Cmd(CommandId.ToolsBucketSharingWizard), Sep(),
            Cmd(CommandId.ToolsCloudFrontManager), Sep(),
            Cmd(CommandId.ToolsFolderSync), Sep(),
            Cmd(CommandId.ToolsUncompletedUploads), Cmd(CommandId.ToolsSecurityScan), Sep(),
            Cmd(CommandId.ToolsDiagnostics), Cmd(CommandId.ToolsOptions), Sep(),
            new SubmenuEntry(CommandId.ViewBucketPane, [Cmd(CommandId.ViewBucketPane), Cmd(CommandId.ViewTaskPanel), Cmd(CommandId.ViewStatusBar)], Title: LayoutSubmenuTitle),
        ]),
        new("&Help",
        [
            Cmd(CommandId.HelpTopics), Cmd(CommandId.HelpCheckUpdates), Sep(), Cmd(CommandId.HelpNotices), Cmd(CommandId.HelpAbout),
        ]),
    ];

    /// <summary>Title of the Storiva "Layout" submenu (its id reuses <see cref="CommandId.ViewBucketPane"/>).</summary>
    public const string LayoutSubmenuTitle = "Layout";

    private static CommandDefinition Def(CommandId id, string text, string? shortcut, CommandBehavior behavior, string? icon, ShortcutScope scope, string evidence, string? reason = null) =>
        new(id, text, shortcut, behavior, icon, scope, evidence, reason);

    private static CommandEntry Cmd(CommandId id) => new(id);

    private static SeparatorEntry Sep() => new();

    /// <summary>Submenu whose children were not observed: a single "not available" item.</summary>
    private static SubmenuEntry Unobserved(CommandId id) => new(id, [Cmd(id)]);
}
