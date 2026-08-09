using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.DataModel.Response;
using Minio.Exceptions;
using Toolkit.Minio.Entities;
using Toolkit.Minio.Internal;

namespace Toolkit.Minio.Extensions;

/// <summary>
/// Provides extension methods for <see cref="IMinioClient"/> that return <see cref="MinioResult{T}"/> and <see cref="MinioResult"/>
/// for better error handling and more structured responses.
/// </summary>
/// <remarks>
/// <para>
/// Every method in this class translates the exceptions thrown by the underlying Minio client into a
/// <see cref="MinioErrorType"/> using a single shared mapping, so the same failure always produces the
/// same <see cref="MinioErrorType"/> regardless of which operation was called.
/// </para>
/// <para>
/// Cancellation is deliberately <i>not</i> converted into a result. When the supplied
/// <see cref="CancellationToken"/> is cancelled the underlying <see cref="OperationCanceledException"/>
/// propagates to the caller, matching the behaviour expected by the TPL and ASP.NET Core.
/// </para>
/// </remarks>
public static class MinioExtensions
{
    /// <summary>
    /// Translates an exception thrown by the Minio client into a <see cref="MinioErrorType"/>.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns>The <see cref="MinioErrorType"/> that best describes <paramref name="exception"/>.</returns>
    /// <remarks>
    /// The order of the patterns matters: the more specific <see cref="MinioException"/> subclasses are
    /// matched before the <see cref="MinioException"/> catch-all, which is itself matched before the
    /// generic fallback.
    /// </remarks>
    private static MinioErrorType ToErrorType(this Exception exception) => exception switch
    {
        AuthorizationException => MinioErrorType.Authorization,
        InvalidBucketNameException => MinioErrorType.InvalidBucketName,
        InvalidObjectNameException => MinioErrorType.InvalidObjectName,
        BucketNotFoundException => MinioErrorType.BucketNotFound,
        ObjectNotFoundException => MinioErrorType.ObjectNotFound,
        AccessDeniedException => MinioErrorType.AccessDenied,
        ConnectionException => MinioErrorType.Connection,
        MinioException => MinioErrorType.UnknownMinioError,
        FileNotFoundException => MinioErrorType.FileNotFound,
        ObjectDisposedException => MinioErrorType.ObjectDisposed,
        NotImplementedException => MinioErrorType.NotImplemented,
        NotSupportedException => MinioErrorType.NotSupported,
        ArgumentNullException => MinioErrorType.ArgumentNull,
        InvalidOperationException => MinioErrorType.InvalidOperation,
        TimeoutException => MinioErrorType.Timeout,
        _ => MinioErrorType.UnexpectedError
    };

    /// <summary>
    /// Uploads an object to a bucket. The maximum size of a single object is limited to 5TB.
    /// PutObject transparently uploads objects larger than 5MiB in multiple parts. Uploaded data is carefully verified using MD5SUM signatures.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="args">Optional action to configure PutObjectArgs for additional parameters like object name, stream data, object size, etc.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{T}"/> indicating success or failure with detailed error information.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <remarks>
    /// This method provides a more structured approach to error handling compared to the original Minio client methods.
    /// Instead of throwing exceptions, it returns a <see cref="MinioResult{T}"/> that can be pattern-matched for different error scenarios.
    /// </remarks>
    public static async Task<MinioResult<PutObjectResponse>> PutObjectAsync(
        this IMinioClient client,
        string bucketName,
        Action<PutObjectArgs>? args = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var putObjectArgs = new PutObjectArgs()
                .WithBucket(bucketName);

            args?.Invoke(putObjectArgs);
            
            var response = await client.PutObjectAsync(putObjectArgs, cancellationToken);
            return MinioResult<PutObjectResponse>.Success(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return MinioResult<PutObjectResponse>.Failure(e.ToErrorType(), e.Message);
        }
    }

