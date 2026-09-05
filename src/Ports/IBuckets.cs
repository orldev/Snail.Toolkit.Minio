using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Ports;

/// <summary>
/// Creates and removes the containers objects live in.
/// </summary>
/// <remarks>
/// Separate from <see cref="IObjectStorage"/> because it is used at a different time and by different code:
/// buckets are made once, by whatever provisions the application, while objects are written all day.
/// </remarks>
public interface IBuckets
{
    /// <summary>Creates a bucket.</summary>
    /// <param name="bucket">The name to create.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Success, or why the bucket could not be created.</returns>
    /// <remarks>Creating a bucket that already exists succeeds: the caller asked for it to be there.</remarks>
    Task<StorageResult> CreateAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>Removes a bucket.</summary>
    /// <param name="bucket">The name to remove.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Success, or why the bucket could not be removed.</returns>
    /// <remarks>A bucket that still holds objects is not removed; the server refuses and says so.</remarks>
    Task<StorageResult> RemoveAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>Asks whether a bucket is there.</summary>
    /// <param name="bucket">The name to look for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether it exists, or why the question could not be answered.</returns>
    Task<StorageResult<bool>> ExistsAsync(string bucket, CancellationToken cancellationToken = default);
}
