using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using Amazon.Runtime;
using Amazon.S3;
using Storiva.Core.Connections;
using Storiva.Core.Remote;

namespace Storiva.Infrastructure.S3;

/// <summary>
/// Turns AWS SDK / transport exceptions into <see cref="StorageError"/> with a clear, actionable message. Messages are
/// written by Storiva; the server's own text is appended only after <see cref="SecretRedactor"/> has run on it.
/// </summary>
public static class S3ErrorMapper
{
    public static StorageOperationException Map(Exception exception, string operation, StorageCredentials? credentials = null, bool isAws = true)
    {
        var error = ToError(exception, operation, credentials, isAws);
        return new StorageOperationException(error, exception);
    }

    public static StorageError ToError(Exception exception, string operation, StorageCredentials? credentials = null, bool isAws = true)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is StorageOperationException already)
        {
            return already.Error;
        }

        // An HTTP timeout surfaces as a TaskCanceledException around a TimeoutException: not a user cancellation.
        if (Find<TimeoutException>(exception) is not null)
        {
            return new StorageError(StorageErrorKind.EndpointUnreachable, $"{operation} timed out. The endpoint did not answer in time.");
        }

        if (exception is OperationCanceledException)
        {
            return new StorageError(StorageErrorKind.Canceled, $"{operation} was canceled.");
        }

        // Look through wrappers (AmazonServiceException → HttpRequestException → AuthenticationException …).
        if (Find<AuthenticationException>(exception) is { } tls)
        {
            return new StorageError(StorageErrorKind.Tls,
                $"{operation} failed: the server's TLS certificate is not trusted or the secure connection could not be established. " +
                "Storiva does not accept invalid certificates. If the server uses a private certificate authority, install that CA in " +
                "Windows (see docs/CONNECTION_SETUP.md, \"TLS problems\"). Details: " + SecretRedactor.Redact(tls.Message, credentials));
        }

        if (exception is AmazonServiceException service && (service.StatusCode != 0 || service.ErrorCode is not null))
        {
            return FromService(service, operation, credentials, isAws);
        }

        if (Find<SocketException>(exception) is { } socket)
        {
            return new StorageError(StorageErrorKind.EndpointUnreachable,
                $"{operation} failed: the endpoint could not be reached ({socket.SocketErrorCode}). Check the address, the port, the network and any proxy or firewall.");
        }

        if (Find<HttpIOException>(exception) is not null)
        {
            // The connection ended while the body was being read (e.g. a network drop in the middle of a download).
            return new StorageError(StorageErrorKind.EndpointUnreachable,
                $"{operation} failed: the connection was interrupted before the transfer finished. Check the network and try again.");
        }

        if (Find<HttpRequestException>(exception) is { } http)
        {
            return new StorageError(StorageErrorKind.EndpointUnreachable,
                $"{operation} failed: no response from the endpoint. Check the address, the port, the network and any proxy or firewall. Details: {SecretRedactor.Redact(http.Message, credentials)}");
        }

        if (exception is AmazonClientException client)
        {
            var message = SecretRedactor.Redact(client.Message, credentials);
            if (message.Contains("credential", StringComparison.OrdinalIgnoreCase) || message.Contains("profile", StringComparison.OrdinalIgnoreCase))
            {
                return new StorageError(StorageErrorKind.CredentialsUnavailable,
                    $"{operation} failed: no usable credentials were found. {message}");
            }

            return new StorageError(StorageErrorKind.Unknown, $"{operation} failed: {message}");
        }

        if (exception is IOException or UnauthorizedAccessException)
        {
            return new StorageError(StorageErrorKind.LocalIo, $"{operation} failed: {SecretRedactor.Redact(exception.Message, credentials)}");
        }

        return new StorageError(StorageErrorKind.Unknown, $"{operation} failed ({exception.GetType().Name}).");
    }

    private static StorageError FromService(AmazonServiceException ex, string operation, StorageCredentials? credentials, bool isAws)
    {
        var code = ex.ErrorCode;
        var status = (int)ex.StatusCode;
        var requestId = ex.RequestId;
        var serverText = SecretRedactor.Redact(ex.Message, credentials);

        StorageError Make(StorageErrorKind kind, string message) =>
            new(kind, message, code, status == 0 ? null : status, requestId);

        switch (code)
        {
            case "InvalidAccessKeyId":
                return Make(StorageErrorKind.InvalidCredentials, $"{operation} failed: the server does not know this access key ID. Check the key, and that it belongs to this {(isAws ? "AWS account/partition" : "server")}.");
            case "SignatureDoesNotMatch":
                return Make(StorageErrorKind.InvalidCredentials, $"{operation} failed: the request signature was rejected. The secret access key is probably wrong{(isAws ? string.Empty : ", or the region does not match the server's configured region")}.");
            case "ExpiredToken" or "TokenRefreshRequired" or "InvalidToken" or "ExpiredTokenException":
                return Make(StorageErrorKind.InvalidCredentials, $"{operation} failed: the temporary credentials (session token) are expired or invalid. Obtain new ones.");
            case "RequestTimeTooSkewed":
                return Make(StorageErrorKind.InvalidCredentials, $"{operation} failed: this computer's clock differs too much from the server's. Synchronise the Windows clock.");
            case "AccessDenied" or "AllAccessDisabled" or "AccountProblem":
                return Make(StorageErrorKind.AccessDenied, $"{operation} was refused: these credentials are not allowed to do this (policy, ACL or bucket settings).");
            case "PermanentRedirect" or "AuthorizationHeaderMalformed" or "IllegalLocationConstraintException" or "InvalidLocationConstraint" or "IncorrectEndpoint" or "301":
                return Make(StorageErrorKind.RegionOrConfiguration,
                    isAws
                        ? $"{operation} failed: the bucket is in a different region than the request, or the region is not valid here. Storiva retries in the bucket's region when it can; otherwise check the connection's region."
                        : $"{operation} failed: the server rejected the region or the addressing style. Check the region (MinIO: usually us-east-1) and try path-style addressing.");
            case "NoSuchBucket":
                return Make(StorageErrorKind.NotFound, $"{operation} failed: the bucket does not exist (or is not visible to these credentials).");
            case "NoSuchKey" or "NotFound" or "NoSuchVersion":
                return Make(StorageErrorKind.NotFound, $"{operation} failed: the object does not exist (it may have been deleted or renamed).");
            case "BucketAlreadyExists":
                return Make(StorageErrorKind.Conflict, $"{operation} failed: this bucket name is already taken{(isAws ? " (bucket names are global across all AWS accounts)" : string.Empty)}. Choose another name.");
            case "BucketAlreadyOwnedByYou":
                return Make(StorageErrorKind.Conflict, $"{operation} failed: you already own a bucket with this name.");
            case "BucketNotEmpty":
                return Make(StorageErrorKind.Conflict, $"{operation} failed: the bucket is not empty. Delete its objects (and, with versioning, their versions) first. Storiva never empties a bucket automatically.");
            case "InvalidBucketName":
                return Make(StorageErrorKind.InvalidRequest, $"{operation} failed: the server rejected the bucket name.");
            case "TooManyBuckets":
                return Make(StorageErrorKind.Conflict, $"{operation} failed: the account has reached its bucket limit.");
            case "NoSuchTagSet" or "NoSuchTagSetError":
                return Make(StorageErrorKind.NotFound, "No tags.");
            case "NotImplemented" or "XNotImplemented" or "MethodNotAllowed" or "UnsupportedOperation":
                return Make(StorageErrorKind.NotSupported, $"{operation} is not supported by this server or for this item.");
            case "InvalidObjectState":
                return Make(StorageErrorKind.Conflict, $"{operation} failed: the object is archived (e.g. Glacier) and must be restored first.");
            case "EntityTooLarge" or "InvalidRequest" when serverText.Contains("copy", StringComparison.OrdinalIgnoreCase) && serverText.Contains("5", StringComparison.Ordinal):
                return Make(StorageErrorKind.NotSupported, $"{operation} failed: objects larger than 5 GB cannot be copied in one request; this version of Storiva does not split copies.");
            case "SlowDown" or "Throttling" or "ThrottlingException" or "RequestLimitExceeded" or "ServiceUnavailable" when status is 503 or 429:
                return Make(StorageErrorKind.Throttled, $"{operation} was throttled by the server. Try again in a moment.");
            case "MissingContentLength" or "InvalidArgument" or "MalformedXML" or "InvalidRequest" or "XAmzContentSHA256Mismatch" or "BadDigest" or "InvalidDigest":
                return Make(StorageErrorKind.InvalidRequest,
                    $"{operation} was rejected by the server: {serverText}" + (isAws ? string.Empty : " If uploads fail on an S3-compatible server, turn off \"flexible checksums\" in the connection's advanced settings."));
        }

        return status switch
        {
            401 => Make(StorageErrorKind.InvalidCredentials, $"{operation} failed: the server rejected the credentials."),
            403 => Make(StorageErrorKind.AccessDenied, $"{operation} was refused: these credentials are not allowed to do this."),
            404 => Make(StorageErrorKind.NotFound, $"{operation} failed: not found."),
            301 or 307 or 400 when serverText.Contains("region", StringComparison.OrdinalIgnoreCase) || serverText.Contains("endpoint", StringComparison.OrdinalIgnoreCase)
                => Make(StorageErrorKind.RegionOrConfiguration, $"{operation} failed: region or endpoint mismatch. {serverText}"),
            301 or 307 => Make(StorageErrorKind.RegionOrConfiguration, $"{operation} failed: the server redirected the request (region or addressing style mismatch)."),
            405 or 501 => Make(StorageErrorKind.NotSupported, $"{operation} is not supported by this server."),
            409 => Make(StorageErrorKind.Conflict, $"{operation} failed: conflict. {serverText}"),
            429 or 503 => Make(StorageErrorKind.Throttled, $"{operation} was throttled or the service is temporarily unavailable. Try again later."),
            >= 500 => Make(StorageErrorKind.ServerError, $"{operation} failed: the server reported an internal error. Try again later."),
            >= 400 => Make(StorageErrorKind.InvalidRequest, $"{operation} was rejected by the server: {serverText}"),
            _ => Make(StorageErrorKind.Unknown, $"{operation} failed: {serverText}"),
        };
    }

    /// <summary>The region the server says the bucket lives in (redirect responses carry it).</summary>
    public static string? RegionHint(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is AmazonS3Exception s3 && s3.ResponseBody is { } body)
            {
                var start = body.IndexOf("<Region>", StringComparison.Ordinal);
                var end = body.IndexOf("</Region>", StringComparison.Ordinal);
                if (start >= 0 && end > start)
                {
                    return body[(start + 8)..end];
                }
            }
        }

        return null;
    }

    public static bool IsRegionMismatch(Exception exception) =>
        exception is AmazonServiceException { ErrorCode: "PermanentRedirect" or "AuthorizationHeaderMalformed" or "IllegalLocationConstraintException" }
        || exception is AmazonServiceException { StatusCode: HttpStatusCode.MovedPermanently };

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is T match)
            {
                return match;
            }

            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Find<T>(inner) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }
}
