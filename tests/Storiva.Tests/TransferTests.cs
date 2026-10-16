using System.Net.Http;
using System.Security.Cryptography;
using Amazon.Runtime;
using Storiva.Core.Connections;
using Storiva.Core.Remote;
using Storiva.Core.Tasks;
using Storiva.FakeS3;
using Storiva.Infrastructure.S3;
using Storiva.Tests.Fakes;

namespace Storiva.Tests;

public class TransferRateTests
{
    private const long Mb = 1024 * 1024;
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Speed_appears_after_a_full_window_and_is_smoothed()
    {
        var rate = new TransferRate();
        rate.Sample(0, T0);
        rate.Sample(Mb / 2, T0.AddMilliseconds(500));
        Assert.Null(rate.BytesPerSecond); // less than one window of data: nothing is invented

        rate.Sample(2 * Mb, T0.AddSeconds(1));
        Assert.Equal(2 * Mb, rate.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(4), rate.Remaining(2 * Mb, 10 * Mb));
        Assert.Equal("2 MB/s · 4 s left", rate.Describe(2 * Mb, 10 * Mb));

        // A burst moves the estimate only part of the way.
        rate.Sample(7 * Mb, T0.AddSeconds(2));
        Assert.InRange(rate.BytesPerSecond!.Value, 2.5 * Mb, 3.5 * Mb);
        Assert.Equal("2.9 MB/s", rate.Describe(7 * Mb, null)); // 2 + 0.3 × (5 − 2); unknown total: speed only
    }

    [Fact]
    public void Going_back_restarts_the_measurement()
    {
        var rate = new TransferRate();
        rate.Sample(0, T0);
        rate.Sample(Mb, T0.AddSeconds(1));
        Assert.NotNull(rate.BytesPerSecond);
        rate.Sample(0, T0.AddSeconds(2)); // a retried file starts over
        Assert.Null(rate.BytesPerSecond);
        Assert.Null(rate.Describe(0, 10 * Mb));
        rate.Sample(Mb, T0.AddSeconds(3));
        Assert.Equal(Mb, rate.BytesPerSecond);
    }

    [Theory]
    [InlineData(0.2, "1 s")]
    [InlineData(12, "12 s")]
    [InlineData(59.5, "1 min")]
    [InlineData(200, "3 min 20 s")]
    [InlineData(900, "15 min")]
    [InlineData(3599, "59 min")]
    [InlineData(3600, "1 h")]
    [InlineData(3900, "1 h 5 min")]
    [InlineData(400_000, "more than 4 days")]
    public void Durations_are_short_and_rounded_up(double seconds, string expected) =>
        Assert.Equal(expected, TransferRate.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Speeds_use_the_size_units_of_the_file_list() =>
        Assert.Equal("4.2 MB/s", TransferRate.FormatSpeed(4.2 * Mb));
}

public class TransferProgressDisplayTests
{
    private const long Mb = 1024 * 1024;
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<bool> Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task A_running_transfer_shows_speed_and_time_left_and_completes_with_the_average()
    {
        var now = T0;
        var queue = new TaskQueue();
        var runner = new OperationRunner(queue, () => now, TimeSpan.Zero);
        var measured = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var task = runner.Start("big.bin", TransferKind.Download, 10 * Mb, async ctx =>
        {
            ctx.Progress.Report(new TransferProgress(0, 10 * Mb));
            now = T0.AddSeconds(1);
            ctx.Progress.Report(new TransferProgress(Mb, 10 * Mb));
            now = T0.AddSeconds(2);
            ctx.Progress.Report(new TransferProgress(2 * Mb, 10 * Mb));
            measured.SetResult();
            await finish.Task;
            now = T0.AddSeconds(10);
            ctx.Progress.Report(new TransferProgress(10 * Mb, 10 * Mb));
            return OperationOutcome.Done("Saved");
        });

        await measured.Task;
        Assert.True(await Until(() => task.Processed == 2 * Mb));
        Assert.Equal(Mb, task.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(8), task.TimeRemaining);
        Assert.Equal("1 MB/s · 8 s left", task.DisplayMessage);

        finish.SetResult();
        Assert.True(await Until(() => task.State == TransferState.Completed));
        Assert.Equal("Saved (10 s, 1 MB/s on average)", task.Message);
        Assert.Equal(task.Message, task.DisplayMessage);
    }

