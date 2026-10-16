namespace Storiva.App.Theme;

/// <summary>Shared styling for standard controls, so individual forms do not repeat colours.</summary>
internal static class ControlStyling
{
    /// <summary>Row and header heights in device pixels; call again after a DPI change.</summary>
    public static void SetGridMetrics(DataGridView grid, int rowHeight, int headerHeight)
    {
        grid.RowTemplate.Height = rowHeight;
        foreach (DataGridViewRow row in grid.Rows)
        {
            row.Height = rowHeight;
        }

        grid.ColumnHeadersHeight = headerHeight;
    }

    /// <summary>One-time styling. Call once per grid (it subscribes to focus events).</summary>
    public static void ApplyGrid(DataGridView grid, StorivaPalette p)
    {
        grid.BackgroundColor = p.Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = p.GridLine;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.Single; // light horizontal + vertical rules, like the reference [F]
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.EnableHeadersVisualStyles = false;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.AllowUserToOrderColumns = false;
        grid.ReadOnly = true;
        grid.MultiSelect = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoGenerateColumns = false;
        grid.StandardTab = true; // Tab leaves the grid instead of walking cells.
        grid.ShowCellToolTips = true;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        // White header with dark text, as in a classic Windows list view [F].
        grid.ColumnHeadersDefaultCellStyle.BackColor = p.Surface;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = p.TextPrimary;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.Surface;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.TextPrimary;
        grid.ColumnHeadersDefaultCellStyle.Font = StorivaFonts.Ui;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(3, 0, 3, 0);
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;

        grid.DefaultCellStyle.BackColor = p.Surface;
        grid.DefaultCellStyle.ForeColor = p.TextPrimary;
        grid.DefaultCellStyle.Font = StorivaFonts.Ui;
        grid.DefaultCellStyle.Padding = new Padding(3, 0, 3, 0);
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        SetSelectionColors(grid, p, focused: false);

        // Stronger selection while the grid has keyboard focus, so focus is always visible.
        grid.Enter += (_, _) => SetSelectionColors(grid, StorivaPalette.Current, focused: true);
        grid.Leave += (_, _) => SetSelectionColors(grid, StorivaPalette.Current, focused: false);
    }

    private static void SetSelectionColors(DataGridView grid, StorivaPalette p, bool focused)
    {
        grid.DefaultCellStyle.SelectionBackColor = focused ? p.SelectionFocused : p.SelectionUnfocused;
        grid.DefaultCellStyle.SelectionForeColor = p.TextPrimary;
    }

    public static void ApplyTree(TreeView tree, StorivaPalette p)
    {
        tree.BackColor = p.Surface;
        tree.ForeColor = p.TextPrimary;
        tree.BorderStyle = BorderStyle.None;
        tree.HideSelection = false;
        tree.FullRowSelect = true;
        tree.ShowLines = false;
        tree.ShowRootLines = true;
        tree.Font = StorivaFonts.Ui;
    }

    public static void ApplyToolStrip(ToolStrip strip, StorivaPalette p, ToolStripRenderer renderer)
    {
        strip.Renderer = renderer;
        strip.ForeColor = p.TextPrimary;
        strip.Font = StorivaFonts.Ui;
        strip.GripStyle = ToolStripGripStyle.Hidden;
    }
}
