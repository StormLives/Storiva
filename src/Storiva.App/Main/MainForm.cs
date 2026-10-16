using Storiva.App.Commands;
using Storiva.App.Icons;
using Storiva.App.Theme;
using Storiva.Core.Activity;
using Storiva.Core.Commands;
using Storiva.Core.Connections;
using Storiva.Core.Details;
using Storiva.Core.Listing;
using Storiva.Core.Navigation;
using Storiva.Core.Remote;
using Storiva.Core.Settings;
using Storiva.Core.Storage;
using Storiva.Core.Tasks;

namespace Storiva.App.Main;

/// <summary>
/// Main window. Composition follows the reference layout [F]:
/// <code>
/// menu (Accounts · Buckets · Files · Bookmarks · Tools · Help)          ← built from CommandCatalog
/// toolbar (navigation + search)                                         ← Storiva addition
/// ┌ bucket bar ───────┬ path bar (breadcrumb, ☆, ✎, filter, copy) ─┐
/// │ bucket tree       │ object list                                 │
/// │                   ├ action bar (Upload … Refresh) ──────────────┤
/// ├───────────────────┴─────────────────────────────────────────────┤
/// │ tabs: Tasks · Permissions · Headers · Tags · Properties · Preview · Versions · Event log │
/// ├ task filters (Running … All)                Start/Stop/Cancel All ┤
/// status bar                                                            ← Storiva addition
/// </code>
/// This file builds the controls; behaviour lives in the other partial files. The data source is either the
/// active connection (MainForm.Remote.cs); without one the window shows an empty state that leads to the Accounts menu.
/// </summary>
internal sealed partial class MainForm : Form
{
    private static StorivaPalette _palette => StorivaPalette.Current;
    private readonly StorivaToolStripRenderer _renderer;
    private readonly SwitchableStorageCatalog _catalog;
    private readonly SwitchableDetailsStore _details;
    private readonly BrowserNavigator _navigator;
    private readonly TaskQueue _tasks;
    private readonly OperationRunner _runner;
    private readonly ConnectionManager _connections;
    private readonly IThemeService _theme;
    private readonly Func<IReadOnlyList<string>>? _awsProfiles;
    private readonly ActivityLog _activity;
    private readonly BookmarkList _noConnectionBookmarks = new();
    private BookmarkList _bookmarks;
    private readonly MenuBuilder _menuBuilder;
    private SessionOptions _options = SessionOptions.Default;

    // Menu (generated)
    private readonly MenuStrip _menu = new();

    // Main toolbar (navigation and search)
    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripButton _backButton = new();
    private readonly ToolStripButton _forwardButton = new();
    private readonly ToolStripButton _upButton = new();
    private readonly ToolStripButton _refreshButton = new("Refresh");
    private readonly ToolStripTextBox _searchBox = new();
    private readonly ToolStripLabel _searchIcon = new();

    // Bucket pane [F E1: New bucket · Add external bucket · Refresh]
    private readonly ToolStrip _bucketBar = new();
    private readonly ToolStripButton _newBucketButton = new("New bucket");
    private readonly ToolStripButton _addExternalButton = new("Add external bucket");
    private readonly ToolStripButton _bucketRefreshButton = new("Refresh");
    private readonly TreeView _tree = new();
    private readonly ImageList _treeImages = new() { ColorDepth = ColorDepth.Depth32Bit };
    private ContextMenuStrip? _treeMenu;

    // Path bar [F E1: path · ☆ · ✎ · view · filter · copy]
    private readonly ToolStrip _locationBar = new();
    private readonly ToolStripTextBox _pathBox = new();
    private readonly ToolStripButton _bookmarkButton = new();
    private readonly ToolStripButton _editPathButton = new();
    private readonly ToolStripButton _copyPathButton = new();
    private readonly ToolStripDropDownButton _filterButton = new();
    private readonly Dictionary<ItemTypeFilter, ToolStripMenuItem> _filterItems = new();

