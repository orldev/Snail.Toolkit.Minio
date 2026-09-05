using System.Net;
using System.Text;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// URLs handed to whoever should do the transfer instead of this application.
/// </summary>
[Collection(MinioContainerCollection.Name)]
public class SignedUrlTests(MinioContainerFixture fixture)
{
    private static readonly byte[] Payload = "0123456789"u8.ToArray();

    [Fact]
    public async Task ASignedReadUrl_ReadsTheObject()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "shared" });

            var signed = await fixture.Storage.SignedUrlAsync(bucket, "shared");
            Assert.True(signed.IsSuccess, signed.Error?.ToString());

            using var browser = new HttpClient();
            var body = await browser.GetByteArrayAsync(signed.Value);

            Assert.Equal(Payload, body);
        });
    }

    [Fact]
    public async Task ASignedUploadUrl_AcceptsTheObject()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            var signed = await fixture.Storage.SignedUploadUrlAsync(bucket, "uploaded");
            Assert.True(signed.IsSuccess, signed.Error?.ToString());

            using var browser = new HttpClient();
            using var response = await browser.PutAsync(signed.Value, new ByteArrayContent(Payload));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var destination = new MemoryStream();
            await fixture.Storage.DownloadToAsync(bucket, "uploaded", destination);

            Assert.Equal(Encoding.UTF8.GetString(Payload), Encoding.UTF8.GetString(destination.ToArray()));
        });
    }

    /// <summary>
    /// Seven days is the ceiling S3 signing allows, and a URL that outlives its use is a credential nobody
    /// is watching.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8 * 24 * 60 * 60)]
    public async Task ALifetimeOutsideWhatSigningAllows_IsRefused(int seconds)
    {
        var signed = await fixture.Storage.SignedUrlAsync(
            "bucket", "object", TimeSpan.FromSeconds(seconds));

        Assert.False(signed.IsSuccess);
        Assert.Equal(StorageErrorKind.InvalidArgument, signed.Error!.Kind);
    }
}
