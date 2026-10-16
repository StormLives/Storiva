using Storiva.App.Dialogs;
using Storiva.App.Icons;
using Storiva.App.Theme;
using Storiva.Core.Details;
using Storiva.Core.Listing;
using Storiva.Core.Storage;

namespace Storiva.App.Details;

/// <summary>What the lower tabs show: the selected item (or the current location) and its address.</summary>
/// <param name="RemoteKey">Exact key (files) or prefix (folders); null for buckets.</param>
internal sealed record DetailsTarget(StorageItem Item, string? RemoteKey = null)
{
    /// <summary>The object's s3:// URI (no endpoint, no credentials, not a download link).</summary>
    public string Url => "s3://" + Item.Path.Bucket + "/" + (RemoteKey ?? string.Empty);

    public string Describe => Item.Kind switch
    {
        StorageItemKind.Bucket => $"bucket \"{Item.Name}\"",
        StorageItemKind.Folder => $"folder \"{Item.Name}\"",
        _ => $"\"{Item.Name}\"",
    };
}

/// <summary>
/// Common frame of a lower tab: optional URL bar with copy button [F E1/E2], content area and a centered
/// message that replaces the content while the data loads, when it cannot be read, or when the tab does not apply.
/// </summary>
internal abstract class DetailsTabBase : UserControl
{
    private readonly Label _message = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    private readonly TextBox _url = new() { ReadOnly = true, BorderStyle = BorderStyle.Fixed3D, AccessibleName = "Address" };
    private readonly ToolStripButton _copyUrl = new() { DisplayStyle = ToolStripItemDisplayStyle.Image, ToolTipText = "Copy address", AccessibleName = "Copy address" };
    private readonly ToolStrip _urlBar = new();

    protected DetailsTabBase(ToolStripRenderer renderer, bool showUrlBar)
    {
        var palette = Palette;
        Renderer = renderer;
        Dock = DockStyle.Fill;
        BackColor = palette.Surface;
        Font = StorivaFonts.Ui;

        Content = new Panel { Dock = DockStyle.Fill, BackColor = palette.Surface };
        _message.ForeColor = palette.TextSecondary;
        _message.BackColor = palette.Surface;
        Controls.Add(Content);
        Controls.Add(_message);

        if (showUrlBar)
        {
            ControlStyling.ApplyToolStrip(_urlBar, palette, renderer);
            _urlBar.Dock = DockStyle.Top;
            _urlBar.Padding = new Padding(2, 1, 2, 1);
            _urlBar.BackColor = palette.Chrome;
            _url.BackColor = palette.Chrome;
            _url.ForeColor = palette.TextSecondary;
            var host = new ToolStripControlHost(_url) { AutoSize = false, Margin = new Padding(0, 1, 0, 1) };
            _copyUrl.Alignment = ToolStripItemAlignment.Right;
            _copyUrl.Click += (_, _) => CopyUrl();
            _urlBar.Items.Add(_copyUrl);
            _urlBar.Items.Add(host);
            _urlBar.Resize += (_, _) => host.Width = Math.Max(100, _urlBar.ClientSize.Width - _copyUrl.Width - 12);
            Controls.Add(_urlBar);
        }
    }

    protected static StorivaPalette Palette => StorivaPalette.Current;

    protected ToolStripRenderer Renderer { get; }

    protected Panel Content { get; }

    protected DetailsTarget? Target { get; private set; }

    /// <summary>Raised with a user-facing message after a change was saved (logged by the main form).</summary>
    public event EventHandler<string>? Applied;

    // Own flag: Control.Visible reports the *effective* visibility, which is false while the tab page is hidden.
    private bool _messageShown;

    internal string MessageText => _messageShown ? _message.Text : string.Empty;

    /// <summary>Returns a message while the tab's data is loading or when it could not be read (null = ready).</summary>
    public Func<DetailsTarget, string?>? Availability { get; set; }

    public void Bind(DetailsTarget? target)
    {
        Target = target;
        _url.Text = target?.Url ?? string.Empty;
        _copyUrl.Enabled = target is not null;
        OnBind(target);
    }