    // Object list + action bar
    private readonly DataGridView _grid = new();
    private ContextMenuStrip? _gridMenu;
    private readonly ToolStrip _actionBar = new();
    private readonly ToolStripSplitButton _uploadButton = new("Upload");
    private readonly ToolStripMenuItem _uploadFileItem = new("Upload file(s)");
    private readonly ToolStripMenuItem _uploadFolderItem = new("Upload folder(s)");
    private readonly ToolStripButton _downloadButton = new("Download");
    private readonly ToolStripButton _openButton = new("Open");
    private readonly ToolStripButton _deleteButton = new("Delete");
    private readonly ToolStripButton _newFolderButton = new("New Folder");
    private readonly ToolStripButton _actionRefreshButton = new("Refresh");
    private readonly ToolStripButton _loadMoreButton = new("Load more") { Visible = false };

    // Panes
    private readonly SplitContainer _outerSplit = new();
    private readonly SplitContainer _innerSplit = new();

    // Bottom panel
    private readonly StorivaTabControl _bottomTabs = new();
    private readonly DataGridView _taskGrid = new();
    private readonly ContextMenuStrip _taskMenu = new();
    private readonly ToolStrip _taskBar = new();
    private readonly DataGridView _activityGrid = new();

    // Status bar
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusMessage = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusItems = new();
    private readonly ToolStripStatusLabel _statusSelection = new();
    private readonly ToolStripStatusLabel _statusAccount = new();
    private readonly ToolStripStatusLabel _statusConnection = new("Not connected");

    // Shown over the object list while no connection is open.
    private readonly TableLayoutPanel _emptyState = new();
    private readonly FlowLayoutPanel _emptyConnections = new();

    private bool _layoutInitialized;

    /// <param name="awsProfiles">Lists AWS profile names for the connection dialog (names only), or null.</param>
    public MainForm(
        TaskQueue tasks,
        ConnectionManager connections,
        ActivityLog activity,
        IThemeService theme,
        Func<IReadOnlyList<string>>? awsProfiles = null)
    {
        _catalog = new SwitchableStorageCatalog(EmptyStorageCatalog.Instance);
        _details = new SwitchableDetailsStore(EmptyDetailsStore.Instance);
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _bookmarks = _noConnectionBookmarks;
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _awsProfiles = awsProfiles;
        _navigator = new BrowserNavigator(_catalog);
        _renderer = new StorivaToolStripRenderer();
        _menuBuilder = new MenuBuilder(IsCommandEnabled, IsCommandChecked, id => Execute(id), DynamicMenuItems);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = StorivaFonts.Ui;
        Text = "Storiva";
        Icon = BrandIcon.Create();
        BackColor = _palette.Chrome;
        ForeColor = _palette.TextPrimary;
        ClientSize = new Size(Metrics.InitialWindowWidth, Metrics.InitialWindowHeight);
        MinimumSize = new Size(Metrics.MinimumWindowWidth, Metrics.MinimumWindowHeight);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildMenu();
        BuildToolbar();
        BuildPanes();
        BuildBottomPanel();
        BuildStatusBar();

        // Dock order: the last added top-docked control sits highest, so add bottom-up.
        Controls.Add(_outerSplit);
        Controls.Add(_toolbar);
        Controls.Add(_status);
        Controls.Add(_menu);
        MainMenuStrip = _menu;
        ResumeLayout(performLayout: true);

        // Created on the UI thread: task state changes of real operations are applied on this thread.
        _runner = new OperationRunner(_tasks);
        WireEvents();
    }

    /// <summary>Confirmations and notices; replaced by a recorder in the smoke test.</summary>
    internal IUserPrompts Prompts { get; set; } = new MessageBoxPrompts();

    private void BuildMenu()
    {
        ControlStyling.ApplyToolStrip(_menu, _palette, _renderer);
        _menu.Dock = DockStyle.Top;
        _menu.AccessibleName = "Main menu";
        _menu.Padding = new Padding(Metrics.SpaceXs, 1, 0, 1);
        _menuBuilder.BuildMenuBar(_menu);
    }

