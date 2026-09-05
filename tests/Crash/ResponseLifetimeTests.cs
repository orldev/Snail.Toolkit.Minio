using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Crash;

/// <summary>
/// Every response body the adapter opens, on every path, has to be closed.
/// </summary>
/// <remarks>
/// This is the leak question asked exactly rather than by running for an hour and watching a graph: a body
/// left open is a connection held, and a hundred of them are an application that stops reading anything.
/// </remarks>
public class ResponseLifetimeTests
{
    private const int Reads = 25;

    [Fact]
    public async Task EverySuccessfulRead_ClosesItsResponse()
    {
        using var transport = new TrackingTransport(_ => HttpStatusCode.OK);
        using var provider = Provider(transport);
        var storage = provider.GetRequiredService<IObjectStorage>();

        for (var read = 0; read < Reads; read++)
        {
            using var destination = new MemoryStream();
            Assert.True((await storage.DownloadToAsync("bucket", "object", destination)).IsSuccess);
        }

        Assert.Equal(Reads, transport.Opened);
        Assert.Equal(Reads, transport.Closed);
    }

    [Fact]
    public async Task EveryFailedRead_ClosesItsResponse()
    {
        using var transport = new TrackingTransport(_ => HttpStatusCode.NotFound);
        using var provider = Provider(transport);
        var storage = provider.GetRequiredService<IObjectStorage>();

        for (var read = 0; read < Reads; read++)
        {
            using var destination = new MemoryStream();
            Assert.False((await storage.DownloadToAsync("bucket", "object", destination)).IsSuccess);
        }

        Assert.Equal(Reads, transport.Opened);
        Assert.Equal(Reads, transport.Closed);
    }

    /// <summary>
    /// A retried read opens a response per attempt, and abandons every one of them but the last.
    /// </summary>
    [Fact]
    public async Task EveryAbandonedAttempt_ClosesItsResponse()
    {
        using var transport = new TrackingTransport(attempt =>
            attempt < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);

        using var provider = Provider(transport, attempts: 3);
        using var destination = new MemoryStream();

        Assert.True((await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination)).IsSuccess);

        Assert.Equal(3, transport.Opened);
        Assert.Equal(3, transport.Closed);
    }

    /// <summary>
    /// The one response the adapter deliberately keeps open is the one it handed to the caller — and it
    /// closes with the content, not before and not never.
    /// </summary>
    [Fact]
    public async Task AHandedOverResponse_ClosesWithItsContent()
    {
        using var transport = new TrackingTransport(_ => HttpStatusCode.OK);
        using var provider = Provider(transport);

        var read = await provider.GetRequiredService<IObjectStorage>().GetAsync("bucket", "object");

        Assert.Equal(1, transport.Opened);
        Assert.Equal(0, transport.Closed);

        await read.Value.DisposeAsync();

        Assert.Equal(1, transport.Closed);
    }

    private static ServiceProvider Provider(MinioTransport transport, int attempts = 0)
    {
        var settings = MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key");

        settings["Minio:RetryAttempts"] = attempts.ToString();
        settings["Minio:RetryDelay"] = "00:00:00.001";

        return MinioContainerFixture.Provider(
            settings,
            services => services.Replace(ServiceDescriptor.Singleton(transport)));
    }
}
