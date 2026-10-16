using Storiva.Core.Connections;
using Storiva.Core.Remote;

namespace Storiva.Core.Tasks;

/// <summary>Final result of a real operation. <see cref="Success"/> false = completed with failures (task Failed).</summary>
public sealed record OperationOutcome(bool Success, string Message)
{
    public static OperationOutcome Done(string message) => new(true, message);

    public static OperationOutcome Partial(string message) => new(false, message);
}

/// <summary>What a running operation can use: its cancellation token and a progress sink.</summary>
public sealed class OperationContext
{
    internal OperationContext(CancellationToken token, IProgress<TransferProgress> progress)
    {
        CancellationToken = token;
        Progress = progress;
    }

    public CancellationToken CancellationToken { get; }

    /// <summary>Reports real progress. A null total means "unknown": the task shows an indeterminate state.</summary>
    public IProgress<TransferProgress> Progress { get; }
}

public sealed class OperationFinishedEventArgs(TransferTask task, OperationOutcome? outcome, StorageError? error) : EventArgs
{
    public TransferTask Task { get; } = task;

    public OperationOutcome? Outcome { get; } = outcome;

    /// <summary>Set when the operation threw (failed or canceled).</summary>
    public StorageError? Error { get; } = error;
}

/// <summary>
/// Runs real operations as tasks of a <see cref="TaskQueue"/>: at most <see cref="TaskQueue.MaxConcurrent"/> at a
/// time, cancellable, retryable. Create and use it on the UI thread: task state changes are applied on the
/// synchronization context captured at construction, never from worker threads.
/// </summary>
public sealed class OperationRunner
{
    private readonly TaskQueue _queue;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _progressInterval;
    private readonly SynchronizationContext? _context;
    private readonly LinkedList<Pending> _waiting = new();
    private readonly Dictionary<int, Pending> _byTask = new();

    public OperationRunner(TaskQueue queue, Func<DateTimeOffset>? clock = null, TimeSpan? progressInterval = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _progressInterval = progressInterval ?? TimeSpan.FromMilliseconds(100);
        _context = SynchronizationContext.Current;
        _queue.CapacityChanged += (_, _) => Pump();
    }

    /// <summary>Raised on the UI thread when an operation completed, failed or was canceled.</summary>
    public event EventHandler<OperationFinishedEventArgs>? Finished;

    public int Waiting => _waiting.Count;

    /// <summary>Queues <paramref name="work"/> as a task. It starts as soon as a slot is free.</summary>
    /// <param name="size">Total bytes when known (uploads/downloads); null shows an indeterminate state.</param>
    public TransferTask Start(string name, TransferKind kind, long? size, Func<OperationContext, Task<OperationOutcome>> work, bool retryable = true)
    {
        ArgumentNullException.ThrowIfNull(work);
        Pending? pending = null;
        var task = _queue.AddOperation(name, kind, size, _clock(), () => Cancel(pending!), retryable ? () => Retry(pending!) : null);
        pending = new Pending(task, work);
        _byTask[task.Id] = pending;
        _waiting.AddLast(pending);
        Pump();
        return task;
    }

    private void Cancel(Pending pending)
    {
        if (pending.Task.State == TransferState.Queued)
        {
            _waiting.Remove(pending);
            _queue.MarkCanceled(pending.Task, "Canceled by user before it started");
            Finished?.Invoke(this, new OperationFinishedEventArgs(pending.Task, null, new StorageError(StorageErrorKind.Canceled, "Canceled by user")));
            return;
        }

        pending.Cancellation.Cancel();
    }

    private void Retry(Pending pending)
    {
        pending.Reset();
        _queue.MarkQueued(pending.Task, "Queued for retry");
        _waiting.AddLast(pending);
        Pump();
    }

    private void Pump()
    {
        while (_waiting.Count > 0 && _queue.RunningOperations < _queue.MaxConcurrent)
        {
            var next = _waiting.First!.Value;
            _waiting.RemoveFirst();
            _queue.MarkRunning(next.Task, TransferTask.InProgressMessage, _clock());
            _ = RunAsync(next);
        }
    }

