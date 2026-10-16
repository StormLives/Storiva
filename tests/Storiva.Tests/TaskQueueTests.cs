using Storiva.Core.Tasks;

namespace Storiva.Tests;

public class TaskQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Starts_empty_without_sample_tasks()
    {
        var queue = new TaskQueue();
        Assert.Empty(queue.Tasks);
        Assert.False(queue.HasActiveWork);
        Assert.Equal(2, queue.MaxConcurrent);
    }

    [Fact]
    public void AddOperation_adds_a_queued_task_with_the_next_id_and_its_size()
    {
        var queue = new TaskQueue();
        var raised = 0;
        queue.Changed += (_, _) => raised++;

        var first = queue.AddOperation("a", TransferKind.Upload, null, T0, () => { }, null);
        var second = queue.AddOperation("bucket/file.csv", TransferKind.Download, 12_288, T0, () => { }, () => { });

        Assert.Equal((1, 2), (first.Id, second.Id));
        Assert.Equal((TransferState.Queued, 0, 12_288L), (second.State, second.Progress, second.Size!.Value));
        Assert.False(first.Retryable);
        Assert.True(second.Retryable);
        Assert.Equal(2, raised);
        Assert.True(queue.HasActiveWork);
    }

    [Fact]
    public void Cancel_asks_the_operation_to_stop_once()
    {
        var queue = new TaskQueue();
        var canceled = 0;
        var task = queue.AddOperation("a", TransferKind.Upload, null, T0, () => canceled++, null);

        Assert.True(queue.Cancel(task.Id));
        Assert.True(task.CancelRequested);
        Assert.Equal(1, canceled);
        Assert.False(queue.Cancel(task.Id)); // already requested
        Assert.Equal(TransferState.Queued, task.State); // the state follows the operation, not the request
    }

    [Fact]
    public void Unknown_ids_and_unfinished_tasks_cannot_be_retried()
    {
        var queue = new TaskQueue();
        var task = queue.AddOperation("a", TransferKind.Upload, null, T0, () => { }, () => { });
        Assert.False(queue.Cancel(99));
        Assert.False(queue.Retry(99));
        Assert.False(queue.Retry(task.Id)); // still queued
    }

    [Fact]
    public void Changed_is_raised_only_when_something_changes()
    {
        var queue = new TaskQueue();
        var raised = 0;
        queue.Changed += (_, _) => raised++;
        Assert.Equal(0, queue.CancelAll());
        Assert.Equal(0, queue.ClearFinished());
        Assert.False(queue.Cancel(1));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Capacity_changes_are_validated_and_announced()
    {
        var queue = new TaskQueue(maxConcurrent: 2);
        var raised = 0;
        queue.CapacityChanged += (_, _) => raised++;
        queue.MaxConcurrent = 2;
        Assert.Equal(0, raised);
        queue.MaxConcurrent = 3;
        Assert.Equal(1, raised);
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.MaxConcurrent = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskQueue(0));
    }

    [Fact]
    public async Task Filters_match_the_states_reported_by_operations()
    {
        var queue = new TaskQueue(maxConcurrent: 4);
        var runner = new OperationRunner(queue, progressInterval: TimeSpan.Zero);
        var gate = new TaskCompletionSource<OperationOutcome>();
        var running = runner.Start("running", TransferKind.Copy, null, _ => gate.Task);
        var done = runner.Start("done", TransferKind.Copy, null, _ => Task.FromResult(OperationOutcome.Done("ok")));
        var failed = runner.Start("failed", TransferKind.Copy, null, _ => Task.FromResult(OperationOutcome.Partial("1 of 2")));
        for (var i = 0; i < 400 && !(running.State == TransferState.Running && done.IsFinished && failed.State == TransferState.Failed); i++)
        {
            await Task.Delay(10);
        }

        Assert.Single(queue.Filter(TaskFilter.Running));
        Assert.Single(queue.Filter(TaskFilter.Completed));
        Assert.Single(queue.Filter(TaskFilter.Failed));
        Assert.Empty(queue.Filter(TaskFilter.Queued));
        Assert.Empty(queue.Filter(TaskFilter.Canceled));
        Assert.Equal(3, queue.Filter(TaskFilter.All).Count);
        gate.SetResult(OperationOutcome.Done("ok"));
    }
}
