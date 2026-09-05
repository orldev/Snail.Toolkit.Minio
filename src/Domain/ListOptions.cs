namespace Snail.Toolkit.Minio.Domain;

/// <summary>
/// What to list, and how much of it at a time.
/// </summary>
/// <remarks>
/// A bucket can hold more objects than fit in an answer, so listing is a page at a time. Everything here
/// has a working default: listing a bucket with no options walks all of it.
/// </remarks>
public sealed record ListOptions
{
    /// <summary>Gets the prefix an object's name has to start with.</summary>
    public string? Prefix { get; init; }

    /// <summary>Gets the page to continue from.</summary>
    /// <remarks>
    /// Taken from <see cref="ObjectPage.Cursor"/> of the previous page. Left unset, listing starts at the
    /// beginning.
    /// </remarks>
    public string? Cursor { get; init; }

    /// <summary>Gets how many objects a page may hold.</summary>
    public int PageSize { get; init; } = 1000;

    /// <summary>Gets a value indicating whether to descend into prefixes.</summary>
    /// <remarks>
    /// Turned off, a name is only listed when it has no further separator after the prefix, which is what
    /// makes a listing look like one level of a folder.
    /// </remarks>
    public bool IsRecursive { get; init; } = true;
}
