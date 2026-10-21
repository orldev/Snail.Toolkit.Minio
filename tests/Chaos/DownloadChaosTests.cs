using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Chaos;

/// <summary>
/// What a download does to the caller's time and to the caller's stream.
/// </summary>
public class DownloadChaosTests
{
    /// <summary>
    /// Breaks the timeout: the server answers the headers of a failure inside the timeout and then never sends the
    /// body. The code is looked for in the body under the caller's token, not the request's deadline, so a caller
    /// without a token of its own waits forever inside a call that promised to give up after 200 ms.
    /// </summary>
    [Fact]
    public async Task AFailureWhoseBodyNeverArrives_StillHonoursTheTimeout()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var transport = new ChaosTransport((_, _)
            => Task.FromResult(ChaosTransport.Stalled(HttpStatusCode.ServiceUnavailable, release)));

        using var provider = Provider(transport, timeout: "00:00:00.200");
        using var destination = new MemoryStream();

        try
        {
            var download = provider.GetRequiredService<IObjectStorage>().DownloadToAsync("bucket", "object", destination);
            var finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.True(finished == download, "the download is still waiting for the body of a failure after three seconds");
            Assert.Equal(StorageErrorKind.Timeout, (await download).Error?.Kind);
        }
        finally
        {
            release.SetResult();
        }
    }

    /// <summary>
    /// Breaks the promise to leave the destination as it was found: a stream positioned inside an existing file is
    /// cut off at that position when the read fails, destroying everything after it that the caller never asked
    /// to touch. The pattern that meets this is parallel ranged downloads into one preallocated file.
    /// </summary>
    [Fact]
    public async Task AFailedReadIntoTheMiddleOfAStream_LeavesWhatFollowsIntact()
    {
        using var transport = new ChaosTransport((_, _) => Task.FromResult(ChaosTransport.Answer(HttpStatusCode.ServiceUnavailable)));
        using var provider = Provider(transport);
        using var destination = Preallocated(100, position: 50);

        var result = await provider.GetRequiredService<IObjectStorage>().DownloadToAsync("bucket", "object", destination);

        Assert.False(result.IsSuccess);
        Assert.Equal(100, destination.Length);
    }

    /// <summary>
    /// The same cut, on the way to a success: the first attempt loses the connection half way, the retry is
    /// prepared by rewinding, and the rewind truncates the file to the start of the range before the second
    /// attempt writes twenty bytes into it. The caller is told the read succeeded and has lost thirty bytes.
    /// </summary>
    [Fact]
    public async Task ARetriedReadIntoTheMiddleOfAStream_LeavesWhatFollowsIntact()
    {
        using var transport = new ChaosTransport((attempt, _) => Task.FromResult(attempt == 1
            ? ChaosTransport.Truncated(stated: 20, sent: 10)
            : ChaosTransport.Answer(HttpStatusCode.OK, new string('b', 20))));

        using var provider = Provider(transport, attempts: 1);
        using var destination = Preallocated(100, position: 50);

        var result = await provider.GetRequiredService<IObjectStorage>().DownloadToAsync("bucket", "object", destination);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(100, destination.Length);
        Assert.Equal((byte)'a', destination.ToArray()[99]);
    }

    private static MemoryStream Preallocated(int length, int position)
    {
        var destination = new MemoryStream();

        destination.Write(new string('a', length).Select(c => (byte)c).ToArray());
        destination.Position = position;

        return destination;
    }

    private static ServiceProvider Provider(MinioTransport transport, int attempts = 0, string? timeout = null)
    {
        var settings = MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key");

        settings["Minio:RetryAttempts"] = attempts.ToString();
        settings["Minio:RetryDelay"] = "00:00:00.010";
        settings["Minio:CircuitBreakFailures"] = "0";

        if (timeout is not null)
            settings["Minio:Timeout"] = timeout;

        return MinioContainerFixture.Provider(
            settings,
            services => services.Replace(ServiceDescriptor.Singleton(transport)));
    }
}
