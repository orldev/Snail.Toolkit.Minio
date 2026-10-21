using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.DataModel.Response;
using Snail.Toolkit.Minio.Adapters;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// What a caller sees as an object's metadata, from a listing, a stat and a read: the names a caller wrote,
/// without what the protocol added, looked up in any case. No server: the answers are made in process.
/// </summary>
public class MetadataMappingTests
{
    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Content-Type"] = "image/png",
        ["Content-Length"] = "1024",
        ["ETag"] = "\"9b2cf535f27731c974343645a3985328\"",
        ["Last-Modified"] = "Mon, 12 Oct 2026 10:00:00 GMT",
        ["Server"] = "MinIO",
        ["X-Amz-Request-Id"] = "17C9F1F5D2A3B4C5",
        ["X-Amz-Meta-Owner"] = "team-a",
        ["x-amz-meta-purpose"] = "avatar, small",
        ["Vary"] = "Origin"
    };

    [Fact]
    public async Task Listing_UserNamesBesideProtocolOnes_KeepsTheUsersOnly()
    {
        var storage = Storage(Answers.Listing(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "image/png",
                ["X-Amz-Meta-Hidden"] = "no",
                ["Cache-Control"] = "no-cache",
                ["Owner"] = "team-a",
                ["Purpose"] = "avatar"
            },
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["ETag"] = "x", ["x-amz-version-id"] = "1" }));

        var listed = new List<IReadOnlyDictionary<string, string>>();

        await foreach (var result in storage.EnumerateAsync("bucket"))
            listed.Add(result.Value.Metadata);

        Assert.Equal(["Owner=team-a", "Purpose=avatar"], Pairs(listed[0]));
        Assert.Equal("team-a", listed[0]["OWNER"]);
        Assert.False(listed[0].ContainsKey("content-type"));
        Assert.All(listed.Skip(1), metadata => Assert.Empty(metadata));
    }

    [Fact]
    public async Task Stat_HeadersOfAnObject_KeepsTheUsersOnly()
    {
        var storage = Storage(Answers.Stat(ObjectStat.FromResponseHeaders("a.png", Headers)));

        var metadata = (await storage.StatAsync("bucket", "a.png")).Value.Metadata;

        Assert.Equal(["Owner=team-a", "purpose=avatar, small"], Pairs(metadata));
        Assert.Equal("avatar, small", metadata["PURPOSE"]);
    }

    [Fact]
    public async Task Get_HeadersOfAnObject_KeepsTheUsersOnly()
    {
        var storage = Storage(Answers.Stat(ObjectStat.FromResponseHeaders("a.png", Headers)), new HttpClient(new Read()));

        await using var content = (await storage.GetAsync("bucket", "a.png")).Value;
        var metadata = content.Metadata.Metadata;

        Assert.Equal(["Owner=team-a", "purpose=avatar, small, second"], Pairs(metadata));
        Assert.Equal("team-a", metadata["OWNER"]);
    }

    [Fact]
    public async Task Get_NoUserHeaders_ReadsAsEmpty()
    {
        var storage = Storage(Answers.Stat(ObjectStat.FromResponseHeaders("a.png", Headers)), new HttpClient(new Read(bare: true)));

        await using var content = (await storage.GetAsync("bucket", "a.png")).Value;

        Assert.Empty(content.Metadata.Metadata);
    }

    /// <summary>
    /// Every UTF-16 code unit in a metadata value, and every character up to U+02FF in a name, is refused
    /// or let through, with the same message, as the checks one character at a time judged it.
    /// </summary>
    [Fact]
    public async Task Put_EveryCharacter_JudgedAsBefore()
    {
        var storage = Storage(Answers.Uploads());
        List<Dictionary<string, string>> cases = [];

        for (var code = 0; code <= char.MaxValue; code++)
            cases.Add(new Dictionary<string, string> { ["owner"] = $"a{(char)code}b" });

        for (var code = 0; code <= 0x2FF; code++)
            cases.Add(new Dictionary<string, string> { [$"k{(char)code}"] = "value" });

        cases.Add(new Dictionary<string, string> { [" "] = "value" });
        cases.Add(new Dictionary<string, string> { ["first"] = "fine", ["second"] = "tab\there" });

        foreach (var metadata in cases)
        {
            using var content = new MemoryStream(new byte[4]);
            var result = await storage.PutAsync("bucket", content, new() { Name = "a.png", Metadata = metadata });

            Assert.Equal(FaultAsBefore(metadata), result.Error?.Message);

            if (result.IsSuccess)
                Assert.Equal(Pairs(metadata), Pairs(result.Value.Metadata));
        }
    }

    /// <summary>Two names that differ only in case cannot both be kept, and the upload says so as it did.</summary>
    [Fact]
    public async Task Put_NamesDifferingInCase_RefusedAsBefore()
    {
        var storage = Storage(Answers.Uploads());
        using var content = new MemoryStream(new byte[4]);

        await Assert.ThrowsAsync<ArgumentException>(() => storage.PutAsync(
            "bucket", content, new() { Name = "a.png", Metadata = new Dictionary<string, string> { ["Owner"] = "a", ["owner"] = "b" } }));
    }

    [Fact]
    public async Task Put_NoMetadata_DescribesNone()
    {
        var storage = Storage(Answers.Uploads());
        using var content = new MemoryStream(new byte[4]);

        var empty = await storage.PutAsync("bucket", content, new() { Name = "a.png", Metadata = new Dictionary<string, string>() });
        content.Position = 0;
        var none = await storage.PutAsync("bucket", content, new() { Name = "a.png" });

        Assert.Empty(empty.Value.Metadata);
        Assert.Empty(none.Value.Metadata);
    }

    /// <summary>
    /// A name reported twice in different cases keeps its first spelling and its later value, and the names keep
    /// the order they were reported in, as a dictionary ignoring case kept them.
    /// </summary>
    [Fact]
    public async Task Listing_NameInTwoCases_KeepsFirstSpellingAndLaterValue()
    {
        var storage = Storage(Answers.Listing(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Owner"] = "a",
                ["purpose"] = "avatar",
                ["OWNER"] = "b",
                ["Cache-Control"] = "no-cache"
            }));

        await foreach (var result in storage.EnumerateAsync("bucket"))
        {
            var metadata = result.Value.Metadata;

            Assert.Equal(["Owner", "purpose"], metadata.Keys);
            Assert.Equal(["b", "avatar"], metadata.Values);
            Assert.Equal(["Owner=b", "purpose=avatar"], metadata.Select(pair => $"{pair.Key}={pair.Value}"));
            AnswersAsADictionary(metadata);
        }
    }

    /// <summary>More names than an object usually carries are kept and looked up as a few are.</summary>
    [Fact]
    public async Task Listing_ManyNames_KeptInOrder()
    {
        var reported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["content-type"] = "image/png" };

        for (var index = 0; index < 12; index++)
            reported[$"Name{index}"] = $"value-{index}";

        var storage = Storage(Answers.Listing(reported));

        await foreach (var result in storage.EnumerateAsync("bucket"))
        {
            var metadata = result.Value.Metadata;

            Assert.Equal([.. Enumerable.Range(0, 12).Select(index => $"Name{index}")], metadata.Keys);
            Assert.Equal("value-11", metadata["NAME11"]);
            AnswersAsADictionary(metadata);
        }
    }

    /// <summary>Every count of names an object may carry reads back in order, in any case, from a listing, a stat and a read.</summary>
    [Fact]
    public async Task EveryPath_UpToTwelveNames_ReadsBackInOrder()
    {
        for (var count = 1; count <= 12; count++)
        {
            var names = Enumerable.Range(0, count).Select(index => $"Name{index}").ToArray();
            var headers = Headers
                .Where(header => !header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(StringComparer.OrdinalIgnoreCase);
            var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                headers[$"X-Amz-Meta-{name}"] = $"{name}-value";
                listed[name] = $"{name}-value";
            }

            var storage = Storage(
                Answers.Listing(listed),
                new HttpClient(new Read(bare: true, names: names)));
            var stat = Storage(Answers.Stat(ObjectStat.FromResponseHeaders("a.png", headers)));

            List<IReadOnlyDictionary<string, string>> described = [];

            await foreach (var result in storage.EnumerateAsync("bucket"))
                described.Add(result.Value.Metadata);

            described.Add((await stat.StatAsync("bucket", "a.png")).Value.Metadata);

            await using (var content = (await storage.GetAsync("bucket", "a.png")).Value)
                described.Add(content.Metadata.Metadata);

            foreach (var metadata in described)
            {
                Assert.Equal(names, metadata.Keys);
                Assert.Equal($"Name{count - 1}-value", metadata[$"NAME{count - 1}"]);
                AnswersAsADictionary(metadata);
            }
        }
    }

    /// <summary>Every count of names an upload may carry is described in order; two in different cases are refused.</summary>
    [Fact]
    public async Task Put_UpToTwelveNames_DescribedInOrder()
    {
        var storage = Storage(Answers.Uploads());

        for (var count = 1; count <= 12; count++)
        {
            var written = Enumerable.Range(0, count).ToDictionary(index => $"Name{index}", index => $"value-{index}");
            using var content = new MemoryStream(new byte[4]);

            var metadata = (await storage.PutAsync("bucket", content, new() { Name = "a.png", Metadata = written })).Value.Metadata;

            Assert.Equal(written.Keys, metadata.Keys);
            Assert.Equal(written.Values, metadata.Values);
            AnswersAsADictionary(metadata);

            written[$"NAME{count - 1}"] = "again";
            content.Position = 0;

            await Assert.ThrowsAsync<ArgumentException>(() => storage.PutAsync(
                "bucket", content, new() { Name = "a.png", Metadata = written }));
        }
    }

    /// <summary>
    /// The request an upload hands the SDK carries the headers and media type it carried when each name was added
    /// on its own: the same names in the same order, whatever kind of dictionary the caller wrote them in.
    /// </summary>
    [Fact]
    public async Task Put_Metadata_HandsTheSdkTheSameRequest()
    {
        IReadOnlyDictionary<string, string>[] written =
        [
            new Dictionary<string, string> { ["owner"] = "team-a", ["purpose"] = "avatar", ["build"] = "rc1" },
            new Dictionary<string, string> { ["Owner"] = "a", ["owner"] = "b" },
            new Dictionary<string, string> { ["Content-Type"] = "text/plain", ["owner"] = "a" },
            new Dictionary<string, string> { ["content-type"] = " ", ["Cache-Control"] = "no-cache" },
            new Dictionary<string, string> { ["X-Amz-Meta-Kept"] = "as-is", ["x-amz-server-side-encryption"] = "AES256" },
            new Dictionary<string, string> { ["owner"] = "a" }.AsReadOnly(),
            new Written(new Dictionary<string, string> { ["owner"] = "a", ["Purpose"] = "b" }),
            new Dictionary<string, string>()
        ];

        foreach (var mediaType in new[] { "image/png", " ", null })
        {
            foreach (var metadata in written)
            {
                var client = Answers.Uploads();
                var storage = Storage(client);
                using var content = new MemoryStream(new byte[4]);

                var upload = storage.PutAsync("bucket", content, new() { Name = "a.png", MediaType = mediaType, Metadata = metadata });

                if (metadata.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == metadata.Count)
                    await upload;
                else
                    await Assert.ThrowsAsync<ArgumentException>(() => upload);

                var expected = new PutObjectArgs().WithBucket("bucket").WithObject("a.png").WithContentType(mediaType ?? "image/png");

                foreach (var (key, value) in metadata)
                    expected = expected.WithHeaders(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

                var sent = ((Answers)(object)client).Uploaded!;

                Assert.Equal(Request(expected), Request(sent));
            }
        }
    }

    private static string[] Request(PutObjectArgs args)
    {
        var headers = (IDictionary<string, string>)typeof(PutObjectArgs).GetProperty("Headers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(args)!;
        var mediaType = (string?)typeof(PutObjectArgs).GetProperty("ContentType", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(args);

        return [$"media type: {mediaType}", .. headers.Select(header => $"{header.Key}: {header.Value}")];
    }

    private static void AnswersAsADictionary(IReadOnlyDictionary<string, string> metadata)
    {
        var first = metadata.First();

        Assert.Equal(metadata.Count, metadata.Count());
        Assert.Equal(metadata.Count, metadata.Keys.Count());
        Assert.True(metadata.ContainsKey(first.Key.ToUpperInvariant()));
        Assert.True(metadata.TryGetValue(first.Key.ToLowerInvariant(), out var value));
        Assert.Equal(first.Value, value);
        Assert.False(metadata.TryGetValue("missing", out _));
        Assert.False(metadata.ContainsKey("missing"));
        var refused = Assert.Throws<KeyNotFoundException>(() => new Dictionary<string, string>()["missing"]);
        Assert.Equal(refused.Message, Assert.Throws<KeyNotFoundException>(() => metadata["missing"]).Message);
        Assert.Throws<ArgumentNullException>("key", () => metadata.ContainsKey(null!));
        Assert.Throws<ArgumentNullException>("key", () => metadata[null!]);
        Assert.False(metadata is IDictionary<string, string> { IsReadOnly: false });
    }

    private static string? FaultAsBefore(IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key) || !key.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal)))
                return $"The metadata name '{key}' is not a header name.";

            if (value.Any(char.IsControl))
                return $"The metadata value of '{key}' carries a control character.";

            if (!value.All(char.IsAscii))
                return $"The metadata value of '{key}' is not US-ASCII. Headers cannot carry anything else; "
                    + "encode the value yourself if it has to travel.";
        }

        return null;
    }

    private static string[] Pairs(IReadOnlyDictionary<string, string> metadata) =>
        [.. metadata.Select(pair => $"{pair.Key}={pair.Value}").Order(StringComparer.Ordinal)];

    private static MinioObjectStorage Storage(IMinioClient client, HttpClient? http = null) =>
        new("Minio", client, http ?? new HttpClient(), new Settings(), NullLogger<MinioObjectStorage>.Instance);

    /// <summary>A Minio client answering a listing, a stat and a URL to sign, in process.</summary>
    public class Answers : DispatchProxy
    {
        private Item[] _items = [];
        private ObjectStat? _stat;

        public PutObjectArgs? Uploaded { get; private set; }

        public static IMinioClient Listing(params IDictionary<string, string>?[] metadata)
        {
            var client = DispatchProxy.Create<IMinioClient, Answers>();
            ((Answers)(object)client)._items =
            [
                .. metadata.Select((entries, index) => new Item { Key = $"object-{index}", Size = 1, UserMetadata = entries })
            ];

            return client;
        }

        public static IMinioClient Uploads() => DispatchProxy.Create<IMinioClient, Answers>();

        public static IMinioClient Stat(ObjectStat stat)
        {
            var client = DispatchProxy.Create<IMinioClient, Answers>();
            ((Answers)(object)client)._stat = stat;

            return client;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "ListObjectsEnumAsync" => List(),
            "StatObjectAsync" => Task.FromResult(_stat!),
            "PresignedGetObjectAsync" => Task.FromResult("http://minio.local/bucket/a.png?X-Amz-Signature=abc"),
            "PutObjectAsync" => Upload((PutObjectArgs)args![0]!),
            _ => throw new NotSupportedException(targetMethod?.Name)
        };

        private async IAsyncEnumerable<Item> List()
        {
            foreach (var item in _items)
                yield return item;

            await Task.CompletedTask;
        }

        private Task<PutObjectResponse> Upload(PutObjectArgs args)
        {
            Uploaded = args;

            return Task.FromResult((PutObjectResponse)RuntimeHelpers.GetUninitializedObject(typeof(PutObjectResponse)));
        }
    }

    private sealed class Written(Dictionary<string, string> entries) : IReadOnlyDictionary<string, string>
    {
        public string this[string key] => entries[key];

        public IEnumerable<string> Keys => entries.Keys;

        public IEnumerable<string> Values => entries.Values;

        public int Count => entries.Count;

        public bool ContainsKey(string key) => entries.ContainsKey(key);

        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value) => entries.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => entries.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Read(bool bare = false, string[]? names = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16]) };
            response.Headers.TryAddWithoutValidation("x-amz-request-id", "17C9F1F5D2A3B4C5");
            response.Headers.TryAddWithoutValidation("Server", "MinIO");

            if (!bare)
            {
                response.Headers.TryAddWithoutValidation("X-Amz-Meta-Owner", "team-a");
                response.Headers.TryAddWithoutValidation("x-amz-meta-purpose", "avatar, small");
                response.Headers.TryAddWithoutValidation("x-amz-meta-purpose", "second");
            }

            foreach (var name in names ?? [])
                response.Headers.TryAddWithoutValidation($"X-Amz-Meta-{name}", $"{name}-value");

            return Task.FromResult(response);
        }
    }

    private sealed class Settings : IOptionsMonitor<MinioOptions>
    {
        public MinioOptions CurrentValue { get; } = new();

        public MinioOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<MinioOptions, string?> listener) => null;
    }
}
