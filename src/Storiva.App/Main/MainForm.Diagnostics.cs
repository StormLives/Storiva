using Storiva.App.Details;
using Storiva.App.Theme;
using Storiva.Core.Commands;
using Storiva.Core.Listing;
using Storiva.Core.Storage;
using Storiva.Core.Tasks;

namespace Storiva.App.Main;

/// <summary>Read-only inspection and UI-level actions used by the smoke test.</summary>
internal sealed partial class MainForm
{
    internal StoragePath CurrentPath => _navigator.Current;

    internal IReadOnlyList<string> VisibleNames =>
        _grid.DataSource is List<ObjectRow> rows ? rows.Select(r => r.Item.Name).ToList() : [];

    internal string LocationText => string.Join(" > ",
        _locationBar.Items.OfType<ToolStripButton>().Where(b => b.Tag is StoragePath && b.Visible).Select(b => b.Text));

    internal StoragePath? TreeSelection => _tree.SelectedNode?.Tag as StoragePath;

    internal IReadOnlyList<string> TreeRootNames => _tree.Nodes.Cast<TreeNode>().Select(n => n.Text).ToList();

    /// <summary>All nodes of the tree, at every level (placeholders included).</summary>
    internal int TreeNodeCount => CountNodes(_tree.Nodes);

    /// <summary>A left click on the tree's selected node, through the real click handler.</summary>
    internal void ClickSelectedTreeNode() => OnTreeNodeClick(_tree.SelectedNode, MouseButtons.Left);

    private static int CountNodes(TreeNodeCollection nodes) => nodes.Cast<TreeNode>().Sum(n => 1 + CountNodes(n.Nodes));

    internal bool BackEnabled => _backButton.Enabled;

    internal bool UpEnabled => _upButton.Enabled;

    internal bool NewFolderEnabled => _newFolderButton.Enabled;

    internal string StatusText => _statusMessage.Text ?? string.Empty;

    internal int TaskRowCount => _taskGrid.Rows.Count;

    internal int TaskCount => _tasks.Tasks.Count;

    internal IReadOnlyList<string> MenuTitles => _menu.Items.Cast<ToolStripItem>().Select(i => i.Text!.Replace("&", string.Empty)).ToList();

    internal IReadOnlyList<string> BottomTabTitles =>
        _bottomTabs.TabPages.Cast<TabPage>().Select(p => p.Text.Split(" (")[0]).ToList();

    internal string TasksTabTitle => _bottomTabs.TabPages[0].Text;

    internal IReadOnlyList<string> ActionBarLabels => _actionBar.Items.Cast<ToolStripItem>().Where(i => i.Available).Select(i => i.Text ?? string.Empty).ToList();

    internal IReadOnlyList<string> BucketBarLabels => _bucketBar.Items.Cast<ToolStripItem>().Select(i => i.Text ?? string.Empty).ToList();

    internal IReadOnlyList<string> TaskFooterLabels => _taskBar.Items.Cast<ToolStripItem>()
        .Where(i => i.Alignment == ToolStripItemAlignment.Left).Select(i => i.Text ?? string.Empty).ToList();

    internal IReadOnlyList<string> TaskColumns => _taskGrid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToList();

    internal IReadOnlyList<string> ObjectColumns => _grid.Columns.Cast<DataGridViewColumn>().Skip(1).Select(c => c.HeaderText).ToList();

    internal (int Actual, int Expected) RowHeight => (_grid.RowTemplate.Height, LogicalToDeviceUnits(RowHeightLogical));

    internal IReadOnlyList<string> EventLogLines => _activityGrid.Rows.Cast<DataGridViewRow>()
        .Select(r => $"{r.Cells[0].Value} {r.Cells[1].Value}").ToList();

    internal PermissionsTab Permissions => _permissionsTab;

    internal KeyValueTab Headers => _headersTab;

    internal KeyValueTab Tags => _tagsTab;

    internal PreviewTab Preview => _previewTab;

