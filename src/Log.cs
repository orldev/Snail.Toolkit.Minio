using Microsoft.Extensions.Logging;
using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio;

/// <summary>
/// What the storage says about an operation that did not succeed at once.
/// </summary>
/// <remarks>
/// A retry is routine and logged at debug. A failure is logged at the level the caller decides: debug for what a
/// caller expects to happen, such as an object that is not there, and a warning for the rest.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Minio {Operation} failed with {Kind}, retrying in {Delay}. {Message}")]
    internal static partial void Retrying(ILogger logger, string operation, StorageErrorKind kind, TimeSpan delay, string message);

    [LoggerMessage(EventId = 2,
        Message = "Minio {Operation} on {Bucket}/{Object} failed with {Kind}{Status}. {Message}")]
    internal static partial void Failed(
        ILogger logger,
        LogLevel level,
        string operation,
        string bucket,
        string @object,
        StorageErrorKind kind,
        string status,
        string message);
}
