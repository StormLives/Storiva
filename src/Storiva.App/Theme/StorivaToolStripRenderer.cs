using System.Drawing.Drawing2D;

namespace Storiva.App.Theme;

/// <summary>
/// Flat renderer for menus, toolbars and status bars. Colours are read from <see cref="StorivaPalette.Current"/> at
/// paint time, so one renderer instance follows theme changes.
/// </summary>
internal sealed class StorivaToolStripRenderer : ToolStripProfessionalRenderer
{
    public StorivaToolStripRenderer()
        : base(new StorivaColorTable())
    {
        RoundedEdges = false;
    }

    private static StorivaPalette _palette => StorivaPalette.Current;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderToolStripBackground(e);
            return;
        }

        var color = e.ToolStrip is MenuStrip or StatusStrip ? _palette.Chrome : _palette.Toolbar;
        using var brush = new SolidBrush(e.ToolStrip.BackColor == SystemColors.Control ? color : e.ToolStrip.BackColor);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            using var pen = new Pen(_palette.BorderStrong);
            var r = e.ToolStrip.ClientRectangle;
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            return;
        }

        using var line = new Pen(_palette.Border);
        var bounds = e.ToolStrip.ClientRectangle;
        if (e.ToolStrip is StatusStrip)
        {
            e.Graphics.DrawLine(line, 0, 0, bounds.Width, 0);
        }
        else if (e.ToolStrip is not MenuStrip)
        {
            e.Graphics.DrawLine(line, 0, bounds.Bottom - 1, bounds.Width, bounds.Bottom - 1);
        }
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is ToolStripButton button)
        {
            PaintItemState(e.Graphics, new Rectangle(Point.Empty, e.Item.Size), e.Item.Selected, e.Item.Pressed, button.Checked, e.Item.Enabled);
        }
    }

    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) =>
        PaintItemState(e.Graphics, new Rectangle(Point.Empty, e.Item.Size), e.Item.Selected, e.Item.Pressed, isChecked: false, e.Item.Enabled);

    /// <summary>
    /// The overflow button ("more items" when a bar is too narrow): the professional renderer paints a light gradient
    /// that stays bright in the dark theme. Same flat states as the other buttons, with a chevron in the text colour.
    /// </summary>
    protected override void OnRenderOverflowButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        PaintItemState(e.Graphics, bounds, e.Item.Selected, e.Item.Pressed, isChecked: false, e.Item.Enabled);
        var size = Math.Max(3, Math.Min(bounds.Width, bounds.Height) / 4);
        var cx = bounds.Width / 2;
        var cy = bounds.Height / 2;
        using var pen = new Pen(_palette.TextSecondary, Math.Max(1f, size / 3f));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new Point[] { new Point(cx - size, cy - (size / 2)), new Point(cx, cy + (size / 2)), new Point(cx + size, cy - (size / 2)) });
        e.Graphics.SmoothingMode = SmoothingMode.Default;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled
            ? _palette.TextDisabled
            : e.Item.ForeColor == SystemColors.ControlText || e.Item.ForeColor == Control.DefaultForeColor
                ? _palette.TextPrimary
                : e.Item.ForeColor;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(_palette.Border);
        var r = e.Item.ContentRectangle;
        if (e.Vertical)
        {
            var x = r.Width / 2;
            e.Graphics.DrawLine(pen, x, r.Top + 4, x, r.Bottom - 4);
        }
        else
        {
            var y = r.Height / 2;
            e.Graphics.DrawLine(pen, r.Left + 4, y, r.Right - 4, y);
        }
    }

    private void PaintItemState(Graphics g, Rectangle bounds, bool hover, bool pressed, bool isChecked, bool enabled)
    {
        if (!enabled || (!hover && !pressed && !isChecked))
        {
            return;
        }

        var fill = pressed ? _palette.PressedFill : isChecked ? _palette.CheckedFill : _palette.HoverFill;
        var rect = Rectangle.Inflate(bounds, -1, -1);
        rect.Width -= 1;
        rect.Height -= 1;
        using var path = RoundedRect(rect, 4);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }

        if (isChecked || pressed)
        {
            using var pen = new Pen(_palette.Accent);
            g.DrawPath(pen, path);
        }

        g.SmoothingMode = SmoothingMode.Default;
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class StorivaColorTable : ProfessionalColorTable
    {
        private static StorivaPalette p => StorivaPalette.Current;

        public override Color MenuItemSelected => p.HoverFill;
        public override Color MenuItemSelectedGradientBegin => p.HoverFill;
        public override Color MenuItemSelectedGradientEnd => p.HoverFill;
        public override Color MenuItemPressedGradientBegin => p.PressedFill;
        public override Color MenuItemPressedGradientMiddle => p.PressedFill;
        public override Color MenuItemPressedGradientEnd => p.PressedFill;
        public override Color MenuItemBorder => p.Accent;
        public override Color MenuBorder => p.BorderStrong;
        public override Color ToolStripDropDownBackground => p.Surface;
        public override Color ImageMarginGradientBegin => p.Surface;
        public override Color ImageMarginGradientMiddle => p.Surface;
        public override Color ImageMarginGradientEnd => p.Surface;
        public override Color CheckBackground => p.CheckedFill;
        public override Color CheckSelectedBackground => p.PressedFill;
        public override Color CheckPressedBackground => p.PressedFill;
        public override Color SeparatorDark => p.Border;
        public override Color SeparatorLight => p.Surface;
        public override Color ButtonSelectedBorder => p.Accent;
        public override Color ToolStripBorder => p.Border;
        public override Color StatusStripGradientBegin => p.Chrome;
        public override Color StatusStripGradientEnd => p.Chrome;
        public override Color MenuStripGradientBegin => p.Chrome;
        public override Color MenuStripGradientEnd => p.Chrome;
    }
}
