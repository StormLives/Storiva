namespace Storiva.Core.Remote;

/// <summary>Provider-neutral classification of a failed remote operation.</summary>
public enum StorageErrorKind
{
    Unknown,

    /// <summary>DNS failure, refused/reset connection, timeout: the server was not reached.</summary>
    EndpointUnreachable,

    /// <summary>The TLS handshake failed (untrusted, expired or mismatched certificate, protocol mismatch).</summary>
    Tls,

    /// <summary>The server rejected the access key or the signature (unknown key, wrong secret, expired token).</summary>
    InvalidCredentials,

    /// <summary>No credentials could be obtained (missing profile, empty default chain, undecryptable secret).</summary>
    CredentialsUnavailable,

    /// <summary>Authenticated, but not allowed to perform this operation.</summary>
    AccessDenied,

    /// <summary>The request went to the wrong region or the endpoint/addressing style does not fit the server.</summary>
    RegionOrConfiguration,

    NotFound,

    /// <summary>Name taken, bucket not empty, precondition failed.</summary>
    Conflict,

    /// <summary>The provider does not implement the operation (e.g. an AWS extension on a compatible server).</summary>
    NotSupported,

    InvalidRequest,

    Throttled,

    /// <summary>5xx response.</summary>
    ServerError,

    Canceled,

    /// <summary>Writing or reading a local file failed (disk full, permissions, path).</summary>
    LocalIo,
}

/// <summary>A failure that is safe to show: <see cref="Message"/> never contains secrets.</summary>
/// <param name="Code">Provider error code (e.g. AccessDenied), when known.</param>
/// <param name="StatusCode">HTTP status, when a response was received.</param>
public sealed record StorageError(StorageErrorKind Kind, string Message, string? Code = null, int? StatusCode = null, string? RequestId = null)
{
    /// <summary>One line for the Event log and the task list.</summary>
    public string Describe()
    {
        var details = new List<string>();
        if (Code is not null)
        {
            details.Add(Code);
        }

        if (StatusCode is { } status)
        {
            details.Add("HTTP " + status);
        }

        return details.Count == 0 ? Message : $"{Message} ({string.Join(", ", details)})";
    }

    public static string Title(StorageErrorKind kind) => kind switch
    {
        StorageErrorKind.EndpointUnreachable => "Endpoint not reachable",
        StorageErrorKind.Tls => "Secure connection (TLS) failed",
        StorageErrorKind.InvalidCredentials => "Credentials rejected",
        StorageErrorKind.CredentialsUnavailable => "No credentials",
        StorageErrorKind.AccessDenied => "Access denied",
        StorageErrorKind.RegionOrConfiguration => "Region or endpoint configuration mismatch",
        StorageErrorKind.NotFound => "Not found",
        StorageErrorKind.Conflict => "Conflict",
        StorageErrorKind.NotSupported => "Not supported by this server",
        StorageErrorKind.InvalidRequest => "Request rejected",
        StorageErrorKind.Throttled => "Too many requests",
        StorageErrorKind.ServerError => "Server error",
        StorageErrorKind.Canceled => "Canceled",
        StorageErrorKind.LocalIo => "Local file error",
        _ => "Operation failed",
    };
}

/// <summary>Thrown by <see cref="IObjectStorageClient"/> implementations; carries a safe <see cref="StorageError"/>.</summary>
public sealed class StorageOperationException : Exception
{
    public StorageOperationException(StorageError error, Exception? inner = null)
        : base(error.Describe(), inner)
    {
        Error = error;
    }

    public StorageError Error { get; }

    public StorageErrorKind Kind => Error.Kind;
}
