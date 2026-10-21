using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Response;
using Snail.Toolkit.Minio.Adapters;

namespace Snail.Toolkit.Minio.Benchmarks;

/// <summary>A Minio client that answers in process, so a benchmark times the mapping and not the network.</summary>
/// <remarks>Only the calls the measured paths make are answered: a listing and a stat, each with what it was given.</remarks>
public class InProcessMinio : DispatchProxy
{
    /// <summary>The headers a MinIO server sends for a HEAD of a picture with two user metadata entries.</summary>
    public static readonly Dictionary<string, string> StatHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Accept-Ranges"] = "bytes",
        ["Content-Length"] = "52341",
        ["Content-Type"] = "image/png",
        ["ETag"] = "\"9b2cf535f27731c974343645a3985328\"",
        ["Last-Modified"] = "Mon, 12 Oct 2026 10:00:00 GMT",
        ["Server"] = "MinIO",
        ["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains",
        ["Vary"] = "Origin",
        ["X-Amz-Id-2"] = "dd9025bab4ad464b049177c95eb6ebf374d3b3fd1af9251148b658df7ac2e3e8",
        ["X-Amz-Request-Id"] = "17C9F1F5D2A3B4C5",
        ["X-Content-Type-Options"] = "nosniff",
        ["X-Xss-Protection"] = "1; mode=block",
        ["X-Amz-Meta-Owner"] = "team-a",
        ["X-Amz-Meta-Purpose"] = "avatar",
        ["Date"] = "Mon, 12 Oct 2026 10:00:01 GMT"
    };

    private static readonly Task<string> SignedUrl =
        Task.FromResult("http://minio.local/bucket/a.png?X-Amz-Signature=9b2cf535f27731c974343645a3985328");

    private static readonly Task<PutObjectResponse> Stored =
        Task.FromResult((PutObjectResponse)RuntimeHelpers.GetUninitializedObject(typeof(PutObjectResponse)));

    private Item[] _items = [];

    private Task<ObjectStat> _stat = null!;

    /// <summary>A client whose listing yields the items given and whose stat answers with <see cref="StatHeaders"/>.</summary>
    public static IMinioClient Create(Item[] items)
    {
        var client = DispatchProxy.Create<IMinioClient, InProcessMinio>();
        var answers = (InProcessMinio)(object)client;

        answers._items = items;
        answers._stat = Task.FromResult(ObjectStat.FromResponseHeaders("a.png", StatHeaders));

        return client;
    }

    /// <summary>
    /// The SDK's own client for a server that is never called: it signs URLs in process, as it signs them for every read.
    /// </summary>
    public static IMinioClient Signing() => new MinioClient()
        .WithEndpoint("minio.local:9000")
        .WithCredentials("benchmark-access-key", "benchmark-secret-key")
        .Build();

    /// <summary>Storage over the client, with the default settings and no logging.</summary>
    public static MinioObjectStorage Storage(IMinioClient client) => Storage(client, new HttpClient());

    /// <summary>Storage over the client and the transport its reads go through.</summary>
    public static MinioObjectStorage Storage(IMinioClient client, HttpClient http) =>
        new("Minio", client, http, new Settings(new MinioOptions()), NullLogger<MinioObjectStorage>.Instance);

    /// <summary>A transport answering every GET with a 1 KB object and the headers of <see cref="StatHeaders"/>.</summary>
    public static HttpClient Objects() => new(new ObjectAnswer());

    /// <summary>Listed objects as MinIO returns them, each with a media type and two user metadata entries.</summary>
    public static Item[] Items(int count)
    {
        var items = new Item[count];

        for (var index = 0; index < count; index++)
        {
            items[index] = new Item
            {
                Key = $"photos/2026/10/{index:D6}.png",
                Size = (ulong)(50_000 + index),
                ETag = "\"9b2cf535f27731c974343645a3985328\"",
                ContentType = "image/png",
                LastModified = "2026-10-12T10:00:00.000Z",
                UserMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["content-type"] = "image/png",
                    ["Owner"] = "team-a",
                    ["Purpose"] = "avatar"
                }
            };
        }

        return items;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name switch
        {
            "ListObjectsEnumAsync" => List(),
            "StatObjectAsync" => _stat,
            "PresignedGetObjectAsync" => SignedUrl,
            "PutObjectAsync" => Stored,
            _ => throw new NotSupportedException($"The in-process Minio does not answer {targetMethod?.Name}.")
        };

    private async IAsyncEnumerable<Item> List()
    {
        foreach (var item in _items)
            yield return item;

        await Task.CompletedTask;
    }

    private sealed class ObjectAnswer : HttpMessageHandler
    {
        private static readonly byte[] Body = new byte[1024];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Body),
                RequestMessage = request
            };

            foreach (var (name, value) in StatHeaders)
            {
                if (!response.Headers.TryAddWithoutValidation(name, value))
                    response.Content.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class Settings(MinioOptions value) : IOptionsMonitor<MinioOptions>
    {
        public MinioOptions CurrentValue => value;

        public MinioOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<MinioOptions, string?> listener) => null;
    }
}
