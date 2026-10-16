using System.Globalization;
using Storiva.App.Details;
using Storiva.App.Dialogs;
using Storiva.App.Icons;
using Storiva.App.Theme;
using Storiva.Core.Activity;
using Storiva.Core.Connections;
using Storiva.Core.Details;
using Storiva.Core.Listing;
using Storiva.Core.Navigation;
using Storiva.Core.Remote;
using Storiva.Core.Storage;
using Storiva.Core.Tasks;

namespace Storiva.App.Main;

/// <summary>
/// Connections. When one is open, the catalog and details stores are the remote caches of that connection and every
/// change goes through <see cref="IObjectStorageClient"/>, either as a
/// task (transfers, bulk changes) or as an awaited request (create folder/bucket). No request is sent without an
/// explicit user action, and nothing is reported as done unless the server confirmed it.
/// </summary>
internal sealed partial class MainForm
{
    private readonly HashSet<StoragePath> _listingInProgress = [];
    private readonly Dictionary<int, (RemoteSession Session, StoragePath[] Refresh)> _operationRefresh = new();
    private readonly System.Windows.Forms.Timer _detailsTimer = new() { Interval = 250 };
    private RemoteSession? _remote;
    private CancellationTokenSource? _detailsCancellation;
    private List<StorageItem> _remoteClipboard = [];
    private bool _remoteCut;
    private string? _selectAfterLoad;
    private ConnectionStatus? _remoteHealth;

    internal bool IsConnected => _remote is not null;

    /// <summary>Closing with real operations still running asks first; nothing remote is deleted or rolled back.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var running = _tasks.Tasks.Count(t => t.State is TransferState.Running or TransferState.Queued);
        if (running > 0 && e.CloseReason == CloseReason.UserClosing && !Prompts.Confirm(this, "Exit Storiva",
                $"{running} operation(s) are still running or queued. Exiting cancels them: what was already transferred stays, " +
                "a partly downloaded file is removed, and nothing else on the server is changed.\r\n\r\nExit anyway?"))
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>Objects per listing page (1000, the S3 maximum). The smoke test lowers it to exercise "Load more".</summary>
    internal int RemotePageSize { get; set; } = RemoteStorageCatalog.DefaultPageSize;

    /// <summary>Profile of the active connection (smoke test, status bar).</summary>
    internal ConnectionProfile? ActiveConnection => _remote?.Profile;

    /// <summary>A live connection: client, caches, its own bookmarks and the operations still using the client.</summary>
    internal sealed class RemoteSession(ConnectionProfile profile, IObjectStorageClient client, int pageSize) : IDisposable
    {
        public ConnectionProfile Profile { get; } = profile;

        public IObjectStorageClient Client { get; } = client;

        public RemoteStorageCatalog Catalog { get; } = new(client, pageSize);

        public RemoteDetailsStore Details => _details ??= new RemoteDetailsStore(Client, Catalog);

        public BookmarkList Bookmarks { get; } = new();

        /// <summary>Canceled on disconnect: stops listings and detail reads of this session.</summary>
        public CancellationTokenSource Lifetime { get; } = new();

        public int ActiveOperations { get; set; }

        public bool Retired { get; private set; }

        private RemoteDetailsStore? _details;

        /// <summary>Stops using the session; the client is disposed once its last operation finished.</summary>
        public void Retire()
        {
            Retired = true;
            Lifetime.Cancel();
            DisposeIfIdle();
        }

        public void DisposeIfIdle()
        {
            if (Retired && ActiveOperations == 0)
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            Client.Dispose();
            Lifetime.Dispose();
        }
    }

    private void WireRemoteEvents()
    {
        _loadMoreButton.Click += (_, _) => LoadMore();
        _runner.Finished += OnOperationFinished;
        _detailsTimer.Tick += async (_, _) =>
        {
            _detailsTimer.Stop();
            await LoadVisibleDetailsAsync();
        };
        _bottomTabs.SelectedIndexChanged += (_, _) => ScheduleDetailsLoad();

        _permissionsTab.Availability = t => RemoteState(t, DetailsAspect.Acl);
        _headersTab.Availability = t => RemoteState(t, DetailsAspect.Metadata);
        _tagsTab.Availability = t => RemoteState(t, DetailsAspect.Tags);
        _previewTab.Availability = t => RemoteState(t, DetailsAspect.Preview);
        _versionsTab.Availability = t => RemoteState(t, DetailsAspect.Versions);
        _permissionsTab.Note = PermissionsNote;
        _tagsTab.RemoteApply = SaveRemoteTagsAsync;
    }

    // ---------- connect / disconnect ----------

    /// <summary>Opens a saved connection: lists the buckets (read-only) and, when that works, switches the window to it.</summary>
    internal async Task<bool> ConnectAsync(Guid id)
    {
        if (_connections.Find(id) is not { } profile)
        {
            return false;
        }

        if (_remote?.Profile.Id == id)
        {
            return true;
        }

        if (!ConfirmLeaveSession())
        {
            return false;
        }

        _connections.SetRuntimeStatus(id, ConnectionStatus.Connecting);
        SetStatus($"Connecting to \"{profile.Name}\" ({profile.Describe()})…");
        UpdateConnectionLabel(ConnectionStatus.Connecting, profile);
        IObjectStorageClient client;
        try
        {
            client = _connections.OpenClient(id);
        }
        catch (StorageOperationException ex)
        {
            ConnectFailed(profile, new ConnectionTestResult(ConnectionStatusText.FromError(ex.Kind), ex.Error.Describe(), Error: ex.Error));
            return false;
        }

        var session = new RemoteSession(profile, client, RemotePageSize);
        ConnectionTestResult result;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            try
            {
                var count = await session.Catalog.LoadAsync(StoragePath.Root, nextPage: false, timeout.Token);
                result = new ConnectionTestResult(ConnectionStatus.Connected, $"Connected. {count} bucket(s) visible to these credentials.", count);
            }
            catch (StorageOperationException ex) when (ex.Kind == StorageErrorKind.AccessDenied)
            {
                result = new ConnectionTestResult(ConnectionStatus.ConnectedLimited,
                    "Connected, but listing buckets is not allowed for these credentials. Use Buckets › Add External Bucket to open a bucket by name.", Error: ex.Error);
            }
            catch (StorageOperationException ex)
            {
                session.Dispose();
                ConnectFailed(profile, new ConnectionTestResult(ConnectionStatusText.FromError(ex.Kind), ex.Error.Describe(), Error: ex.Error));
                return false;
            }
            catch (OperationCanceledException)
            {
                session.Dispose();
                ConnectFailed(profile, new ConnectionTestResult(ConnectionStatus.NetworkError, "The server did not answer within 45 seconds."));
                return false;
            }
        }

