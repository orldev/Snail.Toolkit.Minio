using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Ports;

namespace Snail.Toolkit.Minio.Extensions;

/// <summary>
/// Registers object storage with the dependency injection container.
/// </summary>
/// <remarks>
/// <para>
/// One server is registered with <see cref="AddMinio"/> and resolved as
/// <see cref="IObjectStorage"/>, or as <see cref="IBuckets"/> for the containers objects live in. Further servers are registered with <see cref="AddKeyedMinio"/> and
/// resolved by their key, because a container holds one unkeyed registration per service type and a second
/// unkeyed <c>AddMinio</c> would silently be ignored.
/// </para>
/// <para>
/// Everything is a singleton. A storage client owns connections and is safe to share, while a per-request
/// one would leave the container holding every client it ever built until the application stopped.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the default server, read from the <c>Minio</c> configuration section.
        /// </summary>
        /// <param name="configuration">The configuration to bind from.</param>
        /// <param name="sectionName">The section to bind, when it is not named <c>Minio</c>.</param>
        /// <param name="configureClient">Applied last to the SDK client, overriding what configuration set.</param>
        /// <returns>The same container, so calls can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
        /// <example>
        /// <code>
        /// services.AddMinio(builder.Configuration);
        ///
        /// var stored = await storage.PutAsync("reports", file.OpenReadStream());
        /// </code>
        /// </example>
        public IServiceCollection AddMinio(
            IConfiguration configuration,
            string sectionName = MinioOptions.SectionName,
            Action<IMinioClient>? configureClient = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

            services.AddKeyedMinio(MinioOptions.SectionName, configuration, sectionName, configureClient);

            services.TryAddSingleton(sp => sp.GetRequiredKeyedService<IMinioClient>(MinioOptions.SectionName));
            services.TryAddSingleton(sp => sp.GetRequiredKeyedService<IObjectStorage>(MinioOptions.SectionName));
            services.TryAddSingleton(sp => sp.GetRequiredKeyedService<IBuckets>(MinioOptions.SectionName));

            return services;
        }

        /// <summary>
        /// Registers a further server under a key of its own.
        /// </summary>
        /// <param name="name">The key, which is also the name of the options instance.</param>
        /// <param name="configuration">The configuration to bind from.</param>
        /// <param name="sectionName">The section to bind, when it is not named after the key.</param>
        /// <param name="configureClient">Applied last to the SDK client, overriding what configuration set.</param>
        /// <returns>The same container, so calls can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
        /// <example>
        /// <code>
        /// services.AddKeyedMinio("archive", builder.Configuration);
        ///
        /// public sealed class Reports([FromKeyedServices("archive")] IObjectStorage archive);
        /// </code>
        /// </example>
        public IServiceCollection AddKeyedMinio(
            string name,
            IConfiguration configuration,
            string? sectionName = null,
            Action<IMinioClient>? configureClient = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            services.AddOptions<MinioOptions>(name)
                .Bind(configuration.GetSection(sectionName ?? name))
                .ValidateOnStart();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<MinioOptions>, MinioOptionsValidator>());

            services.TryAddSingleton<MinioTransport>();
            services.TryAddSingleton<IMinioClients, MinioClients>();

            services.TryAddKeyedSingleton<IMinioClient>(
                name,
                (provider, _) => provider.GetRequiredService<IMinioClients>().Create(name, configureClient));

            services.TryAddKeyedSingleton(
                name,
                (provider, _) => new MinioObjectStorage(
                    name,
                    provider.GetRequiredKeyedService<IMinioClient>(name),
                    provider.GetRequiredService<MinioTransport>().CreateClient(name),
                    provider.GetRequiredService<IOptionsMonitor<MinioOptions>>(),
                    provider.GetService<ILogger<MinioObjectStorage>>() ?? NullLogger<MinioObjectStorage>.Instance));

            services.TryAddKeyedSingleton<IObjectStorage>(
                name,
                (provider, _) => provider.GetRequiredKeyedService<MinioObjectStorage>(name));

            services.TryAddKeyedSingleton<IBuckets>(
                name,
                (provider, _) => provider.GetRequiredKeyedService<MinioObjectStorage>(name));

            return services;
        }
    }
}
