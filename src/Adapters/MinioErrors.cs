using System.Net;
using System.Text;
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
    /// <summary>How much of a failure's body is read before looking for the code in it.</summary>
    /// <remarks>
    /// A server that is failing can answer with a body of any size, and reading it whole to find a
    /// six-character code is how it takes the client down with it. Every S3 error document worth parsing
    /// opens with its code well inside this.
    /// </remarks>
    private const int BodyLimit = 8 * 1024;

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
        if (exception is MinioException reported && Reported(reported) is { } detailed)
            return detailed;

        var kind = exception switch
        {
            AuthorizationException => StorageErrorKind.Authorization,
            AccessDeniedException => StorageErrorKind.AccessDenied,
            InvalidBucketNameException => StorageErrorKind.InvalidBucketName,
            InvalidObjectNameException => StorageErrorKind.InvalidObjectName,
            BucketNotFoundException => StorageErrorKind.BucketNotFound,
            ObjectNotFoundException => StorageErrorKind.ObjectNotFound,
            ConnectionException => StorageErrorKind.Connection,
            InternalServerException => StorageErrorKind.Upstream,
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
            StatusCode = Status(exception)
        };
    }

    /// <summary>
    /// Reads the status out of an exception that carries one by name.
    /// </summary>
    /// <param name="exception">The exception to read.</param>
    /// <returns>The status, or <see langword="null"/> when nothing said one.</returns>
    /// <remarks>
    /// <c>InternalServerException</c> is the SDK saying 500 without saying 500. Naming it makes the same
    /// failure retryable whether it arrived through the SDK or over the read path.
    /// </remarks>
    private static int? Status(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => (int)status,
        InternalServerException => 500,
        _ => null
    };

    /// <summary>
    /// Reads a failure the SDK already parsed, when it parsed one.
    /// </summary>
    /// <param name="exception">The exception the SDK threw.</param>
    /// <returns>The error, or <see langword="null"/> when the SDK never reached the server.</returns>
    /// <remarks>
    /// <para>
    /// The SDK keeps the answer on the exception: the status on <c>ServerResponse</c> and the S3 code on
    /// <c>Response</c>. Reading them is what makes a 503 from <c>StatAsync</c> as retryable as the same 503
    /// from a read, which it was not while every SDK failure arrived without a status.
    /// </para>
    /// <para>
    /// Measured: for a 503 the SDK fills in the code and leaves <c>ServerResponse</c> null, so this path
    /// classifies by code and no <c>Retry-After</c> is available. The read path, which issues its own
    /// request, sees both.
    /// </para>
    /// </remarks>
    private static StorageError? Reported(MinioException exception)
    {
        int? status = exception.ServerResponse?.StatusCode is { } reported and not 0 ? (int)reported : null;
        var code = string.IsNullOrWhiteSpace(exception.Response?.Code) ? null : exception.Response.Code;

        if (status is null && code is null)
            return null;

        return new StorageError(Classify(code, status), exception.Message)
        {
            StatusCode = status,
            Code = code,
            Cause = exception,
            RetryAfter = Wait(exception.ServerResponse?.Headers)
        };
    }

    /// <summary>
    /// Reads how long a server asked the caller to wait, out of raw headers.
    /// </summary>
    /// <param name="headers">The headers the SDK collected, when it collected any.</param>
    /// <returns>The wait, or <see langword="null"/> when none was asked for.</returns>
    private static TimeSpan? Wait(IDictionary<string, string>? headers)
    {
        if (headers is null)
            return null;

        var asked = headers
            .Where(header => string.Equals(header.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
            .Select(header => header.Value)
            .FirstOrDefault();

        if (asked is null)
            return null;

        if (int.TryParse(asked, out var seconds))
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

        if (DateTimeOffset.TryParse(asked, out var date))
        {
            var wait = date - DateTimeOffset.UtcNow;

            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
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
        var kind = Classify(code, status);

        return new StorageError(kind, $"The server answered {status} {response.ReasonPhrase}.")
        {
            StatusCode = status,
            Code = code,
            RetryAfter = Wait(response)
        };
    }

    /// <summary>
    /// Reads how long the server asked the caller to wait.
    /// </summary>
    /// <param name="response">The unsuccessful response.</param>
    /// <returns>The wait, or <see langword="null"/> when the server asked for none.</returns>
    /// <remarks>
    /// The header comes in two shapes, a number of seconds or a date. A date in the past is a wait of
    /// nothing rather than a negative one.
    /// </remarks>
    private static TimeSpan? Wait(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter?.Delta is { } delta)
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;

            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Classifies a failure the server described, however it reached this library.
    /// </summary>
    /// <param name="code">The S3 error code, when the server sent one.</param>
    /// <param name="status">The HTTP status, when there was one.</param>
    /// <returns>The kind a caller branches on.</returns>
    /// <remarks>
    /// One table for both paths. The code decides where it says something the status cannot — a missing
    /// bucket and a missing object are both 404 — and the status decides the rest.
    /// </remarks>
    private static StorageErrorKind Classify(string? code, int? status) => code switch
    {
        "NoSuchBucket" => StorageErrorKind.BucketNotFound,
        "NoSuchKey" => StorageErrorKind.ObjectNotFound,
        "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" => StorageErrorKind.AccessDenied,
        "InvalidBucketName" => StorageErrorKind.InvalidBucketName,
        "InvalidObjectName" or "KeyTooLongError" => StorageErrorKind.InvalidObjectName,
        "InvalidRange" or "InvalidArgument" => StorageErrorKind.InvalidArgument,
        _ => FromStatus(status)
    };

    /// <summary>
    /// Classifies a failure that carried no S3 error code.
    /// </summary>
    /// <param name="status">The status the server answered with, when there was one.</param>
    /// <returns>The error kind the status alone implies.</returns>
    private static StorageErrorKind FromStatus(int? status) => status switch
    {
        401 => StorageErrorKind.Authorization,
        403 => StorageErrorKind.AccessDenied,
        404 => StorageErrorKind.ObjectNotFound,
        408 or 504 => StorageErrorKind.Timeout,
        400 or 416 => StorageErrorKind.InvalidArgument,
        _ => StorageErrorKind.Upstream
    };

    /// <summary>
    /// Reads the <c>Code</c> element out of an S3 error document.
    /// </summary>
    /// <param name="response">The unsuccessful response.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>The code, or <see langword="null"/> when the body carries none within the first bytes.</returns>
    private static async Task<string?> CodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[BodyLimit];

                var read = await stream
                    .ReadAtLeastAsync(buffer, BodyLimit, throwOnEndOfStream: false, cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                    return null;

                return XDocument.Parse(Encoding.UTF8.GetString(buffer, 0, read)).Root?.Element("Code")?.Value;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
