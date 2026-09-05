using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.DataModel.Response;
using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// The Minio SDK's own calls, answering with <see cref="StorageResult"/> instead of exceptions.
/// </summary>
/// <remarks>
/// <para>
/// The vendor-level layer, for code that needs an SDK feature this library does not model. Types from the
/// SDK appear in these signatures on purpose; <see cref="Ports.IObjectStorage"/> is the API that does not.
/// </para>
/// <para>
/// Cancellation is deliberately not turned into a result: a cancelled token raises
/// <see cref="OperationCanceledException"/>, matching what the TPL and ASP.NET Core expect.
/// </para>
/// <para>
/// <b>No retries and no circuit breaker.</b> Those live in <see cref="MinioObjectStorage"/>, and a call
/// made here — or on an <see cref="IMinioClient"/> resolved from the container — goes straight to the
/// server, once. Code that wants the resilience takes <see cref="Ports.IObjectStorage"/> instead.
/// </para>
/// </remarks>
public static class MinioClientExtensions
{
    extension(IMinioClient client)
    {
        /// <summary>Uploads an object.</summary>
        /// <param name="bucket">The bucket to write into.</param>
        /// <param name="args">Configures the request; the bucket is already set.</param>
        /// <param name="cancellationToken">Cancels the upload.</param>
        /// <returns>The server's answer, or why the upload failed.</returns>
        public Task<StorageResult<PutObjectResponse>> PutObjectAsync(
            string bucket,
            Action<PutObjectArgs>? args = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);

            var request = new PutObjectArgs().WithBucket(bucket);
            args?.Invoke(request);

            return ExecuteAsync(() => client.PutObjectAsync(request, cancellationToken), cancellationToken);
        }

        /// <summary>Reads an object's metadata without reading its bytes.</summary>
        /// <param name="bucket">The bucket to look in.</param>
        /// <param name="name">The object to describe.</param>
        /// <param name="args">Configures the request; bucket and object are already set.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The object's metadata, or why it could not be read.</returns>
        public Task<StorageResult<ObjectStat>> StatObjectAsync(
            string bucket,
            string name,
            Action<StatObjectArgs>? args = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);

            var request = new StatObjectArgs().WithBucket(bucket).WithObject(name);
            args?.Invoke(request);

            return ExecuteAsync(() => client.StatObjectAsync(request, cancellationToken), cancellationToken);
        }

        /// <summary>Reads an object through a callback stream of the caller's.</summary>
        /// <param name="bucket">The bucket to read from.</param>
        /// <param name="name">The object to read.</param>
        /// <param name="args">Configures the request, including the callback that receives the bytes.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The object's metadata, or why it could not be read.</returns>
        /// <remarks>
        /// Minio 7.0.0 raises <c>PartialContentException</c> for every HTTP 206 answer, so a request configured
        /// with <c>WithOffsetAndLength</c> fails here and delivers no bytes at all. Ranged reads work through
        /// <see cref="Ports.IObjectStorage.DownloadRangeToAsync"/>, which does not go through this path.
        /// </remarks>
        public Task<StorageResult<ObjectStat>> GetObjectAsync(
            string bucket,
            string name,
            Action<GetObjectArgs>? args = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);

            var request = new GetObjectArgs().WithBucket(bucket).WithObject(name);
            args?.Invoke(request);

            return ExecuteAsync(() => client.GetObjectAsync(request, cancellationToken), cancellationToken);
        }

        /// <summary>Removes an object.</summary>
        /// <param name="bucket">The bucket to remove from.</param>
        /// <param name="name">The object to remove.</param>
        /// <param name="args">Configures the request; bucket and object are already set.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>Success, or why the object could not be removed.</returns>
        public Task<StorageResult> RemoveObjectAsync(
            string bucket,
            string name,
            Action<RemoveObjectArgs>? args = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);

            var request = new RemoveObjectArgs().WithBucket(bucket).WithObject(name);
            args?.Invoke(request);

            return ExecuteAsync(() => client.RemoveObjectAsync(request, cancellationToken), cancellationToken);
        }

    }

    /// <summary>
    /// Runs one SDK call and turns its failure into data.
    /// </summary>
    /// <typeparam name="T">What the call produces.</typeparam>
    /// <param name="operation">The call to run.</param>
    /// <param name="cancellationToken">The token the caller supplied.</param>
    /// <returns>The value, or the translated failure.</returns>
    /// <remarks>
    /// The single copy of the try/catch every call needs. It existed six times before, and the guard that
    /// separates the caller's cancellation from the client's own timeout had to be right in each of them.
    /// </remarks>
    internal static async Task<StorageResult<T>> ExecuteAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
        where T : notnull
    {
        try
        {
            return StorageResult<T>.Success(await operation().ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StorageResult<T>.Failure(MinioErrors.From(exception));
        }
    }

    /// <summary>
    /// Runs one SDK call that produces nothing, and turns its failure into data.
    /// </summary>
    /// <param name="operation">The call to run.</param>
    /// <param name="cancellationToken">The token the caller supplied.</param>
    /// <returns>Success, or the translated failure.</returns>
    internal static async Task<StorageResult> ExecuteAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation().ConfigureAwait(false);

            return StorageResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StorageResult.Failure(MinioErrors.From(exception));
        }
    }
}