    /// <summary>Shows the loading/error message instead of the content when the data is not ready.</summary>
    protected bool IsReady(DetailsTarget target)
    {
        if (Availability?.Invoke(target) is { } message)
        {
            ShowMessage(message);
            return false;
        }

        return true;
    }

    public virtual void ApplyDpi(int rowHeight, int headerHeight, int iconSize) =>
        _copyUrl.Image = LucideIcons.Get(Lucide.Copy, iconSize, Palette.IconDefault);

    protected abstract void OnBind(DetailsTarget? target);

    protected void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visible = true;
        Content.Visible = false;
        _messageShown = true;
    }

    protected void ShowContent()
    {
        _message.Visible = false;
        Content.Visible = true;
        _messageShown = false;
    }

    protected void RaiseApplied(string message) => Applied?.Invoke(this, message);

    protected DataGridView NewGrid()
    {
        var grid = new DataGridView { Dock = DockStyle.Fill };
        ControlStyling.ApplyGrid(grid, Palette);
        grid.MultiSelect = false;
        return grid;
    }

    protected ToolStrip NewFooter()
    {
        var strip = new ToolStrip { Dock = DockStyle.Bottom, Padding = new Padding(2, 1, 2, 1), TabStop = true };
        ControlStyling.ApplyToolStrip(strip, Palette, Renderer);
        strip.BackColor = Palette.Chrome;
        return strip;
    }

    protected static ToolStripButton FooterButton(string text, string tooltip, bool right = false) => new(text)
    {
        ToolTipText = tooltip,
        AccessibleName = text,
        ImageScaling = ToolStripItemImageScaling.None,
        DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
        Alignment = right ? ToolStripItemAlignment.Right : ToolStripItemAlignment.Left,
        Padding = new Padding(4, 1, 4, 1),
    };

    protected static ToolStripLabel FooterNote() => new() { Alignment = ToolStripItemAlignment.Right };

    private void CopyUrl()
    {
        if (Target is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(Target.Url);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard busy: nothing to do; the address stays visible in the box.
        }
    }
}

/// <summary>
/// Permissions tab [F E2]: User Name · Full Control · Read · Write · Read Permissions · Write Permissions, as read
/// from the object or bucket ACL. Read-only: Storiva does not change ACLs, and an ACL is not the whole picture
/// (bucket policies and IAM may allow or deny more).
/// </summary>
internal sealed class PermissionsTab : DetailsTabBase
{
    private static readonly (Permission Flag, string Header)[] Columns =
    [
        (Permission.FullControl, "Full Control"), (Permission.Read, "Read"), (Permission.Write, "Write"),
        (Permission.ReadAcl, "Read Permissions"), (Permission.WriteAcl, "Write Permissions"),
    ];

    private readonly IObjectDetailsStore _store;
    private readonly DataGridView _grid;
    private readonly ToolStripLabel _note = FooterNote();
    private IReadOnlyList<Grant> _grants = [];

    public PermissionsTab(ToolStripRenderer renderer, IObjectDetailsStore store)
        : base(renderer, showUrlBar: true)
    {
        _store = store;
        _grid = NewGrid();
        _grid.ReadOnly = true;
        _grid.AccessibleName = "Permissions (read-only)";
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "User", HeaderText = "User Name", Width = 220, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var (flag, header) in Columns)
        {
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = flag.ToString(),
                HeaderText = header,
                Tag = flag,
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
        }

