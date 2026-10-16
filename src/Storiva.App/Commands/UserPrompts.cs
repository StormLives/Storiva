using Storiva.Core.Remote;

namespace Storiva.App.Commands;

/// <summary>Confirmations, notices and file pickers shown to the user. Abstracted so the smoke test can drive them.</summary>
internal interface IUserPrompts
{
    bool Confirm(IWin32Window owner, string title, string message);

    void Info(IWin32Window owner, string title, string message);

    void Warn(IWin32Window owner, string title, string message);

    /// <summary>Local files to upload, or null when canceled.</summary>
    IReadOnlyList<string>? PickFilesToUpload(IWin32Window owner, string title);

    /// <summary>A local folder (to upload, or to download into), or null when canceled.</summary>
    string? PickFolder(IWin32Window owner, string description);

    /// <summary>Where to save one downloaded file (the dialog asks before overwriting), or null when canceled.</summary>
    string? PickSaveFile(IWin32Window owner, string suggestedName);

    /// <summary>What to do with files that already exist in the download folder, or null when canceled.</summary>
    ConflictPolicy? ChooseConflictPolicy(IWin32Window owner, string message);
}

internal sealed class MessageBoxPrompts : IUserPrompts
{
    public bool Confirm(IWin32Window owner, string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;

    public void Info(IWin32Window owner, string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void Warn(IWin32Window owner, string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public IReadOnlyList<string>? PickFilesToUpload(IWin32Window owner, string title)
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = title, CheckFileExists = true, DereferenceLinks = true, RestoreDirectory = true };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileNames : null;
    }

    public string? PickFolder(IWin32Window owner, string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public string? PickSaveFile(IWin32Window owner, string suggestedName)
    {
        using var dialog = new SaveFileDialog { FileName = suggestedName, OverwritePrompt = true, Title = "Download to", RestoreDirectory = true, AddExtension = false };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    public ConflictPolicy? ChooseConflictPolicy(IWin32Window owner, string message)
    {
        var keep = new TaskDialogButton("Keep both (save as \"name (1)\")");
        var overwrite = new TaskDialogButton("Overwrite existing files");
        var skip = new TaskDialogButton("Skip existing files");
        var page = new TaskDialogPage
        {
            Caption = "Download",
            Heading = "Files with the same name may already exist",
            Text = message,
            Icon = TaskDialogIcon.Warning,
            Buttons = { keep, overwrite, skip, TaskDialogButton.Cancel },
            DefaultButton = keep,
            AllowCancel = true,
        };
        var result = TaskDialog.ShowDialog(owner, page);
        return result == keep ? ConflictPolicy.KeepBoth : result == overwrite ? ConflictPolicy.Overwrite : result == skip ? ConflictPolicy.Skip : null;
    }
}

/// <summary>Non-interactive prompts for the smoke test: answers every confirmation and records every message.</summary>
internal sealed class RecordingPrompts(bool confirmAnswer = true) : IUserPrompts
{
    public List<(string Kind, string Title, string Message)> Shown { get; } = [];

    /// <summary>Answers for the pickers (null = "canceled").</summary>
    public IReadOnlyList<string>? FilesToUpload { get; set; }

    public string? Folder { get; set; }

    public string? SaveFile { get; set; }

    public ConflictPolicy? Conflict { get; set; } = ConflictPolicy.KeepBoth;

    public bool ConfirmAnswer { get; set; } = confirmAnswer;

    public bool Confirm(IWin32Window owner, string title, string message)
    {
        Shown.Add(("confirm", title, message));
        return ConfirmAnswer;
    }

    public void Info(IWin32Window owner, string title, string message) => Shown.Add(("info", title, message));

    public void Warn(IWin32Window owner, string title, string message) => Shown.Add(("warn", title, message));

    public IReadOnlyList<string>? PickFilesToUpload(IWin32Window owner, string title)
    {
        Shown.Add(("pick-files", title, string.Empty));
        return FilesToUpload;
    }

    public string? PickFolder(IWin32Window owner, string description)
    {
        Shown.Add(("pick-folder", description, string.Empty));
        return Folder;
    }

    public string? PickSaveFile(IWin32Window owner, string suggestedName)
    {
        Shown.Add(("pick-save", suggestedName, string.Empty));
        return SaveFile;
    }

    public ConflictPolicy? ChooseConflictPolicy(IWin32Window owner, string message)
    {
        Shown.Add(("conflict", "Download", message));
        return Conflict;
    }
}
