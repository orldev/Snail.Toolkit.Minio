using Microsoft.Extensions.Configuration;

namespace Toolkit.Minio.Tests;

/// <summary>
/// Synthetic configuration for tests that inspect registration and option binding without opening a connection.
/// </summary>
/// <remarks>
/// The endpoint is deliberately unroutable: nothing here is meant to be contacted. Tests that need a server
/// get their settings from <see cref="Infrastructure.MinioContainerFixture"/>, which publishes a real one.
/// </remarks>
public static class TestConfiguration
{
    /// <summary>First named client configuration.</summary>
    public const string Client1 = "Minio";

    /// <summary>Second named client configuration, used to test multiple registrations.</summary>
    public const string Client2 = "Minio2";

    private static readonly Dictionary<string, string?> Settings = new()
    {
        [$"{Client1}:Endpoint"] = "minio.example:9000",
        [$"{Client1}:AccessKey"] = "access-key",
        [$"{Client1}:SecretKey"] = "secret-key",
        [$"{Client1}:Region"] = "eu-west-1",
        [$"{Client1}:SessionToken"] = "session-token",
        [$"{Client1}:Timeout"] = "2000",
        [$"{Client1}:SSL"] = "false",

        [$"{Client2}:Endpoint"] = "minio.example:9000",
        [$"{Client2}:AccessKey"] = "access-key-2",
        [$"{Client2}:SecretKey"] = "secret-key-2",
        [$"{Client2}:SSL"] = "false"
    };

    /// <summary>
    /// Builds configuration containing every named client above.
    /// </summary>
    /// <returns>Configuration ready to pass to <c>AddMinio</c>.</returns>
    public static IConfiguration Build()
        => new ConfigurationBuilder().AddInMemoryCollection(Settings).Build();
}
