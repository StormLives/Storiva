using Storiva.Core.Settings;

namespace Storiva.App.Main;

/// <summary>
/// Window layout remembered in settings.json: size, position (only when still on a screen), maximized state, pane
/// sizes and the selected lower tab, plus the connection used last. That connection is only highlighted in the
/// "Not connected" panel: Storiva always starts disconnected and connects only when the user asks.
/// </summary>
internal sealed partial class MainForm
{
    private const string LastUsedSuffix = " — last used";

    private readonly SettingsSession _settings;

    private Guid? LastConnectionId => _settings.Current.LastConnectionId;

    /// <summary>Runs in OnLoad: after DPI autoscaling (the values are in screen pixels) and before the window is shown.</summary>
    private void RestoreWindowBounds()
    {
        if (_settings.Current.Window is not { } layout)
        {
            return; // first start: centered, default size
        }

        var areas = Screen.AllScreens.Select(s => s.WorkingArea).ToList();
        StartPosition = FormStartPosition.Manual;
        if (WindowLayoutRules.VisibleBounds(layout, areas, MinimumSize) is { } bounds)
        {
            Bounds = bounds;
        }
        else
        {
            // The screen it was on is gone (monitor removed, resolution changed): same size, centered on the main screen.
            var size = WindowLayoutRules.SafeSize(layout, areas, MinimumSize);
            var area = Screen.PrimaryScreen?.WorkingArea ?? areas.FirstOrDefault();
            Bounds = new Rectangle(area.X + Math.Max(0, (area.Width - size.Width) / 2), area.Y + Math.Max(0, (area.Height - size.Height) / 2), size.Width, size.Height);
        }

        if (layout.Maximized)
        {
            WindowState = FormWindowState.Maximized; // applied when the window is shown, on the screen of the bounds above
        }
    }

    private void RestoreBottomTab()
    {
        if (_settings.Current.Window?.BottomTab is { } tab)
        {
            SelectDetailsTab(tab); // unknown names (other versions) are ignored
        }
    }

    /// <summary>The current layout; the normal bounds are kept while the window is maximized or minimized.</summary>
    internal WindowLayout CaptureWindowLayout()
    {
        var normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var previous = _settings.Current.Window;
        int Logical(int devicePixels) => (int)Math.Round(devicePixels * 96.0 / DeviceDpi);
        return new WindowLayout
        {
            X = normal.X,
            Y = normal.Y,
            Width = normal.Width,
            Height = normal.Height,
            Maximized = WindowState == FormWindowState.Maximized,
            // A hidden pane keeps the size it had when it was last visible.
            NavigationPaneWidth = _innerSplit.Panel1Collapsed ? previous?.NavigationPaneWidth : Logical(_innerSplit.SplitterDistance),
            TaskPanelHeight = _outerSplit.Panel2Collapsed ? previous?.TaskPanelHeight : Logical(_outerSplit.Height - _outerSplit.SplitterDistance - _outerSplit.SplitterWidth),
            BottomTab = _bottomTabs.SelectedTab?.Name,
        };
    }

    /// <summary>Writes the layout to settings.json. Never fails the exit: a read-only or unwritable file is left as it is.</summary>
    internal SettingsSaveOutcome SaveWindowLayout()
    {
        if (!_layoutInitialized)
        {
            return SettingsSaveOutcome.ReadOnly; // closed before it was shown: nothing worth remembering
        }

        var layout = CaptureWindowLayout();
        return _settings.Update(s => s with { Window = layout }, out _);
    }

    /// <summary>Remembers the connection for the "Not connected" panel (its id only; nothing secret).</summary>
    private void RememberLastConnection(Guid id)
    {
        if (LastConnectionId != id)
        {
            _settings.Update(s => s with { LastConnectionId = id }, out _);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SaveWindowLayout();
        base.OnFormClosed(e);
    }

    // ---------- smoke test ----------

    /// <summary>Bucket pane width and lower panel height in device pixels.</summary>
    internal (int NavigationWidth, int TaskPanelHeight) PaneSizes =>
        (_innerSplit.SplitterDistance, _outerSplit.Height - _outerSplit.SplitterDistance - _outerSplit.SplitterWidth);
}
