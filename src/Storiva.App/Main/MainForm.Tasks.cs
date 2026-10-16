using System.Drawing.Drawing2D;
using System.Globalization;
using Storiva.App.Icons;
using Storiva.App.Theme;
using Storiva.Core.Activity;
using Storiva.Core.Listing;
using Storiva.Core.Tasks;

namespace Storiva.App.Main;

/// <summary>
/// Tasks tab and the full-width task footer [F S1]: filters Running · Queued · Canceled · Failed · All and
/// Clear finished · Cancel All. Per-task Cancel and Retry live in the task context menu.
/// </summary>
internal sealed partial class MainForm
{
    // Reference footer order [F]: Running · Queued · Stopped · Failed · All ("Stopped" is "Canceled" here:
    // real operations are canceled, not paused).
    private static readonly TaskFilter[] FooterFilters = [TaskFilter.Running, TaskFilter.Queued, TaskFilter.Canceled, TaskFilter.Failed, TaskFilter.All];

    private readonly Dictionary<TaskFilter, ToolStripButton> _taskFilterButtons = new();
    private readonly Dictionary<int, TransferState> _lastTaskStates = new();
    private readonly ToolStripMenuItem _taskCancel = new("&Cancel");
    private readonly ToolStripMenuItem _taskRetry = new("&Retry");
    private readonly ToolStripButton _taskClearFinished = new("Clear finished");
    private readonly ToolStripButton _taskCancelAll = new("Cancel All");
    private TaskFilter _taskFilter = TaskFilter.All;

    private const string ProgressColumn = "Progress";

    private DataGridView BuildTaskGrid()
    {
        ControlStyling.ApplyGrid(_taskGrid, _palette);
        _taskGrid.Dock = DockStyle.Fill;
        _taskGrid.MultiSelect = false;
        _taskGrid.AccessibleName = "Tasks";
        _taskGrid.ContextMenuStrip = _taskMenu;
        // Task · Size · % · Progress · Status follow the reference [F]; Type, Created and Message are Storiva additions.
        _taskGrid.Columns.AddRange(
        [
            TextColumn("Task", 300, fill: true),
            TextColumn("Size", 86, rightAlign: true),
            TextColumn("%", 51, rightAlign: true),
            TextColumn(ProgressColumn, 116),
            TextColumn("Status", 100),
            TextColumn("Type", 76),
            TextColumn("Created", 140),
            TextColumn("Message", 220, fill: true),
        ]);
        _taskGrid.Columns["Task"]!.FillWeight = 60;
        _taskGrid.Columns["Task"]!.MinimumWidth = 180;
        _taskGrid.Columns["Message"]!.FillWeight = 40;

        _taskMenu.Renderer = _renderer;
        _taskMenu.Items.AddRange([_taskCancel, _taskRetry]);
        _taskCancel.ToolTipText = "Cancel the selected operation";
        _taskRetry.ToolTipText = "Run the selected failed or canceled operation again";
        return _taskGrid;
    }

    private void BuildTaskFooter()
    {
        ControlStyling.ApplyToolStrip(_taskBar, _palette, _renderer);
        _taskBar.Dock = DockStyle.Bottom;
        _taskBar.BackColor = _palette.Chrome;
        _taskBar.AccessibleName = "Task filters and actions";
        _taskBar.Padding = new Padding(0, 2, 0, 2);
        _taskBar.TabStop = true;

        foreach (var filter in FooterFilters)
        {
            var button = new ToolStripButton(filter.ToString())
            {
                Tag = filter,
                ImageScaling = ToolStripItemImageScaling.None,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                Padding = new Padding(Metrics.SpaceXs, 1, Metrics.SpaceXs, 1),
                Margin = new Padding(0, 0, 2, 0),
            };
            button.Click += (_, _) => SetTaskFilter(filter);
            _taskFilterButtons[filter] = button;
            _taskBar.Items.Add(button);
        }

        // Right-aligned items are laid out right-to-left.
        foreach (var (button, tip) in new[]
                 {
                     (_taskCancelAll, "Cancel every queued or running operation"),
                     (_taskClearFinished, "Remove completed, canceled and failed operations from the list"),
                 })
        {
            button.Alignment = ToolStripItemAlignment.Right;
            button.ToolTipText = tip;
            button.ImageScaling = ToolStripItemImageScaling.None;
            button.Padding = new Padding(Metrics.SpaceXs, 1, Metrics.SpaceXs, 1);
            _taskBar.Items.Add(button);
        }
    }

    private static DataGridViewTextBoxColumn TextColumn(string name, int width, bool fill = false, bool rightAlign = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = name,
            Width = width,
            MinimumWidth = 40,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
        };
        if (rightAlign)
        {
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }

