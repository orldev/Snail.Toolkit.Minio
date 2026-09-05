using System.Net;
using Snail.Toolkit.Minio.Adapters;

namespace Snail.Toolkit.Minio.Tests.Crash;

/// <summary>
/// A transport that counts the response bodies it hands out and the ones that come back closed.
/// </summary>
/// <remarks>
/// A soak run would show a leak as memory that climbs over hours, which is neither a test nor an answer.
/// Counting instead makes the same question exact and instant: every body opened has to be closed, on the
/// path that succeeded, the one that failed, and the one that gave up half way.
/// </remarks>
/// <param name="answer">Produces the status for each request, in order.</param>
internal sealed class TrackingTransport(Func<int, HttpStatusCode> answer) : MinioTransport
{
    private readonly Handler _handler = new(answer);

    /// <summary>Gets how many response bodies were handed out.</summary>
    public int Opened => _handler.Opened;

    /// <summary>Gets how many of them were closed.</summary>
    public int Closed => _handler.Closed;

    /// <inheritdoc />
    public override HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

    /// <inheritdoc />
    public override void Dispose() => _handler.Dispose();

    private sealed class Handler(Func<int, HttpStatusCode> answer) : HttpMessageHandler
    {
        private int _requests;
        private int _opened;
        private int _closed;

        public int Opened => Volatile.Read(ref _opened);

        public int Closed => Volatile.Read(ref _closed);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var status = answer(Interlocked.Increment(ref _requests));

            Interlocked.Increment(ref _opened);

            var body = status == HttpStatusCode.OK
                ? "payload"u8.ToArray()
                : "<Error><Code>NoSuchKey</Code></Error>"u8.ToArray();

            var response = new HttpResponseMessage(status)
            {
                Content = new StreamContent(new CountingStream(body, () => Interlocked.Increment(ref _closed)))
            };

            response.Content.Headers.ContentLength = body.Length;

            return Task.FromResult(response);
        }
    }

    private sealed class CountingStream(byte[] body, Action onClosed) : MemoryStream(body)
    {
        private bool _closed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                onClosed();
            }

            base.Dispose(disposing);
        }
    }
}
