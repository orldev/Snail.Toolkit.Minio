using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Owns the connection pools every client of this library shares.
/// </summary>
/// <remarks>
/// <para>
/// A pool per named configuration, not one per client: creating a client per request used to open a fresh
/// pool each time and exhaust sockets, while a single process-wide pool made
/// <see cref="MinioOptions.MaxConnectionsPerServer"/> and <see cref="MinioOptions.ConnectionLifetime"/>
/// meaningless for anyone talking to two servers at once.
/// </para>
/// <para>
/// Registered as a singleton and disposed with the container, which is what closes the sockets.
/// </para>
/// <para>
/// Left open rather than sealed: <see cref="CreateClient"/> is the seam a test uses to answer reads without
/// a server, and the only way to put a custom handler under this library's own requests.
/// </para>
/// </remarks>
public class MinioTransport : IDisposable
{
    private readonly ConcurrentDictionary<string, SocketsHttpHandler> _handlers = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<MinioOptions>? _options;

    private bool _disposed;

    /// <summary>
    /// Builds pools from named settings.
    /// </summary>
    /// <param name="options">The named settings each pool is built from.</param>
    public MinioTransport(IOptionsMonitor<MinioOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <summary>
    /// Builds a transport that consults no settings.
    /// </summary>
    /// <remarks>
    /// For substitutes that answer requests themselves. A derived class using this has to override
    /// <see cref="CreateClient"/>, because there is nothing here to build a pool from.
    /// </remarks>
    protected MinioTransport()
    {
    }

    /// <summary>
    /// Creates a client bound to the pool of the named configuration.
    /// </summary>
    /// <param name="name">The configuration whose pool to use.</param>
    /// <returns>A client the caller may dispose freely; the pool outlives it.</returns>
    /// <remarks>
    /// The client itself imposes no timeout. A read streams a body of unknown size, so bounding the whole
    /// exchange would cut off large downloads; the request phase is bounded by
    /// <see cref="MinioOptions.Timeout"/> where it is applied, and the body by the caller's token.
    /// </remarks>
    public virtual HttpClient CreateClient(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return new HttpClient(_handlers.GetOrAdd(name, Pool), disposeHandler: false)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
    }

    /// <inheritdoc />
    public virtual void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var handler in _handlers.Values)
        {
            handler.Dispose();
        }

        _handlers.Clear();
    }

    /// <summary>
    /// Builds the pool for one named configuration.
    /// </summary>
    /// <param name="name">The configuration to read.</param>
    /// <returns>A handler owning that configuration's connections.</returns>
    private SocketsHttpHandler Pool(string name)
    {
        var settings = (_options ?? throw new InvalidOperationException(
            "This transport was built without settings, so it cannot open a pool. Override CreateClient."))
            .Get(name);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = settings.ConnectionLifetime
        };

        if (settings.MaxConnectionsPerServer is { } ceiling)
            handler.MaxConnectionsPerServer = ceiling;

        return handler;
    }
}
