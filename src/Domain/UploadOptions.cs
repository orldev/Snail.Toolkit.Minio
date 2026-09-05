namespace Snail.Toolkit.Minio.Domain;

/// <summary>
/// The parts of an upload the caller may want to decide.
/// </summary>
/// <remarks>
/// Everything here has a working default, so an upload that cares about none of it passes nothing.
/// </remarks>
public sealed record UploadOptions
{
    /// <summary>Gets the name to store the object under.</summary>
    /// <remarks>
    /// Left unset, a name is generated. Uploads that have no natural name — a scanned page, a screenshot —
    /// are the reason this is optional.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>Gets the media type to store the object with.</summary>
    /// <remarks>
    /// Left unset, the media type is derived from <see cref="Name"/>, and falls back to
    /// <c>application/octet-stream</c> when the extension says nothing.
    /// </remarks>
    public string? MediaType { get; init; }

    /// <summary>Gets the number of bytes to upload.</summary>
    /// <remarks>
    /// Left unset, the size is taken from the stream, which requires it to be seekable. A request body and
    /// anything else read forward-only has to state its size here.
    /// </remarks>
    public long? Size { get; init; }

    /// <summary>Gets a value indicating whether to append the extension implied by the media type.</summary>
    /// <remarks>
    /// An extension is appended only when the name does not already end with it, so <c>report.txt</c> stays
    /// <c>report.txt</c> rather than becoming <c>report.txt.txt</c>.
    /// </remarks>
    public bool AppendExtension { get; init; }

    /// <summary>Gets the user metadata to store with the object.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
