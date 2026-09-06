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

    /// <summary>Replaces the rules by which a bucket expires its objects.</summary>
    /// <param name="bucket">The bucket to set them on.</param>
    /// <param name="rules">Every rule the bucket is to have; an empty set clears them.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Success, or why the rules could not be set.</returns>
    /// <remarks>
    /// The whole set, not one rule, because that is what the protocol does: a server given one rule keeps
    /// only that rule. An API shaped as "add this rule" would silently delete every other rule the bucket
    /// had, and the caller would find out when something stopped being cleaned up.
    /// </remarks>
    Task<StorageResult> SetExpiryAsync(
        string bucket,
        IReadOnlyList<ExpiryRule> rules,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the rules by which a bucket expires its objects.</summary>
    /// <param name="bucket">The bucket to read them from.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The rules, or why they could not be read.</returns>
    /// <remarks>
    /// A bucket that expires nothing answers with no rules rather than with a failure: having none is a
    /// state a bucket is allowed to be in. Rules the protocol offers and this library does not model —
    /// transitions between storage classes, versioned objects, incomplete uploads — are not reported.
    /// </remarks>
    Task<StorageResult<IReadOnlyList<ExpiryRule>>> GetExpiryAsync(
        string bucket,
        CancellationToken cancellationToken = default);
}
