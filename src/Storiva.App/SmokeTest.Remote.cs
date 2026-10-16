using System.Text;
using System.Text.RegularExpressions;
using Storiva.App.Commands;
using Storiva.App.Main;
using Storiva.Core.Commands;
using Storiva.Core.Connections;
using Storiva.Core.Details;
using Storiva.Core.Listing;
using Storiva.Core.Settings;
using Storiva.Core.Storage;
using Storiva.Core.Tasks;

namespace Storiva.App;

/// <summary>
/// [remote]: the real window against the loopback fake S3 server started by tools\validate.ps1. Exercises the same
/// entry points a user would: connect, list, paginate, preview, upload, download, folder, copy/paste, delete, tags,
/// error mapping and disconnect, and checks that no secret reaches the settings files.
/// </summary>
internal static partial class SmokeTest
{
    private static async Task RemoteSection(
        MainForm form,
        Environment env,
        RecordingPrompts prompts,
        string endpoint,
        string key,
        string secret,
        string outputDir,
        Action<string, bool, string> check,
        Action<string> capture,
        Action<Control, string> save,
        Func<Func<bool>, int, Task<bool>> waitUntil,
        StringBuilder report)
    {
        void Check(string name, bool ok, string detail = "") => check(name, ok, detail);
        Task<bool> Wait(Func<bool> condition, int timeout = 20000) => waitUntil(condition, timeout);
        bool TasksSettled() => form.RealTasks.All(t => t.State is not (TransferState.Queued or TransferState.Running));

        // Pages are 2 items here: load the rest of the location, like a user clicking "Load more".
        async Task LoadAll()
        {
            await Wait(() => !form.IsListing);
            while (form.LoadMoreVisible)
            {
                form.ClickLoadMore();
                await Wait(() => !form.IsListing);
            }
        }

        report.AppendLine("[remote]");
        var connections = env.Connections;
        var profile = new ConnectionProfile
        {
            Name = "Smoke fake S3",
            Provider = StorageProvider.S3Compatible,
            Endpoint = endpoint,
            Region = "us-east-1",
            Authentication = AuthenticationMethod.AccessKey,
            ForcePathStyle = true,
            AllowInsecureHttp = true,
            UseFlexibleChecksums = false,
        };

        // Settings and secrets.
        var saved = connections.Save(profile, new CredentialsDraft(key, secret, string.Empty));
        var json = File.ReadAllText(env.Paths.ConnectionsFile);
        var secretFiles = Directory.GetFiles(env.Paths.SecretsFolder);
        var secretBytesFound = secretFiles.Any(f =>
        {
            var bytes = File.ReadAllBytes(f);
            return Contains(bytes, Encoding.UTF8.GetBytes(secret)) || Contains(bytes, Encoding.Unicode.GetBytes(secret)) || Contains(bytes, Encoding.UTF8.GetBytes(key));
        });
        Check("connection saved: connections.json has no secret and no access key", !json.Contains(secret, StringComparison.Ordinal) && !json.Contains(key, StringComparison.Ordinal) && json.Contains("Smoke fake S3", StringComparison.Ordinal));
        Check("credentials stored separately, DPAPI-protected (no plaintext in the file)", secretFiles.Length == 1 && !secretBytesFound, $"{secretFiles.Length} file(s)");

        // Test connection: real outcome, read-only.
        var ok = await connections.TestAsync(saved, null, CancellationToken.None);
        Check("Test connection: Connected (stored credentials)", ok.Status == ConnectionStatus.Connected, ok.Summary);
        var wrongKey = await connections.TestAsync(saved, new CredentialsDraft("WRONGKEY", secret, string.Empty), CancellationToken.None);
        Check("Test connection: unknown key → Invalid credentials, secret not in the message",
            wrongKey.Status == ConnectionStatus.InvalidCredentials && !wrongKey.Summary.Contains(secret, StringComparison.Ordinal), wrongKey.Summary);
        var unreachable = await connections.TestAsync(saved with { Endpoint = "http://127.0.0.1:9" }, null, CancellationToken.None);
        Check("Test connection: closed port → Network error", unreachable.Status == ConnectionStatus.NetworkError, unreachable.Summary);

        // Connect.
        form.RemotePageSize = 2;
        var connected = await form.ConnectAsync(saved.Id);
        Check("Connect switches the window to the connection", connected && form.IsConnected && form.ConnectionLabel.StartsWith("Connected", StringComparison.Ordinal), form.ConnectionLabel);
        Check("bucket list comes from the server", form.TreeRootNames.SequenceEqual(["storiva-empty", "storiva-smoke"]), string.Join(", ", form.TreeRootNames));
        Check("the \"Not connected\" panel is hidden and the object list shown", !form.EmptyState.Shown && form.ObjectListShown);

        form.SubmitPathText("/storiva-smoke/");
        await Wait(() => !form.IsListing && form.VisibleNames.Count > 0);
        Check("first page of the bucket (page size 2): folders first", form.VisibleNames.SequenceEqual(["data", "many"]), string.Join(", ", form.VisibleNames));
        Check("Load more is offered when the server says the listing is truncated", form.LoadMoreVisible);
        form.ClickLoadMore();
        await Wait(() => !form.IsListing && form.VisibleNames.Contains("readme.txt"));
        Check("Load more appends the next page", form.VisibleNames.Contains("readme.txt") && !form.LoadMoreVisible, string.Join(", ", form.VisibleNames));

        form.OpenVisibleItem("many");
        await Wait(() => !form.IsListing && form.VisibleNames.Count > 0);
        while (form.LoadMoreVisible)
        {
            form.ClickLoadMore();
            await Wait(() => !form.IsListing);
        }

        Check("inside a folder the bucket pane still lists only the buckets", form.TreeNodeCount == form.TreeRootNames.Count && form.TreeSelection == StoragePath.Parse("storiva-smoke"),
            $"{form.TreeNodeCount} nodes");
        Check("paging through a folder of 5 objects (pages of 2)", form.VisibleNames.Count == 5, string.Join(", ", form.VisibleNames));
        form.TypeSearch("003");
        Check("local search filters the loaded items", form.VisibleNames.SequenceEqual(["item-003.bin"]), string.Join(", ", form.VisibleNames));
        form.TypeSearch(string.Empty);
        form.ClickColumnHeader(ItemSortColumn.Size);
        var ascending = form.VisibleNames.FirstOrDefault();
        form.ClickColumnHeader(ItemSortColumn.Size);
        Check("sort by size (both directions)", ascending == "item-001.bin" && form.VisibleNames.FirstOrDefault() == "item-005.bin", $"{ascending} / {form.VisibleNames.FirstOrDefault()}");
        form.ClickColumnHeader(ItemSortColumn.Name);
        form.ChooseFilter(ItemTypeFilter.FoldersOnly);
        var foldersOnly = form.VisibleNames.Count;
        form.ChooseFilter(ItemTypeFilter.FilesOnly);
        Check("folders-only / files-only filters", foldersOnly == 0 && form.VisibleNames.Count == 5, $"{foldersOnly} / {form.VisibleNames.Count}");
        form.ChooseFilter(ItemTypeFilter.All);
        form.ApplyOptions(SessionOptions.Default with { ShowFoldersInTree = true });
        Check("Options › show folders in the bucket pane: the tree follows into the folder", form.TreeSelection == StoragePath.Parse("storiva-smoke/many") && form.TreeNodeCount > form.TreeRootNames.Count,
            $"{form.TreeSelection?.ToDisplayString() ?? "(none)"}, {form.TreeNodeCount} nodes");
        form.ApplyOptions(SessionOptions.Default);
        Check("…and off again: buckets only", form.TreeNodeCount == form.TreeRootNames.Count, $"{form.TreeNodeCount} nodes");
        form.ClickSelectedTreeNode();
        await Wait(() => !form.IsListing);
        Check("clicking the highlighted bucket goes to its top level", form.CurrentPath == StoragePath.Parse("storiva-smoke"), form.CurrentPath.ToDisplayString());
        form.ClickBack();
        await Wait(() => !form.IsListing);
        form.ClickUp();
        await Wait(() => !form.IsListing && form.VisibleNames.Contains("readme.txt"));
        Check("breadcrumb of a remote location", form.LocationText == "Home > storiva-smoke", form.LocationText);

        // Menus and shortcuts in context (reference rules [F E4–E6], with real data).
        bool E(CommandId id) => form.MenuItemEnabled(id);
        form.ClearListSelection();
        Check("in a bucket, nothing selected: Upload and Create New Folder enabled, Open disabled", E(CommandId.FileUploadFiles) && E(CommandId.FileCreateFolder) && !E(CommandId.FileOpen));
        Check("in a bucket: bucket commands enabled (tree bucket)", E(CommandId.BucketDelete) && E(CommandId.BucketEditVersioning) && E(CommandId.ToolsBucketSharingWizard));
        form.SelectVisibleItem("readme.txt");
        Check("1 file selected: Open, Download, Rename, Delete, Headers, Tags, Properties enabled",
            E(CommandId.FileOpen) && E(CommandId.FileDownload) && E(CommandId.FileRename) && E(CommandId.FileDelete) && E(CommandId.FileEditHeaders) && E(CommandId.FileEditTags) && E(CommandId.FileProperties));
        save(form.OpenMenu("Files"), "02-menu-files.png");
        form.CloseMenus();
        form.SelectVisibleItem("data");
        Check("1 folder selected: Download (prefix) enabled; Headers, Tags, storage class disabled; ACL and Rename enabled",
            E(CommandId.FileDownload) && !E(CommandId.FileEditHeaders) && !E(CommandId.FileEditTags) && !E(CommandId.FileChangeStorageClass) && E(CommandId.FileEditAcl) && E(CommandId.FileRename));
        form.FocusList();
        Check("Ctrl+N in the list → Create New Folder", form.ResolveShortcut(Keys.Control | Keys.N) == CommandId.FileCreateFolder, form.ResolveShortcut(Keys.Control | Keys.N)?.ToString() ?? "none");
        Check("Del in the list → Delete", form.ResolveShortcut(Keys.Delete) == CommandId.FileDelete);
        form.FocusTree();
        Check("Ctrl+N in the bucket pane → Create New Bucket", form.ResolveShortcut(Keys.Control | Keys.N) == CommandId.BucketCreate, form.ResolveShortcut(Keys.Control | Keys.N)?.ToString() ?? "none");
        Check("Del in the bucket pane → Delete Bucket", form.ResolveShortcut(Keys.Delete) == CommandId.BucketDelete);
        form.FocusList();
        form.SelectVisibleItem("readme.txt");
        var noticesBefore = prompts.Shown.Count;
        form.Execute(CommandId.FileCopyTo);
        Check("an unavailable command shows the notice and executes nothing",
            prompts.Shown.Count == noticesBefore + 1 && prompts.Shown[^1].Title == "Not available" && prompts.Shown[^1].Message.Contains("Nothing was executed", StringComparison.Ordinal),
            prompts.Shown.Count > noticesBefore ? prompts.Shown[^1].Message.Split('\r')[0] : "no notice");
        Check("…and logs a W entry", form.EventLogLines.Any(l => l.StartsWith("W [", StringComparison.Ordinal) && l.Contains("Copy to", StringComparison.Ordinal)));

        // Sharing: pre-signed links and copy-address commands. The clipboard is replaced by a recorder from here on.
        var copied = new List<(string Text, bool Sensitive)>();
        form.ClipboardWriter = (text, sensitive) => copied.Add((text, sensitive));
        var noticesAfter = prompts.Shown.Count;
        var gridMenu = form.GridMenuItems();
        bool InGridMenu(string text) => gridMenu.Any(i => i.Text == text && i.Enabled);
        Check("list context menu: Generate Web URL.., Copy S3 URI, Copy Object URL enabled for a file",
            E(CommandId.FileGenerateUrl) && InGridMenu("Generate Web URL..") && InGridMenu("Copy S3 URI") && InGridMenu("Copy Object URL"),
            string.Join(", ", gridMenu.Where(i => i.Enabled).Select(i => i.Text)));
        form.Execute(CommandId.CopyS3Uri);
        Check("Copy S3 URI copies s3://bucket/key", copied.Count == 1 && copied[0] == ("s3://storiva-smoke/readme.txt", false), copied.LastOrDefault().Text ?? "nothing copied");
        form.Execute(CommandId.CopyObjectUrl);
        var plainUrl = endpoint.TrimEnd('/') + "/storiva-smoke/readme.txt";
        Check("Copy Object URL copies the plain URL on the endpoint (path-style, no credentials)", copied.Count == 2 && copied[1] == (plainUrl, false), copied.LastOrDefault().Text ?? "nothing copied");
        using (var share = form.CreateShareLinkDialogForSelection())
        {
            share!.Show(form);
            await Wait(() => share.IsReady, 10000);
            var first = share.Links.FirstOrDefault()?.Url ?? string.Empty;
            Check("Generate Web URL: one pre-signed link, 1 hour by default, on the endpoint",
                share.Links.Count == 1 && share.Validity == TimeSpan.FromHours(1) && first.StartsWith(plainUrl + "?", StringComparison.Ordinal)
                && first.Contains("X-Amz-Expires=3600", StringComparison.Ordinal) && first.Contains("X-Amz-Signature=", StringComparison.Ordinal),
                Regex.Replace(first, "X-Amz-(Signature|Credential)=[^&]+", "X-Amz-$1=..."));
            Check("…the secret is not in the link", first.Length > 0 && !first.Contains(secret, StringComparison.Ordinal));
            Check("…the dialog warns that anyone with the link can download until it expires", share.NoticeText.Contains("Anyone who has this link", StringComparison.Ordinal), share.NoticeText);
            Check("…and shows the expiry and the unencrypted-HTTP note", share.StatusText.Contains("Expires", StringComparison.Ordinal) && share.StatusText.Contains("unencrypted HTTP", StringComparison.Ordinal), share.StatusText);
            save(share, "21-dialog-share-link.png");
            share.ChooseValidity(TimeSpan.FromDays(7));
            await Wait(() => share.IsReady && share.Links[0].Url.Contains("X-Amz-Expires=604800", StringComparison.Ordinal), 10000);
            Check("choosing 7 days signs a new link (X-Amz-Expires=604800, the SigV4 maximum)", share.IsReady && share.Links[0].Url.Contains("X-Amz-Expires=604800", StringComparison.Ordinal));
            share.ClickCopy();
            Check("Copy puts the link on the clipboard marked sensitive (kept out of the clipboard history)",
                copied.Count == 3 && copied[2].Sensitive && share.Links.Count == 1 && copied[2].Text == share.Links[0].Url);
            share.Close();
        }

        Check("Event log: \"Generated a link for <key> valid for 7 days\", never the link itself",
            form.EventLogLines.Any(l => l.Contains("Generated a link for s3://storiva-smoke/readme.txt valid for 7 days", StringComparison.Ordinal))
            && form.EventLogLines.All(l => !l.Contains("X-Amz-", StringComparison.Ordinal)));
        form.SelectVisibleItem("data");
        gridMenu = form.GridMenuItems();
        Check("1 folder selected: Generate Web URL and Copy Object URL disabled, Copy S3 URI enabled",
            !E(CommandId.FileGenerateUrl) && !InGridMenu("Copy Object URL") && InGridMenu("Copy S3 URI"));
        form.Execute(CommandId.CopyS3Uri);
        Check("Copy S3 URI of a folder is its prefix", copied.Count == 4 && copied[3].Text == "s3://storiva-smoke/data/", copied.LastOrDefault().Text ?? "nothing copied");
        Check("Buckets › Generate Web URLs.. is enabled for the bucket", E(CommandId.BucketGenerateUrls));
        using (var bucketShare = await form.PrepareBucketShareLinksAsync())
        {
            bucketShare?.Show(form);
            await Wait(() => bucketShare?.IsReady == true, 10000);
            var keys = bucketShare?.Links.Select(l => l.Key).ToList() ?? [];
            Check("Generate Web URLs (bucket): one link per file of the bucket, folder markers left out",
                keys.Count >= 8 && keys.Contains("readme.txt") && keys.Contains("data/config.json") && !keys.Any(k => k.EndsWith('/')), $"{keys.Count} link(s)");
            if (bucketShare is not null)
            {
                save(bucketShare, "22-dialog-share-bucket.png");
                bucketShare.Close();
            }
        }

        Check("…logged without the links, and no sharing command showed a notice",
            form.EventLogLines.Any(l => l.Contains("links for files of s3://storiva-smoke/", StringComparison.Ordinal)) && form.EventLogLines.All(l => !l.Contains("X-Amz-", StringComparison.Ordinal))
            && prompts.Shown.Count == noticesAfter, string.Join(" | ", prompts.Shown.Skip(noticesAfter).Select(p => p.Title)));

        // Details from the server.
        form.SelectVisibleItem("readme.txt");
        form.SelectBottomTab("Preview");
        await Wait(() => form.Preview.PreviewText.Contains("Storiva smoke test object", StringComparison.Ordinal), 10000);
        Check("Preview shows the first bytes read from the server as text", form.Preview.Kind == PreviewKind.Text && form.Preview.PreviewText.Contains("Storiva smoke test object", StringComparison.Ordinal), form.Preview.Kind.ToString());
        form.SelectBottomTab("Properties");
        await Wait(() => form.PropertyValue("Content-Type") is "text/plain", 10000);
        Check("Properties: Content-Type and ETag from HEAD, nothing invented",
            form.PropertyValue("Content-Type") == "text/plain" && form.PropertyValue("ETag")?.StartsWith('"') == true, $"{form.PropertyValue("Content-Type")} {form.PropertyValue("ETag")}");
        form.SelectBottomTab("Permissions");
        await Wait(() => form.Permissions.PermissionsOf(Grantee.Owner) != Permission.None, 10000);
        Check("Permissions: owner FULL_CONTROL read from the ACL (read-only)", form.Permissions.PermissionsOf(Grantee.Owner).HasFlag(Permission.FullControl));
        form.SelectBottomTab("Versions");
        await Wait(() => form.Versions.MessageText.Contains("not enabled", StringComparison.Ordinal), 10000);
        Check("Versions: no versioning → no versions promised", form.Versions.MessageText.Contains("not enabled", StringComparison.Ordinal), form.Versions.MessageText);
        capture("17-remote-connected.png");

        // Upload.
        var upload = Path.Combine(outputDir, "upload-sample.txt");
        var payload = Encoding.UTF8.GetBytes("Uploaded by the Storiva smoke test. " + new string('x', 64 * 1024));
        File.WriteAllBytes(upload, payload);
        prompts.FilesToUpload = [upload];
        form.ClickUpload();
        await Wait(TasksSettled);
        var uploadTask = form.RealTasks.LastOrDefault(t => t.Kind == TransferKind.Upload);
        Check("Upload: task completed with 100% from reported progress", uploadTask is { State: TransferState.Completed, Progress: 100 }, uploadTask?.Message ?? "no task");
        Check("Event log uses the reference format with TaskID", form.EventLogLines.Any(l => Regex.IsMatch(l, @"^I \[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] .+ TaskID: \d+$")), form.EventLogLines.LastOrDefault() ?? "-");
        await LoadAll();
        Check("the list is refreshed after the upload", form.VisibleNames.Contains("upload-sample.txt"), string.Join(", ", form.VisibleNames));

        // Download (single file → save dialog).
        var downloaded = Path.Combine(outputDir, "downloaded-sample.txt");
        File.Delete(downloaded);
        form.SelectVisibleItem("upload-sample.txt");
        prompts.SaveFile = downloaded;
        form.ClickDownload();
        await Wait(TasksSettled);
        Check("Download writes exactly the uploaded bytes", File.Exists(downloaded) && File.ReadAllBytes(downloaded).AsSpan().SequenceEqual(payload));
        Check("no partial file is left behind", !File.Exists(downloaded + ".storiva-partial"));

        // Cancel a running transfer (the fake server holds this object back for 30 s).
        form.ClickUp();
        await Wait(() => !form.IsListing);
        form.SubmitPathText("/storiva-smoke/slow/");
        await Wait(() => !form.IsListing && form.VisibleNames.Contains("big.bin"));
        var slowTarget = Path.Combine(outputDir, "slow-download.bin");
        File.Delete(slowTarget);
        form.SelectVisibleItem("big.bin");
        prompts.SaveFile = slowTarget;
        form.ClickDownload();
        await Wait(() => form.RealTasks.Any(t => t.Name.EndsWith("big.bin", StringComparison.Ordinal) && t.State == TransferState.Running));
        var slow = form.RealTasks.Last(t => t.Name.EndsWith("big.bin", StringComparison.Ordinal));
        await Task.Delay(300);
        Check("a running download shows an indeterminate or real state, not a fake percentage", slow.State == TransferState.Running && slow.Progress == 0, $"{slow.State} {slow.Progress}%");
        form.CancelTask(slow.Id);
        await Wait(() => slow.State == TransferState.Canceled, 15000);
        Check("Cancel stops the running download", slow.State == TransferState.Canceled, slow.Message);
        Check("…and leaves no file or partial file behind", !File.Exists(slowTarget) && !File.Exists(slowTarget + ".storiva-partial"));
        Check("a canceled task can be retried", TaskQueue.CanRetry(slow));
        form.SubmitPathText("/storiva-smoke/");
        await LoadAll();

        // Tags (explicit, confirmed write).
        form.SelectVisibleItem("upload-sample.txt");
        form.SelectBottomTab("Tags");
        await Wait(() => form.Tags.Entries.Count == 0 && form.Tags.MessageText.Length == 0, 10000);
        var tagError = form.Tags.AddEntryDirect("env", "smoke");
        form.Tags.ClickApply();
        await Wait(() => !form.Tags.IsDirty, 10000);
        Check("Tags: Apply asks for confirmation and saves on the server", tagError is null && !form.Tags.IsDirty && prompts.Shown.Any(p => p.Title == "Save tags on the server"));

        // Folder, copy/paste, delete.
        Check("Create folder on the server", await form.CreateRemoteFolderAsync("smoke-folder"));
        await LoadAll();
        form.SelectVisibleItem("readme.txt");
        form.Execute(CommandId.FileCopy);
        form.OpenVisibleItem("smoke-folder");
        await Wait(() => !form.IsListing);
        form.Execute(CommandId.FilePaste);
        await Wait(TasksSettled);
        await LoadAll();
        Check("Copy + Paste copies the object on the server", form.VisibleNames.Contains("readme.txt"), string.Join(", ", form.VisibleNames));

        var confirmsBefore = prompts.Shown.Count(p => p.Kind == "confirm");
        form.SelectVisibleItem("readme.txt");
        form.Execute(CommandId.FileDelete);
        await Wait(TasksSettled);
        await LoadAll();
        Check("Delete always asks first on a real connection, then removes the object",
            prompts.Shown.Count(p => p.Kind == "confirm") == confirmsBefore + 1 && !form.VisibleNames.Contains("readme.txt")
            && prompts.Shown.Last(p => p.Kind == "confirm").Message.Contains("on the server", StringComparison.Ordinal));

        // Rename and move (copy + delete on the server).
        form.ClickUp();
        await Wait(() => !form.IsListing);
        while (form.LoadMoreVisible)
        {
            form.ClickLoadMore();
            await Wait(() => !form.IsListing);
        }

        form.StartRemoteRename(form.VisibleItem("upload-sample.txt"), "renamed-sample.txt");
        await Wait(TasksSettled);
        await Wait(() => !form.IsListing && form.VisibleNames.Contains("renamed-sample.txt"));
        Check("Rename a file: copy to the new key, original deleted", form.VisibleNames.Contains("renamed-sample.txt") && !form.VisibleNames.Contains("upload-sample.txt"), string.Join(", ", form.VisibleNames));
        form.SelectVisibleItem("renamed-sample.txt");
        form.Execute(CommandId.FileCut);
        form.OpenVisibleItem("smoke-folder");
        await Wait(() => !form.IsListing);
        form.Execute(CommandId.FilePaste);
        await Wait(TasksSettled);
        await Wait(() => !form.IsListing && form.VisibleNames.Contains("renamed-sample.txt"));
        Check("Cut + Paste moves the object into the folder", form.VisibleNames.Contains("renamed-sample.txt"), string.Join(", ", form.VisibleNames));
        form.ClickUp();
        await Wait(() => !form.IsListing);
        while (form.LoadMoreVisible)
        {
            form.ClickLoadMore();
            await Wait(() => !form.IsListing);
        }

        Check("…and it left the source folder", !form.VisibleNames.Contains("renamed-sample.txt"), string.Join(", ", form.VisibleNames));
        form.StartRemoteRename(form.VisibleItem("smoke-folder"), "smoke-folder-renamed");
        await Wait(TasksSettled);
        await Wait(() => !form.IsListing && form.VisibleNames.Contains("smoke-folder-renamed"), 10000);
        while (form.LoadMoreVisible && !form.VisibleNames.Contains("smoke-folder-renamed"))
        {
            form.ClickLoadMore();
            await Wait(() => !form.IsListing);
        }

        Check("Rename a folder: every object moved to the new prefix (confirmed first)",
            form.VisibleNames.Contains("smoke-folder-renamed") && !form.VisibleNames.Contains("smoke-folder") && prompts.Shown.Any(p => p.Title == "Rename folder"), string.Join(", ", form.VisibleNames));

        // Folder upload and folder download.
        var localFolder = Path.Combine(outputDir, "upload-folder");
        Directory.CreateDirectory(Path.Combine(localFolder, "nested"));
        File.WriteAllText(Path.Combine(localFolder, "one.txt"), "one");
        File.WriteAllText(Path.Combine(localFolder, "nested", "two.txt"), "two");
        prompts.Folder = localFolder;
        form.Execute(CommandId.FileUploadFolders);
        await Wait(TasksSettled);
        await Wait(() => !form.IsListing);
        while (form.LoadMoreVisible && !form.VisibleNames.Contains("upload-folder"))
        {
            form.ClickLoadMore();
            await Wait(() => !form.IsListing);
        }

        Check("Upload folder(s): the folder appears with its files", form.VisibleNames.Contains("upload-folder"), string.Join(", ", form.VisibleNames));
        var downloadRoot = Path.Combine(outputDir, "download-folder");
        if (Directory.Exists(downloadRoot))
        {
            Directory.Delete(downloadRoot, recursive: true);
        }

        Directory.CreateDirectory(downloadRoot);
        form.SelectVisibleItem("upload-folder");
        prompts.Folder = downloadRoot;
        prompts.Conflict = Core.Remote.ConflictPolicy.KeepBoth;
        form.ClickDownload();
        await Wait(TasksSettled);
        Check("Download a folder keeps its structure", File.ReadAllText(Path.Combine(downloadRoot, "upload-folder", "nested", "two.txt")) == "two");

        // Buckets: create (with versioning), versioning shown, delete when empty, open by name.
        Check("Create New Bucket on the server", await form.CreateRemoteBucketAsync("storiva-created", "us-east-1", versioning: true));
        await Wait(() => !form.IsListing && form.TreeRootNames.Contains("storiva-created"));
        Check("…the new bucket appears in the tree", form.TreeRootNames.Contains("storiva-created"), string.Join(", ", form.TreeRootNames));
        form.SelectInTree(StoragePath.Parse("storiva-created"));
        await Wait(() => !form.IsListing);
        form.SelectBottomTab("Properties");
        await Wait(() => form.PropertyValue("Versioning") == "Enabled", 10000);
        Check("…with versioning enabled (read back from the server)", form.PropertyValue("Versioning") == "Enabled", form.PropertyValue("Versioning") ?? "-");
        Check("Suspend versioning (confirmed)", await form.SetRemoteVersioningAsync("storiva-created", VersioningStatus.Suspended));
        form.Execute(CommandId.BucketDelete);
        await Wait(() => !form.TreeRootNames.Contains("storiva-created"), 10000);
        Check("Delete Bucket removes an empty bucket after confirmation", !form.TreeRootNames.Contains("storiva-created") && form.CurrentPath.IsRoot, string.Join(", ", form.TreeRootNames));
        Check("Add External Bucket opens a bucket that is not in the list", await form.AddRemoteExternalBucketAsync("storiva-hidden") && form.TreeRootNames.Contains("storiva-hidden (external)"), string.Join(", ", form.TreeRootNames));
        var missing = await form.AddRemoteExternalBucketAsync("storiva-does-not-exist");
        Check("…and an unknown bucket name is reported, not added", !missing && !form.TreeRootNames.Any(n => n.StartsWith("storiva-does-not-exist", StringComparison.Ordinal)));

        // A refused bucket deletion is reported, not hidden.
        form.SubmitPathText("/storiva-smoke/");
        await Wait(() => !form.IsListing);
        form.ClickUp();
        await Wait(() => !form.IsListing);
        form.SelectInTree(StoragePath.Parse("storiva-smoke"));
        var warnsBefore = prompts.Shown.Count(p => p.Kind == "warn");
        form.Execute(CommandId.BucketDelete);
        await Wait(() => prompts.Shown.Count(p => p.Kind == "warn") > warnsBefore, 10000);
        Check("Delete Bucket on a non-empty bucket: the server's refusal is shown, bucket kept",
            prompts.Shown.LastOrDefault(p => p.Kind == "warn").Message?.Contains("not empty", StringComparison.Ordinal) == true && form.TreeRootNames.Contains("storiva-smoke"),
            prompts.Shown.LastOrDefault(p => p.Kind == "warn").Message ?? "-");
        Check("errors are logged as E entries", form.EventLogLines.Any(l => l.StartsWith("E [", StringComparison.Ordinal) && l.Contains("not empty", StringComparison.Ordinal)));
        Check("no secret in the Event log", form.EventLogLines.All(l => !l.Contains(secret, StringComparison.Ordinal) && !l.Contains(key, StringComparison.Ordinal)));
        form.SelectBottomTab("Tasks");
        capture("18-remote-tasks.png");

        // Manage Connections shows the state of each saved connection.
        using (var manage = new Dialogs.ConnectionsDialog(connections, () => form.ActiveConnection?.Id, _ => throw new InvalidOperationException("not used"), (_, _) => true))
        {
            manage.Show(form);
            Application.DoEvents();
            Check("Manage Connections lists the connection as connected (active)", manage.RowCount == 1 && manage.StatusTextOf(0) == "Connected (active)", manage.RowCount > 0 ? manage.StatusTextOf(0) : "no rows");
            using var bitmap = new Bitmap(manage.Width, manage.Height);
            manage.DrawToBitmap(bitmap, new Rectangle(Point.Empty, manage.Size));
            bitmap.Save(Path.Combine(outputDir, "19-manage-connections.png"));
            manage.Close();
        }

        // Disconnect.
        form.SubmitPathText("/storiva-smoke/data/");
        await Wait(() => !form.IsListing);
        form.Execute(CommandId.BookmarkToggle);
        Check("Bookmark the current location", form.IsBookmarked(StoragePath.Parse("storiva-smoke/data")));
        Check("Bookmarks menu lists it", form.MenuItemTexts("Bookmarks").Contains("/storiva-smoke/data/"), string.Join(", ", form.MenuItemTexts("Bookmarks")));

        Check("Accounts menu lists the saved connection and Disconnect",
            form.AccountMenuTexts.Count == 2 && form.AccountMenuTexts[0].StartsWith("Smoke fake S3", StringComparison.Ordinal) && form.AccountMenuTexts[1].StartsWith("Disconnect", StringComparison.Ordinal),
            string.Join(" | ", form.AccountMenuTexts));
        form.FocusList();
        form.DisconnectFromServer();
        Check("Disconnect with the list focused: focus moves to the bucket pane, not to a splitter", form.FocusedControl is TreeView, form.FocusedControl?.GetType().Name ?? "none");
        Check("Disconnect: nothing listed, \"Not connected\" panel with a link to the saved connection",
            !form.IsConnected && form.TreeNodeCount == 0 && form.VisibleNames.Count == 0 && form.EmptyState.Shown
            && form.EmptyState.Links.Any(l => l.StartsWith("Connect to \"Smoke fake S3\"", StringComparison.Ordinal)),
            string.Join(" | ", form.EmptyState.Links));
        capture("20-disconnected.png");
        Check("Disconnect: the object list is replaced by the panel (no overlap)", !form.ObjectListShown);
        Check("connection status recorded in connections.json (no secrets)",
            File.ReadAllText(env.Paths.ConnectionsFile) is var after && after.Contains("\"Connected\"", StringComparison.Ordinal) && !after.Contains(secret, StringComparison.Ordinal));
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
