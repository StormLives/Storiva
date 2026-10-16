using Storiva.App.Icons;
using Storiva.Core.Details;

namespace Storiva.App.Dialogs;

public enum KeyValueKind
{
    Header,
    Tag,
}

/// <summary>
/// Add/Edit dialog for one tag (validated with <see cref="TagRules"/>). The change is only sent to the server
/// by "Apply changes" in the Tags tab, after a confirmation. Reference: icons *_dialog_add / *_dialog_edit exist
/// [D1]; the layout itself is a hypothesis [H].
/// </summary>
internal sealed class KeyValueDialog : StorivaDialog
{
    private readonly IReadOnlyList<string> _otherKeys;
    private readonly int _maxItems;
    private readonly TextBox _key = new();
    private readonly TextBox _value = new();
    private readonly Label _keyError;
    private readonly Label _valueError;

    public KeyValueDialog(bool isEdit, string key, string value, IEnumerable<string> otherKeys, int maxItems, string target)
        : base(
            isEdit ? "Edit Tag" : "Add Tag",
            $"{(isEdit ? "Change" : "Add")} a tag of {target}. It is sent to the server only with \"Apply changes\".",
            Lucide.Pencil,
            isEdit ? "Save" : "Add")
    {
        _otherKeys = otherKeys.ToList();
        _maxItems = maxItems;
        _key.Text = key;
        _value.Text = value;
        _keyError = AddField("Key:", _key, $"Up to {TagRules.MaxKeyLength} characters. Keys are case-sensitive and unique.");
        _valueError = AddField("Value:", _value, $"Up to {TagRules.MaxValueLength} characters (may be empty).");
        _key.TextChanged += (_, _) => Revalidate(showMessage: true);
        _value.TextChanged += (_, _) => Revalidate(showMessage: true);
        Revalidate(showMessage: false);
    }

    public string Key => _key.Text.Trim();

    public string Value => _value.Text;

    internal bool CanSubmit => PrimaryButton.Enabled;

    internal string VisibleErrors => string.Join(" | ", new[] { _keyError, _valueError }.Where(l => l.Visible).Select(l => l.Text));

    internal void Type(string key, string value)
    {
        _key.Text = key;
        _value.Text = value;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        (_key.TextLength == 0 ? _key : _value).Focus();
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
        var tag = TagRules.Validate(_key.Text.Trim(), _value.Text, _otherKeys, _maxItems);
        var isValueProblem = tag?.StartsWith("Tag values", StringComparison.Ordinal) == true;
        var keyMessage = isValueProblem ? null : tag;
        var valueMessage = isValueProblem ? tag : null;
        ShowError(_keyError, _key, showMessage ? keyMessage : null);
        ShowError(_valueError, _value, showMessage ? valueMessage : null);
        PrimaryButton.Enabled = keyMessage is null && valueMessage is null;
        return PrimaryButton.Enabled;
    }
}
