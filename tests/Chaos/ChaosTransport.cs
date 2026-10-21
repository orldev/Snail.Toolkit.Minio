using System.Net;
using Snail.Toolkit.Minio.Adapters;

namespace Snail.Toolkit.Minio.Tests.Chaos;

/// <summary>
/// A transport whose answers may take their time, block until the request is cancelled, or stall a body forever.
/// </summary>
/// <remarks>
/// <see cref="Crash.HostileTransport"/> answers at once and never sees the token. The failures in this folder are
/// about time: a probe that hangs until the caller gives up, an error document whose body never ends. Those need
/// an answer that is itself asynchronous and knows when it was cancelled.
/// </remarks>
/// <param name="answer">Produces the answer for each request, in order, under the request's own token.</param>
internal sealed class ChaosTransport(Func<int, CancellationToken, Task<HttpResponseMessage>> answer) : MinioTransport
{
    private readonly Handler _handler = new(answer);

    /// <summary>Gets how many requests the library issued.</summary>
    public int Requests => _handler.Requests;

    /// <inheritdoc />
    public override HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

    /// <inheritdoc />
    public override void Dispose() => _handler.Dispose();

    /// <summary>A plain answer with a body.</summary>
    public static HttpResponseMessage Answer(HttpStatusCode status, string body = "")
        => new(status) { Content = new StringContent(body) };

    /// <summary>A missing object, as S3 reports one.</summary>
    public static HttpResponseMessage NoSuchKey()
        => Answer(HttpStatusCode.NotFound, "<Error><Code>NoSuchKey</Code></Error>");

    /// <summary>A body that promises <paramref name="stated"/> bytes and loses the connection after <paramref name="sent"/>.</summary>
    public static HttpResponseMessage Truncated(int stated, int sent)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream(sent))
        };

        response.Content.Headers.ContentLength = stated;

        return response;
    }

    /// <summary>A failure whose body never arrives until <paramref name="release"/> completes.</summary>
    public static HttpResponseMessage Stalled(HttpStatusCode status, TaskCompletionSource release)
        => new(status) { Content = new StreamContent(new StalledStream(release)) };

    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => answer(Interlocked.Increment(ref _requests), cancellationToken);
    }

    private sealed class FailingStream(int sent) : Stream
    {
        private int _delivered;

        public override bool CanRead => true;
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
            if (_delivered >= sent)
                throw new IOException("The connection was reset by the peer.");

            var take = Math.Min(count, sent - _delivered);
            Array.Fill(buffer, (byte)'b', offset, take);
            _delivered += take;

            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledStream(TaskCompletionSource release) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await release.Task.WaitAsync(cancellationToken);

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
