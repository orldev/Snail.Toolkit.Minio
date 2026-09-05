using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Ports;

/// <summary>
/// Stores and reads objects, in terms this library defines rather than any vendor's.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this contract names a type from a storage SDK. That is the point: an implementation can be
/// swapped, or a broken SDK worked around, without a consumer changing a line.
/// </para>
/// <para>
/// Every method reports failure through <see cref="StorageResult"/>. The single exception is cancellation:
/// a cancelled <see cref="CancellationToken"/> raises <see cref="OperationCanceledException"/>, because a
/// caller who cancelled is not asking to be told about it as data.
/// </para>
/// </remarks>
public interface IObjectStorage
{
    /// <summary>Uploads an object.</summary>
    /// <param name="bucket">The bucket to write into.</param>
    /// <param name="content">The bytes to store, read from the current position.</param>
    /// <param name="options">What to name it and how to describe it.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>What was stored, or why it could not be.</returns>
    Task<StorageResult<StoredObject>> PutAsync(
        string bucket,
        Stream content,
        UploadOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens an object for reading.</summary>
    /// <param name="bucket">The bucket to read from.</param>
    /// <param name="name">The object to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The object's bytes and metadata, or why they could not be read.</returns>
    /// <remarks>
    /// The caller owns the returned <see cref="ObjectContent"/> and must dispose it; the connection stays
    /// open until then.
    /// </remarks>
    Task<StorageResult<ObjectContent>> GetAsync(
        string bucket,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Copies an object into a stream of the caller's.</summary>
    /// <param name="bucket">The bucket to read from.</param>
    /// <param name="name">The object to read.</param>
    /// <param name="destination">Where to copy the bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What was read, or why it could not be.</returns>
    /// <remarks>
    /// A failure part-way through leaves <paramref name="destination"/> as it was found, provided it can
    /// seek: the copy is rewound so a half-written file is never mistaken for a whole one.
    /// </remarks>
    Task<StorageResult<StoredObject>> DownloadToAsync(
        string bucket,
        string name,
        Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>Copies part of an object into a stream of the caller's.</summary>
    /// <param name="bucket">The bucket to read from.</param>
    /// <param name="name">The object to read.</param>
    /// <param name="destination">Where to copy the bytes.</param>
    /// <param name="offset">The first byte to read, counted from the start of the object.</param>
    /// <param name="length">How many bytes to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What was read, or why it could not be.</returns>
    /// <remarks>
    /// Offsets are 64-bit, so ranges past 2 GB are addressable. This is the call to reach for when serving
    /// video or reading a header out of a large file.
    /// </remarks>
    Task<StorageResult<StoredObject>> DownloadRangeToAsync(
        string bucket,
        string name,
        Stream destination,
        long offset,
        long length,
        CancellationToken cancellationToken = default);

    /// <summary>Reads an object's metadata without reading its bytes.</summary>
    /// <param name="bucket">The bucket to look in.</param>
    /// <param name="name">The object to describe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the store knows about the object, or why it could not say.</returns>
    Task<StorageResult<StoredObject>> StatAsync(
        string bucket,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an object.</summary>
    /// <param name="bucket">The bucket to remove from.</param>
    /// <param name="name">The object to remove.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Success, or why the object could not be removed.</returns>
    /// <remarks>
    /// Removing an object that is not there succeeds: the caller asked for it to be gone, and it is.
    /// </remarks>
    Task<StorageResult> RemoveAsync(
        string bucket,
        string name,
        CancellationToken cancellationToken = default);
}
