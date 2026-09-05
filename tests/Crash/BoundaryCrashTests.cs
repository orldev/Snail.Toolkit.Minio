using Microsoft.Extensions.DependencyInjection.Extensions;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Crash;

/// <summary>
/// Values at the edges of what the types allow.
/// </summary>
/// <remarks>
/// Every method here is documented to answer with a result. A crash-test passes when a hostile argument
/// produces a failure a caller can read, and fails when it produces an exception instead.
/// </remarks>
public class BoundaryCrashTests
{
    /// <summary>
    /// Breaks the range arithmetic: the last byte is computed as offset + length - 1, which wraps past
    /// <see cref="long.MaxValue"/> and asks for a range that ends before it starts.
    /// </summary>
    [Theory]
    [InlineData(long.MaxValue, long.MaxValue)]
    [InlineData(long.MaxValue, 2)]
    [InlineData(9_223_372_036_854_775_806, 10)]
    public async Task DownloadRangeTo_SurvivesARangeThatOverflows(long offset, long length)
    {
        using var provider = Provider();
        using var destination = new MemoryStream();

        var result = await provider.GetRequiredService<IObjectStorage>()
            .DownloadRangeToAsync("bucket", "object", destination, offset, length);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageErrorKind.InvalidArgument, result.Error!.Kind);
    }

    /// <summary>
    /// Breaks the upload size check: a stated size larger than the stream has bytes leaves the request
    /// waiting for content that never comes.
    /// </summary>
    [Fact]
    public async Task Put_SurvivesASizeLargerThanTheStream()
    {
        using var provider = Provider();
        using var content = new MemoryStream("short"u8.ToArray());

        var result = await provider.GetRequiredService<IObjectStorage>()
            .PutAsync("bucket", content, new UploadOptions { Name = "object", Size = long.MaxValue });

        Assert.False(result.IsSuccess);
    }

    /// <summary>
    /// Breaks the metadata path: a header name carrying a line break is the classic response-splitting
    /// payload, and it must never reach the wire.
    /// </summary>
    [Theory]
    [InlineData("X-Injected\r\nX-Evil", "value")]
    [InlineData("X-Injected", "value\r\nX-Evil: 1")]
    [InlineData("", "value")]
    public async Task Put_SurvivesMetadataThatTriesToInjectAHeader(string key, string value)
    {
        using var provider = Provider();
        using var content = new MemoryStream("payload"u8.ToArray());

        var result = await provider.GetRequiredService<IObjectStorage>().PutAsync(
            "bucket",
            content,
            new UploadOptions
            {
                Name = "object",
                Metadata = new Dictionary<string, string> { [key] = value }
            });

        Assert.False(result.IsSuccess);
    }

    /// <summary>
    /// Breaks argument handling: a name that is null, empty or blank is a mistake in the calling code, and
    /// has to be reported as one rather than reaching the server.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Stat_RefusesANameThatIsNotOne(string? name)
    {
        using var provider = Provider();
        var storage = provider.GetRequiredService<IObjectStorage>();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.StatAsync("bucket", name!));
    }

    /// <summary>
    /// Breaks the upload path with the stream every web request hands over: forward-only, of unknown
    /// length, and already at its end.
    /// </summary>
    [Fact]
    public async Task Put_SurvivesAnEmptyForwardOnlyStream()
    {
        using var provider = Provider();
        await using var content = new ForwardOnly([]);

        var result = await provider.GetRequiredService<IObjectStorage>()
            .PutAsync("bucket", content, new UploadOptions { Name = "object" });

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageErrorKind.NotSupported, result.Error!.Kind);
    }

    private static ServiceProvider Provider()
    {
        var transport = new HostileTransport(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        });

        return MinioContainerFixture.Provider(
            MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key"),
            services => services.Replace(ServiceDescriptor.Singleton<MinioTransport>(transport)));
    }

    private sealed class ForwardOnly(byte[] buffer) : Stream
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
