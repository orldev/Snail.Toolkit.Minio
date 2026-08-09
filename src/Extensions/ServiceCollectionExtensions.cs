using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Toolkit.Minio.Entities;

namespace Toolkit.Minio.Extensions;

/// <summary>
/// Provides extension methods for <see cref="IServiceCollection"/> to configure Minio client dependency injection.
/// These extensions simplify the process of registering Minio clients with the .NET dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Minio client services to the specified <see cref="IServiceCollection"/> using configuration from appsettings.json
    /// and allows for additional client configuration.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="configuration">The configuration instance containing Minio settings.</param>
    /// <param name="configureClient">An optional delegate for additional client configuration.</param>
    /// <param name="lifetime">The service lifetime for the Minio client. Defaults to <see cref="ServiceLifetime.Singleton"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    /// <remarks>
    /// <para>
    /// This method registers Minio services with the default configuration section name "Minio".
    /// It expects configuration to be available under the "Minio" section in the provided <paramref name="configuration"/>.
    /// </para>
    /// <para>
    /// The method registers the following services:
    /// <list type="bullet">
    /// <item><description><see cref="IMinioClientFactory"/> as singleton</description></item>
    /// <item><description><see cref="IMinioClient"/> with the specified lifetime</description></item>
    /// <item><description>Configuration options for <see cref="MinioOptions"/></description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <example>
    /// The following example shows how to use this method in Startup.cs or Program.cs:
    /// <code>
    /// // Using default configuration section "Minio"
    /// services.AddMinio(Configuration);
    /// 
    /// // With additional client configuration
    /// services.AddMinio(Configuration, client => 
    /// {
    ///     client.WithProxy("http://proxy:8080");
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddMinio(
        this IServiceCollection services, 
        IConfiguration configuration, 
        Action<IMinioClient>? configureClient = null,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        const string defaultName = "Minio";
        return services.AddMinio(defaultName, configuration, configureClient, lifetime);
    }

    /// <summary>
    /// Adds a named Minio client to the specified <see cref="IServiceCollection"/> with configuration from the specified section.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="name">The name of the Minio client configuration. This should match the configuration section name.</param>
    /// <param name="configuration">The configuration instance containing Minio settings.</param>
    /// <param name="configureClient">An optional delegate for additional client configuration.</param>
    /// <param name="lifetime">The service lifetime for the Minio client. Defaults to <see cref="ServiceLifetime.Singleton"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configuration"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="name"/> is null, empty, or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="lifetime"/> is not a valid <see cref="ServiceLifetime"/> value.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method allows for multiple named Minio client configurations within the same application.
    /// Each named client can have different endpoints, credentials, and settings.
    /// </para>
    /// <para>
    /// The configuration is expected to be available under the section named by <paramref name="name"/>
    /// in the provided <paramref name="configuration"/>.
    /// </para>
    /// <para>
    /// The service registration includes:
    /// <list type="bullet">
    /// <item><description>Named options configuration for <see cref="MinioOptions"/></description></item>
    /// <item><description><see cref="IMinioClientFactory"/> as singleton</description></item>
    /// <item><description><see cref="IMinioClient"/> with the specified lifetime</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// When resolving <see cref="IMinioClient"/> from the dependency injection container,
    /// the client will be configured with both the named options and any additional configuration
    /// provided via the <paramref name="configureClient"/> delegate.
    /// </para>
    /// <para>
    /// <b>Only the first call wins for the unnamed <see cref="IMinioClient"/> registration.</b> Named options
    /// accumulate across calls, but <see cref="IMinioClient"/> is registered with <c>TryAdd</c>, which matches on
    /// service type alone. A second <c>AddMinio</c> therefore leaves both the client and the <paramref name="lifetime"/>
    /// of the first call in place. Resolve additional configurations through
    /// <see cref="IMinioClientFactory.CreateClient"/> rather than expecting a second <see cref="IMinioClient"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// The following example shows how to register multiple named Minio clients:
    /// <code>
    /// // Register primary Minio client
    /// services.AddMinio("PrimaryMinio", Configuration, client => 
    /// {
    ///     client.WithTimeout(30000);
    /// });
    /// 
    /// // Register secondary Minio client with different configuration
    /// services.AddMinio("SecondaryMinio", Configuration, lifetime: ServiceLifetime.Scoped);
    /// 
    /// // Later, resolve using the factory
    /// var factory = serviceProvider.GetRequiredService&lt;IMinioClientFactory&gt;();
    /// var primaryClient = factory.CreateClient("PrimaryMinio");
    /// var secondaryClient = factory.CreateClient("SecondaryMinio");
    /// </code>
    /// </example>
    public static IServiceCollection AddMinio(
        this IServiceCollection services, 
        string name, 
        IConfiguration configuration,
        Action<IMinioClient>? configureClient = null,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        services.Configure<MinioOptions>(name, configuration.GetSection(name));
        services.TryAddSingleton<IMinioClientFactory, MinioClientFactory>();
        switch (lifetime)
        {
            case ServiceLifetime.Singleton:
                services.TryAddSingleton(ImplementationFactory);
                break;
            case ServiceLifetime.Scoped:
                services.TryAddScoped(ImplementationFactory);
                break;
            case ServiceLifetime.Transient:
                services.TryAddTransient(ImplementationFactory);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, null);
        }
        return services;

        IMinioClient ImplementationFactory(IServiceProvider sp) => sp
            .GetRequiredService<IMinioClientFactory>().CreateClient(name, configureClient);
    }
}