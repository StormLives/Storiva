using Storiva.Core.Tasks;

namespace Storiva.Core.Remote;

/// <summary>
/// Retries one whole object transfer (an upload or a download) after a transient failure: the endpoint was not
/// reachable or the connection dropped, the server was busy (throttling, 503) or reported a 5xx error. The AWS SDK
/// already retries each HTTP request a few times with a short backoff (Standard mode, see S3ClientFactory); this
/// layer covers what it cannot: a download interrupted while the body streams, and outages longer than the SDK's
/// backoff. Credentials, permission, validation, not-found and local file errors are never retried, nor is a
/// cancellation; the wait between attempts stops at once when the operation is canceled.
/// </summary>
/// <remarks>
/// Use one instance per operation. Once an object has used all its attempts, the next objects of the same operation
/// fail without waiting until one succeeds again, so an outage does not make a large folder wait for every file.
/// </remarks>
public sealed class TransferRetry
{
    /// <summary>Waits before the 2nd and the 3rd attempt.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];

    private readonly IReadOnlyList<TimeSpan> _delays;
    private bool _exhausted;

    /// <param name="delays">Wait before each extra attempt (its count is the number of retries); empty = never retry.</param>
    public TransferRetry(IReadOnlyList<TimeSpan>? delays = null)
    {
        _delays = delays ?? DefaultDelays;
        if (_delays.Any(d => d < TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(delays));
        }
    }

    public int MaxAttempts => _delays.Count + 1;

    /// <summary>Failures worth another attempt: the request may succeed later without changing anything.</summary>
    public static bool IsTransient(StorageError error) =>
        error.Kind is StorageErrorKind.EndpointUnreachable or StorageErrorKind.Throttled or StorageErrorKind.ServerError;

    /// <summary>
    /// Runs <paramref name="attempt"/> until it succeeds, fails for a non-transient reason or has used every attempt.
    /// Each attempt must start the transfer over (a download rewrites its file from the start). Retry notices are
    /// reported through <paramref name="progress"/> as <see cref="TransferProgress.Status"/>.
    /// </summary>
    public async Task RunAsync(Func<IProgress<TransferProgress>?, Task> attempt, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var tracker = progress is null ? null : new Tracker(progress);
        for (var number = 1; ; number++)
        {
            try
            {
                await attempt(tracker).ConfigureAwait(true);
                _exhausted = false;
                if (number > 1)
                {
                    tracker?.Note(TransferTask.InProgressMessage);
                }

                return;
            }
            catch (StorageOperationException ex) when (IsTransient(ex.Error) && !cancellationToken.IsCancellationRequested)
            {
                if (_exhausted || number > _delays.Count)
                {
                    _exhausted = true;
                    if (number == 1)
                    {
                        throw;
                    }

                    tracker?.Note(TransferTask.InProgressMessage);
                    throw new StorageOperationException(ex.Error with { Message = $"{ex.Error.Message} (tried {number} times)" }, ex);
                }

                var delay = _delays[number - 1];
                var reason = StorageError.Title(ex.Kind) + (ex.Error.StatusCode is { } status ? $" (HTTP {status})" : string.Empty);
                tracker?.Note(delay >= TimeSpan.FromSeconds(1)
                    ? $"{reason}: retrying {number + 1}/{MaxAttempts} in {TransferRate.FormatDuration(delay)}…"
                    : $"{reason}: retrying {number + 1}/{MaxAttempts}…");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
                tracker?.Note($"Retrying {number + 1}/{MaxAttempts}…");
            }
        }
    }

    /// <summary>Passes progress through and remembers the last value, so a notice keeps the current byte count.</summary>
    private sealed class Tracker(IProgress<TransferProgress> inner) : IProgress<TransferProgress>
    {
        private TransferProgress _last;

        public void Report(TransferProgress value)
        {
            _last = value;
            inner.Report(value);
        }

        public void Note(string status) => inner.Report(_last with { Status = status });
    }
}