    [Fact]
    public async Task A_retry_notice_replaces_the_message_and_restarts_the_speed()
    {
        var now = T0;
        var queue = new TaskQueue();
        var runner = new OperationRunner(queue, () => now, TimeSpan.Zero);
        var noted = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var task = runner.Start("big.bin", TransferKind.Upload, 10 * Mb, async ctx =>
        {
            ctx.Progress.Report(new TransferProgress(0, 10 * Mb));
            now = T0.AddSeconds(1);
            ctx.Progress.Report(new TransferProgress(Mb, 10 * Mb));
            ctx.Progress.Report(new TransferProgress(Mb, 10 * Mb) { Status = "Server error (HTTP 500): retrying 2/3 in 2 s…" });
            noted.SetResult();
            await finish.Task;
            return OperationOutcome.Done("Uploaded");
        });

        await noted.Task;
        Assert.True(await Until(() => task.Message.Contains("retrying 2/3", StringComparison.Ordinal)));
        Assert.Null(task.BytesPerSecond);
        Assert.Equal("Server error (HTTP 500): retrying 2/3 in 2 s…", task.DisplayMessage);
        finish.SetResult();
        Assert.True(await Until(() => task.State == TransferState.Completed));
        Assert.StartsWith("Uploaded", task.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bulk_operations_count_objects_and_show_no_speed()
    {
        var now = T0;
        var queue = new TaskQueue();
        var runner = new OperationRunner(queue, () => now, TimeSpan.Zero);
        var finish = new TaskCompletionSource();
        var task = runner.Start("folder", TransferKind.Delete, null, async ctx =>
        {
            ctx.Progress.Report(new TransferProgress(0, null));
            now = T0.AddSeconds(5);
            ctx.Progress.Report(new TransferProgress(500, null));
            await finish.Task;
            return OperationOutcome.Done("Deleted 500 object(s)");
        });

        Assert.True(await Until(() => task.Processed == 500));
        Assert.Null(task.BytesPerSecond);
        Assert.Equal(TransferTask.InProgressMessage, task.DisplayMessage);
        now = T0.AddSeconds(10);
        finish.SetResult();
        Assert.True(await Until(() => task.State == TransferState.Completed));
        Assert.Equal("Deleted 500 object(s)", task.Message); // no byte average for object counts
    }
}

public class TransferRetryTests
{
    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    [Theory]
    [InlineData(StorageErrorKind.EndpointUnreachable, true)]
    [InlineData(StorageErrorKind.Throttled, true)]
    [InlineData(StorageErrorKind.ServerError, true)]
    [InlineData(StorageErrorKind.AccessDenied, false)]
    [InlineData(StorageErrorKind.InvalidCredentials, false)]
    [InlineData(StorageErrorKind.InvalidRequest, false)]
    [InlineData(StorageErrorKind.NotFound, false)]
    [InlineData(StorageErrorKind.Tls, false)]
    [InlineData(StorageErrorKind.LocalIo, false)]
    [InlineData(StorageErrorKind.Canceled, false)]
    public void Only_transient_failures_are_retried(StorageErrorKind kind, bool transient) =>
        Assert.Equal(transient, TransferRetry.IsTransient(new StorageError(kind, "x")));

    [Fact]
    public async Task An_interrupted_download_starts_over_and_the_file_is_complete()
    {
        using var temp = new TempDataFolder();
        var client = new InMemoryObjectStorageClient();
        var content = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        client.Put("b", "f.bin", content);
        client.InterruptDownloads["f.bin"] = 1;
        var target = Path.Combine(temp.Root, "f.bin");
        var reports = new List<TransferProgress>();
        var written = await RemoteOperations.DownloadFileAsync(client, "b", "f.bin", target, ConflictPolicy.Overwrite, new SyncProgress(reports.Add), CancellationToken.None, new TransferRetry([TimeSpan.Zero, TimeSpan.Zero]));
        Assert.Equal(target, written);
        Assert.Equal(content, File.ReadAllBytes(target)); // the half written by the failed attempt was dropped
        Assert.False(File.Exists(target + ".storiva-partial"));
        Assert.Equal(2, client.Calls.Count(c => c == "Download f.bin"));
        Assert.Contains(reports, r => r.Status == "Endpoint not reachable: retrying 2/3…");
        Assert.Contains(reports, r => r.Status == "Retrying 2/3…");
        Assert.Equal(TransferTask.InProgressMessage, reports.Last(r => r.Status is not null).Status);
    }

    [Fact]
    public async Task Permission_and_missing_object_errors_are_not_retried()
    {
        using var temp = new TempDataFolder();
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "f.bin", [1, 2]);
        client.FailTransfers["f.bin"] = (5, StorageErrorKind.AccessDenied);
        var target = Path.Combine(temp.Root, "f.bin");
        var error = await Assert.ThrowsAsync<StorageOperationException>(() => RemoteOperations.DownloadFileAsync(client, "b", "f.bin", target, ConflictPolicy.Overwrite, null, CancellationToken.None, new TransferRetry([TimeSpan.Zero, TimeSpan.Zero])));
        Assert.Equal(StorageErrorKind.AccessDenied, error.Kind);
        await Assert.ThrowsAsync<StorageOperationException>(() => RemoteOperations.DownloadFileAsync(client, "b", "missing.bin", target, ConflictPolicy.Overwrite, null, CancellationToken.None, new TransferRetry([TimeSpan.Zero, TimeSpan.Zero])));
        Assert.Equal(1, client.Calls.Count(c => c == "Download f.bin"));
        Assert.Equal(1, client.Calls.Count(c => c == "Download missing.bin"));
        Assert.Empty(Directory.GetFiles(temp.Root));
    }

    [Fact]
    public async Task After_the_last_attempt_the_failure_says_so_and_no_partial_file_is_left()
    {
        using var temp = new TempDataFolder();
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "f.bin", new byte[100]);
        client.InterruptDownloads["f.bin"] = 10;
        var target = Path.Combine(temp.Root, "f.bin");
        var error = await Assert.ThrowsAsync<StorageOperationException>(() => RemoteOperations.DownloadFileAsync(client, "b", "f.bin", target, ConflictPolicy.Overwrite, null, CancellationToken.None, new TransferRetry([TimeSpan.Zero, TimeSpan.Zero])));
        Assert.Equal(StorageErrorKind.EndpointUnreachable, error.Kind);
        Assert.EndsWith("(tried 3 times)", error.Error.Message, StringComparison.Ordinal);
        Assert.Equal(3, client.Calls.Count(c => c == "Download f.bin"));
        Assert.Empty(Directory.GetFiles(temp.Root));
    }

