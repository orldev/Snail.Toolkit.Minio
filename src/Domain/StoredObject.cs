using System.Collections.Frozen;

namespace Snail.Toolkit.Minio.Domain;

/// <summary>
/// What the store knows about an object without reading its bytes.
/// </summary>
/// <remarks>
/// Named properties rather than positional parameters: bucket, name, media type and entity tag are all
/// strings, and a positional record would let any two of them be swapped silently.
/// </remarks>
public sealed record StoredObject
{
    /// <summary>Gets the bucket the object lives in.</summary>
    public required string Bucket { get; init; }

    /// <summary>Gets the object's name within the bucket.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the size of the whole object in bytes.</summary>
    /// <remarks>
    /// This is the size of the object, not of the bytes a ranged read returned.
    /// </remarks>
    public required long Size { get; init; }

    /// <summary>Gets the media type the object was stored with.</summary>
    public string MediaType { get; init; } = Media.MediaTypes.Default;

    /// <summary>Gets the entity tag the server reports for the object.</summary>
    public string? ETag { get; init; }

    /// <summary>Gets when the object was last written.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Gets the user metadata stored with the object.</summary>
    /// <remarks>
    /// The names are as they were written, without the <c>x-amz-meta-</c> the wire adds and removes. What
    /// the protocol itself defines — the media type, the length — is not repeated here.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = FrozenDictionary<string, string>.Empty;
}
