using Storiva.Core.Storage;

namespace Storiva.Core.Navigation;

public sealed record BreadcrumbSegment(string Label, StoragePath Path);

/// <summary>
/// Current location plus back/forward history, in the style of a file browser.
/// Navigation never touches data; it only validates that the target exists.
/// </summary>
public sealed class BrowserNavigator
{
    public const string HomeLabel = "Home";

    private readonly IStorageCatalog _catalog;
    private readonly Stack<StoragePath> _back = new();
    private readonly Stack<StoragePath> _forward = new();

    public BrowserNavigator(IStorageCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public event EventHandler? Navigated;

    public StoragePath Current { get; private set; } = StoragePath.Root;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public bool CanGoUp => !Current.IsRoot;

    public IReadOnlyList<BreadcrumbSegment> Breadcrumb => BuildBreadcrumb(Current);

    /// <summary>Moves to <paramref name="target"/>. Returns false when it does not exist or is already current.</summary>
    public bool NavigateTo(StoragePath target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target == Current || !_catalog.Exists(target))
        {
            return false;
        }

        _back.Push(Current);
        _forward.Clear();
        SetCurrent(target);
        return true;
    }

    public bool GoBack() => Step(_back, _forward);

    public bool GoForward() => Step(_forward, _back);

    /// <summary>Goes to the parent folder; recorded in the back history like any navigation.</summary>
    public bool GoUp() => Current.Parent is { } parent && NavigateTo(parent);

    /// <summary>Clears history and returns to the root (used after the data set is reset).</summary>
    public void Reset()
    {
        _back.Clear();
        _forward.Clear();
        SetCurrent(StoragePath.Root);
    }

    public static IReadOnlyList<BreadcrumbSegment> BuildBreadcrumb(StoragePath path)
    {
        var segments = new List<BreadcrumbSegment> { new(HomeLabel, StoragePath.Root) };
        var walk = StoragePath.Root;
        foreach (var name in path.Segments)
        {
            walk = walk.Append(name);
            segments.Add(new BreadcrumbSegment(name, walk));
        }

        return segments;
    }

    private bool Step(Stack<StoragePath> from, Stack<StoragePath> to)
    {
        // Entries may point to folders that no longer exist (e.g. after a reset); skip them.
        while (from.Count > 0)
        {
            var target = from.Pop();
            if (_catalog.Exists(target) && target != Current)
            {
                to.Push(Current);
                SetCurrent(target);
                return true;
            }
        }

        return false;
    }

    private void SetCurrent(StoragePath path)
    {
        Current = path;
        Navigated?.Invoke(this, EventArgs.Empty);
    }
}