    [Fact]
    public async Task An_outage_does_not_make_every_file_of_a_folder_wait()
    {
        using var temp = new TempDataFolder();
        var folder = Path.Combine(temp.Root, "set");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "a.bin", "b.bin", "c.bin", "d.bin" })
        {
            File.WriteAllBytes(Path.Combine(folder, name), [1, 2, 3]);
        }

        var client = new InMemoryObjectStorageClient();
        client.FailTransfers["up/set/a.bin"] = (10, StorageErrorKind.EndpointUnreachable);
        client.FailTransfers["up/set/b.bin"] = (10, StorageErrorKind.EndpointUnreachable);
        client.FailTransfers["up/set/d.bin"] = (1, StorageErrorKind.Throttled);
        var result = await RemoteOperations.UploadFolderAsync(client, "b", "up/", folder, _ => null, null, CancellationToken.None, new TransferRetry([TimeSpan.Zero, TimeSpan.Zero]));

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(["up/set/a.bin", "up/set/b.bin"], result.Failures.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Equal(3, client.Calls.Count(c => c == "Upload up/set/a.bin")); // all its attempts
        Assert.Equal(1, client.Calls.Count(c => c == "Upload up/set/b.bin")); // the outage was already known: no wait
        Assert.Equal(1, client.Calls.Count(c => c == "Upload up/set/c.bin")); // a success ends the outage…
        Assert.Equal(2, client.Calls.Count(c => c == "Upload up/set/d.bin")); // …so retries are used again
        Assert.Contains("up/set/d.bin", client.Keys("b"));
    }

