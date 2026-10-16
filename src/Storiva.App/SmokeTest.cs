using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using Storiva.App.Commands;
using Storiva.App.Main;
using Storiva.App.Theme;
using Storiva.Core.Commands;
using Storiva.Core.Connections;
using Storiva.Core.Settings;
using Storiva.Infrastructure.Persistence;

namespace Storiva.App;

/// <summary>
/// Drives the real main window through its UI entry points and records what it observes.
/// Started with "Storiva.exe --smoke-test &lt;dir&gt;". Writes report.txt and PNG captures to the folder.
/// Confirmations and notices are answered by <see cref="RecordingPrompts"/> (no modal boxes).
/// <para>Without a connection it checks the layout, the disabled commands, the dialogs and the themes. The
/// [remote] section (everything that needs data) runs only when STORIVA_SMOKE_S3_ENDPOINT, _KEY and _SECRET are set
/// — tools\validate.ps1 starts the loopback fake S3 server (tests\Storiva.FakeS3) for it. It never contacts any
/// other endpoint.</para>
/// </summary>
internal static partial class SmokeTest
{
    /// <summary>Data folder, fixed Windows theme source and connection manager of the smoke-test instance.</summary>
    internal sealed record Environment(StorivaDataPaths Paths, ManualThemeSource ThemeSource, ConnectionManager Connections);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    /// <summary>
    /// Renders only this window (frame included) with PrintWindow(PW_RENDERFULLCONTENT). Unlike a screen capture it
    /// never contains other windows, even when Storiva is covered.
    /// </summary>
    private static void SaveWindowOnly(Form window, string path)
    {
        using var bitmap = RenderWindow(window);
        bitmap.Save(path, ImageFormat.Png);
    }

