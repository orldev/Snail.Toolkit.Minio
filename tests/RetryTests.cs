using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// What is retried, and what deliberately is not.
/// </summary>
/// <remarks>
/// The server used here accepts every connection and answers none, so each attempt is guaranteed to fail
/// the same way and the connections it accepted are a count of the attempts made.
/// </remarks>
public class RetryTests
{
    [Fact]
    public async Task ATimedOutRead_IsAttemptedAgain()
    {
        using var unresponsive = new UnresponsiveServer();
        using var provider = MinioContainerFixture.Provider(Settings(unresponsive.Endpoint, attempts: 2));

        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination);

        Assert.Equal(StorageErrorKind.Timeout, result.Error!.Kind);
        Assert.Equal(3, unresponsive.Accepted);
    }

    [Fact]
    public async Task ATimedOutRead_IsAttemptedOnce_WhenRetriesAreTurnedOff()
    {
        using var unresponsive = new UnresponsiveServer();
        using var provider = MinioContainerFixture.Provider(Settings(unresponsive.Endpoint, attempts: 0));

        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("bucket", "object", destination);

        Assert.Equal(StorageErrorKind.Timeout, result.Error!.Kind);
        Assert.Equal(1, unresponsive.Accepted);
    }

    /// <summary>
    /// An upload is never replayed: its stream may already be partly consumed, and a second attempt would
    /// store the tail of a file as the whole of one.
    /// </summary>
    [Fact]
    public async Task AnUpload_IsNeverAttemptedAgain()
    {
        using var unresponsive = new UnresponsiveServer();
        using var provider = MinioContainerFixture.Provider(Settings(unresponsive.Endpoint, attempts: 3));

        using var content = new MemoryStream("payload"u8.ToArray());

        var result = await provider.GetRequiredService<IObjectStorage>()
            .PutAsync("bucket", content, new UploadOptions { Name = "object" });

        Assert.False(result.IsSuccess);
        Assert.Equal(1, unresponsive.Accepted);
    }

    private static Dictionary<string, string?> Settings(string endpoint, int attempts)
    {
        var settings = MinioContainerFixture.Settings(endpoint, "access-key", "secret-key");

        settings["Minio:Timeout"] = "00:00:00.300";
        settings["Minio:RetryAttempts"] = attempts.ToString();
        settings["Minio:RetryDelay"] = "00:00:00.010";

        return settings;
    }
}