    [Fact]
    public async Task A_transient_upload_failure_is_retried_and_the_folder_completes()
    {
        using var temp = new TempDataFolder();
        var folder = Path.Combine(temp.Root, "set");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(folder, "b.bin"), [5, 6]);
        var client = new InMemoryObjectStorageClient();
        client.FailTransfers["up/set/a.bin"] = (2, StorageErrorKind.ServerError);
        var reports = new List<TransferProgress>();
        var result = await RemoteOperations.UploadFolderAsync(client, "b", "up/", folder, _ => null, new SyncProgress(reports.Add), CancellationToken.None, new TransferRetry([TimeSpan.Zero, TimeSpan.Zero]));

        Assert.False(result.HasFailures);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(3, client.Calls.Count(c => c == "Upload up/set/a.bin"));
        Assert.Contains(reports, r => r.Status == "Server error (HTTP 500): retrying 3/3…");
        Assert.All(reports, r => Assert.Equal(6, r.Total)); // notices keep the folder's byte count
        Assert.Equal(6, reports[^1].Transferred);
    }

    [Fact]
    public async Task Canceling_during_the_wait_stops_at_once_and_removes_the_partial_file()
    {
        using var temp = new TempDataFolder();
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "f.bin", new byte[100]);
        client.InterruptDownloads["f.bin"] = 1;
        using var cancel = new CancellationTokenSource();
        var target = Path.Combine(temp.Root, "f.bin");
        var progress = new SyncProgress(p =>
        {
            if (p.Status?.Contains("retrying 2/2 in 5 min", StringComparison.Ordinal) == true)
            {
                cancel.Cancel();
            }
        });
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteOperations.DownloadFileAsync(client, "b", "f.bin", target, ConflictPolicy.Overwrite, progress, cancel.Token, new TransferRetry([TimeSpan.FromMinutes(5)])));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
        Assert.Empty(Directory.GetFiles(temp.Root));
    }

    [Fact]
    public async Task Retry_notices_reach_the_task_message()
    {
        var queue = new TaskQueue();
        var runner = new OperationRunner(queue, progressInterval: TimeSpan.Zero);
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "f.bin", new byte[100]);
        client.InterruptDownloads["f.bin"] = 1;
        using var temp = new TempDataFolder();
        var target = Path.Combine(temp.Root, "f.bin");
        var messages = new List<string>();
        queue.Changed += (_, _) =>
        {
            lock (messages)
            {
                messages.AddRange(queue.Tasks.Select(t => t.Message));
            }
        };
        var task = runner.Start("f.bin", TransferKind.Download, 100, async ctx =>
        {
            await RemoteOperations.DownloadFileAsync(client, "b", "f.bin", target, ConflictPolicy.Overwrite, ctx.Progress, ctx.CancellationToken, new TransferRetry([TimeSpan.FromMilliseconds(50)]));
            return OperationOutcome.Done("Saved");
        });
        for (var i = 0; i < 400 && !task.IsFinished; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(TransferState.Completed, task.State);
        lock (messages)
        {
            Assert.Contains("Endpoint not reachable: retrying 2/2…", messages);
        }
    }
}

/// <summary>Transient failures through the real AWS SDK against <see cref="FakeS3Server"/> on 127.0.0.1.</summary>
public sealed class TransferRetryAgainstFakeServerTests : IAsyncLifetime
{
    private readonly FakeS3Server _server = new();
    private IObjectStorageClient _client = null!;

