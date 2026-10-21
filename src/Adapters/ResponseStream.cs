namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// A response body that closes its response when the caller is done reading.
/// </summary>
/// <param name="inner">The body being read.</param>
/// <param name="response">The response the body belongs to.</param>
/// <remarks>
/// <para>
/// The caller is handed a stream and told it owns it. Without this, disposing that stream would leave
/// the response — and the connection it is holding — alive until a garbage collection got to it.
/// </para>
/// <para>
/// Reading it after it is closed raises. The response body underneath answers zero bytes instead, which
/// a caller cannot tell from an empty object — a truncated download that looks like a successful one.
/// </para>
/// </remarks>
internal sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
{
    private bool _disposed;

    public override bool CanRead => !_disposed && inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return inner.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return inner.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;

            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            inner.Dispose();
            response.Dispose();
        }

        base.Dispose(disposing);
    }
}
