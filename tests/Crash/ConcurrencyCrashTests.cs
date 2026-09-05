using Microsoft.Extensions.DependencyInjection.Extensions;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Crash;

/// <summary>
/// The same objects, from many threads at once.
/// </summary>
/// <remarks>
/// Storage is registered as a singleton, so every request in an application shares one instance. Anything
/// it keeps has to survive being used from everywhere at the same time.
/// </remarks>
[Collection(MinioContainerCollection.Name)]
public class ConcurrencyCrashTests(MinioContainerFixture fixture)
{
    private const int Threads = 32;

    private static readonly byte[] Payload = "0123456789abcdefghij"u8.ToArray();

    /// <summary>
    /// Breaks the shared instance: thirty-two uploads at once, each with its own stream, all through one
    /// storage object and one connection pool.
    /// </summary>
    [Fact]
    public async Task ManyUploadsAtOnce_AllSucceed()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var uploads = Enumerable.Range(0, Threads).Select(async index =>
            {
                using var source = new MemoryStream(Payload);

                return await fixture.Storage.PutAsync(
                    bucket, source, new UploadOptions { Name = $"parallel-{index}" });
            });

            var results = await Task.WhenAll(uploads);

            Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.ToString()));
            Assert.Equal(Threads, results.Select(result => result.Value.Name).Distinct().Count());
        });
    }

    /// <summary>
    /// Breaks the read path: the same object read by every thread at once, each expecting its own stream
    /// and its own bytes.
    /// </summary>
    [Fact]
    public async Task TheSameObjectReadByEveryThread_YieldsTheSameBytes()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "contended" });

            var reads = Enumerable.Range(0, Threads).Select(async _ =>
            {
                using var destination = new MemoryStream();
                var result = await fixture.Storage.DownloadToAsync(bucket, "contended", destination);

                Assert.True(result.IsSuccess, result.Error?.ToString());

                return destination.ToArray();
            });

            Assert.All(await Task.WhenAll(reads), bytes => Assert.Equal(Payload, bytes));
        });
    }

    /// <summary>
    /// Breaks the delete path: one object, thirty-two deletions. A delete is idempotent, so every caller
    /// has to be told it succeeded rather than one winning and the rest seeing a missing object.
    /// </summary>
    [Fact]
    public async Task TheSameObjectDeletedByEveryThread_SucceedsEveryTime()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "doomed" });

            var deletes = Enumerable.Range(0, Threads)
                .Select(_ => fixture.Storage.RemoveAsync(bucket, "doomed"));

            Assert.All(await Task.WhenAll(deletes), result => Assert.True(result.IsSuccess));
        });
    }

    /// <summary>
    /// Breaks the pool: clients built from many threads at once must share one pool per configuration, or
    /// a burst of traffic opens a pool per request and exhausts the machine's sockets.
    /// </summary>
    [Fact]
    public void ClientsBuiltFromManyThreads_ShareOnePool()
    {
        using var provider = MinioContainerFixture.Provider(
            MinioContainerFixture.Settings("minio.example:9000", "access-key", "secret-key"));

        var clients = provider.GetRequiredService<IMinioClients>();
        var pools = new object?[Threads];

        Parallel.For(0, Threads, index =>
        {
            using var client = clients.Create(MinioOptions.SectionName);
            pools[index] = PoolOf(client);
        });

        Assert.Single(pools.Distinct());
    }

    /// <summary>
    /// Breaks shutdown: a request in flight while the container is being disposed. Whatever the outcome, it
    /// has to be an answer or a disposal error, never a torn object.
    /// </summary>
    [Fact]
    public async Task DisposingTheContainerWhileReadsAreInFlight_DoesNotTearTheTransport()
    {
        var transport = new HostileTransport(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("payload"u8.ToArray())
        });

        var provider = MinioContainerFixture.Provider(
            MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key"),
            services => services.Replace(ServiceDescriptor.Singleton<MinioTransport>(transport)));

        var storage = provider.GetRequiredService<IObjectStorage>();

        var reads = Enumerable.Range(0, Threads).Select(async _ =>
        {
            try
            {
                using var destination = new MemoryStream();
                await storage.DownloadToAsync("bucket", "object", destination);

                return null as Exception;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }).ToArray();

        await provider.DisposeAsync();

        var failures = (await Task.WhenAll(reads)).Where(exception => exception is not null).ToArray();

        Assert.All(failures, exception => Assert.IsType<ObjectDisposedException>(exception));
    }

    /// <summary>
    /// Breaks disposal itself: the default registration and its keyed twin are the same instance, so the
    /// container disposes it twice. Doing that from two threads has to stay harmless.
    /// </summary>
    [Fact]
    public void DisposingTheSameStorageTwiceAtOnce_IsHarmless()
    {
        var provider = MinioContainerFixture.Provider(
            MinioContainerFixture.Settings("minio.example:9000", "access-key", "secret-key"));

        var storage = (IDisposable)provider.GetRequiredService<IObjectStorage>();

        Parallel.For(0, Threads, _ => storage.Dispose());

        provider.Dispose();
    }

    private static object? PoolOf(IMinioClient client)
    {
        var handler = typeof(HttpMessageInvoker)
            .GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        return handler?.GetValue(client.Config.HttpClient);
    }
}
