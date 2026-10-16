namespace Storiva.Core.Remote;

/// <summary>Outcome of a bulk operation: what succeeded, what failed (with a safe reason) and a one-line summary.</summary>
public sealed record BulkResult(int Succeeded, IReadOnlyList<(string Key, string Reason)> Failures, string Summary, bool PartialMove = false)
{
    public bool HasFailures => Failures.Count > 0;
}

/// <summary>
/// Multi-object operations built on <see cref="IObjectStorageClient"/>. S3 "folders" are prefixes, so a folder
/// operation is an operation on every object below it, listed page by page. Each method reports real progress and
/// stops at cancellation; nothing is deleted beyond what was asked. Uploads and downloads retry an object after a
/// transient failure (<see cref="TransferRetry"/>); nothing else is retried beyond the SDK's own request retries.
/// </summary>
public static class RemoteOperations
{
    /// <summary>Deletes every object below <paramref name="prefix"/> (including the folder marker) in batches of 1000.</summary>
    public static async Task<BulkResult> DeletePrefixAsync(IObjectStorageClient client, string bucket, string prefix, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (prefix.Length == 0 || !prefix.EndsWith('/'))
        {
            throw new ArgumentException("A folder prefix must end with '/'. Deleting a whole bucket is not done this way.", nameof(prefix));
        }

        var deleted = 0;
        var failures = new List<(string, string)>();
        var batch = new List<string>(1000);
        await foreach (var obj in client.ListAllObjectsAsync(bucket, prefix, ct).ConfigureAwait(true))
        {
            batch.Add(obj.Key);
            if (batch.Count == 1000)
            {
                await FlushAsync().ConfigureAwait(true);
            }
        }

        await FlushAsync().ConfigureAwait(true);
        return new BulkResult(deleted, failures, failures.Count == 0
            ? $"Deleted {deleted} object(s) below {prefix}"
            : $"Deleted {deleted} object(s); {failures.Count} could not be deleted");

        async Task FlushAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            var result = await client.DeleteObjectsAsync(bucket, batch.ToList(), ct).ConfigureAwait(true);
            deleted += result.Deleted.Count;
            failures.AddRange(result.Failed);
            batch.Clear();
            progress?.Report(new TransferProgress(deleted + failures.Count, null));
        }
    }

    /// <summary>Copies every object below <paramref name="sourcePrefix"/> to the same relative keys below <paramref name="destinationPrefix"/>.</summary>
    public static async Task<BulkResult> CopyPrefixAsync(IObjectStorageClient client, string sourceBucket, string sourcePrefix, string destinationBucket, string destinationPrefix, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (sourceBucket == destinationBucket && destinationPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal))
        {
            // The listing would pick up the new copies and never end.
            throw new ArgumentException("A folder cannot be copied into itself.", nameof(destinationPrefix));
        }

        var (copied, failures, _) = await CopyAllAsync(client, sourceBucket, sourcePrefix, destinationBucket, destinationPrefix, progress, ct).ConfigureAwait(true);
        return new BulkResult(copied.Count, failures, failures.Count == 0
            ? $"Copied {copied.Count} object(s)"
            : $"Copied {copied.Count} object(s); {failures.Count} failed");
    }

    /// <summary>
    /// Move = copy everything, then delete the sources. Sources are deleted only when every copy succeeded; when a
    /// delete fails the objects exist in both places and the result says so (never reported as a completed move).
    /// </summary>
    public static async Task<BulkResult> MovePrefixAsync(IObjectStorageClient client, string sourceBucket, string sourcePrefix, string destinationBucket, string destinationPrefix, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (sourceBucket == destinationBucket && destinationPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("A folder cannot be moved into itself.", nameof(destinationPrefix));
        }

        var (copied, failures, total) = await CopyAllAsync(client, sourceBucket, sourcePrefix, destinationBucket, destinationPrefix, progress, ct).ConfigureAwait(true);
        if (failures.Count > 0)
        {
            return new BulkResult(copied.Count, failures,
                $"Move stopped: {failures.Count} of {total} object(s) could not be copied. Nothing was deleted from the source.", PartialMove: copied.Count > 0);
        }

        var deleteFailures = new List<(string, string)>();
        foreach (var chunk in copied.Chunk(1000))
        {
            ct.ThrowIfCancellationRequested();
            var result = await client.DeleteObjectsAsync(sourceBucket, chunk, ct).ConfigureAwait(true);
            deleteFailures.AddRange(result.Failed);
        }

        return deleteFailures.Count == 0
            ? new BulkResult(copied.Count, [], $"Moved {copied.Count} object(s)")
            : new BulkResult(copied.Count, deleteFailures,
                $"Copied {copied.Count} object(s), but {deleteFailures.Count} source object(s) could not be deleted. They now exist in both places.", PartialMove: true);
    }

    private static async Task<(List<string> Copied, List<(string, string)> Failures, int Total)> CopyAllAsync(
        IObjectStorageClient client, string sourceBucket, string sourcePrefix, string destinationBucket, string destinationPrefix, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var copied = new List<string>();
        var failures = new List<(string, string)>();
        var total = 0;
        await foreach (var obj in client.ListAllObjectsAsync(sourceBucket, sourcePrefix, ct).ConfigureAwait(true))
        {
            total++;
            var target = destinationPrefix + obj.Key[sourcePrefix.Length..];
            try
            {
                await client.CopyObjectAsync(sourceBucket, obj.Key, destinationBucket, target, null, ct).ConfigureAwait(true);
                copied.Add(obj.Key);
            }
            catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
            {
                failures.Add((obj.Key, ex.Error.Describe()));
            }

            progress?.Report(new TransferProgress(copied.Count + failures.Count, null));
        }

        return (copied, failures, total);
    }

    /// <summary>Uploads one local file, retrying the whole file after a transient failure.</summary>
    /// <param name="retry">Retry policy (one per operation); null = <see cref="TransferRetry.DefaultDelays"/>.</param>
    public static Task UploadFileAsync(IObjectStorageClient client, string bucket, string key, string localPath, string? contentType, IProgress<TransferProgress>? progress, CancellationToken ct, TransferRetry? retry = null) =>
        (retry ?? new TransferRetry()).RunAsync(p => client.UploadObjectAsync(bucket, key, localPath, contentType, p, ct), progress, ct);

    /// <summary>
    /// Downloads one object to <paramref name="localPath"/>: written to "name.storiva-partial" first and renamed only
    /// when complete, so an interrupted download never leaves a truncated file under the real name. After a transient
    /// failure the download starts over in the same partial file; when it finally fails, the partial file is removed.
    /// </summary>
    /// <param name="retry">Retry policy (one per operation); null = <see cref="TransferRetry.DefaultDelays"/>.</param>
    /// <returns>The path written, or null when skipped because of <see cref="ConflictPolicy.Skip"/>.</returns>
    public static async Task<string?> DownloadFileAsync(IObjectStorageClient client, string bucket, string key, string localPath, ConflictPolicy conflict, IProgress<TransferProgress>? progress, CancellationToken ct, TransferRetry? retry = null)
    {
        var target = localPath;
        if (File.Exists(target))
        {
            switch (conflict)
            {
                case ConflictPolicy.Skip:
                    return null;
                case ConflictPolicy.KeepBoth:
                    target = LocalPathMapper.UniquePath(target);
                    break;
            }
        }

        var partial = target + ".storiva-partial";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await (retry ?? new TransferRetry()).RunAsync(async p =>
                {
                    // A retried attempt starts over: drop what the failed one wrote.
                    stream.SetLength(0);
                    stream.Position = 0;
                    await client.DownloadObjectAsync(bucket, key, stream, p, ct).ConfigureAwait(true);
                }, progress, ct).ConfigureAwait(true);
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            throw new StorageOperationException(new StorageError(StorageErrorKind.LocalIo, $"Could not write \"{Path.GetFileName(target)}\": {ex.Message}"), ex);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Downloads every object below <paramref name="prefix"/> into <paramref name="localRoot"/>, keeping the folder structure.</summary>
    /// <param name="retry">Retry policy shared by every object; null = <see cref="TransferRetry.DefaultDelays"/>.</param>
    public static async Task<BulkResult> DownloadPrefixAsync(IObjectStorageClient client, string bucket, string prefix, string localRoot, ConflictPolicy conflict, IProgress<TransferProgress>? progress, CancellationToken ct, TransferRetry? retry = null)
    {
        retry ??= new TransferRetry();
        var done = 0;
        var skipped = 0;
        var failures = new List<(string, string)>();
        long bytes = 0;
        await foreach (var obj in client.ListAllObjectsAsync(bucket, prefix, ct).ConfigureAwait(true))
        {
            if (obj.Key.EndsWith('/'))
            {
                continue; // folder markers have no content
            }

            if (!obj.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                // Only a misbehaving server lists keys outside the requested prefix; never map them into the folder.
                failures.Add((obj.Key, "The server listed this key outside the folder being downloaded; it was skipped."));
                continue;
            }

            try
            {
                var local = LocalPathMapper.ToLocalPath(localRoot, obj.Key[prefix.Length..]);
                var start = bytes;
                var inner = progress is null ? null : new SyncProgress(p => progress.Report(new TransferProgress(start + p.Transferred, null) { Status = p.Status }));
                var written = await DownloadFileAsync(client, bucket, obj.Key, local, conflict, inner, ct, retry).ConfigureAwait(true);
                if (written is null)
                {
                    skipped++;
                }
                else
                {
                    done++;
                    bytes += obj.Size ?? 0;
                }
            }
            catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
            {
                failures.Add((obj.Key, ex.Error.Describe()));
            }
            catch (ArgumentException ex)
            {
                failures.Add((obj.Key, ex.Message));
            }
        }

        var summary = $"Downloaded {done} file(s)" + (skipped > 0 ? $", skipped {skipped} existing" : string.Empty)
            + (failures.Count > 0 ? $", {failures.Count} failed" : string.Empty);
        return new BulkResult(done, failures, summary);
    }

    /// <summary>Uploads every file below <paramref name="localFolder"/> to <paramref name="prefix"/> + "folder name/" + relative path.</summary>
    /// <param name="retry">Retry policy shared by every file; null = <see cref="TransferRetry.DefaultDelays"/>.</param>
    public static async Task<BulkResult> UploadFolderAsync(IObjectStorageClient client, string bucket, string prefix, string localFolder, Func<string, string?> contentType, IProgress<TransferProgress>? progress, CancellationToken ct, TransferRetry? retry = null)
    {
        retry ??= new TransferRetry();
        var root = Path.GetFullPath(localFolder);
        var folderPrefix = prefix + LocalPathMapper.SafeName(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))) + "/";
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System })
            .ToList();
        var total = files.Sum(f => new FileInfo(f).Length);
        long done = 0;
        var uploaded = 0;
        var failures = new List<(string, string)>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var key = LocalPathMapper.ToKey(folderPrefix, root, file);
            var start = done;
            try
            {
                var inner = progress is null ? null : new SyncProgress(p => progress.Report(new TransferProgress(start + p.Transferred, total) { Status = p.Status }));
                await UploadFileAsync(client, bucket, key, file, contentType(file), inner, ct, retry).ConfigureAwait(true);
                uploaded++;
            }
            catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
            {
                failures.Add((key, ex.Error.Describe()));
            }

            done = start + new FileInfo(file).Length;
            progress?.Report(new TransferProgress(done, total));
        }

        if (files.Count == 0)
        {
            // An empty folder still appears on the server, as a folder marker.
            await client.CreateFolderAsync(bucket, folderPrefix, ct).ConfigureAwait(true);
        }

        return new BulkResult(uploaded, failures, failures.Count == 0
            ? $"Uploaded {uploaded} file(s) to {folderPrefix}"
            : $"Uploaded {uploaded} of {files.Count} file(s); {failures.Count} failed");
    }

    /// <summary>"name.ext" → "name (copy).ext", "name (copy 2).ext"… not in <paramref name="taken"/>.</summary>
    public static string CopyName(string name, bool isFolder, ISet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isFolder ? string.Empty : Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} (copy{(i == 1 ? string.Empty : " " + i)}){ext}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover ".storiva-partial" file is visibly incomplete.
        }
    }

    /// <summary>IProgress that reports synchronously (the outer progress already marshals to the UI thread).</summary>
    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}
