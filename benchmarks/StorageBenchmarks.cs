using BenchmarkDotNet.Attributes;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Media;

namespace Snail.Toolkit.Minio.Benchmarks;

/// <summary>What the storage does with what the server answered: listed objects, a stat and a read mapped, an upload checked and described, media types found.</summary>
/// <remarks>
/// A listing maps every object with its user metadata, which is where a bucket walk spends its time once the bytes
/// have arrived; a stat maps one response's headers, and so does every read; every read is signed by the SDK first,
/// measured once with its signing answered for free and once with the SDK signing; an upload without a stated media
/// type looks one up by name.
/// </remarks>
[MemoryDiagnoser]
public class StorageBenchmarks
{
    private static readonly string[] Names =
        ["a.png", "report.final.PDF", "archive.tar.gz", "photo.jpeg?versionId=3", "index.html", "data.json", "movie.mp4", "README"];

    private static readonly Dictionary<string, string> Written = new()
    {
        ["owner"] = "team-a",
        ["purpose"] = "avatar",
        ["build"] = "2026.10.12-rc1"
    };

    private readonly MemoryStream _upload = new(new byte[1024]);

    private MinioObjectStorage _storage = null!;

    private MinioObjectStorage _signing = null!;

    /// <summary>
    /// Storage over an in-process client listing a thousand objects and a transport answering reads; then checks
    /// that a listed object, a stat and a read each carry the two names a caller wrote and nothing else.
    /// </summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _storage = InProcessMinio.Storage(InProcessMinio.Create(InProcessMinio.Items(1000)), InProcessMinio.Objects());
        _signing = InProcessMinio.Storage(InProcessMinio.Signing(), InProcessMinio.Objects());

        var url = (await _signing.SignedUrlAsync("bucket", "a.png")).Value;

        await using var signed = (await _signing.GetAsync("bucket", "a.png")).Value;

        if (!url.Query.Contains("X-Amz-Signature=", StringComparison.Ordinal) || signed.Metadata.Metadata.Count != 2)
            throw new InvalidOperationException("The read is not signed by the SDK and described as it was written.");

        await using var read = (await _storage.GetAsync("bucket", "a.png")).Value;
        var stat = (await _storage.StatAsync("bucket", "a.png")).Value;
        StoredObject? listed = null;

        await foreach (var result in _storage.EnumerateAsync("bucket"))
        {
            listed = result.Value;
            break;
        }

        IReadOnlyDictionary<string, string>[] described = [listed!.Metadata, stat.Metadata, read.Metadata.Metadata];

        _upload.Position = 0;
        var put = (await _storage.PutAsync("bucket", _upload, new UploadOptions { Name = "photos/a.png", Metadata = Written })).Value;

        if (put.MediaType != "image/png" || put.Metadata.Count != 3 || put.Metadata["OWNER"] != "team-a")
            throw new InvalidOperationException("The upload is not described as it was written.");

        if (!Array.TrueForAll(described, metadata => metadata.Count == 2 && metadata["owner"] == "team-a" && metadata["PURPOSE"] == "avatar"))
            throw new InvalidOperationException("The metadata is not the two names a caller wrote.");
    }

    /// <summary>Releases the storage and the upload.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _storage.Dispose();
        _signing.Dispose();
        _upload.Dispose();
    }

    /// <summary>A thousand listed objects mapped.</summary>
    [Benchmark(Description = "EnumerateAsync, 1000 objects")]
    public async Task<int> Enumerate()
    {
        var count = 0;

        await foreach (var result in _storage.EnumerateAsync("bucket"))
            count += result.Value.Metadata.Count;

        return count;
    }

    /// <summary>One object's headers mapped.</summary>
    [Benchmark(Description = "StatAsync")]
    public async Task<int> Stat() => (await _storage.StatAsync("bucket", "a.png")).Value.Metadata.Count;

    /// <summary>One 1 KB object read: its URL signed, its response's headers mapped, its body released.</summary>
    [Benchmark(Description = "GetAsync, 1 KB")]
    public async Task<int> Get()
    {
        await using var content = (await _storage.GetAsync("bucket", "a.png")).Value;

        return content.Metadata.Metadata.Count;
    }

    /// <summary>One 1 KB object read with its URL signed by the SDK's own client, as every read signs it.</summary>
    [Benchmark(Description = "GetAsync, 1 KB, signed by the SDK")]
    public async Task<int> SignedGet()
    {
        await using var content = (await _signing.GetAsync("bucket", "a.png")).Value;

        return content.Metadata.Metadata.Count;
    }

    /// <summary>One 1 KB upload with three metadata entries and no media type stated: checked, sent, described.</summary>
    [Benchmark(Description = "PutAsync, 1 KB")]
    public async Task<int> Put()
    {
        _upload.Position = 0;

        return (await _storage.PutAsync("bucket", _upload, new UploadOptions { Name = "photos/a.png", Metadata = Written })).Value.Metadata.Count;
    }

    /// <summary>The media types of eight typical names.</summary>
    [Benchmark(Description = "MediaTypeFor, 8 names")]
    public int MediaType()
    {
        var length = 0;

        foreach (var name in Names)
            length += MediaTypes.MediaTypeFor(name).Length;

        return length;
    }
}