        _connections.RecordTest(id, result);
        SwitchTo(session, result.Status);
        RememberLastConnection(id);
        _activity.Write($"Connected to \"{profile.Name}\" ({profile.Describe()}, {profile.AuthenticationLabel}). {result.Summary}",
            result.Status == ConnectionStatus.Connected ? ActivityLevel.Information : ActivityLevel.Warning);
        SetStatus(result.Summary);
        return true;
    }

    private void ConnectFailed(ConnectionProfile profile, ConnectionTestResult result)
    {
        _connections.RecordTest(profile.Id, result);
        RefreshAccountStatus();
        _activity.Write($"Could not connect to \"{profile.Name}\": {result.Summary}", ActivityLevel.Error);
        SetStatus($"Could not connect to \"{profile.Name}\".");
        Prompts.Warn(this, ConnectionStatusText.Label(result.Status), $"Could not connect to \"{profile.Name}\".\r\n\r\n{result.Summary}");
    }

    /// <summary>Closes the connection (nothing on the server changes).</summary>
    internal void Disconnect()
    {
        if (_remote is null || !ConfirmLeaveSession())
        {
            return;
        }

        var name = _remote.Profile.Name;
        SwitchTo(null, null);
        _activity.Write($"Disconnected from \"{name}\".");
        SetStatus("Not connected.");
    }

    /// <summary>Asks before leaving a session that still has running operations, and cancels them.</summary>
    private bool ConfirmLeaveSession()
    {
        if (_remote is null)
        {
            return true;
        }

        var active = _operationRefresh.Where(o => o.Value.Session == _remote).Select(o => _tasks.Tasks.FirstOrDefault(t => t.Id == o.Key))
            .OfType<TransferTask>().Where(t => t.State is TransferState.Running or TransferState.Queued).ToList();
        if (active.Count > 0 && !Prompts.Confirm(this, "Disconnect",
                $"{active.Count} operation(s) of \"{_remote.Profile.Name}\" are still running or queued. Disconnecting cancels them; " +
                "what was already transferred stays where it is (a partially downloaded file is removed).\r\n\r\nDisconnect?"))
        {
            return false;
        }

        foreach (var task in active)
        {
            _tasks.Cancel(task.Id);
        }

        return true;
    }

    private void SwitchTo(RemoteSession? session, ConnectionStatus? status)
    {
        var old = _remote;
        _detailsCancellation?.Cancel();
        _remote = session;
        _remoteHealth = status;
        _catalog.Inner = session?.Catalog ?? (IStorageCatalog)EmptyStorageCatalog.Instance;
        _details.Inner = session?.Details ?? (IObjectDetailsStore)EmptyDetailsStore.Instance;
        _remoteClipboard = [];
        _detailsTarget = null;
        _listingInProgress.Clear();
        SwapBookmarks(session?.Bookmarks ?? _noConnectionBookmarks);
        if (old is not null)
        {
            _connections.SetRuntimeStatus(old.Profile.Id, ConnectionStatus.Disconnected);
            old.Retire();
        }

        if (session is not null && status is { } s)
        {
            _connections.SetRuntimeStatus(session.Profile.Id, s);
        }

        _navigator.Reset();
        RebuildTree();
        RefreshAccountStatus();
        UpdateCommandStates();
    }

    // ---------- listing ----------

    /// <summary>True when the location is in the cache (or nothing is connected); otherwise starts loading it.</summary>
    private bool EnsureListed(StoragePath path)
    {
        if (_remote is not { } session || session.Catalog.IsLoaded(path))
        {
            UpdateLoadMore();
            return true;
        }

        _ = LoadListingAsync(path, nextPage: false);
        return false;
    }

    private void LoadMore()
    {
        if (IsConnected)
        {
            _ = LoadListingAsync(_navigator.Current, nextPage: true);
        }
    }

    private async Task LoadListingAsync(StoragePath path, bool nextPage)
    {
        if (_remote is not { } session || !_listingInProgress.Add(path))
        {
            return;
        }

        if (path == _navigator.Current)
        {
            SetStatus(nextPage ? "Loading more items from the server…" : $"Loading {path.ToDisplayString()} from the server…");
            UpdateLoadMore();
        }

        try
        {
            await session.Catalog.LoadAsync(path, nextPage, session.Lifetime.Token);
            if (session != _remote)
            {
                return;
            }

            MarkHealthy();
            OnListingLoaded(path);
        }
        catch (StorageOperationException ex) when (session == _remote && ex.Kind != StorageErrorKind.Canceled)
        {
            ReportRemoteError($"Could not list {path.ToDisplayString()}", ex.Error);
        }
        catch (OperationCanceledException)
        {
            // Disconnected meanwhile.
        }
        finally
        {
            _listingInProgress.Remove(path);
            if (session == _remote)
            {
                UpdateLoadMore();
            }
        }
    }

    private void OnListingLoaded(StoragePath path)
    {
        if (path.IsRoot)
        {
            RebuildTree();
        }
        else
        {
            RefreshTreeNode(path);
        }

        if (path != _navigator.Current)
        {
            return;
        }

        var select = _selectAfterLoad;
        _selectAfterLoad = null;
        _currentItems = _catalog.GetChildren(path);
        BindRows(select is null ? SelectedRows().Select(r => r.Item.Name).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal) { select });
        var catalog = _remote!.Catalog;
        SetStatus(catalog.HasMore(path)
            ? $"Showing the first {catalog.LoadedCount(path):N0} items of {path.ToDisplayString()}. More are on the server: Load more."
            : $"Showing {path.ToDisplayString()} ({catalog.LoadedCount(path):N0} items).");
    }

    private void UpdateLoadMore()
    {
        var more = _remote is { } session && session.Catalog.HasMore(_navigator.Current);
        _loadMoreButton.Visible = more;
        _loadMoreButton.Enabled = more && !_listingInProgress.Contains(_navigator.Current);
    }

    /// <summary>F5: forgets the cached listing (and details) of the current location and lists it again.</summary>
    private void RefreshRemote(bool buckets)
    {
        var session = _remote!;
        var current = _navigator.Current;
        if (buckets || current.IsRoot)
        {
            session.Catalog.InvalidateAll();
            session.Details.Reset();
            _ = LoadListingAsync(StoragePath.Root, nextPage: false);
        }
        else
        {
            session.Catalog.Invalidate(current);
            session.Details.Invalidate(current);
        }

        _detailsTarget = null;
        RefreshList(keepSelection: true);
    }

    private void InvalidateAndReload(RemoteSession session, IEnumerable<StoragePath> paths)
    {
        var reloadCurrent = false;
        foreach (var path in paths.Distinct())
        {
            session.Catalog.Invalidate(path);
            session.Details.Invalidate(path);
            reloadCurrent |= _navigator.Current.IsWithin(path) || path.IsWithin(_navigator.Current);
            if (path != _navigator.Current)
            {
                RefreshTreeNode(path);
            }
        }

        if (reloadCurrent && session == _remote)
        {
            _detailsTarget = null;
            RefreshList(keepSelection: true);
        }
    }

    // ---------- errors and connection health ----------

    private void ReportRemoteError(string what, StorageError error, int? taskId = null)
    {
        _activity.Write($"{what}: {error.Describe()}", ActivityLevel.Error, taskId);
        SetStatus($"{what}: {StorageError.Title(error.Kind)}.");
        var health = ConnectionStatusText.FromError(error.Kind);
        if (_remote is { } session && error.Kind is StorageErrorKind.EndpointUnreachable or StorageErrorKind.Tls or StorageErrorKind.InvalidCredentials or StorageErrorKind.CredentialsUnavailable)
        {
            _remoteHealth = health;
            _connections.SetRuntimeStatus(session.Profile.Id, health);
            RefreshAccountStatus();
        }
    }

    private void MarkHealthy()
    {
        if (_remote is { } session && _remoteHealth is not (ConnectionStatus.Connected or ConnectionStatus.ConnectedLimited))
        {
            _remoteHealth = ConnectionStatus.Connected;
            _connections.SetRuntimeStatus(session.Profile.Id, ConnectionStatus.Connected);
            RefreshAccountStatus();
        }
    }

    /// <summary>Status bar: connection state and name first (always visible), provider details after it.</summary>
    private void RefreshAccountStatus()
    {
        UpdateEmptyState();
        if (_remote is { } session)
        {
            UpdateConnectionLabel(_remoteHealth ?? ConnectionStatus.Connected, session.Profile);
        }
        else
        {
            UpdateConnectionLabel(null, null);
        }
    }

    private void UpdateConnectionLabel(ConnectionStatus? status, ConnectionProfile? profile)
    {
        _statusConnection.Text = profile is null
            ? "Not connected"
            : $"{ConnectionStatusText.Label(status ?? ConnectionStatus.Connected)}: {profile.Name}";
        _statusAccount.Text = profile is null ? "Accounts menu to connect" : profile.Describe();
        _statusConnection.ToolTipText = _statusAccount.ToolTipText = profile is null
            ? "Connect to AWS S3 or an S3-compatible server from the Accounts menu."
            : $"{profile.Name}: {profile.Describe()} · {profile.AuthenticationLabel}";
        _connectionLabelState = status;
        UpdateConnectionIcon();
    }

    private ConnectionStatus? _connectionLabelState;

    private void UpdateConnectionIcon()
    {
        var size = LogicalToDeviceUnits(Metrics.IconSmall);
        var state = _connectionLabelState;
        _statusConnection.Image = state switch
        {
            null => LucideIcons.Get(Lucide.CloudOff, size, _palette.TextSecondary),
            ConnectionStatus.Connected => LucideIcons.Get(Lucide.Cloud, size, _palette.Success),
            ConnectionStatus.ConnectedLimited or ConnectionStatus.Connecting => LucideIcons.Get(Lucide.Cloud, size, _palette.Warning),
            _ => LucideIcons.Get(Lucide.CloudOff, size, _palette.Danger),
        };
    }

    // ---------- helpers ----------

    private (string Bucket, string Prefix) RemoteLocation()
    {
        var current = _navigator.Current;
        return (current.Bucket!, _remote!.Catalog.PrefixOf(current));
    }

    private string KeyOf(StorageItem item) => _remote!.Catalog.KeyOf(item);

    /// <summary>The raw last key segment of an item (display names may be adjusted for blank names).</summary>
    private string RawName(StorageItem item)
    {
        var parentPrefix = item.Path.Parent is { } parent ? _remote!.Catalog.PrefixOf(parent) : string.Empty;
        var key = KeyOf(item);
        var raw = key.StartsWith(parentPrefix, StringComparison.Ordinal) ? key[parentPrefix.Length..] : item.Name;
        return raw.TrimEnd('/');
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {many}";

    /// <summary>Queues a real operation as a task; the listed paths are re-read when it finishes.</summary>
    private TransferTask StartRemoteOperation(string name, TransferKind kind, long? size, Func<IObjectStorageClient, OperationContext, Task<OperationOutcome>> work, params StoragePath[] refresh)
    {
        var session = _remote!;
        var task = _runner.Start(name, kind, size, async context =>
        {
            if (session.Retired)
            {
                throw new StorageOperationException(new StorageError(StorageErrorKind.InvalidRequest, "The connection was closed. Connect again and start the operation again."));
            }

            session.ActiveOperations++;
            try
            {
                return await work(session.Client, context);
            }
            finally
            {
                session.ActiveOperations--;
                session.DisposeIfIdle();
            }
        });
        _operationRefresh[task.Id] = (session, refresh);
        SelectDetailsTab("Tasks");
        return task;
    }

    private void OnOperationFinished(object? sender, OperationFinishedEventArgs e)
    {
        if (!_operationRefresh.TryGetValue(e.Task.Id, out var info))
        {
            return;
        }

        if (e.Error is { Kind: not StorageErrorKind.Canceled } error)
        {
            if (info.Session == _remote)
            {
                ReportRemoteError($"{e.Task.Kind} \"{e.Task.Name}\" failed", error, e.Task.Id);
            }
        }
        else if (e.Outcome is { } outcome && info.Session == _remote)
        {
            MarkHealthy();
            SetStatus(outcome.Success ? $"{e.Task.Name}: {outcome.Message}" : $"{e.Task.Name}: completed with problems — {outcome.Message}");
        }

        if (info.Session == _remote && info.Refresh.Length > 0)
        {
            InvalidateAndReload(info.Session, info.Refresh);
        }
    }

    // ---------- Files: upload / download ----------

    private void RemoteUploadFiles()
    {
        var files = Prompts.PickFilesToUpload(this, $"Upload files to {_navigator.Current.ToDisplayString()}");
        if (files is null || files.Count == 0)
        {
            return;
        }

        UploadLocalFiles(files);
    }

    /// <summary>
    /// Queues one upload task per local file into the current location (chosen in the picker, or dropped from
    /// Explorer). Asks before replacing objects with the same name; false when the user declined.
    /// </summary>
    private bool UploadLocalFiles(IReadOnlyList<string> files)
    {
        var (bucket, prefix) = RemoteLocation();
        var location = _navigator.Current;
        var existing = _currentItems.Where(i => i.Kind == StorageItemKind.File).Select(RawName).ToHashSet(StringComparer.Ordinal);
        var clashes = files.Select(Path.GetFileName).Where(n => n is not null && existing.Contains(n)).ToList();
        if (clashes.Count > 0 && !Prompts.Confirm(this, "Replace objects?",
                $"{Plural(clashes.Count, "object", "objects")} with the same name already exist in {location.ToDisplayString()} and will be replaced on the server:\r\n\r\n" +
                string.Join("\r\n", clashes.Take(10)) + (clashes.Count > 10 ? $"\r\n… and {clashes.Count - 10} more" : string.Empty) +
                "\r\n\r\nWith versioning enabled the previous content is kept as an older version. Continue?"))
        {
            return false;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _activity.Write($"Cannot read \"{name}\": {ex.Message}", ActivityLevel.Error);
                continue;
            }

            var key = prefix + name;
            StartRemoteOperation($"{location.ToDisplayString()}{name}", TransferKind.Upload, size, async (client, ctx) =>
            {
                await RemoteOperations.UploadFileAsync(client, bucket, key, file, FileTypes.ContentType(name), ctx.Progress, ctx.CancellationToken);
                return OperationOutcome.Done($"Uploaded {FileTypes.FormatSize(size)} to s3://{bucket}/{key}");
            }, location);
        }

        SetStatus($"{Plural(files.Count, "upload", "uploads")} queued. Progress is shown in the Tasks tab.");
        return true;
    }

    private void RemoteUploadFolder()
    {
        var folder = Prompts.PickFolder(this, $"Choose a folder to upload into {_navigator.Current.ToDisplayString()}");
        if (folder is null)
        {
            return;
        }

        UploadLocalFolder(folder);
    }

    /// <summary>
    /// Queues the upload of a local folder (with everything below it) into the current location, as one task.
    /// Asks first when a folder with that name already exists; false when the user declined.
    /// </summary>
    private bool UploadLocalFolder(string folder)
    {
        var (bucket, prefix) = RemoteLocation();
        var location = _navigator.Current;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (_currentItems.Any(i => i.IsContainer && RawName(i) == name) && !Prompts.Confirm(this, "Upload folder",
                $"A folder \"{name}\" already exists in {location.ToDisplayString()}. Files with the same relative path will be replaced. Continue?"))
        {
            return false;
        }

        StartRemoteOperation($"{location.ToDisplayString()}{name}/ (folder)", TransferKind.Upload, null, async (client, ctx) =>
        {
            var result = await RemoteOperations.UploadFolderAsync(client, bucket, prefix, folder, FileTypes.ContentType, ctx.Progress, ctx.CancellationToken);
            return result.HasFailures ? OperationOutcome.Partial(result.Summary + ": " + FirstFailure(result)) : OperationOutcome.Done(result.Summary);
        }, location);
        return true;
    }

    private void RemoteDownload()
    {
        var selected = SelectedRows().Select(r => r.Item).Where(i => i.Kind != StorageItemKind.Bucket).ToList();
        var files = selected.Where(i => i.Kind == StorageItemKind.File).ToList();
        var folders = selected.Where(i => i.Kind == StorageItemKind.Folder).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var bucket = _navigator.Current.Bucket!;
        if (files.Count == 1 && folders.Count == 0)
        {
            var file = files[0];
            var target = Prompts.PickSaveFile(this, LocalPathMapper.SafeName(file.Name));
            if (target is null)
            {
                return;
            }

            QueueFileDownload(bucket, file, target, ConflictPolicy.Overwrite); // the save dialog already asked
            return;
        }

        var root = Prompts.PickFolder(this, $"Download {Plural(selected.Count, "item", "items")} to");
        if (root is null)
        {
            return;
        }

        var policy = ConflictPolicy.KeepBoth;
        if (folders.Count > 0 || files.Any(f => File.Exists(Path.Combine(root, LocalPathMapper.SafeName(f.Name)))))
        {
            if (Prompts.ChooseConflictPolicy(this, $"Some files may already exist in {root}. What should Storiva do with them?") is not { } chosen)
            {
                return;
            }

            policy = chosen;
        }

        foreach (var file in files)
        {
            QueueFileDownload(bucket, file, Path.Combine(root, LocalPathMapper.SafeName(file.Name)), policy);
        }

        foreach (var folder in folders)
        {
            var prefix = KeyOf(folder);
            var local = Path.Combine(root, LocalPathMapper.SafeName(folder.Name));
            StartRemoteOperation($"{folder.Path.ToDisplayString()} (folder)", TransferKind.Download, null, async (client, ctx) =>
            {
                var result = await RemoteOperations.DownloadPrefixAsync(client, bucket, prefix, local, policy, ctx.Progress, ctx.CancellationToken);
                return result.HasFailures ? OperationOutcome.Partial($"{result.Summary} into {local}: {FirstFailure(result)}") : OperationOutcome.Done($"{result.Summary} into {local}");
            });
        }

        SetStatus($"{Plural(selected.Count, "download", "downloads")} queued.");
    }

    private void QueueFileDownload(string bucket, StorageItem file, string target, ConflictPolicy policy)
    {
        var key = KeyOf(file);
        StartRemoteOperation(file.Path.ToDisplayString().TrimEnd('/'), TransferKind.Download, file.Size, async (client, ctx) =>
        {
            var written = await RemoteOperations.DownloadFileAsync(client, bucket, key, target, policy, ctx.Progress, ctx.CancellationToken);
            return written is null
                ? OperationOutcome.Done($"Skipped: {target} already exists")
                : OperationOutcome.Done($"Saved to {written}");
        });
    }

    private void RemoteDownloadBucket()
    {
        if (BuildContext().ActiveBucket is not { } bucket)
        {
            return;
        }

        if (!Prompts.Confirm(this, "Download all files",
                $"Download every object of bucket \"{bucket.Name}\"?\r\n\r\nStoriva lists the whole bucket page by page and downloads each object. " +
                "Large buckets take time, and on AWS every request and every byte transferred may be billed."))
        {
            return;
        }

        var root = Prompts.PickFolder(this, $"Download bucket \"{bucket.Name}\" to");
        if (root is null || Prompts.ChooseConflictPolicy(this, $"Files may already exist in {root}. What should Storiva do with them?") is not { } policy)
        {
            return;
        }

        var local = Path.Combine(root, LocalPathMapper.SafeName(bucket.Name));
        StartRemoteOperation($"{bucket.ToDisplayString()}* (all files)", TransferKind.Download, null, async (client, ctx) =>
        {
            var result = await RemoteOperations.DownloadPrefixAsync(client, bucket.Name, string.Empty, local, policy, ctx.Progress, ctx.CancellationToken);
            return result.HasFailures ? OperationOutcome.Partial($"{result.Summary} into {local}: {FirstFailure(result)}") : OperationOutcome.Done($"{result.Summary} into {local}");
        });
    }

    private static string FirstFailure(BulkResult result) =>
        result.Failures.Count == 0 ? string.Empty : $"first failure {result.Failures[0].Key}: {result.Failures[0].Reason}";

    // ---------- Files: delete, rename, copy/move, storage class, folders ----------

    private void RemoteDelete()
    {
        var targets = SelectedRows().Select(r => r.Item).Where(i => i.Kind != StorageItemKind.Bucket).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var (bucket, _) = RemoteLocation();
        var location = _navigator.Current;
        var files = targets.Where(t => t.Kind == StorageItemKind.File).ToList();
        var folders = targets.Where(t => t.Kind == StorageItemKind.Folder).ToList();
        var lines = targets.Take(10).Select(t => t.IsContainer ? $"  {t.Name}/  (folder: every object below it)" : $"  {t.Name}").ToList();
        if (targets.Count > 10)
        {
            lines.Add($"  … and {targets.Count - 10} more");
        }

        var versioning = _remote!.Details.KnownVersioning(bucket) switch
        {
            VersioningStatus.Enabled => "Versioning is enabled on this bucket: S3 adds delete markers and keeps the previous versions.",
            VersioningStatus.Suspended => "Versioning is suspended on this bucket: existing versions are kept, new deletes may be permanent.",
            VersioningStatus.Disabled => "Versioning is not enabled on this bucket: the deletion is permanent.",
            _ => "If versioning is not enabled on this bucket, the deletion is permanent.",
        };
        // Real deletions are always confirmed, whatever Tools › Options › General says.
        if (!Prompts.Confirm(this, "Delete from the server",
                $"Delete {Plural(targets.Count, "item", "items")} from bucket \"{bucket}\" on the server?\r\n\r\n{string.Join("\r\n", lines)}\r\n\r\n{versioning}"))
        {
            return;
        }

        if (files.Count > 0)
        {
            var keys = files.Select(KeyOf).ToList();
            StartRemoteOperation($"Delete {Plural(files.Count, "file", "files")} in {location.ToDisplayString()}", TransferKind.Delete, null, async (client, ctx) =>
            {
                var deleted = 0;
                var failures = new List<(string Key, string Reason)>();
                foreach (var chunk in keys.Chunk(1000))
                {
                    ctx.CancellationToken.ThrowIfCancellationRequested();
                    var result = await client.DeleteObjectsAsync(bucket, chunk, ctx.CancellationToken);
                    deleted += result.Deleted.Count;
                    failures.AddRange(result.Failed);
                    ctx.Progress.Report(new TransferProgress(deleted + failures.Count, keys.Count));
                }

                return failures.Count == 0
                    ? OperationOutcome.Done($"Deleted {Plural(deleted, "object", "objects")}")
                    : OperationOutcome.Partial($"Deleted {deleted}; {failures.Count} refused, first {failures[0].Key}: {failures[0].Reason}");
            }, location);
        }

        foreach (var folder in folders)
        {
            var prefix = KeyOf(folder);
            StartRemoteOperation($"Delete {folder.Path.ToDisplayString()}", TransferKind.Delete, null, async (client, ctx) =>
            {
                var result = await RemoteOperations.DeletePrefixAsync(client, bucket, prefix, ctx.Progress, ctx.CancellationToken);
                return result.HasFailures ? OperationOutcome.Partial(result.Summary + ": " + FirstFailure(result)) : OperationOutcome.Done(result.Summary);
            }, location, folder.Path);
        }

        _bookmarks.Prune(p => !folders.Any(f => p.IsWithin(f.Path)));
    }

    private void RemoteRename()
    {
        if (SelectedRows() is not [var row] || row.Item.Kind == StorageItemKind.Bucket)
        {
            return;
        }

        var item = row.Item;
        using var dialog = new RenameDialog(item, _currentItems.Select(i => i.Name));
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            StartRemoteRename(item, dialog.NewName);
        }
    }

    /// <summary>Renames a file (copy + delete) or a folder (every object below its prefix) as a task.</summary>
    internal void StartRemoteRename(StorageItem item, string newName)
    {
        var (bucket, prefix) = RemoteLocation();
        var location = _navigator.Current;
        if (item.Kind == StorageItemKind.File)
        {
            var from = KeyOf(item);
            var to = prefix + newName;
            StartRemoteOperation($"Rename {item.Name} → {newName}", TransferKind.Move, item.Size, async (client, ctx) =>
            {
                var result = await client.MoveObjectAsync(bucket, from, bucket, to, ctx.CancellationToken);
                return result.SourceDeleted
                    ? OperationOutcome.Done($"Renamed to {newName}")
                    : OperationOutcome.Partial($"Copied to {newName}, but the original could not be deleted ({result.DeleteError?.Describe()}). Both now exist.");
            }, location);
        }
        else
        {
            if (!Prompts.Confirm(this, "Rename folder",
                    $"Renaming folder \"{item.Name}\" copies every object below it to \"{newName}/\" and then deletes the originals (S3 has no real folders).\r\n\r\n" +
                    "Large folders take time. If any copy fails, nothing is deleted from the original folder. Continue?"))
            {
                return;
            }

            var from = KeyOf(item);
            var to = prefix + newName + "/";
            StartRemoteOperation($"Rename {item.Name}/ → {newName}/", TransferKind.Move, null, async (client, ctx) =>
            {
                var result = await RemoteOperations.MovePrefixAsync(client, bucket, from, bucket, to, ctx.Progress, ctx.CancellationToken);
                return result.HasFailures ? OperationOutcome.Partial(result.Summary) : OperationOutcome.Done(result.Summary);
            }, location, item.Path);
        }
    }

    private void RemoteSetClipboard(bool cut)
    {
        _remoteClipboard = SelectedRows().Select(r => r.Item).Where(i => i.Kind != StorageItemKind.Bucket).ToList();
        _remoteCut = cut;
        SetStatus($"{Plural(_remoteClipboard.Count, "item", "items")} {(cut ? "cut" : "copied")}. Open a folder and press Ctrl+V: Storiva copies them on the server{(cut ? " and then deletes the originals" : string.Empty)}.");
    }

    private void RemotePaste()
    {
        if (_remoteClipboard.Count == 0)
        {
            return;
        }

        var (bucket, prefix) = RemoteLocation();
        var destination = _navigator.Current;
        var taken = _currentItems.Select(RawName).ToHashSet(StringComparer.Ordinal);
        var queued = 0;
        foreach (var item in _remoteClipboard.ToList())
        {
            var sourceBucket = item.Path.Bucket!;
            if (_remoteCut && item.Path.Parent == destination)
            {
                continue; // moving into the same folder changes nothing
            }

            var name = RemoteOperations.CopyName(RawName(item), item.IsContainer, taken);
            taken.Add(name);
            var sourceKey = KeyOf(item);
            var refresh = _remoteCut ? new[] { destination, item.Path.Parent ?? StoragePath.Root } : [destination];
            if (item.Kind == StorageItemKind.File)
            {
                var targetKey = prefix + name;
                StartRemoteOperation($"{(_remoteCut ? "Move" : "Copy")} {item.Name} → {destination.ToDisplayString()}{name}", _remoteCut ? TransferKind.Move : TransferKind.Copy, item.Size, async (client, ctx) =>
                {
                    if (!_remoteCut)
                    {
                        await client.CopyObjectAsync(sourceBucket, sourceKey, bucket, targetKey, null, ctx.CancellationToken);
                        return OperationOutcome.Done("Copied on the server");
                    }

                    var moved = await client.MoveObjectAsync(sourceBucket, sourceKey, bucket, targetKey, ctx.CancellationToken);
                    return moved.SourceDeleted
                        ? OperationOutcome.Done("Moved on the server")
                        : OperationOutcome.Partial($"Copied, but the original could not be deleted ({moved.DeleteError?.Describe()}). Both now exist.");
                }, refresh);
            }
            else
            {
                var targetPrefix = prefix + name + "/";
                var cut = _remoteCut;
                StartRemoteOperation($"{(cut ? "Move" : "Copy")} {item.Name}/ → {destination.ToDisplayString()}{name}/", cut ? TransferKind.Move : TransferKind.Copy, null, async (client, ctx) =>
                {
                    var result = cut
                        ? await RemoteOperations.MovePrefixAsync(client, sourceBucket, sourceKey, bucket, targetPrefix, ctx.Progress, ctx.CancellationToken)
                        : await RemoteOperations.CopyPrefixAsync(client, sourceBucket, sourceKey, bucket, targetPrefix, ctx.Progress, ctx.CancellationToken);
                    return result.HasFailures ? OperationOutcome.Partial(result.Summary + (result.PartialMove ? string.Empty : ": " + FirstFailure(result))) : OperationOutcome.Done(result.Summary);
                }, cut ? [destination, item.Path] : [destination]);
            }

            queued++;
        }

        if (_remoteCut)
        {
            _remoteClipboard = []; // like Explorer: a cut can be pasted once
        }

        SetStatus(queued == 0 ? "Nothing to paste here." : $"{Plural(queued, "copy", "copies")} queued on the server.");
    }

    private void RemoteChangeStorageClass(string storageClass)
    {
        var files = SelectedRows().Select(r => r.Item).Where(i => i.Kind == StorageItemKind.File).ToList();
        if (files.Count == 0 || !Prompts.Confirm(this, "Change storage class",
                $"Change the storage class of {Plural(files.Count, "file", "files")} to {storageClass}?\r\n\r\nS3 copies each object onto itself " +
                "(new Last-Modified; with versioning a new version is created). Some classes have minimum storage durations and retrieval fees."))
        {
            return;
        }

        var bucket = _navigator.Current.Bucket!;
        var keys = files.Select(KeyOf).ToList();
        StartRemoteOperation($"Storage class → {storageClass} ({Plural(files.Count, "file", "files")})", TransferKind.Copy, null, async (client, ctx) =>
        {
            var done = 0;
            var failures = new List<string>();
            foreach (var key in keys)
            {
                try
                {
                    await client.CopyObjectAsync(bucket, key, bucket, key, storageClass, ctx.CancellationToken);
                    done++;
                }
                catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
                {
                    failures.Add($"{key}: {ex.Error.Describe()}");
                }

                ctx.Progress.Report(new TransferProgress(done + failures.Count, keys.Count));
            }

            return failures.Count == 0 ? OperationOutcome.Done($"{done} file(s) now {storageClass}") : OperationOutcome.Partial($"{done} changed; {failures.Count} failed, first {failures[0]}");
        }, _navigator.Current);
    }

    private async void RemoteCreateFolder()
    {
        using var dialog = new NewFolderDialog(_navigator.Current, _currentItems.Select(i => i.Name));
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await CreateRemoteFolderAsync(dialog.FolderName);
        }
    }

    /// <summary>Creates the folder marker "prefix/name/" on the server (after the dialog, or from the smoke test).</summary>
    internal async Task<bool> CreateRemoteFolderAsync(string name)
    {
        if (_remote is not { } session)
        {
            return false;
        }

        var (bucket, prefix) = RemoteLocation();
        var location = _navigator.Current;
        SetStatus($"Creating folder \"{name}\" on the server…");
        try
        {
            await session.Client.CreateFolderAsync(bucket, prefix + name + "/", session.Lifetime.Token);
            _activity.Write($"Created folder s3://{bucket}/{prefix}{name}/ (zero-byte folder marker).");
            _selectAfterLoad = name;
            InvalidateAndReload(session, [location]);
            SetStatus($"Folder \"{name}\" created on the server.");
            return true;
        }
        catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
        {
            ReportRemoteError($"Could not create folder \"{name}\"", ex.Error);
            Prompts.Warn(this, "Cannot create folder", ex.Error.Describe());
            return false;
        }
    }

    // ---------- Buckets ----------

    private async void RemoteCreateBucket()
    {
        var session = _remote!;
        var names = _catalog.GetChildren(StoragePath.Root).Select(b => b.Name);
        using var dialog = new CreateBucketDialog(names, session.Client.Regions, SuggestBucketName(), ShowLocalHelp, defaultRegion: session.Profile.Region,
            isAws: session.Profile.Provider == StorageProvider.AwsS3);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await CreateRemoteBucketAsync(dialog.BucketName, dialog.BucketRegion, dialog.EnableVersioning);
    }

    /// <summary>Creates a bucket on the server (after the dialog, or from the smoke test).</summary>
    internal async Task<bool> CreateRemoteBucketAsync(string name, string region, bool versioning)
    {
        if (_remote is not { } session)
        {
            return false;
        }

        SetStatus($"Creating bucket \"{name}\" on the server…");
        try
        {
            await session.Client.CreateBucketAsync(name, region, session.Lifetime.Token);
            var versioningNote = string.Empty;
            if (versioning)
            {
                try
                {
                    await session.Details.SetVersioningAsync(name, VersioningStatus.Enabled, session.Lifetime.Token);
                    versioningNote = " with versioning enabled";
                }
                catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
                {
                    versioningNote = $", but versioning could not be enabled ({ex.Error.Describe()})";
                    _activity.Write($"Bucket \"{name}\" was created{versioningNote}.", ActivityLevel.Warning);
                }
            }

            _activity.Write($"Created bucket \"{name}\"{(session.Profile.Provider == StorageProvider.AwsS3 ? " in " + region : string.Empty)}{versioningNote}.");
            InvalidateAndReload(session, [StoragePath.Root]);
            _ = LoadListingAsync(StoragePath.Root, nextPage: false);
            SetStatus($"Bucket \"{name}\" created{versioningNote}.");
            return true;
        }
        catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
        {
            ReportRemoteError($"Could not create bucket \"{name}\"", ex.Error);
            Prompts.Warn(this, "Cannot create bucket", ex.Error.Describe());
            return false;
        }
    }

    private async void RemoteDeleteBucket()
    {
        if (BuildContext().ActiveBucket is not { } bucket || _remote is not { } session)
        {
            return;
        }

        if (!Prompts.Confirm(this, "Delete bucket on the server",
                $"Delete bucket \"{bucket.Name}\" on the server?\r\n\r\nOnly an empty bucket can be deleted; Storiva never deletes its objects for you. " +
                "On AWS the name may not be available again immediately."))
        {
            return;
        }

        SetStatus($"Deleting bucket \"{bucket.Name}\"…");
        try
        {
            await session.Client.DeleteBucketAsync(bucket.Name, session.Lifetime.Token);
            session.Catalog.ForgetBucket(bucket.Name);
            session.Bookmarks.Prune(p => p.Bucket != bucket.Name);
            _activity.Write($"Deleted bucket \"{bucket.Name}\" on the server.", ActivityLevel.Warning);
            if (_navigator.Current.IsWithin(bucket))
            {
                _navigator.Reset();
            }

            RebuildTree();
            RefreshList(keepSelection: false);
            SetStatus($"Bucket \"{bucket.Name}\" deleted.");
        }
        catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
        {
            ReportRemoteError($"Could not delete bucket \"{bucket.Name}\"", ex.Error);
            Prompts.Warn(this, "Cannot delete bucket", ex.Error.Describe());
        }
    }

    private async void RemoteAddExternalBucket()
    {
        var session = _remote!;
        using var dialog = new AddExternalBucketDialog(_catalog.GetChildren(StoragePath.Root).Select(b => b.Name));
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await AddRemoteExternalBucketAsync(dialog.BucketName);
    }

    /// <summary>Opens a bucket by name (HEAD bucket) and adds it to the list for this session.</summary>
    internal async Task<bool> AddRemoteExternalBucketAsync(string name)
    {
        if (_remote is not { } session)
        {
            return false;
        }

        SetStatus($"Opening bucket \"{name}\"…");
        try
        {
            var region = await session.Client.GetBucketRegionAsync(name, session.Lifetime.Token);
            session.Catalog.AddExternalBucket(name, region);
            _activity.Write($"Added bucket \"{name}\" by name{(region is null ? string.Empty : " (" + region + ")")}. What you may do in it depends on its policies.");
            RebuildTree();
            RefreshList(keepSelection: false);
            SetStatus($"Bucket \"{name}\" added to the list for this session.");
            return true;
        }
        catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
        {
            ReportRemoteError($"Could not open bucket \"{name}\"", ex.Error);
            Prompts.Warn(this, "Cannot add bucket", ex.Error.Describe());
            return false;
        }
    }

    private async void RemoteEditVersioning()
    {
        if (BuildContext().ActiveBucket is not { } bucket || _remote is not { } session)
        {
            return;
        }

        VersioningStatus current;
        try
        {
            current = await session.Client.GetVersioningAsync(bucket.Name, session.Lifetime.Token);
        }
        catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
        {
            ReportRemoteError($"Could not read the versioning of \"{bucket.Name}\"", ex.Error);
            Prompts.Warn(this, "Versioning", ex.Error.Describe());
            return;
        }

        using var dialog = new VersioningDialog(bucket.Name, current);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Selected != current)
        {
            await SetRemoteVersioningAsync(bucket.Name, dialog.Selected);
        }
    }

    /// <summary>Confirms and changes the versioning of a bucket on the server.</summary>
    internal async Task<bool> SetRemoteVersioningAsync(string bucket, VersioningStatus status)
    {
        if (_remote is not { } session || !Prompts.Confirm(this, "Versioning",
                $"{(status == VersioningStatus.Enabled ? "Enable" : "Suspend")} versioning on bucket \"{bucket}\" on the server?" +
                (status == VersioningStatus.Enabled ? "\r\n\r\nOnce enabled, versioning can only be suspended, never turned off. Old versions are billed as storage." : string.Empty)))
        {
            return false;
        }

        try
        {
            await session.Details.SetVersioningAsync(bucket, status, session.Lifetime.Token);
            _activity.Write($"Versioning of bucket \"{bucket}\" set to {status} on the server.");
            BindDetails(force: true);
            ScheduleDetailsLoad();
            return true;
        }
        catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
        {
            ReportRemoteError($"Could not change the versioning of \"{bucket}\"", ex.Error);
            Prompts.Warn(this, "Versioning", ex.Error.Describe());
            return false;
        }
    }

    // ---------- lower tabs ----------

    private static DetailsAspect AspectForTab(string? tab) => tab switch
    {
        "Permissions" => DetailsAspect.Acl,
        "Headers" or "Properties" => DetailsAspect.Metadata,
        "Tags" => DetailsAspect.Tags,
        "Preview" => DetailsAspect.Preview,
        "Versions" => DetailsAspect.Versions,
        _ => DetailsAspect.None,
    };

    private string? RemoteState(DetailsTarget target, DetailsAspect aspect) =>
        _remote is { } session ? session.Details.StateMessage(target.Item.Path, aspect & RemoteDetailsStore.Applicable(target.Item)) : null;

    /// <summary>Loads what the visible tab needs (debounced, so arrowing through the list does not send a request per row).</summary>
    private void ScheduleDetailsLoad()
    {
        if (!IsConnected)
        {
            return;
        }

        _detailsTimer.Stop();
        _detailsTimer.Start();
    }

    private async Task LoadVisibleDetailsAsync()
    {
        if (_remote is not { } session || _detailsTarget is not { } target)
        {
            return;
        }

        var aspect = AspectForTab(_bottomTabs.SelectedTab?.Name) & RemoteDetailsStore.Applicable(target.Item);
        if (aspect == DetailsAspect.Preview && PreviewFormatter.Decide(target.Item) is PreviewKind.Unsupported)
        {
            aspect = DetailsAspect.None; // images, video, audio: nothing is downloaded
        }

        if (aspect == DetailsAspect.None || session.Details.IsLoaded(target.Item.Path, aspect))
        {
            return;
        }

        _detailsCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime.Token);
        _detailsCancellation = cancellation;
        try
        {
            await session.Details.LoadAsync(target.Item, aspect, cancellation.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or StorageOperationException { Kind: StorageErrorKind.Canceled })
        {
            return;
        }
        finally
        {
            if (_detailsCancellation == cancellation)
            {
                _detailsCancellation = null;
            }
        }

        if (session != _remote || _detailsTarget != target)
        {
            return;
        }

        foreach (var tab in TabsFor(aspect))
        {
            tab.Bind(target);
        }

        _propertiesTab.Bind(target);
    }

    private IEnumerable<DetailsTabBase> TabsFor(DetailsAspect aspect) => aspect switch
    {
        DetailsAspect.Acl => [_permissionsTab],
        DetailsAspect.Metadata => [_headersTab],
        DetailsAspect.Tags => [_tagsTab],
        DetailsAspect.Preview => [_previewTab],
        DetailsAspect.Versions => [_versionsTab],
        _ => [],
    };

    private string PermissionsNote(DetailsTarget target)
    {
        var details = _remote?.Details;
        var others = details?.OtherGrants(target.Item.Path).Count ?? 0;
        var owner = details?.OwnerName(target.Item.Path);
        return $"Read-only ACL from the server{(owner is null ? string.Empty : $" · owner {owner}")}" +
               (others > 0 ? $" · {others} other grant(s) not shown" : string.Empty) +
               " · bucket policies and IAM may allow or deny more";
    }

    private async Task<string?> SaveRemoteTagsAsync(DetailsTarget target, IReadOnlyList<(string Key, string Value)> entries)
    {
        if (_remote is not { } session)
        {
            return "Not connected.";
        }

        if (!Prompts.Confirm(this, "Save tags on the server",
                $"Replace the tags of {target.Describe} on the server with {Plural(entries.Count, "tag", "tags")}?" +
                (entries.Count == 0 ? "\r\n\r\nAll tags are removed." : string.Empty)))
        {
            return "Not saved: the change was not confirmed.";
        }

        try
        {
            await session.Details.SaveTagsAsync(target.Item, entries.Select(e => new Tag(e.Key, e.Value)).ToList(), session.Lifetime.Token);
            _activity.Write($"Tags of {target.Describe} replaced on the server ({entries.Count}).");
            return null;
        }
        catch (StorageOperationException ex) when (ex.Kind != StorageErrorKind.Canceled)
        {
            ReportRemoteError($"Could not save the tags of {target.Describe}", ex.Error);
            return ex.Error.Describe();
        }
    }

    /// <summary>Properties of a real item: only values reported by the server or the listing; nothing is invented.</summary>
    private List<(string Name, string Value)> RemotePropertyRows(DetailsTarget? target)
    {
        var session = _remote!;
        var rows = new List<(string, string)>();
        if (target is null)
        {
            rows.Add(("Location", _navigator.Current.ToDisplayString()));
            rows.Add(("Buckets listed", _catalog.GetChildren(StoragePath.Root).Count.ToString(CultureInfo.InvariantCulture)));
            rows.Add(("Connection", $"{session.Profile.Name} ({session.Profile.Describe()})"));
            return rows;
        }

        var item = target.Item;
        const string NotReported = "(not reported by the server)";
        rows.Add(("Name", item.Name));
        switch (item.Kind)
        {
            case StorageItemKind.Bucket:
                rows.Add(("Bucket", "s3://" + item.Name));
                rows.Add(("Region", item.Region ?? NotReported));
                rows.Add(("Created", item.LastModified is null ? NotReported : FileTypes.FormatDate(item.LastModified)));
                rows.Add(("Versioning", session.Details.KnownVersioning(item.Name)?.ToString() ?? session.Details.StateMessage(item.Path, DetailsAspect.Metadata) ?? "—"));
                rows.Add(("Objects / total size", "Not counted (it would require listing the whole bucket)"));
                if (item.IsExternal)
                {
                    rows.Add(("Added", "By name (Add External Bucket), for this session"));
                }

                break;
            case StorageItemKind.Folder:
                rows.Add(("Prefix", KeyOf(item)));
                rows.Add(("Kind", "Key prefix (S3 has no real folders)"));
                var loaded = session.Catalog.IsLoaded(item.Path);
                rows.Add(("Items", loaded ? session.Catalog.LoadedCount(item.Path).ToString("N0", CultureInfo.InvariantCulture) + (session.Catalog.HasMore(item.Path) ? " loaded (more on the server)" : string.Empty) : "Open the folder to list it"));
                break;
            default:
                var meta = session.Details.GetMetadata(item.Path);
                rows.Add(("Key", KeyOf(item)));
                rows.Add(("Size", meta is not null
                    ? $"{FileTypes.FormatSize(meta.ContentLength)} ({meta.ContentLength.ToString("N0", CultureInfo.InvariantCulture)} bytes)"
                    : item.Size is { } size ? $"{FileTypes.FormatSize(size)} ({size.ToString("N0", CultureInfo.InvariantCulture)} bytes)" : NotReported));
                rows.Add(("Last modified", FileTypes.FormatDate(meta?.LastModified ?? item.LastModified)));
                rows.Add(("Storage class", meta?.StorageClass ?? item.StorageClass ?? NotReported));
                rows.Add(("ETag", meta?.ETag ?? item.ETag ?? NotReported));
                if (meta is null)
                {
                    rows.Add(("Metadata", session.Details.StateMessage(item.Path, DetailsAspect.Metadata) ?? "—"));
                }
                else
                {
                    rows.Add(("Content-Type", meta.ContentType ?? NotReported));
                    rows.Add(("Version ID", meta.VersionId ?? "(none: versioning not enabled)"));
                    rows.Add(("Server-side encryption", meta.ServerSideEncryption ?? NotReported));
                    rows.Add(("User metadata", meta.UserMetadata.Count == 0 ? "none" : string.Join(", ", meta.UserMetadata.Select(h => h.Name))));
                }

                break;
        }

        rows.Add(("Connection", session.Profile.Name));
        return rows;
    }

    // ---------- Accounts menu ----------

    private ConnectionDialog CreateConnectionEditor(ConnectionProfile? original) => new(
        original,
        _connections.Connections.Where(c => c.Id != original?.Id).Select(c => c.Name),
        original is not null && _connections.HasStoredCredentials(original.Id),
        original is not null && _connections.HasStoredCredentials(original.Id) ? _connections.CredentialHint(original.Id) : null,
        _connections.TestAsync,
        _awsProfiles,
        ShowLocalHelp);

    private void AddConnection()
    {
        using var dialog = CreateConnectionEditor(null);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is not { } profile)
        {
            return;
        }

        ConnectionProfile saved;
        try
        {
            saved = _connections.Save(profile, dialog.ResultCredentials);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Prompts.Warn(this, "Save connection", "The connection could not be saved: " + SecretRedactor.Redact(ex.Message));
            return;
        }

        _activity.Write($"Saved connection \"{saved.Name}\" ({saved.Describe()}, {saved.AuthenticationLabel}).");
        if (Prompts.Confirm(this, "Connect", $"Connection \"{saved.Name}\" saved. Connect to it now?"))
        {
            _ = ConnectAsync(saved.Id);
        }
    }

    private void ManageConnections()
    {
        using var dialog = new ConnectionsDialog(_connections, () => _remote?.Profile.Id, CreateConnectionEditor, (title, message) => Prompts.Confirm(this, title, message));
        dialog.ShowDialog(this);
        switch (dialog.Action)
        {
            case ConnectionsDialogAction.Connect when dialog.SelectedId is { } id:
                _ = ConnectAsync(id);
                break;
            case ConnectionsDialogAction.Disconnect:
                Disconnect();
                break;
        }
    }

    private IReadOnlyList<ToolStripItem> AccountMenuItems()
    {
        var items = new List<ToolStripItem>();
        foreach (var connection in _connections.Connections)
        {
            var status = connection.Id == _remote?.Profile.Id ? "connected" : ConnectionStatusText.Label(_connections.StatusOf(connection.Id)).ToLowerInvariant();
            var item = new ToolStripMenuItem($"{connection.Name} — {connection.Describe()} ({status})") { Checked = connection.Id == _remote?.Profile.Id };
            var id = connection.Id;
            item.Click += (_, _) => _ = ConnectAsync(id);
            items.Add(item);
        }

        if (_connections.Connections.Count == 0)
        {
            items.Add(new ToolStripMenuItem("(no saved connections — Add New Account..)") { Enabled = false });
        }

        if (_remote is { } session)
        {
            var disconnect = new ToolStripMenuItem($"Disconnect from \"{session.Profile.Name}\"");
            disconnect.Click += (_, _) => Disconnect();
            items.Add(disconnect);
        }

        return items;
    }
}
