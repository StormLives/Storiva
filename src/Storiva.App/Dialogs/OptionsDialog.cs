using Storiva.App.Icons;
using Storiva.App.Theme;
using Storiva.Core.Activity;
using Storiva.Core.Settings;

namespace Storiva.App.Dialogs;

/// <summary>
/// Tools › Options (Alt+O) [F E4]. Eight pages, names from the reference option icons [D1]; a Form with
/// 8 tab pages exists in the reference metadata [R1]. Page contents are Storiva's own [S]. The theme
/// (Interface page) is saved in settings.json; the other options apply to the current session only.
/// </summary>
internal sealed class OptionsDialog : Form
{
    private static readonly string[] PageIcons =
        [Lucide.Settings, Lucide.PanelLeft, Lucide.CloudOff, Lucide.Download, Lucide.List, Lucide.FileText, Lucide.CircleAlert, Lucide.Settings];

    private static StorivaPalette _palette => StorivaPalette.Current;
    private readonly ListBox _pages = new();
    private readonly Panel _host = new();
    private readonly Label _pageTitle = new();
    private readonly Label _error = new();
    private readonly Button _ok = new() { Text = "OK" };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private readonly List<Control> _pageControls = [];

    // General
    private readonly CheckBox _openInPreview = new() { Text = "Double-clicking a file shows it in the Preview tab" };

    // Interface
    private readonly RadioButton _themeSystem = new() { Text = "System — follow the Windows light/dark setting" };
    private readonly RadioButton _themeLight = new() { Text = "Light" };
    private readonly RadioButton _themeDark = new() { Text = "Dark" };
    private readonly string _themeNote;
    private readonly CheckBox _showStatus = new() { Text = "Show the status bar" };
    private readonly CheckBox _showBuckets = new() { Text = "Show the bucket pane" };
    private readonly CheckBox _treeFolders = new() { Text = "Show folders in the bucket pane (otherwise only buckets)" };
    private readonly CheckBox _compactRows = new() { Text = "Compact rows (20 px) — uncheck for 24 px" };

    // Queueing
    private readonly NumericUpDown _concurrent = new() { Minimum = SessionOptions.MinConcurrentTasks, Maximum = SessionOptions.MaxConcurrentTasksLimit };

    // Logging
    private readonly ComboBox _logLevel = new() { DropDownStyle = ComboBoxStyle.DropDownList };


