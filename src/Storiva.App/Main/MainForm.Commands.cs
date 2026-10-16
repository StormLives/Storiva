using System.Text;
using Storiva.App.Commands;
using Storiva.Core.Activity;
using Storiva.Core.Commands;
using Storiva.Core.Storage;

namespace Storiva.App.Main;

/// <summary>
/// Central command handling: selection context, enabled/checked state, execution and shortcut routing.
/// Menus, context menus, toolbar buttons and shortcuts all go through <see cref="Execute"/>.
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>
    /// The control the window gives keyboard focus to, inside nested containers (a SplitContainer itself means the
    /// splitter). Unlike Win32 focus it is kept while another application is in the foreground.
    /// </summary>
    internal Control? FocusedControl
    {
        get
        {
            Control? c = ActiveControl;
            while (c is ContainerControl { ActiveControl: { } inner })
            {
                c = inner;
            }

            return c;
        }
    }

    /// <summary>True when <paramref name="target"/> (or a child, e.g. a grid's editing box) has the window's focus.</summary>
    private bool HasLogicalFocus(Control target)
    {
        if (target.ContainsFocus)
        {
            return true;
        }

        for (var c = FocusedControl; c is not null; c = c.Parent)
        {
            if (c == target)
            {
                return true;
            }
        }

        return false;
    }

    internal SelectionContext BuildContext()
    {
        var treeBucket = _tree.SelectedNode?.Tag is StoragePath p && p.Bucket is { } bucket ? StoragePath.Root.Append(bucket) : null;
        var focus = HasLogicalFocus(_tree) ? FocusArea.Tree : HasLogicalFocus(_grid) ? FocusArea.List : FocusArea.Other;
        return new SelectionContext(
            _navigator.Current,
            SelectedRows().Select(r => r.Item).ToList(),
            treeBucket,
            focus,
            _remoteClipboard.Count,
            _navigator.CanGoBack,
            _navigator.CanGoForward,
            _grid.Rows.Count,
            IsConnected);
    }

    private bool IsCommandEnabled(CommandId id) => CommandRules.IsEnabled(id, BuildContext());

    private bool? IsCommandChecked(CommandId id) => id switch
    {
        CommandId.ViewBucketPane => !_innerSplit.Panel1Collapsed,
        CommandId.ViewTaskPanel => !_outerSplit.Panel2Collapsed,
        CommandId.ViewStatusBar => _status.Visible,
        CommandId.BookmarkToggle => !_navigator.Current.IsRoot && _bookmarks.Contains(_navigator.Current),
        _ => null,
    };

    /// <summary>Runs a command if it is enabled in the current context. Returns false when it was not run.</summary>
    internal bool Execute(CommandId id)
    {
        var definition = CommandCatalog.Get(id);
        if (!IsCommandEnabled(id))
        {
            return false;
        }

        if (definition.Behavior == CommandBehavior.Unavailable)
        {
            ShowUnavailable(definition);
            return true;
        }

        switch (id)
        {
            // Accounts
            case CommandId.AccountAdd: AddConnection(); break;
            case CommandId.AccountManage: ManageConnections(); break;
            case CommandId.AccountExit: Close(); return true; // the form is disposed afterwards

            // Buckets
            case CommandId.BucketCreate: RemoteCreateBucket(); break;
            case CommandId.BucketDelete: RemoteDeleteBucket(); break;
            case CommandId.BucketRefresh: RefreshData(buckets: true); break;
            case CommandId.BucketAddExternal: RemoteAddExternalBucket(); break;
            case CommandId.BucketDownloadAllTo: RemoteDownloadBucket(); break;
            case CommandId.BucketEditAcl: ShowBucketTab("Permissions"); break;
            case CommandId.BucketEditTags: ShowBucketTab("Tags"); break;
            case CommandId.BucketProperties: ShowBucketTab("Properties"); break;
            case CommandId.BucketEditVersioning: RemoteEditVersioning(); break;

            // Files
            case CommandId.FileOpen: OpenCurrent(); break;
            case CommandId.FileDownload: RemoteDownload(); break;
            case CommandId.FileUploadFiles: RemoteUploadFiles(); break;
            case CommandId.FileUploadFolders: RemoteUploadFolder(); break;
            case CommandId.FileCut: RemoteSetClipboard(cut: true); break;
            case CommandId.FileCopy: RemoteSetClipboard(cut: false); break;
            case CommandId.FilePaste: RemotePaste(); break;
            case CommandId.FileRename: RemoteRename(); break;
            case CommandId.FileDelete: RemoteDelete(); break;
            case CommandId.FileCreateFolder: RemoteCreateFolder(); break;
            case CommandId.FileRefresh: RefreshData(buckets: false); break;
            case CommandId.FileEditAcl: SelectDetailsTab("Permissions"); break;
            case CommandId.FileEditHeaders: SelectDetailsTab("Headers"); break;
            case CommandId.FileEditTags: SelectDetailsTab("Tags"); break;
            case CommandId.FileProperties: SelectDetailsTab("Properties"); break;
            case CommandId.FileSelectAll: SelectAll(); break;
            case CommandId.FileInvertSelection: InvertSelection(); break;
            case CommandId.FileChangeStorageClass: break; // parent of a submenu

            // Bookmarks
            case CommandId.BookmarkToggle: ToggleBookmark(); break;
            case CommandId.BookmarkClear: ClearBookmarks(); break;

            // Tools and Storiva extensions
            case CommandId.ToolsOptions: ShowOptions(); break;
            case CommandId.ViewBucketPane: ApplyOptions(_options with { ShowBucketPane = _innerSplit.Panel1Collapsed }); break;
            case CommandId.ViewTaskPanel: _outerSplit.Panel2Collapsed = !_outerSplit.Panel2Collapsed; break;
            case CommandId.ViewStatusBar: ApplyOptions(_options with { ShowStatusBar = !_status.Visible }); break;
            case CommandId.NavigateBack: _navigator.GoBack(); break;
            case CommandId.NavigateForward: _navigator.GoForward(); break;
            case CommandId.NavigateUp: _navigator.GoUp(); break;
            case CommandId.Find: FocusSearch(); break;
            case CommandId.EditPath: BeginPathEdit(); break;

            // Help
            case CommandId.HelpTopics: ShowHelpTopics(); break;
            case CommandId.HelpNotices: ShowAbout(focusNotices: true); break;
            case CommandId.HelpAbout: ShowAbout(focusNotices: false); break;

            default:
                ShowUnavailable(definition);
                break;
        }

        UpdateCommandStates();
        return true;
    }

    /// <summary>
    /// Keyboard shortcuts of the reference menus [F E4–E6], routed by focus (see ShortcutRouter).
    /// Text boxes keep their own editing keys (Ctrl+A/C/V/X/Z, Del, Backspace).
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var shortcut = ToShortcut(keyData);
        if (shortcut is not null && !IsTextEditingKey(keyData))
        {
            var context = BuildContext();
            if (ShortcutRouter.Resolve(shortcut, context.Focus) is { } id && CommandRules.IsEnabled(id, context))
            {
                Execute(id);
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool IsTextEditingKey(Keys keyData)
    {
        if (ActiveControlDeep() is not TextBoxBase { ReadOnly: false })
        {
            return false;
        }

        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        return modifiers == Keys.None || modifiers == Keys.Shift
            || (modifiers == Keys.Control && key is Keys.A or Keys.C or Keys.V or Keys.X or Keys.Z or Keys.Y or Keys.Left or Keys.Right or Keys.Back or Keys.Delete);
    }

    private Control? ActiveControlDeep()
    {
        Control? control = ActiveControl;
        while (control is ContainerControl { ActiveControl: { } inner } && inner != control)
        {
            control = inner;
        }

        // ToolStripTextBox hosts its TextBox inside a ToolStrip, which is not a ContainerControl.
        return _searchBox.Focused ? _searchBox.TextBox : _pathBox.Focused ? _pathBox.TextBox : control;
    }

    internal static string? ToShortcut(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.None)
        {
            return null;
        }

        var name = key switch
        {
            >= Keys.A and <= Keys.Z => key.ToString(),
            >= Keys.F1 and <= Keys.F12 => key.ToString(),
            Keys.Delete => "Del",
            Keys.Left => "Left",
            Keys.Right => "Right",
            Keys.Up => "Up",
            _ => null,
        };
        if (name is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        if ((keyData & Keys.Control) != 0) sb.Append("Ctrl+");
        if ((keyData & Keys.Shift) != 0) sb.Append("Shift+");
        if ((keyData & Keys.Alt) != 0) sb.Append("Alt+");
        return sb.Append(name).ToString();
    }

    /// <summary>Dynamic menu sections: accounts, bookmarks and storage classes.</summary>
    private IReadOnlyList<ToolStripItem> DynamicMenuItems(string list)
    {
        switch (list)
        {
            case CommandCatalog.AccountsList:
                return AccountMenuItems();

            case CommandCatalog.BookmarksList:
                _bookmarks.Prune(_catalog.Exists);
                if (_bookmarks.Items.Count == 0)
                {
                    return [new ToolStripMenuItem("(no bookmarks)") { Enabled = false }];
                }

                return _bookmarks.Items.Select(path =>
                {
                    var item = new ToolStripMenuItem(path.ToDisplayString()) { Checked = path == _navigator.Current };
                    item.Click += (_, _) => _navigator.NavigateTo(path);
                    return (ToolStripItem)item;
                }).ToList();

            case CommandCatalog.StorageClassList:
            {
                var files = SelectedRows().Where(r => r.Item.Kind == StorageItemKind.File).Select(r => r.Item).ToList();
                if (_catalog.StorageClasses.Count == 0)
                {
                    return [new ToolStripMenuItem("(this server offers no storage class choice)") { Enabled = false }];
                }

                return _catalog.StorageClasses.Select(storageClass =>
                {
                    var item = new ToolStripMenuItem(storageClass)
                    {
                        Checked = files.Count > 0 && files.All(f => f.StorageClass == storageClass),
                        Enabled = files.Count > 0,
                    };
                    item.Click += (_, _) => RemoteChangeStorageClass(storageClass);
                    return (ToolStripItem)item;
                }).ToList();
            }

            default:
                return [];
        }
    }

    /// <summary>One notice for every command that is not available: name, reason, and "nothing was executed".</summary>
    private void ShowUnavailable(CommandDefinition definition)
    {
        var name = definition.Text.TrimEnd('.', ' ');
        var reason = definition.UnavailableReason ?? "Storiva does not implement it yet.";
        _activity.Write($"\"{name}\" was requested; it is not available. Nothing was executed.", ActivityLevel.Warning);
        SetStatus($"\"{name}\" is not available.");
        Prompts.Info(this, "Not available",
            $"\"{name}\" is not available in Storiva yet.\r\n\r\n{reason}\r\n\r\nNothing was executed.");
    }

    private void ShowHelpTopics()
    {
        var text = new StringBuilder()
            .AppendLine(IsConnected
                ? $"Connected to \"{_remote!.Profile.Name}\" ({_remote.Profile.Describe()}). Operations act on the server and always ask before deleting."
                : "Not connected. Use Accounts › Add New Account.. to connect to AWS S3 or an S3-compatible server (MinIO), or pick a saved connection in the Accounts menu.")
            .AppendLine()
            .AppendLine("Shortcuts (the same key can belong to Files or Buckets; the focused pane decides):")
            .AppendLine("  Ctrl+N   Create New Folder (list) / Create New Bucket (bucket pane)")
            .AppendLine("  Del      Delete (list) / Delete Bucket (bucket pane)")
            .AppendLine("  F2       Rename        Ctrl+P   Properties     F5   Refresh")
            .AppendLine("  Ctrl+X/C/V   Cut / Copy / Paste")
            .AppendLine("  Ctrl+L / Ctrl+H / Ctrl+T   Permissions / HTTP headers / Tags tab")
            .AppendLine("  Ctrl+U / Ctrl+D   Upload / Download")
            .AppendLine("  Alt+Left / Alt+Right / Alt+Up   Back / Forward / Up      Ctrl+F   Find      Alt+D   Edit location")
            .AppendLine("  Alt+O    Options")
            .AppendLine()
            .AppendLine("Drag files or folders from Windows Explorer onto the list to upload them into the current location, or onto a bucket in the bucket pane to upload them there.")
            .AppendLine()
            .AppendLine("Menu items shown in grey text open a \"not available\" notice and never execute anything.")
            .AppendLine()
            .AppendLine("Data: %LOCALAPPDATA%\\Storiva (settings.json, connections.json, secrets\\ protected with Windows DPAPI). See docs/CONNECTION_SETUP.md.");
        Prompts.Info(this, "Storiva help", text.ToString());
    }
}
