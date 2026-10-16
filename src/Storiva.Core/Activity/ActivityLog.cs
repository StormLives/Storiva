using System.Globalization;

namespace Storiva.Core.Activity;

public enum ActivityLevel
{
    Information,
    Warning,
    Error,
}

/// <param name="TaskId">Id of the task the entry belongs to, if any.</param>
public sealed record ActivityEntry(DateTimeOffset Time, ActivityLevel Level, string Message, int? TaskId = null)
{
    /// <summary>Level letter shown in the Event log margin, like the reference ("I", "W") [F].</summary>
    public string LevelLetter => Level switch
    {
        ActivityLevel.Warning => "W",
        ActivityLevel.Error => "E",
        _ => "I",
    };

    /// <summary>Reference line format [F E5/E6]: "[yyyy-MM-dd HH:mm:ss] message TaskID: n".</summary>
    public string Format() =>
        $"[{Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] {Message}" +
        (TaskId is { } id ? $" TaskID: {id.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
}

/// <summary>In-memory log of actions and operations shown in the "Event log" tab. Capped; <see cref="Entries"/> is newest first.</summary>
public sealed class ActivityLog
{
    private readonly LinkedList<ActivityEntry> _entries = new();
    private readonly Func<DateTimeOffset> _clock;

    public ActivityLog(int capacity = 500, Func<DateTimeOffset>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public event EventHandler<ActivityEntry>? Added;

    public int Capacity { get; }

    /// <summary>Entries below this level are dropped (Tools › Options › Logging).</summary>
    public ActivityLevel MinimumLevel { get; set; } = ActivityLevel.Information;

    public IReadOnlyCollection<ActivityEntry> Entries => _entries;

    /// <summary>Records an entry. Returns null when it was below <see cref="MinimumLevel"/>.</summary>
    public ActivityEntry? Write(string message, ActivityLevel level = ActivityLevel.Information, int? taskId = null)
    {
        if (level < MinimumLevel)
        {
            return null;
        }

        var entry = new ActivityEntry(_clock(), level, message, taskId);
        _entries.AddFirst(entry);
        while (_entries.Count > Capacity)
        {
            _entries.RemoveLast();
        }

        Added?.Invoke(this, entry);
        return entry;
    }
}
