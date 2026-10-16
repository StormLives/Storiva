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
}
