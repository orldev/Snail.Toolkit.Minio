using System.Net;
using System.Xml.Linq;
using Minio.Exceptions;
using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Translates what Minio and the wire report into a <see cref="StorageError"/>.
/// </summary>
/// <remarks>
/// One place for both directions of the adapter — the SDK's exceptions and the responses to the requests
/// this library signs itself — so the same failure produces the same
/// <see cref="StorageErrorKind"/> whichever path it arrived on.
/// </remarks>
internal static class MinioErrors
{
    /// <summary>
    /// Classifies an exception thrown by the Minio client.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns>The error a caller sees.</returns>
    /// <remarks>
    /// Order matters: the specific <see cref="MinioException"/> subclasses are matched before the
    /// catch-all, which is matched before the general fallback. An
    /// <see cref="OperationCanceledException"/> reaching here is the client's own request timeout, because
    /// cancellation asked for by the caller is rethrown before any translation happens.
    /// </remarks>
    internal static StorageError From(Exception exception)
    {
        var kind = exception switch
        {
            AuthorizationException => StorageErrorKind.Authorization,
            AccessDeniedException => StorageErrorKind.AccessDenied,
            InvalidBucketNameException => StorageErrorKind.InvalidBucketName,
            InvalidObjectNameException => StorageErrorKind.InvalidObjectName,
            BucketNotFoundException => StorageErrorKind.BucketNotFound,
            ObjectNotFoundException => StorageErrorKind.ObjectNotFound,
            ConnectionException => StorageErrorKind.Connection,
            MinioException => StorageErrorKind.Upstream,
            HttpRequestException => StorageErrorKind.Connection,
            TimeoutException or OperationCanceledException => StorageErrorKind.Timeout,
            FileNotFoundException => StorageErrorKind.InvalidArgument,
            ObjectDisposedException => StorageErrorKind.ObjectDisposed,
            NotImplementedException or NotSupportedException => StorageErrorKind.NotSupported,
            ArgumentException => StorageErrorKind.InvalidArgument,
            InvalidOperationException => StorageErrorKind.InvalidArgument,
            _ => StorageErrorKind.Unexpected
        };

        return new StorageError(kind, exception.Message)
        {
            Cause = exception,
            StatusCode = (exception as HttpRequestException)?.StatusCode is { } status ? (int)status : null
        };
    }

    /// <summary>
    /// Reads the failure out of a response to a request this library signed.
    /// </summary>
    /// <param name="response">The unsuccessful response.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>The error a caller sees, carrying the status and the S3 code.</returns>
    /// <remarks>
    /// <para>
    /// S3 distinguishes a missing bucket from a missing object only in the body of a 404, so the body is
    /// what decides. A body that cannot be parsed is not an error of its own: the status alone still
    /// classifies the failure.
    /// </para>
    /// <para>
    /// Rejected credentials answer 403 and land on <see cref="StorageErrorKind.AccessDenied"/> whichever
    /// path they arrived on, because the SDK cannot tell a bad signature from a forbidden object and a
    /// contract whose error kind depends on which method was called is worth nothing.
    /// <see cref="StorageError.Code"/> keeps the distinction for anyone who needs it.
    /// </para>
    /// </remarks>
    internal static async Task<StorageError> FromAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var code = await CodeAsync(response, cancellationToken).ConfigureAwait(false);

        var kind = code switch
        {
            "NoSuchBucket" => StorageErrorKind.BucketNotFound,
            "NoSuchKey" => StorageErrorKind.ObjectNotFound,
            "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" => StorageErrorKind.AccessDenied,
            "InvalidBucketName" => StorageErrorKind.InvalidBucketName,
            "InvalidObjectName" or "KeyTooLongError" => StorageErrorKind.InvalidObjectName,
            "InvalidRange" or "InvalidArgument" => StorageErrorKind.InvalidArgument,
            _ => FromStatus(response.StatusCode)
        };

        return new StorageError(kind, $"The server answered {status} {response.ReasonPhrase}.")
        {
            StatusCode = status,
            Code = code
        };
    }

    /// <summary>
    /// Classifies a response that carried no S3 error code.
    /// </summary>
    /// <param name="status">The status the server answered with.</param>
    /// <returns>The error kind the status alone implies.</returns>
    private static StorageErrorKind FromStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => StorageErrorKind.Authorization,
        HttpStatusCode.Forbidden => StorageErrorKind.AccessDenied,
        HttpStatusCode.NotFound => StorageErrorKind.ObjectNotFound,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => StorageErrorKind.Timeout,
        HttpStatusCode.BadRequest or HttpStatusCode.RequestedRangeNotSatisfiable => StorageErrorKind.InvalidArgument,
        _ => StorageErrorKind.Upstream
    };

    /// <summary>
    /// Reads the <c>Code</c> element out of an S3 error document.
    /// </summary>
    /// <param name="response">The unsuccessful response.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>The code, or <see langword="null"/> when the body carries none.</returns>
    private static async Task<string?> CodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
                return null;

            return XDocument.Parse(body).Root?.Element("Code")?.Value;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
