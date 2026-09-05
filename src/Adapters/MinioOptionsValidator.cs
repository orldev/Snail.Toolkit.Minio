using Microsoft.Extensions.Options;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Judges a named <see cref="MinioOptions"/> before anything is built from it.
/// </summary>
/// <remarks>
/// <para>
/// One place decides, whichever way the settings arrived. Configuration used to be checked lazily inside
/// the client factory, so a section that was missing, misspelled, or carried a scheme in its endpoint
/// booted a green application that failed on its first request instead.
/// </para>
/// <para>
/// Registered for every name, and paired with <c>ValidateOnStart</c> so a bad configuration stops the host.
/// </para>
/// </remarks>
internal sealed class MinioOptionsValidator : IValidateOptions<MinioOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MinioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var fault = Fault(name ?? MinioOptions.SectionName, options);

        return fault is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(fault);
    }

    /// <summary>
    /// Finds the first thing wrong with a configuration.
    /// </summary>
    /// <param name="name">The configuration section, named so the message is actionable.</param>
    /// <param name="options">The settings to judge.</param>
    /// <returns>What is wrong, or <see langword="null"/> when the settings can be used.</returns>
    internal static string? Fault(string name, MinioOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            return $"'{name}:{nameof(MinioOptions.Endpoint)}' is missing. "
                + $"Check that the '{name}' configuration section exists and is spelled correctly.";

        if (options.Endpoint.Contains("://", StringComparison.Ordinal))
            return $"'{name}:{nameof(MinioOptions.Endpoint)}' has to be a bare 'host' or 'host:port', "
                + $"not '{options.Endpoint}'. Use '{nameof(MinioOptions.IsSecure)}' to choose http or https.";

        if (!Uri.TryCreate($"http://{options.Endpoint}", UriKind.Absolute, out var authority)
            || authority.Authority != options.Endpoint
            || !string.IsNullOrEmpty(authority.PathAndQuery.TrimEnd('/')))
            return $"'{name}:{nameof(MinioOptions.Endpoint)}' has to be a host with an optional port, "
                + $"not '{options.Endpoint}'.";

        if (string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
            return $"'{name}' has to supply both '{nameof(MinioOptions.AccessKey)}' and "
                + $"'{nameof(MinioOptions.SecretKey)}'; anonymous access is not supported by the client.";

        if (options.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            return $"'{name}:{nameof(MinioOptions.Timeout)}' has to be positive, not '{timeout}'. "
                + "A zero timeout fails every request.";

        if (options.ConnectionLifetime <= TimeSpan.Zero)
            return $"'{name}:{nameof(MinioOptions.ConnectionLifetime)}' has to be positive, "
                + $"not '{options.ConnectionLifetime}'.";

        if (options.MaxConnectionsPerServer is { } ceiling && ceiling <= 0)
            return $"'{name}:{nameof(MinioOptions.MaxConnectionsPerServer)}' has to be positive, not {ceiling}.";

        if (options.RetryAttempts is < 0 or > 100)
            return $"'{name}:{nameof(MinioOptions.RetryAttempts)}' has to be between 0 and 100, "
                + $"not {options.RetryAttempts}.";

        if (options.RetryDelay < TimeSpan.Zero || options.RetryDelay > TimeSpan.FromMinutes(1))
            return $"'{name}:{nameof(MinioOptions.RetryDelay)}' has to be between nothing and a minute, "
                + $"not '{options.RetryDelay}'; it doubles per attempt.";

        if (options.CircuitBreakFailures < 0)
            return $"'{name}:{nameof(MinioOptions.CircuitBreakFailures)}' cannot be negative, "
                + $"not {options.CircuitBreakFailures}; zero turns the circuit breaker off.";

        if (options.CircuitBreakDuration <= TimeSpan.Zero)
            return $"'{name}:{nameof(MinioOptions.CircuitBreakDuration)}' has to be positive, "
                + $"not '{options.CircuitBreakDuration}'.";

        if (options.SignedUrlLifetime < TimeSpan.FromSeconds(1) || options.SignedUrlLifetime > TimeSpan.FromDays(7))
            return $"'{name}:{nameof(MinioOptions.SignedUrlLifetime)}' has to be between one second and "
                + $"seven days, not '{options.SignedUrlLifetime}'; seven days is the ceiling S3 signing allows.";

        return null;
    }
}