    /// <param name="themeNote">Current Windows setting and any persistence problem, shown under the theme choice.</param>
    public OptionsDialog(SessionOptions current, ThemePreference theme = ThemePreference.System, string themeNote = "")
    {
        _themeNote = themeNote;
        ThemeResult = theme;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = StorivaFonts.Ui;
        Text = "Options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = _palette.Surface;
        ClientSize = new Size(700, 440);

        _pages.Dock = DockStyle.Left;
        _pages.Width = 190;
        _pages.IntegralHeight = false;
        _pages.BorderStyle = BorderStyle.None;
        _pages.BackColor = _palette.Chrome;
        _pages.DrawMode = DrawMode.OwnerDrawFixed;
        _pages.ItemHeight = 28;
        _pages.AccessibleName = "Option pages";
        _pages.Items.AddRange(SessionOptions.PageNames);
        _pages.DrawItem += DrawPageItem;
        _pages.SelectedIndexChanged += (_, _) => ShowPage(_pages.SelectedIndex);

        _pageTitle.Dock = DockStyle.Top;
        _pageTitle.Font = StorivaFonts.Title;
        _pageTitle.Height = 36;
        _pageTitle.Padding = new Padding(Metrics.SpaceL, Metrics.SpaceS, 0, 0);
        _pageTitle.ForeColor = _palette.TextPrimary;

        _host.Dock = DockStyle.Fill;
        _host.Padding = new Padding(Metrics.SpaceL, 0, Metrics.SpaceL, 0);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(Metrics.SpaceS),
            BackColor = _palette.Chrome,
        };
        foreach (var button in new[] { _cancel, _ok })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(88, 30);
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            ButtonStyling.Apply(button, primary: button == _ok);
            footer.Controls.Add(button);
        }

        _error.AutoSize = true;
        _error.ForeColor = _palette.Danger;
        _error.Margin = new Padding(0, 8, Metrics.SpaceL, 0);
        footer.Controls.Add(_error);
        _ok.Click += (_, _) => Submit();
        AcceptButton = _ok;
        CancelButton = _cancel;

        BuildPages(current);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_host);
        right.Controls.Add(_pageTitle);
        Controls.Add(right);
        Controls.Add(_pages);
        Controls.Add(footer);
        ResumeLayout(performLayout: true);
        _pages.SelectedIndex = 0;
    }

    public SessionOptions? Result { get; private set; }

    /// <summary>Theme chosen on the Interface page (applied and saved by the main window).</summary>
    public ThemePreference ThemeResult { get; private set; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _pages.ItemHeight = LogicalToDeviceUnits(28); // owner-drawn item height is not auto-scaled
        ThemeApplier.ApplyInitial(this);
    }

    internal void ChooseTheme(ThemePreference preference)
    {
        _themeSystem.Checked = preference == ThemePreference.System;
        _themeLight.Checked = preference == ThemePreference.Light;
        _themeDark.Checked = preference == ThemePreference.Dark;
    }

    internal void ShowPageNamed(string name) => _pages.SelectedIndex = Array.IndexOf(SessionOptions.PageNames, name);

    internal int PageCount => _pages.Items.Count;

    internal IReadOnlyList<string> PageTitles => _pages.Items.Cast<string>().ToList();

    internal void SetConcurrent(int value) => _concurrent.Value = value;

    internal void SetCompactRows(bool value) => _compactRows.Checked = value;

    internal void ClickOk() => Submit();

    private void BuildPages(SessionOptions o)
    {
        _openInPreview.Checked = o.OpenFilesInPreview;
        _showStatus.Checked = o.ShowStatusBar;
        _showBuckets.Checked = o.ShowBucketPane;
        _treeFolders.Checked = o.ShowFoldersInTree;
        _compactRows.Checked = o.CompactRows;
        _concurrent.Value = o.MaxConcurrentTasks;
        _logLevel.Items.AddRange(["Information (everything)", "Warnings and errors", "Errors only"]);
        _logLevel.SelectedIndex = (int)o.MinimumLogLevel;
        ChooseTheme(ThemeResult);
        var themeGroup = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, Metrics.SpaceS) };
        themeGroup.Controls.Add(new Label { Text = "Appearance (saved for next time):", AutoSize = true, Font = StorivaFonts.UiBold, ForeColor = _palette.TextPrimary, Margin = new Padding(0, Metrics.SpaceXs, 0, 2) });
        foreach (var radio in new[] { _themeSystem, _themeLight, _themeDark })
        {
            radio.AutoSize = true;
            radio.Margin = new Padding(Metrics.SpaceS, 2, 0, 2);
            themeGroup.Controls.Add(radio);
        }

        themeGroup.AccessibleName = "Appearance";
        themeGroup.AccessibleRole = AccessibleRole.Grouping;
        if (_themeNote.Length > 0)
        {
            var note = Note(_themeNote);
            note.Margin = new Padding(Metrics.SpaceS, 2, 0, Metrics.SpaceS);
            themeGroup.Controls.Add(note);
        }

        _pageControls.Add(Page(_openInPreview, Note("Deleting on the server always asks for confirmation.")));
        _pageControls.Add(Page(themeGroup, _showStatus, _showBuckets, _treeFolders, _compactRows));
        _pageControls.Add(Page(Note("Real connections use the Windows proxy settings through .NET. Storiva has no proxy settings of its own yet.")));
        _pageControls.Add(Page(Note("Bandwidth limits are not available yet: real transfers use the full available bandwidth.")));
        _pageControls.Add(Page(Labeled("Simultaneous tasks:", _concurrent), Note("Uploads, downloads and other operations that run at the same time.")));
        _pageControls.Add(Page(Labeled("Event log level:", _logLevel), Note("Entries below this level are not recorded. Secrets are never written to the log.")));
        _pageControls.Add(Page(Note("Transfers are checked by the AWS SDK (checksums/length); a download whose length does not match is reported as failed.")));
        _pageControls.Add(Page(Note("The theme is saved in %LOCALAPPDATA%\\Storiva\\settings.json. The other options apply to this session only.")));
        foreach (var page in _pageControls)
        {
            page.Visible = false;
            _host.Controls.Add(page);
        }
    }

    private Control Page(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, Metrics.SpaceS, 0, 0),
        };
        foreach (var control in controls)
        {
            if (control is CheckBox box)
            {
                box.AutoSize = true;
                box.Margin = new Padding(0, Metrics.SpaceXs, 0, Metrics.SpaceXs);
            }

            panel.Controls.Add(control);
        }

        return panel;
    }

    private Control Labeled(string text, Control input)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, Metrics.SpaceXs, 0, Metrics.SpaceXs) };
        row.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, Metrics.SpaceS, 0), ForeColor = _palette.TextPrimary });
        input.Width = input is ComboBox ? 220 : 70;
        input.AccessibleName = text.TrimEnd(':');
        row.Controls.Add(input);
        return row;
    }

    private Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(440, 0),
        ForeColor = _palette.TextSecondary,
        Margin = new Padding(0, Metrics.SpaceS, 0, 0),
    };

    private void ShowPage(int index)
    {
        for (var i = 0; i < _pageControls.Count; i++)
        {
            _pageControls[i].Visible = i == index;
        }

        _pageTitle.Text = index >= 0 ? SessionOptions.PageNames[index] : string.Empty;
    }

    private void DrawPageItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            return;
        }

        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var brush = new SolidBrush(selected ? _palette.SelectionFocused : _palette.Chrome))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        var size = LogicalToDeviceUnits(Metrics.IconSmall);
        var icon = LucideIcons.Get(PageIcons[e.Index], size, _palette.IconDefault);
        var pad = LogicalToDeviceUnits(Metrics.SpaceS);
        e.Graphics.DrawImage(icon, e.Bounds.X + pad, e.Bounds.Y + (e.Bounds.Height - size) / 2, size, size);
        var textBounds = new Rectangle(e.Bounds.X + pad * 2 + size, e.Bounds.Y, e.Bounds.Width - pad * 3 - size, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, SessionOptions.PageNames[e.Index], Font, textBounds, _palette.TextPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        if (selected && _pages.Focused)
        {
            using var accent = new SolidBrush(_palette.FocusRing);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y, LogicalToDeviceUnits(3), e.Bounds.Height);
        }
    }

    private void Submit()
    {
        var result = new SessionOptions
        {
            OpenFilesInPreview = _openInPreview.Checked,
            ShowStatusBar = _showStatus.Checked,
            ShowBucketPane = _showBuckets.Checked,
            ShowFoldersInTree = _treeFolders.Checked,
            CompactRows = _compactRows.Checked,
            MaxConcurrentTasks = (int)_concurrent.Value,
            MinimumLogLevel = (ActivityLevel)Math.Max(0, _logLevel.SelectedIndex),
        };
        var error = result.Validate();
        _error.Text = error ?? string.Empty;
        if (error is not null)
        {
            return;
        }

        Result = result;
        ThemeResult = _themeDark.Checked ? ThemePreference.Dark : _themeLight.Checked ? ThemePreference.Light : ThemePreference.System;
        DialogResult = DialogResult.OK;
        Close();
    }
}
