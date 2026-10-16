namespace Storiva.Core.Commands;

/// <summary>
/// Enabled/disabled rules for every command. The states for "nothing selected" match the reference
/// captures [F E4–E6]; the other contexts follow Storiva's own conditions [H].
/// </summary>
public static class CommandRules
{
    public static bool IsEnabled(CommandId id, SelectionContext c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (!c.Connected && !WorksWithoutConnection(id))
        {
            return false;
        }

        var bucket = c.ActiveBucket is not null;

        return id switch
        {
            // Files
            CommandId.FileOpen => c.SingleSelection,
            // Files and folders (prefixes) can be downloaded.
            CommandId.FileDownload => c.SelectionIsObjects,
            // The reference shows Upload and Create New Folder enabled even at "/" [F E5]; Storiva disables
            // them there because the root holds only buckets (decision D-30).
            CommandId.FileUploadFiles or CommandId.FileUploadFolders => c.InBucket,
            CommandId.FileCut => c.SelectionIsObjects,
            CommandId.FileCopy => c.SelectionIsObjects,
            CommandId.FilePaste => c.ClipboardCount > 0 && c.InBucket,
            CommandId.FileCopyTo or CommandId.FileMoveTo => c.SelectionIsObjects,
            CommandId.FileRename => c.SingleSelection && c.SelectionIsObjects,
            CommandId.FileDelete => c.SelectionIsObjects,
            CommandId.FileCreateFolder => c.InBucket,
            CommandId.FileRefresh => true,
            CommandId.FileEditAcl => c.SingleSelection,
            CommandId.FileEditHeaders => c.SingleSelection && c.FileCount == 1,
            CommandId.FileEditTags => c.SingleSelection && c.FileCount == 1,
            CommandId.FileGenerateUrl => c.FileCount == 1 && c.SingleSelection,
            // Grey with an empty list in the reference [F E5].
            CommandId.FileSelectAll or CommandId.FileInvertSelection => c.ListCount > 0,
            CommandId.FileChangeStorageClass => c.FileCount > 0 && c.ContainerCount == 0,
            CommandId.FileServerSideEncryption or CommandId.FileObjectLock => c.FileCount > 0,
            CommandId.FileAdvanced => true,
            CommandId.FileProperties => c.SingleSelection,

            // Buckets: without a bucket only Create, Refresh and Add External are enabled [F E6].
            CommandId.BucketCreate or CommandId.BucketRefresh or CommandId.BucketAddExternal => true,
            CommandId.BucketDelete or CommandId.BucketCopyAllTo or CommandId.BucketMoveAllTo or CommandId.BucketDownloadAllTo
                or CommandId.BucketEditAcl or CommandId.BucketMountDrive or CommandId.BucketSharingWizard or CommandId.BucketEditTags
                or CommandId.BucketEditPolicy or CommandId.BucketEditLogging or CommandId.BucketEditWebsite or CommandId.BucketEditVersioning
                or CommandId.BucketDefaultEncryption or CommandId.BucketPublicAccessBlock or CommandId.BucketObjectOwnership
                or CommandId.BucketObjectLock or CommandId.BucketTransferAcceleration or CommandId.BucketReplication
                or CommandId.BucketLifecycle or CommandId.BucketCors or CommandId.BucketIntelligentTiering or CommandId.BucketGenerateUrls
                or CommandId.BucketChangeStorageClass or CommandId.BucketServerSideEncryption or CommandId.BucketCloudFront
                or CommandId.BucketProperties => bucket,

            // Tools: everything enabled except the sharing wizard without a bucket [F E4].
            CommandId.ToolsBucketSharingWizard => bucket,

            CommandId.BookmarkToggle => c.InBucket,
            CommandId.NavigateBack => c.CanGoBack,
            CommandId.NavigateForward => c.CanGoForward,
            CommandId.NavigateUp => c.InBucket,

            _ => true,
        };
    }

    /// <summary>Commands that make sense before a connection is open.</summary>
    public static bool WorksWithoutConnection(CommandId id) => id is CommandId.AccountAdd or CommandId.AccountManage or CommandId.AccountExit
        or CommandId.ToolsOptions or CommandId.HelpTopics or CommandId.HelpCheckUpdates or CommandId.HelpNotices or CommandId.HelpAbout
        or CommandId.ViewBucketPane or CommandId.ViewTaskPanel or CommandId.ViewStatusBar;
}
