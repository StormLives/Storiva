using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;
using System.Xml.Linq;

namespace Storiva.FakeS3;

/// <summary>
/// Minimal S3-compatible HTTP server on 127.0.0.1 for tests: path-style requests, the subset of the S3 API Storiva
/// uses, aws-chunked uploads, configurable failures. It checks the access key ID in the SigV4 Authorization
/// header but does not verify signatures (that is the SDK's job, tested by AWS). Never listens on other interfaces.
/// </summary>
public sealed class FakeS3Server : IAsyncDisposable
{
    public const string TestAccessKey = "STORIVATESTKEY";
    public const string TestSecret = "storiva-fake-secret-not-a-real-credential";
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2? _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private Task? _acceptLoop;

    public FakeS3Server(X509Certificate2? tlsCertificate = null) => _certificate = tlsCertificate;

    public Uri Endpoint { get; private set; } = null!;

    /// <summary>Requests received ("GET /bucket?list-type=2"), for assertions such as "the test sent no write".</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>ListBuckets answers 403 AccessDenied (credentials valid, permission missing).</summary>
    public bool DenyListBuckets { get; set; }

    /// <summary>Every request answers 403 SignatureDoesNotMatch (wrong secret).</summary>
    public bool RejectSignatures { get; set; }

    /// <summary>Buckets where any write answers 403 AccessDenied.</summary>
    public HashSet<string> ReadOnlyBuckets { get; } = new(StringComparer.Ordinal);

    /// <summary>Keys whose DeleteObject/DeleteObjects answers AccessDenied (partial-failure tests).</summary>
    public HashSet<string> UndeletableKeys { get; } = new(StringComparer.Ordinal);

    public string AccessKey { get; set; } = TestAccessKey;

    /// <summary>Keys whose GET waits this long before answering (cancellation tests).</summary>
    public ConcurrentDictionary<string, TimeSpan> SlowKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Buckets that exist but are not returned by ListBuckets (e.g. owned by another account).</summary>
    public HashSet<string> HiddenBuckets { get; } = new(StringComparer.Ordinal);

