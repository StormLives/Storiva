using Storiva.App.Icons;
using Storiva.App.Theme;
using Storiva.Core.Details;
using Storiva.Core.Storage;

namespace Storiva.App.Dialogs;

/// <summary>
/// "Create New Bucket" [F E7]: bucket name (pre-selected default "new-bucket-xxxxxxxx"), bucket region,
/// "show more settings.." link, Create new bucket / Cancel. The bucket is created on the server of the connection.
/// </summary>
internal sealed class CreateBucketDialog : StorivaDialog
{
    private readonly IReadOnlyList<string> _existing;
    private readonly TextBox _name = new();
    private readonly ComboBox _region = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _versioning;
    private readonly TableLayoutPanel _more;
    private readonly LinkLabel _moreLink;
    private readonly Label _error;

    /// <param name="isAws">True for AWS S3 (region chosen here), false for an S3-compatible server (region of the connection).</param>
    public CreateBucketDialog(IEnumerable<string> existingBuckets, IReadOnlyList<string> regions, string suggestedName, Action<string> showHelp,
        string? defaultRegion = null, bool isAws = true)
        : base("Create New Bucket", "Specify the bucket name and optional parameters and click Create new bucket.", Lucide.Database, "Create new bucket")
    {
        _existing = existingBuckets.ToList();
        AddHeaderLink("online help", () => showHelp(
            "Create New Bucket\r\n\r\n" +
            "• " + BucketNameRules.HelpText + "\r\n" +
            $"• {BucketNameRules.MinLength}–{BucketNameRules.MaxLength} characters, starting and ending with a letter or a number.\r\n" +
            (isAws
                ? "• The bucket is created on AWS in the chosen region. Bucket names are global: another account may already use the name."
                : "• The bucket is created on the server of this connection; the server decides where it is stored.")));

        _name.Text = suggestedName;
        _error = AddField("Bucket name:", _name, BucketNameRules.HelpText);
        foreach (var region in regions)
        {
            _region.Items.Add(region);
        }

        var preferred = defaultRegion is null ? -1 : _region.Items.IndexOf(defaultRegion);
        _region.SelectedIndex = preferred >= 0 ? preferred : _region.Items.Count > 0 ? 0 : -1;
        _region.Enabled = isAws;
        AddField("Bucket region:", _region, isAws
            ? "The AWS region where the bucket is created (it cannot be changed later)."
            : "S3-compatible servers decide the location themselves; this is the region of the connection.");

        _more = AddSection(visible: false);
        _versioning = AddCheckBox("Enable versioning",
            "Keeps previous versions of every object (billed as storage). Can later be suspended, never turned off.",
            initial: false, _more);
        _moreLink = AddFooterLink("show more settings..", () =>
        {
            _more.Visible = !_more.Visible;
            _moreLink!.Text = _more.Visible ? "hide more settings" : "show more settings..";
        });

        _name.TextChanged += (_, _) => Revalidate(showMessage: true);
        Revalidate(showMessage: false);
    }

    public string BucketName => _name.Text;

    public string BucketRegion => _region.SelectedItem as string ?? string.Empty;

    public bool EnableVersioning => _versioning.Checked;

    // Smoke-test hooks.
    internal bool CanSubmit => PrimaryButton.Enabled;

    internal string VisibleError => _error.Visible ? _error.Text : string.Empty;

    internal bool NameIsSelected => _name.SelectionLength == _name.TextLength && _name.TextLength > 0;

    internal void TypeName(string text) => _name.Text = text;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _name.Focus();
        _name.SelectAll(); // the reference pre-selects the suggested name [F E7]
    }

    protected override void OnPrimaryClick()
    {
        if (Revalidate(showMessage: true))
        {
            base.OnPrimaryClick();
        }
    }

    private bool Revalidate(bool showMessage)
    {
        var message = BucketNameRules.Validate(_name.Text, _existing);
        ShowError(_error, _name, showMessage ? message : null);
        PrimaryButton.Enabled = message is null && _region.SelectedIndex >= 0;
        return message is null;
    }
}

/// <summary>"Add External Bucket" [F menu item E6; layout H]: opens a bucket by name (e.g. another account's).</summary>
internal sealed class AddExternalBucketDialog : StorivaDialog
{
    private readonly IReadOnlyList<string> _existing;
    private readonly TextBox _name = new();
    private readonly Label _error;

