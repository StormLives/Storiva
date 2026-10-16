namespace Storiva.Core.Tasks;

public enum TaskFilter
{
    All,
    Running,
    Queued,
    Canceled,
    Failed,
    Completed,
}

/// <summary>
/// Operations shown in the Tasks tab. States change only from what the operation reports (see
/// <see cref="OperationRunner"/>): Queued → Running → Completed / Failed / Canceled. Cancel requests cancellation;
/// the task becomes Canceled when the operation actually stops. Retry runs a failed or canceled operation again.
/// </summary>
public sealed class TaskQueue
{
    private readonly List<TransferTask> _tasks = [];
    private readonly Dictionary<int, Action> _cancelHandlers = new();
    private readonly Dictionary<int, Action> _retryHandlers = new();
    private int _maxConcurrent;

    public TaskQueue(int maxConcurrent = 2) => MaxConcurrent = maxConcurrent;

    public event EventHandler? Changed;

    /// <summary>Raised when the simultaneous-task limit changes (lets the operation runner start waiting tasks).</summary>
    public event EventHandler? CapacityChanged;

    /// <summary>Simultaneous running operations (Tools › Options › Queueing). Lowering it never stops running
    /// operations; it only limits how many queued ones start next.</summary>
    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            var changed = _maxConcurrent != value;
            _maxConcurrent = value;
            if (changed)
            {
                CapacityChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public IReadOnlyList<TransferTask> Tasks => _tasks;

    public bool HasActiveWork => _tasks.Any(t => t.State is TransferState.Running or TransferState.Queued);

    public IReadOnlyList<TransferTask> Filter(TaskFilter filter) => _tasks.Where(t => Matches(t, filter)).ToList();

    public int Count(TaskFilter filter) => _tasks.Count(t => Matches(t, filter));

    public static bool Matches(TransferTask task, TaskFilter filter) => filter switch
    {
        TaskFilter.Running => task.State == TransferState.Running,
        TaskFilter.Queued => task.State == TransferState.Queued,
        TaskFilter.Canceled => task.State == TransferState.Canceled,
        TaskFilter.Failed => task.State == TransferState.Failed,
        TaskFilter.Completed => task.State == TransferState.Completed,
        _ => true,
    };

    public static bool CanCancel(TransferTask t) => !t.CancelRequested && t.State is TransferState.Queued or TransferState.Running;

    public static bool CanRetry(TransferTask t) => t.Retryable && t.State is TransferState.Failed or TransferState.Canceled;

    /// <summary>Adds a task (Queued). Its state then follows the operation.</summary>
    public TransferTask AddOperation(string name, TransferKind kind, long? size, DateTimeOffset createdAt, Action cancel, Action? retry)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        var task = new TransferTask(_tasks.Count == 0 ? 1 : _tasks.Max(t => t.Id) + 1, name, kind, createdAt, size) { Retryable = retry is not null };
        _tasks.Add(task);
        _cancelHandlers[task.Id] = cancel;
        if (retry is not null)
        {
            _retryHandlers[task.Id] = retry;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return task;
    }

    public bool Cancel(int id) => Apply(id, CanCancel, CancelCore);

    public bool Retry(int id) => Apply(id, CanRetry, t =>
    {
        if (_retryHandlers.TryGetValue(t.Id, out var retry))
        {
            retry();
        }
    });

    public int CancelAll()
    {
        var targets = _tasks.Where(CanCancel).ToList();
        targets.ForEach(CancelCore);
        if (targets.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return targets.Count;
    }

    /// <summary>Removes finished tasks (Completed, Canceled, Failed) from the list.</summary>
    public int ClearFinished()
    {
        var removed = _tasks.RemoveAll(t => t.State is TransferState.Completed or TransferState.Canceled or TransferState.Failed);
        if (removed > 0)
        {
            var ids = _tasks.Select(t => t.Id).ToHashSet();
            foreach (var id in _cancelHandlers.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                _cancelHandlers.Remove(id);
            }

            foreach (var id in _retryHandlers.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                _retryHandlers.Remove(id);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    // ---------- state reported by the operation ----------

    internal int RunningOperations => _tasks.Count(t => t.State == TransferState.Running);

    internal void MarkRunning(TransferTask task, string message, DateTimeOffset? at = null)
    {
        task.CancelRequested = false;
        task.Progress = 0;
        task.Processed = 0;
        task.StartedAt = at;
        task.Rate.Reset();
        if (at is { } start && TransferTask.MeasuresBytes(task.Kind))
        {
            task.Rate.Sample(0, start); // the speed is measured from the start, connection setup included
        }

        Set(task, TransferState.Running, message);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void MarkQueued(TransferTask task, string message)
    {
        task.CancelRequested = false;
        task.Progress = 0;
        task.Processed = 0;
        task.Rate.Reset();
        Set(task, TransferState.Queued, message);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Progress from the operation: a percentage only when the total is known, never invented. With
    /// <paramref name="at"/>, uploads and downloads also update their smoothed speed; a new
    /// <paramref name="message"/> (e.g. a retry notice) starts a new speed measurement.
    /// </summary>
    internal void ReportProgress(TransferTask task, long processed, long? total, string? message = null, DateTimeOffset? at = null)
    {
        if (message is not null)
        {
            task.Rate.Reset();
        }

        if (at is { } now && TransferTask.MeasuresBytes(task.Kind))
        {
            task.Rate.Sample(processed, now);
        }

        task.Processed = processed;
        if (total is > 0)
        {
            task.Size ??= total;
            task.IsIndeterminate = false;
            task.Progress = (int)Math.Clamp(processed * 100 / total.Value, 0, 100);
        }
        else
        {
            task.IsIndeterminate = true;
        }

        if (message is not null)
        {
            task.Message = message;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Complete(TransferTask task, string message)
    {
        if (!task.IsIndeterminate)
        {
            task.Progress = 100;
        }

        Finish(task, TransferState.Completed, message);
    }

    internal void Fail(TransferTask task, string message) => Finish(task, TransferState.Failed, message);

    internal void MarkCanceled(TransferTask task, string message) => Finish(task, TransferState.Canceled, message);

    private void Finish(TransferTask task, TransferState state, string message)
    {
        task.CancelRequested = false;
        Set(task, state, message);
        if (!task.Retryable)
        {
            _cancelHandlers.Remove(task.Id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CancelCore(TransferTask task)
    {
        task.CancelRequested = true;
        task.Message = "Canceling…";
        if (_cancelHandlers.TryGetValue(task.Id, out var cancel))
        {
            cancel();
        }
    }

    private static void Set(TransferTask task, TransferState state, string message)
    {
        task.State = state;
        task.Message = message;
    }

    private bool Apply(int id, Func<TransferTask, bool> allowed, Action<TransferTask> action)
    {
        var task = _tasks.FirstOrDefault(t => t.Id == id);
        if (task is null || !allowed(task))
        {
            return false;
        }

        action(task);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
