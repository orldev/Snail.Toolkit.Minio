using Microsoft.Extensions.Options;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Builds Minio clients from named configuration.
/// </summary>
/// <remarks>
/// The vendor-level entry point, for code that needs the Minio SDK itself. Code that only stores and reads
/// objects should take <see cref="Ports.IObjectStorage"/> instead and stay independent of the SDK.
/// </remarks>
public interface IMinioClients
{
    /// <summary>
    /// Builds a client from the named configuration.
    /// </summary>
    /// <param name="name">The configuration to build from.</param>
    /// <param name="configureClient">Applied last, so it can override anything the configuration set.</param>
    /// <returns>A client the caller owns and must dispose.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    /// <exception cref="OptionsValidationException">The named configuration cannot be used.</exception>
    /// <remarks>
    /// A client holds connections. Resolve one per configuration and keep it, rather than building one per
    /// request; the connection pool is shared either way, but the client itself is not free.
    /// </remarks>
    IMinioClient Create(string name, Action<IMinioClient>? configureClient = null);
}