    internal static Bitmap RenderWindow(Form window)
    {
        var bitmap = new Bitmap(window.Width, window.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try
            {
                PrintWindow(window.Handle, hdc, 2);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        return bitmap;
    }

    /// <summary>Colour actually rendered on the top edge of a focused text box (window-only capture, no screen read).</summary>
    private static Color FocusedInputBorder(Form window, TextBox box)
    {
        box.Focus();
        Application.DoEvents();
        using var bitmap = RenderWindow(window);
        var bounds = box.Parent!.RectangleToScreen(box.Bounds);
        return bitmap.GetPixel(bounds.X - window.Left + (bounds.Width / 2), bounds.Y - window.Top);
    }

    private static IEnumerable<T> Descendants<T>(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Ch(int v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }

        var (la, lb) = (L(a), L(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static int Run(MainForm form, string outputDir, Environment env)
    {
        Directory.CreateDirectory(outputDir);
        var report = new StringBuilder();
        var failures = 0;
        var checks = 0;
        var prompts = new RecordingPrompts();
        form.Prompts = prompts;

        void Check(string name, bool ok, string detail = "")
        {
            checks++;
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  - " + detail : string.Empty)}");
            if (!ok)
            {
                failures++;
            }
        }

        void Save(Control window, string file)
        {
            Application.DoEvents();
            using var bitmap = new Bitmap(Math.Max(1, window.Width), Math.Max(1, window.Height));
            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
            bitmap.Save(Path.Combine(outputDir, file), ImageFormat.Png);
            report.AppendLine($"      capture: {file} ({bitmap.Width}x{bitmap.Height})");
        }

        void Capture(string file) => Save(form, file);

        static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 20000)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > timeoutMs)
                {
                    return false;
                }

                await Task.Delay(25);
            }

            return true;
        }

        form.Shown += async (_, _) =>
        {
            try
            {
                report.AppendLine($"Storiva smoke test - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                report.AppendLine($"Window: {form.Width}x{form.Height} px, DPI {form.DeviceDpi}");
                report.AppendLine();

                // ---------- 1. Start state (no connection) and layout ----------
                report.AppendLine("[layout]");
                Check("starts not connected, at Home, with nothing listed", !form.IsConnected && form.CurrentPath.IsRoot && form.VisibleNames.Count == 0 && form.TreeNodeCount == 0);
                Check("\"Not connected\" panel shown over the list", form.EmptyState.Shown && form.ConnectionLabel == "Not connected", form.ConnectionLabel);
                Check("keyboard focus starts in the bucket pane, not on a splitter (no dotted focus cue)", form.FocusedControl is TreeView, form.FocusedControl?.GetType().Name ?? "none");
                Check("no tasks at start (no sample tasks)", form.TaskRowCount == 0 && form.TasksTabTitle == "Tasks (0)", form.TasksTabTitle);
                Check("object columns follow the reference order", form.ObjectColumns.SequenceEqual(["Name", "Size", "Type", "Last Modified", "Storage Class"]), string.Join(", ", form.ObjectColumns));
                Check("row height is 20 logical px", form.RowHeight.Actual == form.RowHeight.Expected, $"{form.RowHeight.Actual} px");
                Check("bucket bar: New bucket, Add external bucket, Refresh [F E1]", form.BucketBarLabels.SequenceEqual(["New bucket", "Add external bucket", "Refresh"]), string.Join(", ", form.BucketBarLabels));
                Check("action bar: Upload, Download, Open, Delete, New Folder, Refresh", form.ActionBarLabels.SequenceEqual(["Upload", "Download", "Open", "Delete", "New Folder", "Refresh"]), string.Join(", ", form.ActionBarLabels));
                Check("eight bottom tabs in reference order", form.BottomTabTitles.SequenceEqual(MainForm.BottomTabNames), string.Join(", ", form.BottomTabTitles));
                Check("task footer filters: Running, Queued, Canceled, Failed, All", form.TaskFooterLabels.SequenceEqual(["Running", "Queued", "Canceled", "Failed", "All"]), string.Join(", ", form.TaskFooterLabels));
                Check("task columns start with Task, Size, %, Progress, Status", form.TaskColumns.Take(5).SequenceEqual(["Task", "Size", "%", "Progress", "Status"]), string.Join(", ", form.TaskColumns));
                var r = form.RegionBounds;
                Check("path bar and bucket bar share the top edge", Math.Abs(r.Path.Top - r.Buckets.Top) <= 1, $"path y={r.Path.Top}, buckets y={r.Buckets.Top}");
                Check("vertical order: path bar < list < action bar < tabs < footer",
                    r.Path.Bottom <= r.List.Top + 2 && r.List.Bottom <= r.Actions.Top + 2 && r.Actions.Bottom < r.Tabs.Top && r.Tabs.Bottom <= r.Footer.Top + 1,
                    $"path {r.Path.Top}-{r.Path.Bottom}, list {r.List.Top}-{r.List.Bottom}, actions {r.Actions.Top}-{r.Actions.Bottom}, tabs {r.Tabs.Top}-{r.Tabs.Bottom}, footer {r.Footer.Top}-{r.Footer.Bottom}");
                Capture("01-not-connected.png");
                SaveWindowOnly(form, Path.Combine(outputDir, "01b-not-connected-window.png"));
                report.AppendLine("      capture: 01b-not-connected-window.png (PrintWindow: this window only)");

                // ---------- 2. Menus without a connection ----------
                report.AppendLine("[menus]");
                Check("menu bar: Accounts, Buckets, Files, Bookmarks, Tools, Help [F]", form.MenuTitles.SequenceEqual(["Accounts", "Buckets", "Files", "Bookmarks", "Tools", "Help"]), string.Join(", ", form.MenuTitles));
                foreach (var title in new[] { "Files", "Buckets" })
                {
                    var expected = CommandCatalog.Menus.First(m => m.Title.Replace("&", string.Empty) == title).Entries
                        .Select(e => e switch { CommandEntry c => CommandCatalog.Get(c.Id).Text, SubmenuEntry s => s.Title ?? CommandCatalog.Get(s.Id).Text, _ => null })
                        .OfType<string>().ToList();
                    var actual = form.MenuItemTexts(title);
                    Check($"{title} menu shows the {expected.Count} transcribed items in order", actual.SequenceEqual(expected), $"{actual.Count} items");
                }

                var tools = form.MenuItemTexts("Tools").ToList();
                Check("Tools menu: reference items end with Diagnostics.., Options..; no demo commands",
                    tools.IndexOf("Options..") == tools.IndexOf("Diagnostics..") + 1 && tools.IndexOf("Options..") > 0 && !tools.Any(t => t.Contains("Demo", StringComparison.Ordinal) || t.Contains("Simulation", StringComparison.Ordinal)),
                    string.Join(", ", tools));
                bool E(CommandId id) => form.MenuItemEnabled(id);
                Check("not connected: bucket and file commands disabled",
                    !E(CommandId.BucketCreate) && !E(CommandId.BucketRefresh) && !E(CommandId.FileUploadFiles) && !E(CommandId.FileRefresh) && !E(CommandId.BookmarkToggle));
                Check("not connected: accounts, Options and Help enabled",
                    E(CommandId.AccountAdd) && E(CommandId.AccountManage) && E(CommandId.ToolsOptions) && E(CommandId.HelpAbout));
                Check("Accounts menu without saved connections says so", form.AccountMenuTexts.SequenceEqual(["(no saved connections — Add New Account..)"]), string.Join(" | ", form.AccountMenuTexts));

                // ---------- 3. Dialogs ----------
                report.AppendLine("[dialogs]");
                using (var create = new Dialogs.CreateBucketDialog(["existing-bucket"], ConnectionRules.KnownAwsRegions, form.SuggestBucketName(), _ => { }, defaultRegion: "eu-west-1", isAws: true))
                {
                    create.Show(form);
                    Application.DoEvents();
                    Check("Create New Bucket: default new-bucket-xxxxxxxx, pre-selected [F E7]", Regex.IsMatch(create.BucketName, "^new-bucket-[0-9a-f]{8}$") && create.NameIsSelected, create.BucketName);
                    Check("Create New Bucket: the connection's region is preselected", create.BucketRegion == "eu-west-1", create.BucketRegion);
                    create.TypeName("Bad_Name");
                    Application.DoEvents();
                    Check("Create New Bucket: invalid name blocks the button", !create.CanSubmit && create.VisibleError.Length > 0, create.VisibleError);
                    create.TypeName("existing-bucket");
                    Application.DoEvents();
                    Check("Create New Bucket: an existing name blocks the button", !create.CanSubmit, create.VisibleError);
                    create.TypeName("smoke-test-bucket");
                    Application.DoEvents();
                    Check("Create New Bucket: valid name enables the button", create.CanSubmit);
                    Save(create, "09-dialog-create-bucket.png");
                    create.Close();
                }

                // Connection dialog (no network: the test button uses a stub here).
                using (var editor = new Dialogs.ConnectionDialog(null, [], false, null,
                           (_, _, _) => Task.FromResult(new ConnectionTestResult(ConnectionStatus.Connected, "stub")), null, _ => { }))
                {
                    editor.Show(form);
                    Application.DoEvents();
                    Check("Add New Account: AWS by default, key fields shown, secret masked, endpoint hidden",
                        editor.KeyFieldsVisible && editor.SecretIsMasked && !editor.EndpointVisible);
                    Check("Add New Account: cannot save without credentials", !editor.CanSubmit);
                    var field = Descendants<TextBox>(editor).First(t => t.Visible && !t.Multiline);
                    var edge = FocusedInputBorder(editor, field);
                    Check("focused text box keeps the flat theme border (no accent \"active\" border)",
                        edge.ToArgb() == StorivaPalette.Current.BorderStrong.ToArgb(), $"#{edge.R:X2}{edge.G:X2}{edge.B:X2}");
                    editor.Fill("Local MinIO", StorageProvider.S3Compatible, "minio.example.internal:9000", "us-east-1", AuthenticationMethod.AccessKey, "EXAMPLEKEY", "example-secret");
                    editor.ClickSave();
                    Application.DoEvents();
                    Check("S3-compatible: endpoint field shown; URL without scheme rejected", editor.EndpointVisible && editor.VisibleErrors.Contains("scheme", StringComparison.Ordinal), editor.VisibleErrors);
                    editor.Fill("Local MinIO", StorageProvider.S3Compatible, "http://minio.example.internal:9000", "us-east-1", AuthenticationMethod.AccessKey, "EXAMPLEKEY", "example-secret");
                    Application.DoEvents();
                    Check("http:// is rejected unless unencrypted HTTP is explicitly allowed", !editor.CanSubmit && editor.VisibleErrors.Contains("unencrypted", StringComparison.Ordinal), editor.VisibleErrors);
                    editor.Fill("Local MinIO", StorageProvider.S3Compatible, "https://minio.example.internal:9000", "us-east-1", AuthenticationMethod.AccessKey, "EXAMPLEKEY", "example-secret", pathStyle: true);
                    Application.DoEvents();
                    Check("valid S3-compatible draft can be saved", editor.CanSubmit, editor.VisibleErrors);
                    Save(editor, "10-dialog-connection.png");
                    editor.Close();
                }

                using (var options = new Dialogs.OptionsDialog(SessionOptions.Default, ThemePreference.System, "Windows is currently set to light apps."))
                {
                    options.Show(form);
                    Application.DoEvents();
                    Check("Options: 8 pages with the reference names", options.PageTitles.SequenceEqual(SessionOptions.PageNames), string.Join(", ", options.PageTitles));
                    Save(options, "11-dialog-options.png");
                    options.SetConcurrent(1);
                    options.ShowPageNamed("Interface");
                    options.ChooseTheme(ThemePreference.Dark);
                    Application.DoEvents();
                    Save(options, "11b-dialog-options-interface.png");
                    options.ClickOk();
                    Check("Options › Interface: Light / Dark / System choice is returned", options.ThemeResult == ThemePreference.Dark, options.ThemeResult.ToString());
                    if (options.Result is { } result)
                    {
                        form.ApplyOptions(result);
                    }
                }

                Check("Options › Queueing sets the simultaneous operations", form.MaxConcurrentTasks == 1, form.MaxConcurrentTasks.ToString());
                form.ApplyOptions(SessionOptions.Default with { CompactRows = false });
                Check("Options › Interface: comfortable rows are 24 px", form.RowHeight.Actual == form.RowHeight.Expected && form.RowHeight.Expected == form.LogicalToDeviceUnits(24), $"{form.RowHeight.Actual}");
                form.ApplyOptions(SessionOptions.Default);

                using (var about = new Dialogs.AboutDialog())
                {
                    about.Show(form);
                    Application.DoEvents();
                    Save(about, "12-dialog-about.png");
                    about.Close();
                }

                Check("no notice was shown so far", prompts.Shown.Count == 0, string.Join(" | ", prompts.Shown.Select(p => p.Title)));

                // ---------- 4. Themes ----------
                report.AppendLine("[theme]");
                Check("starts with the System preference, Windows light → Light", form.EffectiveTheme == EffectiveTheme.Light && form.GridBackColor == StorivaPalette.Light.Surface);
                form.ApplyThemePreference(ThemePreference.Dark);
                Application.DoEvents();
                Check("Dark: palette applied to list, tree, menu and status bar",
                    form.EffectiveTheme == EffectiveTheme.Dark && form.GridBackColor == StorivaPalette.Dark.Surface && form.TreeBackColor == StorivaPalette.Dark.Surface
                    && form.ChromeColors.All(c => c == StorivaPalette.Dark.Chrome || c == StorivaPalette.Dark.Toolbar), $"grid {form.GridBackColor.Name}, tree {form.TreeBackColor.Name}");
                Check("Dark: tab strip painted with the palette", form.TabsCustomPainted);
                Check("Dark: grid text keeps contrast", Contrast(form.GridForeColor, form.GridBackColor) >= 7, $"{Contrast(form.GridForeColor, form.GridBackColor):0.0}:1");
                Check("Dark: preference saved in settings.json", File.ReadAllText(env.Paths.SettingsFile).Contains("\"Dark\"", StringComparison.Ordinal));
                Capture("16-dark-theme.png");
                var searchEdge = FocusedInputBorder(form, form.SearchTextBox);
                Check("Dark: focused search box keeps the flat dark border", searchEdge.ToArgb() == StorivaPalette.Dark.BorderStrong.ToArgb(), $"#{searchEdge.R:X2}{searchEdge.G:X2}{searchEdge.B:X2} {InputBorders.Describe(form.SearchTextBox)} parent={form.SearchTextBox.Parent?.GetType().Name}");
                // DrawToBitmap does not paint the title bar, and a screen capture could include other windows of the
                // user: read the attribute back from DWM instead.
                var darkTitle = NativeTheming.IsTitleBarDark(form);
                Check("Dark: the Windows title bar uses dark mode (DWM attribute read back)", darkTitle != false, darkTitle?.ToString() ?? "not reported by this Windows version");
                SaveWindowOnly(form, Path.Combine(outputDir, "16b-dark-theme-window.png"));
                report.AppendLine("      capture: 16b-dark-theme-window.png (PrintWindow: this window only, with its title bar)");

                form.ApplyThemePreference(ThemePreference.System);
                Application.DoEvents();
                Check("System + Windows light → Light", form.EffectiveTheme == EffectiveTheme.Light && form.GridBackColor == StorivaPalette.Light.Surface);
                Check("Light: the title bar is back to light mode", NativeTheming.IsTitleBarDark(form) != true);
                env.ThemeSource.Set(true);
                Application.DoEvents();
                Check("System follows Windows switching to dark without a restart", form.EffectiveTheme == EffectiveTheme.Dark && form.GridBackColor == StorivaPalette.Dark.Surface);
                env.ThemeSource.Set(false);
                Application.DoEvents();
                Check("…and back to light, with the original light colours", form.EffectiveTheme == EffectiveTheme.Light && form.GridBackColor == StorivaPalette.Light.Surface
                    && form.TreeBackColor == StorivaPalette.Light.Surface && !form.TabsCustomPainted);
                Check("System preference saved", File.ReadAllText(env.Paths.SettingsFile).Contains("\"System\"", StringComparison.Ordinal));

                // ---------- 5. Connection against the loopback fake S3 server ----------
                var endpoint = System.Environment.GetEnvironmentVariable("STORIVA_SMOKE_S3_ENDPOINT");
                var key = System.Environment.GetEnvironmentVariable("STORIVA_SMOKE_S3_KEY");
                var secret = System.Environment.GetEnvironmentVariable("STORIVA_SMOKE_S3_SECRET");
                if (endpoint is { Length: > 0 } && key is { Length: > 0 } && secret is { Length: > 0 }
                    && Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) && endpointUri.IsLoopback)
                {
                    await RemoteSection(form, env, prompts, endpoint, key, secret, outputDir, Check, Capture, Save, WaitUntil, report);
                }
                else
                {
                    report.AppendLine("SKIP  [remote] STORIVA_SMOKE_S3_* not set (run tools\\validate.ps1 to start the loopback fake S3 server)");
                }

                form.WindowState = FormWindowState.Normal;
                form.Size = new Size(form.MinimumSize.Width, form.MinimumSize.Height);
                Capture("14-minimum-size.png");
                form.WindowState = FormWindowState.Maximized;
                Capture("15-maximized.png");
            }
            catch (Exception ex)
            {
                Check("smoke test completed without exceptions", false, ex.ToString());
            }
            finally
            {
                report.AppendLine();
                report.AppendLine(failures == 0 ? $"RESULT: all {checks} checks passed" : $"RESULT: {failures} of {checks} check(s) failed");
                File.WriteAllText(Path.Combine(outputDir, "report.txt"), report.ToString());
                form.BeginInvoke(form.Close);
            }
        };

        Application.Run(form);
        return failures == 0 ? 0 : 1;
    }
}
