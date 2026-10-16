using System.Globalization;

namespace Storiva.Core.Remote;

/// <summary>
/// A pre-signed GET link. It is a bearer credential: anyone who has it can download the object until
/// <see cref="ExpiresAt"/>. Storiva shows it to the user and copies it on request, but never logs or stores it.
/// </summary>
/// <param name="Key">Exact object key.</param>
/// <param name="UsesTemporaryCredentials">The link carries a session token: it stops working when those credentials
/// expire, which can be before <see cref="ExpiresAt"/>.</param>
public sealed record PresignedUrl(string Bucket, string Key, string Url, DateTimeOffset ExpiresAt, bool UsesTemporaryCredentials)
{
    /// <summary>Never prints the URL (records print every property by default).</summary>
    public override string ToString() => $"Pre-signed link for s3://{Bucket}/{Key} (expires {ExpiresAt:u})";
}

/// <summary>Rules and helpers for "Generate Web URL" and the copy-address commands.</summary>
public static class ShareLinks
{
    /// <summary>Longest validity of a Signature Version 4 pre-signed URL (7 days).</summary>
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(7);

    /// <summary>Validities offered in the dialog.</summary>
    public static readonly IReadOnlyList<TimeSpan> ValidityChoices =
        [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(24), MaxValidity];

    public static readonly TimeSpan DefaultValidity = TimeSpan.FromHours(1);

    /// <summary>Most links generated at once (one per selected file, or per file of a bucket).</summary>
    public const int MaxLinks = 1000;

    /// <summary>"15 min", "1 h", "24 h", "7 days" (used in the dialog and in the Event log).</summary>
    public static string Describe(TimeSpan validity)
    {
        if (validity.TotalDays > 1 && validity.TotalDays % 1 == 0)
        {
            return $"{validity.TotalDays.ToString(CultureInfo.InvariantCulture)} days";
        }

        if (validity.TotalHours >= 1 && validity.TotalHours % 1 == 0)
        {
            return $"{validity.TotalHours.ToString(CultureInfo.InvariantCulture)} h";
        }

        return $"{Math.Max(1, (int)Math.Round(validity.TotalMinutes)).ToString(CultureInfo.InvariantCulture)} min";
    }

    /// <summary>Throws unless <paramref name="validity"/> is between 1 second and <see cref="MaxValidity"/>.</summary>
    public static void ValidateValidity(TimeSpan validity)
    {
        if (validity < TimeSpan.FromSeconds(1) || validity > MaxValidity)
        {
            throw new ArgumentOutOfRangeException(nameof(validity), "A pre-signed link is valid for at least 1 second and at most 7 days.");
        }
    }

    /// <summary>"s3://bucket/key" (no endpoint, no credentials, not a download link). A null key gives the bucket.</summary>
    public static string S3Uri(string bucket, string? key) => "s3://" + bucket + "/" + (key ?? string.Empty);

    /// <summary>One link per key, in order. Computed locally by the client; nothing is sent to the server.</summary>
    public static async Task<IReadOnlyList<PresignedUrl>> CreateLinksAsync(IObjectStorageClient client, string bucket, IReadOnlyList<string> keys, TimeSpan validity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateValidity(validity);
        if (keys.Count > MaxLinks)
        {
            throw new ArgumentException($"At most {MaxLinks} links at once.", nameof(keys));
        }

        var links = new List<PresignedUrl>(keys.Count);
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            links.Add(await client.CreatePresignedGetUrlAsync(bucket, key, validity, ct).ConfigureAwait(true));
        }

        return links;
    }

    /// <summary>
    /// Keys of the files of a bucket (folder markers left out), listed page by page. Stops after
    /// <paramref name="max"/> + 1 keys, so a caller can tell "more than max" without listing a huge bucket.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListFileKeysAsync(IObjectStorageClient client, string bucket, int max, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var keys = new List<string>();
        await foreach (var obj in client.ListAllObjectsAsync(bucket, string.Empty, ct).ConfigureAwait(true))
        {
            if (obj.Key.EndsWith('/'))
            {
                continue;
            }

            keys.Add(obj.Key);
            if (keys.Count > max)
            {
                break;
            }
        }

        return keys;
    }
}
