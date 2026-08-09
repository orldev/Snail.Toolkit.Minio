namespace Toolkit.Minio.Tests.Infrastructure;

/// <summary>
/// Terminal <see cref="HttpMessageHandler"/> that answers every request with an empty 200 and records what
/// was sent.
/// </summary>
/// <remarks>
/// Used only where an assertion has to be made about the outgoing request itself and a real server cannot
/// provide it. Everything a live server can demonstrate is tested against a live server instead.
/// </remarks>
internal sealed class RequestRecorder : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests = [];

    /// <summary>Gets the requests the client issued, in order.</summary>
    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    /// <summary>
    /// Builds a client whose traffic is captured by a new recorder instead of reaching a server.
    /// </summary>
    /// <param name="recorder">The recorder backing the client.</param>
    /// <returns>A client that never touches the network.</returns>
    public static IMinioClient CreateClient(out RequestRecorder recorder)
    {
        recorder = new RequestRecorder();

        return new MinioClient()
            .WithEndpoint("localhost:9000")
            .WithCredentials("access-key", "secret-key")
            .WithSSL(false)
            .WithHttpClient(new HttpClient(recorder), disposeHttpClient: true)
            .Build();
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requests.Add(request);

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        };

        response.Content.Headers.ContentLength = 0;
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        response.Headers.ETag = new("\"etag\"");

        return Task.FromResult(response);
    }
}