    internal VersionsTab Versions => _versionsTab;

    internal string? PropertyValue(string name)
    {
        _propertiesTab.Bind(_detailsTarget);
        return _propertiesTab.ValueOf(name);
    }

    /// <summary>Vertical order check: path bar above the list, action bar below it, footer under the tabs.</summary>
    internal (Rectangle Path, Rectangle List, Rectangle Actions, Rectangle Tabs, Rectangle Footer, Rectangle Buckets) RegionBounds =>
        (RegionOf(_locationBar), RegionOf(_grid), RegionOf(_actionBar), RegionOf(_bottomTabs), RegionOf(_taskBar), RegionOf(_bucketBar));

    private Rectangle RegionOf(Control c) => RectangleToClient(c.RectangleToScreen(c.ClientRectangle));

    /// <summary>Enabled state of a menu-bar item after the menu refreshed it (as when the user opens the menu).</summary>
    internal bool MenuItemEnabled(CommandId id)
    {
        foreach (ToolStripMenuItem top in _menu.Items)
        {
            _menuBuilder.Refresh(top.DropDownItems);
        }

        return _menuBuilder.Items.TryGetValue(id, out var items) && items.Count > 0 && items[0].Enabled;
    }

    internal IReadOnlyList<string> MenuItemTexts(string menuTitle)
    {
        var top = _menu.Items.Cast<ToolStripMenuItem>().First(i => i.Text!.Replace("&", string.Empty) == menuTitle);
        _menuBuilder.Refresh(top.DropDownItems);
        return top.DropDownItems.Cast<ToolStripItem>().Where(i => i is not ToolStripSeparator).Select(i => i.Text ?? string.Empty).ToList();
    }

    // Focus() fails while another application is in the foreground; the window then remembers the control instead.
    internal void FocusList()
    {
        if (!_grid.Focus())
        {
            ActiveControl = _grid;
        }
    }

    internal void FocusTree()
    {
        if (!_tree.Focus())
        {
            ActiveControl = _tree;
        }
    }

    /// <summary>Simulates a key press through the same routing as real keyboard input.</summary>
    internal bool PressShortcut(Keys keys)
    {
        var shortcut = ToShortcut(keys);
        var context = BuildContext();
        if (shortcut is not null && ShortcutRouter.Resolve(shortcut, context.Focus) is { } id && CommandRules.IsEnabled(id, context))
        {
            return Execute(id);
        }

        return false;
    }

    internal CommandId? ResolveShortcut(Keys keys) =>
        ToShortcut(keys) is { } s ? ShortcutRouter.Resolve(s, BuildContext().Focus) : null;

    /// <summary>Opens a row exactly like a double-click does.</summary>
    internal void OpenVisibleItem(string name)
    {
        var index = VisibleNames.ToList().IndexOf(name);
        if (index < 0)
        {
            throw new InvalidOperationException($"'{name}' is not visible.");
        }

        OpenRow(index);
    }

    internal void SelectVisibleItem(string name)
    {
        var index = VisibleNames.ToList().IndexOf(name);
        if (index < 0)
        {
            throw new InvalidOperationException($"'{name}' is not visible.");
        }

        _grid.ClearSelection();
        _grid.Rows[index].Selected = true;
        _grid.CurrentCell = _grid.Rows[index].Cells[1];
    }

    internal void ClearListSelection() => _grid.ClearSelection();

    internal void ClickBack() => _backButton.PerformClick();

    internal void ClickUp() => _upButton.PerformClick();

    internal void ClickUpload() => _uploadButton.PerformButtonClick();

    internal void ClickDownload() => _downloadButton.PerformClick();

    internal void TypeSearch(string text) => _searchBox.Text = text;

    internal void ChooseFilter(ItemTypeFilter filter) => _filterItems[filter].PerformClick();

    internal void SubmitPathText(string text)
    {
        BeginPathEdit();
        _pathBox.Text = text;
        SubmitPath(text);
    }

