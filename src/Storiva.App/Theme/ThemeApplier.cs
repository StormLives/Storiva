using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Storiva.App.Theme;

/// <summary>
/// Re-colours an existing control tree after a palette change. Every colour Storiva assigns comes from the palette,
/// so each assigned colour is mapped by its role (old palette value → new palette value). Owner-drawn parts read
/// <see cref="StorivaPalette.Current"/> when they paint and only need an invalidate; icons are re-rendered by the
/// forms (same path as a DPI change).
/// </summary>
internal static class ThemeApplier
{
    /// <summary>
    /// WinForms defaults (SystemColors) replaced when going dark, per object and property, so that returning to the
    /// light theme restores exactly the original look instead of a palette approximation.
    /// </summary>
    private static readonly ConditionalWeakTable<object, Dictionary<string, Color>> SystemOriginals = new();

    public static void Apply(Control root, StorivaPalette from, StorivaPalette to)
    {
        ArgumentNullException.ThrowIfNull(root);
        var map = new ColorMap(from, to);
        root.SuspendLayout();
        try
        {
            Walk(root, map, to);
        }
        finally
        {
            root.ResumeLayout(performLayout: false);
        }

        if (root is Form form)
        {
            NativeTheming.ApplyTitleBar(form, to.IsDark);
        }

        root.Invalidate(invalidateChildren: true);
        InputBorders.Refresh(root);
    }

    /// <summary>
    /// Finishes a freshly built window. Its Storiva colours already come from the current palette; in the dark
    /// theme this also maps WinForms defaults (SystemColors) of unstyled controls and themes the native parts.
    /// The light theme is left exactly as built.
    /// </summary>
    public static void ApplyInitial(Control root)
    {
        var palette = StorivaPalette.Current;
        InputBorders.Install(root);
        if (palette.IsDark)
        {
            Apply(root, palette, palette);
        }
        else if (root is Form form)
        {
            NativeTheming.ApplyTitleBar(form, dark: false);
        }
    }

    private static Color Map(object owner, string property, Color current, Func<Color, Color> map, StorivaPalette to)
    {
        if (!to.IsDark && SystemOriginals.TryGetValue(owner, out var saved) && saved.Remove(property, out var original))
        {
            return original;
        }

        if (to.IsDark && current.IsSystemColor)
        {
            SystemOriginals.GetOrCreateValue(owner)[property] = current;
        }

        return map(current);
    }

    private static void Walk(Control control, ColorMap map, StorivaPalette to)
    {
        // Tool strips left at SystemColors.Control are painted by StorivaToolStripRenderer from the palette.
        if (control is not ToolStrip || control.BackColor != SystemColors.Control)
        {
            control.BackColor = Map(control, nameof(control.BackColor), control.BackColor, map.Back, to);
        }

        control.ForeColor = Map(control, nameof(control.ForeColor), control.ForeColor, map.Fore, to);

        switch (control)
        {
            case DataGridView grid:
                ApplyGrid(grid, map, to);
                break;
            case ToolStrip strip:
                ApplyItems(strip.Items, map, to);
                break;
            case LinkLabel link:
                link.LinkColor = Map(link, nameof(link.LinkColor), link.LinkColor, map.Fore, to);
                link.ActiveLinkColor = Map(link, nameof(link.ActiveLinkColor), link.ActiveLinkColor, map.Fore, to);
                link.VisitedLinkColor = Map(link, nameof(link.VisitedLinkColor), link.VisitedLinkColor, map.Fore, to);
                break;
            case ButtonBase { FlatStyle: FlatStyle.Flat } button:
                button.FlatAppearance.BorderColor = map.Back(button.FlatAppearance.BorderColor);
                button.FlatAppearance.MouseOverBackColor = map.Back(button.FlatAppearance.MouseOverBackColor);
                button.FlatAppearance.MouseDownBackColor = map.Back(button.FlatAppearance.MouseDownBackColor);
                break;
            case ComboBox combo:
                // Native combo boxes only honour BackColor with the flat style.
                combo.FlatStyle = to.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                break;
        }

        if (control.ContextMenuStrip is { } menu)
        {
            menu.ForeColor = Map(menu, nameof(menu.ForeColor), menu.ForeColor, map.Fore, to);
            ApplyItems(menu.Items, map, to);
        }

        if (control is TreeView or ScrollBar or ListBox || control is TextBoxBase { Multiline: true })
        {
            NativeTheming.ApplyScrollbars(control, to.IsDark);
        }

        foreach (Control child in control.Controls)
        {
            Walk(child, map, to);
        }
    }

    private static void ApplyItems(ToolStripItemCollection items, ColorMap map, StorivaPalette to)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = Map(item, nameof(item.BackColor), item.BackColor, map.Back, to);
            item.ForeColor = Map(item, nameof(item.ForeColor), item.ForeColor, map.Fore, to);
            if (item is ToolStripControlHost host)
            {
                Walk(host.Control, map, to);
            }

