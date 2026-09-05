using System.Net;
using System.Net.Sockets;

namespace Snail.Toolkit.Minio.Tests.Infrastructure;

/// <summary>
/// A TCP endpoint that completes the handshake and then stays silent forever.
/// </summary>
/// <remarks>
/// Accepting the connection matters: a closed port would produce a refusal, which is a connection error
/// rather than a timeout. Holding the socket open makes the request hang until the client's own timeout
/// fires, which is the condition under test.
/// </remarks>
internal sealed class UnresponsiveServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _accepted = [];

    public UnresponsiveServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    /// <summary>Gets the <c>host:port</c> this server listens on.</summary>
    public string Endpoint => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Gets how many connections have been accepted so far.</summary>
    /// <remarks>
    /// A retried request opens a new connection, so this counts attempts as well.
    /// </remarks>
    public int Accepted
    {
        get
        {
            lock (_accepted)
                return _accepted.Count;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();

        lock (_accepted)
        {
            foreach (var client in _accepted)
                client.Dispose();
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);

                lock (_accepted)
                    _accepted.Add(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }
}