    private void BuildToolbar()
    {
        ControlStyling.ApplyToolStrip(_toolbar, _palette, _renderer);
        _toolbar.Dock = DockStyle.Top;
        _toolbar.AccessibleName = "Navigation toolbar";
        _toolbar.Padding = new Padding(Metrics.SpaceXs, 1, Metrics.SpaceXs, 1);
        _toolbar.TabStop = true;

        ConfigureButton(_backButton, "Back (Alt+Left)", "Back", textVisible: false);
        ConfigureButton(_forwardButton, "Forward (Alt+Right)", "Forward", textVisible: false);
        ConfigureButton(_upButton, "Up one level (Alt+Up)", "Up one level", textVisible: false);
        ConfigureButton(_refreshButton, "List this location again from the server (F5)", "Refresh", textVisible: true);

        _searchBox.AutoSize = false;
        _searchBox.Width = 220;
        _searchBox.Alignment = ToolStripItemAlignment.Right;
        _searchBox.ToolTipText = "Search by name in the current location (Ctrl+F). Esc clears.";
        _searchBox.AccessibleName = "Search by name";
        _searchBox.TextBox.PlaceholderText = "Search this location";
        _searchBox.BorderStyle = BorderStyle.Fixed3D; // frame painted flat by InputBorders
        _searchIcon.Alignment = ToolStripItemAlignment.Right;
        _searchIcon.AccessibleName = "Search";
        _searchIcon.ImageScaling = ToolStripItemImageScaling.None;
        _searchIcon.Margin = new Padding(Metrics.SpaceS, 1, 2, 2);

        // Right-aligned items are laid out right-to-left: search box, then its icon.
        _toolbar.Items.AddRange([_backButton, _forwardButton, _upButton, new ToolStripSeparator(), _refreshButton, _searchBox, _searchIcon]);
    }

    private static void ConfigureButton(ToolStripItem button, string tooltip, string accessibleName, bool textVisible)
    {
        button.ToolTipText = tooltip;
        button.AccessibleName = accessibleName;
        button.DisplayStyle = textVisible ? ToolStripItemDisplayStyle.ImageAndText : ToolStripItemDisplayStyle.Image;
        button.ImageScaling = ToolStripItemImageScaling.None;
        button.Padding = new Padding(2, 1, 2, 1);
    }

    private void BuildPanes()
    {
        _outerSplit.Dock = DockStyle.Fill;
        _outerSplit.Orientation = Orientation.Horizontal;
        _outerSplit.FixedPanel = FixedPanel.Panel2;
        _outerSplit.SplitterWidth = Metrics.SplitterWidth;
        _outerSplit.BackColor = _palette.Chrome;
        _outerSplit.TabStop = false;
        _outerSplit.Paint += PaintSplitterGrip;
        _outerSplit.SizeChanged += (_, _) => KeepTaskPanelProportional();

        _innerSplit.Dock = DockStyle.Fill;
        _innerSplit.Orientation = Orientation.Vertical;
        _innerSplit.FixedPanel = FixedPanel.Panel1;
        _innerSplit.SplitterWidth = Metrics.SplitterWidth;
        _innerSplit.BackColor = _palette.Chrome;
        _innerSplit.TabStop = false;
        _innerSplit.Paint += PaintSplitterGrip;
        // Min sizes are applied in ApplySplitLayout(): a SplitContainer starts at 150×100 px and
        // throws if Panel2MinSize does not fit its current (not yet laid out) size.

        BuildBucketPane();
        BuildObjectPane();

        _outerSplit.Panel1.Controls.Add(_innerSplit);
        _outerSplit.Panel1.Padding = new Padding(Metrics.OuterMargin, Metrics.OuterMargin, Metrics.OuterMargin, 0);
        _outerSplit.Panel2.Padding = new Padding(Metrics.OuterMargin, 0, Metrics.OuterMargin, 0);
    }