        return column;
    }

    private void WireTaskEvents()
    {
        _tasks.Changed += (_, _) =>
        {
            LogTaskTransitions();
            RefreshTaskView();
        };
        _taskGrid.SelectionChanged += (_, _) => UpdateTaskCommands();
        _taskGrid.CellPainting += PaintProgressCell;
        _taskGrid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                _taskGrid.ClearSelection();
                _taskGrid.Rows[e.RowIndex].Selected = true;
                _taskGrid.CurrentCell = _taskGrid.Rows[e.RowIndex].Cells[0];
            }
        };
        _taskMenu.Opening += (_, e) =>
        {
            UpdateTaskCommands();
            e.Cancel = SelectedTask() is null;
        };

        _taskCancel.Click += (_, _) => RunOnSelectedTask(_tasks.Cancel, "cancellation requested");
        _taskRetry.Click += (_, _) => RunOnSelectedTask(_tasks.Retry, "queued again");
        _taskCancelAll.Click += (_, _) => ReportBulk(_tasks.CancelAll(), "cancellation requested");
        _taskClearFinished.Click += (_, _) => ReportBulk(_tasks.ClearFinished(), "removed from the list");
    }

    /// <summary>Event log entries with "TaskID: n" for starts, completions, failures and cancellations [F format].</summary>
    private void LogTaskTransitions()
    {
        foreach (var task in _tasks.Tasks)
        {
            var known = _lastTaskStates.TryGetValue(task.Id, out var previous);
            _lastTaskStates[task.Id] = task.State;
            if (!known || previous == task.State)
            {
                continue;
            }

            switch (task.State)
            {
                case TransferState.Completed:
                    _activity.Write($"{task.Kind}: {task.Name} — {task.Message}", taskId: task.Id);
                    break;
                case TransferState.Failed:
                    _activity.Write($"{task.Kind}: {task.Name} — {task.Message}", ActivityLevel.Error, task.Id);
                    break;
                case TransferState.Canceled:
                    _activity.Write($"{task.Kind}: {task.Name} — canceled.", ActivityLevel.Warning, task.Id);
                    break;
                case TransferState.Running:
                    _activity.Write($"{task.Kind}: {task.Name} — started.", taskId: task.Id);
                    break;
            }
        }
    }

    private void ApplyTaskBarIcons(int size)
    {
        var icons = new Dictionary<TaskFilter, (string Name, Color Color)>
        {
            [TaskFilter.All] = (Lucide.List, _palette.IconDefault),
            [TaskFilter.Running] = (Lucide.Play, _palette.Accent),
            [TaskFilter.Queued] = (Lucide.Loader, _palette.IconDefault),
            [TaskFilter.Canceled] = (Lucide.Square, _palette.Info),
            [TaskFilter.Failed] = (Lucide.CircleAlert, _palette.Danger),
        };
        foreach (var (filter, button) in _taskFilterButtons)
        {
            button.Image = LucideIcons.Get(icons[filter].Name, size, icons[filter].Color);
        }

        _taskCancel.Image = _taskCancelAll.Image = LucideIcons.Get(Lucide.CircleX, size, _palette.Danger);
        _taskRetry.Image = LucideIcons.Get(Lucide.RotateCcw, size, _palette.IconDefault);
        _taskClearFinished.Image = LucideIcons.Get(Lucide.X, size, _palette.IconDefault);
        _bottomTabs.Padding = new Point(LogicalToDeviceUnits(Metrics.SpaceS), LogicalToDeviceUnits(3));
    }

    private void SetTaskFilter(TaskFilter filter)
    {
        _taskFilter = filter;
        SelectDetailsTab("Tasks");
        RefreshTaskView();
    }

    private void RefreshTaskView()
    {
        var selectedId = SelectedTask()?.Id;
        foreach (var (filter, button) in _taskFilterButtons)
        {
            var count = _tasks.Count(filter);
            button.Checked = filter == _taskFilter;
            button.ToolTipText = $"Show {filter.ToString().ToLowerInvariant()} tasks ({count})";
            button.AccessibleName = $"{filter} tasks, {count}";
        }

        var visible = _tasks.Filter(_taskFilter);
        _taskGrid.SuspendLayout();
        _taskGrid.Rows.Clear();
        foreach (var task in visible)
        {
            var index = _taskGrid.Rows.Add(
                task.Name,
                task.Kind is TransferKind.Upload or TransferKind.Download ? FileTypes.FormatSize(task.Size) : string.Empty,
                task.IsIndeterminate ? (task.State == TransferState.Running && task.Processed > 0 ? task.Processed.ToString("N0", CultureInfo.InvariantCulture) : "—")
                    : task.Progress.ToString(CultureInfo.InvariantCulture) + "%",
                string.Empty,
                task.State.ToString(),
                task.Kind.ToString(),
                task.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC",
                task.Message);
            var row = _taskGrid.Rows[index];
            row.Tag = task;
            row.Height = _taskGrid.RowTemplate.Height;
            row.Cells["Status"].Style.ForeColor = task.State switch
            {
                TransferState.Failed => _palette.Danger,
                TransferState.Completed => _palette.Success,
                TransferState.Running => _palette.Accent,
                _ => _palette.TextSecondary,
            };
        }

        _taskGrid.ClearSelection();
        foreach (DataGridViewRow row in _taskGrid.Rows)
        {
            if (row.Tag is TransferTask t && t.Id == selectedId)
            {
                row.Selected = true;
                _taskGrid.CurrentCell = row.Cells[0];
            }
        }

        _taskGrid.ResumeLayout();
        // Total number of tasks, like the reference "Tasks (15)" [F E1].
        _bottomTabs.TabPages[0].Text = $"Tasks ({_tasks.Tasks.Count})";
        UpdateTaskCommands();
    }

    private TransferTask? SelectedTask() =>
        _taskGrid.SelectedRows.Count > 0 ? _taskGrid.SelectedRows[0].Tag as TransferTask : null;

    private void UpdateTaskCommands()
    {
        var task = SelectedTask();
        _taskCancel.Enabled = task is not null && TaskQueue.CanCancel(task);
        _taskRetry.Enabled = task is not null && TaskQueue.CanRetry(task);
        _taskCancelAll.Enabled = _tasks.Tasks.Any(TaskQueue.CanCancel);
        _taskClearFinished.Enabled = _tasks.Tasks.Any(t => t.State is TransferState.Completed or TransferState.Canceled or TransferState.Failed);
    }

    private void RunOnSelectedTask(Func<int, bool> action, string verb)
    {
        if (SelectedTask() is { } task && action(task.Id))
        {
            _activity.Write($"Task \"{task.Name}\": {verb}.", taskId: task.Id);
            SetStatus($"Task {verb}.");
        }
    }

    private void ReportBulk(int count, string verb)
    {
        if (count > 0)
        {
            _activity.Write($"{count} {(count == 1 ? "task" : "tasks")} {verb}.");
        }

        SetStatus(count == 0 ? "No task could be changed." : $"{count} {(count == 1 ? "task" : "tasks")} {verb}.");
    }

    private void PaintProgressCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _taskGrid.Columns[e.ColumnIndex].Name != ProgressColumn
            || _taskGrid.Rows[e.RowIndex].Tag is not TransferTask task || e.Graphics is null)
        {
            return;
        }

        e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
        var pad = LogicalToDeviceUnits(Metrics.SpaceXs);
        var barHeight = LogicalToDeviceUnits(8);
        var track = new Rectangle(
            e.CellBounds.X + pad,
            e.CellBounds.Y + (e.CellBounds.Height - barHeight) / 2,
            Math.Max(0, e.CellBounds.Width - pad * 2 - 1),
            barHeight);

        var fillColor = task.State switch
        {
            TransferState.Completed => _palette.Success,
            TransferState.Failed => _palette.Danger,
            TransferState.Running or TransferState.Queued => _palette.Accent,
            _ => _palette.TextDisabled,
        };

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var trackBrush = new SolidBrush(_palette.ProgressTrack))
        using (var trackPath = StorivaToolStripRenderer.RoundedRect(track, barHeight / 2))
        {
            g.FillPath(trackBrush, trackPath);
        }

        if (task.IsIndeterminate && task.State is TransferState.Running or TransferState.Queued)
        {
            // No reliable total: a static striped bar instead of an invented percentage.
            using var stripes = new HatchBrush(HatchStyle.WideUpwardDiagonal, fillColor, _palette.ProgressTrack);
            using var stripePath = StorivaToolStripRenderer.RoundedRect(track, barHeight / 2);
            g.FillPath(stripes, stripePath);
        }
        else
        {
            var fillWidth = (int)Math.Round(track.Width * task.Progress / 100.0);
            if (fillWidth > 0)
            {
                using var fillBrush = new SolidBrush(fillColor);
                using var fillPath = StorivaToolStripRenderer.RoundedRect(new Rectangle(track.X, track.Y, Math.Max(fillWidth, barHeight), track.Height), barHeight / 2);
                g.FillPath(fillBrush, fillPath);
            }
        }

        g.SmoothingMode = SmoothingMode.Default;

        // Keep the grid lines around the custom-painted cell.
        using var grid = new Pen(_taskGrid.GridColor);
        g.DrawLine(grid, e.CellBounds.Right - 1, e.CellBounds.Top, e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
        g.DrawLine(grid, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
        e.Handled = true;
    }
}
