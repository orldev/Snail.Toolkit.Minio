using Microsoft.Extensions.Options;
using Toolkit.Minio.Entities;

namespace Toolkit.Minio;

/// <summary>
/// A factory implementation for creating and configuring Minio client instances based on named options.
/// This class provides a centralized way to create Minio clients with pre-configured settings
/// from <see cref="MinioOptions"/> while allowing for additional runtime configuration.
/// </summary>
/// <remarks>
/// <para>
/// This factory uses the Options Pattern to retrieve configuration settings for Minio clients.
/// It supports multiple named configurations through the <see cref="IOptionsMonitor{TOptions}"/> interface.
/// </para>
/// <para>
/// The factory applies configuration in the following order:
/// <list type="number">
/// <item><description>Basic SSL configuration</description></item>
/// <item><description>Endpoint configuration</description></item>
/// <item><description>Credentials (AccessKey and SecretKey)</description></item>
/// <item><description>Region configuration (if provided)</description></item>
/// <item><description>Session token (if provided)</description></item>
/// <item><description>Timeout configuration (if provided)</description></item>
/// <item><description>Additional runtime configuration via the <c>configureClient</c> delegate</description></item>
/// <item><description>A shared connection pool, unless the caller supplied its own transport</description></item>
/// </list>
/// </para>
/// <para>
/// Every created client is an independent instance that owns disposable state. Clients are intended to be
/// long-lived: resolve one per named configuration at startup rather than calling
/// <see cref="CreateClient"/> per request. To make the per-request case survivable anyway, all clients created
/// by this factory share a single <see cref="SocketsHttpHandler"/>, so repeated creation no longer exhausts
/// sockets. The shared handler is bypassed when the caller configures its own <c>HttpClient</c> or a proxy,
/// so <c>WithProxy</c> and <c>WithHttpClient</c> keep working exactly as before.
/// </para>
/// </remarks>
/// <param name="optionsMonitor">The options monitor used to retrieve Minio configuration options by name.</param>
public class MinioClientFactory(IOptionsMonitor<MinioOptions> optionsMonitor) : IMinioClientFactory
{
    /// <summary>
    /// Connection pool shared by every client this factory builds.
    /// </summary>
    /// <remarks>
    /// A single handler owns the TCP connection pool. <see cref="SocketsHttpHandler.PooledConnectionLifetime"/>
    /// bounds how long a pooled connection is reused so that DNS changes are eventually picked up, mirroring
    /// the guidance for <c>IHttpClientFactory</c>.
    /// </remarks>
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// Creates and configures a Minio client instance with the specified name and optional additional configuration.
    /// </summary>
    /// <param name="name">The name of the Minio configuration to use. This should match a named options configuration.</param>
    /// <param name="configureClient">An optional delegate for applying additional configuration to the client before building.</param>
    /// <returns>A fully configured instance of <see cref="IMinioClient"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the named configuration is missing required values — <see cref="MinioOptions.Endpoint"/>,
    /// <see cref="MinioOptions.AccessKey"/>, or <see cref="MinioOptions.SecretKey"/> — or when the underlying
    /// client cannot be built.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method retrieves the <see cref="MinioOptions"/> for the specified <paramref name="name"/>
    /// and applies all configured settings to a new Minio client instance. Optional settings are applied
    /// only when present.
    /// </para>
    /// <para>
    /// Required values are validated up front so a misspelled configuration section fails with a message that
    /// names the section, rather than surfacing later as an opaque error from the Minio client.
    /// </para>
    /// <para>
    /// After applying the options-based configuration, any additional configuration provided via the
    /// <paramref name="configureClient"/> delegate is applied, allowing for runtime customization and
    /// letting callers override anything the options set.
    /// </para>
    /// </remarks>
    /// <example>
    /// The following example shows how to use the factory to create a Minio client:
    /// <code>
    /// var factory = new MinioClientFactory(optionsMonitor);
    /// var client = factory.CreateClient("my-minio-config", client =>
    /// {
    ///     client.WithProxy(new WebProxy("http://proxy-server:8080"));
    /// });
    /// </code>
    /// </example>
    public IMinioClient CreateClient(string name, Action<IMinioClient>? configureClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var options = optionsMonitor.Get(name);

        Validate(name, options);

        var client = new MinioClient()
            .WithSSL(options.SSL)
            .WithEndpoint(options.Endpoint)
            .WithCredentials(options.AccessKey, options.SecretKey);

        if (options.Region is { } region)
            client.WithRegion(region);

        if (options.SessionToken is { } sessionToken)
            client.WithSessionToken(sessionToken);

        if (options.Timeout is { } timeout)
            client.WithTimeout(timeout);

        configureClient?.Invoke(client);

        UseSharedConnectionPool(client);

        return client.Build();
    }

    /// <summary>
    /// Verifies that the named configuration carries everything the Minio client requires.
    /// </summary>
    /// <param name="name">The configuration section name, used to make the error actionable.</param>
    /// <param name="options">The resolved options.</param>
    /// <exception cref="InvalidOperationException">Thrown when a required value is missing.</exception>
    /// <remarks>
    /// The Minio client rejects anonymous access at build time, so credentials are required rather than optional.
    /// </remarks>
    private static void Validate(string name, MinioOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new InvalidOperationException(
                $"Minio configuration '{name}' is missing '{nameof(MinioOptions.Endpoint)}'. " +
                $"Check that the '{name}' configuration section exists and is spelled correctly.");

        if (string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
            throw new InvalidOperationException(
                $"Minio configuration '{name}' must supply both '{nameof(MinioOptions.AccessKey)}' and " +
                $"'{nameof(MinioOptions.SecretKey)}'; the Minio client does not support anonymous access.");
    }

    /// <summary>
    /// Points the client at the factory-wide connection pool when it has no transport of its own.
    /// </summary>
    /// <param name="client">The client being configured.</param>
    /// <remarks>
    /// Skipped when <paramref name="client"/> already has an <c>HttpClient</c> or a proxy configured, because
    /// Minio only honours <c>Proxy</c> while it builds its own handler — injecting a transport here would
    /// silently disable a caller's <c>WithProxy</c>.
    /// </remarks>
    private static void UseSharedConnectionPool(IMinioClient client)
    {
        if (client.Config.HttpClient is not null || client.Config.Proxy is not null)
            return;

        client.WithHttpClient(new HttpClient(SharedHandler, disposeHandler: false), disposeHttpClient: true);
    }
}
