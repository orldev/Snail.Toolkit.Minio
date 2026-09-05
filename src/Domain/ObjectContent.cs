namespace Snail.Toolkit.Minio.Domain;

/// <summary>
/// An object's bytes together with what is known about it.
/// </summary>
/// <param name="Metadata">What the store knows about the object.</param>
/// <param name="Stream">The bytes, still being read from the server.</param>
/// <remarks>
/// <para>
/// The stream is live: nothing is buffered before the caller reads, so an object larger than memory costs
/// no more than the copy buffer. That also makes ownership the caller's — dispose this, and the underlying
/// response goes with it.
/// </para>
/// <para>
/// <see cref="Metadata"/> describes the whole object even when the stream carries only a requested range.
/// </para>
/// </remarks>
public sealed record ObjectContent(StoredObject Metadata, Stream Stream) : IAsyncDisposable, IDisposable
{
    /// <inheritdoc />
    public void Dispose() => Stream.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
