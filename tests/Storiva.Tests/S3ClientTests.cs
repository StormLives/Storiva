using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Storiva.Core.Connections;
using Storiva.Core.Details;
using Storiva.Core.Remote;
using Storiva.FakeS3;
using Storiva.Infrastructure.S3;

namespace Storiva.Tests;

/// <summary>
/// The real AWS SDK client against <see cref="FakeS3Server"/> on 127.0.0.1. Exercises signing, path-style
/// addressing, XML parsing, pagination, streaming and error mapping without any external network.
/// </summary>
public sealed class S3ClientAgainstFakeServerTests : IAsyncLifetime
{
    private readonly FakeS3Server _server = new();
    private IObjectStorageClient _client = null!;

    public Task InitializeAsync()
    {
        _server.Start();
        _server.AddBucket("alpha");
        _server.AddBucket("beta");
        _server.PutObject("alpha", "readme.txt", Encoding.UTF8.GetBytes("hello"), "text/plain");
        _server.PutObject("alpha", "docs/", []);
        _server.PutObject("alpha", "docs/a.txt", [1, 2, 3]);
        _server.PutObject("alpha", "docs/sub/b.txt", [4]);
        _server.PutObject("alpha", "odd name+%/ü ñ.txt", Encoding.UTF8.GetBytes("unicode"));
        _client = Create(_server);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    internal static ConnectionProfile Profile(Uri endpoint) => new()
    {
        Name = "fake",
        Provider = StorageProvider.S3Compatible,
        Endpoint = endpoint.ToString().TrimEnd('/'),
        Region = "us-east-1",
        ForcePathStyle = true,
        AllowInsecureHttp = endpoint.Scheme == Uri.UriSchemeHttp,
        UseFlexibleChecksums = false,
    };

    private static IObjectStorageClient Create(FakeS3Server server, string key = FakeS3Server.TestAccessKey) =>
        new S3ClientFactory().Create(Profile(server.Endpoint), new StorageCredentials(key, FakeS3Server.TestSecret));

    [Fact]
    public async Task Test_connection_is_read_only_and_counts_buckets()
    {
        var before = _server.Requests.Count;
        var result = await _client.TestConnectionAsync(CancellationToken.None);
        Assert.Equal(ConnectionStatus.Connected, result.Status);
        Assert.Equal(2, result.VisibleBuckets);
        var sent = _server.Requests.Skip(before).ToList();
        Assert.NotEmpty(sent);
        Assert.All(sent, r => Assert.StartsWith("GET ", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Denied_bucket_listing_means_valid_credentials_with_limited_permissions()
    {
        _server.DenyListBuckets = true;
        var result = await _client.TestConnectionAsync(CancellationToken.None);
        Assert.Equal(ConnectionStatus.ConnectedLimited, result.Status);
    }

    [Fact]
    public async Task Unknown_key_and_wrong_signature_are_invalid_credentials()
    {
        using var wrong = Create(_server, "NOT-THE-KEY");
        var ex = await Assert.ThrowsAsync<StorageOperationException>(() => wrong.ListBucketsAsync(null, CancellationToken.None));
        Assert.Equal((StorageErrorKind.InvalidCredentials, "InvalidAccessKeyId"), (ex.Kind, ex.Error.Code));
        Assert.DoesNotContain(FakeS3Server.TestSecret, ex.Message, StringComparison.Ordinal);

        _server.RejectSignatures = true;
        var signature = await Assert.ThrowsAsync<StorageOperationException>(() => _client.ListBucketsAsync(null, CancellationToken.None));
        Assert.Equal(StorageErrorKind.InvalidCredentials, signature.Kind);
        Assert.Contains("secret access key", signature.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_port_is_reported_as_unreachable()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens there now
        using var client = new S3ClientFactory().Create(Profile(new Uri($"http://127.0.0.1:{port}")), new StorageCredentials(FakeS3Server.TestAccessKey, FakeS3Server.TestSecret));
        var ex = await Assert.ThrowsAsync<StorageOperationException>(() => client.ListBucketsAsync(null, CancellationToken.None));
        Assert.Equal(StorageErrorKind.EndpointUnreachable, ex.Kind);
    }

    [Fact]
    public async Task An_untrusted_certificate_is_rejected_not_accepted_silently()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        await using var tls = new FakeS3Server(certificate);
        tls.Start();
        tls.AddBucket("secure");
        using var client = Create(tls);
        var ex = await Assert.ThrowsAsync<StorageOperationException>(() => client.ListBucketsAsync(null, CancellationToken.None));
        Assert.Equal(StorageErrorKind.Tls, ex.Kind);
        Assert.Contains("does not accept invalid certificates", ex.Message, StringComparison.Ordinal);
        Assert.Empty(tls.Requests); // no request reached the server
    }

    [Fact]
    public async Task Listing_with_delimiter_and_pagination()
    {
        var first = await _client.ListObjectsAsync("alpha", string.Empty, null, 2, CancellationToken.None);
        Assert.Equal(["docs/", "odd name+%/"], first.CommonPrefixes);
        Assert.Empty(first.Objects);
        Assert.True(first.IsTruncated);
        var second = await _client.ListObjectsAsync("alpha", string.Empty, first.ContinuationToken, 2, CancellationToken.None);
        Assert.Empty(second.CommonPrefixes);
        Assert.Equal(["readme.txt"], second.Objects.Select(o => o.Key));
        Assert.False(second.IsTruncated);
        Assert.Equal(5L, second.Objects.Single().Size); // size from the listing ("hello")

        var all = new List<string>();
        await foreach (var o in _client.ListAllObjectsAsync("alpha", "docs/", CancellationToken.None))
        {
            all.Add(o.Key);
        }

        Assert.Equal(["docs/", "docs/a.txt", "docs/sub/b.txt"], all);
    }

    [Fact]
    public async Task Unusual_keys_round_trip_exactly()
    {
        var key = "odd name+%/ü ñ.txt";
        var listing = await _client.ListObjectsAsync("alpha", "odd name+%/", null, 1000, CancellationToken.None);
        Assert.Equal(key, listing.Objects.Single().Key);
        var meta = await _client.GetObjectMetadataAsync("alpha", key, CancellationToken.None);
        Assert.Equal(7, meta.ContentLength);
        await _client.CopyObjectAsync("alpha", key, "beta", "copy of " + key, null, CancellationToken.None);
        Assert.Equal("unicode", Encoding.UTF8.GetString(_server.GetObject("beta", "copy of " + key)!));
    }

    [Fact]
    public async Task Upload_and_download_stream_the_bytes_with_real_progress()
    {
        using var temp = new TempDataFolder();
        var source = Path.Combine(temp.Root, "data.bin");
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        File.WriteAllBytes(source, bytes);
        var uploadProgress = new List<TransferProgress>();
        await _client.UploadObjectAsync("beta", "in/data.bin", source, "application/octet-stream", new SyncProgress(uploadProgress.Add), CancellationToken.None);
        Assert.Equal(bytes, _server.GetObject("beta", "in/data.bin"));
        Assert.Equal(300_000, uploadProgress[^1].Total);
        Assert.Equal(100, uploadProgress[^1].Percent);

        using var target = new MemoryStream();
        var downloadProgress = new List<TransferProgress>();
        await _client.DownloadObjectAsync("beta", "in/data.bin", target, new SyncProgress(downloadProgress.Add), CancellationToken.None);
        Assert.Equal(bytes, target.ToArray());
        Assert.All(downloadProgress, p => Assert.Equal(300_000, p.Total));

        var start = await _client.ReadObjectStartAsync("beta", "in/data.bin", 4096, CancellationToken.None);
        Assert.Equal(bytes.Take(4096), start);
    }

    [Fact]
    public async Task Folders_deletes_and_buckets()
    {
        await _client.CreateFolderAsync("beta", "new-folder/", CancellationToken.None);
        Assert.Contains("new-folder/", _server.Keys("beta"));

        _server.UndeletableKeys.Add("docs/a.txt");
        var result = await _client.DeleteObjectsAsync("alpha", ["docs/a.txt", "docs/sub/b.txt"], CancellationToken.None);
        Assert.Equal(["docs/sub/b.txt"], result.Deleted);
        Assert.Equal("docs/a.txt", result.Failed.Single().Key);

        var notEmpty = await Assert.ThrowsAsync<StorageOperationException>(() => _client.DeleteBucketAsync("alpha", CancellationToken.None));
        Assert.Equal((StorageErrorKind.Conflict, "BucketNotEmpty"), (notEmpty.Kind, notEmpty.Error.Code));
        Assert.True(_server.HasBucket("alpha"));

        await _client.CreateBucketAsync("gamma", null, CancellationToken.None);
        Assert.True(_server.HasBucket("gamma"));
        Assert.Equal("us-east-1", await _client.GetBucketRegionAsync("gamma", CancellationToken.None));
        await _client.DeleteBucketAsync("gamma", CancellationToken.None);
        Assert.False(_server.HasBucket("gamma"));

        var missing = await Assert.ThrowsAsync<StorageOperationException>(() => _client.GetObjectMetadataAsync("alpha", "nope", CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, missing.Kind);
    }

    [Fact]
    public async Task Acl_tags_and_versioning()
    {
        var acl = await _client.GetAclAsync("alpha", "readme.txt", CancellationToken.None);
        Assert.Equal("fake-owner-id", acl.OwnerId);
        Assert.Equal("FULL_CONTROL", acl.Grants.Single().Permission);

        Assert.Empty(await _client.GetTagsAsync("alpha", null, CancellationToken.None)); // NoSuchTagSet → none
        await _client.SetTagsAsync("alpha", "readme.txt", [new Tag("env", "test"), new Tag("team", "storage")], CancellationToken.None);
        Assert.Equal([("env", "test"), ("team", "storage")], _server.TagsOf("alpha", "readme.txt"));
        Assert.Equal(2, (await _client.GetTagsAsync("alpha", "readme.txt", CancellationToken.None)).Count);

        Assert.Equal(VersioningStatus.Disabled, await _client.GetVersioningAsync("alpha", CancellationToken.None));
        await _client.SetVersioningAsync("alpha", VersioningStatus.Enabled, CancellationToken.None);
        Assert.Equal(VersioningStatus.Enabled, await _client.GetVersioningAsync("alpha", CancellationToken.None));
        var versions = await _client.ListVersionsAsync("alpha", "readme.txt", 10, CancellationToken.None);
        Assert.True(versions.Single().IsLatest);
        await Assert.ThrowsAsync<ArgumentException>(() => _client.SetVersioningAsync("alpha", VersioningStatus.Disabled, CancellationToken.None));
    }

    [Fact]
    public async Task Storage_class_change_copies_the_object_onto_itself()
    {
        await _client.CopyObjectAsync("alpha", "readme.txt", "alpha", "readme.txt", "STANDARD_IA", CancellationToken.None);
        Assert.Equal("STANDARD_IA", _server.StorageClassOf("alpha", "readme.txt"));
        Assert.Equal("hello", Encoding.UTF8.GetString(_server.GetObject("alpha", "readme.txt")!));
    }

    [Fact]
    public async Task Write_permission_errors_are_access_denied()
    {
        _server.ReadOnlyBuckets.Add("beta");
        var ex = await Assert.ThrowsAsync<StorageOperationException>(() => _client.CreateFolderAsync("beta", "x/", CancellationToken.None));
        Assert.Equal(StorageErrorKind.AccessDenied, ex.Kind);
        Assert.Contains("not allowed", ex.Message, StringComparison.Ordinal);
    }

    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}

public class S3ConfigurationTests
{
    [Fact]
    public void Compatible_endpoints_use_service_url_path_style_and_signing_region()
    {
        var config = S3ClientFactory.BuildConfig(new ConnectionProfile
        {
            Name = "m",
            Provider = StorageProvider.S3Compatible,
            Endpoint = "http://127.0.0.1:9000",
            Region = "eu-local-1",
            ForcePathStyle = true,
            AllowInsecureHttp = true,
            UseFlexibleChecksums = false,
        });
        Assert.StartsWith("http://127.0.0.1:9000", config.ServiceURL, StringComparison.Ordinal);
        Assert.True(config.ForcePathStyle);
        Assert.True(config.UseHttp);
        Assert.Equal("eu-local-1", config.AuthenticationRegion);
        Assert.Equal(RequestChecksumCalculation.WHEN_REQUIRED, config.RequestChecksumCalculation);
    }

    [Fact]
    public void Aws_uses_the_region_endpoint_and_https()
    {
        var config = S3ClientFactory.BuildConfig(new ConnectionProfile { Name = "a", Region = "eu-west-3" }, regionOverride: "ap-south-1");
        Assert.Equal("ap-south-1", config.RegionEndpoint?.SystemName);
        Assert.False(config.UseHttp);
        Assert.False(config.ForcePathStyle);
    }

    [Fact]
    public void Invalid_profiles_and_missing_credentials_fail_before_any_request()
    {
        var factory = new S3ClientFactory();
        var badEndpoint = Assert.Throws<StorageOperationException>(() => factory.Create(new ConnectionProfile { Name = "x", Provider = StorageProvider.S3Compatible, Endpoint = "minio:9000" }, new StorageCredentials("k", "s")));
        Assert.Equal(StorageErrorKind.RegionOrConfiguration, badEndpoint.Kind);
        var noKeys = Assert.Throws<StorageOperationException>(() => factory.Create(new ConnectionProfile { Name = "x" }, null));
        Assert.Equal(StorageErrorKind.CredentialsUnavailable, noKeys.Kind);
        var missingProfile = Assert.Throws<StorageOperationException>(() => factory.Create(
            new ConnectionProfile { Name = "x", Authentication = AuthenticationMethod.AwsProfile, ProfileName = "storiva-test-profile-that-does-not-exist-" + Guid.NewGuid().ToString("N") }, null));
        Assert.Equal(StorageErrorKind.CredentialsUnavailable, missingProfile.Kind);
    }
}

public class S3ErrorMapperTests
{
    private static AmazonS3Exception S3(string code, HttpStatusCode status, string message = "server message") =>
        new(message, ErrorType.Sender, code, "req-1", status);

    [Theory]
    [InlineData("AccessDenied", HttpStatusCode.Forbidden, StorageErrorKind.AccessDenied)]
    [InlineData("InvalidAccessKeyId", HttpStatusCode.Forbidden, StorageErrorKind.InvalidCredentials)]
    [InlineData("SignatureDoesNotMatch", HttpStatusCode.Forbidden, StorageErrorKind.InvalidCredentials)]
    [InlineData("ExpiredToken", HttpStatusCode.BadRequest, StorageErrorKind.InvalidCredentials)]
    [InlineData("PermanentRedirect", HttpStatusCode.MovedPermanently, StorageErrorKind.RegionOrConfiguration)]
    [InlineData("AuthorizationHeaderMalformed", HttpStatusCode.BadRequest, StorageErrorKind.RegionOrConfiguration)]
    [InlineData("NoSuchBucket", HttpStatusCode.NotFound, StorageErrorKind.NotFound)]
    [InlineData("BucketAlreadyExists", HttpStatusCode.Conflict, StorageErrorKind.Conflict)]
    [InlineData("BucketNotEmpty", HttpStatusCode.Conflict, StorageErrorKind.Conflict)]
    [InlineData("NotImplemented", HttpStatusCode.NotImplemented, StorageErrorKind.NotSupported)]
    [InlineData("SlowDown", HttpStatusCode.ServiceUnavailable, StorageErrorKind.Throttled)]
    [InlineData("InternalError", HttpStatusCode.InternalServerError, StorageErrorKind.ServerError)]
    [InlineData(null, HttpStatusCode.Forbidden, StorageErrorKind.AccessDenied)]
    public void Service_errors_are_classified(string? code, HttpStatusCode status, StorageErrorKind expected) =>
        Assert.Equal(expected, S3ErrorMapper.ToError(S3(code!, status), "Listing").Kind);

    [Fact]
    public void Transport_failures_are_classified()
    {
        Assert.Equal(StorageErrorKind.Tls, S3ErrorMapper.ToError(new HttpRequestExceptionWrapper(new System.Security.Authentication.AuthenticationException("bad cert")), "x").Kind);
        Assert.Equal(StorageErrorKind.EndpointUnreachable, S3ErrorMapper.ToError(new HttpRequestExceptionWrapper(new System.Net.Sockets.SocketException(10061)), "x").Kind);
        Assert.Equal(StorageErrorKind.Canceled, S3ErrorMapper.ToError(new OperationCanceledException(), "x").Kind);
        Assert.Equal(StorageErrorKind.CredentialsUnavailable, S3ErrorMapper.ToError(new AmazonClientException("Failed to resolve AWS credentials."), "x").Kind);
    }

    [Fact]
    public void Messages_never_carry_secrets()
    {
        var credentials = new StorageCredentials("AKIAEXAMPLE000000001", "abcdefghijklmnopqrstuvwxyz0123456789ABCD");
        var error = S3ErrorMapper.ToError(S3("InvalidArgument", HttpStatusCode.BadRequest, "bad request for AKIAEXAMPLE000000001 with abcdefghijklmnopqrstuvwxyz0123456789ABCD"), "Uploading", credentials, isAws: false);
        Assert.DoesNotContain("AKIAEXAMPLE000000001", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789ABCD", error.Message, StringComparison.Ordinal);
        Assert.Contains("flexible checksums", error.Message, StringComparison.Ordinal);
    }

    private sealed class HttpRequestExceptionWrapper(Exception inner) : Exception("wrapper", new System.Net.Http.HttpRequestException("send failed", inner));
}
