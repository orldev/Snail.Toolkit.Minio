using Microsoft.Extensions.Options;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Builds Minio clients from named configuration, over pools the process shares.
/// </summary>
/// <remarks>
/// <para>
/// Settings are read through <see cref="IOptionsMonitor{TOptions}"/>, which runs
/// <see cref="MinioOptionsValidator"/> as it resolves them: a configuration that cannot be used never
/// reaches the SDK.
/// </para>
/// <para>
/// Every client is given a client from <see cref="MinioTransport"/> unless the caller configured a
/// transport of its own. The exception matters: Minio honours a proxy only while it builds its own handler,
/// so injecting one here would silently disable <c>WithProxy</c>.
/// </para>
/// </remarks>
/// <param name="options">The named settings to build from.</param>
/// <param name="transport">The connection pools clients share.</param>
public sealed class MinioClients(IOptionsMonitor<MinioOptions> options, MinioTransport transport)
    : IMinioClients
{
    /// <inheritdoc />
    public IMinioClient Create(string name, Action<IMinioClient>? configureClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var settings = options.Get(name);

        if (MinioOptionsValidator.Fault(name, settings) is { } fault)
            throw new OptionsValidationException(name, typeof(MinioOptions), [fault]);

        var client = new MinioClient()
            .WithSSL(settings.IsSecure)
            .WithEndpoint(settings.Endpoint!)
            .WithCredentials(settings.AccessKey!, settings.SecretKey!);

        if (settings.Region is { } region)
            client.WithRegion(region);

        if (settings.SessionToken is { } sessionToken)
            client.WithSessionToken(sessionToken);

        if (settings.Timeout is { } timeout)
            client.WithTimeout((int)timeout.TotalMilliseconds);

        configureClient?.Invoke(client);

        if (client.Config.HttpClient is null && client.Config.Proxy is null)
            client.WithHttpClient(transport.CreateClient(name), disposeHttpClient: true);

        return client.Build();
    }
}