    /// <summary>Key → number of the next GET/PUT requests on it that answer 500 InternalError (retry tests).</summary>
    public ConcurrentDictionary<string, int> FailingKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Key → number of the next GETs on it whose connection drops after half of the body (announced with the full
    /// Content-Length): a network failure in the middle of a download.
    /// </summary>
    public ConcurrentDictionary<string, int> TruncatedKeys { get; } = new(StringComparer.Ordinal);

    public void Start()
    {
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Endpoint = new Uri($"{(_certificate is null ? "http" : "https")}://127.0.0.1:{port}");
        _acceptLoop = AcceptLoopAsync();
    }

    public void AddBucket(string name, DateTimeOffset? created = null) => _buckets.TryAdd(name, new Bucket(created ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    public void PutObject(string bucket, string key, byte[] content, string contentType = "application/octet-stream")
    {
        AddBucket(bucket);
        _buckets[bucket].Objects[key] = new StoredObject(content, contentType, DateTimeOffset.UtcNow, "STANDARD", new Dictionary<string, string>(), []);
    }

    public byte[]? GetObject(string bucket, string key) =>
        _buckets.TryGetValue(bucket, out var b) && b.Objects.TryGetValue(key, out var o) ? o.Content : null;

    public IReadOnlyList<string> Keys(string bucket) => _buckets.TryGetValue(bucket, out var b) ? b.Objects.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() : [];

    public bool HasBucket(string bucket) => _buckets.ContainsKey(bucket);

    public string? StorageClassOf(string bucket, string key) => _buckets.TryGetValue(bucket, out var b) && b.Objects.TryGetValue(key, out var o) ? o.StorageClass : null;

    public IReadOnlyList<(string Key, string Value)> TagsOf(string bucket, string key) =>
        _buckets.TryGetValue(bucket, out var b) && b.Objects.TryGetValue(key, out var o) ? o.Tags : [];

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using var _ = client;
        Stream stream = client.GetStream();
        try
        {
            if (_certificate is not null)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(_certificate, clientCertificateRequired: false, checkCertificateRevocation: false).ConfigureAwait(false);
                stream = ssl;
            }

            var reader = new HttpReader(stream);
            while (!_stop.IsCancellationRequested)
            {
                var request = await reader.ReadRequestAsync(_stop.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var response = Handle(request);
                if (request.Method == "GET" && SplitPath(request.Path) is (_, { } slowKey) && SlowKeys.TryGetValue(slowKey, out var delay))
                {
                    await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                }

                if (request.Method == "GET" && response.Status == 200 && SplitPath(request.Path) is (_, { } cutKey) && Take(TruncatedKeys, cutKey))
                {
                    await WriteResponseAsync(stream, response, head: false, bodyLimit: response.Body.Length / 2).ConfigureAwait(false);
                    return; // closes the connection in the middle of the body
                }

                await WriteResponseAsync(stream, response, request.Method == "HEAD").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            // Client went away or rejected our certificate (TLS test).
        }
    }

    private Response Handle(Request r)
    {
        Requests.Enqueue($"{r.Method} {r.RawTarget}");
        var auth = r.Header("authorization");
        if (auth is null || !auth.Contains($"Credential={AccessKey}/", StringComparison.Ordinal))
        {
            return Error(403, "InvalidAccessKeyId", "The AWS Access Key Id you provided does not exist in our records.");
        }

        if (RejectSignatures)
        {
            return Error(403, "SignatureDoesNotMatch", "The request signature we calculated does not match the signature you provided.");
        }

        var (bucket, key) = SplitPath(r.Path);
        var q = r.Query;
        if (key is not null && r.Method is "GET" or "PUT" && Take(FailingKeys, key))
        {
            return Error(500, "InternalError", "We encountered an internal error. Please try again.");
        }

        try
        {
            if (bucket is null)
            {
                return r.Method == "GET" ? ListBuckets() : Error(405, "MethodNotAllowed", "Not allowed.");
            }

            var isWrite = r.Method is "PUT" or "POST" or "DELETE";
            if (isWrite && ReadOnlyBuckets.Contains(bucket))
            {
                return Error(403, "AccessDenied", "Access Denied");
            }

            if (key is null)
            {
                return BucketRequest(r, bucket, q);
            }

            if (!_buckets.TryGetValue(bucket, out var b))
            {
                return Error(404, "NoSuchBucket", "The specified bucket does not exist.");
            }

            return ObjectRequest(r, b, bucket, key, q);
        }
        catch (FormatException ex)
        {
            return Error(400, "InvalidArgument", ex.Message);
        }
    }

    private Response ListBuckets()
    {
        if (DenyListBuckets)
        {
            return Error(403, "AccessDenied", "Access Denied");
        }

        var doc = new XElement(S3 + "ListAllMyBucketsResult",
            new XElement(S3 + "Owner", new XElement(S3 + "ID", "fake-owner-id"), new XElement(S3 + "DisplayName", "storiva-test")),
            new XElement(S3 + "Buckets", _buckets.Where(b => !HiddenBuckets.Contains(b.Key)).OrderBy(b => b.Key, StringComparer.Ordinal).Select(b =>
                new XElement(S3 + "Bucket", new XElement(S3 + "Name", b.Key), new XElement(S3 + "CreationDate", Iso(b.Value.Created))))));
        return Xml(200, doc);
    }

    private Response BucketRequest(Request r, string bucket, Dictionary<string, string> q)
    {
        var exists = _buckets.TryGetValue(bucket, out var b);
        switch (r.Method)
        {
            case "PUT" when q.ContainsKey("tagging"):
                if (!exists)
                {
                    return NoBucket();
                }

                b!.Tags = ParseTags(r.Body);
                return Empty(200);
            case "PUT" when q.ContainsKey("versioning"):
                if (!exists)
                {
                    return NoBucket();
                }

                var status = XDocument.Parse(Encoding.UTF8.GetString(r.Body)).Descendants().FirstOrDefault(e => e.Name.LocalName == "Status")?.Value;
                b!.Versioning = status;
                return Empty(200);
            case "PUT":
                if (exists)
                {
                    return Error(409, "BucketAlreadyOwnedByYou", "Your previous request to create the named bucket succeeded and you already own it.");
                }

                AddBucket(bucket, DateTimeOffset.UtcNow);
                return Empty(200);
            case "DELETE" when q.ContainsKey("tagging"):
                if (exists)
                {
                    b!.Tags = [];
                }

                return Empty(204);
            case "DELETE":
                if (!exists)
                {
                    return NoBucket();
                }

                if (!b!.Objects.IsEmpty)
                {
                    return Error(409, "BucketNotEmpty", "The bucket you tried to delete is not empty");
                }

                _buckets.TryRemove(bucket, out _);
                return Empty(204);
            case "HEAD":
                return exists ? Empty(200, new() { ["x-amz-bucket-region"] = "us-east-1" }) : Empty(404);
            case "POST" when q.ContainsKey("delete"):
                return exists ? DeleteObjects(b!, r.Body) : NoBucket();
        }

        if (!exists)
        {
            return NoBucket();
        }

        if (q.ContainsKey("location"))
        {
            return Xml(200, new XElement(S3 + "LocationConstraint"));
        }

        if (q.ContainsKey("versioning"))
        {
            var doc = new XElement(S3 + "VersioningConfiguration");
            if (b!.Versioning is { } v)
            {
                doc.Add(new XElement(S3 + "Status", v));
            }

            return Xml(200, doc);
        }

        if (q.ContainsKey("acl"))
        {
            return Acl();
        }

        if (q.ContainsKey("tagging"))
        {
            return b!.Tags.Count == 0 ? Error(404, "NoSuchTagSet", "The TagSet does not exist") : Xml(200, TagsXml(b.Tags));
        }

        if (q.ContainsKey("versions"))
        {
            var prefix = q.GetValueOrDefault("prefix", string.Empty);
            var doc = new XElement(S3 + "ListVersionsResult", new XElement(S3 + "Name", bucket), new XElement(S3 + "IsTruncated", "false"),
                b!.Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(o => o.Key, StringComparer.Ordinal).Select(o =>
                    new XElement(S3 + "Version", new XElement(S3 + "Key", o.Key), new XElement(S3 + "VersionId", "null"), new XElement(S3 + "IsLatest", "true"),
                        new XElement(S3 + "LastModified", Iso(o.Value.Modified)), new XElement(S3 + "ETag", o.Value.ETag), new XElement(S3 + "Size", o.Value.Content.Length),
                        new XElement(S3 + "StorageClass", o.Value.StorageClass))));
            return Xml(200, doc);
        }

        if (q.GetValueOrDefault("list-type") == "2")
        {
            return ListObjectsV2(bucket, b!, q);
        }

        return Error(501, "NotImplemented", "This fake server does not implement that request.");
    }

    private Response ListObjectsV2(string bucket, Bucket b, Dictionary<string, string> q)
    {
        var prefix = q.GetValueOrDefault("prefix", string.Empty);
        var delimiter = q.GetValueOrDefault("delimiter");
        var max = int.TryParse(q.GetValueOrDefault("max-keys"), out var m) ? Math.Clamp(m, 1, 1000) : 1000;
        var after = q.TryGetValue("continuation-token", out var token) ? Encoding.UTF8.GetString(Convert.FromBase64String(token)) : null;
        var urlEncode = q.GetValueOrDefault("encoding-type") == "url";

        // Entries in key order: objects, or common prefixes when a delimiter folds them.
        var entries = new List<(string Name, bool IsPrefix, StoredObject? Obj)>();
        foreach (var (k, o) in b.Objects.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!k.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (delimiter is { Length: > 0 } d)
            {
                var rest = k[prefix.Length..];
                var at = rest.IndexOf(d, StringComparison.Ordinal);
                if (at >= 0)
                {
                    var common = prefix + rest[..(at + d.Length)];
                    if (entries.Count == 0 || entries[^1].Name != common)
                    {
                        entries.Add((common, true, null));
                    }

                    continue;
                }
            }

            entries.Add((k, false, o));
        }

        var page = entries.Where(e => after is null || string.CompareOrdinal(e.Name, after) > 0).Take(max + 1).ToList();
        var truncated = page.Count > max;
        page = page.Take(max).ToList();
        string Enc(string s) => urlEncode ? Uri.EscapeDataString(s).Replace("%2F", "/", StringComparison.Ordinal) : s;

        var doc = new XElement(S3 + "ListBucketResult",
            new XElement(S3 + "Name", bucket),
            new XElement(S3 + "Prefix", Enc(prefix)),
            new XElement(S3 + "KeyCount", page.Count),
            new XElement(S3 + "MaxKeys", max),
            new XElement(S3 + "IsTruncated", truncated ? "true" : "false"));
        if (delimiter is not null)
        {
            doc.Add(new XElement(S3 + "Delimiter", Enc(delimiter)));
        }

        if (urlEncode)
        {
            doc.Add(new XElement(S3 + "EncodingType", "url"));
        }

        if (token is not null)
        {
            doc.Add(new XElement(S3 + "ContinuationToken", token));
        }

        if (truncated)
        {
            doc.Add(new XElement(S3 + "NextContinuationToken", Convert.ToBase64String(Encoding.UTF8.GetBytes(page[^1].Name))));
        }

        foreach (var e in page)
        {
            doc.Add(e.IsPrefix
                ? new XElement(S3 + "CommonPrefixes", new XElement(S3 + "Prefix", Enc(e.Name)))
                : new XElement(S3 + "Contents",
                    new XElement(S3 + "Key", Enc(e.Name)),
                    new XElement(S3 + "LastModified", Iso(e.Obj!.Modified)),
                    new XElement(S3 + "ETag", e.Obj.ETag),
                    new XElement(S3 + "Size", e.Obj.Content.Length),
                    new XElement(S3 + "StorageClass", e.Obj.StorageClass)));
        }

        return Xml(200, doc);
    }

    private Response ObjectRequest(Request r, Bucket b, string bucket, string key, Dictionary<string, string> q)
    {
        b.Objects.TryGetValue(key, out var obj);
        switch (r.Method)
        {
            case "PUT" when q.ContainsKey("tagging"):
                if (obj is null)
                {
                    return NoKey();
                }

                b.Objects[key] = obj with { Tags = ParseTags(r.Body) };
                return Empty(200);
            case "PUT" when r.Header("x-amz-copy-source") is { } source:
                var decoded = Uri.UnescapeDataString(source.Split('?')[0]).TrimStart('/');
                var slash = decoded.IndexOf('/');
                if (slash <= 0 || !_buckets.TryGetValue(decoded[..slash], out var sourceBucket) || !sourceBucket.Objects.TryGetValue(decoded[(slash + 1)..], out var original))
                {
                    return NoKey();
                }

                var copy = original with { Modified = DateTimeOffset.UtcNow, StorageClass = r.Header("x-amz-storage-class") ?? (decoded == bucket + "/" + key ? original.StorageClass : "STANDARD") };
                b.Objects[key] = copy;
                return Xml(200, new XElement(S3 + "CopyObjectResult", new XElement(S3 + "LastModified", Iso(copy.Modified)), new XElement(S3 + "ETag", copy.ETag)));
            case "PUT":
                var metadata = r.Headers.Where(h => h.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)).ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value);
                var stored = new StoredObject(r.Body, r.Header("content-type") ?? "binary/octet-stream", DateTimeOffset.UtcNow, r.Header("x-amz-storage-class") ?? "STANDARD", metadata, []);
                b.Objects[key] = stored;
                return Empty(200, new() { ["ETag"] = stored.ETag });
            case "DELETE":
                if (UndeletableKeys.Contains(key))
                {
                    return Error(403, "AccessDenied", "Access Denied");
                }

                b.Objects.TryRemove(key, out _);
                return Empty(204);
            case "POST":
                return Error(501, "NotImplemented", "Multipart uploads are not implemented by this fake server.");
        }

        if (obj is null)
        {
            return r.Method == "HEAD" ? Empty(404) : NoKey();
        }

        if (q.ContainsKey("acl"))
        {
            return Acl();
        }

        if (q.ContainsKey("tagging"))
        {
            return Xml(200, TagsXml(obj.Tags));
        }

        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = obj.ContentType,
            ["ETag"] = obj.ETag,
            ["Last-Modified"] = obj.Modified.ToString("R", CultureInfo.InvariantCulture),
            ["Accept-Ranges"] = "bytes",
        };
        if (obj.StorageClass != "STANDARD")
        {
            headers["x-amz-storage-class"] = obj.StorageClass;
        }

        foreach (var (name, value) in obj.Metadata)
        {
            headers[name] = value;
        }

        var body = obj.Content;
        var status = 200;
        if (r.Header("range") is { } range && range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            if (body.Length == 0)
            {
                return Error(416, "InvalidRange", "The requested range is not satisfiable");
            }

            var parts = range[6..].Split('-');
            var start = long.Parse(parts[0], CultureInfo.InvariantCulture);
            var end = parts[1].Length == 0 ? body.Length - 1 : Math.Min(long.Parse(parts[1], CultureInfo.InvariantCulture), body.Length - 1);
            headers["Content-Range"] = $"bytes {start}-{end}/{body.Length}";
            body = body[(int)start..(int)(end + 1)];
            status = 206;
        }

        return new Response(status, headers, body);
    }

