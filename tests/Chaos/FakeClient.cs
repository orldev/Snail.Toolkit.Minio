using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Minio.Adapters;

namespace Snail.Toolkit.Minio.Tests.Chaos;

/// <summary>
/// A Minio client answering only the calls a test names, in process.
/// </summary>
/// <remarks>
/// For the paths that go through the SDK rather than the library's own reads — a listing, a bucket, an upload —
/// where the failure under test is a race or a lie the server tells, not something a transport can stage.
/// </remarks>
public class FakeClient : DispatchProxy
{
    private readonly Dictionary<string, Func<object?[], object?>> _answers = new(StringComparer.Ordinal);

    /// <summary>A client answering each named SDK method with the given function.</summary>
    public static IMinioClient Answering(params (string Method, Func<object?[], object?> Answer)[] answers)
    {
        var client = DispatchProxy.Create<IMinioClient, FakeClient>();
        var fake = (FakeClient)(object)client;

        foreach (var (method, answer) in answers)
            fake._answers[method] = answer;

        return client;
    }

    /// <summary>Storage over a fake client, with no retries and the circuit breaker off.</summary>
    public static MinioObjectStorage Storage(IMinioClient client, HttpClient? http = null)
        => new(
            MinioOptions.SectionName,
            client,
            http ?? new HttpClient(),
            new Monitor(new MinioOptions { Endpoint = "minio.local", AccessKey = "a", SecretKey = "b", RetryAttempts = 0, CircuitBreakFailures = 0 }),
            NullLogger<MinioObjectStorage>.Instance);

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => _answers.TryGetValue(targetMethod!.Name, out var answer)
            ? answer(args ?? [])
            : throw new NotSupportedException(targetMethod.Name);

    private sealed class Monitor(MinioOptions options) : IOptionsMonitor<MinioOptions>
    {
        public MinioOptions CurrentValue => options;

        public MinioOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<MinioOptions, string?> listener) => null;
    }
}
