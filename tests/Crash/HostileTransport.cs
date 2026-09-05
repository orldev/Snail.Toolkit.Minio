using System.Net;
using Snail.Toolkit.Minio.Adapters;

namespace Snail.Toolkit.Minio.Tests.Crash;

/// <summary>
/// A transport that answers the library's own reads with whatever a hostile server might send.
/// </summary>
/// <remarks>
/// A live server is honest, which is exactly why it cannot produce these cases: a body that stops
/// mid-stream, an error document the size of a video, or a status the client has never seen. The read path
/// has to survive all of them without throwing at a caller who was promised a result.
/// </remarks>
/// <param name="answer">Produces the answer for each request, in order.</param>
internal sealed class HostileTransport(Func<int, HttpResponseMessage> answer) : MinioTransport
{
    private readonly Handler _handler = new(answer);

    /// <summary>Gets how many requests the library issued.</summary>
    public int Requests => _handler.Requests;

    /// <inheritdoc />
    public override HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

    /// <inheritdoc />
    public override void Dispose() => _handler.Dispose();

    /// <summary>Answers every read with a body that stops short of its stated length.</summary>
    /// <param name="stated">The length the headers promise.</param>
    /// <param name="sent">The bytes actually delivered before the stream fails.</param>
    /// <returns>A transport that truncates.</returns>
    public static HostileTransport Truncating(int stated, int sent)
        => new(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FailingStream(sent))
            };

            response.Content.Headers.ContentLength = stated;

            return response;
        });

    /// <summary>Answers every read with a failure whose body is the size of a video.</summary>
    /// <param name="megabytes">How large the error document is.</param>
    /// <returns>A transport that answers with an oversized error.</returns>
    public static HostileTransport Oversized(int megabytes)
        => new(_ =>
        {
            var padding = new string('x', megabytes * 1024 * 1024);

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"<Error><Code>NoSuchKey</Code><Message>{padding}</Message></Error>")
            };
        });

    /// <summary>Answers every read with a failure whose code hides behind a megabyte of padding.</summary>
    /// <param name="megabytes">How much padding precedes the code.</param>
    /// <returns>A transport whose code is out of reach of a bounded read.</returns>
    public static HostileTransport Buried(int megabytes)
        => new(_ =>
        {
            var padding = new string('x', megabytes * 1024 * 1024);

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"<Error><Message>{padding}</Message><Code>NoSuchKey</Code></Error>")
            };
        });

    /// <summary>Answers every read with an error document that tries to read a local file.</summary>
    /// <returns>A transport that answers with an XML external entity.</returns>
    public static HostileTransport Xxe()
        => new(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\"?>"
                + "<!DOCTYPE Error [<!ENTITY leak SYSTEM \"file:///etc/passwd\">]>"
                + "<Error><Code>&leak;</Code></Error>")
        });

    private sealed class Handler(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(answer(Interlocked.Increment(ref _requests)));
    }

    /// <summary>
    /// A body that delivers some bytes and then loses the connection.
    /// </summary>
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
            Array.Fill(buffer, (byte)'a', offset, take);
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
}
