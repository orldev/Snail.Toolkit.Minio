using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Crash;

/// <summary>
/// A server that answers, but not honestly.
/// </summary>
/// <remarks>
/// Everything here is an answer a real server can produce under load or under attack, and none of it can be
/// staged against a healthy one: a body that stops mid-stream, an error document the size of a video, an
/// entity that wants to read the filesystem, a gateway that is briefly out of capacity.
/// </remarks>
public class ChaosCrashTests
{
    /// <summary>
    /// Breaks the copy: the server promises a hundred bytes, sends ten, and drops the connection. The
    /// caller must be told, and the destination must not keep the ten bytes it received.
    /// </summary>
    [Fact]
    public async Task AConnectionLostMidDownload_IsReportedAndRewound()
    {
        using var transport = HostileTransport.Truncating(stated: 100, sent: 10);
        using var provider = Provider(transport);
        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageErrorKind.Connection, result.Error!.Kind);
        Assert.Equal(0, destination.Length);
    }

    /// <summary>
    /// Breaks the error path: a failure whose body is sixteen megabytes of padding. Reading it whole to
    /// find a six-character code is how a failing server takes the client down with it.
    /// </summary>
    [Fact]
    public async Task AnOversizedErrorDocument_IsClassifiedWithoutReadingItWhole()
    {
        using var transport = HostileTransport.Oversized(megabytes: 16);
        using var provider = Provider(transport);
        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageErrorKind.ObjectNotFound, result.Error!.Kind);
        Assert.True(
            result.Error.Message.Length < 100_000,
            $"the message carries {result.Error.Message.Length} characters of the server's body");
    }

    /// <summary>
    /// The cost of reading a failure only up to a limit: a code the server buried behind a megabyte of
    /// padding is not found. The failure is still classified, from the status, and still says what happened.
    /// </summary>
    [Fact]
    public async Task ACodeBuriedBehindPadding_IsNotSearchedFor()
    {
        using var transport = HostileTransport.Buried(megabytes: 2);
        using var provider = Provider(transport);
        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageErrorKind.ObjectNotFound, result.Error!.Kind);
        Assert.Null(result.Error.Code);
        Assert.Equal(404, result.Error.StatusCode);
    }

    /// <summary>
    /// Breaks the parser: an error document that declares an external entity pointing at a local file. The
    /// code must never come back holding the contents of that file.
    /// </summary>
    [Fact]
    public async Task AnErrorDocumentThatWantsToReadTheFilesystem_IsRefused()
    {
        using var transport = HostileTransport.Xxe();
        using var provider = Provider(transport);
        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("root:", result.Error!.Code ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("root:", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Breaks the retry rule: a gateway answering 503 is the textbook transient failure, and a client that
    /// gives up on it turns a five-second deployment into a five-second outage.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ATransientServerAnswer_IsTriedAgain(HttpStatusCode status)
    {
        using var transport = new HostileTransport(attempt => attempt < 3
            ? new HttpResponseMessage(status) { Content = new StringContent(string.Empty) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("late"u8.ToArray()) });

        using var provider = Provider(transport, attempts: 3);
        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(3, transport.Requests);
        Assert.Equal("late"u8.ToArray(), destination.ToArray());
    }

    /// <summary>
    /// Breaks the retry loop with the caller's own token: cancelling while the client is waiting between
    /// attempts has to raise, not linger and answer.
    /// </summary>
    [Fact]
    public async Task CancellingWhileWaitingBetweenAttempts_Raises()
    {
        using var transport = new HostileTransport(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(string.Empty)
        });

        using var provider = Provider(transport, attempts: 50, delay: "00:00:05");
        using var destination = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetRequiredService<IObjectStorage>()
                .DownloadToAsync("bucket", "object", destination, cts.Token));
    }

    /// <summary>
    /// Breaks the classifier: a status nobody planned for still has to arrive as a failure a caller can
    /// branch on, carrying what the server said.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.InsufficientStorage)]
    [InlineData((HttpStatusCode)599)]
    public async Task AStatusNobodyPlannedFor_IsStillAnAnswer(HttpStatusCode status)
    {
        using var transport = new HostileTransport(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(string.Empty)
        });

        using var provider = Provider(transport);
        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination);

        Assert.False(result.IsSuccess);
        Assert.Equal((int)status, result.Error!.StatusCode);
    }

    private static ServiceProvider Provider(MinioTransport transport, int attempts = 0, string delay = "00:00:00.010")
    {
        var settings = MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key");

        settings["Minio:RetryAttempts"] = attempts.ToString();
        settings["Minio:RetryDelay"] = delay;

        return MinioContainerFixture.Provider(
            settings,
            services => services.Replace(ServiceDescriptor.Singleton(transport)));
    }
}