    private void BuildBucketPane()
    {
        ControlStyling.ApplyToolStrip(_bucketBar, _palette, _renderer);
        _bucketBar.Dock = DockStyle.Top;
        _bucketBar.AutoSize = false;
        _bucketBar.Height = Metrics.StripHeight;
        _bucketBar.AccessibleName = "Bucket actions";
        _bucketBar.Padding = new Padding(1, 0, 1, 0);
        ConfigureButton(_newBucketButton, "Create a bucket on the server (Ctrl+N in the bucket pane)", "New bucket", textVisible: true);
        ConfigureButton(_addExternalButton, "Open a bucket by name that is not in the bucket list (Ctrl+E)", "Add external bucket", textVisible: true);
        ConfigureButton(_bucketRefreshButton, "Refresh the bucket list (F5)", "Refresh buckets", textVisible: true);
        _bucketBar.Items.AddRange([_newBucketButton, _addExternalButton, _bucketRefreshButton]);

        ControlStyling.ApplyTree(_tree, _palette);
        _tree.Dock = DockStyle.Fill;
        _tree.ImageList = _treeImages;
        _tree.AccessibleName = "Buckets and folders";
        _tree.TabIndex = 0;
        ConfigureTreeDrawing();
        _treeMenu = _menuBuilder.BuildContextMenu(
        [
            CommandId.BucketCreate, CommandId.BucketDelete, CommandId.BucketRefresh, null,
            CommandId.BucketAddExternal, null,
            CommandId.BucketEditAcl, CommandId.BucketEditTags, CommandId.BucketEditVersioning, null,
            CommandId.BucketDownloadAllTo, null, CommandId.BucketProperties,
        ], _renderer);
        _tree.ContextMenuStrip = _treeMenu;

        _innerSplit.Panel1.Controls.Add(Framed(_tree));
        _innerSplit.Panel1.Controls.Add(_bucketBar);
    }

    private void BuildObjectPane()
    {
        ControlStyling.ApplyToolStrip(_locationBar, _palette, _renderer);
        _locationBar.Dock = DockStyle.Top;
        _locationBar.AutoSize = false;
        _locationBar.Height = Metrics.StripHeight;
        _locationBar.AccessibleName = "Current location";
        _locationBar.AccessibleRole = AccessibleRole.ToolBar;
        _locationBar.Padding = new Padding(1, 0, 1, 0);
        _locationBar.TabStop = true;
        _locationBar.CanOverflow = true;

        _pathBox.AutoSize = false;
        _pathBox.Visible = false;
        _pathBox.BorderStyle = BorderStyle.Fixed3D; // frame painted flat by InputBorders
        _pathBox.AccessibleName = "Location path";
        _pathBox.ToolTipText = "Type a path such as /my-bucket/folder/ and press Enter. Esc cancels.";
        ConfigureButton(_bookmarkButton, "Bookmark this location", "Bookmark", textVisible: false);
        ConfigureButton(_editPathButton, "Edit location (Alt+D)", "Edit location", textVisible: false);
        ConfigureButton(_copyPathButton, "Copy location", "Copy location", textVisible: false);
        ConfigureButton(_filterButton, "Show all items, folders only or files only", "Filter items", textVisible: false);
        foreach (var item in new ToolStripItem[] { _bookmarkButton, _editPathButton, _copyPathButton, _filterButton })
        {
            item.Alignment = ToolStripItemAlignment.Right;
        }

        foreach (var filter in Enum.GetValues<ItemTypeFilter>())
        {
            var item = new ToolStripMenuItem(FilterLabel(filter)) { Tag = filter };
            _filterButton.DropDownItems.Add(item);
            _filterItems[filter] = item;
        }

        ControlStyling.ApplyGrid(_grid, _palette);
        _grid.Dock = DockStyle.Fill;
        _grid.AccessibleName = "Items in the current location";
        _grid.TabIndex = 1;
        BuildGridColumns();
        _gridMenu = _menuBuilder.BuildContextMenu(
        [
            CommandId.FileOpen, null,
            CommandId.FileDownload, CommandId.FileUploadFiles, null,
            CommandId.FileCut, CommandId.FileCopy, CommandId.FilePaste, null,
            CommandId.FileRename, CommandId.FileDelete, null,
            CommandId.FileCreateFolder, CommandId.FileRefresh, null,
            CommandId.FileEditAcl, CommandId.FileEditHeaders, CommandId.FileEditTags, null,
            CommandId.FileProperties,
        ], _renderer);
        _grid.ContextMenuStrip = _gridMenu;

        BuildActionBar();

        // Fill first, then the edge-docked strips (later-added controls dock outermost).
        var listFrame = Framed(_grid);
        BuildEmptyState();
        listFrame.Controls.Add(_emptyState);
        _emptyState.BringToFront();
        _innerSplit.Panel2.Controls.Add(listFrame);
        _innerSplit.Panel2.Controls.Add(_locationBar);
        _innerSplit.Panel2.Controls.Add(_actionBar);
    }

