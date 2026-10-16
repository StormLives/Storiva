using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;
using Storiva.Core.Commands;
using Storiva.Core.Connections;
using Storiva.Core.Remote;
using Storiva.Core.Storage;
using Storiva.FakeS3;
using Storiva.Infrastructure.S3;
using Storiva.Tests.Fakes;

namespace Storiva.Tests;

/// <summary>"Generate Web URL" and the copy-address commands: rules, link shape and a real download of a link.</summary>
public class ShareLinkRulesTests
{
    private static readonly StoragePath Images = StoragePath.Parse("team-assets/images");
    private static readonly StorageItem File = new(Images.Append("banner.png"), StorageItemKind.File, 10, null, "STANDARD");
    private static readonly StorageItem Other = new(Images.Append("logo.svg"), StorageItemKind.File, 10, null, "STANDARD");
    private static readonly StorageItem Folder = new(Images.Append("icons"), StorageItemKind.Folder, null, null, null);
    private static readonly StorageItem Bucket = new(StoragePath.Parse("team-assets"), StorageItemKind.Bucket, null, null, null);

    private static bool On(CommandId id, StoragePath location, IReadOnlyList<StorageItem> selected, bool connected = true) =>
        CommandRules.IsEnabled(id, new SelectionContext(location, selected, null, FocusArea.List, 0, Connected: connected));

    [Fact]
    public void Links_are_for_one_or_more_files_only()
    {
        Assert.True(On(CommandId.FileGenerateUrl, Images, [File]));
        Assert.True(On(CommandId.FileGenerateUrl, Images, [File, Other]));
        Assert.False(On(CommandId.FileGenerateUrl, Images, []));
        Assert.False(On(CommandId.FileGenerateUrl, Images, [Folder]));
        Assert.False(On(CommandId.FileGenerateUrl, Images, [File, Folder]));
        Assert.False(On(CommandId.FileGenerateUrl, StoragePath.Root, [Bucket]));
        Assert.False(On(CommandId.FileGenerateUrl, Images, [File], connected: false));
        Assert.Equal(On(CommandId.FileGenerateUrl, Images, [File, Folder]), On(CommandId.CopyObjectUrl, Images, [File, Folder]));
        Assert.True(On(CommandId.CopyObjectUrl, Images, [File, Other]));
    }

    [Fact]
    public void S3_uris_exist_for_files_folders_and_buckets()
    {
        Assert.True(On(CommandId.CopyS3Uri, Images, [File, Folder]));
        Assert.True(On(CommandId.CopyS3Uri, StoragePath.Root, [Bucket]));
        Assert.False(On(CommandId.CopyS3Uri, Images, []));
        Assert.False(On(CommandId.CopyS3Uri, Images, [File], connected: false));
        Assert.Equal("s3://team-assets/images/banner.png", ShareLinks.S3Uri("team-assets", "images/banner.png"));
        Assert.Equal("s3://team-assets/", ShareLinks.S3Uri("team-assets", null));
    }

    [Fact]
    public void Generate_web_url_commands_run_instead_of_the_not_available_notice()
    {
        foreach (var id in new[] { CommandId.FileGenerateUrl, CommandId.BucketGenerateUrls, CommandId.CopyS3Uri, CommandId.CopyObjectUrl })
        {
            Assert.Equal(CommandBehavior.Local, CommandCatalog.Get(id).Behavior);
        }

        // The reference shortcuts are kept: Ctrl+W in the list, Ctrl+U in the bucket pane.
        Assert.Equal(CommandId.FileGenerateUrl, ShortcutRouter.Resolve("Ctrl+W", FocusArea.List));
        Assert.Equal(CommandId.BucketGenerateUrls, ShortcutRouter.Resolve("Ctrl+U", FocusArea.Tree));
    }