            if (item is ToolStripDropDownItem { HasDropDownItems: true } dropDown)
            {
                ApplyItems(dropDown.DropDownItems, map, to);
            }
        }
    }

    private static void ApplyGrid(DataGridView grid, ColorMap map, StorivaPalette to)
    {
        grid.BackgroundColor = Map(grid, nameof(grid.BackgroundColor), grid.BackgroundColor, map.Back, to);
        grid.GridColor = Map(grid, nameof(grid.GridColor), grid.GridColor, map.Back, to);
        ApplyStyle(grid.DefaultCellStyle, map, to);
        ApplyStyle(grid.ColumnHeadersDefaultCellStyle, map, to);
        ApplyStyle(grid.RowHeadersDefaultCellStyle, map, to);
        ApplyStyle(grid.RowsDefaultCellStyle, map, to);
        ApplyStyle(grid.AlternatingRowsDefaultCellStyle, map, to);
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (column.HasDefaultCellStyle)
            {
                ApplyStyle(column.DefaultCellStyle, map, to);
            }

            if (column.HeaderCell.HasStyle)
            {
                ApplyStyle(column.HeaderCell.Style, map, to);
            }
        }

        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.HasDefaultCellStyle)
            {
                ApplyStyle(row.DefaultCellStyle, map, to);
            }

            foreach (DataGridViewCell cell in row.Cells)
            {
                if (cell.HasStyle)
                {
                    ApplyStyle(cell.Style, map, to);
                }
            }
        }
    }

    private static void ApplyStyle(DataGridViewCellStyle style, ColorMap map, StorivaPalette to)
    {
        if (!style.BackColor.IsEmpty)
        {
            style.BackColor = Map(style, nameof(style.BackColor), style.BackColor, map.Back, to);
        }

        if (!style.ForeColor.IsEmpty)
        {
            style.ForeColor = Map(style, nameof(style.ForeColor), style.ForeColor, map.Fore, to);
        }

        if (!style.SelectionBackColor.IsEmpty)
        {
            style.SelectionBackColor = Map(style, nameof(style.SelectionBackColor), style.SelectionBackColor, map.Back, to);
        }

        if (!style.SelectionForeColor.IsEmpty)
        {
            style.SelectionForeColor = Map(style, nameof(style.SelectionForeColor), style.SelectionForeColor, map.Fore, to);
        }
    }

    /// <summary>Old → new colour by role. A value used by several roles maps by the first role in priority order.</summary>
    private sealed class ColorMap
    {
        private readonly Dictionary<int, Color> _back = new();
        private readonly Dictionary<int, Color> _fore = new();

        public ColorMap(StorivaPalette from, StorivaPalette to)
        {
            foreach (var role in StorivaPalette.BackgroundRoles)
            {
                _back.TryAdd(role(from).ToArgb(), role(to));
            }

            foreach (var role in StorivaPalette.ForegroundRoles)
            {
                _fore.TryAdd(role(from).ToArgb(), role(to));
            }

            // Colours of controls that were never styled (WinForms defaults) follow the palette too.
            _back.TryAdd(SystemColors.Window.ToArgb(), to.Surface);
            _back.TryAdd(SystemColors.Control.ToArgb(), to.Chrome);
            _fore.TryAdd(SystemColors.WindowText.ToArgb(), to.TextPrimary);
            _fore.TryAdd(SystemColors.ControlText.ToArgb(), to.TextPrimary);
        }

        public Color Back(Color color) => color.A == 255 && _back.TryGetValue(color.ToArgb(), out var mapped) ? mapped : color;

        public Color Fore(Color color) => color.A == 255 && _fore.TryGetValue(color.ToArgb(), out var mapped) ? mapped : color;
    }
}

/// <summary>
/// The two Windows theming calls Storiva uses for native parts that ignore BackColor:
/// <list type="bullet">
/// <item>DWMWA_USE_IMMERSIVE_DARK_MODE (documented for Windows 11 and Windows 10 20H1+): dark title bar.</item>
/// <item>SetWindowTheme(…, "DarkMode_Explorer"): dark scroll bars on trees, lists and multi-line text boxes. The class
/// name is the one Windows Explorer uses; when it is unknown Windows silently keeps the default look.</item>
/// </list>
/// Both fail soft: on older systems the parts simply stay light (see docs/THEMES.md).
/// </summary>
internal static class NativeTheming
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private static readonly HashSet<IntPtr> Darkened = [];

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>Reads the dark title bar attribute back from DWM (null when Windows cannot report it).</summary>
    public static bool? IsTitleBarDark(Form form)
    {
        if (!form.IsHandleCreated)
        {
            return null;
        }

        try
        {
            return DwmGetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, out var value, sizeof(int)) == 0 ? value != 0 : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    public static void ApplyTitleBar(Form form, bool dark)
    {
        if (!form.IsHandleCreated || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var value = dark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not available: the title bar keeps the system colours.
        }
    }

    /// <summary>Dark scroll bars in the dark theme; back to the default theme only for controls darkened before.</summary>
    public static void ApplyScrollbars(Control control, bool dark)
    {
        if (!control.IsHandleCreated)
        {
            if (dark)
            {
                control.HandleCreated -= OnHandleCreated;
                control.HandleCreated += OnHandleCreated;
            }

            return;
        }

        var handle = control.Handle;
        if (!dark && !Darkened.Contains(handle))
        {
            return; // never touched: keep the exact original light look
        }

        try
        {
            SetWindowTheme(handle, dark ? "DarkMode_Explorer" : null, null);
            if (dark)
            {
                Darkened.Add(handle);
            }
            else
            {
                Darkened.Remove(handle);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Visual styles unavailable.
        }
    }

    private static void OnHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Control control)
        {
            control.HandleCreated -= OnHandleCreated;
            ApplyScrollbars(control, StorivaPalette.Current.IsDark);
        }
    }
}
