namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Everything needed to reach one Minio or S3-compatible server.
/// </summary>
/// <remarks>
/// <para>
/// Bound from configuration under a section named after the registration, which defaults to
/// <see cref="SectionName"/>. Registering several names gives several servers side by side.
/// </para>
/// <para>
/// The settings are validated when the host starts, not when the first request runs, so a misspelled
/// section stops the application instead of surfacing as a storage failure under load.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// {
///   "Minio": {
///     "Endpoint": "play.min.io",
///     "AccessKey": "your-access-key",
///     "SecretKey": "your-secret-key",
///     "Region": "us-east-1",
///     "IsSecure": true,
///     "Timeout": "00:00:30"
///   }
/// }
/// </code>
/// </example>
public sealed record MinioOptions
{
    /// <summary>The configuration section read when no name is given.</summary>
    public const string SectionName = "Minio";

    /// <summary>Gets the server's authority, as <c>host</c> or <c>host:port</c>.</summary>
    /// <remarks>
    /// Required, and stated without a scheme: whether the connection is encrypted is
    /// <see cref="IsSecure"/>, not a prefix. A value carrying <c>http://</c> is rejected at startup rather
    /// than failing later inside the client.
    /// </remarks>
    public string? Endpoint { get; init; }

    /// <summary>Gets the access key to authenticate with.</summary>
    public string? AccessKey { get; init; }

    /// <summary>Gets the secret key to authenticate with.</summary>
    public string? SecretKey { get; init; }

    /// <summary>Gets the region API calls are made against.</summary>
    public string? Region { get; init; }

    /// <summary>Gets the session token for temporary credentials.</summary>
    public string? SessionToken { get; init; }

    /// <summary>Gets a value indicating whether connections are encrypted.</summary>
    public bool IsSecure { get; init; } = true;

    /// <summary>Gets how long a single request may take before it is abandoned.</summary>
    /// <remarks>
    /// Left unset, the underlying client's own default applies. This bounds a request, not a download: the
    /// body of an object streams under the caller's cancellation token.
    /// </remarks>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets how long a pooled connection is reused before it is replaced.</summary>
    /// <remarks>
    /// Bounded so that DNS changes are eventually picked up, mirroring the guidance for
    /// <c>IHttpClientFactory</c>.
    /// </remarks>
    public TimeSpan ConnectionLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the ceiling on simultaneous connections to the server.</summary>
    /// <remarks>Left unset, the platform default applies, which is unbounded.</remarks>
    public int? MaxConnectionsPerServer { get; init; }

    /// <summary>Gets how many times a failed idempotent request is retried.</summary>
    /// <remarks>
    /// Only reads and deletes are retried, and only when the failure was a lost connection or a timeout.
    /// An upload is never retried: its stream may already be partly consumed.
    /// </remarks>
    public int RetryAttempts { get; init; } = 2;

    /// <summary>Gets the delay before the first retry; each further attempt doubles it.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Gets how long a signed read URL stays valid.</summary>
    /// <remarks>
    /// Reads are issued against a URL this library signs itself. The URL never leaves the process, so this
    /// only has to outlive the time between signing the request and the server answering it.
    /// </remarks>
    public TimeSpan SignedUrlLifetime { get; init; } = TimeSpan.FromMinutes(1);
}