    [Fact]
    public void Validity_is_between_one_second_and_seven_days()
    {
        Assert.Equal(["15 min", "1 h", "24 h", "7 days"], ShareLinks.ValidityChoices.Select(ShareLinks.Describe));
        Assert.Equal(TimeSpan.FromDays(7), ShareLinks.ValidityChoices.Max());
        Assert.Contains(ShareLinks.DefaultValidity, ShareLinks.ValidityChoices);
        ShareLinks.ValidateValidity(TimeSpan.FromDays(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShareLinks.ValidateValidity(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShareLinks.ValidateValidity(TimeSpan.Zero));
    }

    [Fact]
    public async Task Links_are_created_in_order_without_listing_or_reading_anything()
    {
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "a.txt", [1]);
        client.Put("b", "docs/b.txt", [2]);
        var links = await ShareLinks.CreateLinksAsync(client, "b", ["docs/b.txt", "a.txt"], TimeSpan.FromHours(1), CancellationToken.None);
        Assert.Equal(["docs/b.txt", "a.txt"], links.Select(l => l.Key));
        Assert.Equal(["Presign docs/b.txt", "Presign a.txt"], client.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => ShareLinks.CreateLinksAsync(client, "b", Enumerable.Range(0, ShareLinks.MaxLinks + 1).Select(i => $"k{i}").ToList(), TimeSpan.FromHours(1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ShareLinks.CreateLinksAsync(client, "b", ["a.txt"], TimeSpan.FromDays(8), CancellationToken.None));
    }

    [Fact]
    public async Task Bucket_file_list_skips_folder_markers_and_stops_after_the_limit()
    {
        var client = new InMemoryObjectStorageClient();
        client.Put("b", "docs/", []);
        for (var i = 0; i < 10; i++)
        {
            client.Put("b", $"docs/{i}.txt", [1]);
        }

        Assert.Equal(10, (await ShareLinks.ListFileKeysAsync(client, "b", 100, CancellationToken.None)).Count);
        var capped = await ShareLinks.ListFileKeysAsync(client, "b", 3, CancellationToken.None);
        Assert.Equal(4, capped.Count); // max + 1: "more than 3"
        Assert.DoesNotContain("docs/", capped);
    }
}

/// <summary>Pre-signed URLs from the real SDK client: computed locally, in the shape the connection needs.</summary>
public sealed class PresignedUrlTests : IAsyncLifetime
{
    private const string AwsKey = "AKIAEXAMPLE000000001";
    private const string AwsSecret = "abcdefghijklmnopqrstuvwxyz0123456789ABCD";
    private readonly FakeS3Server _server = new();
    private IObjectStorageClient _client = null!;

    public Task InitializeAsync()
    {
        _server.Start();
        _server.PutObject("alpha", "docs/odd name+%/ü ñ.txt", Encoding.UTF8.GetBytes("shared content"), "text/plain");
        _client = new S3ClientFactory().Create(S3ClientAgainstFakeServerTests.Profile(_server.Endpoint), new StorageCredentials(FakeS3Server.TestAccessKey, FakeS3Server.TestSecret));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    private static Dictionary<string, string> Query(string url)
    {
        var parsed = HttpUtility.ParseQueryString(new Uri(url).Query);
        return parsed.AllKeys.OfType<string>().ToDictionary(k => k, k => parsed[k]!, StringComparer.Ordinal);
    }

    private static IObjectStorageClient Aws(string region = "eu-west-3", bool pathStyle = false, string? sessionToken = null) =>
        new S3ClientFactory().Create(new ConnectionProfile { Name = "aws", Region = region, ForcePathStyle = pathStyle }, new StorageCredentials(AwsKey, AwsSecret, sessionToken));

    [Fact]
    public async Task Compatible_endpoint_link_is_path_style_signed_and_sends_nothing()
    {
        var before = _server.Requests.Count;
        var link = await _client.CreatePresignedGetUrlAsync("alpha", "docs/odd name+%/ü ñ.txt", TimeSpan.FromHours(1), CancellationToken.None);
        Assert.Equal(before, _server.Requests.Count); // computed locally

        var uri = new Uri(link.Url);
        Assert.Equal((_server.Endpoint.Scheme, _server.Endpoint.Host, _server.Endpoint.Port), (uri.Scheme, uri.Host, uri.Port));
        Assert.StartsWith("/alpha/docs/odd%20name%2B%25/%C3%BC%20%C3%B1.txt", uri.AbsolutePath, StringComparison.Ordinal);
        var q = Query(link.Url);
        Assert.Equal("3600", q["X-Amz-Expires"]);
        Assert.Equal("AWS4-HMAC-SHA256", q["X-Amz-Algorithm"]);
        Assert.StartsWith(FakeS3Server.TestAccessKey + "/", q["X-Amz-Credential"], StringComparison.Ordinal);
        Assert.Contains("/us-east-1/s3/aws4_request", q["X-Amz-Credential"], StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", q["X-Amz-Signature"]);
        Assert.DoesNotContain(FakeS3Server.TestSecret, link.Url, StringComparison.Ordinal);
        Assert.False(link.UsesTemporaryCredentials);
        Assert.InRange(link.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
        Assert.DoesNotContain("X-Amz-Signature", link.ToString(), StringComparison.Ordinal); // a log line never prints the link
    }

    [Fact]
    public async Task The_link_downloads_the_object_without_credentials_until_it_expires()
    {
        var link = await _client.CreatePresignedGetUrlAsync("alpha", "docs/odd name+%/ü ñ.txt", TimeSpan.FromSeconds(1), CancellationToken.None);
        // Plain client, no proxy and no Authorization header: only the loopback fake server is contacted.
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using (var response = await http.GetAsync(link.Url))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("shared content", await response.Content.ReadAsStringAsync());
        }

        await Task.Delay(TimeSpan.FromSeconds(2.2));
        using var expired = await http.GetAsync(link.Url);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
    }

    [Fact]
    public void Plain_object_url_has_no_credentials()
    {
        var url = _client.GetObjectUrl("alpha", "docs/odd name+%/ü ñ.txt");
        Assert.Equal(_server.Endpoint.ToString().TrimEnd('/') + "/alpha/docs/odd%20name%2B%25/%C3%BC%20%C3%B1.txt", url);
        Assert.DoesNotContain("X-Amz", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aws_links_follow_the_region_and_the_addressing_style()
    {
        using var virtualHosted = Aws();
        var link = await virtualHosted.CreatePresignedGetUrlAsync("my-bucket", "a/b.txt", TimeSpan.FromDays(7), CancellationToken.None);
        Assert.StartsWith("https://my-bucket.s3.eu-west-3.amazonaws.com/a/b.txt?", link.Url, StringComparison.Ordinal);
        Assert.Equal("604800", Query(link.Url)["X-Amz-Expires"]);
        Assert.Contains("/eu-west-3/s3/aws4_request", Query(link.Url)["X-Amz-Credential"], StringComparison.Ordinal);
        Assert.DoesNotContain(AwsSecret, link.Url, StringComparison.Ordinal);
        Assert.Equal("https://my-bucket.s3.eu-west-3.amazonaws.com/a/b.txt", virtualHosted.GetObjectUrl("my-bucket", "a/b.txt"));

        // Dotted names cannot use virtual-hosted HTTPS (certificate): the SDK falls back to path-style.
        Assert.StartsWith("https://s3.eu-west-3.amazonaws.com/my.dotted.bucket/x?", (await virtualHosted.CreatePresignedGetUrlAsync("my.dotted.bucket", "x", TimeSpan.FromHours(1), CancellationToken.None)).Url, StringComparison.Ordinal);

        using var pathStyle = Aws(pathStyle: true);
        Assert.StartsWith("https://s3.eu-west-3.amazonaws.com/my-bucket/a/b.txt?", (await pathStyle.CreatePresignedGetUrlAsync("my-bucket", "a/b.txt", TimeSpan.FromHours(1), CancellationToken.None)).Url, StringComparison.Ordinal);
        Assert.Equal("https://s3.eu-west-3.amazonaws.com/my-bucket/a/b.txt", pathStyle.GetObjectUrl("my-bucket", "a/b.txt"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => virtualHosted.CreatePresignedGetUrlAsync("my-bucket", "a", TimeSpan.FromDays(8), CancellationToken.None));
    }

    [Theory]
    [InlineData(-3)] // SDK clock behind this computer's (corrected from the servers' Date headers)
    [InlineData(2)]
    public async Task Seven_day_links_stay_valid_when_the_sdk_clock_is_corrected(int correctionSeconds)
    {
        var previous = Amazon.AWSConfigs.ManualClockCorrection;
        try
        {
            Amazon.AWSConfigs.ManualClockCorrection = TimeSpan.FromSeconds(correctionSeconds);
            using var client = Aws();
            var week = await client.CreatePresignedGetUrlAsync("my-bucket", "a.txt", TimeSpan.FromDays(7), CancellationToken.None);
            Assert.InRange(long.Parse(Query(week.Url)["X-Amz-Expires"], System.Globalization.CultureInfo.InvariantCulture), 604799, 604800);
            var hour = await client.CreatePresignedGetUrlAsync("my-bucket", "a.txt", TimeSpan.FromHours(1), CancellationToken.None);
            Assert.InRange(long.Parse(Query(hour.Url)["X-Amz-Expires"], System.Globalization.CultureInfo.InvariantCulture), 3599, 3600);
        }
        finally
        {
            Amazon.AWSConfigs.ManualClockCorrection = previous;
        }
    }

    [Fact]
    public async Task Temporary_credentials_are_flagged()
    {
        using var session = Aws(sessionToken: "example-session-token");
        var link = await session.CreatePresignedGetUrlAsync("my-bucket", "a.txt", TimeSpan.FromMinutes(15), CancellationToken.None);
        Assert.True(link.UsesTemporaryCredentials);
        Assert.Equal("900", Query(link.Url)["X-Amz-Expires"]);
    }

    [Fact]
    public void Virtual_hosted_compatible_endpoint_puts_the_bucket_in_the_host()
    {
        using var client = new S3ClientFactory().Create(new ConnectionProfile
        {
            Name = "minio",
            Provider = StorageProvider.S3Compatible,
            Endpoint = "https://storage.example.com:9000",
            Region = "eu-local-1",
            ForcePathStyle = false,
        }, new StorageCredentials("k", "s"));
        Assert.Equal("https://media.storage.example.com:9000/a%20b.txt", client.GetObjectUrl("media", "a b.txt"));
    }
}
