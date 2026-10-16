using System.Text.RegularExpressions;
using Storiva.Core.Activity;
using Storiva.Core.Details;
using Storiva.Core.Navigation;
using Storiva.Core.Settings;
using Storiva.Core.Storage;

namespace Storiva.Tests;

public class AclRulesTests
{
    [Fact]
    public void Normalize_gives_one_row_per_grantee_in_order() =>
        Assert.Equal([Grantee.Owner, Grantee.AuthenticatedUsers, Grantee.AllUsers], AclRules.Normalize([new Grant(Grantee.AllUsers, Permission.Read)]).Select(g => g.Grantee));

    [Fact]
    public void Public_means_all_users_have_some_permission()
    {
        Assert.False(AclRules.IsPublic(AclRules.Normalize([new Grant(Grantee.Owner, Permission.All)])));
        Assert.True(AclRules.IsPublic(AclRules.Normalize([new Grant(Grantee.Owner, Permission.All), new Grant(Grantee.AllUsers, Permission.Read)])));
    }
}

public class TagRulesTests
{
    [Fact]
    public void Tag_rules()
    {
        Assert.Null(TagRules.Validate("owner", "", [], TagRules.MaxObjectTags));
        Assert.NotNull(TagRules.Validate("", "x", [], TagRules.MaxObjectTags));
        Assert.NotNull(TagRules.Validate(new string('k', 129), "x", [], TagRules.MaxObjectTags));
        Assert.NotNull(TagRules.Validate("k", new string('v', 257), [], TagRules.MaxObjectTags));
        Assert.NotNull(TagRules.Validate("owner", "x", ["owner"], TagRules.MaxObjectTags));
        Assert.NotNull(TagRules.Validate("k11", "x", Enumerable.Range(1, 10).Select(i => $"k{i}"), TagRules.MaxObjectTags));
    }

    [Fact]
    public void Edit_session_tracks_pending_changes_until_apply_or_reload()
    {
        var stored = new List<string> { "a" };
        var session = new EditSession<string>(() => stored, list => stored = list.ToList());
        Assert.False(session.IsDirty);
        session.Update(["a", "b"]);
        Assert.True(session.IsDirty);
        session.Reload();
        Assert.Equal(["a"], session.Current);
        session.Update(["a", "c"]);
        session.Apply();
        Assert.False(session.IsDirty);
        Assert.Equal(["a", "c"], stored);
    }
}

public class BucketNameRulesTests
{
    [Theory]
    [InlineData("my-bucket", true)]
    [InlineData("logs.2026", true)]
    [InlineData("ab", false)]
    [InlineData("Bad_Name", false)]
    [InlineData("-start", false)]
    [InlineData("end-", false)]
    [InlineData("two..dots", false)]
    [InlineData("dot.-dash", false)]
    [InlineData("192.168.10.1", false)]
    public void Validation(string name, bool valid) => Assert.Equal(valid, BucketNameRules.Validate(name, []) is null);

    [Fact]
    public void Too_long_and_duplicate_names_are_rejected()
    {
        Assert.NotNull(BucketNameRules.Validate(new string('a', 64), []));
        Assert.NotNull(BucketNameRules.Validate("team-assets", ["team-assets"]));
    }

    [Fact]
    public void Suggested_names_follow_the_reference_pattern_and_are_deterministic()
    {
        var a = BucketNameRules.SuggestName(1, []);
        Assert.Matches("^new-bucket-[0-9a-f]{8}$", a);
        Assert.Equal(a, BucketNameRules.SuggestName(1, []));
        Assert.NotEqual(a, BucketNameRules.SuggestName(1, [a]));
        Assert.Null(BucketNameRules.Validate(a, []));
    }
}

public class EmptyDetailsStoreTests
{
    [Fact]
    public void Without_a_connection_no_details_are_invented()
    {
        var store = EmptyDetailsStore.Instance;
        var path = StoragePath.Parse("bucket/file.txt");
        Assert.Empty(store.GetHeaders(path));
        Assert.Empty(store.GetTags(path));
        Assert.Empty(store.GetVersions(path));
        Assert.Equal(VersioningStatus.Disabled, store.GetVersioning(path));
        Assert.Empty(store.GetContentSample(new StorageItem(path, StorageItemKind.File, 10, null, null), 4096));
    }
}

public class PreviewFormatterTests
{
    private static StorageItem FileNamed(string name, long size = 100) => new(StoragePath.Parse("b/" + name), StorageItemKind.File, size, null, null);

    [Theory]
    [InlineData("a.json", PreviewKind.Json)]
    [InlineData("a.txt", PreviewKind.Text)]
    [InlineData("a.csv", PreviewKind.Text)]
    [InlineData("a.js", PreviewKind.Text)]
    [InlineData("a.zip", PreviewKind.Hex)]
    [InlineData("a.pdf", PreviewKind.Hex)]
    [InlineData("a.png", PreviewKind.Unsupported)]
    [InlineData("a.mp4", PreviewKind.Unsupported)]
    public void Kind_by_extension(string name, PreviewKind kind) => Assert.Equal(kind, PreviewFormatter.Decide(FileNamed(name)));

