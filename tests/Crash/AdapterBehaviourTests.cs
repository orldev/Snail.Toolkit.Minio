using System.Collections.Frozen;
using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;
using Snail.Toolkit.Minio.Tests.Infrastructure;

namespace Snail.Toolkit.Minio.Tests.Crash;

/// <summary>
/// Behaviour that only shows up against a server that misbehaves on purpose.
/// </summary>
public class AdapterBehaviourTests
{
    /// <summary>
    /// The defect this replaces: a 503 was retried when it arrived over the read path and not when it
    /// arrived through the SDK, because the SDK's failures reached the adapter without a status. The same
    /// failure has to behave the same way whichever method the caller used.
    /// </summary>
    [Fact]
    public async Task ATransientAnswer_IsRetried_OnTheSdkPathToo()
    {
        using var transport = new HostileTransport(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<Error><Code>SlowDown</Code><Message>slow down</Message></Error>")
        });

        using var provider = Provider(transport, attempts: 2);

        var stat = await provider.GetRequiredService<IObjectStorage>().StatAsync("bucket", "object");

        Assert.False(stat.IsSuccess);
        Assert.Equal("SlowDown", stat.Error!.Code);
        Assert.Equal(3, transport.Requests);
    }

    /// <summary>
    /// The same for a server error, which S3 reports as <c>InternalError</c>.
    /// </summary>
    /// <remarks>
    /// The body matters more than it should: the SDK raises <c>NullReferenceException</c> on a 5xx with no
    /// body at all, and <c>ArgumentNullException</c> on an error document with no <c>Message</c> element.
    /// Real servers send both, so this sends both.
    /// </remarks>
    [Fact]
    public async Task AServerError_IsRetried_OnTheSdkPathToo()
    {
        using var transport = new HostileTransport(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("<Error><Code>InternalError</Code><Message>try later</Message></Error>")
        });

        using var provider = Provider(transport, attempts: 2);

        var removed = await provider.GetRequiredService<IObjectStorage>().RemoveAsync("bucket", "object");

        Assert.False(removed.IsSuccess);
        Assert.Equal("InternalError", removed.Error!.Code);
        Assert.Equal(3, transport.Requests);
    }

    /// <summary>
    /// The defect this replaces: every failure was logged at warning, so a workload that asks whether
    /// objects exist filled the log with alarms about ordinary answers.
    /// </summary>
    [Fact]
    public async Task AMissingObject_IsNotLoggedAsAWarning()
    {
        var log = new Spy();

        using var transport = new HostileTransport(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<Error><Code>NoSuchKey</Code></Error>")
        });

        using var provider = Provider(transport, register: services =>
            services.AddSingleton<ILogger<MinioObjectStorage>>(log));

        using var destination = new MemoryStream();
        await provider.GetRequiredService<IObjectStorage>().DownloadToAsync("bucket", "object", destination);

        Assert.DoesNotContain(LogLevel.Warning, log.Levels);
        Assert.Contains(LogLevel.Debug, log.Levels);
    }

    /// <summary>
    /// A server failure is still an alarm.
    /// </summary>
    [Fact]
    public async Task AServerFailure_IsStillLoggedAsAWarning()
    {
        var log = new Spy();

        using var transport = new HostileTransport(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(string.Empty)
        });

        using var provider = Provider(transport, register: services =>
            services.AddSingleton<ILogger<MinioObjectStorage>>(log));

        using var destination = new MemoryStream();
        await provider.GetRequiredService<IObjectStorage>().DownloadToAsync("bucket", "object", destination);

        Assert.Contains(LogLevel.Warning, log.Levels);
    }

    /// <summary>
    /// Metadata is handed out as something a caller cannot write through, whatever it casts it to.
    /// </summary>
    [Fact]
    public void ReportedMetadata_CannotBeWrittenThrough()
    {
        var stored = new StoredObject { Bucket = "bucket", Name = "object", Size = 0 };

        Assert.IsAssignableFrom<FrozenDictionary<string, string>>(stored.Metadata);
    }

    private static ServiceProvider Provider(
        MinioTransport transport,
        int attempts = 0,
        Action<IServiceCollection>? register = null)
    {
        var settings = MinioContainerFixture.Settings("localhost:9000", "access-key", "secret-key");

        settings["Minio:RetryAttempts"] = attempts.ToString();
        settings["Minio:RetryDelay"] = "00:00:00.001";

        return MinioContainerFixture.Provider(settings, services =>
        {
            services.Replace(ServiceDescriptor.Singleton(transport));
            register?.Invoke(services);
        });
    }

    private sealed class Spy : ILogger<MinioObjectStorage>
    {
        private readonly List<LogLevel> _levels = [];

        public IReadOnlyList<LogLevel> Levels
        {
            get
            {
                lock (_levels)
                    return _levels.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_levels)
                _levels.Add(logLevel);
        }
    }
}
