using System.Drawing.Imaging;
using System.Text;
using Storiva.App.Commands;
using Storiva.App.Main;
using Storiva.Core.Connections;
using Storiva.Core.Settings;

namespace Storiva.App;

/// <summary>
/// "--screenshot-tour &lt;dir&gt;": the README screenshots. Connects to the loopback fake S3 server started by
/// tools\capture-readme-screenshots.ps1 (fictitious "example-*" buckets and test credentials), walks a few views and
/// saves each with PrintWindow (this window only). Like the smoke test it uses its own data folder inside
/// &lt;dir&gt; and a fixed Windows theme, so the user's settings and connections are never read or changed.
/// </summary>
internal static class ScreenshotTour
{
    public static int Run(MainForm form, string outputDir, SmokeTest.Environment env)
    {
        Directory.CreateDirectory(outputDir);
        var log = new StringBuilder();
        var exitCode = 1;
        form.Prompts = new RecordingPrompts();

        void Save(Form window, string file)
        {
            Application.DoEvents();
            using var bitmap = SmokeTest.RenderWindow(window);
            bitmap.Save(Path.Combine(outputDir, file), ImageFormat.Png);
            log.AppendLine($"saved {file} ({bitmap.Width}x{bitmap.Height})");
        }

        static async Task Settle(Func<bool> condition, int timeoutMs = 15000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (!condition() && Environment.TickCount64 < deadline)
            {
                await Task.Delay(25);
            }

            // Let pending detail loads (preview, properties) and repaints finish.
            for (var i = 0; i < 20; i++)
            {
                Application.DoEvents();
                await Task.Delay(25);
            }
        }

        form.Shown += async (_, _) =>
        {
            try
            {
                form.Size = new Size(form.LogicalToDeviceUnits(1120), form.LogicalToDeviceUnits(740));
                form.ApplyThemePreference(ThemePreference.Light);
                await Settle(() => true);
                Save(form, "storiva-not-connected.png");

                var endpoint = System.Environment.GetEnvironmentVariable("STORIVA_SMOKE_S3_ENDPOINT");
                var key = System.Environment.GetEnvironmentVariable("STORIVA_SMOKE_S3_KEY");
                var secret = System.Environment.GetEnvironmentVariable("STORIVA_SMOKE_S3_SECRET");
                if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
                {
                    throw new InvalidOperationException("STORIVA_SMOKE_S3_* not set: run tools\\capture-readme-screenshots.ps1.");
                }

                // Dialogs, filled with example values only (nothing is saved from them).
                using (var editor = new Dialogs.ConnectionDialog(null, [], false, null,
                           (_, _, _) => Task.FromResult(new ConnectionTestResult(ConnectionStatus.Connected, "stub")), null, _ => { }))
                {
                    editor.Show(form);
                    editor.Fill("Example MinIO", StorageProvider.S3Compatible, "https://minio.example.internal:9000", "us-east-1",
                        AuthenticationMethod.AccessKey, "EXAMPLEACCESSKEY", "example-secret-value", pathStyle: true);
                    editor.ActiveControl = null;
                    await Settle(() => true);
                    Save(editor, "storiva-add-account.png");
                    editor.Close();
                }

                using (var options = new Dialogs.OptionsDialog(SessionOptions.Default, ThemePreference.System, "Windows is currently set to light apps."))
                {
                    options.Show(form);
                    options.ShowPageNamed("Interface");
                    await Settle(() => true);
                    Save(options, "storiva-options-interface.png");
                    options.Close();
                }

                var profile = env.Connections.Save(new ConnectionProfile
                {
                    Name = "Example storage",
                    Provider = StorageProvider.S3Compatible,
                    Endpoint = endpoint,
                    Region = "us-east-1",
                    Authentication = AuthenticationMethod.AccessKey,
                    ForcePathStyle = true,
                    AllowInsecureHttp = true,
                    UseFlexibleChecksums = false,
                }, new CredentialsDraft(key, secret, string.Empty));
                if (!await form.ConnectAsync(profile.Id))
                {
                    throw new InvalidOperationException("could not connect to the fake S3 server: " + form.ConnectionLabel);
                }

                form.SubmitPathText("/example-website/");
                await Settle(() => !form.IsListing && form.VisibleNames.Contains("index.html"));
                form.SelectVisibleItem("index.html");
                form.SelectBottomTab("Preview");
                await Settle(() => true);
                Save(form, "storiva-main-light.png");

                using (var share = form.CreateShareLinkDialogForSelection())
                {
                    if (share is not null)
                    {
                        share.Show(form);
                        await Settle(() => true, 1000);
                        Save(share, "storiva-share-link.png");
                        share.Close();
                    }
                }

                form.ApplyThemePreference(ThemePreference.Dark);
                form.SubmitPathText("/example-website/downloads/");
                await Settle(() => !form.IsListing && form.VisibleNames.Contains("catalog-2026.pdf"));
                form.SelectVisibleItem("catalog-2026.pdf");
                form.SelectBottomTab("Properties");
                await Settle(() => true);
                Save(form, "storiva-main-dark.png");

                form.ApplyThemePreference(ThemePreference.Light);
                form.DisconnectFromServer();
                exitCode = 0;
            }
            catch (Exception ex)
            {
                log.AppendLine("ERROR " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                File.WriteAllText(Path.Combine(outputDir, "tour.txt"), log.ToString());
                form.Close();
            }
        };

        Application.Run(form);
        return exitCode;
    }
}
