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
public static class MinioExtensions
{
    /// <summary>
    /// Uploads an object to a bucket with simplified parameters. The maximum size of a single object is limited to 5TB.
    /// PutObject transparently uploads objects larger than 5MiB in multiple parts. Uploaded data is carefully verified using MD5SUM signatures.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket. Defaults to "default" if not specified.</param>
    /// <param name="args">Optional action to configure PutObjectArgs for additional parameters like object name, stream data, object size, etc.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{PutObjectResponse}"/> indicating success or failure with detailed error information.</returns>
    /// <remarks>
    /// This method provides a more structured approach to error handling compared to the original Minio client methods.
    /// Instead of throwing exceptions, it returns a <see cref="MinioResult{T}"/> that can be pattern-matched for different error scenarios.
    /// </remarks>
    public static async Task<MinioResult<PutObjectResponse>> PutObjectAsync(
        this IMinioClient client,
        string bucketName = "default", 
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
        catch (Exception e)
        {
            var errorType = e switch
            {
                AuthorizationException => MinioErrorType.Authorization,
                InvalidBucketNameException => MinioErrorType.InvalidBucketName,
                InvalidObjectNameException => MinioErrorType.InvalidObjectName,
                BucketNotFoundException => MinioErrorType.BucketNotFound,
                ObjectNotFoundException => MinioErrorType.ObjectNotFound,
                MinioException => MinioErrorType.UnknownMinioError,
                _ => MinioErrorType.UnexpectedError
            };
    
            return MinioResult<PutObjectResponse>.Failure(errorType, e.Message);
        }
    }

    /// <summary>
    /// Uploads a stream as an object to a bucket with automatic file extension mapping based on content type.
    /// The maximum size of a single object is limited to 5TB. PutObject transparently uploads objects larger 
    /// than 5MiB in multiple parts. Uploaded data is carefully verified using MD5SUM signatures.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="stream">The stream containing the data to upload.</param>
    /// <param name="contentType">Content-Type of the uploaded file.</param>
    /// <param name="objectName">Optional name of the object. If not provided, a GUID will be generated.</param>
    /// <param name="mimeTypeMap">Whether to automatically append file extension based on content type. Defaults to true.</param>
    /// <param name="args">Optional action to configure additional PutObjectArgs parameters.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{PutObjectResponse}"/> indicating success or failure with detailed error information.</returns>
    /// <remarks>
    /// <para>
    /// When <paramref name="mimeTypeMap"/> is true, the method automatically appends the appropriate file extension
    /// to the <paramref name="objectName"/> based on the provided <paramref name="contentType"/>.
    /// </para>
    /// <para>
    /// If no <paramref name="objectName"/> is provided, a GUID will be generated as the object name.
    /// </para>
    /// </remarks>
    public static async Task<MinioResult<PutObjectResponse>> PutStreamAsync(
        this IMinioClient client,
        string bucketName,
        Stream stream,
        string contentType,
        string? objectName = null,
        bool mimeTypeMap = true,
        Action<PutObjectArgs>? args = null,
        CancellationToken cancellationToken = default)
    {
        objectName ??= Guid.NewGuid().ToString("N");
        
        if (mimeTypeMap)
        {
            var mimeType = MimeTypeMap.GetExtension(contentType, false);
            objectName += mimeType;
        }
        
        return await client.PutObjectAsync(bucketName, (e => e
                .WithObject(objectName)
                .WithContentType(contentType)
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)) + args,
            cancellationToken);
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
            
