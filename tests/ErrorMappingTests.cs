using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// Verifies that each failure a caller can realistically hit maps to a distinct
/// <see cref="StorageErrorKind"/>.
/// </summary>
/// <remarks>
/// <para>
/// The point of the result pattern is that callers branch on the kind instead of matching exception types
/// or parsing messages. That promise is worth something only if the mapping is right for failures produced
/// by a real server and a real socket.
/// </para>
/// <para>
/// Provoking the failures for real rather than simulating them is what makes this class useful: the
/// connection and timeout cases were both reported as an unexpected error until a live server exposed it.
/// </para>
/// </remarks>
[Collection(MinioContainerCollection.Name)]
public class ErrorMappingTests(MinioContainerFixture fixture)
{
    [Fact]
    public async Task MissingBucket_MapsToBucketNotFound()
    {
        var result = await fixture.Storage.StatAsync("no-such-bucket-anywhere", "object");

        Assert.Equal(StorageErrorKind.BucketNotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task MissingObject_InAnExistingBucket_MapsToObjectNotFound()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var result = await fixture.Storage.StatAsync(bucket, "no-such-object");

            Assert.Equal(StorageErrorKind.ObjectNotFound, result.Error!.Kind);
        });
    }

    /// <summary>
    /// The read path signs its own request, so a missing object comes back as a 404 carrying
    /// <c>NoSuchKey</c> rather than as an SDK exception. Both paths have to agree on the kind.
    /// </summary>
    [Fact]
    public async Task MissingObject_OnTheReadPath_AlsoMapsToObjectNotFound()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var result = await fixture.Storage.GetAsync(bucket, "no-such-object");

            Assert.Equal(StorageErrorKind.ObjectNotFound, result.Error!.Kind);
            Assert.Equal(404, result.Error.StatusCode);
            Assert.Equal("NoSuchKey", result.Error.Code);
        });
    }

    [Fact]
    public async Task BucketNameRejectedByTheClient_MapsToInvalidBucketName()
    {
        var result = await fixture.Storage.StatAsync("A", "object");

        Assert.Equal(StorageErrorKind.InvalidBucketName, result.Error!.Kind);
    }

    [Fact]
    public async Task WrongCredentials_MapToAccessDenied()
    {
        using var provider = MinioContainerFixture.Provider(
            MinioContainerFixture.Settings(fixture.Endpoint, "wrong-access-key", "wrong-secret-key"));

        var result = await provider.GetRequiredService<IObjectStorage>().StatAsync("any-bucket-name", "object");

        Assert.Equal(StorageErrorKind.AccessDenied, result.Error!.Kind);
    }

    /// <summary>
    /// The same rejection over the read path, which sees the S3 code the SDK hides.
    /// </summary>
    [Fact]
    public async Task WrongCredentials_OnTheReadPath_AlsoMapToAccessDenied()
    {
        using var provider = MinioContainerFixture.Provider(
            MinioContainerFixture.Settings(fixture.Endpoint, "wrong-access-key", "wrong-secret-key"));

        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadToAsync("any-bucket-name", "object", destination);

        Assert.Equal(StorageErrorKind.AccessDenied, result.Error!.Kind);
        Assert.Equal(403, result.Error.StatusCode);
    }

    [Fact]
    public async Task UnreachableEndpoint_MapsToConnection()
    {
        using var provider = MinioContainerFixture.Provider(
            MinioContainerFixture.Settings("127.0.0.1:1", "access-key", "secret-key"));

        var result = await provider.GetRequiredService<IObjectStorage>().StatAsync("any-bucket-name", "object");

        Assert.Equal(StorageErrorKind.Connection, result.Error!.Kind);
    }

    /// <summary>
    /// The server here accepts the connection and then never answers, so the timeout is guaranteed to be
    /// what fails. Pointing a short timeout at the real container instead would be a race.
    /// </summary>
    [Fact]
    public async Task ExpiredRequestTimeout_MapsToTimeout()
    {
        using var unresponsive = new UnresponsiveServer();

        var settings = MinioContainerFixture.Settings(unresponsive.Endpoint, "access-key", "secret-key");
        settings["Minio:Timeout"] = "00:00:00.300";

        using var provider = MinioContainerFixture.Provider(settings);

        var result = await provider.GetRequiredService<IObjectStorage>().StatAsync("any-bucket-name", "object");

        Assert.Equal(StorageErrorKind.Timeout, result.Error!.Kind);
    }

    [Fact]
    public async Task CallerCancellation_Throws_AndIsNotReportedAsAResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Storage.StatAsync("any-bucket-name", "object", cts.Token));
    }

    [Fact]
    public async Task CallerCancellation_Throws_FromOperationsWithoutAReturnValue()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Storage.RemoveAsync("any-bucket-name", "object", cts.Token));
    }

    [Fact]
    public async Task CallerCancellation_Throws_FromTheReadPath()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Storage.GetAsync("any-bucket-name", "object", cts.Token));
    }
}
