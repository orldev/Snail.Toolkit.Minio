using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Chaos;

/// <summary>
/// The circuit breaker's way back, when the one call it lets through does not end the way it expects.
/// </summary>
/// <remarks>
/// The breaker marks itself as probing when it lets a call through and clears that mark only on a success or on a
/// failure worth counting. A probe that is answered with something else — a missing object, which the breaker
/// rightly refuses to count, or a caller's cancellation, which never reaches the breaker at all — leaves the mark
/// set, and every later call is refused for "another 00:00:00" until the process restarts.
/// </remarks>
public class CircuitBreakerChaosTests
{
    /// <summary>
    /// Breaks recovery with an honest answer: the probe asks for an object that is not there. The server is healthy,
    /// the answer is a 404, and the traffic has to come back.
    /// </summary>
    [Fact]
    public async Task AProbeAnsweredWithAMissingObject_ReopensTheGate()
    {
        using var transport = new ChaosTransport((attempt, _) => Task.FromResult(attempt switch
        {
            <= 2 => ChaosTransport.Answer(HttpStatusCode.ServiceUnavailable),
            3 => ChaosTransport.NoSuchKey(),
            _ => ChaosTransport.Answer(HttpStatusCode.OK, "back")
        }));

        using var provider = Provider(transport, failures: 2, breakFor: "00:00:00.100");
        var storage = provider.GetRequiredService<IObjectStorage>();

        await FailTwiceAsync(storage);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        using var probe = new MemoryStream();
        Assert.Equal(StorageErrorKind.ObjectNotFound, (await storage.DownloadToAsync("bucket", "object", probe)).Error?.Kind);

        using var after = new MemoryStream();
        var result = await storage.DownloadToAsync("bucket", "object", after);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(4, transport.Requests);
    }

    /// <summary>
    /// Breaks recovery with the caller's own impatience: the probe is cancelled by the token it was given. The
    /// exception is the contract, but the next caller must not inherit a gate that never opens again.
    /// </summary>
    [Fact]
    public async Task AProbeCancelledByItsCaller_DoesNotLeaveTheGateShutForever()
    {
        using var transport = new ChaosTransport(async (attempt, token) =>
        {
            if (attempt == 3)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);

            return attempt <= 2
                ? ChaosTransport.Answer(HttpStatusCode.ServiceUnavailable)
                : ChaosTransport.Answer(HttpStatusCode.OK, "back");
        });

        using var provider = Provider(transport, failures: 2, breakFor: "00:00:00.100");
        var storage = provider.GetRequiredService<IObjectStorage>();

        await FailTwiceAsync(storage);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        using var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var probe = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.DownloadToAsync("bucket", "object", probe, impatient.Token));

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        using var after = new MemoryStream();
        var result = await storage.DownloadToAsync("bucket", "object", after);

        Assert.True(result.IsSuccess, result.Error?.ToString());
    }

    private static async Task FailTwiceAsync(IObjectStorage storage)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var destination = new MemoryStream();
            await storage.DownloadToAsync("bucket", "object", destination);
        }
    }

    private static ServiceProvider Provider(MinioTransport transport, int failures, string breakFor)
    {
        var settings = MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key");

        settings["Minio:RetryAttempts"] = "0";
        settings["Minio:CircuitBreakFailures"] = failures.ToString();
        settings["Minio:CircuitBreakDuration"] = breakFor;

        return MinioContainerFixture.Provider(
            settings,
            services => services.Replace(ServiceDescriptor.Singleton(transport)));
    }
}
