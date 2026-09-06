using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// Copying an object without its bytes travelling through the caller.
/// </summary>
[Collection(MinioContainerCollection.Name)]
public class CopyTests(MinioContainerFixture fixture)
{
    private IObjectStorage Storage => fixture.Storage;

    private static Stream Content(string text) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

    private async Task<string> ReadAsync(string bucket, string name)
    {
        var read = await Storage.GetAsync(bucket, name);
        Assert.True(read.IsSuccess, read.Error?.ToString());

        using var content = read.Value;
        using var reader = new StreamReader(content.Stream);

        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task AnObjectIsCopied_AndBothNamesRead()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await Storage.PutAsync(bucket, Content("staged"), new UploadOptions { Name = "tmp/staged" });

            var copied = await Storage.CopyAsync(bucket, "tmp/staged", bucket, "kept");

            Assert.True(copied.IsSuccess, copied.Error?.ToString());
            Assert.Equal("staged", await ReadAsync(bucket, "kept"));
            Assert.Equal("staged", await ReadAsync(bucket, "tmp/staged"));
        });
    }

    [Fact]
    public async Task AnObjectIsCopied_BetweenBuckets()
    {
        await fixture.WithBucketAsync(async source =>
        {
            await fixture.WithBucketAsync(async target =>
            {
                await Storage.PutAsync(source, Content("moved"), new UploadOptions { Name = "report.txt" });

                var copied = await Storage.CopyAsync(source, "report.txt", target, "archived/report.txt");

                Assert.True(copied.IsSuccess, copied.Error?.ToString());
                Assert.Equal("moved", await ReadAsync(target, "archived/report.txt"));
            });
        });
    }

    /// <summary>A copy is a write, and a write to a name that is taken replaces what was there.</summary>
    [Fact]
    public async Task CopyingOntoAName_ThatIsTaken_ReplacesIt()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await Storage.PutAsync(bucket, Content("new"), new UploadOptions { Name = "source" });
            await Storage.PutAsync(bucket, Content("old"), new UploadOptions { Name = "target" });

            Assert.True((await Storage.CopyAsync(bucket, "source", bucket, "target")).IsSuccess);

            Assert.Equal("new", await ReadAsync(bucket, "target"));
        });
    }

    [Fact]
    public async Task CopyingSomethingThatIsNotThere_IsReported()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var copied = await Storage.CopyAsync(bucket, "never-written", bucket, "target");

            Assert.False(copied.IsSuccess);
            Assert.Equal(StorageErrorKind.ObjectNotFound, copied.Error!.Kind);
        });
    }

    [Fact]
    public async Task CopyingIntoABucketThatIsNotThere_IsReported()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await Storage.PutAsync(bucket, Content("payload"), new UploadOptions { Name = "source" });

            var copied = await Storage.CopyAsync(bucket, "source", "no-such-bucket-at-all", "target");

            Assert.False(copied.IsSuccess);
            Assert.NotEqual(StorageErrorKind.Unexpected, copied.Error!.Kind);
        });
    }

    [Theory]
    [InlineData("", "name", "bucket", "name")]
    [InlineData("bucket", "", "bucket", "name")]
    [InlineData("bucket", "name", "", "name")]
    [InlineData("bucket", "name", "bucket", "")]
    public async Task CopyingWithSomethingLeftBlank_IsRefusedOutright(
        string sourceBucket,
        string sourceName,
        string targetBucket,
        string targetName)
        => await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Storage.CopyAsync(sourceBucket, sourceName, targetBucket, targetName));
}
