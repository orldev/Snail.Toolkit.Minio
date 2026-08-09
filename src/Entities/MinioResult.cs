using System.Diagnostics.CodeAnalysis;

namespace Toolkit.Minio.Entities;

/// <summary>
/// Represents the result of a Minio operation, providing a structured way to handle both success and failure scenarios.
/// </summary>
/// <remarks>
/// <para>
/// This class implements a result pattern that eliminates the need for exception handling in common scenarios
/// by explicitly representing both successful and failed operations. This approach leads to more predictable
/// and maintainable code when working with Minio storage operations.
/// </para>
/// <para>
/// Instead of throwing exceptions for expected error conditions (like missing buckets or invalid credentials),
/// operations return a <see cref="MinioResult"/> that can be pattern-matched or inspected for specific error types.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Checking success explicitly
/// var result = await minioClient.RemoveObjectAsync("bucket", "object");
/// if (result.IsSuccess)
/// {
///     Console.WriteLine("Operation succeeded");
/// }
/// else
/// {
///     Console.WriteLine($"Operation failed: {result.ErrorType} - {result.ErrorMessage}");
/// }
/// 
/// // Using pattern matching
/// result.Match(
///     onSuccess: () => Console.WriteLine("Success"),
///     onFailure: (errorType, message) => Console.WriteLine($"Failed: {errorType}")
/// );
/// </code>
/// </example>
public class MinioResult
{
    /// <summary>
    /// Gets a value indicating whether the operation completed successfully.
    /// </summary>
    /// <value>
    /// <c>true</c> if the operation succeeded; otherwise, <c>false</c>.
    /// </value>
    /// <remarks>
    /// This property is computed based on the <see cref="ErrorType"/> - it returns <c>true</c> when
    /// <see cref="ErrorType"/> is <see cref="MinioErrorType.None"/>.
    /// </remarks>
    public bool IsSuccess => ErrorType == MinioErrorType.None;
    
    /// <summary>
    /// Gets the type of error that occurred during the operation, if any.
    /// </summary>
    /// <value>
    /// A <see cref="MinioErrorType"/> value indicating the specific type of error that occurred,
    /// or <see cref="MinioErrorType.None"/> if the operation succeeded.
    /// </value>
    /// <remarks>
    /// This property allows callers to handle different error scenarios specifically without
    /// relying on exception types or string matching of error messages. It is init-only: a result
    /// cannot be flipped from success to failure after it has been created.
    /// </remarks>
    public MinioErrorType ErrorType { get; init; }

    /// <summary>
    /// Gets a descriptive error message providing details about the failure.
    /// </summary>
    /// <value>
    /// A string containing error details, or <c>null</c> if the operation succeeded.
    /// </value>
    /// <remarks>
    /// This message is typically the original exception message or a user-friendly description
    /// of what went wrong during the Minio operation.
    /// </remarks>
    public string? ErrorMessage { get; init; }
    
    /// <summary>
    /// Creates a new successful <see cref="MinioResult"/> instance.
    /// </summary>
    /// <returns>A <see cref="MinioResult"/> representing a successful operation.</returns>
    /// <remarks>
    /// Use this method to create result instances for successful operations that don't return any data.
    /// </remarks>
    public static MinioResult Success() => new() { ErrorType = MinioErrorType.None };
    
    /// <summary>
    /// Creates a new failed <see cref="MinioResult"/> instance with the specified error type and message.
    /// </summary>
    /// <param name="errorType">The type of error that occurred.</param>
    /// <param name="message">A descriptive error message.</param>
    /// <returns>A <see cref="MinioResult"/> representing a failed operation.</returns>
    /// <remarks>
    /// Use this method to create result instances for failed operations, providing specific error
    /// information that can be used for logging, user feedback, or error recovery.
    /// </remarks>
    public static MinioResult Failure(MinioErrorType errorType, string message) => new() { ErrorType = errorType, ErrorMessage = message };
}

