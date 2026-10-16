using Storiva.App.Icons;
using Storiva.App.Theme;

namespace Storiva.App.Dialogs;

/// <summary>
/// Base layout for Storiva dialogs, following the reference dialog pattern [F S2, E3, E7]:
/// header (icon, bold title, subtitle, "online help" link) → separator → labelled fields with help text
/// and inline errors → footer with an optional link on the left and primary/Cancel buttons on the right.
/// The look is Storiva's own.
/// </summary>
internal class StorivaDialog : Form
{
    public const int FieldWidth = 460;

    private readonly PictureBox _headerIcon = new() { SizeMode = PictureBoxSizeMode.CenterImage };
    private readonly string _iconName;
    private readonly TableLayoutPanel _header;
    private readonly TableLayoutPanel _body;
    private readonly TableLayoutPanel _footer;

    protected StorivaDialog(string title, string subtitle, string iconName, string primaryText)
    {
        _iconName = iconName;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = StorivaFonts.Ui;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Palette.Surface;
        ForeColor = Palette.TextPrimary;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };

        // Header: icon | title + subtitle | help link
        _header = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(Metrics.SpaceXl, Metrics.SpaceL, Metrics.SpaceL, Metrics.SpaceM),
            BackColor = Palette.Chrome,
            Margin = new Padding(0),
        };
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _headerIcon.Size = new Size(Metrics.DialogHeaderIcon + 8, Metrics.DialogHeaderIcon + 8);
        _headerIcon.Margin = new Padding(0, 0, Metrics.SpaceM, 0);
        _header.Controls.Add(_headerIcon, 0, 0);
        _header.SetRowSpan(_headerIcon, 2);
        _header.Controls.Add(new Label
        {
            Text = title,
            Font = StorivaFonts.Title,
            AutoSize = true,
            ForeColor = Palette.TextPrimary,
            Margin = new Padding(0, 0, 0, 2),
        }, 1, 0);
        _header.Controls.Add(new Label
        {
            Text = subtitle,
            AutoSize = true,
            MaximumSize = new Size(FieldWidth - 60, 0),
            ForeColor = Palette.TextSecondary,
            Margin = new Padding(0),
        }, 1, 1);

        var separator = new Panel { Height = 1, Dock = DockStyle.Fill, BackColor = Palette.Border, Margin = new Padding(0) };

        _body = NewSection();
        _body.Padding = new Padding(Metrics.SpaceXl, Metrics.SpaceL, Metrics.SpaceXl, Metrics.SpaceS);

        // Footer: optional link (left) | buttons (right)
        _footer = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(Metrics.SpaceXl, Metrics.SpaceS, Metrics.SpaceXl - 3, Metrics.SpaceL),
            Margin = new Padding(0),
        };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Right,
        };

        CancelButtonControl = CreateButton("Cancel", primary: false);
        CancelButtonControl.DialogResult = DialogResult.Cancel;
        PrimaryButton = CreateButton(primaryText, primary: true);
        PrimaryButton.Click += (_, _) => OnPrimaryClick();
        buttons.Controls.Add(CancelButtonControl);
        buttons.Controls.Add(PrimaryButton);
        _footer.Controls.Add(buttons, 1, 0);
        AcceptButton = PrimaryButton;
        CancelButton = CancelButtonControl;

        root.Controls.Add(_header, 0, 0);
        root.Controls.Add(separator, 0, 1);
        root.Controls.Add(_body, 0, 2);
        root.Controls.Add(_footer, 0, 3);
        Controls.Add(root);
        ResumeLayout(performLayout: true);
    }

    /// <summary>Palette of the current theme; <see cref="OnLoad"/> also themes the native parts.</summary>
    protected static StorivaPalette Palette => StorivaPalette.Current;

    protected Button PrimaryButton { get; }

    protected Button CancelButtonControl { get; }

    /// <summary>Main field area; pass a section from <see cref="AddSection"/> to add fields elsewhere.</summary>
    protected TableLayoutPanel Body => _body;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdateHeaderIcon();
        ThemeApplier.ApplyInitial(this);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateHeaderIcon();
    }

    /// <summary>Validates and closes with OK when valid. Override to apply the result.</summary>
    protected virtual void OnPrimaryClick()
    {
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>"online help" link in the header [F]. Storiva shows local help only (no web page).</summary>
    protected LinkLabel AddHeaderLink(string text, Action onClick)
    {
        var link = CreateLink(text, onClick);
        link.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _header.Controls.Add(link, 2, 0);
        return link;
    }

    /// <summary>Link at the left of the footer ("advanced settings..", "show more settings.." [F]).</summary>
    protected LinkLabel AddFooterLink(string text, Action onClick)
    {
        var link = CreateLink(text, onClick);
        link.Anchor = AnchorStyles.Left;
        _footer.Controls.Add(link, 0, 0);
        return link;
    }

    /// <summary>Extra field area below the body; hidden sections back "advanced/more settings" links.</summary>
    protected TableLayoutPanel AddSection(bool visible)
    {
        var section = NewSection();
        section.Padding = new Padding(0);
        section.Visible = visible;
        _body.Controls.Add(section);
        return section;
    }

    /// <summary>Adds a highlighted notice line.</summary>
    protected Label AddNotice(string text, TableLayoutPanel? into = null)
    {
        var notice = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(FieldWidth, 0),
            ForeColor = Palette.TextPrimary,
            BackColor = Palette.HoverFill,
            Padding = new Padding(Metrics.SpaceS),
            Margin = new Padding(0, 0, 0, Metrics.SpaceM),
            AccessibleRole = AccessibleRole.StaticText,
        };
        (into ?? _body).Controls.Add(notice);
        return notice;
    }

    /// <summary>Adds label, input, help text and a hidden error line. Returns the error label.</summary>
    protected Label AddField(string label, Control input, string help, TableLayoutPanel? into = null)
    {
        var target = into ?? _body;
        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            Margin = new Padding(0, Metrics.SpaceS, 0, Metrics.SpaceXs),
            ForeColor = Palette.TextPrimary,
        };
        // Inputs and help text are indented under their label, as in the reference dialogs [F S2].
        input.Margin = new Padding(Metrics.SpaceL, 0, 0, 2);
        input.Width = FieldWidth;
        input.AccessibleName = label.TrimEnd(':');
        input.AccessibleDescription = help;
        target.Controls.Add(caption);
        target.Controls.Add(input);
        target.Controls.Add(HelpLabel(help));
        var error = ErrorLabel();
        target.Controls.Add(error);
        return error;
    }

    /// <summary>Checkbox with help text below it (e.g. "Use secure transfer (SSL/TLS)" [F]).</summary>
    protected CheckBox AddCheckBox(string text, string help, bool initial, TableLayoutPanel? into = null)
    {
        var target = into ?? _body;
        var box = new CheckBox
        {
            Text = text,
            Checked = initial,
            AutoSize = true,
            Margin = new Padding(0, Metrics.SpaceS, 0, 2),
            ForeColor = Palette.TextPrimary,
            AccessibleDescription = help,
        };
        target.Controls.Add(box);
        target.Controls.Add(HelpLabel(help));
        return box;
    }

    protected static void ShowError(Label errorLabel, Control input, string? message)
    {
        errorLabel.Text = message ?? string.Empty;
        errorLabel.Visible = message is not null;
        input.AccessibleDescription = message is null
            ? input.AccessibleDescription?.Split(" Error: ")[0]
            : input.AccessibleDescription?.Split(" Error: ")[0] + " Error: " + message;
    }

    private Label HelpLabel(string help) => new()
    {
        Text = help,
        AutoSize = true,
        MaximumSize = new Size(FieldWidth, 0),
        ForeColor = Palette.TextSecondary,
        Margin = new Padding(Metrics.SpaceL, 0, 0, 0),
    };

    private Label ErrorLabel() => new()
    {
        AutoSize = true,
        MaximumSize = new Size(FieldWidth, 0),
        ForeColor = Palette.Danger,
        Margin = new Padding(Metrics.SpaceL, 2, 0, Metrics.SpaceXs),
        Visible = false,
    };

    private TableLayoutPanel NewSection()
    {
        var section = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, FieldWidth + Metrics.SpaceL));
        return section;
    }

    private LinkLabel CreateLink(string text, Action onClick)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            LinkColor = Palette.Link,
            ActiveLinkColor = Palette.AccentStrong,
            VisitedLinkColor = Palette.Link,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, Metrics.SpaceXs, Metrics.SpaceS, 0),
            TabStop = true,
        };
        link.LinkClicked += (_, _) => onClick();
        return link;
    }

    private Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(96, 30),
            Padding = new Padding(Metrics.SpaceM, 0, Metrics.SpaceM, 0),
            Margin = new Padding(Metrics.SpaceS, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
        };
        ButtonStyling.Apply(button, primary);
        return button;
    }

    private void UpdateHeaderIcon()
    {
        var px = LogicalToDeviceUnits(Metrics.DialogHeaderIcon);
        _headerIcon.Image = LucideIcons.Get(_iconName, px, Palette.Accent);
    }
}

