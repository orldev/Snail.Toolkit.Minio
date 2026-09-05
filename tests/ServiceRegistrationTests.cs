using Snail.Toolkit.Minio.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// What the container holds after registration.
/// </summary>
/// <remarks>
/// The defect this replaces: a second registration was added with <c>TryAdd</c>, which matches on service
/// type alone, so an application asking for two servers silently got the first one twice.
/// </remarks>
public class ServiceRegistrationTests
{
    [Fact]
    public void AddMinio_RegistersStorageAndTheClientBehindIt()
    {
        using var provider = MinioContainerFixture.Provider(Settings("minio.example:9000"));

        Assert.NotNull(provider.GetRequiredService<IObjectStorage>());
        Assert.NotNull(provider.GetRequiredService<IMinioClient>());
        Assert.NotNull(provider.GetRequiredService<IMinioClients>());
    }

    [Fact]
    public void Storage_IsShared()
    {
        using var provider = MinioContainerFixture.Provider(Settings("minio.example:9000"));

        Assert.Same(
            provider.GetRequiredService<IObjectStorage>(),
            provider.GetRequiredService<IObjectStorage>());
    }

    [Fact]
    public void TheDefaultRegistration_IsTheOneUnderItsOwnKey()
    {
        using var provider = MinioContainerFixture.Provider(Settings("minio.example:9000"));

        Assert.Same(
            provider.GetRequiredService<IObjectStorage>(),
            provider.GetRequiredKeyedService<IObjectStorage>(MinioOptions.SectionName));
    }

    [Fact]
    public void TwoServers_StandSideBySide()
    {
        var settings = Settings("primary.example:9000");

        foreach (var (key, value) in MinioContainerFixture.Settings(
                     "archive.example:9000", "other-access", "other-secret", "Archive"))
        {
            settings[key] = value;
        }

        var services = new ServiceCollection()
            .AddMinio(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddKeyedMinio("Archive", new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        using var provider = services.BuildServiceProvider();

        var primary = provider.GetRequiredService<IMinioClient>();
        var archive = provider.GetRequiredKeyedService<IMinioClient>("Archive");

        Assert.Equal("primary.example:9000", primary.Config.BaseUrl);
        Assert.Equal("archive.example:9000", archive.Config.BaseUrl);
        Assert.NotSame(
            provider.GetRequiredService<IObjectStorage>(),
            provider.GetRequiredKeyedService<IObjectStorage>("Archive"));
    }

    [Fact]
    public void ASectionOfAnotherName_CanBeBound()
    {
        var settings = MinioContainerFixture.Settings(
            "minio.example:9000", "access-key", "secret-key", "Storage");

        var services = new ServiceCollection()
            .AddMinio(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), "Storage");

        using var provider = services.BuildServiceProvider();

        Assert.Equal("minio.example:9000", provider.GetRequiredService<IMinioClient>().Config.BaseUrl);
    }

    [Fact]
    public void SettingsThatCannotBeUsed_StopResolution()
    {
        var settings = Settings("https://minio.example");

        using var provider = MinioContainerFixture.Provider(settings);

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IObjectStorage>());

        Assert.Contains(nameof(MinioOptions.IsSecure), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationApplies_ToTheClientItBuilds()
    {
        var settings = Settings("minio.example:9000");
        settings["Minio:Region"] = "eu-west-1";
        settings["Minio:SessionToken"] = "session-token";
        settings["Minio:Timeout"] = "00:00:01.234";

        using var provider = MinioContainerFixture.Provider(settings);

        var config = provider.GetRequiredService<IMinioClient>().Config;

        Assert.Equal("eu-west-1", config.Region);
        Assert.Equal("session-token", config.SessionToken);
        Assert.Equal(1234, config.RequestTimeout);
        Assert.False(config.Secure);
    }

    [Fact]
    public void ACallerSuppliedTransport_IsLeftAlone()
    {
        var settings = Settings("minio.example:9000");

        var services = new ServiceCollection().AddMinio(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            configureClient: client => client.WithProxy(new System.Net.WebProxy("http://127.0.0.1:8080")));

        using var provider = services.BuildServiceProvider();

        var config = provider.GetRequiredService<IMinioClient>().Config;

        Assert.NotNull(config.Proxy);
    }

    /// <summary>
    /// The defect this replaces: every client opened a connection pool of its own, so building one per
    /// request exhausted sockets.
    /// </summary>
    [Fact]
    public void ClientsOfOneConfiguration_ShareAPool()
    {
        using var provider = MinioContainerFixture.Provider(Settings("minio.example:9000"));

        var clients = provider.GetRequiredService<IMinioClients>();

        using var first = clients.Create(MinioOptions.SectionName);
        using var second = clients.Create(MinioOptions.SectionName);

        Assert.NotSame(first.Config.HttpClient, second.Config.HttpClient);
        Assert.Same(PoolOf(first), PoolOf(second));
    }

    private static object? PoolOf(IMinioClient client)
    {
        var handler = typeof(HttpMessageInvoker)
            .GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        return handler?.GetValue(client.Config.HttpClient);
    }

    private static Dictionary<string, string?> Settings(string endpoint)
        => MinioContainerFixture.Settings(endpoint, "access-key", "secret-key");
}
