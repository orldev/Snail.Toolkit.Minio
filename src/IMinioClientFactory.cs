namespace Toolkit.Minio;

/// <summary>
/// A factory interface for creating and configuring Minio client instances.
/// This factory pattern allows for centralized management and configuration of Minio clients,
/// supporting multiple named client configurations within an application.
/// </summary>
public interface IMinioClientFactory
{
    /// <summary>
    /// Creates and configures a Minio client instance with the specified name.
    /// </summary>
    /// <param name="name">The name of the client configuration. This name can be used to identify 
    /// and retrieve specific client configurations from the factory.</param>
    /// <param name="configureClient">An optional delegate that can be used to apply additional 
    /// configuration to the Minio client instance before it is returned.</param>
    /// <returns>A configured instance of <see cref="IMinioClient"/>.</returns>
    /// <remarks>
    /// <para>
    /// The factory pattern implemented by this interface enables:
    /// <list type="bullet">
    /// <item><description>Centralized configuration management for Minio clients</description></item>
    /// <item><description>Support for multiple Minio endpoints with different configurations</description></item>
    /// <item><description>Lifetime management of client instances</description></item>
    /// <item><description>Consistent configuration application across the application</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Implementations of this factory should handle the proper initialization and configuration
    /// of Minio clients based on the provided name and configuration delegate.
    /// </para>
    /// </remarks>
    /// <example>
    /// The following example demonstrates how to use the factory to create a Minio client:
    /// <code>
    /// var factory = serviceProvider.GetRequiredService&lt;IMinioClientFactory&gt;();
    /// var client = factory.CreateClient("primary", client => 
    /// {
    ///     client.WithTimeout(30000);
    /// });
    /// </code>
    /// </example>
    IMinioClient CreateClient(string name, Action<IMinioClient>? configureClient = null);
}