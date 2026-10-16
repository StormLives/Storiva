using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Storiva.App.Theme;

/// <summary>
/// Flat, constant border for single-line text boxes. The Windows 11 visual style paints an accent-coloured "active"
/// border on hover and focus; Storiva paints the frame itself instead (one line in the palette's strong border
/// colour, the rest in the box's background), the same in every state.
/// <para>Only the client-edge frame (BorderStyle.Fixed3D) is non-client and can be painted here: the edit control
/// draws a FixedSingle border inside its client area, so Storiva text boxes use Fixed3D.</para>
/// </summary>
internal static class InputBorders
{
    private const int WmNcPaint = 0x0085;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwFrame = 0x0400;

    private static readonly ConditionalWeakTable<TextBoxBase, FramePainter> Attached = new();

    /// <summary>Attaches to every text box of <paramref name="root"/> (once its controls exist, for a new form).</summary>
    public static void Install(Control root)
    {
        if (root is Form { IsHandleCreated: false } form)
        {
            form.Load += (_, _) => Refresh(form);
        }
        else
        {
            Refresh(root);
        }
    }

    /// <summary>Attaches to text boxes added since, and repaints the frames (after a palette change).</summary>
    public static void Refresh(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is TextBoxBase { Multiline: false, BorderStyle: not BorderStyle.None } box)
            {
                if (!Attached.TryGetValue(box, out _))
                {
                    Attached.Add(box, new FramePainter(box));
                }

                if (box.IsHandleCreated)
                {
                    RedrawWindow(box.Handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwFrame);
                }
            }

            Refresh(child);
        }
    }

    internal static string Describe(TextBoxBase box) =>
        Attached.TryGetValue(box, out var painter) ? $"attached, subclassed={painter.Handle == box.Handle}, paints={painter.Paints}" : "not attached";

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr update, IntPtr region, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private sealed class FramePainter : NativeWindow
    {
        private readonly TextBoxBase _box;

        public FramePainter(TextBoxBase box)
        {
            _box = box;
            box.HandleCreated += (_, _) => AssignHandle(box.Handle);
            box.HandleDestroyed += (_, _) => ReleaseHandle();
            if (box.IsHandleCreated)
            {
                AssignHandle(box.Handle);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcPaint && _box.BorderStyle != BorderStyle.None && Paint())
            {
                m.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref m);
        }

        internal int Paints { get; private set; }

        private bool Paint()
        {
            Paints++;
            if (!GetWindowRect(Handle, out var r))
            {
                return false;
            }

            var size = new Size(r.Right - r.Left, r.Bottom - r.Top);
            var thickness = Math.Max(1, (size.Width - _box.ClientSize.Width) / 2);
            var dc = GetWindowDC(Handle);
            if (dc == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                using var g = Graphics.FromHdc(dc);
                using var border = new Pen(_box.Enabled ? StorivaPalette.Current.BorderStrong : StorivaPalette.Current.Border);
                using var fill = new Pen(_box.BackColor);
                g.DrawRectangle(border, 0, 0, size.Width - 1, size.Height - 1);
                for (var i = 1; i < thickness; i++)
                {
                    g.DrawRectangle(fill, i, i, size.Width - 1 - (2 * i), size.Height - 1 - (2 * i));
                }
            }
            finally
            {
                ReleaseDC(Handle, dc);
            }

            return true;
        }
    }
}
