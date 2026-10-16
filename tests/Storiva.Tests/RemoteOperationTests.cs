using System.Text;
using Storiva.Core.Remote;
using Storiva.Core.Tasks;
using Storiva.Tests.Fakes;

namespace Storiva.Tests;

public class RemoteOperationsTests
{
    private static InMemoryObjectStorageClient Seeded()
    {
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "docs/", []);
        client.Put("b", "docs/a.txt", [1]);
        client.Put("b", "docs/sub/b.txt", [2]);
        client.Put("b", "docsx/keep.txt", [3]); // shares the "docs" string, not the "docs/" prefix
        return client;
    }

    [Fact]
    public async Task Delete_prefix_removes_only_that_folder()
    {
        var client = Seeded();
        var result = await RemoteOperations.DeletePrefixAsync(client, "b", "docs/", null, CancellationToken.None);
        Assert.Equal(3, result.Succeeded);
        Assert.Equal(["docsx/keep.txt"], client.Keys("b"));
        await Assert.ThrowsAsync<ArgumentException>(() => RemoteOperations.DeletePrefixAsync(client, "b", string.Empty, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => RemoteOperations.DeletePrefixAsync(client, "b", "docs", null, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_prefix_reports_refused_keys()
    {
        var client = Seeded();
        client.FailDeleteKeys.Add("docs/a.txt");
        var result = await RemoteOperations.DeletePrefixAsync(client, "b", "docs/", null, CancellationToken.None);
        Assert.True(result.HasFailures);
        Assert.Equal("docs/a.txt", result.Failures.Single().Key);
        Assert.Contains("docs/a.txt", client.Keys("b"));
    }

    [Fact]
    public async Task Move_deletes_sources_only_after_every_copy_succeeded()
    {
        var client = Seeded();
        client.FailCopyKeys.Add("docs/sub/b.txt");
        var failed = await RemoteOperations.MovePrefixAsync(client, "b", "docs/", "b", "archive/docs/", null, CancellationToken.None);
        Assert.True(failed.HasFailures);
        Assert.Contains("Nothing was deleted", failed.Summary, StringComparison.Ordinal);
        Assert.Contains("docs/a.txt", client.Keys("b"));  // source untouched

        client.FailCopyKeys.Clear();
        var moved = await RemoteOperations.MovePrefixAsync(client, "b", "docs/", "b", "archive/docs/", null, CancellationToken.None);
        Assert.False(moved.HasFailures);
        Assert.DoesNotContain(client.Keys("b"), k => k.StartsWith("docs/", StringComparison.Ordinal));
        Assert.Contains("archive/docs/sub/b.txt", client.Keys("b"));
    }

    [Fact]
    public async Task A_failed_delete_after_copy_is_reported_as_a_partial_move()
    {
        var client = Seeded();
        client.FailDeleteKeys.Add("docs/a.txt");
        var result = await RemoteOperations.MovePrefixAsync(client, "b", "docs/", "b", "new/", null, CancellationToken.None);
        Assert.True(result.PartialMove);
        Assert.Contains("both places", result.Summary, StringComparison.Ordinal);
        Assert.Contains("docs/a.txt", client.Keys("b"));
        Assert.Contains("new/a.txt", client.Keys("b"));

        var single = await ((IObjectStorageClient)client).MoveObjectAsync("b", "docs/a.txt", "b", "other.txt", CancellationToken.None);
        Assert.False(single.SourceDeleted);
        Assert.NotNull(single.DeleteError);
    }

    [Fact]
    public async Task Copying_or_moving_a_folder_into_itself_is_refused()
    {
        var client = Seeded();
        await Assert.ThrowsAsync<ArgumentException>(() => RemoteOperations.CopyPrefixAsync(client, "b", "docs/", "b", "docs/copy/", null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => RemoteOperations.MovePrefixAsync(client, "b", "docs/", "b", "docs/sub/x/", null, CancellationToken.None));
        Assert.Equal(4, client.Keys("b").Count);
    }

    [Fact]
    public async Task Download_writes_a_partial_file_first_and_renames_it_when_complete()
    {
        using var temp = new TempDataFolder();
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "f.bin", Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());
        var target = Path.Combine(temp.Root, "f.bin");
        var seenPartial = false;
        client.OnDownloadChunk = () => seenPartial = File.Exists(target + ".storiva-partial") && !File.Exists(target);
        var reports = new List<TransferProgress>();
        var written = await RemoteOperations.DownloadFileAsync(client, "b", "f.bin", target, ConflictPolicy.Overwrite, new SyncProgress(reports.Add), CancellationToken.None);
        Assert.Equal(target, written);
        Assert.True(seenPartial);
        Assert.Equal(1000, new FileInfo(target).Length);
        Assert.False(File.Exists(target + ".storiva-partial"));
        Assert.Equal(100, reports[^1].Percent);
    }

    [Fact]
    public async Task A_canceled_download_leaves_no_file()
    {
        using var temp = new TempDataFolder();
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "f.bin", new byte[100]);
        using var cancel = new CancellationTokenSource();
        client.OnDownloadChunk = cancel.Cancel;
        var target = Path.Combine(temp.Root, "f.bin");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteOperations.DownloadFileAsync(client, "b", "f.bin", target, ConflictPolicy.Overwrite, null, cancel.Token));
        Assert.Empty(Directory.GetFiles(temp.Root));
    }

    [Theory]
    [InlineData(ConflictPolicy.Skip, "old", false)]
    [InlineData(ConflictPolicy.Overwrite, "new", false)]
    [InlineData(ConflictPolicy.KeepBoth, "old", true)]
    public async Task Conflicts_follow_the_chosen_policy(ConflictPolicy policy, string expectedOriginal, bool copyCreated)
    {
        using var temp = new TempDataFolder();
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "f.txt", Encoding.UTF8.GetBytes("new"));
        var target = Path.Combine(temp.Root, "f.txt");
        File.WriteAllText(target, "old");
        await RemoteOperations.DownloadFileAsync(client, "b", "f.txt", target, policy, null, CancellationToken.None);
        Assert.Equal(expectedOriginal, File.ReadAllText(target));
        Assert.Equal(copyCreated, File.Exists(Path.Combine(temp.Root, "f (1).txt")));
    }

    [Fact]
    public async Task Prefix_download_keeps_structure_and_never_leaves_the_folder()
    {
        using var temp = new TempDataFolder();
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "set/", []);
        client.Put("b", "set/a.txt", [1]);
        client.Put("b", "set/sub/b.txt", [2]);
        client.Put("b", "set/../../escape.txt", [3]);
        var result = await RemoteOperations.DownloadPrefixAsync(client, "b", "set/", temp.Root, ConflictPolicy.KeepBoth, null, CancellationToken.None);
        Assert.Equal(3, result.Succeeded);
        Assert.True(File.Exists(Path.Combine(temp.Root, "a.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Root, "sub", "b.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Root, "_", "_", "escape.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(temp.Root)!, "escape.txt")));
    }

    [Fact]
    public async Task Folder_upload_uses_the_folder_name_and_relative_paths()
    {
        using var temp = new TempDataFolder();
        var folder = Path.Combine(temp.Root, "photos");
        Directory.CreateDirectory(Path.Combine(folder, "2026"));
        File.WriteAllBytes(Path.Combine(folder, "a.jpg"), [1, 2]);
        File.WriteAllBytes(Path.Combine(folder, "2026", "b.jpg"), [3]);
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "keep", []);
        var reports = new List<TransferProgress>();
        var result = await RemoteOperations.UploadFolderAsync(client, "b", "uploads/", folder, _ => null, new SyncProgress(reports.Add), CancellationToken.None);
        Assert.Equal(2, result.Succeeded);
        Assert.Contains("uploads/photos/a.jpg", client.Keys("b"));
        Assert.Contains("uploads/photos/2026/b.jpg", client.Keys("b"));
        Assert.Equal(3, reports[^1].Total);
        Assert.Equal(3, reports[^1].Transferred);
    }

    [Fact]
    public void Copy_names_avoid_overwriting()
    {
        var taken = new HashSet<string> { "a.txt", "a (copy).txt", "dir" };
        Assert.Equal("a (copy 2).txt", RemoteOperations.CopyName("a.txt", false, taken));
        Assert.Equal("dir (copy)", RemoteOperations.CopyName("dir", true, taken));
        Assert.Equal("new.txt", RemoteOperations.CopyName("new.txt", false, taken));
    }

    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}

public class RemoteCommandRulesTests
{
    private static Core.Commands.SelectionContext Context(bool connected, params Core.Storage.StorageItem[] selected) =>
        new(Core.Storage.StoragePath.Parse("b"), selected, Core.Storage.StoragePath.Parse("b"), Core.Commands.FocusArea.List, 0, Connected: connected);

    private static readonly Core.Storage.StorageItem Folder = new(Core.Storage.StoragePath.Parse("b/dir"), Core.Storage.StorageItemKind.Folder, null, null, null, Key: "dir/");

    [Fact]
    public void Folders_can_be_downloaded_when_connected()
    {
        Assert.True(Core.Commands.CommandRules.IsEnabled(Core.Commands.CommandId.FileDownload, Context(connected: true, Folder)));
        Assert.False(Core.Commands.CommandRules.IsEnabled(Core.Commands.CommandId.FileDownload, Context(connected: false, Folder)));
    }

    [Fact]
    public void Without_a_connection_only_accounts_options_help_and_view_work()
    {
        var offline = Context(connected: false);
        Assert.True(Core.Commands.CommandRules.IsEnabled(Core.Commands.CommandId.AccountAdd, offline));
        Assert.True(Core.Commands.CommandRules.IsEnabled(Core.Commands.CommandId.AccountManage, offline));
        Assert.True(Core.Commands.CommandRules.IsEnabled(Core.Commands.CommandId.ToolsOptions, offline));
        Assert.True(Core.Commands.CommandRules.IsEnabled(Core.Commands.CommandId.HelpAbout, offline));
        Assert.False(Core.Commands.CommandRules.IsEnabled(Core.Commands.CommandId.BucketCreate, offline));
        Assert.False(Core.Commands.CommandRules.IsEnabled(Core.Commands.CommandId.BucketRefresh, offline));
        Assert.False(Core.Commands.CommandRules.IsEnabled(Core.Commands.CommandId.FileUploadFiles, offline));
        Assert.False(Core.Commands.CommandRules.IsEnabled(Core.Commands.CommandId.BookmarkToggle, offline));
    }
}

public class OperationRunnerTests
{
    private static (TaskQueue Queue, OperationRunner Runner) Create(int max = 2)
    {
        var queue = new TaskQueue(max);
        return (queue, new OperationRunner(queue, progressInterval: TimeSpan.Zero));
    }

    private static async Task<bool> Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task A_real_task_reports_progress_and_completes()
    {
        var (queue, runner) = Create();
        var gate = new TaskCompletionSource();
        var task = runner.Start("upload", TransferKind.Upload, 200, async ctx =>
        {
            ctx.Progress.Report(new TransferProgress(50, 200));
            await gate.Task;
            ctx.Progress.Report(new TransferProgress(200, 200));
            return OperationOutcome.Done("ok");
        });
        Assert.True(await Until(() => task.State == TransferState.Running && task.Progress == 25));
        gate.SetResult();
        Assert.True(await Until(() => task.State == TransferState.Completed));
        Assert.Equal((100, "ok"), (task.Progress, task.Message));
    }

    [Fact]
    public async Task Unknown_totals_are_indeterminate_not_invented()
    {
        var (_, runner) = Create();
        var gate = new TaskCompletionSource();
        var task = runner.Start("delete folder", TransferKind.Delete, null, async ctx =>
        {
            ctx.Progress.Report(new TransferProgress(10, null));
            await gate.Task;
            return OperationOutcome.Done("deleted");
        });
        Assert.True(await Until(() => task.Processed == 10));
        Assert.True(task.IsIndeterminate);
        Assert.Equal(0, task.Progress);
        gate.SetResult();
        Assert.True(await Until(() => task.State == TransferState.Completed));
    }

    [Fact]
    public async Task Cancel_stops_a_running_operation_and_a_queued_one_immediately()
    {
        var (queue, runner) = Create(max: 1);
        var running = runner.Start("a", TransferKind.Download, 10, async ctx =>
        {
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            return OperationOutcome.Done("never");
        });
        var waiting = runner.Start("b", TransferKind.Download, 10, _ => Task.FromResult(OperationOutcome.Done("b")));
        Assert.True(await Until(() => running.State == TransferState.Running));
        Assert.Equal(TransferState.Queued, waiting.State); // limit 1

        Assert.True(queue.Cancel(waiting.Id));
        Assert.Equal(TransferState.Canceled, waiting.State);
        Assert.True(queue.Cancel(running.Id));
        Assert.True(running.CancelRequested);
        Assert.True(await Until(() => running.State == TransferState.Canceled));
        Assert.False(TaskQueue.CanCancel(running));
    }

    [Fact]
    public async Task Failures_keep_the_safe_message_and_can_be_retried()
    {
        var (queue, runner) = Create();
        var attempts = 0;
        var task = runner.Start("copy", TransferKind.Copy, null, _ =>
        {
            attempts++;
            return attempts == 1
                ? throw new StorageOperationException(new StorageError(StorageErrorKind.AccessDenied, "Copy was refused", "AccessDenied", 403))
                : Task.FromResult(OperationOutcome.Done("copied"));
        });
        Assert.True(await Until(() => task.State == TransferState.Failed));
        Assert.Equal("Copy was refused (AccessDenied, HTTP 403)", task.Message);
        Assert.True(TaskQueue.CanRetry(task));
        Assert.True(queue.Retry(task.Id));
        Assert.True(await Until(() => task.State == TransferState.Completed));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Partial_outcomes_end_as_failed_and_the_limit_is_respected()
    {
        var (queue, runner) = Create(max: 2);
        var gates = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource()).ToArray();
        var tasks = gates.Select((g, i) => runner.Start($"t{i}", TransferKind.Copy, null, async _ =>
        {
            await g.Task;
            return OperationOutcome.Partial("2 of 3 copied");
        })).ToList();
        Assert.True(await Until(() => tasks.Count(t => t.State == TransferState.Running) == 2));
        Assert.Equal(TransferState.Queued, tasks[2].State);
        gates[0].SetResult();
        Assert.True(await Until(() => tasks[2].State == TransferState.Running));
        gates[1].SetResult();
        gates[2].SetResult();
        Assert.True(await Until(() => tasks.All(t => t.State == TransferState.Failed)));
        Assert.Equal("2 of 3 copied", tasks[0].Message);
    }

    [Fact]
    public async Task Cancel_all_and_clear_finished()
    {
        var (queue, runner) = Create(max: 1);
        var done = runner.Start("done", TransferKind.Upload, 10, _ => Task.FromResult(OperationOutcome.Done("ok")));
        Assert.True(await Until(() => done.State == TransferState.Completed));
        var blocked = runner.Start("blocked", TransferKind.Upload, 10, async ctx =>
        {
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            return OperationOutcome.Done("never");
        });
        var waiting = runner.Start("waiting", TransferKind.Upload, 10, _ => Task.FromResult(OperationOutcome.Done("w")));
        Assert.True(await Until(() => blocked.State == TransferState.Running));

        Assert.Equal(2, queue.CancelAll());
        Assert.True(await Until(() => blocked.State == TransferState.Canceled && waiting.State == TransferState.Canceled));
        Assert.Equal(0, queue.CancelAll());
        Assert.Equal(3, queue.ClearFinished());
        Assert.Empty(queue.Tasks);
    }
}
