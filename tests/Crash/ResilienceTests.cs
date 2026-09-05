using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Crash;

/// <summary>
/// What the adapter does once a server stops answering properly.
/// </summary>
public class ResilienceTests
{
    /// <summary>
    /// A server under load says how long to wait. Waiting a shorter interval of one's own is part of what
    /// keeps it under load.
    /// </summary>
    [Fact]
    public async Task AServerThatAsksForAWait_IsGivenIt()
    {
        using var transport = new HostileTransport(attempt => attempt == 1
            ? Unavailable(TimeSpan.FromSeconds(1))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("late"u8.ToArray()) });

        using var provider = Provider(transport, attempts: 1, delay: "00:00:00.010");
        using var destination = new MemoryStream();

        var clock = Stopwatch.StartNew();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination);

        clock.Stop();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(
            clock.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"waited {clock.Elapsed} instead of the second the server asked for");
    }

    /// <summary>
    /// A wait longer than any request should live is an answer, not an instruction: the caller is told now
    /// rather than blocked for five minutes inside a call nobody can see.
    /// </summary>
    [Fact]
    public async Task AServerThatAsksForTooLong_IsNotWaitedFor()
    {
        using var transport = new HostileTransport(_ => Unavailable(TimeSpan.FromMinutes(5)));
        using var provider = Provider(transport, attempts: 3);
        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination);

        Assert.False(result.IsSuccess);
        Assert.Equal(TimeSpan.FromMinutes(5), result.Error!.RetryAfter);
        Assert.Equal(1, transport.Requests);
    }

    /// <summary>
    /// Once a server has refused everything, asking again is the client's contribution to the outage.
    /// </summary>
    [Fact]
    public async Task AfterEnoughFailures_TheServerIsLeftAlone()
    {
        using var transport = new HostileTransport(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(string.Empty)
        });

        using var provider = Provider(transport, failures: 3, breakFor: "00:10:00");
        var storage = provider.GetRequiredService<IObjectStorage>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var destination = new MemoryStream();
            await storage.DownloadToAsync("bucket", "object", destination);
        }

        Assert.Equal(3, transport.Requests);

        using var refused = new MemoryStream();
        var result = await storage.DownloadToAsync("bucket", "object", refused);

        Assert.False(result.IsSuccess);
        Assert.Equal(3, transport.Requests);
        Assert.Contains("Not attempted", result.Error!.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Error.RetryAfter);
    }

    /// <summary>
    /// The way back: after the wait, exactly one call is let through, and a server that has recovered gets
    /// its traffic back.
    /// </summary>
    [Fact]
    public async Task AfterTheWait_OneCallIsLetThrough_AndRecoveryReopensTheGate()
    {
        var recovered = false;

        using var transport = new HostileTransport(_ => recovered
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("back"u8.ToArray()) }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(string.Empty) });

        using var provider = Provider(transport, failures: 2, breakFor: "00:00:00.200");
        var storage = provider.GetRequiredService<IObjectStorage>();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var destination = new MemoryStream();
            await storage.DownloadToAsync("bucket", "object", destination);
        }

        using (var blocked = new MemoryStream())
        {
            Assert.False((await storage.DownloadToAsync("bucket", "object", blocked)).IsSuccess);
        }

        Assert.Equal(2, transport.Requests);

        recovered = true;
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        using var probe = new MemoryStream();
        Assert.True((await storage.DownloadToAsync("bucket", "object", probe)).IsSuccess);

        using var after = new MemoryStream();
        Assert.True((await storage.DownloadToAsync("bucket", "object", after)).IsSuccess);
        Assert.Equal(4, transport.Requests);
    }

    /// <summary>
    /// A missing object is not the server's health. Counting it would open the circuit on a workload that
    /// legitimately asks for things that are not there.
    /// </summary>
    [Fact]
    public async Task ObjectsThatAreNotThere_DoNotOpenTheCircuit()
    {
        using var transport = new HostileTransport(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<Error><Code>NoSuchKey</Code></Error>")
        });

        using var provider = Provider(transport, failures: 3);
        var storage = provider.GetRequiredService<IObjectStorage>();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var destination = new MemoryStream();
            var result = await storage.DownloadToAsync("bucket", "object", destination);

            Assert.Equal(StorageErrorKind.ObjectNotFound, result.Error!.Kind);
        }

        Assert.Equal(10, transport.Requests);
    }

    /// <summary>
    /// Turning the breaker off has to mean off, for callers who compose their own resilience.
    /// </summary>
    [Fact]
    public async Task WithTheBreakerTurnedOff_EveryCallIsMade()
    {
        using var transport = new HostileTransport(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(string.Empty)
        });

        using var provider = Provider(transport, failures: 0);
        var storage = provider.GetRequiredService<IObjectStorage>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var destination = new MemoryStream();
            await storage.DownloadToAsync("bucket", "object", destination);
        }

        Assert.Equal(5, transport.Requests);
    }

    private static HttpResponseMessage Unavailable(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(string.Empty)
        };

        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);

        return response;
    }

    private static ServiceProvider Provider(
        MinioTransport transport,
        int attempts = 0,
        string delay = "00:00:00.010",
        int failures = 0,
        string breakFor = "00:00:05")
    {
        var settings = MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key");

        settings["Minio:RetryAttempts"] = attempts.ToString();
        settings["Minio:RetryDelay"] = delay;
        settings["Minio:CircuitBreakFailures"] = failures.ToString();
        settings["Minio:CircuitBreakDuration"] = breakFor;

        return MinioContainerFixture.Provider(
            settings,
            services => services.Replace(ServiceDescriptor.Singleton(transport)));
    }
}
