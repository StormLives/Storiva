using System.Text;
using Storiva.Core.Connections;
using Storiva.Core.Remote;
using Storiva.Infrastructure.S3;

namespace Storiva.Tests;

/// <summary>
/// Skipped unless the developer points the tests at an isolated test environment (see docs/TESTING.md):
/// <code>
/// STORIVA_IT_PROVIDER   aws | compatible
/// STORIVA_IT_ENDPOINT   https://minio.test.internal:9000   (compatible only)
/// STORIVA_IT_REGION     us-east-1
/// STORIVA_IT_ACCESS_KEY / STORIVA_IT_SECRET_KEY            (test credentials, never real ones)
/// STORIVA_IT_BUCKET     an existing bucket dedicated to tests
/// STORIVA_IT_ALLOW_WRITES=1                                 (optional: allows the write test)
/// </code>
/// The read test only lists. The write test only touches keys under a new "storiva-it/&lt;guid&gt;/" prefix and
/// deletes exactly what it created.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(bool writes = false)
    {
        if (IntegrationEnvironment.Load() is null)
        {
            Skip = "Integration environment not configured (STORIVA_IT_* variables).";
        }
        else if (writes && Environment.GetEnvironmentVariable("STORIVA_IT_ALLOW_WRITES") != "1")
        {
            Skip = "Writes not allowed (set STORIVA_IT_ALLOW_WRITES=1 for an isolated test bucket).";
        }
    }
}

internal sealed record IntegrationEnvironment(ConnectionProfile Profile, StorageCredentials Credentials, string Bucket)
{
    public static IntegrationEnvironment? Load()
    {
        string? Var(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        var provider = Var("STORIVA_IT_PROVIDER");
        var key = Var("STORIVA_IT_ACCESS_KEY");
        var secret = Var("STORIVA_IT_SECRET_KEY");
        var bucket = Var("STORIVA_IT_BUCKET");
        if (provider is null || key is null || secret is null || bucket is null)
        {
            return null;
        }

        var compatible = provider.Equals("compatible", StringComparison.OrdinalIgnoreCase);
        var profile = new ConnectionProfile
        {
            Name = "integration",
            Provider = compatible ? StorageProvider.S3Compatible : StorageProvider.AwsS3,
            Endpoint = compatible ? Var("STORIVA_IT_ENDPOINT") : null,
            Region = Var("STORIVA_IT_REGION") ?? "us-east-1",
            ForcePathStyle = compatible,
            AllowInsecureHttp = Var("STORIVA_IT_ENDPOINT")?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true,
            UseFlexibleChecksums = !compatible,
        };
        return new IntegrationEnvironment(profile, new StorageCredentials(key, secret), bucket);
    }
}

public class IntegrationTests
{
    [IntegrationFact]
    public async Task Connects_and_lists_without_writing()
    {
        var env = IntegrationEnvironment.Load()!;
        using var client = new S3ClientFactory().Create(env.Profile, env.Credentials);
        var test = await client.TestConnectionAsync(CancellationToken.None);
        Assert.True(test.Succeeded, test.Summary);
        var page = await client.ListObjectsAsync(env.Bucket, string.Empty, null, 10, CancellationToken.None);
        Assert.NotNull(page);
    }

    [IntegrationFact(writes: true)]
    public async Task Upload_download_copy_and_delete_under_a_private_prefix()
    {
        var env = IntegrationEnvironment.Load()!;
        using var client = new S3ClientFactory().Create(env.Profile, env.Credentials);
        var prefix = $"storiva-it/{Guid.NewGuid():N}/";
        using var temp = new TempDataFolder();
        var local = Path.Combine(temp.Root, "sample.txt");
        File.WriteAllText(local, "Storiva integration test " + DateTimeOffset.UtcNow.ToString("O"));
        var created = new List<string>();
        try
        {
            await client.UploadObjectAsync(env.Bucket, prefix + "sample.txt", local, "text/plain", null, CancellationToken.None);
            created.Add(prefix + "sample.txt");
            await client.CopyObjectAsync(env.Bucket, prefix + "sample.txt", env.Bucket, prefix + "copy.txt", null, CancellationToken.None);
            created.Add(prefix + "copy.txt");

            using var downloaded = new MemoryStream();
            await client.DownloadObjectAsync(env.Bucket, prefix + "copy.txt", downloaded, null, CancellationToken.None);
            Assert.Equal(File.ReadAllText(local), Encoding.UTF8.GetString(downloaded.ToArray()));
        }
        finally
        {
            // Only the keys this test created.
            if (created.Count > 0)
            {
                var result = await client.DeleteObjectsAsync(env.Bucket, created, CancellationToken.None);
                Assert.Empty(result.Failed);
            }
        }
    }
}
