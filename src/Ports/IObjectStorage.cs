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
    /// <remarks>
    /// The answer carries no <see cref="StoredObject.LastModified"/>: a write is acknowledged with an
    /// entity tag, not with the moment the store recorded, and inventing one from this machine's clock
    /// would be worse than leaving it unknown. Call <see cref="StatAsync"/> where the moment matters.
    /// </remarks>
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

    /// <summary>Asks whether an object is there.</summary>
    /// <param name="bucket">The bucket to look in.</param>
    /// <param name="name">The object to look for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether it exists, or why the question could not be answered.</returns>
    /// <remarks>
    /// A missing object is an answer of <see langword="false"/>, not a failure. A failure here means the
    /// question itself could not be put to the server.
    /// </remarks>
    Task<StorageResult<bool>> ExistsAsync(
        string bucket,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Lists a page of the objects in a bucket.</summary>
    /// <param name="bucket">The bucket to list.</param>
    /// <param name="options">What to list, and how much of it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A page of objects, or why the bucket could not be listed.</returns>
    /// <remarks>
    /// <para>
    /// For a screen of results. Walking a whole bucket goes through <see cref="EnumerateAsync"/> instead.
    /// </para>
    /// <para>
    /// Resuming from <see cref="ListOptions.Cursor"/> costs a walk to that point: the SDK offers no way to
    /// start a listing after a given name, so the server lists from the beginning and this skips. Fine for
    /// a few pages, wrong for a bucket with a million objects.
    /// </para>
    /// </remarks>
    Task<StorageResult<ObjectPage>> ListAsync(
        string bucket,
        ListOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Walks every object in a bucket, once, forwards.</summary>
    /// <param name="bucket">The bucket to walk.</param>
    /// <param name="options">What to list; the page size and cursor do not apply here.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    /// <returns>Each object in turn, or the failure that ended the walk.</returns>
    /// <remarks>
    /// A walk has nowhere to put a result once it has started yielding, so a failure arrives as the last
    /// item of the sequence rather than as an exception. Read each item, and stop at the first one that
    /// carries an error.
    /// </remarks>
    IAsyncEnumerable<StorageResult<StoredObject>> EnumerateAsync(
        string bucket,
        ListOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Signs a URL that reads an object.</summary>
    /// <param name="bucket">The bucket the object lives in.</param>
    /// <param name="name">The object to read.</param>
    /// <param name="lifetime">How long the URL stays valid; the configured lifetime when omitted.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A URL anyone holding it can read the object with, or why it could not be signed.</returns>
    /// <remarks>
    /// Signing happens locally, without asking the server. The URL carries the credentials' authority for
    /// as long as it lives, so it is handed to a browser and not written to a log.
    /// </remarks>
    Task<StorageResult<Uri>> SignedUrlAsync(
        string bucket,
        string name,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default);

    /// <summary>Signs a URL that writes an object.</summary>
    /// <param name="bucket">The bucket to write into.</param>
    /// <param name="name">The object to write.</param>
    /// <param name="lifetime">How long the URL stays valid; the configured lifetime when omitted.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A URL that accepts a PUT of the object, or why it could not be signed.</returns>
    /// <remarks>
    /// The upload never passes through this application, which is the point: a browser sends the bytes
    /// straight to the store.
    /// </remarks>
    Task<StorageResult<Uri>> SignedUploadUrlAsync(
        string bucket,
        string name,
        TimeSpan? lifetime = null,
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
