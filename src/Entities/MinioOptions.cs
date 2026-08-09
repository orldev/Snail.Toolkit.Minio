namespace Toolkit.Minio.Entities;

/// <summary>
/// Represents configuration options for Minio client connections.
/// This class contains all the necessary settings to configure a Minio client instance
/// for connecting to Minio storage or Amazon S3 compatible services.
/// </summary>
/// <remarks>
/// <para>
/// This options class is typically used with the Options Pattern in .NET applications,
/// allowing for configuration through appsettings.json, environment variables, or other configuration providers.
/// </para>
/// <para>
/// <see cref="Endpoint"/>, <see cref="AccessKey"/>, and <see cref="SecretKey"/> are required; the remaining
/// properties are optional. The section name matches the name passed to <c>AddMinio</c> and defaults to "Minio".
/// </para>
/// </remarks>
/// <example>
/// Example configuration in appsettings.json:
/// <code>
/// {
///   "Minio": {
///     "Endpoint": "play.min.io",
///     "AccessKey": "your-access-key",
///     "SecretKey": "your-secret-key",
///     "Region": "us-east-1",
///     "SSL": true,
///     "Timeout": 30000
///   }
/// }
/// </code>
/// </example>
public class MinioOptions
{
    /// <summary>
    /// Gets or sets the endpoint URL for the Minio service. This can be a domain name, IPv4 address, or IPv6 address.
    /// </summary>
    /// <value>
    /// Valid endpoints include:
    /// <list type="bullet">
    /// <item><description>s3.amazonaws.com (Amazon S3)</description></item>
    /// <item><description>play.min.io (Minio Play service)</description></item>
    /// <item><description>localhost (local Minio instance)</description></item>
    /// <item><description>127.0.0.1 (local Minio instance)</description></item>
    /// <item><description>Any other S3-compatible service endpoint</description></item>
    /// </list>
    /// </value>
    /// <remarks>
    /// Required. <see cref="IMinioClientFactory.CreateClient"/> throws an
    /// <see cref="InvalidOperationException"/> naming the configuration section when this value is missing.
    /// </remarks>
    public string? Endpoint { get; set; }
    
    /// <summary>
    /// Gets or sets the access key (user ID) that uniquely identifies your account.
    /// </summary>
    /// <value>
    /// The access key string used for authentication. Required — the underlying Minio client rejects
    /// anonymous access when the client is built.
    /// </value>
    /// <remarks>
    /// For Minio, this is typically a randomly generated string. For AWS S3, this is your AWS Access Key ID.
    /// </remarks>
    public string? AccessKey { get; set; }
    
    /// <summary>
    /// Gets or sets the secret key (password) for your account.
    /// </summary>
    /// <value>
    /// The secret key string used for authentication. Required, and must be provided together with
    /// <see cref="AccessKey"/>.
    /// </value>
    /// <remarks>
    /// This value should be kept secure and never exposed in client-side code or public repositories.
    /// For AWS S3, this is your AWS Secret Access Key.
    /// </remarks>
    public string? SecretKey { get; set; }
    
    /// <summary>
    /// Gets or sets the region to which API calls should be made.
    /// </summary>
    /// <value>
    /// The AWS region name (e.g., "us-east-1", "eu-west-1") or custom region for S3-compatible services.
    /// This field is optional and can be omitted for some services or when using default regions.
    /// </value>
    /// <remarks>
    /// For Minio, this is often not required as Minio typically doesn't use regions in the same way as AWS S3.
    /// However, it may be required for certain S3-compatible services or specific bucket configurations.
    /// </remarks>
    public string? Region { get; set; }
    
    /// <summary>
    /// Gets or sets the session token for temporary security credentials.
    /// </summary>
    /// <value>
    /// The session token string used for temporary credentials. This field is optional and only needed when
    /// using temporary access credentials (such as AWS STS tokens).
    /// </value>
    /// <remarks>
    /// Session tokens are typically used with AWS Security Token Service (STS) or when assuming IAM roles.
    /// When using session tokens, both <see cref="AccessKey"/> and <see cref="SecretKey"/> must also be provided.
    /// </remarks>
    public string? SessionToken { get; set; }
    
    /// <summary>
    /// Gets or sets the timeout for all HTTP requests made by the Minio client.
    /// </summary>
    /// <value>
    /// The timeout duration in milliseconds. If not specified, the client will use a default timeout.
    /// Set to <c>null</c> to use the system default timeout.
    /// </value>
    /// <remarks>
    /// This timeout applies to all operations including uploads, downloads, and bucket operations.
    /// For large file transfers, consider setting an appropriate timeout value to avoid premature termination.
    /// </remarks>
    public int? Timeout { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use HTTPS (SSL/TLS) for connections to the Minio service.
    /// </summary>
    /// <value>
    /// <c>true</c> to enable HTTPS connections (recommended for production); <c>false</c> to use HTTP.
    /// Default value is <c>true</c>.
    /// </value>
    /// <remarks>
    /// <para>
    /// It is strongly recommended to keep this setting as <c>true</c> (default) for production environments
    /// to ensure encrypted communication with the storage service.
    /// </para>
    /// <para>
    /// Set to <c>false</c> only for development purposes or when connecting to services that don't support HTTPS.
    /// </para>
    /// </remarks>
    public bool SSL { get; set; } = true;
}