using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// The containers objects live in.
/// </summary>
[Collection(MinioContainerCollection.Name)]
public class BucketTests(MinioContainerFixture fixture)
{
    private IBuckets Buckets => fixture.Buckets;

    [Fact]
    public async Task ABucket_IsCreated_Found_AndRemoved()
    {
        var bucket = $"toolkit-{Guid.NewGuid():N}";

        Assert.False((await Buckets.ExistsAsync(bucket)).Value);

        var created = await Buckets.CreateAsync(bucket);
        Assert.True(created.IsSuccess, created.Error?.ToString());

        Assert.True((await Buckets.ExistsAsync(bucket)).Value);

        var removed = await Buckets.RemoveAsync(bucket);
        Assert.True(removed.IsSuccess, removed.Error?.ToString());

        Assert.False((await Buckets.ExistsAsync(bucket)).Value);
    }

    /// <summary>
    /// Creating a bucket that is already there is what the caller asked for, so it succeeds.
    /// </summary>
    [Fact]
    public async Task CreatingABucketTwice_Succeeds()
    {
        var bucket = $"toolkit-{Guid.NewGuid():N}";

        try
        {
            Assert.True((await Buckets.CreateAsync(bucket)).IsSuccess);
            Assert.True((await Buckets.CreateAsync(bucket)).IsSuccess);
        }
        finally
        {
            await Buckets.RemoveAsync(bucket);
        }
    }

    [Fact]
    public async Task RemovingABucketThatStillHoldsObjects_IsRefused()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream("payload"u8.ToArray());
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "held" });

            var removed = await Buckets.RemoveAsync(bucket);

            Assert.False(removed.IsSuccess);
            Assert.NotEqual(StorageErrorKind.Unexpected, removed.Error!.Kind);
        });
    }

    [Fact]
    public async Task RemovingABucketThatIsNotThere_IsReported()
    {
        var removed = await Buckets.RemoveAsync("no-such-bucket-at-all");

        Assert.False(removed.IsSuccess);
        Assert.Equal(StorageErrorKind.BucketNotFound, removed.Error!.Kind);
    }

    [Fact]
    public async Task ABucketNameTheServerRefuses_IsReported()
    {
        var created = await Buckets.CreateAsync("A");

        Assert.False(created.IsSuccess);
        Assert.Equal(StorageErrorKind.InvalidBucketName, created.Error!.Kind);
    }
}