    private void BuildActionBar()
    {
        ControlStyling.ApplyToolStrip(_actionBar, _palette, _renderer);
        _actionBar.Dock = DockStyle.Bottom;
        _actionBar.AccessibleName = "Object actions";
        _actionBar.Padding = new Padding(1, 2, 1, 1);
        _actionBar.TabStop = true;
        _actionBar.BackColor = _palette.Chrome;

        _uploadButton.DropDownItems.AddRange([_uploadFileItem, _uploadFolderItem]);
        _uploadFileItem.ShortcutKeyDisplayString = "Ctrl+U";
        _uploadFolderItem.ShortcutKeyDisplayString = "Ctrl+Shift+U";
        ConfigureButton(_uploadButton, "Upload local files into this location on the server (Ctrl+U)", "Upload", textVisible: true);
        ConfigureButton(_downloadButton, "Download the selected files or folders to this computer (Ctrl+D)", "Download", textVisible: true);
        ConfigureButton(_openButton, "Open the selected item (Enter)", "Open", textVisible: true);
        ConfigureButton(_deleteButton, "Delete the selected items on the server (Del). Always asks first.", "Delete", textVisible: true);
        ConfigureButton(_newFolderButton, "Create a folder in the current location (Ctrl+N)", "New Folder", textVisible: true);
        ConfigureButton(_actionRefreshButton, "List this location again from the server (F5)", "Refresh", textVisible: true);
        ConfigureButton(_loadMoreButton, "Load the next page of this location (the server returned only part of it)", "Load more", textVisible: true);
        _loadMoreButton.Alignment = ToolStripItemAlignment.Right;
        foreach (var item in new ToolStripItem[] { _uploadButton, _downloadButton, _openButton, _deleteButton, _newFolderButton, _actionRefreshButton, _loadMoreButton })
        {
            item.Padding = new Padding(Metrics.SpaceXs, 1, Metrics.SpaceXs, 1);
            item.Margin = new Padding(0, 0, 2, 0);
        }

        _actionBar.Items.AddRange([_uploadButton, _downloadButton, _openButton, _deleteButton, _newFolderButton, _actionRefreshButton, _loadMoreButton]);
    }