        _note.ForeColor = Palette.TextSecondary;
        var footer = NewFooter();
        footer.Items.Add(_note);
        Content.Controls.Add(_grid);
        Content.Controls.Add(footer);
    }

    /// <summary>Text under the grid (owner, grants not shown, ACL ≠ policies).</summary>
    public Func<DetailsTarget, string>? Note { get; set; }

    internal Permission PermissionsOf(Grantee grantee) => _grants.FirstOrDefault(g => g.Grantee == grantee)?.Permissions ?? Permission.None;

    public override void ApplyDpi(int rowHeight, int headerHeight, int iconSize)
    {
        base.ApplyDpi(rowHeight, headerHeight, iconSize);
        ControlStyling.SetGridMetrics(_grid, rowHeight, headerHeight);
    }

    protected override void OnBind(DetailsTarget? target)
    {
        _grants = [];
        if (target is null)
        {
            ShowMessage("Select a bucket or file to see its permissions.");
            return;
        }

        if (target.Item.Kind == StorageItemKind.Folder)
        {
            ShowMessage("In S3 a folder is only a key prefix and has no permissions of its own. Select a file or the bucket.");
            return;
        }

        if (!IsReady(target))
        {
            return;
        }

        _grants = AclRules.Normalize(_store.GetGrants(target.Item.Path));
        _grid.Rows.Clear();
        foreach (var grant in _grants)
        {
            var values = new object[Columns.Length + 1];
            values[0] = grant.Grantee switch
            {
                Grantee.Owner => "Owner",
                Grantee.AuthenticatedUsers => "Authenticated users",
                _ => "All users (public)",
            };
            for (var i = 0; i < Columns.Length; i++)
            {
                values[i + 1] = (grant.Permissions & Columns[i].Flag) != 0;
            }

            _grid.Rows[_grid.Rows.Add(values)].Height = _grid.RowTemplate.Height;
        }

        _note.Text = Note?.Invoke(target) ?? "Read-only ACL";
        _grid.ClearSelection();
        ShowContent();
    }
}

/// <summary>
/// Headers tab (read-only: headers and metadata from a HEAD request) and Tags tab (editable: Add.. / Edit.. / Delete,
/// then Apply changes asks for confirmation and saves on the server).
/// </summary>
internal sealed class KeyValueTab : DetailsTabBase
{
    private readonly KeyValueKind _kind;
    private readonly IObjectDetailsStore _store;
    private readonly DataGridView _grid;
    private readonly ToolStripButton _add = FooterButton("Add..", "Add a tag");
    private readonly ToolStripButton _edit = FooterButton("Edit..", "Edit the selected tag");
    private readonly ToolStripButton _delete = FooterButton("Delete", "Delete the selected tag");
    private readonly ToolStripButton _apply = FooterButton("Apply changes", "Save the tags on the server (asks first)", right: true);
    private readonly ToolStripButton _reload = FooterButton("Reload", "Discard pending changes", right: true);
    private readonly ToolStripLabel _pending = FooterNote();
    private EditSession<(string Key, string Value)>? _session;
    private bool _applying;

    public KeyValueTab(ToolStripRenderer renderer, IObjectDetailsStore store, KeyValueKind kind)
        : base(renderer, showUrlBar: true)
    {
        _kind = kind;
        _store = store;
        _grid = NewGrid();
        _grid.AccessibleName = kind == KeyValueKind.Header ? "HTTP headers (read-only)" : "Tags";
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", HeaderText = kind == KeyValueKind.Header ? "Name" : "Key", Width = 260, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });

        var footer = NewFooter();
        if (kind == KeyValueKind.Tag)
        {
            _grid.CellDoubleClick += (_, e) =>
            {
                if (e.RowIndex >= 0)
                {
                    EditSelected();
                }
            };
            _grid.SelectionChanged += (_, _) => UpdateButtons();
            _grid.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Delete)
                {
                    DeleteSelected();
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            _add.Click += (_, _) => AddEntry();
            _edit.Click += (_, _) => EditSelected();
            _delete.Click += (_, _) => DeleteSelected();
            _apply.Click += (_, _) => Apply();
            _reload.Click += (_, _) => Reload();
            // Right-aligned items are laid out right-to-left: Reload, Apply changes, note.
            footer.Items.AddRange([_add, _edit, _delete, _reload, _apply, _pending]);
        }
        else
        {
            _pending.ForeColor = Palette.TextSecondary;
            _pending.Text = "Read-only: headers and metadata as reported by the server";
            footer.Items.Add(_pending);
        }

        Content.Controls.Add(_grid);
        Content.Controls.Add(footer);
    }

    /// <summary>Tags: confirms and saves the tags on the server; returns null on success or a safe error message.</summary>
    public Func<DetailsTarget, IReadOnlyList<(string Key, string Value)>, Task<string?>>? RemoteApply { get; set; }

    internal IReadOnlyList<(string Key, string Value)> Entries => _session?.Current ?? [];

    internal bool IsDirty => _session?.IsDirty == true;

