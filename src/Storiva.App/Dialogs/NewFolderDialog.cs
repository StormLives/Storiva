using Storiva.App.Icons;
using Storiva.Core.Storage;

namespace Storiva.App.Dialogs;

internal sealed class NewFolderDialog : StorivaDialog
{
    private readonly IReadOnlyList<string> _existingNames;
    private readonly TextBox _name = new() { Text = "New folder" };
    private readonly Label _error;

    public NewFolderDialog(StoragePath parent, IEnumerable<string> existingNames)
        : base(
            "New folder",
            $"Create an empty folder in {parent.ToDisplayString()}. S3 stores it as a zero-byte object whose key ends with \"/\".",
            Lucide.FolderPlus,
            "Create")
    {
        _existingNames = existingNames.ToList();
        _error = AddField("Folder name:", _name, $"Up to {FolderNameRules.MaxLength} characters. Not allowed: / \\ : * ? \" < > |");
        _name.TextChanged += (_, _) => Revalidate(showMessage: true);
        Revalidate(showMessage: false);
    }

    public string FolderName => _name.Text;

    // Smoke-test hooks.
    internal bool CanSubmit => PrimaryButton.Enabled;

    internal string VisibleError => _error.Visible ? _error.Text : string.Empty;

    internal void TypeName(string text) => _name.Text = text;

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
        var message = FolderNameRules.Validate(_name.Text, _existingNames);
        ShowError(_error, _name, showMessage ? message : null);
        PrimaryButton.Enabled = message is null;
        return message is null;
    }
}
