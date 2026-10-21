using System.Net;
using Minio.DataModel.Result;
using Minio.Exceptions;
using Snail.Toolkit.Minio.Adapters;

namespace Snail.Toolkit.Minio.Tests.Chaos;

/// <summary>
/// Buckets made by several processes at once, and settings the validator lets through to a client that cannot
/// hold them.
/// </summary>
public class BucketChaosTests
{
    /// <summary>
    /// Breaks the promise that creating a bucket that exists succeeds: eight replicas provisioning the same bucket
    /// at start-up all see it missing, all create it, and seven are answered 409 by the server — which the adapter
    /// reported as an upstream failure, at warning, to callers who asked for exactly what they now have. The
    /// fake answers that the bucket is there from the moment the first creation went through.
    /// </summary>
    [Fact]
    public async Task CreatingTheSameBucketFromEightCallersAtOnce_SucceedsForAll()
    {
        var made = 0;

        var client = FakeClient.Answering(
            (nameof(IMinioClient.BucketExistsAsync), _ => Task.FromResult(Volatile.Read(ref made) > 0)),
            (nameof(IMinioClient.MakeBucketAsync), _ => Interlocked.Increment(ref made) == 1
                ? Task.CompletedTask
                : Task.FromException(AlreadyOwned())));

        using var storage = FakeClient.Storage(client);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => storage.CreateAsync("shared")));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.ToString()));
    }

    /// <summary>
    /// Breaks the validator with a timeout longer than the client's integer of milliseconds: the settings are
    /// accepted at start-up and the number handed to the SDK is whatever the cast makes of it on this CPU.
    /// </summary>
    [Fact]
    public void ATimeoutTheClientCannotRepresent_IsRefusedAtStartup()
    {
        var options = new MinioOptions
        {
            Endpoint = "minio.local:9000",
            AccessKey = "a",
            SecretKey = "b",
            Timeout = TimeSpan.FromDays(30)
        };

        Assert.NotNull(MinioOptionsValidator.Fault("Minio", options));
    }

    private static MinioException AlreadyOwned()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "http://minio.local/shared");

        var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("<Error><Code>BucketAlreadyOwnedByYou</Code></Error>")
        };

        return new UnexpectedMinioException("Your previous request to create the named bucket succeeded and you already own it.", new ResponseResult(request, response));
    }
}
