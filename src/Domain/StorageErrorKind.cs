namespace Snail.Toolkit.Minio.Domain;

/// <summary>
/// The kinds of failure an object store can report.
/// </summary>
/// <remarks>
/// A kind is what a caller branches on. The wire details that vary between servers — the HTTP status and
/// the S3 error code — travel alongside it on <see cref="StorageError"/> rather than multiplying this list.
/// </remarks>
public enum StorageErrorKind
{
    /// <summary>The credentials were rejected.</summary>
    Authorization,

    /// <summary>The credentials were accepted but the operation is not permitted.</summary>
    AccessDenied,

    /// <summary>The bucket does not exist.</summary>
    BucketNotFound,

    /// <summary>The object does not exist in the bucket.</summary>
    ObjectNotFound,

    /// <summary>The bucket name does not satisfy the naming rules of the server.</summary>
    InvalidBucketName,

    /// <summary>The object name is empty or carries characters the server rejects.</summary>
    InvalidObjectName,

    /// <summary>The server could not be reached.</summary>
    Connection,

    /// <summary>The request outlived the configured timeout.</summary>
    /// <remarks>
    /// Cancellation requested by the caller is not reported here: it propagates as
    /// <see cref="OperationCanceledException"/>, which is what the TPL and ASP.NET Core expect.
    /// </remarks>
    Timeout,

    /// <summary>An argument the caller supplied cannot be used.</summary>
    InvalidArgument,

    /// <summary>The requested operation is not supported for the supplied arguments.</summary>
    NotSupported,

    /// <summary>A stream handed to the operation was already disposed.</summary>
    ObjectDisposed,

    /// <summary>The server answered, and the answer was a failure this library does not classify further.</summary>
    /// <remarks>
    /// <see cref="StorageError.StatusCode"/> and <see cref="StorageError.Code"/> carry what the server said.
    /// </remarks>
    Upstream,

    /// <summary>Something failed that is not specific to object storage at all.</summary>
    Unexpected
}
