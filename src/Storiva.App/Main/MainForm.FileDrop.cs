using System.Diagnostics;
using Storiva.Core.Activity;
using Storiva.Core.Commands;
using Storiva.Core.Storage;

namespace Storiva.App.Main;

/// <summary>
/// Files and folders dragged from Windows Explorer. Dropped on the object list they are uploaded into the current
/// location; dropped on a bucket (or folder) of the bucket pane, into that location. The upload is the one of
/// Files › Upload (same tasks, same replace confirmations). Only while connected and inside a bucket: elsewhere the
/// pointer shows "no drop" and nothing happens. Storiva only offers Copy, so Explorer never deletes the originals.
/// </summary>
internal sealed partial class MainForm
{
    private TreeNode? _treeDropNode;
    private string? _statusBeforeDrag;

    /// <summary>The upload started by the last drop (smoke test).</summary>
    internal Task<bool>? LastDropUpload { get; private set; }

    private void WireFileDrop()
    {
        foreach (var control in new Control[] { _grid, _tree })
        {
            control.AllowDrop = true;
            control.DragEnter += (_, e) => OnFileDragOver(control, e);
            control.DragOver += (_, e) => OnFileDragOver(control, e);
            control.DragLeave += (_, _) => EndFileDrag();
            control.DragDrop += (_, e) => OnFileDragDrop(control, e);
        }
    }

    /// <summary>Where files dropped at screen point (x, y) on <paramref name="control"/> would be uploaded, or null.</summary>
    private StoragePath? DropTargetAt(Control control, int x, int y)
    {
        if (!IsConnected)
        {
            return null;
        }

        var target = control == _tree ? _tree.GetNodeAt(_tree.PointToClient(new Point(x, y)))?.Tag as StoragePath : _navigator.Current;
        // Same rule as Files › Upload, evaluated for the drop location (never the root: it holds only buckets).
        return target is not null && CommandRules.IsEnabled(CommandId.FileUploadFiles, BuildContext() with { Location = target }) ? target : null;
    }

    private static bool HasFiles(IDataObject? data) => data?.GetDataPresent(DataFormats.FileDrop) == true;

