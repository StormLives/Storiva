using System.Reflection;
using Storiva.App.Icons;
using Storiva.App.Theme;

namespace Storiva.App.Dialogs;

internal sealed class AboutDialog : Form
{
    public AboutDialog(bool focusNotices = false)
    {
        var palette = StorivaPalette.Current;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = StorivaFonts.Ui;
        Text = "About Storiva";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = palette.Surface;
        ClientSize = new Size(560, 440);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var logo = new PictureBox
        {
            Image = BrandIcon.Bitmap(64),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(64, 64),
            Location = new Point(24, 24),
            AccessibleName = "Storiva logo",
        };
        var title = new Label
        {
            Text = "Storiva",
            Font = StorivaFonts.Title,
            AutoSize = true,
            Location = new Point(104, 28),
            ForeColor = palette.TextPrimary,
        };
        var details = new Label
        {
            Text = $"Version {version}\r\n" +
                   "Open source under the MIT License. No telemetry: Storiva only connects to the servers you configure.\r\n" +
                   "Client for AWS S3 and S3-compatible servers (MinIO). Credentials are protected with Windows DPAPI.",
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            Location = new Point(104, 58),
            ForeColor = palette.TextSecondary,
        };
        var noticesLabel = new Label
        {
            Text = "Third-party notices:",
            AutoSize = true,
            Location = new Point(24, 128),
            ForeColor = palette.TextPrimary,
        };
        var notices = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = ReadNotices(),
            Location = new Point(24, 150),
            Size = new Size(512, 220),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = palette.Chrome,
            AccessibleName = "Third-party notices",
        };
        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(96, 30),
            Location = new Point(440, 388),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
        };
        ButtonStyling.Apply(close, primary: true);
        AcceptButton = close;
        CancelButton = close;

        Controls.AddRange([logo, title, details, noticesLabel, notices, close]);
        close.TabIndex = 0;
        notices.TabIndex = 1;
        ActiveControl = close;
        ResumeLayout(performLayout: true);

        // A read-only TextBox selects all its text when it gets focus; keep the caret at the start.
        notices.GotFocus += (_, _) => BeginInvoke(() => notices.Select(0, 0));
        if (focusNotices)
        {
            Shown += (_, _) => notices.Focus();
        }
    }

    private static string ReadNotices()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        try
        {
            return File.Exists(file)
                ? File.ReadAllText(file).ReplaceLineEndings("\r\n")
                : "THIRD_PARTY_NOTICES.md was not found next to the executable.\r\nIcons: Lucide (ISC License) — https://lucide.dev/license";
        }
        catch (IOException ex)
        {
            return "Could not read THIRD_PARTY_NOTICES.md: " + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            return "Could not read THIRD_PARTY_NOTICES.md: " + ex.Message;
        }
    }
}
