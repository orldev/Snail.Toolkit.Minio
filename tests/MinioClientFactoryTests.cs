using Microsoft.Extensions.Options;
using Toolkit.Minio.Entities;
using Toolkit.Minio.Extensions;

namespace Toolkit.Minio.Tests;

/// <summary>
/// Tests for <see cref="MinioClientFactory"/> configuration handling and validation.
/// </summary>
public class MinioClientFactoryTests
{
    private static IMinioClientFactory CreateFactory(params (string Name, MinioOptions Options)[] configurations)
    {
        var services = new ServiceCollection();

        foreach (var (name, options) in configurations)
            services.Configure<MinioOptions>(name, o =>
            {
                o.Endpoint = options.Endpoint;
                o.AccessKey = options.AccessKey;
                o.SecretKey = options.SecretKey;
                o.Region = options.Region;
                o.SessionToken = options.SessionToken;
                o.Timeout = options.Timeout;
                o.SSL = options.SSL;
            });

        var provider = services.BuildServiceProvider();

        return new MinioClientFactory(provider.GetRequiredService<IOptionsMonitor<MinioOptions>>());
    }

    private static MinioOptions Valid() => new()
    {
        Endpoint = "localhost:9000",
        AccessKey = "access-key",
        SecretKey = "secret-key",
        SSL = false
    };

    [Fact]
    public void CreateClient_Throws_WhenNameIsBlank()
    {
        var factory = CreateFactory(("valid", Valid()));

        Assert.ThrowsAny<ArgumentException>(() => factory.CreateClient("   "));
    }

    [Fact]
    public void CreateClient_Throws_NamingSection_WhenEndpointMissing()
    {
        var options = Valid();
        options.Endpoint = null;
        var factory = CreateFactory(("storage", options));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient("storage"));

        Assert.Contains("storage", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(MinioOptions.Endpoint), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateClient_Throws_WhenCredentialsMissing()
    {
        var options = Valid();
        options.SecretKey = null;
        var factory = CreateFactory(("storage", options));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient("storage"));

        Assert.Contains(nameof(MinioOptions.SecretKey), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateClient_Throws_ForUnknownConfigurationName()
    {
        var factory = CreateFactory(("storage", Valid()));

        // An unregistered name yields default options, which cannot produce a usable client.
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient("typo"));
    }

    [Fact]
    public void CreateClient_AppliesOptionalSettings()
    {
        var options = Valid();
        options.Region = "eu-west-1";
        options.SessionToken = "session-token";
        options.Timeout = 1234;
        var factory = CreateFactory(("storage", options));

        var config = factory.CreateClient("storage").Config;

        Assert.Equal("localhost:9000", config.BaseUrl);
        Assert.Equal("eu-west-1", config.Region);
        Assert.Equal("session-token", config.SessionToken);
        Assert.Equal(1234, config.RequestTimeout);
        Assert.False(config.Secure);
    }

    [Fact]
    public void CreateClient_UsesSharedConnectionPool_ByDefault()
    {
        var factory = CreateFactory(("storage", Valid()));

        var first = factory.CreateClient("storage");
        var second = factory.CreateClient("storage");

        // Each client gets its own thin HttpClient, but both must be backed by the factory's shared handler,
        // otherwise repeated creation exhausts sockets.
        Assert.NotNull(first.Config.HttpClient);
        Assert.NotNull(second.Config.HttpClient);
        Assert.NotSame(first.Config.HttpClient, second.Config.HttpClient);
        Assert.Same(SharedHandlerOf(first), SharedHandlerOf(second));
    }

    [Fact]
    public void CreateClient_DoesNotOverrideCallerSuppliedHttpClient()
    {
        var factory = CreateFactory(("storage", Valid()));
        using var custom = new HttpClient();

        var client = factory.CreateClient("storage", c => c.WithHttpClient(custom, disposeHttpClient: false));

        Assert.Same(custom, client.Config.HttpClient);
    }

    [Fact]
    public void CreateClient_LeavesProxyEffective()
    {
        var factory = CreateFactory(("storage", Valid()));
        var proxy = new System.Net.WebProxy("http://127.0.0.1:8080");

        var proxied = factory.CreateClient("storage", c => c.WithProxy(proxy));
        var plain = factory.CreateClient("storage");

        // Minio only honours Proxy while it builds its own transport. The factory must therefore step aside
        // and let Build() construct a proxy-aware handler, rather than injecting the shared pool.
        Assert.Same(proxy, proxied.Config.Proxy);
        Assert.NotSame(SharedHandlerOf(plain), SharedHandlerOf(proxied));
    }

    /// <summary>
    /// Reaches the <see cref="HttpMessageHandler"/> behind a client's <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="client">The client to inspect.</param>
    /// <returns>The handler instance backing the client's transport.</returns>
    private static object SharedHandlerOf(IMinioClient client)
    {
        var field = typeof(HttpMessageInvoker)
            .GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        return field!.GetValue(client.Config.HttpClient)!;
    }
}
