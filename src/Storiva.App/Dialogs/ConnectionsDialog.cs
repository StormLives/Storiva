using System.Globalization;
using Storiva.App.Theme;
using Storiva.Core.Connections;

namespace Storiva.App.Dialogs;

/// <summary>What the user asked for when closing Manage Connections.</summary>
internal enum ConnectionsDialogAction
{
    None,
    Connect,
    Disconnect,
}

/// <summary>
/// Accounts › Manage Accounts..: saved connections with provider, endpoint/region, authentication, state and last
/// test. Add / Edit / Test / Connect / Remove. Test sends read-only requests; Remove deletes only local data.
/// </summary>
internal sealed class ConnectionsDialog : Form
{
    private readonly ConnectionManager _manager;
    private readonly Func<Guid?> _activeId;
    private readonly Func<ConnectionProfile?, ConnectionDialog> _editorFactory;
    private readonly Func<string, string, bool> _confirm;
    private readonly DataGridView _grid = new();
    private readonly Label _note = new();
    private readonly Button _add = NewButton("Add..");
    private readonly Button _edit = NewButton("Edit..");
    private readonly Button _test = NewButton("Test");
    private readonly Button _connect = NewButton("Connect");
    private readonly Button _disconnect = NewButton("Disconnect");
    private readonly Button _remove = NewButton("Remove");
    private readonly Button _close = NewButton("Close");
    private CancellationTokenSource? _testing;

