using System.Windows.Forms;
using Storiva.App.Main;
using Storiva.Core.Commands;
using Storiva.Core.Storage;

namespace Storiva.Tests;

public class CommandRulesTests
{
    private static readonly StoragePath Images = StoragePath.Parse("team-assets/images");
    private static readonly StorageItem File = new(Images.Append("banner.png"), StorageItemKind.File, 10, null, "STANDARD");
    private static readonly StorageItem Folder = new(Images.Append("icons"), StorageItemKind.Folder, null, null, null);
    private static readonly StorageItem Bucket = new(StoragePath.Parse("team-assets"), StorageItemKind.Bucket, null, null, null);

    private static SelectionContext At(StoragePath location, IReadOnlyList<StorageItem>? selected = null, StoragePath? treeBucket = null,
        int clipboard = 0, int listCount = 5, bool connected = true) =>
        new(location, selected ?? [], treeBucket, FocusArea.List, clipboard, ListCount: listCount, Connected: connected);

    private static bool On(CommandId id, SelectionContext c) => CommandRules.IsEnabled(id, c);

    [Fact]
    public void Home_with_nothing_selected_matches_the_reference_captures()
    {
        var c = At(StoragePath.Root, listCount: 3);

        // Files [F E5]
        Assert.False(On(CommandId.FileOpen, c));
        Assert.False(On(CommandId.FileDownload, c));
        Assert.False(On(CommandId.FileCut, c));
        Assert.False(On(CommandId.FilePaste, c));
        Assert.False(On(CommandId.FileRename, c));
        Assert.False(On(CommandId.FileDelete, c));
        Assert.False(On(CommandId.FileEditAcl, c));
        Assert.False(On(CommandId.FileGenerateUrl, c));
        Assert.False(On(CommandId.FileProperties, c));
        Assert.True(On(CommandId.FileRefresh, c));

        // Buckets [F E6]: only Create, Refresh and Add External
        Assert.True(On(CommandId.BucketCreate, c));
        Assert.True(On(CommandId.BucketRefresh, c));
        Assert.True(On(CommandId.BucketAddExternal, c));
        Assert.False(On(CommandId.BucketDelete, c));
        Assert.False(On(CommandId.BucketEditPolicy, c));
        Assert.False(On(CommandId.BucketProperties, c));

        // Tools [F E4]
        Assert.True(On(CommandId.ToolsOptions, c));
        Assert.True(On(CommandId.ToolsAccessManager, c));
        Assert.False(On(CommandId.ToolsBucketSharingWizard, c));
    }

    [Fact]
    public void Upload_and_create_folder_need_a_bucket_location_decision_D30() =>
        Assert.False(On(CommandId.FileUploadFiles, At(StoragePath.Root)) || On(CommandId.FileCreateFolder, At(StoragePath.Root)));

    [Fact]
    public void Single_file_enables_the_file_commands()
    {
        var c = At(Images, [File], treeBucket: Bucket.Path);
        foreach (var id in new[] { CommandId.FileOpen, CommandId.FileDownload, CommandId.FileCut, CommandId.FileCopy, CommandId.FileRename,
                     CommandId.FileDelete, CommandId.FileEditAcl, CommandId.FileEditHeaders, CommandId.FileEditTags, CommandId.FileGenerateUrl,
                     CommandId.FileChangeStorageClass, CommandId.FileProperties })
        {
            Assert.True(On(id, c), id.ToString());
        }
    }

    [Fact]
    public void Folder_selection_disables_file_only_commands()
    {
        var c = At(Images, [Folder], treeBucket: Bucket.Path);
        Assert.True(On(CommandId.FileDownload, c)); // downloads every object under the prefix
        Assert.False(On(CommandId.FileEditHeaders, c));
        Assert.False(On(CommandId.FileEditTags, c));
        Assert.False(On(CommandId.FileChangeStorageClass, c));
        Assert.True(On(CommandId.FileRename, c));
        Assert.True(On(CommandId.FileEditAcl, c));
        Assert.True(On(CommandId.FileOpen, c));
    }

    [Fact]
    public void Multiple_selection_disables_single_item_commands()
    {
        var c = At(Images, [File, Folder]);
        Assert.False(On(CommandId.FileOpen, c));
        Assert.False(On(CommandId.FileRename, c));
        Assert.False(On(CommandId.FileProperties, c));
        Assert.True(On(CommandId.FileDelete, c));
        Assert.True(On(CommandId.FileDownload, c)); // at least one file
    }

