using Microsoft.Extensions.Configuration;
using Minio.DataModel.Args;
using Testcontainers.Minio;
using Toolkit.Minio.Extensions;

namespace Toolkit.Minio.Tests.Infrastructure;

/// <summary>
/// Starts a disposable Minio server in Docker and hands out clients wired to it.
/// </summary>
/// <remarks>
/// <para>
/// Tests run against a real server rather than a simulated one. That is what makes them worth trusting:
/// a stubbed transport can only confirm the behaviour it was written to imitate, and cannot surface how
/// the client reacts to refused connections, request timeouts, or rejected credentials.
/// </para>
/// <para>
/// Clients are built through <c>AddMinio</c> and <see cref="IMinioClientFactory"/>, so every test exercises
/// the real registration and construction path as a side effect.
/// </para>
/// <para>
/// Requires a running Docker daemon. Shared through <see cref="MinioContainerCollection"/> so the container
/// starts once for the whole suite.
/// </para>
/// </remarks>
public sealed class MinioContainerFixture : IAsyncLifetime
{
    /// <summary>
    /// The Minio image the suite runs against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinned explicitly: Testcontainers 4.13 deprecated the image-less constructor, and an explicit tag keeps
    /// runs reproducible instead of drifting with <c>latest</c>.
    /// </para>
    /// <para>
    /// The plain tag rather than the <c>-cpuv1</c> variant: that variant exists for x86 CPUs without the newer
    /// instruction-set levels and buys nothing on arm64 or on any current runner. Switch to it only if the
    /// suite has to run on genuinely old x86 hardware.
    /// </para>
    /// </remarks>
    private const string Image = "minio/minio:RELEASE.2025-09-07T16-13-09Z";

    private readonly MinioContainer _container = new MinioBuilder(Image).Build();

    /// <summary>Gets the running server's <c>host:port</c> endpoint.</summary>
    public string Endpoint { get; private set; } = null!;

    /// <summary>Gets the access key the server was started with.</summary>
    public string AccessKey { get; private set; } = null!;

    /// <summary>Gets the secret key the server was started with.</summary>
    public string SecretKey { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Endpoint = ToAuthority(_container.GetConnectionString());
        AccessKey = _container.GetAccessKey();
        SecretKey = _container.GetSecretKey();
    }

    /// <inheritdoc />
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Creates a client pointed at the running container.
    /// </summary>
    /// <returns>A client with valid credentials.</returns>
    public IMinioClient CreateClient() => CreateClient(Endpoint, AccessKey, SecretKey);

    /// <summary>
    /// Creates a client from explicit settings, for tests that need a deliberately broken configuration.
    /// </summary>
    /// <param name="endpoint">The endpoint to target.</param>
    /// <param name="accessKey">The access key to authenticate with.</param>
    /// <param name="secretKey">The secret key to authenticate with.</param>
    /// <param name="timeoutMs">Optional request timeout in milliseconds.</param>
    /// <returns>A client built through the library's own registration path.</returns>
    public static IMinioClient CreateClient(string endpoint, string accessKey, string secretKey, int? timeoutMs = null)
    {
        const string name = "TestContainer";

        var settings = new Dictionary<string, string?>
        {
            [$"{name}:Endpoint"] = endpoint,
            [$"{name}:AccessKey"] = accessKey,
            [$"{name}:SecretKey"] = secretKey,
            [$"{name}:SSL"] = "false"
        };

        if (timeoutMs is not null)
            settings[$"{name}:Timeout"] = timeoutMs.Value.ToString();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection()
            .AddMinio(name, configuration)
            .BuildServiceProvider()
            .GetRequiredService<IMinioClientFactory>()
            .CreateClient(name);
    }

    /// <summary>
    /// Creates a uniquely named bucket, runs <paramref name="body"/> against it, and removes it afterwards.
    /// </summary>
    /// <param name="client">The client to use.</param>
    /// <param name="body">The work to perform while the bucket exists; receives the bucket name.</param>
    /// <remarks>
    /// Each test gets its own bucket, so tests sharing the container stay independent.
    /// </remarks>
    public static async Task WithBucketAsync(IMinioClient client, Func<string, Task> body)
    {
        var bucketName = $"toolkit-{Guid.NewGuid():N}";

        await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName));

        try
        {
            await body(bucketName);
        }
        finally
        {
            var forceDelete = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "x-minio-force-delete", "true" }
            };

            await client.RemoveBucketAsync(new RemoveBucketArgs()
                .WithBucket(bucketName)
                .WithHeaders(forceDelete));
        }
    }

    /// <summary>
    /// Converts a Testcontainers connection string into the <c>host:port</c> form the Minio client expects.
    /// </summary>
    /// <param name="connectionString">The value reported by the container.</param>
    /// <returns>The bare authority.</returns>
    /// <remarks>
    /// The module reports an absolute URL. A value that is already an authority is passed through unchanged,
    /// so this keeps working if the module's format ever changes.
    /// </remarks>
    private static string ToAuthority(string connectionString)
        => Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
            ? uri.Authority
            : connectionString.Trim('/');
}

/// <summary>
/// Groups every server-backed test class around a single shared Minio container.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MinioContainerCollection : ICollectionFixture<MinioContainerFixture>
{
    /// <summary>The xUnit collection name.</summary>
    public const string Name = "Minio container";
}