    /// <summary>
    /// Uploads a stream as an object to a bucket, optionally deriving the file extension from the content type.
    /// The maximum size of a single object is limited to 5TB. PutObject transparently uploads objects larger
    /// than 5MiB in multiple parts. Uploaded data is carefully verified using MD5SUM signatures.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="stream">The stream containing the data to upload.</param>
    /// <param name="contentType">Content-Type of the uploaded file.</param>
    /// <param name="objectName">Optional name of the object. If not provided, a GUID will be generated.</param>
    /// <param name="objectSize">
    /// Number of bytes to upload. When <see langword="null"/> the size is taken from the stream, which requires
    /// <paramref name="stream"/> to be seekable. Pass an explicit value for non-seekable streams such as an
    /// HTTP request body.
    /// </param>
    /// <param name="appendExtension">
    /// Whether to append a file extension derived from <paramref name="contentType"/> to the object name.
    /// Defaults to <see langword="false"/>.
    /// </param>
    /// <param name="args">Optional action to configure additional PutObjectArgs parameters.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{T}"/> indicating success or failure with detailed error information.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <remarks>
    /// <para>
    /// When <paramref name="appendExtension"/> is <see langword="true"/> an extension is appended only if
    /// <paramref name="objectName"/> does not already end with the extension that maps to
    /// <paramref name="contentType"/>, so <c>report.txt</c> stays <c>report.txt</c> instead of becoming
    /// <c>report.txt.txt</c>. Unknown content types contribute no extension.
    /// </para>
    /// <para>
    /// If no <paramref name="objectName"/> is provided, a GUID will be generated as the object name.
    /// </para>
    /// <para>
    /// When the size is taken from <paramref name="stream"/>, the current position is accounted for, so a
    /// partially consumed stream reports the number of bytes that actually remain.
    /// </para>
    /// <para>
    /// <paramref name="args"/> is applied last and can therefore override any value set by this method.
    /// </para>
    /// </remarks>
    public static async Task<MinioResult<PutObjectResponse>> PutStreamAsync(
        this IMinioClient client,
        string bucketName,
        Stream stream,
        string contentType,
        string? objectName = null,
        long? objectSize = null,
        bool appendExtension = false,
        Action<PutObjectArgs>? args = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        objectName ??= Guid.NewGuid().ToString("N");

        if (appendExtension)
            objectName += ResolveExtension(objectName, contentType);

        if (objectSize is null)
        {
            if (!stream.CanSeek)
                return MinioResult<PutObjectResponse>.Failure(
                    MinioErrorType.NotSupported,
                    $"Cannot determine the size of a non-seekable stream. Pass '{nameof(objectSize)}' explicitly.");

            objectSize = stream.Length - stream.Position;
        }

        return await client.PutObjectAsync(
            bucketName,
            e =>
            {
                e.WithObject(objectName)
                    .WithContentType(contentType)
                    .WithStreamData(stream)
                    .WithObjectSize(objectSize.Value);

                args?.Invoke(e);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the extension that should be appended to <paramref name="objectName"/> for the given content type.
    /// </summary>
    /// <param name="objectName">The object name the extension would be appended to.</param>
    /// <param name="contentType">The content type to map to an extension.</param>
    /// <returns>
    /// The mapped extension, or <see cref="string.Empty"/> when the content type is unknown or
    /// <paramref name="objectName"/> already carries that extension.
    /// </returns>
    private static string ResolveExtension(string objectName, string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return string.Empty;

        var extension = MimeTypeMap.GetExtension(contentType, false);

        if (extension.Length == 0 || objectName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return extension;
    }

    /// <summary>
    /// Removes an object from a bucket.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectName">Name of the object to remove.</param>
    /// <param name="args">Optional action to configure RemoveObjectArgs for additional parameters like versioning.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous remove operation. The task result contains a <see cref="MinioResult"/> indicating success or failure with detailed error information.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <remarks>
    /// This method returns a <see cref="MinioResult"/> without a value type since remove operations don't return data.
    /// Use the <see cref="MinioResult.IsSuccess"/> property to check if the operation was successful.
    /// </remarks>
    public static async Task<MinioResult> RemoveObjectAsync(
        this IMinioClient client,
        string bucketName,
        string objectName,
        Action<RemoveObjectArgs>? args = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var removeObjectArgs = new RemoveObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectName);

            args?.Invoke(removeObjectArgs);

            await client.RemoveObjectAsync(removeObjectArgs, cancellationToken).ConfigureAwait(false);
            return MinioResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return MinioResult.Failure(e.ToErrorType(), e.Message);
        }
    }

    /// <summary>
    /// Gets an object from a bucket.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectName">Name of the object.</param>
    /// <param name="args">Optional action to configure GetObjectArgs for additional parameters like version ID, server-side encryption, offset, and length.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{T}"/> with the object metadata if successful, or error information if the operation failed.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <remarks>
    /// The object data is delivered through the callback stream configured on <paramref name="args"/>; this
    /// method itself does not set one. For simpler download scenarios, use
    /// <see cref="DownloadObjectAsync(IMinioClient, string, string, Stream, Action{GetObjectArgs}?, CancellationToken)"/>.
    /// </remarks>
    public static async Task<MinioResult<ObjectStat>> GetObjectAsync(
        this IMinioClient client,
        string bucketName,
        string objectName,
        Action<GetObjectArgs>? args = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var getObjectArgs = new GetObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectName);

            args?.Invoke(getObjectArgs);

            var objectStat = await client.GetObjectAsync(getObjectArgs, cancellationToken).ConfigureAwait(false);
            return MinioResult<ObjectStat>.Success(objectStat);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return MinioResult<ObjectStat>.Failure(e.ToErrorType(), e.Message);
        }
    }