    private void OnFileDragOver(Control control, DragEventArgs e)
    {
        var target = HasFiles(e.Data) && (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DropTargetAt(control, e.X, e.Y) : null;
        e.Effect = target is null ? DragDropEffects.None : DragDropEffects.Copy;
        ShowDropTarget(control == _tree && target is not null ? _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y))) : null, target);
    }

    /// <summary>Feedback while dragging: the target node is outlined in the bucket pane and the status bar names the location.</summary>
    private void ShowDropTarget(TreeNode? node, StoragePath? target)
    {
        if (node != _treeDropNode)
        {
            _treeDropNode = node;
            _tree.Invalidate();
        }

        if (target is null)
        {
            if (_statusBeforeDrag is { } previous)
            {
                _statusBeforeDrag = null;
                SetStatus(previous);
            }

            return;
        }

        _statusBeforeDrag ??= _statusMessage.Text ?? string.Empty;
        SetStatus($"Drop to upload into {target.ToDisplayString()}");
    }

    private void EndFileDrag() => ShowDropTarget(null, null);

    private void OnFileDragDrop(Control control, DragEventArgs e)
    {
        var target = DropTargetAt(control, e.X, e.Y);
        EndFileDrag();
        if (target is null || e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Copy;
        // Explorer waits until this handler returns: ask the confirmations once the drop itself has completed.
        BeginInvoke(() => LastDropUpload = UploadDroppedAsync(target, paths));
    }

    /// <summary>
    /// Uploads dropped files and folders into <paramref name="target"/>. A target other than the current location is
    /// opened first, and its listing awaited, so the replace confirmations see the objects that are already there.
    /// </summary>
    internal async Task<bool> UploadDroppedAsync(StoragePath target, IReadOnlyList<string> paths)
    {
        if (_remote is not { } session || target.IsRoot)
        {
            return false;
        }

        var drop = ExplorerDrop.Classify(paths);
        foreach (var (path, reason) in drop.Skipped)
        {
            _activity.Write($"Not uploaded: {path} ({reason}).", ActivityLevel.Warning);
        }

        if (drop.Files.Count == 0 && drop.Folders.Count == 0)
        {
            SetStatus("Nothing to upload: the dropped items are not files or folders on this computer.");
            return false;
        }

        if (target != _navigator.Current && !_navigator.NavigateTo(target))
        {
            SetStatus($"{target.ToDisplayString()} is no longer available. Nothing was uploaded.");
            return false;
        }

        if (!await WaitForListingAsync(session, target) || session != _remote || _navigator.Current != target)
        {
            SetStatus($"Nothing was uploaded: {target.ToDisplayString()} could not be listed, or the location changed meanwhile. Drop the items again.");
            return false;
        }

        if (drop.Files.Count > 0 && !UploadLocalFiles(drop.Files))
        {
            return false;
        }

        foreach (var folder in drop.Folders)
        {
            if (!UploadLocalFolder(folder))
            {
                return false;
            }
        }

        var count = drop.Files.Count + drop.Folders.Count;
        SetStatus($"{Plural(count, "dropped item", "dropped items")} queued for upload into {target.ToDisplayString()}. Progress is shown in the Tasks tab.");
        return true;
    }

    /// <summary>
    /// True once <paramref name="path"/> is listed (at once when cached). Navigating started the listing; it has no
    /// awaitable handle, so its state is polled. False when it failed or the connection changed.
    /// </summary>
    private async Task<bool> WaitForListingAsync(RemoteSession session, StoragePath path)
    {
        var watch = Stopwatch.StartNew();
        while (session == _remote && !session.Catalog.IsLoaded(path))
        {
            if (!_listingInProgress.Contains(path) || watch.Elapsed > TimeSpan.FromSeconds(60))
            {
                return false;
            }

            await Task.Delay(50);
        }

        return session == _remote;
    }

    // ---------- smoke test ----------

    // Explorer offers every effect; Storiva must still answer Copy only.
    private const DragDropEffects ExplorerEffects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

    /// <summary>
    /// Simulates Explorer dragging <paramref name="paths"/> over the object list, or over the bucket pane node of
    /// <paramref name="treeTarget"/>, through the real DragOver handler (no mouse input). Returns the offered effect.
    /// </summary>
    internal DragDropEffects SimulateFileDragOver(IReadOnlyList<string> paths, StoragePath? treeTarget = null)
    {
        var (control, point) = SimulatedDragPoint(treeTarget);
        var over = new DragEventArgs(new DataObject(DataFormats.FileDrop, paths.ToArray()), 0, point.X, point.Y, ExplorerEffects, DragDropEffects.None);
        OnFileDragOver(control, over);
        return over.Effect;
    }

    /// <summary>Drag over, then drop (see <see cref="SimulateFileDragOver"/>). Returns the effect offered while dragging (None = refused).</summary>
    internal DragDropEffects SimulateFileDrop(IReadOnlyList<string> paths, StoragePath? treeTarget = null)
    {
        LastDropUpload = null;
        var effect = SimulateFileDragOver(paths, treeTarget);
        if (effect == DragDropEffects.None)
        {
            EndFileDrag(); // like the user moving away (DragLeave)
            return DragDropEffects.None;
        }

        var (control, point) = SimulatedDragPoint(treeTarget);
        OnFileDragDrop(control, new DragEventArgs(new DataObject(DataFormats.FileDrop, paths.ToArray()), 0, point.X, point.Y, ExplorerEffects, effect));
        return effect;
    }

    private (Control Control, Point Screen) SimulatedDragPoint(StoragePath? treeTarget)
    {
        if (treeTarget is null)
        {
            return (_grid, _grid.PointToScreen(new Point(_grid.ClientSize.Width / 2, _grid.ClientSize.Height / 2)));
        }

        var node = FindNode(treeTarget, load: true);
        if (node is null || !treeTarget.Equals(node.Tag as StoragePath))
        {
            throw new InvalidOperationException($"No tree node for {treeTarget}.");
        }

        return (_tree, _tree.PointToScreen(new Point(node.Bounds.Left + node.Bounds.Width / 2, node.Bounds.Top + node.Bounds.Height / 2)));
    }

    /// <summary>The bucket pane node outlined as the drop target, if any.</summary>
    internal string? TreeDropHighlight => _treeDropNode?.Text;
}

/// <summary>Sorts paths dropped from Explorer into files and folders that can be uploaded.</summary>
internal static class ExplorerDrop
{
    internal sealed record Contents(IReadOnlyList<string> Files, IReadOnlyList<string> Folders, IReadOnlyList<(string Path, string Reason)> Skipped);

    public static Contents Classify(IEnumerable<string> paths)
    {
        var files = new List<string>();
        var folders = new List<string>();
        var skipped = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                skipped.Add((raw, "not a valid path"));
                continue;
            }

            if (Directory.Exists(full))
            {
                var trimmed = Path.TrimEndingDirectorySeparator(full);
                // A whole drive or network share has no folder name to upload under (and is rarely meant).
                if (string.Equals(trimmed, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty), StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add((raw, "a whole drive is not uploaded; drop its folders instead"));
                }
                else if (seen.Add(trimmed))
                {
                    folders.Add(trimmed);
                }
            }
            else if (File.Exists(full))
            {
                if (seen.Add(full))
                {
                    files.Add(full);
                }
            }
            else
            {
                skipped.Add((raw, "not found on this computer"));
            }
        }

        return new Contents(files, folders, skipped);
    }
}
