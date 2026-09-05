using System.Reflection;
using Snail.Toolkit.Minio.Adapters;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// What a configuration has to say before anything is built from it.
/// </summary>
/// <remarks>
/// The defect this replaces: three settings were checked, lazily, inside the client factory. A zero timeout
/// and an endpoint carrying a scheme both passed, and surfaced later as a storage failure that named the
/// network rather than the configuration.
/// </remarks>
public class OptionsValidationTests
{
    [Fact]
    public void CompleteSettings_AreAccepted()
        => Assert.Null(MinioOptionsValidator.Fault("Minio", Valid()));

    [Fact]
    public void AMissingEndpoint_NamesTheSection()
    {
        var fault = MinioOptionsValidator.Fault("Archive", Valid() with { Endpoint = null });

        Assert.Contains("Archive", fault!, StringComparison.Ordinal);
        Assert.Contains(nameof(MinioOptions.Endpoint), fault, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://minio.example")]
    [InlineData("http://minio.example:9000")]
    public void AnEndpointCarryingAScheme_IsRejected(string endpoint)
    {
        var fault = MinioOptionsValidator.Fault("Minio", Valid() with { Endpoint = endpoint });

        Assert.Contains(nameof(MinioOptions.IsSecure), fault!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("minio.example/bucket")]
    [InlineData("minio example")]
    public void AnEndpointThatIsNotAnAuthority_IsRejected(string endpoint)
        => Assert.NotNull(MinioOptionsValidator.Fault("Minio", Valid() with { Endpoint = endpoint }));

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("access", null)]
    [InlineData(null, null)]
    public void HalfACredential_IsRejected(string? accessKey, string? secretKey)
        => Assert.NotNull(MinioOptionsValidator.Fault(
            "Minio",
            Valid() with { AccessKey = accessKey, SecretKey = secretKey }));

    [Fact]
    public void AZeroTimeout_IsRejected()
    {
        var fault = MinioOptionsValidator.Fault("Minio", Valid() with { Timeout = TimeSpan.Zero });

        Assert.Contains(nameof(MinioOptions.Timeout), fault!, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignedUrlLifetimeBeyondWhatSigningAllows_IsRejected()
        => Assert.NotNull(MinioOptionsValidator.Fault(
            "Minio",
            Valid() with { SignedUrlLifetime = TimeSpan.FromDays(8) }));

    /// <summary>
    /// Guards the rule rather than the cases: a duration or count added later has to reject a negative
    /// value too, and this fails until it does.
    /// </summary>
    /// <remarks>
    /// Negative rather than zero, because zero is a legitimate value for some of them: no delay between
    /// retries is a choice, while a negative one is nonsense whichever setting it is.
    /// </remarks>
    [Fact]
    public void EveryDurationAndCount_IsChecked()
    {
        var settings = typeof(MinioOptions).GetProperties()
            .Where(property => property.PropertyType == typeof(TimeSpan)
                || property.PropertyType == typeof(TimeSpan?)
                || property.PropertyType == typeof(int)
                || property.PropertyType == typeof(int?))
            .ToArray();

        Assert.NotEmpty(settings);

        foreach (var setting in settings)
        {
            var options = Valid();

            setting.SetValue(
                options,
                setting.PropertyType == typeof(int) || setting.PropertyType == typeof(int?)
                    ? -1
                    : TimeSpan.FromSeconds(-1));

            var fault = MinioOptionsValidator.Fault("Minio", options);

            Assert.NotNull(fault);
            Assert.Contains(setting.Name, fault, StringComparison.Ordinal);
        }
    }

    private static MinioOptions Valid() => new()
    {
        Endpoint = "minio.example:9000",
        AccessKey = "access-key",
        SecretKey = "secret-key"
    };
}
