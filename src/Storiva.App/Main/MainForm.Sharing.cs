using System.Globalization;
using System.Runtime.InteropServices;
using Storiva.App.Dialogs;
using Storiva.Core.Remote;
using Storiva.Core.Storage;

namespace Storiva.App.Main;

/// <summary>
/// Sharing: pre-signed links ("Generate Web URL.." for the selected files, "Generate Web URLs.." for a bucket) and
/// the copy-address commands (s3:// URI, plain object URL). Links are computed locally by the SDK; the Event log
/// records that a link was generated, for which key and for how long, but never the link itself (it is a bearer
/// credential), and Storiva does not keep it after the dialog closes.
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>
    /// Puts text on the clipboard; <c>sensitive</c> text is kept out of the Windows clipboard history and cloud
    /// clipboard. Throws <see cref="ExternalException"/> when the clipboard is busy. The smoke test replaces it to
    /// read what would be copied without touching the user's clipboard.
    /// </summary>
    internal Action<string, bool> ClipboardWriter { get; set; } = WriteClipboard;

    private static void WriteClipboard(string text, bool sensitive)
    {
        if (!sensitive)
        {
            Clipboard.SetText(text);
            return;
        }

        // Documented Windows clipboard formats: a DWORD 0 asks the clipboard history (Win+V) and the cloud
        // clipboard not to keep or sync this entry. Pasting works as usual.
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Clipboard.SetDataObject(data, copy: true);
    }

    // ---------- Generate Web URL(s) ----------

    private void RemoteGenerateUrls()
    {
        using var dialog = CreateShareLinkDialogForSelection();
        dialog?.ShowDialog(this);
    }

    /// <summary>The dialog for the selected files (null when nothing applies); the smoke test shows it non-modally.</summary>
    internal ShareLinkDialog? CreateShareLinkDialogForSelection()
    {
        var files = SelectedRows().Select(r => r.Item).Where(i => i.Kind == StorageItemKind.File).ToList();
        if (_remote is not { } session || files.Count == 0 || files[0].Path.Bucket is not { } bucket)
        {
            return null;
        }

        if (files.Count > ShareLinks.MaxLinks)
        {
            Prompts.Warn(this, "Generate Web URLs", $"{Plural(files.Count, "file is", "files are")} selected. Storiva generates at most {ShareLinks.MaxLinks.ToString("N0", CultureInfo.InvariantCulture)} links at once.");
            return null;
        }

        var keys = files.Select(KeyOf).ToList();
        var subject = files.Count == 1 ? $"\"{files[0].Name}\" from bucket \"{bucket}\"" : $"{files.Count} files from bucket \"{bucket}\"";
        return CreateShareLinkDialog(session, bucket, keys, subject);
    }

    private async void RemoteGenerateBucketUrls()
    {
        using var dialog = await PrepareBucketShareLinksAsync();
        dialog?.ShowDialog(this);
    }

    /// <summary>
    /// "Generate Web URLs.." of the Buckets menu: lists the bucket (read-only) and returns the dialog for all its files,
    /// or null after telling the user why not (empty bucket, more than <see cref="ShareLinks.MaxLinks"/> files, error).
    /// </summary>
    internal async Task<ShareLinkDialog?> PrepareBucketShareLinksAsync()
    {
        if (_remote is not { } session || BuildContext().ActiveBucket?.Bucket is not { } bucket)
        {
            return null;
        }

        SetStatus($"Listing the files of bucket \"{bucket}\"…");
        IReadOnlyList<string> keys;
        try
        {
            keys = await ShareLinks.ListFileKeysAsync(session.Client, bucket, ShareLinks.MaxLinks, session.Lifetime.Token);
        }
        catch (StorageOperationException ex)
        {
            if (ex.Kind != StorageErrorKind.Canceled && session == _remote)
            {
                ReportRemoteError($"Could not list bucket \"{bucket}\"", ex.Error);
                Prompts.Warn(this, "Generate Web URLs", ex.Error.Describe());
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null; // disconnected meanwhile
        }

        if (session != _remote)
        {
            return null;
        }

        SetStatus(string.Empty);
        if (keys.Count == 0)
        {
            Prompts.Info(this, "Generate Web URLs", $"Bucket \"{bucket}\" has no files to share.");
            return null;
        }

        if (keys.Count > ShareLinks.MaxLinks)
        {
            Prompts.Warn(this, "Generate Web URLs",
                $"Bucket \"{bucket}\" has more than {ShareLinks.MaxLinks.ToString("N0", CultureInfo.InvariantCulture)} files, the most Storiva signs at once. " +
                "Open a folder, select the files to share and use Files › Generate Web URL.. instead.");
            return null;
        }

        return CreateShareLinkDialog(session, bucket, keys, keys.Count == 1 ? $"the only file of bucket \"{bucket}\"" : $"all {keys.Count} files of bucket \"{bucket}\"");
    }

    private ShareLinkDialog CreateShareLinkDialog(RemoteSession session, string bucket, IReadOnlyList<string> keys, string subject) =>
        new(subject, keys.Count, (validity, ct) => GenerateLinksAsync(session, bucket, keys, validity, ct), CopyLinks);

    private async Task<IReadOnlyList<PresignedUrl>> GenerateLinksAsync(RemoteSession session, string bucket, IReadOnlyList<string> keys, TimeSpan validity, CancellationToken ct)
    {
        if (session.Retired)
        {
            throw new StorageOperationException(new StorageError(StorageErrorKind.Canceled, "The connection was closed."));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Lifetime.Token);
        try
        {
            var links = await ShareLinks.CreateLinksAsync(session.Client, bucket, keys, validity, linked.Token);

            // Key and validity only: the link itself is a credential and is never written anywhere.
            _activity.Write(keys.Count == 1
                ? $"Generated a link for {ShareLinks.S3Uri(bucket, keys[0])} valid for {ShareLinks.Describe(validity)}."
                : $"Generated {keys.Count} links for files of {ShareLinks.S3Uri(bucket, null)} valid for {ShareLinks.Describe(validity)}.");
            return links;
        }
        catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
        {
            ReportRemoteError("Generating the link", ex.Error);
            throw;
        }
    }

    private void CopyLinks(string text)
    {
        var count = LineCount(text);
        try
        {
            ClipboardWriter(text, true);
            SetStatus(count == 1 ? "Link copied to the clipboard (kept out of the clipboard history)." : $"{count} links copied to the clipboard (kept out of the clipboard history).");
        }
        catch (ExternalException)
        {
            SetStatus("The clipboard is in use by another application. Try again.");
        }
    }

    // ---------- Copy S3 URI / Copy Object URL ----------

    /// <summary>s3://bucket/key of each selected item, one per line (folders end with "/", buckets are "s3://bucket/").</summary>
    internal string SelectionS3UriText() => _remote is null ? string.Empty : string.Join("\r\n", SelectedRows()
        .Select(r => r.Item)
        .Where(i => i.Path.Bucket is not null)
        .Select(i => ShareLinks.S3Uri(i.Path.Bucket!, i.Kind == StorageItemKind.Bucket ? null : KeyOf(i))));

    /// <summary>Plain URL of each selected file on the connection's endpoint, one per line (no credentials in it).</summary>
    internal string SelectionObjectUrlText() => _remote is not { } session ? string.Empty : string.Join("\r\n", SelectedRows()
        .Select(r => r.Item)
        .Where(i => i.Kind == StorageItemKind.File && i.Path.Bucket is not null)
        .Select(i => session.Client.GetObjectUrl(i.Path.Bucket!, KeyOf(i))));

    private void CopySelectionS3Uris()
    {
        var text = SelectionS3UriText();
        var count = LineCount(text);
        if (count > 0)
        {
            CopyText(text, count == 1 ? $"Copied {text} to the clipboard." : $"Copied {count} S3 URIs to the clipboard.");
        }
    }

    private void CopySelectionObjectUrls()
    {
        var text = SelectionObjectUrlText();
        var count = LineCount(text);
        if (count > 0)
        {
            CopyText(text, $"Copied {(count == 1 ? "the URL" : $"{count} URLs")} to the clipboard. It opens only if the object is public; " +
                "use Generate Web URL.. to share a private file.");
        }
    }

    private static int LineCount(string text) => text.Length == 0 ? 0 : text.Split('\n').Length;

    // ---------- smoke-test hooks ----------

    /// <summary>Items of the object list's context menu after it refreshed, as when the user right-clicks.</summary>
    internal IReadOnlyList<(string Text, bool Enabled)> GridMenuItems()
    {
        _menuBuilder.Refresh(_gridMenu!.Items);
        return _gridMenu.Items.Cast<ToolStripItem>().Where(i => i is not ToolStripSeparator).Select(i => (i.Text ?? string.Empty, i.Enabled)).ToList();
    }
}
