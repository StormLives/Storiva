using System.Globalization;
using Storiva.Core.Listing;

namespace Storiva.Core.Tasks;

/// <summary>
/// Smoothed speed of a running transfer, computed only from the progress it really reports. Samples are grouped in
/// windows of at least <see cref="Window"/>; each window's speed is blended into the previous estimate
/// (exponential moving average), so the speed and the time left do not jump with every network burst.
/// </summary>
public sealed class TransferRate
{
    /// <summary>Minimum time between two speed updates. No speed is shown before the first full window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>Weight of the newest window (0–1): lower is smoother, higher follows changes faster.</summary>
    private const double Smoothing = 0.3;

    private DateTimeOffset? _windowStart;
    private long _windowBytes;

    /// <summary>Bytes per second, or null while there is not enough data (or after a restart).</summary>
    public double? BytesPerSecond { get; private set; }

    /// <summary>Forgets every sample (the transfer starts again, or a retry begins).</summary>
    public void Reset()
    {
        _windowStart = null;
        _windowBytes = 0;
        BytesPerSecond = null;
    }

    public void Sample(long processed, DateTimeOffset at)
    {
        if (_windowStart is not { } start || processed < _windowBytes || at < start)
        {
            // First sample, or the transfer went back (a retried file starts over): measure again from here.
            if (_windowStart is not null)
            {
                BytesPerSecond = null;
            }

            _windowStart = at;
            _windowBytes = processed;
            return;
        }

        var elapsed = at - start;
        if (elapsed < Window)
        {
            return;
        }

        var rate = (processed - _windowBytes) / elapsed.TotalSeconds;
        BytesPerSecond = BytesPerSecond is { } previous ? previous + Smoothing * (rate - previous) : rate;
        _windowStart = at;
        _windowBytes = processed;
    }

    /// <summary>Time left at the current speed; null when the total or the speed is unknown (or the speed is zero).</summary>
    public TimeSpan? Remaining(long processed, long? total)
    {
        if (BytesPerSecond is not > 0 || total is not > 0 || processed > total)
        {
            return null;
        }

        var seconds = (total.Value - processed) / BytesPerSecond.Value;
        return seconds < TimeSpan.MaxValue.TotalSeconds / 2 ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>"4.2 MB/s".</summary>
    public static string FormatSpeed(double bytesPerSecond) => FileTypes.FormatSize((long)Math.Max(0, bytesPerSecond)) + "/s";

    /// <summary>"12 s", "3 min 20 s", "1 h 5 min"; durations are rounded up so a running transfer never shows "0 s".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var seconds = (long)Math.Max(1, Math.Ceiling(duration.TotalSeconds));
        if (seconds < 60)
        {
            return seconds.ToString(CultureInfo.InvariantCulture) + " s";
        }

        if (seconds < 3600)
        {
            var s = seconds % 60;
            return s == 0 || seconds >= 600
                ? $"{Math.Min(59, (seconds + 30) / 60)} min"
                : $"{seconds / 60} min {s} s";
        }

        if (seconds >= 100 * 3600)
        {
            return "more than 4 days";
        }

        var minutes = seconds / 60 % 60;
        return minutes == 0 ? $"{seconds / 3600} h" : $"{seconds / 3600} h {minutes} min";
    }

    /// <summary>"4.2 MB/s · 12 s left" (or just the speed when the total is unknown); null without a speed yet.</summary>
    public string? Describe(long processed, long? total)
    {
        if (BytesPerSecond is not { } speed)
        {
            return null;
        }

        var text = FormatSpeed(speed);
        if (Remaining(processed, total) is { } left)
        {
            text += " · " + FormatDuration(left) + " left";
        }
        else if (speed <= 0 && total is > 0 && processed < total)
        {
            text += " · stalled";
        }

        return text;
    }
}