    /// <summary>True when the entries cannot be edited: headers, no target, or a save in progress.</summary>
    private bool IsLocked => _kind == KeyValueKind.Header || _session is null || _applying || RemoteApply is null;

    private int MaxItems => Target?.Item.Kind == StorageItemKind.Bucket ? TagRules.MaxBucketTags : TagRules.MaxObjectTags;

    /// <summary>Adds a tag without the dialog (smoke test); returns the validation error, if any.</summary>
    internal string? AddEntryDirect(string key, string value)
    {
        var error = TagRules.Validate(key, value, Entries.Select(e => e.Key), MaxItems);
        if (error is null && !IsLocked)
        {
            _session!.Update([.. Entries, (key, value)]);
            Render();
        }

        return IsLocked ? "Not editable." : error;
    }

    internal void RemoveEntryDirect(string key)
    {
        if (!IsLocked)
        {
            _session!.Update(Entries.Where(e => e.Key != key).ToList());
            Render();
        }
    }

    internal void ClickApply() => Apply();

    public override void ApplyDpi(int rowHeight, int headerHeight, int iconSize)
    {
        base.ApplyDpi(rowHeight, headerHeight, iconSize);
        ControlStyling.SetGridMetrics(_grid, rowHeight, headerHeight);
        _add.Image = LucideIcons.Get(Lucide.Plus, iconSize, Palette.Success);
        _edit.Image = LucideIcons.Get(Lucide.Pencil, iconSize, Palette.IconDefault);
        _delete.Image = LucideIcons.Get(Lucide.Trash2, iconSize, Palette.Danger);
        _apply.Image = LucideIcons.Get(Lucide.Check, iconSize, Palette.Success);
        _reload.Image = LucideIcons.Get(Lucide.RefreshCw, iconSize, Palette.IconDefault);
    }

    protected override void OnBind(DetailsTarget? target)
    {
        _session = null;
        if (target is null)
        {
            ShowMessage(_kind == KeyValueKind.Header ? "Select a file to see its HTTP headers." : "Select a file or bucket to see its tags.");
            return;
        }

        var applies = _kind == KeyValueKind.Header
            ? target.Item.Kind == StorageItemKind.File
            : target.Item.Kind is StorageItemKind.File or StorageItemKind.Bucket;
        if (!applies)
        {
            ShowMessage(_kind == KeyValueKind.Header
                ? $"HTTP headers belong to files. {char.ToUpperInvariant(target.Describe[0]) + target.Describe[1..]} has none."
                : "Folders do not have tags: in object storage a folder is only a name prefix. Select a file or a bucket.");
            return;
        }

        if (!IsReady(target))
        {
            return;
        }

        var path = target.Item.Path;
        _session = _kind == KeyValueKind.Header
            ? new EditSession<(string, string)>(() => _store.GetHeaders(path).Select(h => (h.Name, h.Value)).ToList(), _ => { })
            : new EditSession<(string, string)>(() => _store.GetTags(path).Select(t => (t.Key, t.Value)).ToList(), _ => { });
        ShowContent();
        Render();
    }

