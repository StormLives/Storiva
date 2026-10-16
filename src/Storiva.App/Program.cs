using Storiva.App.Main;
using Storiva.App.Theme;
using Storiva.Core.Activity;
using Storiva.Core.Connections;
using Storiva.Core.Settings;
using Storiva.Core.Tasks;
using Storiva.Infrastructure.Persistence;
using Storiva.Infrastructure.S3;
using Storiva.Infrastructure.Security;

namespace Storiva.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorReporter.Show(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorReporter.Show(e.ExceptionObject as Exception, e.IsTerminating);

        // High DPI mode (PerMonitorV2), visual styles and default font come from the .csproj.
        ApplicationConfiguration.Initialize();

        // Services created before the first control (ThemeService) capture this context, so Windows theme
        // notifications, raised on a system thread, are handed back to the UI thread.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        // "--smoke-test <dir>": drive the real window, write screenshots + report, exit with 0/1. It uses its own
        // data folder inside <dir> and a fixed Windows theme, so the user's settings and connections are never touched.
        // "--screenshot-tour <dir>": same isolation, saves the README screenshots (tools\capture-readme-screenshots.ps1).
        var smokeIndex = Array.FindIndex(args, a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        var tourIndex = Array.FindIndex(args, a => string.Equals(a, "--screenshot-tour", StringComparison.OrdinalIgnoreCase));
        if (tourIndex >= 0)
        {
            smokeIndex = tourIndex;
        }

        if (smokeIndex >= 0)
        {
            var outputDir = Path.GetFullPath(smokeIndex + 1 < args.Length ? args[smokeIndex + 1] : Path.Combine(AppContext.BaseDirectory, "smoke-test"));
            var dataDir = Path.Combine(outputDir, "data");
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }

            var themeSource = new ManualThemeSource(prefersDark: false);
            var paths = new StorivaDataPaths(dataDir);
            var smokeForm = CreateMainForm(paths, themeSource, out var connections);
            var environment = new SmokeTest.Environment(paths, themeSource, connections);
            return tourIndex >= 0
                ? ScreenshotTour.Run(smokeForm, outputDir, environment)
                : SmokeTest.Run(smokeForm, outputDir, environment);
        }

        var form = CreateMainForm(new StorivaDataPaths(), new WindowsThemeSource(), out _);
        Application.Run(form);
        return 0;
    }

    /// <summary>
    /// Composition root: persisted settings, connections and DPAPI-protected secrets under <paramref name="paths"/>.
    /// No connection is opened here: only an explicit user action connects.
    /// </summary>
    internal static MainForm CreateMainForm(StorivaDataPaths paths, ISystemThemeSource themeSource, out ConnectionManager connections)
    {
        var theme = new ThemeService(new JsonSettingsStore(paths), themeSource);
        connections = new ConnectionManager(new JsonConnectionStore(paths), new DpapiSecretStore(paths), new S3ClientFactory());
        return new MainForm(new TaskQueue(), connections, new ActivityLog(), theme, S3ClientFactory.ListAwsProfileNames);
    }
}