    /// <summary>
    /// Downloads an object from a bucket and copies it to the specified destination stream.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectName">Name of the object.</param>
    /// <param name="destination">The stream to which the contents of the object will be copied.</param>
    /// <param name="args">Optional action to configure GetObjectArgs for additional parameters.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{T}"/> with the object metadata if successful, or error information if the operation failed.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <remarks>
    /// <para>
    /// The method automatically copies the object data to the provided <paramref name="destination"/> stream.
    /// The destination stream should be writable and properly disposed by the caller.
    /// </para>
    /// <para>
    /// The callback stream is owned by this method: <paramref name="args"/> is applied first and any callback
    /// stream it configures is replaced, because the copy to <paramref name="destination"/> is what this
    /// method exists to do. Use <see cref="GetObjectAsync"/> directly to supply your own callback.
    /// </para>
    /// <para>
    /// The returned <see cref="ObjectStat"/> contains metadata about the downloaded object, such as size, content type, and ETag.
    /// </para>
    /// </remarks>
    public static async Task<MinioResult<ObjectStat>> DownloadObjectAsync(
        this IMinioClient client,
        string bucketName,
        string objectName,
        Stream destination,
        Action<GetObjectArgs>? args = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        return await client.GetObjectAsync(
            bucketName,
            objectName,
            e =>
            {
                args?.Invoke(e);

                e.WithCallbackStream(async (stream, token) =>
                {
                    await using (stream)
                    {
                        await stream.CopyToAsync(destination, token).ConfigureAwait(false);
                    }
                });
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads an object from a bucket and returns it as a <see cref="MemoryStream"/>.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectName">Name of the object.</param>
    /// <param name="args">Optional action to configure GetObjectArgs for additional parameters.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{T}"/> with the object data as a <see cref="MemoryStream"/> if successful, or error information if the operation failed.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <remarks>
    /// <para>
    /// This method is convenient for small to medium-sized objects that can comfortably fit in memory.
    /// For large objects, consider using <see cref="DownloadObjectAsync(IMinioClient, string, string, Stream, Action{GetObjectArgs}?, CancellationToken)"/>
    /// with a file stream or other persistent storage.
    /// </para>
    /// <para>
    /// On success the returned stream is rewound to position 0 and ready to read; the caller owns it and is
    /// responsible for disposing it. On failure nothing is allocated for the caller to dispose.
    /// </para>
    /// </remarks>
    public static async Task<MinioResult<Stream>> DownloadObjectAsync(
        this IMinioClient client,
        string bucketName,
        string objectName,
        Action<GetObjectArgs>? args = null,
        CancellationToken cancellationToken = default)
    {
        var memoryStream = new MemoryStream();

        try
        {
            var objectStat = await client.DownloadObjectAsync(
                bucketName,
                objectName,
                memoryStream,
                args,
                cancellationToken).ConfigureAwait(false);

            if (!objectStat.IsSuccess)
            {
                await memoryStream.DisposeAsync().ConfigureAwait(false);

                return MinioResult<Stream>.Failure(
                    objectStat.ErrorType,
                    objectStat.ErrorMessage ?? "Failed to download object");
            }

            memoryStream.Position = 0;
            return MinioResult<Stream>.Success(memoryStream);
        }
        catch
        {
            await memoryStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Downloads a specific portion of an object defined by offset and length, and copies it to the specified destination stream.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectName">Name of the object.</param>
    /// <param name="destination">The stream to which the contents of the object will be copied.</param>
    /// <param name="offset">The offset from the start of the object from which to begin reading.</param>
    /// <param name="length">The number of bytes to read from the object.</param>
    /// <param name="args">Optional action to configure GetObjectArgs for additional parameters.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{T}"/> with the object metadata if successful, or error information if the operation failed.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <remarks>
    /// <para>
    /// This method is useful for reading specific parts of large objects without downloading the entire file,
    /// such as for video streaming or reading specific sections of large documents. Offset and length are
    /// 64-bit so ranges beyond 2GB are addressable.
    /// </para>
    /// <para>
    /// <b>Known limitation.</b> Minio 7.0.0 — the current release — reports every HTTP 206 (Partial Content)
    /// response as a <c>PartialContentException</c>, so ranged reads fail against a real server and this method
    /// returns <see cref="MinioErrorType.UnknownMinioError"/>. The defect is upstream, not in this wrapper: the
    /// underlying client fails identically for <c>WithOffsetAndLength</c>, <c>WithLength</c>, a hand-written
    /// <c>Range</c> header, and the file-based download path, while the same request without a range succeeds.
    /// The method is kept so callers are ready when a fixed Minio release ships.
    /// </para>
    /// </remarks>
    public static async Task<MinioResult<ObjectStat>> DownloadObjectWithOffsetAndLengthAsync(
        this IMinioClient client,
        string bucketName,
        string objectName,
        Stream destination,
        long offset,
        long length,
        Action<GetObjectArgs>? args = null,
        CancellationToken cancellationToken = default)
    {
        return await client.DownloadObjectAsync(
            bucketName,
            objectName,
            destination,
            e =>
            {
                args?.Invoke(e);
                e.WithOffsetAndLength(offset, length);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves metadata of an object without returning the object itself.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectName">Name of the object.</param>
    /// <param name="args">Optional action to configure StatObjectArgs for additional parameters like server-side encryption.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{T}"/> with the object metadata if the object exists, or error information if the operation failed.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <remarks>
    /// This method is more efficient than downloading the entire object when you only need metadata
    /// such as size, content type, last modified date, or ETag.
    /// </remarks>
    public static async Task<MinioResult<ObjectStat>> StatObjectAsync(
        this IMinioClient client,
        string bucketName,
        string objectName,
        Action<StatObjectArgs>? args = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var statObjectArgs = new StatObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectName);

            args?.Invoke(statObjectArgs);

            var statObject = await client.StatObjectAsync(statObjectArgs, cancellationToken).ConfigureAwait(false);
            return MinioResult<ObjectStat>.Success(statObject);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return MinioResult<ObjectStat>.Failure(e.ToErrorType(), e.Message);
        }
    }
}
