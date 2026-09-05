using System.Net;
using Snail.Toolkit.Minio.Adapters;

namespace Snail.Toolkit.Minio.Tests.Infrastructure;

/// <summary>
/// A transport that answers the library's own read requests without a server, and keeps what was asked.
/// </summary>
/// <remarks>
/// Used only where an assertion has to be made about the outgoing request itself and a live server cannot
/// provide it — a 64-bit range needs an object larger than 2 GB to mean anything server-side. Everything a
/// live server can demonstrate is tested against a live server instead.
/// </remarks>
/// <param name="body">The bytes to answer every read with.</param>
internal sealed class RecordingTransport(byte[] body) : MinioTransport
{
    private readonly Recorder _recorder = new(body);

    /// <summary>Gets the requests the library issued, in order.</summary>
    public IReadOnlyList<HttpRequestMessage> Requests => _recorder.Requests;

    /// <inheritdoc />
    public override HttpClient CreateClient(string name) => new(_recorder, disposeHandler: false);

    /// <inheritdoc />
    public override void Dispose() => _recorder.Dispose();

    private sealed class Recorder(byte[] body) : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_requests)
                _requests.Add(request);

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body)
            };

            response.Content.Headers.ContentType = new("text/plain");
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            response.Headers.ETag = new("\"etag\"");

            return Task.FromResult(response);
        }
    }
}