    private Response DeleteObjects(Bucket b, byte[] body)
    {
        var keys = XDocument.Parse(Encoding.UTF8.GetString(body)).Descendants().Where(e => e.Name.LocalName == "Key").Select(e => e.Value).ToList();
        var result = new XElement(S3 + "DeleteResult");
        foreach (var key in keys)
        {
            if (UndeletableKeys.Contains(key))
            {
                result.Add(new XElement(S3 + "Error", new XElement(S3 + "Key", key), new XElement(S3 + "Code", "AccessDenied"), new XElement(S3 + "Message", "Access Denied")));
                continue;
            }

            b.Objects.TryRemove(key, out _);
            result.Add(new XElement(S3 + "Deleted", new XElement(S3 + "Key", key)));
        }

        return Xml(200, result);
    }

    private static Response Acl()
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var doc = new XElement(S3 + "AccessControlPolicy",
            new XElement(S3 + "Owner", new XElement(S3 + "ID", "fake-owner-id"), new XElement(S3 + "DisplayName", "storiva-test")),
            new XElement(S3 + "AccessControlList",
                new XElement(S3 + "Grant",
                    new XElement(S3 + "Grantee", new XAttribute(XNamespace.Xmlns + "xsi", xsi), new XAttribute(xsi + "type", "CanonicalUser"),
                        new XElement(S3 + "ID", "fake-owner-id"), new XElement(S3 + "DisplayName", "storiva-test")),
                    new XElement(S3 + "Permission", "FULL_CONTROL"))));
        return Xml(200, doc);
    }

    private static XElement TagsXml(IReadOnlyList<(string Key, string Value)> tags) =>
        new(S3 + "Tagging", new XElement(S3 + "TagSet", tags.Select(t => new XElement(S3 + "Tag", new XElement(S3 + "Key", t.Key), new XElement(S3 + "Value", t.Value)))));

    private static List<(string, string)> ParseTags(byte[] body) =>
        XDocument.Parse(Encoding.UTF8.GetString(body)).Descendants().Where(e => e.Name.LocalName == "Tag")
            .Select(t => (t.Elements().First(e => e.Name.LocalName == "Key").Value, t.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value ?? string.Empty))
            .ToList();

    private static (string? Bucket, string? Key) SplitPath(string path)
    {
        var trimmed = path.TrimStart('/');
        if (trimmed.Length == 0)
        {
            return (null, null);
        }

        var slash = trimmed.IndexOf('/');
        if (slash < 0 || slash == trimmed.Length - 1)
        {
            return (Uri.UnescapeDataString(trimmed.TrimEnd('/')), null);
        }

        return (Uri.UnescapeDataString(trimmed[..slash]), Uri.UnescapeDataString(trimmed[(slash + 1)..]));
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static Response Xml(int status, XElement root)
    {
        var bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + root.ToString(SaveOptions.DisableFormatting));
        return new Response(status, new() { ["Content-Type"] = "application/xml" }, bytes);
    }

    private static Response Empty(int status, Dictionary<string, string>? headers = null) => new(status, headers ?? new(), []);

    private static Response NoBucket() => Error(404, "NoSuchBucket", "The specified bucket does not exist.");

    private static Response NoKey() => Error(404, "NoSuchKey", "The specified key does not exist.");

    private static Response Error(int status, string code, string message) =>
        Xml(status, new XElement("Error", new XElement("Code", code), new XElement("Message", message), new XElement("RequestId", "fake-request")));

    private static bool Take(ConcurrentDictionary<string, int> counts, string key)
    {
        while (counts.TryGetValue(key, out var left) && left > 0)
        {
            if (counts.TryUpdate(key, left - 1, left))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WriteResponseAsync(Stream stream, Response response, bool head, int? bodyLimit = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n");
        foreach (var (name, value) in response.Headers)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }

        sb.Append("x-amz-request-id: fake-request\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {response.Body.Length}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Date: {DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture)}\r\n");
        sb.Append("Connection: keep-alive\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);
        if (!head && response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body.AsMemory(0, bodyLimit ?? response.Body.Length)).ConfigureAwait(false);
        }

        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        206 => "Partial Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        416 => "Range Not Satisfiable",
        500 => "Internal Server Error",
        _ => "Not Implemented",
    };

    private sealed record Response(int Status, Dictionary<string, string> Headers, byte[] Body);

    private sealed class Bucket(DateTimeOffset created)
    {
        public DateTimeOffset Created { get; } = created;

        public ConcurrentDictionary<string, StoredObject> Objects { get; } = new(StringComparer.Ordinal);

        public List<(string, string)> Tags { get; set; } = [];

        public string? Versioning { get; set; }
    }

    private sealed record StoredObject(byte[] Content, string ContentType, DateTimeOffset Modified, string StorageClass, Dictionary<string, string> Metadata, List<(string Key, string Value)> Tags)
    {
        public string ETag => "\"" + Convert.ToHexString(MD5.HashData(Content)).ToLowerInvariant() + "\"";
    }

    internal sealed class Request(string method, string rawTarget, Dictionary<string, string> headers, byte[] body)
    {
        public string Method { get; } = method;

        public string RawTarget { get; } = rawTarget;

        public Dictionary<string, string> Headers { get; } = headers;

        public byte[] Body { get; } = body;

        public string Path => RawTarget.Split('?')[0];

        public Dictionary<string, string> Query
        {
            get
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                var at = RawTarget.IndexOf('?');
                if (at < 0)
                {
                    return result;
                }

                foreach (var part in RawTarget[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    var name = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
                    result[name] = eq < 0 ? string.Empty : HttpUtility.UrlDecode(part[(eq + 1)..]);
                }

                return result;
            }
        }

        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>HTTP/1.1 request reader: Content-Length or chunked bodies, Expect: 100-continue, aws-chunked payloads.</summary>
    private sealed class HttpReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        public async Task<Request?> ReadRequestAsync(CancellationToken ct)
        {
            var requestLine = await ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(requestLine))
            {
                return null;
            }

            var parts = requestLine.Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await ReadLineAsync(ct).ConfigureAwait(false)))
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            if (headers.TryGetValue("expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            byte[] body;
            if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                body = await ReadHttpChunkedAsync(ct).ConfigureAwait(false);
            }
            else if (headers.TryGetValue("content-length", out var lengthText) && long.TryParse(lengthText, out var length) && length > 0)
            {
                body = await ReadExactAsync((int)length, ct).ConfigureAwait(false);
            }
            else
            {
                body = [];
            }

            var isAwsChunked = (headers.TryGetValue("x-amz-content-sha256", out var sha) && sha.StartsWith("STREAMING-", StringComparison.Ordinal))
                || (headers.TryGetValue("content-encoding", out var ce) && ce.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase));
            if (isAwsChunked)
            {
                body = DecodeAwsChunked(body);
            }

            return new Request(parts[0], parts.Length > 1 ? parts[1] : "/", headers, body);
        }

        private static byte[] DecodeAwsChunked(byte[] data)
        {
            using var output = new MemoryStream();
            var position = 0;
            while (position < data.Length)
            {
                var lineEnd = IndexOfCrLf(data, position);
                if (lineEnd < 0)
                {
                    break;
                }

                var header = Encoding.ASCII.GetString(data, position, lineEnd - position);
                var size = Convert.ToInt32(header.Split(';')[0].Trim(), 16);
                position = lineEnd + 2;
                if (size == 0)
                {
                    break; // trailers (checksums) follow; not needed here
                }

                output.Write(data, position, size);
                position += size + 2;
            }

            return output.ToArray();
        }

        private static int IndexOfCrLf(byte[] data, int from)
        {
            for (var i = from; i < data.Length - 1; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private async Task<byte[]> ReadHttpChunkedAsync(CancellationToken ct)
        {
            using var output = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsync(ct).ConfigureAwait(false) ?? string.Empty;
                var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
                if (size == 0)
                {
                    // Trailer headers until an empty line.
                    while (!string.IsNullOrEmpty(await ReadLineAsync(ct).ConfigureAwait(false)))
                    {
                    }

                    return output.ToArray();
                }

                output.Write(await ReadExactAsync(size, ct).ConfigureAwait(false));
                await ReadLineAsync(ct).ConfigureAwait(false);
            }
        }

        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var bytes = new List<byte>();
            while (true)
            {
                if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                {
                    return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                }

                var b = _buffer[_start++];
                if (b == '\n')
                {
                    if (bytes.Count > 0 && bytes[^1] == '\r')
                    {
                        bytes.RemoveAt(bytes.Count - 1);
                    }

                    return Encoding.ASCII.GetString(bytes.ToArray());
                }

                bytes.Add(b);
            }
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
        {
            var result = new byte[count];
            var written = 0;
            while (written < count)
            {
                if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                {
                    throw new IOException("Connection closed in the middle of a body.");
                }

                var n = Math.Min(count - written, _end - _start);
                Array.Copy(_buffer, _start, result, written, n);
                _start += n;
                written += n;
            }

            return result;
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
            return _end > 0;
        }
    }
}
