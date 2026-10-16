namespace Storiva.Core.Commands;

/// <summary>Every command reachable from the menus, toolbars or shortcuts.</summary>
public enum CommandId
{
    // Accounts [H]
    AccountAdd,
    AccountManage,
    AccountExit,

    // Buckets [F E6]
    BucketCreate,
    BucketDelete,
    BucketRefresh,
    BucketCopyAllTo,
    BucketMoveAllTo,
    BucketDownloadAllTo,
    BucketEditAcl,
    BucketAddExternal,
    BucketMountDrive,
    BucketSharingWizard,
    BucketEditTags,
    BucketEditPolicy,
    BucketEditLogging,
    BucketEditWebsite,
    BucketEditVersioning,
    BucketDefaultEncryption,
    BucketPublicAccessBlock,
    BucketObjectOwnership,
    BucketObjectLock,
    BucketTransferAcceleration,
    BucketReplication,
    BucketLifecycle,
    BucketCors,
    BucketIntelligentTiering,
    BucketGenerateUrls,
    BucketChangeStorageClass,
    BucketServerSideEncryption,
    BucketCloudFront,
    BucketProperties,

    // Files [F E5]
    FileOpen,
    FileDownload,
    FileUploadFiles,
    FileUploadFolders,
    FileCut,
    FileCopy,
    FilePaste,
    FileCopyTo,
    FileMoveTo,
    FileRename,
    FileDelete,
    FileCreateFolder,
    FileRefresh,
    FileEditAcl,
    FileEditHeaders,
    FileEditTags,
    FileGenerateUrl,
    FileSelectAll,
    FileInvertSelection,
    FileChangeStorageClass,
    FileServerSideEncryption,
    FileObjectLock,
    FileAdvanced,
    FileProperties,

    // Bookmarks [H]
    BookmarkToggle,
    BookmarkClear,

    // Tools [F E4]
    ToolsDefaultHeaders,
    ToolsDefaultTags,
    ToolsDefaultStorageClasses,
    ToolsUploadFilters,
    ToolsCompressionEncryption,
    ToolsServerSideEncryption,
    ToolsObjectLockRules,
    ToolsAccessManager,
    ToolsBucketSharingWizard,
    ToolsCloudFrontManager,
    ToolsFolderSync,
    ToolsUncompletedUploads,
    ToolsSecurityScan,
    ToolsDiagnostics,
    ToolsOptions,

    // Storiva extensions [S]
    ViewBucketPane,
    ViewTaskPanel,
    ViewStatusBar,
    NavigateBack,
    NavigateForward,
    NavigateUp,
    Find,
    EditPath,
    CopyS3Uri,
    CopyObjectUrl,

    // Help [H]
    HelpTopics,
    HelpCheckUpdates,
    HelpNotices,
    HelpAbout,
}

/// <summary>What happens when a command runs.</summary>
public enum CommandBehavior
{
    /// <summary>Runs (on the active connection, or locally for window commands).</summary>
    Local,

    /// <summary>Selects one of the lower tabs.</summary>
    OpensTab,

    /// <summary>Shows a "not available" notice; nothing is executed.</summary>
    Unavailable,
}
