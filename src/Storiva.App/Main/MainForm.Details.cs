using System.Globalization;
using Storiva.App.Details;
using Storiva.App.Dialogs;
using Storiva.App.Theme;
using Storiva.Core.Activity;
using Storiva.Core.Details;
using Storiva.Core.Listing;
using Storiva.Core.Storage;

namespace Storiva.App.Main;

/// <summary>
/// Lower tabs [F S1/E1/E2/E8]: Tasks · Permissions · Headers · Tags · Properties · Preview · Versions · Event log.
/// The selected list item (or, without a selection, the current location) is the target of every detail tab.
/// </summary>
internal sealed partial class MainForm
{
    internal static readonly string[] BottomTabNames = ["Tasks", "Permissions", "Headers", "Tags", "Properties", "Preview", "Versions", "Event log"];

    private PermissionsTab _permissionsTab = null!;
    private KeyValueTab _headersTab = null!;
    private KeyValueTab _tagsTab = null!;
    private PropertiesTab _propertiesTab = null!;
    private PreviewTab _previewTab = null!;
    private VersionsTab _versionsTab = null!;
    private DetailsTarget? _detailsTarget;

    private IEnumerable<DetailsTabBase> DetailTabs =>
        _permissionsTab is null ? [] : [_permissionsTab, _headersTab, _tagsTab, _propertiesTab, _previewTab, _versionsTab];

    private void BuildBottomPanel()
    {
        _bottomTabs.Dock = DockStyle.Fill;
        _bottomTabs.AccessibleName = "Details and tasks";
        _bottomTabs.TabIndex = 2;
        _bottomTabs.Padding = new Point(Metrics.SpaceS, 3);

        _permissionsTab = new PermissionsTab(_renderer, _details);
        _headersTab = new KeyValueTab(_renderer, _details, KeyValueKind.Header);
        _tagsTab = new KeyValueTab(_renderer, _details, KeyValueKind.Tag);
        _propertiesTab = new PropertiesTab(_renderer) { RowProvider = PropertyRows };
        _previewTab = new PreviewTab(_renderer, _details);
        _versionsTab = new VersionsTab(_renderer, _details);

        foreach (var name in BottomTabNames)
        {
            var page = new TabPage(name) { BackColor = _palette.Surface, Padding = new Padding(0), UseVisualStyleBackColor = false, Name = name };
            page.Controls.Add(name switch
            {
                "Tasks" => Framed(BuildTaskGrid()),
                "Permissions" => Framed(_permissionsTab),
                "Headers" => Framed(_headersTab),
                "Tags" => Framed(_tagsTab),
                "Properties" => Framed(_propertiesTab),
                "Preview" => Framed(_previewTab),
                "Versions" => Framed(_versionsTab),
                "Event log" => Framed(BuildEventLogGrid()),
                _ => throw new InvalidOperationException(name),
            });
            _bottomTabs.TabPages.Add(page);
        }

        BuildTaskFooter();

        // Tabs fill the panel; the footer sits under them across the full width (reference [F]).
        _outerSplit.Panel2.Controls.Add(_bottomTabs);
        _outerSplit.Panel2.Controls.Add(_taskBar);
    }

    private void WireDetailEvents()
    {
        foreach (var tab in DetailTabs)
        {
            tab.Applied += (_, message) =>
            {
                _activity.Write(message);
                SetStatus(message);
            };
        }

        _activity.Added += (_, entry) => AppendEventLog(entry);

        // Properties is derived data (no pending edits): refresh it whenever it becomes visible, so it
        // reflects permission/tag/versioning changes applied in the other tabs.
        _bottomTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_bottomTabs.SelectedTab?.Name == "Properties")
            {
                _propertiesTab.Bind(_detailsTarget);
            }
        };
    }

    /// <summary>Recomputes the target from the selection and refreshes the tabs when it changed.</summary>
    private void BindDetails(bool force = false)
    {
        StorageItem? item = SelectedRows() is [var single] ? single.Item : null;
        item ??= _navigator.Current.IsRoot ? null : _catalog.Find(_navigator.Current);
        BindDetails(item is null ? null : TargetFor(item), force);
    }

    private DetailsTarget TargetFor(StorageItem item) =>
        new(item, item.Kind == StorageItemKind.Bucket ? null : _remote?.Catalog.KeyOf(item));

    /// <summary>
    /// Binds every detail tab to <paramref name="target"/>. Re-binding the same target is skipped unless
    /// <paramref name="force"/> is set, so a selection event never throws away pending edits.
    /// </summary>
    private void BindDetails(DetailsTarget? target, bool force = false)
    {
        if (!force && target == _detailsTarget && _detailsTarget is not null)
        {
            return;
        }

        if (_detailsTarget is not null && target != _detailsTarget && _tagsTab.IsDirty)
        {
            _activity.Write($"Unapplied changes for {_detailsTarget.Describe} were discarded (selection changed).", ActivityLevel.Warning);
        }

        _detailsTarget = target;
        foreach (var tab in DetailTabs)
        {
            tab.Bind(target);
        }

        ScheduleDetailsLoad();
    }

    internal void SelectDetailsTab(string name)
    {
        var index = Array.IndexOf(BottomTabNames, name);
        if (index >= 0)
        {
            _bottomTabs.SelectedIndex = index;
        }
    }

    /// <summary>Properties rows by item kind.</summary>
    private IReadOnlyList<(string Name, string Value)> PropertyRows(DetailsTarget? target) =>
        IsConnected ? RemotePropertyRows(target) : [("Status", "Not connected")];

    // ---------- Event log [F E3/E5/E6]: "I [yyyy-MM-dd HH:mm:ss] message TaskID: n", warnings highlighted ----------

    private DataGridView BuildEventLogGrid()
    {
        ControlStyling.ApplyGrid(_activityGrid, _palette);
        _activityGrid.Dock = DockStyle.Fill;
        _activityGrid.AccessibleName = "Event log";
        _activityGrid.ColumnHeadersVisible = false; // the reference log has no header row [F]
        _activityGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Level",
            Width = 28,
            Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = new Padding(0) },
        });
        _activityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        return _activityGrid;
    }

    private void AppendEventLog(ActivityEntry entry)
    {
        var index = _activityGrid.Rows.Add(entry.LevelLetter, entry.Format());
        var row = _activityGrid.Rows[index];
        row.Height = _activityGrid.RowTemplate.Height;
        row.Tag = entry;
        var color = entry.Level switch
        {
            ActivityLevel.Error => _palette.Danger,
            ActivityLevel.Warning => _palette.Warning,
            _ => _palette.TextPrimary,
        };
        row.DefaultCellStyle.ForeColor = color;
        row.DefaultCellStyle.SelectionForeColor = color;
        row.Cells["Level"].Style.ForeColor = entry.Level == ActivityLevel.Information ? _palette.Info : color;
        row.Cells["Level"].Style.Font = StorivaFonts.UiBold;

        while (_activityGrid.Rows.Count > _activity.Capacity)
        {
            _activityGrid.Rows.RemoveAt(0);
        }

        // Newest at the bottom, like the reference; keep it in view.
        if (_activityGrid.Rows.Count > 0 && _activityGrid.IsHandleCreated)
        {
            _activityGrid.FirstDisplayedScrollingRowIndex = _activityGrid.Rows.Count - 1;
        }
    }
}
