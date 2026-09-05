using Microsoft.Extensions.Configuration;
using Minio.DataModel.Args;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Extensions;
using Snail.Toolkit.Minio.Ports;
using Testcontainers.Minio;

namespace Snail.Toolkit.Minio.Tests.Infrastructure;

/// <summary>
/// Starts a disposable Minio server in Docker and hands out storage wired to it.
/// </summary>
/// <remarks>
/// <para>
/// Tests run against a real server rather than a simulated one. That is what makes them worth trusting: a
/// stubbed transport can only confirm the behaviour it was written to imitate, and cannot surface how the
/// client reacts to refused connections, request timeouts, or rejected credentials.
/// </para>
/// <para>
/// Everything is resolved through <c>AddMinio</c>, so every test exercises the real registration and
/// construction path as a side effect.
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
    /// Pinned explicitly: an explicit tag keeps runs reproducible instead of drifting with <c>latest</c>.
    /// </remarks>
    private const string Image = "minio/minio:RELEASE.2025-09-07T16-13-09Z";

    private readonly MinioContainer _container = new MinioBuilder(Image).Build();

    private ServiceProvider _provider = null!;

    /// <summary>Gets the running server's <c>host:port</c> endpoint.</summary>
    public string Endpoint { get; private set; } = null!;

    /// <summary>Gets the access key the server was started with.</summary>
    public string AccessKey { get; private set; } = null!;

    /// <summary>Gets the secret key the server was started with.</summary>
    public string SecretKey { get; private set; } = null!;

    /// <summary>Gets storage pointed at the running container.</summary>
    public IObjectStorage Storage => _provider.GetRequiredService<IObjectStorage>();

    /// <summary>Gets the SDK client pointed at the running container.</summary>
    public IMinioClient Client => _provider.GetRequiredService<IMinioClient>();

    /// <summary>Gets the bucket operations pointed at the running container.</summary>
    public IBuckets Buckets => _provider.GetRequiredService<IBuckets>();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Endpoint = ToAuthority(_container.GetConnectionString());
        AccessKey = _container.GetAccessKey();
        SecretKey = _container.GetSecretKey();

        _provider = Provider(Settings(Endpoint, AccessKey, SecretKey));
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Builds the configuration entries for one named server.
    /// </summary>
    /// <param name="endpoint">The authority to talk to.</param>
    /// <param name="accessKey">The access key to authenticate with.</param>
    /// <param name="secretKey">The secret key to authenticate with.</param>
    /// <param name="section">The configuration section to write under.</param>
    /// <returns>Settings ready for an in-memory configuration source.</returns>
    public static Dictionary<string, string?> Settings(
        string endpoint,
        string accessKey,
        string secretKey,
        string section = MinioOptions.SectionName)
        => new()
        {
            [$"{section}:Endpoint"] = endpoint,
            [$"{section}:AccessKey"] = accessKey,
            [$"{section}:SecretKey"] = secretKey,
            [$"{section}:IsSecure"] = "false"
        };

    /// <summary>
    /// Builds a container holding storage for the supplied settings.
    /// </summary>
    /// <param name="settings">The configuration to register from.</param>
    /// <param name="register">Applied to the collection before it is built.</param>
    /// <returns>A provider the caller disposes.</returns>
    public static ServiceProvider Provider(
        Dictionary<string, string?> settings,
        Action<IServiceCollection>? register = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection().AddMinio(configuration);
        register?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a uniquely named bucket, runs <paramref name="body"/> against it, and removes it afterwards.
    /// </summary>
    /// <param name="body">The work to perform while the bucket exists; receives the bucket name.</param>
    /// <returns>A task that completes once the bucket is gone.</returns>
    /// <remarks>
    /// Each test gets its own bucket, so tests sharing the container stay independent.
    /// </remarks>
    public async Task WithBucketAsync(Func<string, Task> body)
    {
        var bucket = $"toolkit-{Guid.NewGuid():N}";
        var client = Client;

        await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));

        try
        {
            await body(bucket);
        }
        finally
        {
            var forceDelete = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "x-minio-force-delete", "true" }
            };

            await client.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket).WithHeaders(forceDelete));
        }
    }

    /// <summary>
    /// Converts a Testcontainers connection string into the <c>host:port</c> form the client expects.
    /// </summary>
    /// <param name="connectionString">The value reported by the container.</param>
    /// <returns>The bare authority.</returns>
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
