using Toolkit.Minio.Extensions;
using Toolkit.Minio.Tests.Infrastructure;

namespace Toolkit.Minio.Tests;

/// <summary>
/// Checks the range request this library builds, at the wire level.
/// </summary>
/// <remarks>
/// <para>
/// This is the one assertion a live server cannot make. A range beyond <see cref="int.MaxValue"/> would need
/// an object larger than 2GB to mean anything server-side, and Minio 7.0.0 rejects every 206 response with
/// <c>PartialContentException</c> regardless — see the skipped
/// <see cref="MinioObjectOperationsTests.DownloadObjectWithOffsetAndLength_ReturnsTheRequestedRange"/>.
/// </para>
/// <para>
/// What can still be verified, cheaply and without storing gigabytes, is that a 64-bit offset survives the
/// journey to the <c>Range</c> header. The previous <c>int</c>-based signature could not express this range
/// at all.
/// </para>
/// </remarks>
public class MinioRangeRequestTests
{
    [Fact]
    public async Task DownloadObjectWithOffsetAndLength_EmitsA64BitRangeHeader()
    {
        const long offset = 3_000_000_000;
        const long length = 1_024;

        var client = RequestRecorder.CreateClient(out var recorder);
        using var destination = new MemoryStream();

        await client.DownloadObjectWithOffsetAndLengthAsync(
            "test-bucket", "object", destination, offset, length);

        var get = Assert.Single(recorder.Requests, r => r.Method == HttpMethod.Get);

        Assert.True(get.Headers.TryGetValues("Range", out var range));
        Assert.Equal($"bytes={offset}-{offset + length - 1}", Assert.Single(range));
    }
}
