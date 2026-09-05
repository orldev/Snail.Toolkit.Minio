using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// User metadata, written and read back.
/// </summary>
/// <remarks>
/// The defect this replaces: metadata could be written and never read, and a value carrying anything but
/// US-ASCII failed the upload with a message about the connection, which is not where the problem was.
/// </remarks>
[Collection(MinioContainerCollection.Name)]
public class MetadataTests(MinioContainerFixture fixture)
{
    private static readonly byte[] Payload = "payload"u8.ToArray();

    [Fact]
    public async Task MetadataWrittenWithAnObject_ComesBackWithIt()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);

            var put = await fixture.Storage.PutAsync(bucket, source, new UploadOptions
            {
                Name = "described",
                MediaType = "text/plain",
                Metadata = new Dictionary<string, string> { ["author"] = "orldev", ["build"] = "42" }
            });

            Assert.True(put.IsSuccess, put.Error?.ToString());
            Assert.Equal("orldev", put.Value.Metadata["author"]);

            var stat = await fixture.Storage.StatAsync(bucket, "described");
            Assert.Equal("orldev", stat.Value.Metadata["author"]);
            Assert.Equal("42", stat.Value.Metadata["build"]);

            var read = await fixture.Storage.GetAsync(bucket, "described");
            await using var content = read.Value;

            Assert.Equal("orldev", content.Metadata.Metadata["author"]);
            Assert.Equal("42", content.Metadata.Metadata["build"]);
        });
    }

    /// <summary>
    /// The media type is not metadata a caller stored, and reporting it as such would invent a name nobody
    /// wrote.
    /// </summary>
    [Fact]
    public async Task WhatTheProtocolAdds_IsNotReportedAsMetadata()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions
            {
                Name = "plain",
                MediaType = "text/plain"
            });

            var stat = await fixture.Storage.StatAsync(bucket, "plain");

            Assert.Empty(stat.Value.Metadata);
            Assert.Equal("text/plain", stat.Value.MediaType);
        });
    }

    /// <summary>
    /// Header values are US-ASCII. Anything else has to be refused where the caller can see why, not passed
    /// to a client that reports it as a connection problem.
    /// </summary>
    [Theory]
    [InlineData("Иванов")]
    [InlineData("🚀")]
    [InlineData("café")]
    public async Task AMetadataValueThatIsNotAscii_IsRefused(string value)
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);

            var put = await fixture.Storage.PutAsync(bucket, source, new UploadOptions
            {
                Name = "rejected",
                Metadata = new Dictionary<string, string> { ["author"] = value }
            });

            Assert.False(put.IsSuccess);
            Assert.Equal(StorageErrorKind.InvalidArgument, put.Error!.Kind);
            Assert.Contains("author", put.Error.Message, StringComparison.Ordinal);

            var stat = await fixture.Storage.StatAsync(bucket, "rejected");
            Assert.Equal(StorageErrorKind.ObjectNotFound, stat.Error!.Kind);
        });
    }
}
