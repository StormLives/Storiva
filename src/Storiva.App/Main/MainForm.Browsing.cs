using System.Globalization;
using Storiva.App.Icons;
using Storiva.App.Theme;
using Storiva.Core.Commands;
using Storiva.Core.Listing;
using Storiva.Core.Navigation;
using Storiva.Core.Storage;

namespace Storiva.App.Main;

/// <summary>Row shown in the object list (presentation only).</summary>
internal sealed record ObjectRow(Image? Icon, string Name, string Type, string Size, string LastModified, string StorageClass, StorageItem Item);

internal sealed partial class MainForm
{
    private ItemQuery _query = ItemQuery.Default;
    private IReadOnlyList<StorageItem> _currentItems = [];
    private bool _syncingTree;
    private bool _suppressSearch;
    private bool _rebuildingLocation;
    private bool _simulateUserTreeSelection;

    private void WireEvents()
    {
        _navigator.Navigated += (_, _) => OnNavigated();
        _connections.Changed += (_, _) => RefreshAccountStatus();
        _bookmarks.Changed += OnBookmarksChanged;
        _theme.Changed += OnThemeChanged;

        _backButton.Click += (_, _) => Execute(CommandId.NavigateBack);
        _forwardButton.Click += (_, _) => Execute(CommandId.NavigateForward);
        _upButton.Click += (_, _) => Execute(CommandId.NavigateUp);
        _refreshButton.Click += (_, _) => Execute(CommandId.FileRefresh);
        foreach (var (filter, item) in _filterItems)
        {
            item.Click += (_, _) => SetFilter(filter);
        }

        _searchBox.TextChanged += (_, _) => ApplySearch();
        _searchBox.KeyDown += OnSearchKeyDown;

        _bookmarkButton.Click += (_, _) => Execute(CommandId.BookmarkToggle);
        _editPathButton.Click += (_, _) => Execute(CommandId.EditPath);
        _copyPathButton.Click += (_, _) => CopyLocation();
        _pathBox.KeyDown += OnPathBoxKeyDown;
        _pathBox.LostFocus += (_, _) => EndPathEdit();

        _tree.BeforeExpand += (_, e) => EnsureChildrenLoaded(e.Node!);
        _tree.AfterExpand += (_, e) => SetFolderImage(e.Node!, expanded: true);
        _tree.AfterCollapse += (_, e) => SetFolderImage(e.Node!, expanded: false);
        _tree.AfterSelect += OnTreeAfterSelect;
        _tree.BeforeSelect += (_, e) =>
        {
            // The native TreeView auto-selects its first node when it gets focus (Action = Unknown).
            // Block that so the tree never shows a bucket as selected while the list shows Home.
            if (!_syncingTree && !_simulateUserTreeSelection && e.Action == TreeViewAction.Unknown)
            {
                e.Cancel = true;
            }
        };
        _tree.NodeMouseClick += (_, e) => OnTreeNodeClick(e.Node, e.Button);

        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                OpenRow(e.RowIndex);
            }
        };
        _grid.KeyDown += OnGridKeyDown;
        _grid.ColumnHeaderMouseClick += OnGridHeaderClick;
        _grid.SelectionChanged += (_, _) =>
        {
            UpdateSelectionStatus();
            UpdateActionStates();
            BindDetails();
        };
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == 0 && RowAt(e.RowIndex) is { } row)
            {
                e.ToolTipText = row.Type;
            }
        };
        _grid.CellMouseDown += (_, e) =>
        {
            // Right-click selects the row under the pointer before the menu opens.
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !_grid.Rows[e.RowIndex].Selected)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
                _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[1];
            }
        };

        WireActionEvents();
        WireTaskEvents();
        WireDetailEvents();
    }

    // ---------- Navigation ----------

    private void OnNavigated()
    {
        if (_searchBox.Text.Length > 0)
        {
            // Search is scoped to one location: clear it when the location changes.
            _suppressSearch = true;
            try
            {
                _searchBox.Text = string.Empty;
            }
            finally
            {
                _suppressSearch = false;
            }
        }

        _query = _query with { SearchText = string.Empty };
        RefreshList(keepSelection: false);
        RebuildLocationBar();
        SyncTreeSelection(_navigator.Current);
        UpdateCommandStates();
        Text = _navigator.Current.IsRoot ? "Storiva" : $"Storiva — {_navigator.Current.ToDisplayString()}";
        SetStatus(!IsConnected ? "Not connected. Choose a connection in the Accounts menu."
            : _navigator.Current.IsRoot ? "Choose a bucket to browse its folders and files." : $"Showing {_navigator.Current.ToDisplayString()}");
    }

    private void UpdateCommandStates()
    {
        var context = BuildContext();
        _backButton.Enabled = CommandRules.IsEnabled(CommandId.NavigateBack, context);
        _forwardButton.Enabled = CommandRules.IsEnabled(CommandId.NavigateForward, context);
        _upButton.Enabled = CommandRules.IsEnabled(CommandId.NavigateUp, context);
        UpdateBookmarkButton();
        UpdateActionStates();
    }

    /// <summary>Opens a row: folders and buckets navigate; files show their Preview tab [H].</summary>
    private void OpenRow(int rowIndex)
    {
        if (RowAt(rowIndex) is not { } row)
        {
            return;
        }

        if (row.Item.IsContainer)
        {
            _navigator.NavigateTo(row.Item.Path);
        }
        else if (_options.OpenFilesInPreview)
        {
            SelectDetailsTab("Preview");
            SetStatus($"Previewing the start of \"{row.Name}\" as text. Use Download to save the file; Storiva never opens or runs it.");
        }
        else
        {
            SetStatus($"Use Download to save \"{row.Name}\" on this computer; Storiva never opens or runs files.");
        }
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter when !e.Alt && !e.Control:
                if (_grid.CurrentRow is { } current)
                {
                    OpenRow(current.Index);
                }

                e.Handled = e.SuppressKeyPress = true;
                break;
            case Keys.Back:
                _navigator.GoUp();
                e.Handled = e.SuppressKeyPress = true;
                break;
        }
    }

    // ---------- Object list ----------

    private void RefreshList(bool keepSelection)
    {
        var selectedNames = keepSelection
            ? _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => RowAt(r.Index)?.Item.Name).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];

        try
        {
            _currentItems = _catalog.GetChildren(_navigator.Current);
        }
        catch (DirectoryNotFoundException)
        {
            // The location vanished (e.g. after a reset or a delete): fall back to the root.
            _navigator.Reset();
            return;
        }

        BindRows(selectedNames);
        EnsureListed(_navigator.Current);
    }

    private void BindRows(IReadOnlySet<string> selectNames)
    {
        var small = LogicalToDeviceUnits(Metrics.IconSmall);
        var rows = _query.Apply(_currentItems)
            .Select(item => new ObjectRow(
                LucideIcons.Get(IconFor(item), small, item.IsExternal ? _palette.IconArchive : _palette.IconFor(FileTypes.Categorize(item))),
                item.IsExternal ? item.Name + "  (external, read-only)" : item.Name,
                FileTypes.Describe(item),
                FileTypes.FormatSize(item.Size),
                FileTypes.FormatDate(item.LastModified),
                item.StorageClass ?? string.Empty,
                item))
            .ToList();

        _grid.DataSource = rows;
        _grid.ClearSelection();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Height = _grid.RowTemplate.Height;
            if (selectNames.Contains(rows[row.Index].Item.Name))
            {
                row.Selected = true;
                _grid.CurrentCell = row.Cells[1];
            }
        }

        if (_grid.SelectedRows.Count == 0 && _grid.Rows.Count > 0)
        {
            _grid.CurrentCell = _grid.Rows[0].Cells[1];
            _grid.Rows[0].Selected = _grid.Focused;
        }

        UpdateSortGlyphs();
        UpdateItemStatus(rows.Count);
        UpdateSelectionStatus();
        UpdateActionStates();
        BindDetails();
    }

    private static string IconFor(StorageItem item) => FileTypes.Categorize(item) switch
    {
        FileCategory.Bucket => Lucide.Database,
        FileCategory.Folder => Lucide.Folder,
        FileCategory.Text => Lucide.FileText,
        FileCategory.Image => Lucide.FileImage,
        FileCategory.Archive => Lucide.FileArchive,
        FileCategory.Spreadsheet => Lucide.FileSpreadsheet,
        FileCategory.Code => Lucide.FileCode,
        FileCategory.Video => Lucide.FileVideo,
        FileCategory.Audio => Lucide.FileMusic,
        _ => Lucide.File,
    };

    private ObjectRow? RowAt(int index) =>
        _grid.DataSource is List<ObjectRow> rows && index >= 0 && index < rows.Count ? rows[index] : null;

    private List<ObjectRow> SelectedRows() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => RowAt(r.Index)).OfType<ObjectRow>().ToList();

    private void SelectRowsByName(IReadOnlySet<string> names)
    {
        _grid.ClearSelection();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (RowAt(row.Index) is { } r && names.Contains(r.Item.Name))
            {
                row.Selected = true;
                _grid.CurrentCell = row.Cells[1];
            }
        }
    }

    private void OnGridHeaderClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (_grid.Columns[e.ColumnIndex].Tag is ItemSortColumn column)
        {
            _query = _query.WithSortToggled(column);
            RefreshList(keepSelection: true);
            SetStatus($"Sorted by {_grid.Columns[e.ColumnIndex].HeaderText.ToLowerInvariant()}, {(_query.Direction == SortDirection.Ascending ? "ascending" : "descending")}. Folders stay above files.");
        }
    }

    private void UpdateSortGlyphs()
    {
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.HeaderCell.SortGlyphDirection = column.Tag is ItemSortColumn c && c == _query.SortColumn
                ? (_query.Direction == SortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending)
                : SortOrder.None;
        }
    }

    // ---------- Search and filter ----------

    private void ApplySearch()
    {
        if (_suppressSearch)
        {
            return;
        }

        _query = _query with { SearchText = _searchBox.Text };
        BindRows(new HashSet<string>());
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            ClearSearch();
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode is Keys.Enter or Keys.Down)
        {
            _grid.Focus();
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    private void FocusSearch()
    {
        _searchBox.Focus();
        _searchBox.SelectAll();
    }

    private void ClearSearch()
    {
        _searchBox.Text = string.Empty;
        if (_searchBox.Focused)
        {
            _grid.Focus();
        }
    }

    private void SetFilter(ItemTypeFilter filter)
    {
        _query = _query with { Filter = filter };
        foreach (var (key, item) in _filterItems)
        {
            item.Checked = key == filter;
        }

        _filterButton.ToolTipText = $"Filter: {FilterLabel(filter).ToLowerInvariant()} (click to change)";
        _filterButton.AccessibleDescription = FilterLabel(filter);
        BindRows(new HashSet<string>());
    }

    private void SelectAll()
    {
        _grid.Focus();
        _grid.SelectAll();
    }

    private void InvertSelection()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Selected = !row.Selected;
        }

        _grid.Focus();
    }

    // ---------- Path bar ----------

    private void RebuildLocationBar()
    {
        if (_rebuildingLocation)
        {
            return; // Removing the focused path box raises LostFocus, which would re-enter here.
        }

        _rebuildingLocation = true;
        try
        {
            RebuildLocationBarCore();
        }
        finally
        {
            _rebuildingLocation = false;
        }
    }

    private void RebuildLocationBarCore()
    {
        var small = LogicalToDeviceUnits(Metrics.IconSmall);
        _locationBar.SuspendLayout();
        _pathBox.Visible = false;
        _locationBar.Items.Clear();
        var segments = _navigator.Breadcrumb;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Count - 1;
            if (i > 0)
            {
                _locationBar.Items.Add(new ToolStripLabel
                {
                    Image = LucideIcons.Get(Lucide.ChevronRight, small, _palette.TextSecondary),
                    ImageScaling = ToolStripItemImageScaling.None,
                    DisplayStyle = ToolStripItemDisplayStyle.Image,
                    AccessibleRole = AccessibleRole.Separator,
                    AccessibleName = "separator",
                    Margin = new Padding(0),
                });
            }

            var button = new ToolStripButton(segment.Label)
            {
                Tag = segment.Path,
                Font = isLast ? StorivaFonts.UiBold : StorivaFonts.Ui,
                ToolTipText = isLast ? "Current location" : $"Go to {segment.Path.ToDisplayString()}",
                AccessibleName = i == 0 ? BrowserNavigator.HomeLabel : segment.Label,
                AccessibleDescription = isLast ? "Current location" : "Navigate to this location",
                ImageScaling = ToolStripItemImageScaling.None,
                Padding = new Padding(2, 0, 2, 0),
            };
            if (i == 0)
            {
                button.Image = LucideIcons.Get(Lucide.House, small, _palette.IconDefault);
                button.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            }

            button.Click += (_, _) => _navigator.NavigateTo(segment.Path);
            _locationBar.Items.Add(button);
        }

        _locationBar.Items.Add(_pathBox);
        // Right-aligned items are laid out right-to-left: copy, filter, edit, bookmark ([F E1] order ☆ ✎ … filter copy).
        _locationBar.Items.AddRange([_copyPathButton, _filterButton, _editPathButton, _bookmarkButton]);
        _locationBar.ResumeLayout();
        UpdateBookmarkButton();
    }

    private void UpdateBookmarkButton()
    {
        var current = _navigator.Current;
        var marked = !current.IsRoot && _bookmarks.Contains(current);
        _bookmarkButton.Enabled = !current.IsRoot;
        _bookmarkButton.Checked = marked;
        _bookmarkButton.ToolTipText = current.IsRoot ? "Open a bucket or folder to bookmark it" : marked ? "Remove this location from the bookmarks" : "Bookmark this location";
        _bookmarkButton.Image = LucideIcons.Get(Lucide.Star, LogicalToDeviceUnits(Metrics.IconSmall), marked ? _palette.Warning : _palette.IconDefault);
    }

    /// <summary>Switches the breadcrumb to an editable path field (reference: path field with edit button [F]).</summary>
    private void BeginPathEdit()
    {
        foreach (ToolStripItem item in _locationBar.Items)
        {
            if (item.Alignment == ToolStripItemAlignment.Left && item != _pathBox)
            {
                item.Visible = false;
            }
        }

        var reserved = _copyPathButton.Width + _filterButton.Width + _editPathButton.Width + _bookmarkButton.Width + LogicalToDeviceUnits(16);
        _pathBox.Width = Math.Max(LogicalToDeviceUnits(120), _locationBar.ClientSize.Width - reserved);
        _pathBox.Text = _navigator.Current.ToDisplayString();
        _pathBox.Visible = true;
        _pathBox.Focus();
        _pathBox.SelectAll();
    }

    private void EndPathEdit()
    {
        if (_pathBox.Visible)
        {
            RebuildLocationBar();
        }
    }

    private void OnPathBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = e.SuppressKeyPress = true;
            SubmitPath(_pathBox.Text);
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.Handled = e.SuppressKeyPress = true;
            EndPathEdit();
            _grid.Focus();
        }
    }

    private void SubmitPath(string text)
    {
        var target = StoragePath.Parse(text);
        if (!_catalog.Exists(target))
        {
            SetStatus(IsConnected ? $"\"{text.Trim()}\" is not a bucket or folder of this connection." : "Not connected.");
            return;
        }

        if (!_navigator.NavigateTo(target))
        {
            EndPathEdit();
        }

        _grid.Focus();
    }

    private void CopyLocation() =>
        CopyText(_navigator.Current.ToDisplayString(), $"Copied {_navigator.Current.ToDisplayString()} to the clipboard.");

    private void CopyText(string text, string status)
    {
        try
        {
            ClipboardWriter(text, false);
            SetStatus(status);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetStatus("The clipboard is in use by another application. Try again.");
        }
    }

    // ---------- Tree: buckets only by default; folders too with Options › Interface › "Show folders in the bucket pane" ----------

    private bool TreeShowsFolders => _options.ShowFoldersInTree;

    private void RebuildTree()
    {
        var expanded = new HashSet<StoragePath>();
        CollectExpanded(_tree.Nodes, expanded);

        _syncingTree = true;
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            AddChildNodes(_tree.Nodes, StoragePath.Root);
            foreach (var path in expanded.OrderBy(p => p.Segments.Count))
            {
                if (FindNode(path, load: true) is { } node && node.Tag is StoragePath found && found == path)
                {
                    node.Expand();
                }
            }
        }
        finally
        {
            _tree.EndUpdate();
            _syncingTree = false;
        }

        SyncTreeSelection(_navigator.Current);
    }

    private static void CollectExpanded(TreeNodeCollection nodes, HashSet<StoragePath> into)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.IsExpanded && node.Tag is StoragePath path)
            {
                into.Add(path);
                CollectExpanded(node.Nodes, into);
            }
        }
    }

    private void EnsureChildrenLoaded(TreeNode node)
    {
        if (!TreeShowsFolders || node.Tag is not StoragePath path || (node.Nodes.Count > 0 && node.Nodes[0].Tag is not null))
        {
            return;
        }

        if (!EnsureListed(path))
        {
            return; // remote location not listed yet: keep the "Loading…" placeholder until the page arrives
        }

        node.Nodes.Clear();
        if (_catalog.Exists(path))
        {
            AddChildNodes(node.Nodes, path);
        }
    }

    /// <summary>Re-reads the children of an existing tree node after its listing changed (remote pages, writes).</summary>
    private void RefreshTreeNode(StoragePath path)
    {
        if (path.IsRoot)
        {
            RebuildTree();
            return;
        }

        if (!TreeShowsFolders)
        {
            return; // bucket nodes have no children to refresh
        }

        if (FindNode(path, load: false) is not { } node || !path.Equals(node.Tag as StoragePath))
        {
            return;
        }

        _syncingTree = true;
        _tree.BeginUpdate();
        try
        {
            var expanded = node.IsExpanded;
            node.Nodes.Clear();
            AddChildNodes(node.Nodes, path);
            if (expanded && node.Nodes.Count > 0)
            {
                node.Expand();
            }
        }
        finally
        {
            _tree.EndUpdate();
            _syncingTree = false;
        }

        SyncTreeSelection(_navigator.Current);
    }

    private void AddChildNodes(TreeNodeCollection into, StoragePath path)
    {
        foreach (var child in _catalog.GetChildren(path).Where(c => c.IsContainer).OrderBy(c => c.Name, NaturalStringComparer.Instance))
        {
            var image = child.Kind == StorageItemKind.Bucket
                ? (child.IsExternal ? TreeImage.ExternalBucket : TreeImage.Bucket)
                : TreeImage.Folder;
            var childNode = new TreeNode(child.IsExternal ? child.Name + " (external)" : child.Name) { Tag = child.Path, ImageKey = image, SelectedImageKey = image };
            if (TreeShowsFolders && _catalog.MayHaveChildContainers(child.Path))
            {
                // Placeholder so the expand glyph shows; replaced on first expand.
                childNode.Nodes.Add(new TreeNode("Loading…") { ImageKey = TreeImage.Loading, SelectedImageKey = TreeImage.Loading });
            }

            into.Add(childNode);
        }
    }

    private static void SetFolderImage(TreeNode node, bool expanded)
    {
        if (node.ImageKey is TreeImage.Folder or TreeImage.FolderOpen)
        {
            node.ImageKey = node.SelectedImageKey = expanded ? TreeImage.FolderOpen : TreeImage.Folder;
        }
    }

    /// <summary>Deepest existing node on the way to <paramref name="path"/>; null for the root.</summary>
    private TreeNode? FindNode(StoragePath path, bool load)
    {
        TreeNode? node = null;
        var nodes = _tree.Nodes;
        var walk = StoragePath.Root;
        foreach (var segment in path.Segments)
        {
            walk = walk.Append(segment);
            if (node is not null && load && TreeShowsFolders)
            {
                EnsureChildrenLoaded(node);
                if (!node.IsExpanded)
                {
                    node.Expand();
                }
            }

            var next = nodes.Cast<TreeNode>().FirstOrDefault(n => walk.Equals(n.Tag as StoragePath));
            if (next is null)
            {
                return node;
            }

            node = next;
            nodes = next.Nodes;
        }

        return node;
    }

    private void SyncTreeSelection(StoragePath path)
    {
        var node = FindNode(path, load: true);
        if (_tree.SelectedNode == node)
        {
            return;
        }

        _syncingTree = true;
        try
        {
            _tree.SelectedNode = node;
            node?.EnsureVisible();
        }
        finally
        {
            _syncingTree = false;
        }
    }

    private void OnTreeNodeClick(TreeNode? node, MouseButtons button)
    {
        // Right-click selects the node first, so the context menu acts on it.
        if (button == MouseButtons.Right && node is not null)
        {
            _tree.SelectedNode = node;
        }

        // Inside a folder the tree highlights the folder's bucket; clicking that (already selected) bucket goes
        // back to the bucket's top level, like a click on the bucket in the breadcrumb.
        else if (button == MouseButtons.Left && node is not null && node == _tree.SelectedNode && node.Tag is StoragePath path && path != _navigator.Current)
        {
            _navigator.NavigateTo(path);
        }
    }

    private void OnTreeAfterSelect(object? sender, TreeViewEventArgs e)
    {
        // Only user selections navigate. The TreeView also selects its first node on its own
        // (Action = Unknown) when it is created or focused; that must not leave Home.
        var byUser = e.Action is TreeViewAction.ByMouse or TreeViewAction.ByKeyboard || _simulateUserTreeSelection;
        if (!_syncingTree && byUser && e.Node?.Tag is StoragePath path)
        {
            _navigator.NavigateTo(path);
        }
    }

    // ---------- Status ----------

    private void SetStatus(string message) => _statusMessage.Text = message;

    private void UpdateItemStatus(int shown)
    {
        var folders = _currentItems.Count(i => i.IsContainer);
        var files = _currentItems.Count - folders;
        var total = _currentItems.Where(i => i.Size is not null).Sum(i => i.Size!.Value);
        var summary = $"{folders} {(folders == 1 ? "folder" : "folders")}, {files} {(files == 1 ? "file" : "files")}";
        if (files > 0)
        {
            summary += $" ({FileTypes.FormatSize(total)})";
        }

        var more = IsConnected && _remote!.Catalog.HasMore(_navigator.Current);
        if (more)
        {
            summary += " loaded · more on the server";
        }

        _statusItems.Text = shown == _currentItems.Count ? summary : $"{shown} of {_currentItems.Count} shown · {summary}";
        if (_query.HasSearch)
        {
            var scope = more ? " (search covers the items loaded so far — Load more to include the rest)" : " in this location";
            SetStatus(shown == 0
                ? $"No loaded items match \"{_query.SearchText.Trim()}\"{scope}."
                : $"{shown} {(shown == 1 ? "item matches" : "items match")} \"{_query.SearchText.Trim()}\"{scope}.");
        }
    }

    private void UpdateSelectionStatus()
    {
        var selected = SelectedRows();
        if (selected.Count == 0)
        {
            _statusSelection.Text = "Nothing selected";
            return;
        }

        var bytes = selected.Where(r => r.Item.Size is not null).Sum(r => r.Item.Size!.Value);
        _statusSelection.Text = selected.Count == 1
            ? $"Selected: {selected[0].Item.Name}"
            : $"{selected.Count} selected" + (bytes > 0 ? $" ({FileTypes.FormatSize(bytes)})" : string.Empty);
    }

    private void OnBookmarksChanged(object? sender, EventArgs e) => UpdateBookmarkButton();

    /// <summary>Bookmarks belong to a connection: each one keeps its own (session-only) list.</summary>
    private void SwapBookmarks(BookmarkList next)
    {
        _bookmarks.Changed -= OnBookmarksChanged;
        _bookmarks = next;
        _bookmarks.Changed += OnBookmarksChanged;
        UpdateBookmarkButton();
    }

    private static string Now() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