    private void AddEntry()
    {
        if (IsLocked || Target is null)
        {
            return;
        }

        using var dialog = new KeyValueDialog(isEdit: false, string.Empty, string.Empty, Entries.Select(e => e.Key), MaxItems, Target.Describe);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _session!.Update([.. Entries, (dialog.Key, dialog.Value)]);
            Render();
        }
    }

    private void EditSelected()
    {
        if (IsLocked || Target is null || _grid.CurrentRow is not { Index: var index } || index >= Entries.Count)
        {
            return;
        }

        var current = Entries[index];
        var others = Entries.Where((_, i) => i != index).Select(e => e.Key);
        using var dialog = new KeyValueDialog(isEdit: true, current.Key, current.Value, others, MaxItems + 1, Target.Describe);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            var list = Entries.ToList();
            list[index] = (dialog.Key, dialog.Value);
            _session!.Update(list);
            Render();
        }
    }

    private void DeleteSelected()
    {
        if (IsLocked || _grid.CurrentRow is not { Index: var index } || index >= Entries.Count)
        {
            return;
        }

        _session!.Update(Entries.Where((_, i) => i != index).ToList());
        Render();
    }

    private async void Apply()
    {
        if (IsLocked || Target is null || !_session!.IsDirty)
        {
            return;
        }

        var target = Target;
        var session = _session;
        _applying = true;
        _pending.Text = "Saving on the server…";
        UpdateButtons();
        string? error;
        try
        {
            error = await RemoteApply!(target, Entries.ToList());
        }
        finally
        {
            _applying = false;
        }

        if (!ReferenceEquals(session, _session))
        {
            return; // the selection changed meanwhile; the result was logged by the main form
        }

        if (error is null)
        {
            session.Reload();
            RaiseApplied($"Tags of {target.Describe} saved on the server ({Entries.Count} entr{(Entries.Count == 1 ? "y" : "ies")}).");
            Render();
        }
        else
        {
            Render();
            _pending.Text = error;
        }
    }

    private void Reload()
    {
        _session?.Reload();
        Render();
    }

    private void Render()
    {
        _grid.Rows.Clear();
        foreach (var (key, value) in Entries)
        {
            _grid.Rows[_grid.Rows.Add(key, value)].Height = _grid.RowTemplate.Height;
        }

        if (Entries.Count == 0)
        {
            _grid.Rows[_grid.Rows.Add(_kind == KeyValueKind.Header ? "(no headers)" : "(no tags)", string.Empty)].DefaultCellStyle.ForeColor = Palette.TextSecondary;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (_kind == KeyValueKind.Header)
        {
            return;
        }

        var hasRow = _grid.CurrentRow is { Index: var i } && i < Entries.Count;
        _add.Enabled = !IsLocked && Entries.Count < MaxItems;
        _edit.Enabled = _delete.Enabled = !IsLocked && hasRow;
        _apply.Enabled = !IsLocked && _session?.IsDirty == true;
        _reload.Enabled = !_applying && _session?.IsDirty == true;
        if (!_applying)
        {
            _pending.ForeColor = Palette.Warning;
            _pending.Text = _session?.IsDirty == true ? "Unapplied changes (Apply saves them on the server)" : string.Empty;
        }
    }
}

/// <summary>Properties tab: name/value rows that depend on the item kind.</summary>
internal sealed class PropertiesTab : DetailsTabBase
{
    private readonly DataGridView _grid;

    public PropertiesTab(ToolStripRenderer renderer)
        : base(renderer, showUrlBar: false)
    {
        _grid = NewGrid();
        _grid.AccessibleName = "Properties of the selected item";
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Property", HeaderText = "Property", Width = 170, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        Content.Controls.Add(_grid);
    }

    /// <summary>Rows computed by the main form (needs the catalog and the details).</summary>
    public Func<DetailsTarget?, IReadOnlyList<(string Name, string Value)>>? RowProvider { get; set; }

    internal string? ValueOf(string name) => _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (string?)r.Cells[0].Value == name)?.Cells[1].Value as string;

    public override void ApplyDpi(int rowHeight, int headerHeight, int iconSize) => ControlStyling.SetGridMetrics(_grid, rowHeight, headerHeight);

    protected override void OnBind(DetailsTarget? target)
    {
        ShowContent();
        _grid.Rows.Clear();
        foreach (var (name, value) in RowProvider?.Invoke(target) ?? [])
        {
            _grid.Rows[_grid.Rows.Add(name, value)].Height = _grid.RowTemplate.Height;
        }

        _grid.ClearSelection();
    }
}

/// <summary>Preview tab [F E1]: the first bytes of a file as text/JSON, or a hex view (offset · 16 bytes · ASCII).</summary>
internal sealed class PreviewTab : DetailsTabBase
{
    private readonly IObjectDetailsStore _store;
    private readonly TextBox _text = new()
    {
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        AccessibleName = "Preview content",
    };

    private readonly Label _note = new() { Dock = DockStyle.Top, Height = 22, Padding = new Padding(4, 4, 0, 0) };

