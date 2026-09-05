using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// Ranged reads, against the server they have to work on.
/// </summary>
/// <remarks>
/// The defect this replaces: Minio 7.0.0 turns every HTTP 206 answer into <c>PartialContentException</c>
/// and delivers no bytes, so ranged reads through the SDK's read path failed against every server they were
/// tried on. The adapter signs the request and issues it itself, which is why these tests can exist at all.
/// </remarks>
[Collection(MinioContainerCollection.Name)]
public class RangeReadTests(MinioContainerFixture fixture)
{
    private static readonly byte[] Payload = "0123456789abcdefghij"u8.ToArray();

    [Fact]
    public async Task DownloadRangeTo_ReturnsTheRequestedRange()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "ranged" });

            using var destination = new MemoryStream();
            var result = await fixture.Storage.DownloadRangeToAsync(
                bucket, "ranged", destination, offset: 10, length: 5);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal("abcde", Encoding.UTF8.GetString(destination.ToArray()));
        });
    }

    [Fact]
    public async Task DownloadRangeTo_ReportsTheSizeOfTheWholeObject()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "ranged" });

            using var destination = new MemoryStream();
            var result = await fixture.Storage.DownloadRangeToAsync(
                bucket, "ranged", destination, offset: 0, length: 4);

            Assert.Equal(Payload.Length, result.Value.Size);
            Assert.Equal(4, destination.Length);
        });
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(0, 0)]
    [InlineData(0, -5)]
    public async Task DownloadRangeTo_RefusesARangeThatCannotBeRead(long offset, long length)
    {
        using var destination = new MemoryStream();

        var result = await fixture.Storage.DownloadRangeToAsync(
            "any-bucket", "any-object", destination, offset, length);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageErrorKind.InvalidArgument, result.Error!.Kind);
    }
}

/// <summary>
/// The range request the adapter builds, at the wire level.
/// </summary>
/// <remarks>
/// The one assertion a live server cannot make: a 64-bit range needs an object larger than 2 GB to mean
/// anything server-side. What can be checked cheaply is that the offset survives the journey into the
/// <c>Range</c> header rather than overflowing on the way.
/// </remarks>
public class RangeRequestTests
{
    [Fact]
    public async Task DownloadRangeTo_EmitsA64BitRangeHeader()
    {
        const long offset = 3_000_000_000;
        const long length = 1_024;

        using var transport = new RecordingTransport("payload"u8.ToArray());

        using var provider = MinioContainerFixture.Provider(
            MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key"),
            services => services.Replace(ServiceDescriptor.Singleton<MinioTransport>(transport)));

        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadRangeToAsync("bucket", "object", destination, offset, length);

        Assert.True(result.IsSuccess, result.Error?.ToString());

        var request = Assert.Single(transport.Requests);

        Assert.True(request.Headers.TryGetValues("Range", out var range));
        Assert.Equal($"bytes={offset}-{offset + length - 1}", Assert.Single(range));
    }
}
