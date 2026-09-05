using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Crash;

/// <summary>
/// What the library holds on to, and for how long.
/// </summary>
/// <remarks>
/// A streaming read hands the caller a connection. That is the point of it, and also the risk: a caller who
/// forgets to dispose is holding a socket, and enough of them stall every later read.
/// </remarks>
[Collection(MinioContainerCollection.Name)]
public class ResourceCrashTests(MinioContainerFixture fixture)
{
    private static readonly byte[] Payload = "0123456789abcdefghij"u8.ToArray();

    /// <summary>
    /// Breaks the pool on purpose: one connection allowed, one read left undisposed, and the next read has
    /// nothing to run on. This is the cost of streaming, and it has to be reported as a timeout rather than
    /// hanging forever.
    /// </summary>
    [Fact]
    public async Task AnUndisposedReadHoldsItsConnection()
    {
        var settings = MinioContainerFixture.Settings(fixture.Endpoint, fixture.AccessKey, fixture.SecretKey);
        settings["Minio:MaxConnectionsPerServer"] = "1";
        settings["Minio:Timeout"] = "00:00:02";

        using var provider = MinioContainerFixture.Provider(settings);
        var storage = provider.GetRequiredService<IObjectStorage>();

        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await storage.PutAsync(bucket, source, new UploadOptions { Name = "held" });

            var leaked = await storage.GetAsync(bucket, "held");
            Assert.True(leaked.IsSuccess, leaked.Error?.ToString());

            var blocked = await storage.GetAsync(bucket, "held");

            Assert.False(blocked.IsSuccess);
            Assert.Equal(StorageErrorKind.Timeout, blocked.Error!.Kind);

            await leaked.Value.DisposeAsync();

            var freed = await storage.GetAsync(bucket, "held");
            Assert.True(freed.IsSuccess, freed.Error?.ToString());

            await freed.Value.DisposeAsync();
        });
    }

    /// <summary>
    /// Breaks the handed-over stream: reading it after it was disposed has to fail loudly rather than
    /// return zero bytes and look like an empty object.
    /// </summary>
    [Fact]
    public async Task ReadingContentAfterDisposingIt_Raises()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "closed" });

            var read = await fixture.Storage.GetAsync(bucket, "closed");
            var content = read.Value;

            await content.DisposeAsync();

            await Assert.ThrowsAnyAsync<ObjectDisposedException>(
                async () => await content.Stream.ReadExactlyAsync(new byte[16]));
        });
    }

    /// <summary>
    /// Breaks disposal ordering: disposing the same content twice is what a <c>using</c> inside a
    /// <c>try/finally</c> does, and it has to stay harmless.
    /// </summary>
    [Fact]
    public async Task DisposingContentTwice_IsHarmless()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "twice" });

            var read = await fixture.Storage.GetAsync(bucket, "twice");
            var content = read.Value;

            await content.DisposeAsync();
            await content.DisposeAsync();
            content.Dispose();
        });
    }

    /// <summary>
    /// Breaks the failure path: two hundred reads that all fail must not leave two hundred responses
    /// waiting for a garbage collection to release their sockets.
    /// </summary>
    [Fact]
    public async Task ManyFailedReads_DoNotStarveThePool()
    {
        var settings = MinioContainerFixture.Settings(fixture.Endpoint, fixture.AccessKey, fixture.SecretKey);
        settings["Minio:MaxConnectionsPerServer"] = "2";
        settings["Minio:Timeout"] = "00:00:05";

        using var provider = MinioContainerFixture.Provider(settings);
        var storage = provider.GetRequiredService<IObjectStorage>();

        await fixture.WithBucketAsync(async bucket =>
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var missing = await storage.GetAsync(bucket, $"absent-{attempt}");

                Assert.Equal(StorageErrorKind.ObjectNotFound, missing.Error!.Kind);
            }

            using var source = new MemoryStream(Payload);
            var stored = await storage.PutAsync(bucket, source, new UploadOptions { Name = "after" });

            Assert.True(stored.IsSuccess, stored.Error?.ToString());
        });
    }
}