    public Task InitializeAsync()
    {
        _server.Start();
        _server.AddBucket("alpha");
        _client = new S3ClientFactory().Create(S3ClientAgainstFakeServerTests.Profile(_server.Endpoint), new StorageCredentials(FakeS3Server.TestAccessKey, FakeS3Server.TestSecret));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    [Fact]
    public async Task A_connection_dropped_in_the_middle_of_the_body_is_a_network_error_and_is_retried()
    {
        var bytes = RandomNumberGenerator.GetBytes(200_000);
        _server.PutObject("alpha", "big.bin", bytes);
        using var temp = new TempDataFolder();
        var target = Path.Combine(temp.Root, "big.bin");

        // Every attempt is cut: the error is a network one (not a local file error) and nothing is left behind.
        _server.TruncatedKeys["big.bin"] = 10;
        var error = await Assert.ThrowsAsync<StorageOperationException>(() => RemoteOperations.DownloadFileAsync(_client, "alpha", "big.bin", target, ConflictPolicy.Overwrite, null, CancellationToken.None, new TransferRetry([TimeSpan.Zero])));
        Assert.Equal(StorageErrorKind.EndpointUnreachable, error.Kind);
        Assert.Contains("tried 2 times", error.Error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(temp.Root));

        // One cut: the second attempt completes the file.
        _server.TruncatedKeys["big.bin"] = 1;
        var reports = new List<TransferProgress>();
        await RemoteOperations.DownloadFileAsync(_client, "alpha", "big.bin", target, ConflictPolicy.Overwrite, new SyncProgress(reports.Add), CancellationToken.None, new TransferRetry([TimeSpan.Zero]));
        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.Contains(reports, r => r.Status?.StartsWith("Endpoint not reachable: retrying 2/2", StringComparison.Ordinal) == true);
        Assert.Single(Directory.GetFiles(temp.Root));
    }

    [Fact]
    public async Task Server_errors_beyond_the_sdk_retries_are_retried_once_more_for_the_whole_file()
    {
        using var temp = new TempDataFolder();
        var source = Path.Combine(temp.Root, "data.bin");
        var bytes = RandomNumberGenerator.GetBytes(50_000);
        File.WriteAllBytes(source, bytes);
        var config = S3ClientFactory.BuildConfig(S3ClientAgainstFakeServerTests.Profile(_server.Endpoint));
        var sdkAttempts = config.MaxErrorRetry + 1;

        // The SDK's own attempts all fail; the next whole-file attempt succeeds.
        _server.FailingKeys["in/data.bin"] = sdkAttempts;
        var reports = new List<TransferProgress>();
        await RemoteOperations.UploadFileAsync(_client, "alpha", "in/data.bin", source, null, new SyncProgress(reports.Add), CancellationToken.None, new TransferRetry([TimeSpan.Zero]));
        Assert.Equal(bytes, _server.GetObject("alpha", "in/data.bin"));
        Assert.Contains(reports, r => r.Status == "Server error (HTTP 500): retrying 2/2…");
        Assert.Equal(sdkAttempts + 1, _server.Requests.Count(r => r.StartsWith("PUT /alpha/in/data.bin", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_sdk_retry_mode_is_pinned()
    {
        var config = S3ClientFactory.BuildConfig(S3ClientAgainstFakeServerTests.Profile(_server.Endpoint));
        Assert.Equal(RequestRetryMode.Standard, config.RetryMode);
        Assert.Equal(2, config.MaxErrorRetry);
    }

    [Fact]
    public void Timeouts_and_dropped_bodies_are_network_errors_not_cancellations_or_disk_errors()
    {
        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException("timed out"));
        Assert.Equal(StorageErrorKind.EndpointUnreachable, S3ErrorMapper.ToError(timeout, "Downloading").Kind);
        Assert.Equal(StorageErrorKind.EndpointUnreachable, S3ErrorMapper.ToError(new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."), "Downloading").Kind);
        Assert.Equal(StorageErrorKind.Canceled, S3ErrorMapper.ToError(new TaskCanceledException(), "Downloading").Kind);
        Assert.Equal(StorageErrorKind.LocalIo, S3ErrorMapper.ToError(new IOException("There is not enough space on the disk."), "Downloading").Kind);
    }
}
