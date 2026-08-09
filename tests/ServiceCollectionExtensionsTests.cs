using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Toolkit.Minio.Entities;
using Toolkit.Minio.Extensions;

namespace Toolkit.Minio.Tests;

/// <summary>
/// Tests for the <c>AddMinio</c> registration extensions.
/// </summary>
/// <remarks>
/// Covers what this library decides — argument guards, the lifetime switch, and which registration wins —
/// rather than re-testing the configuration binder or the DI container underneath it.
/// </remarks>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMinio_RegistersResolvableFactoryAndClient()
    {
        var services = new ServiceCollection()
            .AddMinio(TestConfiguration.Client1, TestConfiguration.Build());

        using var serviceProvider = services.BuildServiceProvider();

        Assert.NotNull(serviceProvider.GetService<IMinioClientFactory>());
        Assert.NotNull(serviceProvider.GetService<IMinioClient>());
    }

    [Fact]
    public void AddMinio_Throws_WhenNameIsBlank()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new ServiceCollection().AddMinio("   ", TestConfiguration.Build()));
    }

    [Fact]
    public void AddMinio_Throws_WhenConfigurationIsNull()
    {
        // The cast disambiguates from Minio's own AddMinio(IServiceCollection, string, string, ServiceLifetime),
        // which is in scope whenever the Minio namespace is imported alongside this one.
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddMinio("Minio", (IConfiguration)null!));
    }

    [Fact]
    public void AddMinio_Throws_ForUnsupportedLifetime()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddMinio("Minio", TestConfiguration.Build(), lifetime: (ServiceLifetime)99));
    }

    [Fact]
    public void AddMinio_RegistersClientWithRequestedLifetime()
    {
        var services = new ServiceCollection()
            .AddMinio(TestConfiguration.Client1, TestConfiguration.Build(), lifetime: ServiceLifetime.Scoped);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IMinioClient));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddMinio_KeepsFirstClientRegistration_WhenCalledTwice()
    {
        // TryAdd matches on service type alone, so the second call cannot replace the client or its lifetime.
        // The behaviour is documented; this test pins it so it cannot change silently.
        var services = new ServiceCollection()
            .AddMinio(TestConfiguration.Client1, TestConfiguration.Build())
            .AddMinio(TestConfiguration.Client2, TestConfiguration.Build(), lifetime: ServiceLifetime.Transient);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IMinioClient));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var serviceProvider = services.BuildServiceProvider();
        var client = serviceProvider.GetRequiredService<IMinioClient>();

        var expected = serviceProvider.GetRequiredService<IOptionsMonitor<MinioOptions>>().Get(TestConfiguration.Client1);
        Assert.Equal(expected.AccessKey, client.Config.AccessKey);
    }
}
