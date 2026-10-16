using Storiva.App.Icons;
using Storiva.App.Theme;

namespace Storiva.App.Main;

/// <summary>
/// Owner-drawn navigation tree so selection, hover and focus use the Storiva palette instead of
/// the system highlight. Expand/collapse glyphs are drawn and hit-tested here (native ones are off).
/// </summary>
internal sealed partial class MainForm
{
    private void ConfigureTreeDrawing()
    {
        _tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        _tree.ShowPlusMinus = false;
        _tree.ShowRootLines = false;
        _tree.ShowLines = false;
        _tree.FullRowSelect = true;
        _tree.HotTracking = true;
        _tree.DrawNode += DrawTreeNode;
        _tree.MouseDown += OnTreeMouseDown;
        _tree.GotFocus += (_, _) => _tree.Invalidate();
        _tree.LostFocus += (_, _) => _tree.Invalidate();
    }

    private int TreeIconSize => LogicalToDeviceUnits(Metrics.IconSmall);

    private int TreeIndent => LogicalToDeviceUnits(16);

    private Rectangle GlyphBounds(TreeNode node, Rectangle row)
    {
        var x = LogicalToDeviceUnits(Metrics.SpaceXs) + node.Level * TreeIndent;
        // Buckets-only tree: no expand glyphs, so no space is reserved for them.
        return new Rectangle(x, row.Top, TreeShowsFolders ? TreeIconSize + LogicalToDeviceUnits(4) : 0, row.Height);
    }

    private void DrawTreeNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node is not { } node || e.Bounds.IsEmpty || e.Graphics is null)
        {
            return;
        }

        var g = e.Graphics;
        var row = new Rectangle(0, e.Bounds.Top, _tree.ClientSize.Width, e.Bounds.Height);
        var selected = (e.State & TreeNodeStates.Selected) != 0;
        var hot = (e.State & TreeNodeStates.Hot) != 0;
        var dropTarget = node == _treeDropNode;
        var background = selected
            ? (_tree.Focused ? _palette.SelectionFocused : _palette.SelectionUnfocused)
            : hot || dropTarget ? _palette.HoverFill : _palette.Surface;
        using (var brush = new SolidBrush(background))
        {
            g.FillRectangle(brush, row);
        }

        // Files dragged from Explorer over this bucket or folder: outlined, so the upload target is unambiguous.
        if (dropTarget)
        {
            using var pen = new Pen(_palette.FocusRing);
            g.DrawRectangle(pen, row.Left, row.Top, row.Width - 1, row.Height - 1);
        }

        // Visible keyboard focus: accent bar on the left of the selected row while the tree is focused.
        if (selected && _tree.Focused)
        {
            using var accent = new SolidBrush(_palette.FocusRing);
            g.FillRectangle(accent, row.Left, row.Top, LogicalToDeviceUnits(3), row.Height);
        }

        var size = TreeIconSize;
        var glyph = GlyphBounds(node, row);
        if (node.Nodes.Count > 0)
        {
            var chevron = LucideIcons.Get(node.IsExpanded ? Lucide.ChevronDown : Lucide.ChevronRight, size, _palette.TextSecondary);
            g.DrawImage(chevron, glyph.Left, row.Top + (row.Height - size) / 2, size, size);
        }

        var iconX = glyph.Right;
        if (_treeImages.Images.ContainsKey(node.ImageKey))
        {
            var key = selected && !string.IsNullOrEmpty(node.SelectedImageKey) ? node.SelectedImageKey : node.ImageKey;
            var image = _treeImages.Images[key] ?? _treeImages.Images[node.ImageKey];
            if (image is not null)
            {
                g.DrawImage(image, iconX, row.Top + (row.Height - size) / 2, size, size);
            }
        }

        var textX = iconX + size + LogicalToDeviceUnits(6);
        var textBounds = new Rectangle(textX, row.Top, Math.Max(0, row.Right - textX - LogicalToDeviceUnits(4)), row.Height);
        TextRenderer.DrawText(g, node.Text, _tree.Font, textBounds, _palette.TextPrimary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void OnTreeMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _tree.GetNodeAt(e.Location) is not { } node || node.Nodes.Count == 0)
        {
            return;
        }

        if (GlyphBounds(node, node.Bounds with { X = 0, Width = _tree.ClientSize.Width }).Contains(e.Location))
        {
            node.Toggle();
        }
    }
}
