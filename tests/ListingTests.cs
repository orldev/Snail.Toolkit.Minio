using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// Walking a bucket.
/// </summary>
/// <remarks>
/// The defect this replaces: there was no way to list anything through the port, so every caller who needed
/// a listing had to drop to the SDK — the one place the abstraction was supposed to hold.
/// </remarks>
[Collection(MinioContainerCollection.Name)]
public class ListingTests(MinioContainerFixture fixture)
{
    private static readonly byte[] Payload = "payload"u8.ToArray();

    [Fact]
    public async Task AListing_ReportsWhatWasStored()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await StoreAsync(bucket, "one.txt", "two.txt");

            var listed = await fixture.Storage.ListAsync(bucket);

            Assert.True(listed.IsSuccess, listed.Error?.ToString());
            Assert.Equal(["one.txt", "two.txt"], listed.Value.Objects.Select(o => o.Name).Order());
            Assert.All(listed.Value.Objects, stored => Assert.Equal(Payload.Length, stored.Size));
            Assert.False(listed.Value.HasMore);
        });
    }

    [Fact]
    public async Task APrefix_NarrowsTheListing()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await StoreAsync(bucket, "reports/q1.txt", "reports/q2.txt", "invoices/i1.txt");

            var listed = await fixture.Storage.ListAsync(bucket, new ListOptions { Prefix = "reports/" });

            Assert.Equal(2, listed.Value.Objects.Count);
            Assert.All(listed.Value.Objects, stored =>
                Assert.StartsWith("reports/", stored.Name, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task APageThatFills_CarriesACursorToTheNextOne()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await StoreAsync(bucket, "a.txt", "b.txt", "c.txt");

            var first = await fixture.Storage.ListAsync(bucket, new ListOptions { PageSize = 2 });

            Assert.True(first.Value.HasMore);
            Assert.Equal(2, first.Value.Objects.Count);

            var second = await fixture.Storage.ListAsync(
                bucket, new ListOptions { PageSize = 2, Cursor = first.Value.Cursor });

            Assert.False(second.Value.HasMore);
            Assert.Equal("c.txt", Assert.Single(second.Value.Objects).Name);
        });
    }

    /// <summary>
    /// A listing that does not descend rolls names up into prefixes, which is what a folder looks like in a
    /// store that has none.
    /// </summary>
    [Fact]
    public async Task AListingThatDoesNotDescend_ReportsPrefixes()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await StoreAsync(bucket, "top.txt", "reports/q1.txt", "reports/q2.txt");

            var listed = await fixture.Storage.ListAsync(bucket, new ListOptions { IsRecursive = false });

            Assert.Equal("top.txt", Assert.Single(listed.Value.Objects).Name);
            Assert.Equal("reports/", Assert.Single(listed.Value.Prefixes));
        });
    }

    [Fact]
    public async Task AWalk_YieldsEveryObjectOnce()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await StoreAsync(bucket, "a.txt", "b.txt", "c.txt");

            var seen = new List<string>();

            await foreach (var item in fixture.Storage.EnumerateAsync(bucket))
            {
                Assert.True(item.IsSuccess, item.Error?.ToString());
                seen.Add(item.Value.Name);
            }

            Assert.Equal(["a.txt", "b.txt", "c.txt"], seen.Order());
        });
    }

    /// <summary>
    /// A walk has nowhere to put a result once it has started, so the failure arrives as its last item.
    /// </summary>
    [Fact]
    public async Task AWalkOfAMissingBucket_EndsWithTheFailure()
    {
        var items = new List<StorageResult<StoredObject>>();

        await foreach (var item in fixture.Storage.EnumerateAsync("no-such-bucket-at-all"))
        {
            items.Add(item);
        }

        var last = Assert.Single(items);

        Assert.False(last.IsSuccess);
        Assert.Equal(StorageErrorKind.BucketNotFound, last.Error!.Kind);
    }

    [Fact]
    public async Task AListingOfAMissingBucket_Fails()
    {
        var listed = await fixture.Storage.ListAsync("no-such-bucket-at-all");

        Assert.False(listed.IsSuccess);
        Assert.Equal(StorageErrorKind.BucketNotFound, listed.Error!.Kind);
    }

    [Fact]
    public async Task APageSizeThatIsNotOne_IsRefused()
    {
        var listed = await fixture.Storage.ListAsync("bucket", new ListOptions { PageSize = 0 });

        Assert.Equal(StorageErrorKind.InvalidArgument, listed.Error!.Kind);
    }

    private async Task StoreAsync(string bucket, params string[] names)
    {
        foreach (var name in names)
        {
            using var source = new MemoryStream(Payload);

            var put = await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = name });

            Assert.True(put.IsSuccess, put.Error?.ToString());
        }
    }
}
