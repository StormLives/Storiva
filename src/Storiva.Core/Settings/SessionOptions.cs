using Storiva.Core.Activity;

namespace Storiva.Core.Settings;

/// <summary>
/// Settings edited in Tools › Options for the current session. The eight page names come from the reference option
/// icons [F, D1]; the settings themselves are Storiva's [S]. The theme is persisted separately (<see cref="AppSettings"/>).
/// </summary>
public sealed record SessionOptions
{
    public static readonly string[] PageNames =
        ["General", "Interface", "Proxy", "Bandwidth throttling", "Queueing", "Logging", "Data integrity", "Advanced"];

    public const int MinConcurrentTasks = 1;
    public const int MaxConcurrentTasksLimit = 8;

    // General
    public bool OpenFilesInPreview { get; init; } = true;

    // Interface
    public bool ShowStatusBar { get; init; } = true;
    public bool ShowBucketPane { get; init; } = true;
    public bool CompactRows { get; init; } = true;

    /// <summary>
    /// False (default): the bucket pane lists only buckets and folders are browsed in the object list, so a bucket
    /// with many folders never floods the pane. True: folders can also be expanded in the tree.
    /// </summary>
    public bool ShowFoldersInTree { get; init; }

    // Queueing
    public int MaxConcurrentTasks { get; init; } = 2;

    // Logging
    public ActivityLevel MinimumLogLevel { get; init; } = ActivityLevel.Information;

    public static SessionOptions Default { get; } = new();

    /// <summary>Null when valid, otherwise a user-facing message.</summary>
    public string? Validate() =>
        MaxConcurrentTasks is < MinConcurrentTasks or > MaxConcurrentTasksLimit
            ? $"Simultaneous tasks must be between {MinConcurrentTasks} and {MaxConcurrentTasksLimit}."
            : null;
}
