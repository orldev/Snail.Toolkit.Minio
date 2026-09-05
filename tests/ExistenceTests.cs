using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// Asking whether something is there, and removing what may not be.
/// </summary>
[Collection(MinioContainerCollection.Name)]
public class ExistenceTests(MinioContainerFixture fixture)
{
    private static readonly byte[] Payload = "payload"u8.ToArray();

    [Fact]
    public async Task Exists_AnswersForAnObjectThatIsThere()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "here" });

            var exists = await fixture.Storage.ExistsAsync(bucket, "here");

            Assert.True(exists.IsSuccess, exists.Error?.ToString());
            Assert.True(exists.Value);
        });
    }

    /// <summary>
    /// A missing object is an answer of false, not a failure: only a question that could not be put to the
    /// server is a failure.
    /// </summary>
    [Fact]
    public async Task Exists_AnswersFalse_ForAnObjectThatIsNot()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var exists = await fixture.Storage.ExistsAsync(bucket, "absent");

            Assert.True(exists.IsSuccess, exists.Error?.ToString());
            Assert.False(exists.Value);
        });
    }

    [Fact]
    public async Task Exists_AnswersFalse_ForAnObjectInABucketThatIsNot()
    {
        var exists = await fixture.Storage.ExistsAsync("no-such-bucket-at-all", "absent");

        Assert.True(exists.IsSuccess, exists.Error?.ToString());
        Assert.False(exists.Value);
    }

    /// <summary>
    /// The documented promise: the caller asked for the object to be gone, and it is.
    /// </summary>
    [Fact]
    public async Task RemovingAnObjectThatWasNeverThere_Succeeds()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var removed = await fixture.Storage.RemoveAsync(bucket, "never-existed");

            Assert.True(removed.IsSuccess, removed.Error?.ToString());
        });
    }

    [Fact]
    public async Task RemovingFromABucketThatIsNotThere_IsReported()
    {
        var removed = await fixture.Storage.RemoveAsync("no-such-bucket-at-all", "object");

        Assert.False(removed.IsSuccess);
        Assert.Equal(StorageErrorKind.BucketNotFound, removed.Error!.Kind);
    }

    /// <summary>
    /// A name nobody could type an extension for should not acquire one: the fallback media type says
    /// nothing about the file, so appending <c>.bin</c> would be inventing information.
    /// </summary>
    [Fact]
    public async Task AppendExtension_AddsNothing_WhenTheMediaTypeIsTheFallback()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);

            var put = await fixture.Storage.PutAsync(
                bucket, source, new UploadOptions { Name = "nameless", AppendExtension = true });

            Assert.True(put.IsSuccess, put.Error?.ToString());
            Assert.Equal("nameless", put.Value.Name);
        });
    }
}
