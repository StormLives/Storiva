using Storiva.Core.Remote;

namespace Storiva.Core.Connections;

/// <summary>State of a connection as shown in Manage Connections and the status bar.</summary>
public enum ConnectionStatus
{
    /// <summary>Saved, never tested in this or a previous session.</summary>
    NotTested,
    Disconnected,
    Connecting,
    Connected,

    /// <summary>The credentials work, but listing buckets was refused (e.g. no s3:ListAllMyBuckets).</summary>
    ConnectedLimited,
    AccessDenied,
    InvalidCredentials,
    CredentialsUnavailable,
    NetworkError,
    TlsError,
    ConfigurationError,
    Canceled,
    Failed,
}

public static class ConnectionStatusText
{
    public static string Label(ConnectionStatus status) => status switch
    {
        ConnectionStatus.NotTested => "Saved (not tested)",
        ConnectionStatus.Disconnected => "Disconnected",
        ConnectionStatus.Connecting => "Connecting…",
        ConnectionStatus.Connected => "Connected",
        ConnectionStatus.ConnectedLimited => "Connected (limited permissions)",
        ConnectionStatus.AccessDenied => "Access denied",
        ConnectionStatus.InvalidCredentials => "Invalid credentials",
        ConnectionStatus.CredentialsUnavailable => "No credentials",
        ConnectionStatus.NetworkError => "Network error",
        ConnectionStatus.TlsError => "TLS error",
        ConnectionStatus.ConfigurationError => "Region/endpoint mismatch",
        ConnectionStatus.Canceled => "Test canceled",
        _ => "Error",
    };

    public static bool IsHealthy(ConnectionStatus status) => status is ConnectionStatus.Connected or ConnectionStatus.ConnectedLimited;

    public static bool IsError(ConnectionStatus status) => status is ConnectionStatus.AccessDenied or ConnectionStatus.InvalidCredentials
        or ConnectionStatus.CredentialsUnavailable or ConnectionStatus.NetworkError or ConnectionStatus.TlsError
        or ConnectionStatus.ConfigurationError or ConnectionStatus.Failed;

    /// <summary>Maps an operation error to the connection state it implies.</summary>
    public static ConnectionStatus FromError(StorageErrorKind kind) => kind switch
    {
        StorageErrorKind.EndpointUnreachable => ConnectionStatus.NetworkError,
        StorageErrorKind.Tls => ConnectionStatus.TlsError,
        StorageErrorKind.InvalidCredentials => ConnectionStatus.InvalidCredentials,
        StorageErrorKind.CredentialsUnavailable => ConnectionStatus.CredentialsUnavailable,
        StorageErrorKind.AccessDenied => ConnectionStatus.AccessDenied,
        StorageErrorKind.RegionOrConfiguration => ConnectionStatus.ConfigurationError,
        StorageErrorKind.Canceled => ConnectionStatus.Canceled,
        _ => ConnectionStatus.Failed,
    };
}

/// <summary>Result of "Test connection": real outcome of read-only requests, never of a write.</summary>
/// <param name="VisibleBuckets">Buckets returned by the first page of the bucket list, when it was allowed.</param>
public sealed record ConnectionTestResult(ConnectionStatus Status, string Summary, int? VisibleBuckets = null, StorageError? Error = null, TimeSpan? Duration = null)
{
    public bool Succeeded => ConnectionStatusText.IsHealthy(Status);
}
