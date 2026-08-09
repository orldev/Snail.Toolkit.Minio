using System.Text;
using Toolkit.Minio.Entities;
using Toolkit.Minio.Extensions;
using Toolkit.Minio.Tests.Infrastructure;

namespace Toolkit.Minio.Tests;

/// <summary>
/// Object operations exercised against a real Minio server.
/// </summary>
/// <remarks>
/// Assertions are made on stored and retrieved bytes rather than on outgoing requests, so a test passes only
/// when the object really round-trips.
/// </remarks>
[Collection(MinioContainerCollection.Name)]
public class MinioObjectOperationsTests(MinioContainerFixture fixture)
{
    private static readonly byte[] Payload = "0123456789abcdefghij"u8.ToArray();

    private IMinioClient Client => fixture.CreateClient();

    [Fact]
    public async Task PutStream_ThenDownload_RoundTripsContentExactly()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            using var source = new MemoryStream(Payload);
            var put = await client.PutStreamAsync(bucket, source, "application/octet-stream", "roundtrip");
            Assert.True(put.IsSuccess, put.ErrorMessage);

            var stat = await client.StatObjectAsync(bucket, "roundtrip");
            Assert.True(stat.IsSuccess, stat.ErrorMessage);
            Assert.Equal(Payload.Length, stat.Value!.Size);

            var download = await client.DownloadObjectAsync(bucket, "roundtrip");
            Assert.True(download.IsSuccess, download.ErrorMessage);