    [Fact]
    public void Bucket_selected_in_the_Home_list_enables_bucket_commands()
    {
        var c = At(StoragePath.Root, [Bucket]);
        Assert.Equal(Bucket.Path, c.ActiveBucket);
        Assert.True(On(CommandId.BucketDelete, c));
        Assert.True(On(CommandId.BucketEditTags, c));
        Assert.True(On(CommandId.BucketProperties, c));
        Assert.False(On(CommandId.FileDelete, c)); // buckets are not deleted from the Files menu
        Assert.False(On(CommandId.FileCopy, c));
    }

    [Fact]
    public void Paste_needs_clipboard_items_and_a_bucket()
    {
        Assert.False(On(CommandId.FilePaste, At(Images)));
        Assert.True(On(CommandId.FilePaste, At(Images, clipboard: 2)));
        Assert.False(On(CommandId.FilePaste, At(StoragePath.Root, clipboard: 2)));
        Assert.False(On(CommandId.FilePaste, At(Images, clipboard: 2, connected: false)));
    }

    [Fact]
    public void Select_all_needs_items_in_the_list() =>
        Assert.False(On(CommandId.FileSelectAll, At(Images, listCount: 0)) || On(CommandId.FileInvertSelection, At(Images, listCount: 0)));

    [Fact]
    public void Navigation_commands_follow_history()
    {
        var c = At(Images) with { CanGoBack = true, CanGoForward = false };
        Assert.True(On(CommandId.NavigateBack, c));
        Assert.False(On(CommandId.NavigateForward, c));
        Assert.True(On(CommandId.NavigateUp, c));
        Assert.False(On(CommandId.NavigateUp, At(StoragePath.Root)));
        Assert.False(On(CommandId.BookmarkToggle, At(StoragePath.Root)));
    }
}

public class CommandCatalogTests
{
    private static List<string> Texts(string menu) => CommandCatalog.Menus.First(m => m.Title.Replace("&", "") == menu).Entries
        .Select(e => e switch { CommandEntry c => CommandCatalog.Get(c.Id).Text, SubmenuEntry s => s.Title ?? CommandCatalog.Get(s.Id).Text, _ => null })
        .OfType<string>().ToList();

    [Fact]
    public void Menu_bar_follows_the_reference_order() =>
        Assert.Equal(["Accounts", "Buckets", "Files", "Bookmarks", "Tools", "Help"], CommandCatalog.Menus.Select(m => m.Title.Replace("&", "")));

    [Fact]
    public void Files_menu_is_the_transcription_of_capture_E5() => Assert.Equal(
        ["Open", "Download", "Upload file(s)", "Upload folder(s)", "Cut", "Copy", "Paste", "Copy to..", "Move to..", "Rename..", "Delete",
         "Create New Folder..", "Refresh", "Edit Permissions (ACL)", "Edit HTTP Headers", "Edit Object Tags", "Generate Web URL..",
         "Select All", "Invert Selection", "Change Storage Class to ..", "Server Side Encryption ..", "Object Lock", "Advanced", "Properties"],
        Texts("Files"));

    [Fact]
    public void Buckets_menu_has_the_29_items_of_capture_E6()
    {
        var texts = Texts("Buckets");
        Assert.Equal(29, texts.Count);
        Assert.Equal("Create New Bucket..", texts[0]);
        Assert.Equal("Properties", texts[^1]);
        Assert.Contains("Intelligent Tiering Configuration..", texts);
    }

    [Fact]
    public void Tools_menu_starts_with_the_15_items_of_capture_E4()
    {
        var texts = Texts("Tools");
        Assert.Equal(
            ["Default HTTP Headers..", "Default Object Tags..", "Default Storage Classes..", "Uploading Filters..", "Compression and Encryption..",
             "Server-Side Encryption..", "Object Lock Retention Rules..", "Access Manager (IAM)..", "Bucket Sharing Wizard..", "CloudFront Manager..",
             "Folder Sync Tool..", "Uncompleted Multipart Uploads..", "Security Scan..", "Diagnostics..", "Options.."],
            texts.Take(15));
    }

    [Theory]
    [InlineData(CommandId.FileDownload, "Ctrl+D")]
    [InlineData(CommandId.FileUploadFolders, "Ctrl+Shift+U")]
    [InlineData(CommandId.FileRename, "F2")]
    [InlineData(CommandId.FileMoveTo, "Ctrl+Shift+X")]
    [InlineData(CommandId.BucketAddExternal, "Ctrl+E")]
    [InlineData(CommandId.BucketEditVersioning, "Ctrl+Shift+V")]
    [InlineData(CommandId.ToolsOptions, "Alt+O")]
    public void Shortcuts_match_the_captures(CommandId id, string shortcut) => Assert.Equal(shortcut, CommandCatalog.Get(id).Shortcut);

