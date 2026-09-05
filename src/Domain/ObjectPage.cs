namespace Snail.Toolkit.Minio.Domain;

/// <summary>
/// One page of a listing.
/// </summary>
/// <param name="Objects">What the page holds, in the order the server returned it.</param>
/// <param name="Cursor">Where the next page starts, or <see langword="null"/> when this was the last one.</param>
/// <remarks>
/// The cursor is the whole of the pagination contract: a caller that keeps asking while it is not
/// <see langword="null"/> has walked the bucket, and one that stores it can resume later.
/// </remarks>
public sealed record ObjectPage(IReadOnlyList<StoredObject> Objects, string? Cursor)
{
    /// <summary>Gets the prefixes a listing rolled its objects up into.</summary>
    /// <remarks>
    /// Only a listing that does not descend produces these: they are what a folder looks like in a store
    /// that has no folders.
    /// </remarks>
    public IReadOnlyList<string> Prefixes { get; init; } = [];

    /// <summary>Gets a value indicating whether another page follows this one.</summary>
    public bool HasMore => Cursor is not null;
}