    public AddExternalBucketDialog(IEnumerable<string> existingBuckets)
        : base("Add External Bucket", "Open a bucket that is not in your bucket list, for example one shared by another account.", Lucide.Plus, "Add bucket")
    {
        _existing = existingBuckets.ToList();
        _error = AddField("Bucket name:", _name, BucketNameRules.HelpText);
        AddNotice("Storiva checks that the bucket exists and can be reached (a read-only HEAD request). What you may do in it depends on its policies.");
        _name.TextChanged += (_, _) => Revalidate(showMessage: true);
        Revalidate(showMessage: false);
    }

    public string BucketName => _name.Text;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _name.Focus();
        _name.SelectAll();
    }

    protected override void OnPrimaryClick()
    {
        if (Revalidate(showMessage: true))
        {
            base.OnPrimaryClick();
        }
    }

    private bool Revalidate(bool showMessage)
    {
        var message = BucketNameRules.Validate(_name.Text, _existing);
        ShowError(_error, _name, showMessage ? message : null);
        PrimaryButton.Enabled = message is null;
        return message is null;
    }
}

/// <summary>"Edit Versioning Settings" [F menu item E6; layout H].</summary>
internal sealed class VersioningDialog : StorivaDialog
{
    private readonly RadioButton _enabled = new() { Text = "Enabled — keep previous versions of objects", AutoSize = true };
    private readonly RadioButton _suspended = new() { Text = "Suspended — stop creating new versions, keep existing ones", AutoSize = true };
    private readonly RadioButton _disabled = new() { Text = "Disabled — never enabled (cannot be selected again once enabled)", AutoSize = true };

    public VersioningDialog(string bucket, VersioningStatus current)
        : base("Versioning Settings", $"Versioning of bucket \"{bucket}\" on the server.", Lucide.Database, "Apply")
    {
        foreach (var radio in new[] { _enabled, _suspended, _disabled })
        {
            radio.Margin = new Padding(Metrics.SpaceL, 4, 0, 4);
            Body.Controls.Add(radio);
        }

        _enabled.Checked = current == VersioningStatus.Enabled;
        _suspended.Checked = current == VersioningStatus.Suspended;
        _disabled.Checked = current == VersioningStatus.Disabled;
        _disabled.Enabled = current == VersioningStatus.Disabled; // same rule as S3
    }

    public VersioningStatus Selected =>
        _enabled.Checked ? VersioningStatus.Enabled : _suspended.Checked ? VersioningStatus.Suspended : VersioningStatus.Disabled;
}

/// <summary>"Rename.." (F2) [F menu item E5; layout H].</summary>
internal sealed class RenameDialog : StorivaDialog
{
    private readonly IReadOnlyList<string> _siblings;
    private readonly string _original;
    private readonly TextBox _name = new();
    private readonly Label _error;

    public RenameDialog(StorageItem item, IEnumerable<string> siblingNames)
        : base("Rename", $"Rename {(item.IsContainer ? "folder" : "file")} \"{item.Name}\". S3 copies the object(s) to the new name and then deletes the original(s).", Lucide.Pencil, "Rename")
    {
        _original = item.Name;
        _siblings = siblingNames.Where(n => !string.Equals(n, item.Name, StringComparison.Ordinal)).ToList();
        _name.Text = item.Name;
        _error = AddField("New name:", _name, $"Up to {FolderNameRules.MaxLength} characters. Not allowed: / \\ : * ? \" < > |");
        _name.TextChanged += (_, _) => Revalidate(showMessage: true);
        Revalidate(showMessage: false);
    }

    public string NewName => _name.Text;

    internal bool CanSubmit => PrimaryButton.Enabled;

    internal void TypeName(string text) => _name.Text = text;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _name.Focus();
        // Select the stem only, like Windows Explorer.
        var dot = _original.LastIndexOf('.');
        _name.Select(0, dot > 0 ? dot : _original.Length);
    }

    protected override void OnPrimaryClick()
    {
        if (Revalidate(showMessage: true))
        {
            base.OnPrimaryClick();
        }
    }

    private bool Revalidate(bool showMessage)
    {
        var message = FolderNameRules.Validate(_name.Text, _siblings);
        ShowError(_error, _name, showMessage ? message?.Replace("folder name", "name", StringComparison.Ordinal) : null);
        PrimaryButton.Enabled = message is null;
        return message is null;
    }
}
