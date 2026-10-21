using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Media;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// What a listing asks for and reads, with user metadata asked for as MinIO serves it and without it as any S3
/// server answers. No server: the Minio client's requests are answered in process with the XML each server sends.
/// </summary>
public class ListingMetadataSettingTests
{
    private const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    private const string MinioAnswer =
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="{Namespace}">
          <Name>bucket</Name><Prefix></Prefix><KeyCount>1</KeyCount><MaxKeys>1000</MaxKeys><IsTruncated>false</IsTruncated>
          <Contents>
            <Key>photos/a.png</Key><LastModified>2026-10-12T10:00:00.000Z</LastModified>
            <ETag>&quot;9b2cf535f27731c974343645a3985328&quot;</ETag><Size>1024</Size><StorageClass>STANDARD</StorageClass>
            <UserMetadata><content-type>image/png</content-type><X-Amz-Meta-Owner>team-a</X-Amz-Meta-Owner></UserMetadata>
          </Contents>
        </ListBucketResult>
        """;

    private const string S3Answer =
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="{Namespace}">
          <Name>bucket</Name><Prefix></Prefix><KeyCount>1</KeyCount><MaxKeys>1000</MaxKeys><IsTruncated>false</IsTruncated>
          <Contents>
            <Key>photos/a.png</Key><LastModified>2026-10-12T10:00:00.000Z</LastModified>
            <ETag>&quot;9b2cf535f27731c974343645a3985328&quot;</ETag><Size>1024</Size><StorageClass>STANDARD</StorageClass>
          </Contents>
        </ListBucketResult>
        """;

    [Fact]
    public void Settings_ByDefault_AskForUserMetadata()
        => Assert.True(new MinioOptions().CanListUserMetadata);

    [Fact]
    public async Task Enumerate_ByDefault_AsksForMetadataAndReadsIt()
    {
        using var transport = new Listings(MinioAnswer);
        using var provider = Provider(transport, canListUserMetadata: null);

        var listed = await Listed(provider.GetRequiredService<IObjectStorage>());

        Assert.Contains("metadata=true", transport.Queries.Single(), StringComparison.Ordinal);
        Assert.Equal("image/png", listed.MediaType);
        Assert.Equal("team-a", listed.Metadata["owner"]);
    }

    [Fact]
    public async Task Enumerate_ByDefault_AgainstAnS3Server_Fails()
    {
        using var transport = new Listings(S3Answer);
        using var provider = Provider(transport, canListUserMetadata: null);

        var results = await All(provider.GetRequiredService<IObjectStorage>());

        var failure = Assert.Single(results);

        Assert.False(failure.IsSuccess);
        Assert.Contains("MinIO specific feature", failure.Error.Cause?.Message ?? failure.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enumerate_TurnedOff_AsksForNamesOnly()
    {
        using var transport = new Listings(S3Answer);
        using var provider = Provider(transport, canListUserMetadata: false);

        var listed = await Listed(provider.GetRequiredService<IObjectStorage>());

        Assert.DoesNotContain("metadata=", transport.Queries.Single(), StringComparison.Ordinal);
        Assert.Equal("photos/a.png", listed.Name);
        Assert.Equal(1024, listed.Size);
        Assert.Equal(MediaTypes.Default, listed.MediaType);
        Assert.Empty(listed.Metadata);
    }

    [Fact]
    public async Task List_TurnedOff_AsksForNamesOnly()
    {
        using var transport = new Listings(S3Answer);
        using var provider = Provider(transport, canListUserMetadata: false);

        var page = await provider.GetRequiredService<IObjectStorage>().ListAsync("bucket");

        Assert.DoesNotContain("metadata=", transport.Queries.Single(), StringComparison.Ordinal);
        Assert.Equal("photos/a.png", Assert.Single(page.Value.Objects).Name);
    }

    [Fact]
    public async Task List_TurnedOn_AsksForMetadata()
    {
        using var transport = new Listings(MinioAnswer);
        using var provider = Provider(transport, canListUserMetadata: true);

        var page = await provider.GetRequiredService<IObjectStorage>().ListAsync("bucket");

        Assert.Contains("metadata=true", transport.Queries.Single(), StringComparison.Ordinal);
        Assert.Equal("team-a", Assert.Single(page.Value.Objects).Metadata["OWNER"]);
    }

    private static async Task<StoredObject> Listed(IObjectStorage storage)
    {
        var result = Assert.Single(await All(storage));

        Assert.True(result.IsSuccess, result.Error?.Message);

        return result.Value;
    }

    private static async Task<List<StorageResult<StoredObject>>> All(IObjectStorage storage)
    {
        List<StorageResult<StoredObject>> results = [];

        await foreach (var result in storage.EnumerateAsync("bucket"))
            results.Add(result);

        return results;
    }

    private static ServiceProvider Provider(MinioTransport transport, bool? canListUserMetadata)
    {
        var settings = MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key");

        settings["Minio:RetryAttempts"] = "0";

        if (canListUserMetadata is { } asked)
            settings["Minio:CanListUserMetadata"] = asked ? "true" : "false";

        return MinioContainerFixture.Provider(settings, services => services.Replace(ServiceDescriptor.Singleton(transport)));
    }

    private sealed class Listings(string answer) : MinioTransport
    {
        private readonly Handler _handler = new(answer);

        public IReadOnlyList<string> Queries => _handler.Queries;

        public override HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

        public override void Dispose() => _handler.Dispose();

        private sealed class Handler(string answer) : HttpMessageHandler
        {
            private readonly List<string> _queries = [];

            public IReadOnlyList<string> Queries
            {
                get
                {
                    lock (_queries)
                        return [.. _queries];
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (_queries)
                    _queries.Add(request.RequestUri!.Query);

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(answer, Encoding.UTF8, "application/xml"),
                    RequestMessage = request
                };

                return Task.FromResult(response);
            }
        }
    }
}