    /// <summary>1 px border around a pane (WinForms borders cannot be coloured directly).</summary>
    private Panel Framed(Control inner)
    {
        var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = _palette.BorderStrong };
        frame.Controls.Add(inner);
        return frame;
    }

    private void BuildGridColumns()
    {
        _grid.Columns.Add(new DataGridViewImageColumn
        {
            Name = "Icon",
            DataPropertyName = nameof(ObjectRow.Icon),
            HeaderText = string.Empty,
            Width = 24,
            Resizable = DataGridViewTriState.False,
            ImageLayout = DataGridViewImageCellLayout.Normal,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, NullValue = null, Padding = new Padding(0) },
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        // Column order and widths follow the reference list (Name · Size · Type · Last Modified · Storage Class) [F].
        AddTextColumn("Name", nameof(ObjectRow.Name), 240, ItemSortColumn.Name, fill: true);
        AddTextColumn("Size", nameof(ObjectRow.Size), 90, ItemSortColumn.Size, rightAlign: true);
        AddTextColumn("Type", nameof(ObjectRow.Type), 120, ItemSortColumn.Type);
        AddTextColumn("Last Modified", nameof(ObjectRow.LastModified), 145, ItemSortColumn.LastModified);
        AddTextColumn("Storage Class", nameof(ObjectRow.StorageClass), 110, ItemSortColumn.StorageClass);
    }

    private void AddTextColumn(string header, string property, int width, ItemSortColumn sort, bool fill = false, bool rightAlign = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = property,
            HeaderText = header,
            DataPropertyName = property,
            Width = width,
            MinimumWidth = fill ? 140 : 60,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            Tag = sort,
        };
        if (fill)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.FillWeight = 100;
        }

        if (rightAlign)
        {
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }

        _grid.Columns.Add(column);
    }

    private void BuildStatusBar()
    {
        ControlStyling.ApplyToolStrip(_status, _palette, _renderer);
        _status.Dock = DockStyle.Bottom;
        _status.SizingGrip = true;
        _status.AccessibleName = "Status bar";
        _status.Padding = new Padding(Metrics.OuterMargin, 0, 0, 0);
        _statusMessage.AccessibleName = "Status message";
        _statusItems.AccessibleName = "Item count";
        _statusSelection.AccessibleName = "Selection";
        _statusAccount.AccessibleName = "Account";
        _statusConnection.AccessibleName = "Connection state";
        _statusConnection.ImageScaling = ToolStripItemImageScaling.None;
        foreach (var label in new[] { _statusItems, _statusSelection, _statusAccount, _statusConnection })
        {
            label.BorderSides = ToolStripStatusLabelBorderSides.Left;
            label.BorderStyle = Border3DStyle.Etched;
            label.Padding = new Padding(Metrics.SpaceXs, 0, Metrics.SpaceXs, 0);
        }

        _status.Items.AddRange([_statusMessage, _statusItems, _statusSelection, _statusConnection, _statusAccount]);
    }

    /// <summary>Dotted grip in the middle of each splitter, so the reader sees it can be dragged.</summary>
    private void PaintSplitterGrip(object? sender, PaintEventArgs e)
    {
        if (sender is not SplitContainer split)
        {
            return;
        }

        var r = split.SplitterRectangle;
        var dot = Math.Max(2, LogicalToDeviceUnits(2));
        var gap = LogicalToDeviceUnits(4);
        using var brush = new SolidBrush(_palette.BorderStrong);
        for (var i = -1; i <= 1; i++)
        {
            var x = split.Orientation == Orientation.Horizontal ? r.X + r.Width / 2 + i * (dot + gap) - dot / 2 : r.X + (r.Width - dot) / 2;
            var y = split.Orientation == Orientation.Horizontal ? r.Y + (r.Height - dot) / 2 : r.Y + r.Height / 2 + i * (dot + gap) - dot / 2;
            e.Graphics.FillRectangle(brush, x, y, dot, dot);
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyDpiResources();
        ApplySplitLayout();
        _layoutInitialized = true;

        RebuildTree();
        OnNavigated();
        RefreshTaskView();
        RefreshAccountStatus();
        SetFilter(ItemTypeFilter.All);
        ThemeApplier.ApplyInitial(this);
        _activity.Write("Storiva started. No connection is opened until you choose one (Accounts menu).");
        foreach (var warning in _connections.LoadWarnings.Concat(_theme.PersistenceWarning is { } w ? [w] : []))
        {
            _activity.Write(warning, ActivityLevel.Warning);
        }

        UpdateEmptyState();
        // The object list is the main content; start keyboard focus there. While disconnected it is hidden: focus the
        // bucket pane, otherwise WinForms falls back to the splitter and draws its dotted focus cue.
        ActiveControl = _emptyStateShown ? _tree : _grid;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeTheming.ApplyTitleBar(this, StorivaPalette.Current.IsDark);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_layoutInitialized)
        {
            ApplyDpiResources();
            RebuildTree();
            RefreshList(keepSelection: true);
            RebuildLocationBar();
        }
    }

    /// <summary>
    /// Splitter positions and minimum pane sizes in device pixels. Runs after the form has its
    /// real size; each value is clamped so it always fits the container (no exceptions on small windows).
    /// </summary>
    private void ApplySplitLayout()
    {
        static void Configure(SplitContainer split, int min1, int min2, int distance)
        {
            var length = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
            var available = Math.Max(0, length - split.SplitterWidth);
            min1 = Math.Min(min1, available / 2);
            min2 = Math.Min(min2, available - min1);
            split.Panel1MinSize = 0;
            split.Panel2MinSize = 0;
            split.SplitterDistance = Math.Clamp(distance, min1, Math.Max(min1, available - min2));
            split.Panel1MinSize = min1;
            split.Panel2MinSize = min2;
        }

        var bottom = LogicalToDeviceUnits(Metrics.TaskPanelHeight);
        var outerLength = _outerSplit.Height - _outerSplit.SplitterWidth;
        Configure(_outerSplit, LogicalToDeviceUnits(150), LogicalToDeviceUnits(120), outerLength - bottom);
        Configure(_innerSplit, LogicalToDeviceUnits(140), LogicalToDeviceUnits(280), LogicalToDeviceUnits(Metrics.NavigationPaneWidth));
    }

    /// <summary>
    /// The task panel keeps its height when the window grows (the list gets the space), but never
    /// takes more than half of the height when the window shrinks, so the object list stays usable.
    /// </summary>
    private void KeepTaskPanelProportional()
    {
        if (!_layoutInitialized || _outerSplit.Panel2Collapsed)
        {
            return;
        }

        var available = _outerSplit.Height - _outerSplit.SplitterWidth;
        var maxBottom = Math.Max(_outerSplit.Panel2MinSize, available / 2);
        if (_outerSplit.Panel2.Height > maxBottom)
        {
            var distance = available - maxBottom;
            if (distance >= _outerSplit.Panel1MinSize)
            {
                _outerSplit.SplitterDistance = distance;
            }
        }
    }

    /// <summary>"Not connected" panel over the object list: what to do next, and the saved connections.</summary>
    private void BuildEmptyState()
    {
        _emptyState.Dock = DockStyle.Fill;
        _emptyState.BackColor = _palette.Surface;
        _emptyState.ColumnCount = 1;
        _emptyState.RowCount = 3;
        _emptyState.Padding = new Padding(Metrics.SpaceXl);
        _emptyState.AccessibleName = "Not connected";
        _emptyState.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        _emptyState.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _emptyState.RowStyles.Add(new RowStyle(SizeType.Percent, 60));

        var content = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.None, BackColor = _palette.Surface };
        content.Controls.Add(new Label { Text = "Not connected", AutoSize = true, Font = StorivaFonts.Title, ForeColor = _palette.TextPrimary, Margin = new Padding(0, 0, 0, Metrics.SpaceS) });
        content.Controls.Add(new Label
        {
            Text = "Connect to Amazon S3 or to an S3-compatible server (MinIO…) to browse your buckets.",
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            ForeColor = _palette.TextSecondary,
            Margin = new Padding(0, 0, 0, Metrics.SpaceM),
        });
        _emptyConnections.FlowDirection = FlowDirection.TopDown;
        _emptyConnections.AutoSize = true;
        _emptyConnections.WrapContents = false;
        _emptyConnections.BackColor = _palette.Surface;
        content.Controls.Add(_emptyConnections);
        content.Controls.Add(EmptyStateLink("Add New Account..", () => Execute(CommandId.AccountAdd)));
        content.Controls.Add(EmptyStateLink("Manage Accounts..", () => Execute(CommandId.AccountManage)));
        _emptyState.Controls.Add(content, 0, 1);
    }

    private LinkLabel EmptyStateLink(string text, Action onClick)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            LinkColor = _palette.Link,
            ActiveLinkColor = _palette.AccentStrong,
            VisitedLinkColor = _palette.Link,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 2, 0, 2),
        };
        link.LinkClicked += (_, _) => onClick();
        return link;
    }

    /// <summary>Shows the empty state while disconnected, with one "Connect" link per saved connection.</summary>
    // Own flag: Control.Visible reads the effective visibility (false while the window is hidden).
    private bool _emptyStateShown;

    private void UpdateEmptyState()
    {
        var listHadFocus = HasLogicalFocus(_grid);
        _emptyStateShown = !IsConnected;
        _emptyState.Visible = _emptyStateShown;
        _grid.Visible = !_emptyStateShown; // no overlapping siblings: the panel replaces the empty list
        if (_emptyStateShown && listHadFocus)
        {
            ActiveControl = _tree; // not the splitter (see OnLoad)
        }

        if (IsConnected)
        {
            return;
        }

        _emptyConnections.SuspendLayout();
        foreach (Control old in _emptyConnections.Controls)
        {
            old.Dispose();
        }

        _emptyConnections.Controls.Clear();
        foreach (var connection in _connections.Connections.Take(8))
        {
            var id = connection.Id;
            _emptyConnections.Controls.Add(EmptyStateLink($"Connect to \"{connection.Name}\" ({connection.Describe()})", () => _ = ConnectAsync(id)));
        }

        _emptyConnections.ResumeLayout();
    }

    private int RowHeightLogical => _options.CompactRows ? Metrics.RowHeight : Metrics.RowHeight + 4;

    /// <summary>Icons, row heights and image sizes in device pixels for the current DPI.</summary>
    private void ApplyDpiResources()
    {
        var small = LogicalToDeviceUnits(Metrics.IconSmall);
        var icon = (string name) => LucideIcons.Get(name, small, _palette.IconDefault);

        _toolbar.ImageScalingSize = _bucketBar.ImageScalingSize = _locationBar.ImageScalingSize = _actionBar.ImageScalingSize = new Size(small, small);
        _backButton.Image = icon(Lucide.ArrowLeft);
        _forwardButton.Image = icon(Lucide.ArrowRight);
        _upButton.Image = icon(Lucide.ArrowUp);
        _refreshButton.Image = _actionRefreshButton.Image = _bucketRefreshButton.Image = icon(Lucide.RefreshCw);
        _searchIcon.Image = LucideIcons.Get(Lucide.Search, small, _palette.TextSecondary);
        _searchBox.Width = LogicalToDeviceUnits(220);

        _newBucketButton.Image = LucideIcons.Get(Lucide.Plus, small, _palette.Success);
        _addExternalButton.Image = LucideIcons.Get(Lucide.Plus, small, _palette.IconBucket);
        _bucketBar.Height = _locationBar.Height = LogicalToDeviceUnits(Metrics.StripHeight);

        _editPathButton.Image = icon(Lucide.Pencil);
        _copyPathButton.Image = icon(Lucide.Copy);
        _filterButton.Image = icon(Lucide.ListFilter);
        UpdateBookmarkButton();

        _uploadButton.Image = LucideIcons.Get(Lucide.Upload, small, _palette.Success);
        _downloadButton.Image = LucideIcons.Get(Lucide.Download, small, _palette.Accent);
        _openButton.Image = icon(Lucide.ExternalLink);
        _deleteButton.Image = LucideIcons.Get(Lucide.Trash2, small, _palette.Danger);
        _newFolderButton.Image = LucideIcons.Get(Lucide.FolderPlus, small, _palette.IconFolder);
        UpdateConnectionIcon();
        _menuBuilder.ApplyIconSize(small);

        _treeImages.Images.Clear();
        _treeImages.ImageSize = new Size(small, small);
        _treeImages.Images.Add(TreeImage.Bucket, LucideIcons.Get(Lucide.Database, small, _palette.IconBucket));
        _treeImages.Images.Add(TreeImage.ExternalBucket, LucideIcons.Get(Lucide.Database, small, _palette.IconArchive));
        _treeImages.Images.Add(TreeImage.Folder, LucideIcons.Get(Lucide.Folder, small, _palette.IconFolder));
        _treeImages.Images.Add(TreeImage.FolderOpen, LucideIcons.Get(Lucide.FolderOpen, small, _palette.IconFolder));
        _treeImages.Images.Add(TreeImage.Loading, LucideIcons.Get(Lucide.Loader, small, _palette.TextSecondary));

        ApplyRowMetrics();
        _grid.Columns["Icon"]!.Width = LogicalToDeviceUnits(24);
        _activityGrid.Columns["Level"]!.Width = LogicalToDeviceUnits(28);
        ApplyTaskBarIcons(small);
    }

    /// <summary>Row heights (20 px compact like the reference [F], or 24 px — Tools › Options › Interface).</summary>
    private void ApplyRowMetrics()
    {
        var row = LogicalToDeviceUnits(RowHeightLogical);
        var header = LogicalToDeviceUnits(Metrics.HeaderHeight);
        var small = LogicalToDeviceUnits(Metrics.IconSmall);
        _tree.ItemHeight = row;
        ControlStyling.SetGridMetrics(_grid, row, header);
        ControlStyling.SetGridMetrics(_taskGrid, row, header);
        ControlStyling.SetGridMetrics(_activityGrid, row, header);
        foreach (var tab in DetailTabs)
        {
            tab.ApplyDpi(row, header, small);
        }
    }

    private static string FilterLabel(ItemTypeFilter filter) => filter switch
    {
        ItemTypeFilter.FoldersOnly => "Folders only",
        ItemTypeFilter.FilesOnly => "Files only",
        _ => "All items",
    };

    private static class TreeImage
    {
        public const string Bucket = "bucket";
        public const string ExternalBucket = "bucket-external";
        public const string Folder = "folder";
        public const string FolderOpen = "folder-open";
        public const string Loading = "loading";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _theme.Changed -= OnThemeChanged;
            _remote?.Retire();
            _detailsTimer.Dispose();
            _treeImages.Dispose();
            Icon?.Dispose();
        }

        base.Dispose(disposing);
    }
}