    internal void SelectBottomTab(string title) => SelectDetailsTab(title);

    internal string SelectedBottomTab => _bottomTabs.SelectedTab?.Name ?? string.Empty;

    internal void ClickColumnHeader(ItemSortColumn column)
    {
        var index = _grid.Columns.Cast<DataGridViewColumn>().First(c => c.Tag is ItemSortColumn s && s == column).Index;
        OnGridHeaderClick(_grid, new DataGridViewCellMouseEventArgs(index, -1, 0, 0, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)));
    }

    /// <summary>Selects a tree node the way a user click would (AfterSelect with a user action).</summary>
    internal void SelectInTree(StoragePath path)
    {
        var node = FindNode(path, load: true) ?? throw new InvalidOperationException($"No tree node for {path}.");
        _simulateUserTreeSelection = true;
        try
        {
            _tree.SelectedNode = node;
        }
        finally
        {
            _simulateUserTreeSelection = false;
        }
    }

    internal bool IsBookmarked(StoragePath path) => _bookmarks.Contains(path);

    internal string? StorageClassOf(string name) => _currentItems.FirstOrDefault(i => i.Name == name)?.StorageClass;

    /// <summary>Opens a menu-bar drop-down (refreshing its state) and returns it for capture.</summary>
    internal ToolStripDropDown OpenMenu(string menuTitle)
    {
        var top = _menu.Items.Cast<ToolStripMenuItem>().First(i => i.Text!.Replace("&", string.Empty) == menuTitle);
        top.ShowDropDown();
        return top.DropDown;
    }

    internal void CloseMenus()
    {
        foreach (ToolStripMenuItem top in _menu.Items)
        {
            top.HideDropDown();
        }
    }

    internal int MaxConcurrentTasks => _tasks.MaxConcurrent;

    internal void ChooseTaskFilter(TaskFilter filter) => _taskFilterButtons[filter].PerformClick();

    // ---------- themes ----------

    internal Color GridBackColor => _grid.BackgroundColor;

    internal Color GridForeColor => _grid.DefaultCellStyle.ForeColor;

    internal Color TreeBackColor => _tree.BackColor;

    internal bool TabsCustomPainted => _bottomTabs.IsCustomPainted;

    /// <summary>Background the renderer actually paints for the menu, toolbar and status bar.</summary>
    internal IReadOnlyList<Color> ChromeColors => new ToolStrip[] { _menu, _toolbar, _status }
        .Select(s => s.BackColor == SystemColors.Control ? (s is MenuStrip or StatusStrip ? _palette.Chrome : _palette.Toolbar) : s.BackColor)
        .ToList();

    // ---------- real connections ----------

    internal IReadOnlyList<TransferTask> RealTasks => _tasks.Tasks.ToList();

    internal bool LoadMoreVisible => _loadMoreButton.Available;

    internal void ClickLoadMore() => _loadMoreButton.PerformClick();

    internal bool IsListing => _listingInProgress.Count > 0;

    internal string ConnectionLabel => _statusConnection.Text ?? string.Empty;

    internal void DisconnectFromServer() => Disconnect();

    /// <summary>Whether the "Not connected" panel is shown, and its saved-connection links.</summary>
    internal (bool Shown, IReadOnlyList<string> Links) EmptyState =>
        (_emptyStateShown, _emptyConnections.Controls.OfType<LinkLabel>().Select(l => l.Text).ToList());

    internal bool ObjectListShown => _grid.Visible;

    internal TextBox SearchTextBox => _searchBox.TextBox;

    internal StorageItem VisibleItem(string name) => _currentItems.First(i => i.Name == name);

    /// <summary>Same call as the task context menu's Cancel.</summary>
    internal bool CancelTask(int id) => _tasks.Cancel(id);

    internal IReadOnlyList<string> AccountMenuTexts => AccountMenuItems().Select(i => i.Text ?? string.Empty).ToList();

    internal Bitmap CaptureWindow()
    {
        var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        return bitmap;
    }
}
