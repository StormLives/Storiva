using System.Drawing;

namespace Storiva.Core.Settings;

/// <summary>
/// Main window geometry remembered between sessions. Bounds are the normal (not maximized) window rectangle in
/// screen pixels; pane sizes are logical pixels (96 DPI), so they fit again on a monitor with another scale.
/// </summary>
public sealed record WindowLayout
{
    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public bool Maximized { get; init; }

    /// <summary>Width of the bucket pane, or null for the default.</summary>
    public int? NavigationPaneWidth { get; init; }

    /// <summary>Height of the lower panel (tabs and task footer), or null for the default.</summary>
    public int? TaskPanelHeight { get; init; }

    /// <summary>Name of the selected lower tab (Tasks, Permissions…), or null.</summary>
    public string? BottomTab { get; init; }

    public Rectangle Bounds => new(X, Y, Width, Height);
}

/// <summary>Checks a remembered layout against the screens Windows reports now (monitors may have been removed).</summary>
public static class WindowLayoutRules
{
    /// <summary>Larger values come from a damaged or hand-edited file and are ignored.</summary>
    public const int MaxDimension = 32000;

    /// <summary>Part of the title bar that must be on a screen so the window can still be grabbed and moved.</summary>
    private const int MinVisibleWidth = 120;
    private const int MinVisibleHeight = 24;

    /// <summary>True when the stored values are plausible (positive sizes, sane pane sizes); otherwise defaults are used.</summary>
    public static bool IsPlausible(WindowLayout layout) =>
        layout.Width > 0 && layout.Height > 0 && layout.Width <= MaxDimension && layout.Height <= MaxDimension
        && Math.Abs((long)layout.X) <= MaxDimension && Math.Abs((long)layout.Y) <= MaxDimension
        && layout.NavigationPaneWidth is null or (>= 0 and <= MaxDimension)
        && layout.TaskPanelHeight is null or (>= 0 and <= MaxDimension)
        && (layout.BottomTab is null || layout.BottomTab.Length <= 64);

    /// <summary>
    /// Window size to use: the remembered one, at least <paramref name="minimum"/> and at most the largest working
    /// area, so the window never opens bigger than any screen.
    /// </summary>
    public static Size SafeSize(WindowLayout layout, IReadOnlyList<Rectangle> workingAreas, Size minimum)
    {
        var maxWidth = workingAreas.Count == 0 ? layout.Width : workingAreas.Max(a => a.Width);
        var maxHeight = workingAreas.Count == 0 ? layout.Height : workingAreas.Max(a => a.Height);
        return new Size(
            Math.Max(minimum.Width, Math.Min(layout.Width, maxWidth)),
            Math.Max(minimum.Height, Math.Min(layout.Height, maxHeight)));
    }

    /// <summary>
    /// The remembered rectangle (with <see cref="SafeSize"/>) when its top edge is still on one of the screens, or
    /// null when it is not — for example after a second monitor was disconnected. The caller then centers the window.
    /// </summary>
    public static Rectangle? VisibleBounds(WindowLayout layout, IReadOnlyList<Rectangle> workingAreas, Size minimum)
    {
        var size = SafeSize(layout, workingAreas, minimum);
        var bounds = new Rectangle(layout.X, layout.Y, size.Width, size.Height);
        var titleBar = new Rectangle(bounds.X, bounds.Y, bounds.Width, MinVisibleHeight);
        foreach (var area in workingAreas)
        {
            var visible = Rectangle.Intersect(area, titleBar);
            if (visible.Width >= Math.Min(MinVisibleWidth, bounds.Width) && visible.Height >= MinVisibleHeight)
            {
                return bounds;
            }
        }

        return null;
    }
}
