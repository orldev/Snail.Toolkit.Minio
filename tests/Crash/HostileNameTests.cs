using System.Text;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Crash;

/// <summary>
/// Object names that survive an upload but may not survive being put into a signed URL.
/// </summary>
/// <remarks>
/// The read path signs a URL and then hands the string to <see cref="Uri"/>. That is where a name can be
/// lost: percent-encoding a signature was computed over can be normalised away, a dot segment can be
/// collapsed, and a plus can change meaning. Any of those produces a signature the server rejects, or a
/// request for the wrong object — while the upload that created the name succeeded.
/// </remarks>
[Collection(MinioContainerCollection.Name)]
public class HostileNameTests(MinioContainerFixture fixture)
{
    private static readonly byte[] Payload = "0123456789abcdefghij"u8.ToArray();

    [Theory]
    [InlineData("file with spaces.txt")]
    [InlineData("файл-кириллица.txt")]
    [InlineData("emoji-🚀-name.txt")]
    [InlineData("plus+sign.txt")]
    [InlineData("percent%20literal.txt")]
    [InlineData("hash#fragment.txt")]
    [InlineData("question?query.txt")]
    [InlineData("ampersand&and.txt")]
    [InlineData("nested/prefix/object.txt")]
    [InlineData("dot/../segment.txt")]
    [InlineData("tilde~and'quote.txt")]
    [InlineData("semi;colon,comma.txt")]
    [InlineData("равно=знак.txt")]
    public async Task AnObjectStoredUnderAHostileName_CanBeReadBack(string name)
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            var put = await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = name });
            Assert.True(put.IsSuccess, $"upload: {put.Error}");

            using var destination = new MemoryStream();
            var read = await fixture.Storage.DownloadToAsync(bucket, name, destination);

            Assert.True(read.IsSuccess, $"read: {read.Error}");
            Assert.Equal(Payload, destination.ToArray());
        });
    }

    /// <summary>
    /// A long key is signed, encoded and sent like any other.
    /// </summary>
    /// <remarks>
    /// Two hundred characters, because MinIO stores a name segment as a directory entry and rejects one
    /// past the filesystem's own limit. Where that limit falls is the server's business; that the library
    /// carries the name there and back is this test's.
    /// </remarks>
    [Fact]
    public async Task AnObjectStoredUnderALongName_CanBeReadBack()
    {
        var name = new string('n', 200) + ".txt";

        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            var put = await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = name });
            Assert.True(put.IsSuccess, $"upload: {put.Error}");

            using var destination = new MemoryStream();
            var read = await fixture.Storage.DownloadToAsync(bucket, name, destination);

            Assert.True(read.IsSuccess, $"read: {read.Error}");
        });
    }

    /// <summary>
    /// Past the server's own limit the answer has to be classified, and has to carry what the server said.
    /// </summary>
    /// <remarks>
    /// Anything but <see cref="StorageErrorKind.Unexpected"/>: the server refused the name and explained
    /// itself, so a caller reading the kind has to learn that this was about the request rather than about
    /// the library falling over.
    /// </remarks>
    [Theory]
    [InlineData(2000)]
    [InlineData(400)]
    public async Task AnObjectNameBeyondTheServerLimit_IsReported(int length)
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);

            var put = await fixture.Storage.PutAsync(
                bucket, source, new UploadOptions { Name = new string('n', length) });

            Assert.False(put.IsSuccess);
            Assert.NotEqual(StorageErrorKind.Unexpected, put.Error!.Kind);
            Assert.False(string.IsNullOrWhiteSpace(put.Error.Message));
        });
    }

    /// <summary>
    /// An empty object is a legitimate object: zero bytes stored, zero bytes read, and a size that says so.
    /// </summary>
    [Fact]
    public async Task AnEmptyObject_RoundTrips()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream([]);
            var put = await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "empty" });
            Assert.True(put.IsSuccess, $"upload: {put.Error}");

            using var destination = new MemoryStream();
            var read = await fixture.Storage.DownloadToAsync(bucket, "empty", destination);

            Assert.True(read.IsSuccess, $"read: {read.Error}");
            Assert.Equal(0, destination.Length);
            Assert.Equal(0, read.Value.Size);
        });
    }

    /// <summary>
    /// A range that starts past the end of the object: the server answers 416, and the caller has to be
    /// told which argument was wrong rather than being handed an empty success.
    /// </summary>
    [Fact]
    public async Task ARangeThatStartsPastTheEnd_IsReported()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "short" });

            using var destination = new MemoryStream();
            var read = await fixture.Storage.DownloadRangeToAsync(
                bucket, "short", destination, offset: 1_000, length: 10);

            Assert.False(read.IsSuccess);
            Assert.Equal(StorageErrorKind.InvalidArgument, read.Error!.Kind);
            Assert.Equal(0, destination.Length);
        });
    }

    /// <summary>
    /// A range that runs past the end is not an error: the server sends what it has, and the caller has to
    /// receive exactly that.
    /// </summary>
    [Fact]
    public async Task ARangeThatRunsPastTheEnd_ReturnsWhatExists()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "short" });

            using var destination = new MemoryStream();
            var read = await fixture.Storage.DownloadRangeToAsync(
                bucket, "short", destination, offset: 15, length: 1_000);

            Assert.True(read.IsSuccess, $"read: {read.Error}");
            Assert.Equal("fghij", Encoding.UTF8.GetString(destination.ToArray()));
        });
    }

    /// <summary>
    /// The stream handed over by <c>GetAsync</c> has to be the connection, not a copy of the object in
    /// memory. A seekable stream would mean the whole object was buffered before the caller saw a byte.
    /// </summary>
    [Fact]
    public async Task Get_HandsOverTheConnection_NotACopyInMemory()
    {
        await fixture.WithBucketAsync(async bucket =>
        {
            using var source = new MemoryStream(Payload);
            await fixture.Storage.PutAsync(bucket, source, new UploadOptions { Name = "streamed" });

            var read = await fixture.Storage.GetAsync(bucket, "streamed");
            await using var content = read.Value;

            Assert.False(content.Stream.CanSeek);
            Assert.Throws<NotSupportedException>(() => content.Stream.Length);
        });
    }
}
