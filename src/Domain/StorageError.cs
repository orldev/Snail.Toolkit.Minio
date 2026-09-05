namespace Snail.Toolkit.Minio.Domain;

/// <summary>
/// Why an operation failed, in the amount of detail a caller can act on.
/// </summary>
/// <param name="Kind">The classification to branch on.</param>
/// <param name="Message">A human-readable description of the failure.</param>
/// <remarks>
/// The original exception is kept on <see cref="Cause"/> instead of being flattened into
/// <paramref name="Message"/>. Losing it is what turns a production incident into guesswork: the message
/// alone says nothing about the HTTP status, the S3 error code, or the stack that produced it.
/// </remarks>
public sealed record StorageError(StorageErrorKind Kind, string Message)
{
    /// <summary>Gets the HTTP status the server answered with, when the failure came from the server.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Gets the S3 error code the server answered with, such as <c>NoSuchKey</c>.</summary>
    public string? Code { get; init; }

    /// <summary>Gets the exception this error was translated from.</summary>
    public Exception? Cause { get; init; }

    /// <summary>Gets how long the server asked the caller to wait before trying again.</summary>
    /// <remarks>
    /// Read from <c>Retry-After</c>. A server under load says here what it wants, and a client that waits
    /// its own interval instead is part of what is keeping the server under load.
    /// </remarks>
    public TimeSpan? RetryAfter { get; init; }

    /// <inheritdoc />
    public override string ToString()
        => Code is null
            ? $"{Kind}: {Message}"
            : $"{Kind} ({Code}): {Message}";
}