            await client.RemoveObjectAsync(removeObjectArgs, cancellationToken);
            return MinioResult.Success();
        }
        catch (Exception e)
        {
            var errorType = e switch
            {
                AuthorizationException => MinioErrorType.Authorization,
                InvalidBucketNameException => MinioErrorType.InvalidBucketName,
                InvalidObjectNameException => MinioErrorType.InvalidObjectName,
                BucketNotFoundException => MinioErrorType.BucketNotFound,
                ObjectNotFoundException => MinioErrorType.ObjectNotFound,
                NotImplementedException => MinioErrorType.NotImplemented,
                MinioException => MinioErrorType.UnknownMinioError,
                _ => MinioErrorType.UnexpectedError
            };
    
            return MinioResult.Failure(errorType, e.Message);
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
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{ObjectStat}"/> with the object metadata if successful, or error information if the operation failed.</returns>
    /// <remarks>
    /// This method retrieves both the object data and metadata. The object data can be accessed through the callback stream
    /// in the <paramref name="args"/> parameter. For simpler download scenarios, consider using <see cref="DownloadObjectAsync(IMinioClient, string, string, Stream, Action{GetObjectArgs}?, CancellationToken)"/>.
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
        catch (Exception e)
        {
            var errorType = e switch
            {
                AuthorizationException => MinioErrorType.Authorization,
                InvalidBucketNameException => MinioErrorType.InvalidBucketName,
                InvalidObjectNameException => MinioErrorType.InvalidObjectName,
                BucketNotFoundException => MinioErrorType.BucketNotFound,
                ObjectNotFoundException => MinioErrorType.ObjectNotFound,
                FileNotFoundException => MinioErrorType.FileNotFound,
                ObjectDisposedException => MinioErrorType.ObjectDisposed,
                NotSupportedException => MinioErrorType.NotSupported,
                InvalidOperationException => MinioErrorType.InvalidOperation,
                AccessDeniedException => MinioErrorType.AccessDenied,
                MinioException => MinioErrorType.UnknownMinioError,
                _ => MinioErrorType.UnexpectedError
            };
    
            return MinioResult<ObjectStat>.Failure(errorType, e.Message);
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
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{ObjectStat}"/> with the object metadata if successful, or error information if the operation failed.</returns>
    /// <remarks>
    /// <para>
    /// The method automatically copies the object data to the provided <paramref name="destination"/> stream.
    /// The destination stream should be writable and properly disposed by the caller.
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
        return await client.GetObjectAsync(
            bucketName,
            objectName,
            args + (e => e
                .WithCallbackStream(async (stream, token) =>
                {
                    await using (stream)
                    {
                        await stream.CopyToAsync(destination, token).ConfigureAwait(false);    
                    }
                })),
            cancellationToken);
    }
    
    
    /// <summary>
    /// Downloads an object from a bucket and returns it as a MemoryStream.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectName">Name of the object.</param>
    /// <param name="args">Optional action to configure GetObjectArgs for additional parameters.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{Stream}"/> with the object data as a MemoryStream if successful, or error information if the operation failed.</returns>
    /// <remarks>
    /// <para>
    /// This method is convenient for small to medium-sized objects that can comfortably fit in memory.
    /// For large objects, consider using <see cref="DownloadObjectAsync(IMinioClient, string, string, Stream, Action{GetObjectArgs}?, CancellationToken)"/>
    /// with a file stream or other persistent storage.
    /// </para>
    /// <para>
    /// The returned MemoryStream is positioned at the beginning and should be disposed by the caller.
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
        
        var objectStat = await client.GetObjectAsync(
            bucketName,
            objectName,
            args + (e => e
                .WithCallbackStream(async (stream, token) =>
                {
                    await using (stream)
                    {
                        await stream.CopyToAsync(memoryStream, token).ConfigureAwait(false);    
                    }
                })),
            cancellationToken);

        if (objectStat.IsSuccess)
        {
            return MinioResult<Stream>.Success(memoryStream);    
        }

        return MinioResult<Stream>.Failure(
            objectStat.ErrorType, 
            objectStat.ErrorMessage ?? "Failed to download object");
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
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{ObjectStat}"/> with the object metadata if successful, or error information if the operation failed.</returns>
    /// <remarks>
    /// This method is useful for reading specific parts of large objects without downloading the entire file,
    /// such as for video streaming or reading specific sections of large documents.
    /// </remarks>
    public static async Task<MinioResult<ObjectStat>> DownloadObjectWithOffsetAndLengthAsync(
        this IMinioClient client,
        string bucketName, 
        string objectName,
        Stream destination,
        int offset,
        int length,
        Action<GetObjectArgs>? args = null,
        CancellationToken cancellationToken = default)
    {
        return await client.DownloadObjectAsync(
            bucketName, 
            objectName, 
            destination, 
            args + (e => e
                .WithOffsetAndLength(offset, length)), 
            cancellationToken);
    }
    
    /// <summary>
    /// Retrieves metadata of an object without returning the object itself.
    /// </summary>
    /// <param name="client">The Minio client instance.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectName">Name of the object.</param>
    /// <param name="args">Optional action to configure StatObjectArgs for additional parameters like server-side encryption.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MinioResult{ObjectStat}"/> with the object metadata if the object exists, or error information if the operation failed.</returns>
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

            var statObject = await client.StatObjectAsync(statObjectArgs, cancellationToken);
            return MinioResult<ObjectStat>.Success(statObject);
        }
        catch (Exception e)
        {
            var errorType = e switch
            {
                BucketNotFoundException => MinioErrorType.BucketNotFound,
                ObjectNotFoundException => MinioErrorType.ObjectNotFound,
                AccessDeniedException => MinioErrorType.AccessDenied,
                ConnectionException => MinioErrorType.Connection,
                InvalidOperationException => MinioErrorType.InvalidOperation,
                ArgumentNullException => MinioErrorType.ArgumentNull,
                TimeoutException => MinioErrorType.Timeout,
                MinioException => MinioErrorType.UnknownMinioError,
                _ => MinioErrorType.UnexpectedError
            };
    
            return MinioResult<ObjectStat>.Failure(errorType, e.Message);
        }
    }
}