internal static class ButtonStyling
{
    /// <summary>Styles a button. Colours are read from the current palette on every refresh (theme changes).</summary>
    public static void Apply(Button button, bool primary)
    {
        void Refresh()
        {
            var p = StorivaPalette.Current;
            if (primary)
            {
                button.BackColor = button.Enabled ? p.Accent : p.ProgressTrack;
                button.ForeColor = button.Enabled ? p.TextOnAccent : p.TextDisabled;
                button.FlatAppearance.BorderColor = button.Enabled ? p.AccentStrong : p.Border;
                button.FlatAppearance.MouseOverBackColor = p.AccentStrong;
                button.FlatAppearance.MouseDownBackColor = p.AccentStrong;
            }
            else
            {
                button.BackColor = p.Surface;
                button.ForeColor = button.Enabled ? p.TextPrimary : p.TextDisabled;
                button.FlatAppearance.BorderColor = p.BorderStrong;
                button.FlatAppearance.MouseOverBackColor = p.HoverFill;
                button.FlatAppearance.MouseDownBackColor = p.PressedFill;
            }
        }

        button.FlatAppearance.BorderSize = 1;
        button.EnabledChanged += (_, _) => Refresh();
        Refreshers[button] = Refresh;
        button.Disposed += (_, _) => Refreshers.Remove(button);
        Refresh();
    }

    private static readonly Dictionary<Button, Action> Refreshers = new();

    /// <summary>Re-applies the colours of every styled button (after a theme change).</summary>
    public static void RefreshAll()
    {
        foreach (var refresh in Refreshers.Values.ToList())
        {
            refresh();
        }
    }
}
