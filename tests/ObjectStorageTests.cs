using System.Text;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// Object operations exercised against a real Minio server.
/// </summary>
/// <remarks>
/// Assertions are made on stored and retrieved bytes rather than on outgoing requests, so a test passes only
/// when the object really round-trips.
/// </remarks>
[Collection(MinioContainerCollection.Name)]
public class ObjectStorageTests(MinioContainerFixture fixture)
{
    private static readonly byte[] Payload = "0123456789abcdefghij"u8.ToArray();

    [Fact]
    public async Task Put_ThenGet_RoundTripsContentExactly()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            var put = await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "roundtrip" });
            Assert.True(put.IsSuccess, put.Error?.ToString());

            var read = await fixture.Storage.GetAsync(bucket, "roundtrip");
            Assert.True(read.IsSuccess, read.Error?.ToString());

            await using var content = read.Value;
            using var buffer = new MemoryStream();
            await content.Stream.CopyToAsync(buffer);

            Assert.Equal(Payload, buffer.ToArray());
            Assert.Equal(Payload.Length, content.Metadata.Size);
        });
    }

    [Fact]
    public async Task Get_DescribesTheObjectItStreams()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(
                bucket, source, new UploadOptions { Name = "described", MediaType = "text/plain" });

            var read = await fixture.Storage.GetAsync(bucket, "described");
            await using var content = read.Value;

            Assert.Equal(bucket, content.Metadata.Bucket);
            Assert.Equal("described", content.Metadata.Name);
            Assert.Equal("text/plain", content.Metadata.MediaType);
            Assert.False(string.IsNullOrWhiteSpace(content.Metadata.ETag));
            Assert.NotNull(content.Metadata.LastModified);
        });
    }

    [Fact]
    public async Task DownloadTo_CopiesContent()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "copy" });

            using var destination = new MemoryStream();
            var result = await fixture.Storage.DownloadToAsync(bucket, "copy", destination);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal(Payload, destination.ToArray());
        });
    }

    [Fact]
    public async Task DownloadTo_LeavesTheDestinationUntouched_WhenTheObjectIsMissing()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var destination = new MemoryStream();
            await destination.WriteAsync("already here"u8.ToArray());
            var written = destination.Length;

            var result = await fixture.Storage.DownloadToAsync(bucket, "no-such-object", destination);

            Assert.False(result.IsSuccess);
            Assert.Equal(StorageErrorKind.ObjectNotFound, result.Error!.Kind);
            Assert.Equal(written, destination.Length);
        });
    }

    [Fact]
    public async Task Put_UploadsOnlyTheRemainingBytes_OfAPartiallyReadStream()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload) { Position = 10 };

            var put = await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "partial" });
            Assert.True(put.IsSuccess, put.Error?.ToString());

            using var destination = new MemoryStream();
            await fixture.Storage.DownloadToAsync(bucket, "partial", destination);

            Assert.Equal("abcdefghij", Encoding.UTF8.GetString(destination.ToArray()));
        });
    }

    [Fact]
    public async Task Put_UploadsNonSeekableStream_WhenTheSizeIsGiven()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await using var source = new ForwardOnlyStream(Payload);

            var put = await fixture.Storage.PutAsync(
                bucket, source, new UploadOptions { Name = "forward-only", Size = Payload.Length });

            Assert.True(put.IsSuccess, put.Error?.ToString());

            using var destination = new MemoryStream();
            await fixture.Storage.DownloadToAsync(bucket, "forward-only", destination);

            Assert.Equal(Payload, destination.ToArray());
        });
    }

    [Fact]
    public async Task Put_ReportsNotSupported_ForANonSeekableStreamWithoutASize()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            await using var source = new ForwardOnlyStream(Payload);

            var put = await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "no-size" });

            Assert.False(put.IsSuccess);
            Assert.Equal(StorageErrorKind.NotSupported, put.Error!.Kind);

            var stat = await fixture.Storage.StatAsync(bucket, "no-size");
            Assert.Equal(StorageErrorKind.ObjectNotFound, stat.Error!.Kind);
        });
    }

    [Theory]
    [InlineData("report", "text/plain", "report.txt")]
    [InlineData("report.txt", "text/plain", "report.txt")]
    [InlineData("report.TXT", "text/plain", "report.TXT")]
    [InlineData("archive", "application/x-made-up", "archive")]
    public async Task Put_AppendsAnExtensionOnlyWhenItIsMissing(string name, string mediaType, string stored)
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);

            var put = await fixture.Storage.PutAsync(
                bucket,
                source,
                new UploadOptions { Name = name, MediaType = mediaType, AppendExtension = true });

            Assert.True(put.IsSuccess, put.Error?.ToString());
            Assert.Equal(stored, put.Value.Name);
            Assert.True((await fixture.Storage.StatAsync(bucket, stored)).IsSuccess);
        });
    }

    /// <summary>
    /// The defect this replaces: a media type that looked like an extension reached a lookup that threw
    /// <see cref="ArgumentException"/>, so a caller's typo escaped a method documented to answer with a
    /// result rather than an exception.
    /// </summary>
    [Theory]
    [InlineData(".txt")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Put_AnswersWithAResult_ForAMediaTypeThatIsNotOne(string mediaType)
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);

            var put = await fixture.Storage.PutAsync(
                bucket,
                source,
                new UploadOptions { Name = "typo", MediaType = mediaType, AppendExtension = true });

            Assert.True(put.IsSuccess, put.Error?.ToString());
            Assert.Equal("typo", put.Value.Name);
        });
    }

    [Fact]
    public async Task Put_GeneratesAName_WhenNoneIsGiven()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            var put = await fixture.Storage.PutAsync(bucket, source);

            Assert.True(put.IsSuccess, put.Error?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(put.Value.Name));
            Assert.True((await fixture.Storage.StatAsync(bucket, put.Value.Name)).IsSuccess);
        });
    }

    [Fact]
    public async Task Stat_DescribesTheObject()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(
                bucket, source, new UploadOptions { Name = "described", MediaType = "text/plain" });

            var stat = await fixture.Storage.StatAsync(bucket, "described");

            Assert.True(stat.IsSuccess, stat.Error?.ToString());
            Assert.Equal(Payload.Length, stat.Value.Size);
            Assert.Equal("text/plain", stat.Value.MediaType);
        });
    }

    [Fact]
    public async Task Remove_DeletesTheObject()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "doomed" });

            var removed = await fixture.Storage.RemoveAsync(bucket, "doomed");

            Assert.True(removed.IsSuccess, removed.Error?.ToString());
            Assert.Equal(
                StorageErrorKind.ObjectNotFound,
                (await fixture.Storage.StatAsync(bucket, "doomed")).Error!.Kind);
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
