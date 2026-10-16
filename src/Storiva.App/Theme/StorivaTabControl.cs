namespace Storiva.App.Theme;

/// <summary>
/// TabControl whose tab strip follows the palette in the dark theme. The native TabControl ignores BackColor for
/// its headers, so with the dark palette this control paints the strip itself (UserPaint); keyboard navigation,
/// focus, accessibility and the tab pages stay native. With the light palette it is a plain native TabControl,
/// exactly as before.
/// </summary>
internal sealed class StorivaTabControl : TabControl
{
    private bool _custom;

    public StorivaTabControl() => UseCustomPainting(StorivaPalette.Current.IsDark);

    /// <summary>True while the dark palette paints the strip.</summary>
    public bool IsCustomPainted => _custom;

    /// <summary>Switches between native painting (light) and palette painting (dark).</summary>
    public void UseCustomPainting(bool custom)
    {
        if (_custom == custom && IsHandleCreated)
        {
            return;
        }

        _custom = custom;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, custom);
        UpdateStyles();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_custom)
        {
            base.OnPaint(e);
            return;
        }

        var p = StorivaPalette.Current;
        var g = e.Graphics;
        using (var chrome = new SolidBrush(p.Chrome))
        {
            g.FillRectangle(chrome, ClientRectangle);
        }

        var display = DisplayRectangle;
        var frame = Rectangle.Inflate(display, 1, 1);
        using (var surface = new SolidBrush(p.Surface))
        {
            g.FillRectangle(surface, frame);
        }

        using var border = new Pen(p.Border);
        g.DrawRectangle(border, frame.X, frame.Y, frame.Width - 1, frame.Height - 1);

        for (var i = 0; i < TabCount; i++)
        {
            var bounds = GetTabRect(i);
            var selected = i == SelectedIndex;
            if (selected)
            {
                bounds.Inflate(0, 1);
            }

            using (var fill = new SolidBrush(selected ? p.Surface : p.Toolbar))
            {
                g.FillRectangle(fill, bounds);
            }

            g.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - (selected ? 0 : 1));
            if (selected)
            {
                // Accent line on top of the selected tab; no border under it so it joins its page.
                using var accent = new SolidBrush(p.Accent);
                g.FillRectangle(accent, bounds.X + 1, bounds.Y, bounds.Width - 2, Math.Max(2, LogicalToDeviceUnits(2)));
                using var join = new Pen(p.Surface);
                g.DrawLine(join, bounds.X + 1, bounds.Bottom - 1, bounds.Right - 2, bounds.Bottom - 1);
            }

            TextRenderer.DrawText(g, TabPages[i].Text, Font, bounds, selected ? p.TextPrimary : p.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (selected && Focused && ShowFocusCues)
            {
                var focus = Rectangle.Inflate(bounds, -3, -3);
                ControlPaint.DrawFocusRectangle(g, focus, p.TextPrimary, p.Surface);
            }
        }
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        if (_custom)
        {
            Invalidate();
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }
}