            await using var stream = download.Value!;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            Assert.Equal(Payload, buffer.ToArray());
        });
    }

    [Fact]
    public async Task DownloadObject_ReturnsStreamPositionedAtStart()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await client.PutStreamAsync(bucket, source, "application/octet-stream", "rewound");

            var download = await client.DownloadObjectAsync(bucket, "rewound");
            Assert.True(download.IsSuccess, download.ErrorMessage);

            await using var stream = download.Value!;

            // The whole point: a caller must be able to read the stream as handed over, without seeking first.
            Assert.Equal(0, stream.Position);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("0123456789abcdefghij", await reader.ReadToEndAsync());
        });
    }

    [Fact]
    public async Task DownloadObject_ToDestinationStream_CopiesContent()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await client.PutStreamAsync(bucket, source, "application/octet-stream", "copy");

            using var destination = new MemoryStream();
            var result = await client.DownloadObjectAsync(bucket, "copy", destination);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(Payload, destination.ToArray());
        });
    }

    [Fact]
    public async Task PutStream_UploadsOnlyTheRemainingBytes_OfAPartiallyReadStream()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            using var source = new MemoryStream(Payload);
            source.Position = 10; // "abcdefghij"

            var put = await client.PutStreamAsync(bucket, source, "text/plain", "partial");
            Assert.True(put.IsSuccess, put.ErrorMessage);

            var download = await client.DownloadObjectAsync(bucket, "partial");
            await using var stream = download.Value!;
            using var reader = new StreamReader(stream, Encoding.UTF8);

            Assert.Equal("abcdefghij", await reader.ReadToEndAsync());
        });
    }

    [Fact]
    public async Task PutStream_UploadsNonSeekableStream_WhenSizeIsGiven()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            await using var source = new ForwardOnlyStream(Payload);

            var put = await client.PutStreamAsync(
                bucket, source, "text/plain", "forward-only", objectSize: Payload.Length);

            Assert.True(put.IsSuccess, put.ErrorMessage);

            var download = await client.DownloadObjectAsync(bucket, "forward-only");
            await using var stream = download.Value!;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            Assert.Equal(Payload, buffer.ToArray());
        });
    }

    [Fact]
    public async Task PutStream_ReturnsNotSupported_ForNonSeekableStreamWithoutSize()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            await using var source = new ForwardOnlyStream(Payload);

            var put = await client.PutStreamAsync(bucket, source, "text/plain", "no-size");

            Assert.False(put.IsSuccess);
            Assert.Equal(MinioErrorType.NotSupported, put.ErrorType);

            // Nothing was written, because the size is resolved before any request goes out.
            var stat = await client.StatObjectAsync(bucket, "no-size");
            Assert.Equal(MinioErrorType.ObjectNotFound, stat.ErrorType);
        });
    }

    [Theory]
    [InlineData("report", "text/plain", "report.txt")]
    [InlineData("report.txt", "text/plain", "report.txt")]
    [InlineData("report.TXT", "text/plain", "report.TXT")]
    [InlineData("archive", "application/x-made-up", "archive")]
    public async Task PutStream_AppendsExtensionOnlyWhenMissing(
        string objectName,
        string contentType,
        string expectedStoredName)
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            using var source = new MemoryStream(Payload);
            var put = await client.PutStreamAsync(
                bucket, source, contentType, objectName, appendExtension: true);

            Assert.True(put.IsSuccess, put.ErrorMessage);

            var stat = await client.StatObjectAsync(bucket, expectedStoredName);
            Assert.True(stat.IsSuccess, $"expected the object to be stored as '{expectedStoredName}'");
        });
    }

    [Fact]
    public async Task PutStream_DoesNotAppendExtensionByDefault()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await client.PutStreamAsync(bucket, source, "text/plain", "plain-name");

            Assert.True((await client.StatObjectAsync(bucket, "plain-name")).IsSuccess);
            Assert.Equal(
                MinioErrorType.ObjectNotFound,
                (await client.StatObjectAsync(bucket, "plain-name.txt")).ErrorType);
        });
    }

    [Fact]
    public async Task PutStream_LetsCallerArgsOverrideDefaults()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await client.PutStreamAsync(
                bucket, source, "text/plain", "original", args: e => e.WithObject("overridden"));

            Assert.True((await client.StatObjectAsync(bucket, "overridden")).IsSuccess);
            Assert.Equal(
                MinioErrorType.ObjectNotFound,
                (await client.StatObjectAsync(bucket, "original")).ErrorType);
        });
    }

    [Fact]
    public async Task PutStream_GeneratesObjectName_WhenNoneGiven()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            using var source = new MemoryStream(Payload);
            var put = await client.PutStreamAsync(bucket, source, "text/plain");

            Assert.True(put.IsSuccess, put.ErrorMessage);
            Assert.False(string.IsNullOrWhiteSpace(put.Value!.ObjectName));
            Assert.True((await client.StatObjectAsync(bucket, put.Value.ObjectName)).IsSuccess);
        });
    }

    [Fact(Skip = "Blocked upstream in the Minio client, not the server: Minio 7.0.0 turns every HTTP 206 " +
                 "response into PartialContentException. Reproduced identically against server releases " +
                 "2023-01-31 and 2025-09-07, and for WithOffsetAndLength, WithLength, a raw Range header, and " +
                 "the file-based path alike; the same call without a range succeeds. This test is the tripwire " +
                 "— un-skip it when a fixed Minio client ships. The request this library builds is still " +
                 "checked in MinioRangeRequestTests.")]
    public async Task DownloadObjectWithOffsetAndLength_ReturnsTheRequestedRange()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await client.PutStreamAsync(bucket, source, "text/plain", "ranged");

            using var destination = new MemoryStream();
            var result = await client.DownloadObjectWithOffsetAndLengthAsync(
                bucket, "ranged", destination, offset: 10, length: 5);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal("abcde", Encoding.UTF8.GetString(destination.ToArray()));
        });
    }

    [Fact]
    public async Task RemoveObject_DeletesTheObject()
    {
        var client = Client;

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await client.PutStreamAsync(bucket, source, "text/plain", "doomed");
            Assert.True((await client.StatObjectAsync(bucket, "doomed")).IsSuccess);

            var remove = await client.RemoveObjectAsync(bucket, "doomed");
            Assert.True(remove.IsSuccess, remove.ErrorMessage);

            Assert.Equal(
                MinioErrorType.ObjectNotFound,
                (await client.StatObjectAsync(bucket, "doomed")).ErrorType);
        });
    }

    /// <summary>
    /// A forward-only stream, standing in for an HTTP request body.
    /// </summary>
    private sealed class ForwardOnlyStream(byte[] buffer) : Stream
    {
        private readonly MemoryStream _inner = new(buffer);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] target, int offset, int count) => _inner.Read(target, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] source, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
