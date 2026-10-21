using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio.DataModel;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Chaos;

/// <summary>
/// A listing against a bucket that changes under it, and a walk against a server that stumbles.
/// </summary>
public class ListingChaosTests
{
    /// <summary>
    /// Breaks the cursor: the page is resumed by skipping until the cursor's own name is seen. Delete that object
    /// between two pages — any workload that lists and then removes does this — and the skip never ends: the
    /// second page comes back empty with no cursor, and the caller believes the bucket had two objects.
    /// </summary>
    [Fact]
    public async Task ListingAfterTheCursorObjectWasDeleted_ContinuesFromItsPlace()
    {
        var names = new List<string> { "a", "b", "c", "d", "e" };
        using var storage = FakeClient.Storage(FakeClient.Answering((nameof(IMinioClient.ListObjectsEnumAsync), _ => Listed(names))));

        var first = await storage.ListAsync("bucket", new ListOptions { PageSize = 2 });

        Assert.Equal(["a", "b"], first.Value.Objects.Select(stored => stored.Name));
        Assert.Equal("b", first.Value.Cursor);

        names.Remove("b");

        var second = await storage.ListAsync("bucket", new ListOptions { PageSize = 2, Cursor = first.Value.Cursor });

        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal(["c", "d"], second.Value.Objects.Select(stored => stored.Name));
        Assert.True(second.Value.HasMore);
    }

    /// <summary>
    /// Breaks the promise that reads are retried: a walk is the recommended way to read a whole bucket, and it is
    /// the one read that went to the server once, outside the retry funnel and the circuit breaker. A deployment
    /// answering 503 for a second ended the walk where a paged listing would have waited. The answer carries an
    /// S3 code because the SDK reports a 503 by its code and without its status.
    /// </summary>
    [Fact]
    public async Task AWalkMeetingATransientFailure_IsRetriedLikeEveryOtherRead()
    {
        using var transport = new ChaosTransport((_, _) => Task.FromResult(ChaosTransport.Answer(
            HttpStatusCode.ServiceUnavailable,
            "<Error><Code>SlowDown</Code><Message>slow down</Message></Error>")));

        using var provider = Provider(transport, attempts: 2);

        var results = new List<StorageResult<StoredObject>>();

        await foreach (var result in provider.GetRequiredService<IObjectStorage>().EnumerateAsync("bucket"))
            results.Add(result);

        Assert.False(Assert.Single(results).IsSuccess);
        Assert.Equal(3, transport.Requests);
    }

    private static async IAsyncEnumerable<Item> Listed(List<string> names)
    {
        foreach (var name in names.ToArray())
            yield return new Item { Key = name, Size = 1 };

        await Task.CompletedTask;
    }

    private static ServiceProvider Provider(MinioTransport transport, int attempts)
    {
        var settings = MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key");

        settings["Minio:RetryAttempts"] = attempts.ToString();
        settings["Minio:RetryDelay"] = "00:00:00.010";

        return MinioContainerFixture.Provider(
            settings,
            services => services.Replace(ServiceDescriptor.Singleton(transport)));
    }
}