    public ConnectionsDialog(ConnectionManager manager, Func<Guid?> activeId, Func<ConnectionProfile?, ConnectionDialog> editorFactory, Func<string, string, bool> confirm)
    {
        _manager = manager;
        _activeId = activeId;
        _editorFactory = editorFactory;
        _confirm = confirm;
        var palette = StorivaPalette.Current;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = StorivaFonts.Ui;
        Text = "Manage Connections";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = palette.Surface;
        ForeColor = palette.TextPrimary;
        ClientSize = new Size(860, 380);
        MinimumSize = new Size(640, 300);

        _note.Dock = DockStyle.Top;
        _note.Height = 62;
        _note.Padding = new Padding(Metrics.SpaceM, Metrics.SpaceS, Metrics.SpaceM, 0);
        _note.ForeColor = palette.TextSecondary;
        _note.Text = $"Settings: {manager.StoreLocation}\r\nCredentials: {manager.SecretStoreDescription}. Removing a connection deletes only these local files, never anything on the server.";

        ControlStyling.ApplyGrid(_grid, palette);
        _grid.Dock = DockStyle.Fill;
        _grid.MultiSelect = false;
        _grid.AccessibleName = "Saved connections";
        foreach (var (name, width, fill) in new[] { ("Name", 170, false), ("Provider", 230, true), ("Credentials", 170, false), ("Status", 170, false), ("Last test", 130, false) })
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = name,
                Width = width,
                AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
        }

        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                EditSelected();
            }
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Metrics.SpaceS), BackColor = palette.Chrome };
        ButtonStyling.Apply(_close, primary: true);
        foreach (var b in new[] { _add, _edit, _test, _connect, _disconnect, _remove })
        {
            ButtonStyling.Apply(b, primary: false);
        }

        buttons.Controls.AddRange([_close, _remove, _disconnect, _connect, _test, _edit, _add]);
        _close.DialogResult = DialogResult.Cancel;
        _add.Click += (_, _) => AddConnection();
        _edit.Click += (_, _) => EditSelected();
        _test.Click += async (_, _) => await TestSelectedAsync();
        _connect.Click += (_, _) => Finish(ConnectionsDialogAction.Connect);
        _disconnect.Click += (_, _) => Finish(ConnectionsDialogAction.Disconnect);
        _remove.Click += (_, _) => RemoveSelected();
        AcceptButton = _connect;
        CancelButton = _close;

        Controls.Add(_grid);
        Controls.Add(_note);
        Controls.Add(buttons);
        ResumeLayout(performLayout: true);

        _manager.Changed += OnManagerChanged;
        Reload(null);
    }

    public ConnectionsDialogAction Action { get; private set; }

    public Guid? SelectedId => SelectedProfile()?.Id;

    // Smoke-test hooks
    internal int RowCount => _grid.Rows.Count;

    internal string StatusTextOf(int row) => _grid.Rows[row].Cells["Status"].Value as string ?? string.Empty;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ControlStyling.SetGridMetrics(_grid, LogicalToDeviceUnits(Metrics.RowHeight + 2), LogicalToDeviceUnits(Metrics.HeaderHeight));
        ThemeApplier.ApplyInitial(this);
    }

    private void OnManagerChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            Reload(SelectedId);
        }
    }

    private void Reload(Guid? select)
    {
        var palette = StorivaPalette.Current;
        _grid.Rows.Clear();
        foreach (var c in _manager.Connections)
        {
            var status = c.Id == _activeId() ? ConnectionStatus.Connected : _manager.StatusOf(c.Id);
            var label = c.Id == _activeId() ? "Connected (active)" : ConnectionStatusText.Label(status);
            if (c.LastTest is { } t && c.Id != _activeId() && status == t.Status && ConnectionStatusText.IsError(status))
            {
                label += " — " + Shorten(t.Summary, 60);
            }

            var index = _grid.Rows.Add(
                c.Name,
                c.Describe(),
                c.AuthenticationLabel,
                label,
                c.LastTest is { } test ? test.TestedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "never");
            var row = _grid.Rows[index];
            row.Tag = c.Id;
            row.Cells["Status"].ToolTipText = c.LastTest?.Summary;
            row.Cells["Status"].Style.ForeColor = status switch
            {
                ConnectionStatus.Connected => palette.Success,
                ConnectionStatus.ConnectedLimited or ConnectionStatus.Connecting => palette.Warning,
                _ when ConnectionStatusText.IsError(status) => palette.Danger,
                _ => palette.TextSecondary,
            };
            if (c.Id == select)
            {
                row.Selected = true;
                _grid.CurrentCell = row.Cells[0];
            }
        }

        if (_manager.LoadWarnings.Count > 0 || _manager.IsReadOnly)
        {
            _note.Text = string.Join(" ", _manager.LoadWarnings) + (_manager.IsReadOnly ? " (read-only)" : string.Empty);
            _note.ForeColor = palette.Warning;
        }

        UpdateButtons();
    }

    private ConnectionProfile? SelectedProfile() =>
        _grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Tag is Guid id ? _manager.Find(id) : null;

    private void UpdateButtons()
    {
        var selected = SelectedProfile();
        var testing = _testing is not null;
        _edit.Enabled = _remove.Enabled = selected is not null && !testing && !_manager.IsReadOnly;
        _connect.Enabled = selected is not null && !testing && selected.Id != _activeId();
        _test.Enabled = selected is not null;
        _test.Text = testing ? "Cancel test" : "Test";
        _disconnect.Enabled = _activeId() is not null;
        _add.Enabled = !_manager.IsReadOnly;
    }

    private void AddConnection()
    {
        using var editor = _editorFactory(null);
        if (editor.ShowDialog(this) == DialogResult.OK && editor.Result is { } profile)
        {
            TrySave(profile, editor.ResultCredentials);
        }
    }

    private void EditSelected()
    {
        if (SelectedProfile() is not { } selected || _manager.IsReadOnly)
        {
            return;
        }

        if (selected.Id == _activeId())
        {
            MessageBox.Show(this, "Disconnect before editing the active connection.", "Edit Connection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var editor = _editorFactory(selected);
        if (editor.ShowDialog(this) == DialogResult.OK && editor.Result is { } profile)
        {
            TrySave(profile, editor.ResultCredentials);
        }
    }

    private void TrySave(ConnectionProfile profile, CredentialsDraft? credentials)
    {
        try
        {
            var saved = _manager.Save(profile, credentials);
            Reload(saved.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(this, "The connection could not be saved: " + ex.Message, "Save connection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task TestSelectedAsync()
    {
        if (_testing is not null)
        {
            _testing.Cancel();
            return;
        }

        if (SelectedProfile() is not { } selected)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _testing = cancellation;
        UpdateButtons();
        try
        {
            await _manager.TestSavedAsync(selected.Id, cancellation.Token);
        }
        finally
        {
            _testing = null;
            if (!IsDisposed)
            {
                Reload(selected.Id);
            }
        }
    }

    private void RemoveSelected()
    {
        if (SelectedProfile() is not { } selected)
        {
            return;
        }

        if (selected.Id == _activeId())
        {
            MessageBox.Show(this, "Disconnect before removing the active connection.", "Remove Connection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_confirm("Remove Connection",
                $"Remove the connection \"{selected.Name}\"?\r\n\r\nIts settings and its protected credentials are deleted from this computer. " +
                "Nothing on the server is changed (no bucket or object is deleted)."))
        {
            return;
        }

        try
        {
            _manager.Remove(selected.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, "The connection could not be removed: " + ex.Message, "Remove Connection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Reload(null);
    }

    private void Finish(ConnectionsDialogAction action)
    {
        Action = action;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _testing?.Cancel();
        _manager.Changed -= OnManagerChanged;
        base.OnFormClosed(e);
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static Button NewButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(88, 30),
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false,
        Margin = new Padding(Metrics.SpaceS, 0, 0, 0),
    };
}