    private async Task RunAsync(Pending pending)
    {
        var task = pending.Task;
        var progress = new ThrottledProgress(this, task, _progressInterval);
        var context = new OperationContext(pending.Cancellation.Token, progress);
        OperationOutcome? outcome = null;
        StorageError? error = null;
        try
        {
            // Yield first so a synchronous prefix of the work never runs inside Start().
            await Task.Yield();
            outcome = await pending.Work(context).ConfigureAwait(true);
            progress.Flush();
            if (outcome.Success)
            {
                _queue.Complete(task, outcome.Message + Summary(task));
            }
            else
            {
                _queue.Fail(task, outcome.Message);
            }
        }
        catch (Exception ex) when (IsCancellation(ex, pending.Cancellation.Token))
        {
            error = new StorageError(StorageErrorKind.Canceled, "Canceled by user");
            _queue.MarkCanceled(task, "Canceled by user. What was already completed stays; a partly downloaded file is removed.");
        }
        catch (StorageOperationException ex)
        {
            error = ex.Error;
            _queue.Fail(task, ex.Error.Describe());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = new StorageError(StorageErrorKind.LocalIo, SecretRedactor.Redact(ex.Message));
            _queue.Fail(task, error.Describe());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            error = new StorageError(StorageErrorKind.InvalidRequest, SecretRedactor.Redact(ex.Message));
            _queue.Fail(task, error.Describe());
        }
        finally
        {
            progress.Stop();
            Pump();
        }

        Finished?.Invoke(this, new OperationFinishedEventArgs(task, outcome, error));
    }

    /// <summary>" (2 min 3 s, 10.1 MB/s on average)" for an upload or download that took at least a second.</summary>
    private string Summary(TransferTask task)
    {
        if (!TransferTask.MeasuresBytes(task.Kind) || task.StartedAt is not { } started || task.Processed <= 0)
        {
            return string.Empty;
        }

        var elapsed = _clock() - started;
        return elapsed < TimeSpan.FromSeconds(1)
            ? string.Empty
            : $" ({TransferRate.FormatDuration(elapsed)}, {TransferRate.FormatSpeed(task.Processed / elapsed.TotalSeconds)} on average)";
    }

    private static bool IsCancellation(Exception ex, CancellationToken token) =>
        token.IsCancellationRequested && (ex is OperationCanceledException || ex is StorageOperationException { Kind: StorageErrorKind.Canceled });

    private void Post(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    private sealed class Pending(TransferTask task, Func<OperationContext, Task<OperationOutcome>> work)
    {
        public TransferTask Task { get; } = task;

        public Func<OperationContext, Task<OperationOutcome>> Work { get; } = work;

        public CancellationTokenSource Cancellation { get; private set; } = new();

        public void Reset()
        {
            Cancellation.Dispose();
            Cancellation = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// Keeps the latest value reported from any thread and applies it on the UI thread at most every
    /// <c>interval</c>, so a fast transfer does not flood the task list.
    /// </summary>
    private sealed class ThrottledProgress(OperationRunner owner, TransferTask task, TimeSpan interval) : IProgress<TransferProgress>
    {
        private readonly object _gate = new();
        private TransferProgress? _latest;
        private DateTimeOffset _latestAt; // when the latest value was reported (for the speed)
        private string? _status; // kept until applied, even when newer values without a note arrive
        private DateTime _lastApplied = DateTime.MinValue;
        private bool _posted;
        private bool _stopped;

        public void Report(TransferProgress value)
        {
            bool applyNow;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _latest = value;
                _latestAt = owner._clock();
                _status = value.Status ?? _status;
                if (_posted)
                {
                    return; // an update is already scheduled; it will pick up this value
                }

                _posted = true;
                applyNow = DateTime.UtcNow - _lastApplied >= interval;
            }

            if (applyNow)
            {
                owner.Post(Apply);
            }
            else
            {
                _ = ApplyLaterAsync();
            }
        }

        private async Task ApplyLaterAsync()
        {
            await Task.Delay(interval).ConfigureAwait(false);
            owner.Post(Apply);
        }

        private void Apply()
        {
            TransferProgress? value;
            DateTimeOffset at;
            string? status;
            lock (_gate)
            {
                _posted = false;
                if (_stopped)
                {
                    return;
                }

                (value, at) = (_latest, _latestAt);
                (status, _status) = (_status, null);
                _lastApplied = DateTime.UtcNow;
            }

            if (value is { } v && task.State == TransferState.Running)
            {
                owner._queue.ReportProgress(task, v.Transferred, v.Total, status, at);
            }
        }

        /// <summary>Applies the last value synchronously (called on the UI thread before completing).</summary>
        public void Flush()
        {
            TransferProgress? value;
            DateTimeOffset at;
            lock (_gate)
            {
                (value, at) = (_latest, _latestAt);
                _status = null; // the outcome message replaces any note
            }

            if (value is { } v)
            {
                owner._queue.ReportProgress(task, v.Transferred, v.Total, at: at);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _stopped = true;
            }
        }
    }
}