    [Fact]
    public void Containers_are_not_previewed() =>
        Assert.Equal(PreviewKind.NotAFile, PreviewFormatter.Decide(new StorageItem(StoragePath.Parse("b/f"), StorageItemKind.Folder, null, null, null)));

    [Fact]
    public void Hex_dump_matches_the_reference_layout()
    {
        var lines = PreviewFormatter.HexDump("PK\u0003\u0004 Hello, Storiva!!"u8.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("00000000  50 4B 03 04 20 48 65 6C  6C 6F 2C 20 53 74 6F 72  PK.. Hello, Stor", lines[0]);
        Assert.StartsWith("00000010  69 76 61 21 21", lines[1]);
        Assert.EndsWith("iva!!", lines[1]);
    }

    [Fact]
    public void Json_is_indented_and_invalid_json_is_left_as_is()
    {
        Assert.Contains("\r\n  \"a\": 1", PreviewFormatter.PrettyJson("{\"a\":1}"));
        Assert.Equal("{not json", PreviewFormatter.PrettyJson("{not json"));
    }

    [Fact]
    public void Json_preview_shows_accents_and_html_characters_as_they_are()
    {
        var pretty = PreviewFormatter.PrettyJson("{\"nome\":\"São Paulo <b> & café\"}");
        Assert.Contains("\"São Paulo <b> & café\"", pretty);
        Assert.DoesNotContain("\\u", pretty);
    }

    [Fact]
    public void Build_notes_that_content_comes_from_the_server_and_is_truncated()
    {
        var result = PreviewFormatter.Build(FileNamed("big.zip", 10_000), _ => new byte[16]);
        Assert.Equal(PreviewKind.Hex, result.Kind);
        Assert.Contains("server", result.Note);
        Assert.Contains("first 16", result.Note);
    }
}

public class SessionStateTests
{
    [Fact]
    public void Bookmarks_toggle_prune_and_reject_home()
    {
        var b = new BookmarkList();
        var raised = 0;
        b.Changed += (_, _) => raised++;
        Assert.True(b.Toggle(StoragePath.Parse("assets/images")));
        Assert.False(b.Toggle(StoragePath.Parse("assets/images")));
        b.Toggle(StoragePath.Parse("public"));
        Assert.Equal(1, b.Prune(p => p.Bucket != "public"));
        Assert.Empty(b.Items);
        Assert.Throws<ArgumentException>(() => b.Toggle(StoragePath.Root));
        Assert.Equal(4, raised);
    }

    [Fact]
    public void Options_validation()
    {
        Assert.Null(SessionOptions.Default.Validate());
        Assert.NotNull((SessionOptions.Default with { MaxConcurrentTasks = 0 }).Validate());
        Assert.NotNull((SessionOptions.Default with { MaxConcurrentTasks = 9 }).Validate());
        Assert.Equal(8, SessionOptions.PageNames.Length);
    }

    [Fact]
    public void Event_log_format_matches_the_reference_and_filters_by_level()
    {
        var log = new ActivityLog(clock: () => new DateTimeOffset(2026, 10, 15, 11, 27, 25, TimeSpan.Zero));
        var entry = log.Write("Task done", taskId: 42)!;
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] Task done TaskID: 42$", entry.Format());
        Assert.Equal("I", entry.LevelLetter);
        log.MinimumLevel = ActivityLevel.Warning;
        Assert.Null(log.Write("ignored"));
        Assert.Equal("W", log.Write("careful", ActivityLevel.Warning)!.LevelLetter);
        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public async Task Task_queue_concurrency_can_change_at_run_time()
    {
        var queue = new Storiva.Core.Tasks.TaskQueue(maxConcurrent: 1);
        var runner = new Storiva.Core.Tasks.OperationRunner(queue, progressInterval: TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.MaxConcurrent = 0);
        var gate = new TaskCompletionSource<Storiva.Core.Tasks.OperationOutcome>();
        var tasks = Enumerable.Range(0, 3).Select(i => runner.Start($"t{i}", Storiva.Core.Tasks.TransferKind.Copy, null, _ => gate.Task)).ToList();
        Assert.True(await Until(() => queue.Count(Storiva.Core.Tasks.TaskFilter.Running) == 1));
        Assert.Equal(2, queue.Count(Storiva.Core.Tasks.TaskFilter.Queued));

        queue.MaxConcurrent = 4; // raising the limit starts the waiting operations
        Assert.True(await Until(() => queue.Count(Storiva.Core.Tasks.TaskFilter.Running) == 3));
        gate.SetResult(Storiva.Core.Tasks.OperationOutcome.Done("ok"));
        Assert.True(await Until(() => tasks.All(t => t.State == Storiva.Core.Tasks.TransferState.Completed)));
    }

    private static async Task<bool> Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }
}
