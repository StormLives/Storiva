using System.Text;
using Storiva.App.Commands;
using Storiva.App.Main;
using Storiva.App.Theme;
using Storiva.Core.Settings;
using Storiva.Infrastructure.Persistence;

namespace Storiva.App;

/// <summary>
/// [window layout]: the layout this window writes to its settings.json, and two more windows started from a
/// settings.json that remembers a layout (each with its own data folder, so the window under test keeps its fixed size).
/// </summary>
internal static partial class SmokeTest
{
    private static void WindowLayoutSection(MainForm form, Environment env, string outputDir, Action<string, bool, string> check, StringBuilder report)
    {
        void Check(string name, bool ok, string detail = "") => check(name, ok, detail);
        report.AppendLine("[window layout]");

        var outcome = form.SaveWindowLayout();
        var json = File.ReadAllText(env.Paths.SettingsFile);
        var saved = new JsonSettingsStore(env.Paths).Load().Value;
        Check("the window layout is saved in settings.json next to the theme preference",
            outcome == SettingsSaveOutcome.Saved && saved.Window is { } w && w.Width == form.Width && w.Height == form.Height && w.BottomTab == form.SelectedBottomTab
            && json.Contains("\"theme\"", StringComparison.Ordinal), $"{outcome} {saved.Window}");
        Check("…without any credential field", !json.Contains("secret", StringComparison.OrdinalIgnoreCase) && !json.Contains("accessKey", StringComparison.OrdinalIgnoreCase));

        var primary = Screen.PrimaryScreen!.WorkingArea;
        var size = new Size(Math.Min(primary.Width - 80, form.MinimumSize.Width + 180), Math.Min(primary.Height - 60, form.MinimumSize.Height + 160));
        var remembered = new WindowLayout
        {
            X = primary.X + 40,
            Y = primary.Y + 30,
            Width = size.Width,
            Height = size.Height,
            NavigationPaneWidth = 300,
            TaskPanelHeight = 200,
            BottomTab = "Event log",
        };

        using (var second = OpenWithLayout(Path.Combine(outputDir, "data-layout"), remembered))
        {
            Check("restart: size and position come back from settings.json", second.Bounds == remembered.Bounds && second.WindowState == FormWindowState.Normal,
                $"{second.Bounds} (saved {remembered.Bounds})");
            var panes = second.PaneSizes;
            Check("restart: bucket pane width and task panel height come back",
                Math.Abs(panes.NavigationWidth - second.LogicalToDeviceUnits(300)) <= 1 && Math.Abs(panes.TaskPanelHeight - second.LogicalToDeviceUnits(200)) <= 1,
                $"{panes.NavigationWidth} x {panes.TaskPanelHeight} px");
            Check("restart: the last selected lower tab is selected again", second.SelectedBottomTab == "Event log", second.SelectedBottomTab);
            Check("restart: a remembered connection is never opened on its own", !second.IsConnected && second.EmptyState.Shown && second.ConnectionLabel == "Not connected");
            second.Close();
        }

        // The monitor it was on is gone: centered on the main screen with the same size, still maximized.
        var screens = Screen.AllScreens.Aggregate(Rectangle.Empty, (all, s) => Rectangle.Union(all, s.Bounds));
        using (var third = OpenWithLayout(Path.Combine(outputDir, "data-layout-lost"), remembered with { X = screens.Right + 400, Y = screens.Top + 100, Maximized = true }))
        {
            var normal = third.RestoreBounds;
            Check("restart on a removed monitor: the window opens on a visible screen, same size, maximized again",
                third.WindowState == FormWindowState.Maximized && normal.Size == size && primary.Contains(normal),
                $"{third.WindowState}, normal bounds {normal}");
            third.Close();
        }

        form.Activate();
    }

    /// <summary>A second main window whose (fresh) data folder holds a settings.json with <paramref name="layout"/>.</summary>
    private static MainForm OpenWithLayout(string dataDir, WindowLayout layout)
    {
        if (Directory.Exists(dataDir))
        {
            Directory.Delete(dataDir, recursive: true);
        }

        var paths = new StorivaDataPaths(dataDir);
        new JsonSettingsStore(paths).Save(new AppSettings { Window = layout, LastConnectionId = Guid.NewGuid() });
        var window = Program.CreateMainForm(paths, new ManualThemeSource(prefersDark: false), out _);
        window.Prompts = new RecordingPrompts();
        window.Show();
        Application.DoEvents();
        return window;
    }
}
