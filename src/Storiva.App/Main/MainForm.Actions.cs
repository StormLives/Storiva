using Storiva.App.Dialogs;
using Storiva.Core.Commands;
using Storiva.Core.Settings;
using Storiva.Core.Storage;

namespace Storiva.App.Main;

/// <summary>
/// Window-level commands: action-bar wiring, bookmarks, options, refresh and help. Operations on the server
/// (upload, download, delete, buckets…) live in MainForm.Remote.cs.
/// </summary>
internal sealed partial class MainForm
{
    private int _bucketCounter;

    private void WireActionEvents()
    {
        _uploadButton.ButtonClick += (_, _) => Execute(CommandId.FileUploadFiles);
        _uploadFileItem.Click += (_, _) => Execute(CommandId.FileUploadFiles);
        _uploadFolderItem.Click += (_, _) => Execute(CommandId.FileUploadFolders);
        _downloadButton.Click += (_, _) => Execute(CommandId.FileDownload);
        _openButton.Click += (_, _) => Execute(CommandId.FileOpen);
        _deleteButton.Click += (_, _) => Execute(CommandId.FileDelete);
        _newFolderButton.Click += (_, _) => Execute(CommandId.FileCreateFolder);
        _actionRefreshButton.Click += (_, _) => Execute(CommandId.FileRefresh);
        _newBucketButton.Click += (_, _) => Execute(CommandId.BucketCreate);
        _addExternalButton.Click += (_, _) => Execute(CommandId.BucketAddExternal);
        _bucketRefreshButton.Click += (_, _) => Execute(CommandId.BucketRefresh);
        WireRemoteEvents();
    }

    /// <summary>Toolbar and action-bar buttons follow the same rules as the menus.</summary>
    private void UpdateActionStates()
    {
        var c = BuildContext();
        _uploadButton.Enabled = CommandRules.IsEnabled(CommandId.FileUploadFiles, c);
        _downloadButton.Enabled = CommandRules.IsEnabled(CommandId.FileDownload, c);
        _openButton.Enabled = CommandRules.IsEnabled(CommandId.FileOpen, c);
        _deleteButton.Enabled = CommandRules.IsEnabled(CommandId.FileDelete, c);
        _newFolderButton.Enabled = CommandRules.IsEnabled(CommandId.FileCreateFolder, c);
        _newBucketButton.Enabled = CommandRules.IsEnabled(CommandId.BucketCreate, c);
        _addExternalButton.Enabled = CommandRules.IsEnabled(CommandId.BucketAddExternal, c);
        _bucketRefreshButton.Enabled = _refreshButton.Enabled = _actionRefreshButton.Enabled = CommandRules.IsEnabled(CommandId.BucketRefresh, c);
        _newFolderButton.ToolTipText = _newFolderButton.Enabled
            ? "Create a folder in the current location (Ctrl+N)"
            : IsConnected ? "Open a bucket first." : "Connect first (Accounts menu).";
    }

    private void OpenCurrent()
    {
        if (SelectedRows() is [var row])
        {
            OpenRow(_grid.Rows.Cast<DataGridViewRow>().First(r => RowAt(r.Index) == row).Index);
        }
    }

    private void ShowBucketTab(string tab)
    {
        if (BuildContext().ActiveBucket is { } bucket && _catalog.Find(bucket) is { } item)
        {
            BindDetails(TargetFor(item), force: false);
            SelectDetailsTab(tab);
            ScheduleDetailsLoad();
        }
    }

    internal string SuggestBucketName() =>
        BucketNameRules.SuggestName(++_bucketCounter, _catalog.GetChildren(StoragePath.Root).Select(b => b.Name));

    // ---------- Bookmarks, options ----------

    private void ToggleBookmark()
    {
        var path = _navigator.Current;
        try
        {
            var added = _bookmarks.Toggle(path);
            _activity.Write(added ? $"Bookmarked {path.ToDisplayString()}." : $"Removed bookmark {path.ToDisplayString()}.");
            SetStatus(added ? $"{path.ToDisplayString()} added to Bookmarks." : $"{path.ToDisplayString()} removed from Bookmarks.");
        }
        catch (InvalidOperationException ex)
        {
            Prompts.Warn(this, "Bookmarks", ex.Message);
        }
    }

    private void ClearBookmarks()
    {
        if (_bookmarks.Items.Count > 0 && Prompts.Confirm(this, "Bookmarks", $"Remove all {_bookmarks.Items.Count} bookmark(s)?"))
        {
            _bookmarks.Clear();
            _activity.Write("All bookmarks removed.");
        }
    }

    private void ShowOptions()
    {
        var windows = _theme.SystemPrefersDark switch
        {
            true => "Windows is currently set to dark apps.",
            false => "Windows is currently set to light apps.",
            _ => "The Windows app mode could not be read; System uses Light.",
        };
        using var dialog = new OptionsDialog(_options, _theme.Preference, windows + (_theme.PersistenceWarning is { } w ? " " + w : string.Empty));
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } result)
        {
            ApplyOptions(result);
            ApplyThemePreference(dialog.ThemeResult);
            _activity.Write("Options updated for this session.");
        }
    }

    internal void ApplyOptions(SessionOptions options)
    {
        var rowsChanged = options.CompactRows != _options.CompactRows;
        var treeChanged = options.ShowFoldersInTree != _options.ShowFoldersInTree;
        _options = options;
        _tasks.MaxConcurrent = options.MaxConcurrentTasks;
        _status.Visible = options.ShowStatusBar;
        _innerSplit.Panel1Collapsed = !options.ShowBucketPane;
        _activity.MinimumLevel = options.MinimumLogLevel;
        if (rowsChanged)
        {
            ApplyRowMetrics();
            RefreshList(keepSelection: true);
            RefreshTaskView();
        }

        if (treeChanged)
        {
            RebuildTree();
        }

        SetStatus("Options applied for this session.");
    }

    /// <summary>F5: lists the current location (or the buckets) again from the server.</summary>
    private void RefreshData(bool buckets)
    {
        if (!IsConnected)
        {
            return;
        }

        RefreshRemote(buckets);
        SetStatus($"Listing {(buckets || _navigator.Current.IsRoot ? "the buckets" : _navigator.Current.ToDisplayString())} again from the server…");
    }

    private void ShowAbout(bool focusNotices)
    {
        using var dialog = new AboutDialog(focusNotices);
        dialog.ShowDialog(this);
    }

    private void ShowLocalHelp(string text) => Prompts.Info(this, "Help", text);
}