    public PreviewTab(ToolStripRenderer renderer, IObjectDetailsStore store)
        : base(renderer, showUrlBar: true)
    {
        _store = store;
        // Monospaced, as in the reference hex view [F E1]: Consolas when present, otherwise the system monospace font.
        _text.Font = new Font(IsInstalled("Consolas") ? "Consolas" : FontFamily.GenericMonospace.Name, 9f);
        _text.BackColor = Palette.Surface;
        _text.ForeColor = Palette.TextPrimary;
        _note.ForeColor = Palette.TextSecondary;
        Content.Controls.Add(_text);
        Content.Controls.Add(_note);
    }

    internal PreviewKind Kind { get; private set; }

    internal string PreviewText => _text.Text;

    protected override void OnBind(DetailsTarget? target)
    {
        if (target is { Item.Kind: StorageItemKind.File } && PreviewFormatter.Decide(target.Item) is not PreviewKind.Unsupported && !IsReady(target))
        {
            Kind = PreviewKind.NotAFile;
            return;
        }

        var result = PreviewFormatter.Build(target?.Item, item => _store.GetContentSample(item, PreviewFormatter.SampleBytes));
        Kind = result.Kind;
        if (result.Kind is PreviewKind.NotAFile or PreviewKind.Unsupported)
        {
            ShowMessage(result.Note);
            return;
        }

        _note.Text = result.Kind == PreviewKind.Hex ? "Hex view · " + result.Note : (result.Kind == PreviewKind.Json ? "JSON · " : "Text · ") + result.Note;
        _text.Text = result.Content;
        _text.SelectionStart = 0;
        ShowContent();
    }

    private static bool IsInstalled(string family) =>
        FontFamily.Families.Any(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Versions tab [H]: versions reported by the server (read-only; Restore and Delete are not implemented).</summary>
internal sealed class VersionsTab : DetailsTabBase
{
    private readonly IObjectDetailsStore _store;
    private readonly DataGridView _grid;
    private readonly ToolStripLabel _note = FooterNote();

    public VersionsTab(ToolStripRenderer renderer, IObjectDetailsStore store)
        : base(renderer, showUrlBar: true)
    {
        _store = store;
        _grid = NewGrid();
        _grid.AccessibleName = "Versions of the selected file";
        foreach (var (name, width, fill) in new[] { ("Version ID", 200, false), ("Last Modified", 160, false), ("Size", 90, false), ("Storage Class", 120, false), ("Latest", 70, true) })
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, Width = width, AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        _note.ForeColor = Palette.TextSecondary;
        var footer = NewFooter();
        footer.Items.Add(_note);
        Content.Controls.Add(_grid);
        Content.Controls.Add(footer);
    }

    internal int RowCount => _grid.Rows.Count;

    public override void ApplyDpi(int rowHeight, int headerHeight, int iconSize)
    {
        base.ApplyDpi(rowHeight, headerHeight, iconSize);
        ControlStyling.SetGridMetrics(_grid, rowHeight, headerHeight);
    }

    protected override void OnBind(DetailsTarget? target)
    {
        if (target?.Item is not { Kind: StorageItemKind.File } file)
        {
            ShowMessage("Select a file to see its versions.");
            return;
        }

        if (!IsReady(target))
        {
            return;
        }

        var status = _store.GetVersioning(file.Path);
        if (status == VersioningStatus.Disabled)
        {
            ShowMessage($"Versioning is not enabled for bucket \"{file.Path.Bucket}\", so there are no previous versions of this object to restore.");
            return;
        }

        _grid.Rows.Clear();
        foreach (var version in _store.GetVersions(file.Path))
        {
            var row = _grid.Rows[_grid.Rows.Add(
                version.IsDeleteMarker ? version.VersionId + " (delete marker)" : version.VersionId,
                FileTypes.FormatDate(version.LastModified),
                FileTypes.FormatSize(version.Size),
                version.StorageClass,
                version.IsLatest ? "Yes" : string.Empty)];
            row.Height = _grid.RowTemplate.Height;
            if (version.IsLatest)
            {
                row.DefaultCellStyle.Font = StorivaFonts.UiBold;
            }
        }

        _note.Text = (status == VersioningStatus.Suspended ? "Versioning suspended · " : string.Empty) + $"{RowCount} version(s) reported by the server · read-only";
        _grid.ClearSelection();
        ShowContent();
    }
}