    [Fact]
    public void Every_command_is_defined_once_and_unavailable_ones_explain_why()
    {
        Assert.Equal(Enum.GetValues<CommandId>().Length, CommandCatalog.Definitions.Count);
        Assert.Equal(CommandCatalog.Definitions.Count, CommandCatalog.Definitions.Select(d => d.Id).Distinct().Count());
        Assert.All(CommandCatalog.Definitions.Where(d => d.Behavior == CommandBehavior.Unavailable),
            d => Assert.False(string.IsNullOrWhiteSpace(d.UnavailableReason), d.Id.ToString()));
        Assert.All(CommandCatalog.Definitions, d => Assert.Contains(d.Evidence, new[] { "F", "H", "S" }));
    }

    [Fact]
    public void Remote_or_system_changing_operations_are_never_local()
    {
        foreach (var id in new[] { CommandId.FileGenerateUrl, CommandId.BucketEditPolicy, CommandId.BucketMountDrive, CommandId.ToolsAccessManager,
                     CommandId.ToolsFolderSync, CommandId.HelpCheckUpdates, CommandId.ToolsDiagnostics })
        {
            Assert.Equal(CommandBehavior.Unavailable, CommandCatalog.Get(id).Behavior);
        }
    }
}

public class ShortcutRouterTests
{
    [Theory]
    [InlineData("ctrl + shift+n", "Ctrl+Shift+N")]
    [InlineData("Shift+Ctrl+U", "Ctrl+Shift+U")]
    [InlineData("delete", "Del")]
    [InlineData("alt+o", "Alt+O")]
    [InlineData("F5", "F5")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, ShortcutRouter.Normalize(input));

    [Fact]
    public void The_ambiguous_reference_shortcuts_are_detected()
    {
        var ambiguous = ShortcutRouter.AmbiguousShortcuts.ToList();
        foreach (var shortcut in new[] { "Ctrl+N", "Del", "F5", "Ctrl+L", "Ctrl+T", "Ctrl+W", "Ctrl+P", "Ctrl+I", "Ctrl+U", "Ctrl+Shift+C" })
        {
            Assert.Contains(shortcut, ambiguous);
        }
    }

    [Theory]
    [InlineData("Ctrl+N", FocusArea.List, CommandId.FileCreateFolder)]
    [InlineData("Ctrl+N", FocusArea.Tree, CommandId.BucketCreate)]
    [InlineData("Del", FocusArea.List, CommandId.FileDelete)]
    [InlineData("Del", FocusArea.Tree, CommandId.BucketDelete)]
    [InlineData("Ctrl+T", FocusArea.Tree, CommandId.BucketEditTags)]
    [InlineData("Ctrl+T", FocusArea.List, CommandId.FileEditTags)]
    [InlineData("Ctrl+U", FocusArea.List, CommandId.FileUploadFiles)]
    [InlineData("F2", FocusArea.List, CommandId.FileRename)]
    [InlineData("Alt+O", FocusArea.Tree, CommandId.ToolsOptions)]
    [InlineData("Alt+Left", FocusArea.Other, CommandId.NavigateBack)]
    [InlineData("Ctrl+F", FocusArea.Other, CommandId.Find)]
    [InlineData("F5", FocusArea.Other, CommandId.FileRefresh)]
    public void Resolve_by_focus(string shortcut, FocusArea focus, CommandId expected) =>
        Assert.Equal(expected, ShortcutRouter.Resolve(shortcut, focus));

    [Theory]
    [InlineData("Del")]
    [InlineData("F2")]
    public void Plain_keys_stay_with_the_focused_control_outside_tree_and_list(string shortcut) =>
        Assert.Null(ShortcutRouter.Resolve(shortcut, FocusArea.Other));

    [Fact]
    public void Unknown_shortcuts_resolve_to_nothing() => Assert.Null(ShortcutRouter.Resolve("Ctrl+Shift+Alt+Q", FocusArea.List));

    [Theory]
    [InlineData(Keys.Control | Keys.Shift | Keys.U, "Ctrl+Shift+U")]
    [InlineData(Keys.Delete, "Del")]
    [InlineData(Keys.F2, "F2")]
    [InlineData(Keys.Alt | Keys.Left, "Alt+Left")]
    [InlineData(Keys.Control | Keys.ControlKey, null)]
    [InlineData(Keys.Control | Keys.OemPeriod, null)]
    public void WinForms_keys_are_converted_to_shortcut_text(Keys keys, string? expected) =>
        Assert.Equal(expected, MainForm.ToShortcut(keys));
}
