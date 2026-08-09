using System.Net;
using System.Net.Sockets;
using Toolkit.Minio.Entities;
using Toolkit.Minio.Extensions;
using Toolkit.Minio.Tests.Infrastructure;

namespace Toolkit.Minio.Tests;

/// <summary>
/// Verifies that each failure mode a caller can realistically hit maps to a distinct <see cref="MinioErrorType"/>.
/// </summary>
/// <remarks>
/// <para>
/// The point of the result pattern is that callers can branch on <see cref="MinioResult.ErrorType"/> instead of
/// matching exception types or parsing messages. That promise is only worth anything if the mapping is right for
/// failures produced by a real server and a real socket.
/// </para>
/// <para>
/// Provoking the failures for real rather than simulating them is what makes this class useful: the
/// <see cref="MinioErrorType.Connection"/> and <see cref="MinioErrorType.Timeout"/> cases below were both
/// reported as <see cref="MinioErrorType.UnexpectedError"/> until a live server exposed it.
/// </para>
/// </remarks>
[Collection(MinioContainerCollection.Name)]
public class MinioErrorMappingTests(MinioContainerFixture fixture)
{
    [Fact]
    public async Task MissingBucket_MapsToBucketNotFound()
    {
        var result = await fixture.CreateClient().StatObjectAsync("no-such-bucket-anywhere", "object");

        Assert.Equal(MinioErrorType.BucketNotFound, result.ErrorType);
    }

    [Fact]
    public async Task MissingObject_InAnExistingBucket_MapsToObjectNotFound()
    {
        var client = fixture.CreateClient();

        await MinioContainerFixture.WithBucketAsync(client, async bucket =>
        {
            var result = await client.StatObjectAsync(bucket, "never-uploaded");

            Assert.Equal(MinioErrorType.ObjectNotFound, result.ErrorType);
        });
    }

    [Fact]
    public async Task BucketNameRejectedByTheClient_MapsToInvalidBucketName()
    {
        // Rejected locally, before any request is sent.
        var result = await fixture.CreateClient().StatObjectAsync("ab", "object");

        Assert.Equal(MinioErrorType.InvalidBucketName, result.ErrorType);
    }

    [Fact]
    public async Task WrongCredentials_MapToAccessDenied()
    {
        var client = MinioContainerFixture.CreateClient(fixture.Endpoint, "wrong-key", "wrong-secret-value");

        var result = await client.StatObjectAsync("any-bucket-name", "object");

        Assert.Equal(MinioErrorType.AccessDenied, result.ErrorType);
    }

    [Fact]
    public async Task UnreachableEndpoint_MapsToConnection()
    {
        // Port 1 refuses connections, so the failure surfaces as a transport error rather than a Minio one.
        var client = MinioContainerFixture.CreateClient("127.0.0.1:1", fixture.AccessKey, fixture.SecretKey);

        var result = await client.StatObjectAsync("any-bucket-name", "object");

        Assert.Equal(MinioErrorType.Connection, result.ErrorType);
    }

    [Fact]
    public async Task ExpiredRequestTimeout_MapsToTimeout()
    {
        // The Minio client enforces MinioOptions.Timeout with an internal cancellation token. That surfaces as
        // an OperationCanceledException which must not be confused with the caller cancelling.
        //
        // The server here accepts the connection and then never answers, so the timeout is guaranteed to be
        // what fails. Pointing a very short timeout at the real container instead would be a race: clients
        // share a connection pool, so a warm connection can complete the request first and return a genuine
        // BucketNotFound.
        using var unresponsive = new UnresponsiveServer();

        var client = MinioContainerFixture.CreateClient(
            unresponsive.Endpoint, fixture.AccessKey, fixture.SecretKey, timeoutMs: 500);

        var result = await client.StatObjectAsync("any-bucket-name", "object");

        Assert.Equal(MinioErrorType.Timeout, result.ErrorType);
    }

    [Fact]
    public async Task CallerCancellation_Throws_AndIsNotReportedAsAResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var client = fixture.CreateClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.StatObjectAsync("any-bucket-name", "object", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task CallerCancellation_Throws_FromOperationsWithoutAReturnValue()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var client = fixture.CreateClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.RemoveObjectAsync("any-bucket-name", "object", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task CallerCancellation_Throws_AndDoesNotLeakTheDownloadBuffer()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var client = fixture.CreateClient();

        // The MemoryStream overload allocates before calling; cancellation must dispose it and rethrow
        // rather than hand back a result.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DownloadObjectAsync("any-bucket-name", "object", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task DownloadOfAMissingBucket_FailsWithoutAValue()
    {
        var result = await fixture.CreateClient().DownloadObjectAsync("no-such-bucket-anywhere", "object");

        Assert.Equal(MinioErrorType.BucketNotFound, result.ErrorType);
        Assert.Null(result.Value);
    }

    /// <summary>
    /// A TCP endpoint that completes the handshake and then stays silent forever.
    /// </summary>
    /// <remarks>
    /// Accepting the connection matters: a closed port would produce a refusal, which is a connection error
    /// rather than a timeout. Holding the socket open makes the request hang until the client's own timeout
    /// fires, which is the condition under test.
    /// </remarks>
    private sealed class UnresponsiveServer : IDisposable
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
                // Shutting down.
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
    }
}
