using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// The rules by which a bucket removes objects on its own.
/// </summary>
[Collection(MinioContainerCollection.Name)]
public class ExpiryTests(MinioContainerFixture fixture)
{
    private IBuckets Buckets => fixture.Buckets;

    private static ExpiryRule Rule(string id, int days, string prefix = "") =>
        new() { Id = id, Days = days, Prefix = prefix };

    /// <summary>
    /// A bucket that expires nothing is a bucket in a perfectly ordinary state. The server reports it by
    /// refusing the request, and a caller should not have to know that.
    /// </summary>
    [Fact]
    public async Task ABucketWithNoRules_ReportsNone()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var read = await Buckets.GetExpiryAsync(bucket);

            Assert.True(read.IsSuccess, read.Error?.ToString());
            Assert.Empty(read.Value);
        });
    }

    [Fact]
    public async Task RulesAreSet_AndReadBack()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var set = await Buckets.SetExpiryAsync(bucket, [Rule("stage", 1, "tmp/"), Rule("audit", 30, "logs/")]);
            Assert.True(set.IsSuccess, set.Error?.ToString());

            var read = await Buckets.GetExpiryAsync(bucket);
            Assert.True(read.IsSuccess, read.Error?.ToString());

            var stage = Assert.Single(read.Value, rule => rule.Id == "stage");
            Assert.Equal(1, stage.Days);
            Assert.Equal("tmp/", stage.Prefix);
            Assert.True(stage.IsEnabled);

            var audit = Assert.Single(read.Value, rule => rule.Id == "audit");
            Assert.Equal(30, audit.Days);
            Assert.Equal("logs/", audit.Prefix);
        });
    }

    /// <summary>
    /// The whole point of taking the complete set: the server keeps what it was last given and nothing
    /// else. A caller that believed it was adding a rule would have deleted the other one here.
    /// </summary>
    [Fact]
    public async Task SettingRules_ReplacesTheOnesThatWereThere()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await Buckets.SetExpiryAsync(bucket, [Rule("first", 1, "one/"), Rule("second", 2, "two/")]);

            await Buckets.SetExpiryAsync(bucket, [Rule("third", 3, "three/")]);

            var read = await Buckets.GetExpiryAsync(bucket);
            Assert.Equal("third", Assert.Single(read.Value).Id);
        });
    }

    [Fact]
    public async Task ARuleThatIsTurnedOff_ReadsBackAsTurnedOff()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await Buckets.SetExpiryAsync(bucket, [new ExpiryRule { Id = "paused", Days = 7, IsEnabled = false }]);

            var read = await Buckets.GetExpiryAsync(bucket);

            Assert.False(Assert.Single(read.Value).IsEnabled);
        });
    }

    [Fact]
    public async Task ClearingTheRules_LeavesTheBucketWithNone()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await Buckets.SetExpiryAsync(bucket, [Rule("stage", 1, "tmp/")]);

            var cleared = await Buckets.SetExpiryAsync(bucket, []);
            Assert.True(cleared.IsSuccess, cleared.Error?.ToString());

            var read = await Buckets.GetExpiryAsync(bucket);
            Assert.True(read.IsSuccess, read.Error?.ToString());
            Assert.Empty(read.Value);
        });
    }

    [Fact]
    public async Task ARuleWithNoName_IsRefusedBeforeItIsSent()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var set = await Buckets.SetExpiryAsync(bucket, [Rule(" ", 1)]);

            Assert.False(set.IsSuccess);
            Assert.Equal(StorageErrorKind.InvalidArgument, set.Error!.Kind);
        });
    }

    [Fact]
    public async Task ARuleThatExpiresAfterNoDays_IsRefusedBeforeItIsSent()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var set = await Buckets.SetExpiryAsync(bucket, [Rule("stage", 0, "tmp/")]);

            Assert.False(set.IsSuccess);
            Assert.Equal(StorageErrorKind.InvalidArgument, set.Error!.Kind);
        });
    }

    /// <summary>Two rules under one name are one rule, and the caller meant two.</summary>
    [Fact]
    public async Task TwoRulesSharingAName_AreRefused()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var set = await Buckets.SetExpiryAsync(bucket, [Rule("stage", 1, "one/"), Rule("stage", 2, "two/")]);

            Assert.False(set.IsSuccess);
            Assert.Equal(StorageErrorKind.InvalidArgument, set.Error!.Kind);
        });
    }

    /// <summary>
    /// The server accepts a lifecycle for a bucket that does not exist and keeps nothing, so a typo would
    /// otherwise pass as success and be found only by noticing, much later, that nothing was cleaned up.
    /// </summary>
    [Fact]
    public async Task RulesForABucketThatIsNotThere_AreRefused()
    {
        var set = await Buckets.SetExpiryAsync("no-such-bucket-at-all", [Rule("stage", 1, "tmp/")]);

        Assert.False(set.IsSuccess);
        Assert.Equal(StorageErrorKind.BucketNotFound, set.Error!.Kind);
    }
}
