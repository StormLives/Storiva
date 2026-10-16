namespace Storiva.Core.Tasks;

public enum TransferKind
{
    Upload,
    Download,
    Copy,
    Move,
    Delete,
}

public enum TransferState
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
}

/// <summary>An operation in the Tasks tab. Its state follows what the operation reports (see <see cref="OperationRunner"/>).</summary>
public sealed class TransferTask
{
    public TransferTask(int id, string name, TransferKind kind, DateTimeOffset createdAt, long? size = null)
    {
        if (size is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name;
        Kind = kind;
        CreatedAt = createdAt;
        Size = size;
        IsIndeterminate = size is null;
        Message = "Waiting for a free slot";
    }

    /// <summary>Total bytes, when known.</summary>
    public long? Size { get; internal set; }

    public int Id { get; }

    public string Name { get; }

    public TransferKind Kind { get; }

    public DateTimeOffset CreatedAt { get; }

    public TransferState State { get; internal set; } = TransferState.Queued;

    /// <summary>0–100. Meaningful only when <see cref="IsIndeterminate"/> is false.</summary>
    public int Progress { get; internal set; }

    /// <summary>No known total (e.g. deleting a folder of unknown size): no percentage is shown.</summary>
    public bool IsIndeterminate { get; internal set; }

    /// <summary>Bytes (transfers) or objects (bulk operations) processed so far.</summary>
    public long Processed { get; internal set; }

    public string Message { get; internal set; }

    /// <summary>True while a cancellation has been requested but the operation has not stopped yet.</summary>
    public bool CancelRequested { get; internal set; }

    /// <summary>The operation can be run again (Retry) after a failure or a cancellation.</summary>
    public bool Retryable { get; internal set; }

    public bool IsFinished => State is TransferState.Completed or TransferState.Canceled;

    /// <summary>When the current (or last) run started; null while it never ran.</summary>
    public DateTimeOffset? StartedAt { get; internal set; }

    /// <summary>Smoothed speed of a running upload or download (bytes per second), from real progress only.</summary>
    public double? BytesPerSecond => Rate.BytesPerSecond;

    /// <summary>Time left at the current speed, when the total is known.</summary>
    public TimeSpan? TimeRemaining => Rate.Remaining(Processed, Size);

    /// <summary>
    /// What the Tasks tab shows in the Message column: while an upload or download runs, its speed and time left
    /// ("4.2 MB/s · 12 s left") followed by any note other than the plain "In progress" (e.g. a retry notice).
    /// </summary>
    public string DisplayMessage
    {
        get
        {
            if (State != TransferState.Running || CancelRequested || Rate.Describe(Processed, Size) is not { } live)
            {
                return Message;
            }

            return Message == InProgressMessage ? live : $"{live} · {Message}";
        }
    }

    /// <summary>The message of a running operation that has nothing else to say.</summary>
    public const string InProgressMessage = "In progress";

    internal TransferRate Rate { get; } = new();

    internal static bool MeasuresBytes(TransferKind kind) => kind is TransferKind.Upload or TransferKind.Download;
}