/// <summary>
/// Represents the result of a Minio operation that returns a value, providing a structured way
/// to handle both success and failure scenarios.
/// </summary>
/// <typeparam name="T">The type of the value returned by a successful operation.</typeparam>
/// <remarks>
/// <para>
/// This generic version of <see cref="MinioResult"/> is used for operations that return data,
/// such as retrieving object metadata or downloading files. It combines the result pattern
/// with the convenience of strongly-typed return values.
/// </para>
/// <para>
/// The <see cref="Value"/> property is only meaningful when <see cref="MinioResult.IsSuccess"/> is <c>true</c>.
/// Accessing <see cref="Value"/> when <see cref="MinioResult.IsSuccess"/> is <c>false</c> may return <c>null</c>
/// or default values and should be avoided.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Working with generic results
/// var result = await minioClient.GetObjectAsync("bucket", "object");
/// if (result.IsSuccess)
/// {
///     var objectStat = result.Value;
///     Console.WriteLine($"Object size: {objectStat.Size}");
/// }
/// 
/// // Using pattern matching with values
/// var content = await minioClient.DownloadObjectAsync("bucket", "object")
///     .Match(
///         onSuccess: stream => ReadStreamContent(stream),
///         onFailure: (errorType, message) => $"Error: {message}"
///     );
/// </code>
/// </example>
public class MinioResult<T> : MinioResult
{
    /// <summary>
    /// Gets the value returned by a successful operation.
    /// </summary>
    /// <value>
    /// The operation result value of type <typeparamref name="T"/>, or <c>default(T)</c> if the operation failed.
    /// </value>
    /// <remarks>
    /// <para>
    /// This property should only be accessed when <see cref="MinioResult.IsSuccess"/> is <c>true</c>.
    /// When the operation fails, this property may be <c>null</c> or contain a default value.
    /// </para>
    /// <para>
    /// For safe access, use <see cref="TryGetValue"/>, pattern matching via the <c>Match</c> extension
    /// methods, or check <see cref="MinioResult.IsSuccess"/> before accessing this property.
    /// </para>
    /// </remarks>
    public T? Value { get; init; }

    /// <summary>
    /// Gets the value of a successful result without risking a null dereference on failure.
    /// </summary>
    /// <param name="value">
    /// When this method returns <see langword="true"/>, contains the value produced by the operation;
    /// otherwise <c>default(T)</c>.
    /// </param>
    /// <returns><see langword="true"/> if the operation succeeded; otherwise <see langword="false"/>.</returns>
    /// <example>
    /// <code>
    /// if ((await client.DownloadObjectAsync("bucket", "object")).TryGetValue(out var stream))
    /// {
    ///     using (stream) { /* ... */ }
    /// }
    /// </code>
    /// </example>
    public bool TryGetValue([NotNullWhen(true)] out T? value)
    {
        value = IsSuccess ? Value : default;
        return IsSuccess && value is not null;
    }


    /// <summary>
    /// Creates a new successful <see cref="MinioResult{T}"/> instance with the specified value.
    /// </summary>
    /// <param name="value">The value returned by the successful operation.</param>
    /// <returns>A <see cref="MinioResult{T}"/> representing a successful operation with a return value.</returns>
    /// <remarks>
    /// Use this method to create result instances for successful operations that return data.
    /// The provided value should be the actual result of the Minio operation.
    /// </remarks>
    public static MinioResult<T> Success(T value) => new() { Value = value, ErrorType = MinioErrorType.None };
    
    /// <summary>
    /// Creates a new failed <see cref="MinioResult{T}"/> instance with the specified error type and message.
    /// </summary>
    /// <param name="errorType">The type of error that occurred.</param>
    /// <param name="message">A descriptive error message.</param>
    /// <returns>A <see cref="MinioResult{T}"/> representing a failed operation.</returns>
    /// <remarks>
    /// Use this method to create result instances for failed operations that would normally return a value.
    /// The <see cref="Value"/> property will be set to <c>default(T)</c> for failed results.
    /// </remarks>
    public new static MinioResult<T> Failure(MinioErrorType errorType, string message) => new() { ErrorType = errorType, ErrorMessage = message };
}