using Toolkit.Minio.Entities;

namespace Toolkit.Minio.Extensions;

/// <summary>
/// Provides extension methods for <see cref="MinioResult"/> and <see cref="MinioResult{T}"/> to enable
/// functional-style pattern matching for handling success and failure scenarios.
/// </summary>
/// <remarks>
/// <para>
/// These extension methods allow for more expressive and functional handling of Minio operation results
/// by providing a way to execute different code paths based on whether the operation succeeded or failed.
/// </para>
/// <para>
/// Pattern matching with these methods can make code more readable and reduce the need for explicit if-else statements
/// when working with <see cref="MinioResult"/> types.
/// </para>
/// </remarks>
/// <example>
/// The following example shows how to use the Match method:
/// <code>
/// var result = await minioClient.RemoveObjectAsync("bucket", "object");
/// 
/// result.Match(
///     onSuccess: () => Console.WriteLine("Object removed successfully"),
///     onFailure: (errorType, message) => Console.WriteLine($"Failed: {errorType} - {message}")
/// );
/// </code>
/// </example>
public static class MinioResultExtensions
{
    /// <summary>
    /// Executes the appropriate action based on the result state (success or failure).
    /// </summary>
    /// <param name="result">The MinioResult to match against.</param>
    /// <param name="onSuccess">The action to execute if the operation was successful.</param>
    /// <param name="onFailure">The action to execute if the operation failed, receiving the error type and message.</param>
    /// <remarks>
    /// <para>
    /// This method provides a way to handle both success and failure scenarios in a single, fluent call.
    /// It's particularly useful for side-effecting operations like logging, notifications, or UI updates.
    /// </para>
    /// <para>
    /// The <paramref name="onFailure"/> action receives the <see cref="MinioErrorType"/> and error message,
    /// allowing for detailed error handling based on the specific type of failure.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// await minioClient.PutObjectAsync("bucket", "object", args)
    ///     .Match(
    ///         onSuccess: () => _logger.LogInformation("Upload successful"),
    ///         onFailure: (errorType, message) => _logger.LogError("Upload failed: {ErrorType} - {Message}", errorType, message)
    ///     );
    /// </code>
    /// </example>
    public static void Match(
        this MinioResult result,
        Action onSuccess,
        Action<MinioErrorType, string> onFailure)
    {
        if (result.IsSuccess)
            onSuccess();
        else
            onFailure(result.ErrorType, result.ErrorMessage ?? "Unknown error");
    }
    
    /// <summary>
    /// Transforms the result by executing the appropriate function based on the result state (success or failure)
    /// and returns the transformed value.
    /// </summary>
    /// <typeparam name="T">The type of the value to return.</typeparam>
    /// <param name="result">The MinioResult to match against.</param>
    /// <param name="onSuccess">The function to execute if the operation was successful, returning a value of type T.</param>
    /// <param name="onFailure">The function to execute if the operation failed, receiving the error type and message and returning a value of type T.</param>
    /// <returns>The value returned by either the success or failure function.</returns>
    /// <remarks>
    /// <para>
    /// This method is useful when you need to transform a <see cref="MinioResult"/> into another value
    /// based on whether the operation succeeded or failed. It's commonly used for creating HTTP responses,
    /// DTOs, or any scenario where you need to project the result into a different form.
    /// </para>
    /// <para>
    /// Unlike the void <see cref="Match(MinioResult, Action, Action{MinioErrorType, string})"/> method,
    /// this version returns a value, making it suitable for functional composition.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var response = await minioClient.StatObjectAsync("bucket", "object")
    ///     .Match(
    ///         onSuccess: stat => new ObjectResponse { 
    ///             Exists = true, 
    ///             Size = stat.Size,
    ///             ContentType = stat.ContentType 
    ///         },
    ///         onFailure: (errorType, message) => new ObjectResponse { 
    ///             Exists = false,
    ///             Error = message,
    ///             ErrorType = errorType.ToString()
    ///         }
    ///     );
    /// </code>
    /// </example>
    public static T Match<T>(
        this MinioResult result,
        Func<T> onSuccess,
        Func<MinioErrorType, string, T> onFailure)
    {
        return result.IsSuccess 
            ? onSuccess() 
            : onFailure(result.ErrorType, result.ErrorMessage ?? "Unknown error");
    }
    
    /// <summary>
    /// Executes the appropriate action based on the result state (success or failure) for a generic MinioResult.
    /// </summary>
    /// <typeparam name="T">The type of the value contained in a successful result.</typeparam>
    /// <param name="result">The MinioResult&lt;T&gt; to match against.</param>
    /// <param name="onSuccess">The action to execute if the operation was successful, receiving the result value.</param>
    /// <param name="onFailure">The action to execute if the operation failed, receiving the error type and message.</param>
    /// <remarks>
    /// <para>
    /// This method extends the pattern matching capability to generic <see cref="MinioResult{T}"/> instances,
    /// providing access to the successful operation's result value in the success case.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// await minioClient.DownloadObjectAsync("bucket", "object")
    ///     .Match(
    ///         onSuccess: stream => {
    ///             using (stream)
    ///             {
    ///                 // Process the downloaded stream
    ///                 ProcessStream(stream);
    ///             }
    ///         },
    ///         onFailure: (errorType, message) => Console.WriteLine($"Download failed: {message}")
    ///     );
    /// </code>
    /// </example>
    public static void Match<T>(
        this MinioResult<T> result,
        Action<T> onSuccess,
        Action<MinioErrorType, string> onFailure)
    {
        if (result.IsSuccess)
            onSuccess(result.Value!);
        else
            onFailure(result.ErrorType, result.ErrorMessage ?? "Unknown error");
    }

    /// <summary>
    /// Transforms the result by executing the appropriate function based on the result state (success or failure)
    /// and returns the transformed value for a generic MinioResult.
    /// </summary>
    /// <typeparam name="TInput">The type of the value contained in the original result.</typeparam>
    /// <typeparam name="TOutput">The type of the value to return.</typeparam>
    /// <param name="result">The MinioResult&lt;TInput&gt; to match against.</param>
    /// <param name="onSuccess">The function to execute if the operation was successful, receiving the result value and returning a value of type TOutput.</param>
    /// <param name="onFailure">The function to execute if the operation failed, receiving the error type and message and returning a value of type TOutput.</param>
    /// <returns>The value returned by either the success or failure function.</returns>
    /// <remarks>
    /// <para>
    /// This method enables transforming a <see cref="MinioResult{T}"/> into a different type while preserving
    /// the success/failure semantics. It's useful for mapping Minio operation results to API responses,
    /// view models, or any other domain-specific types.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var apiResponse = await minioClient.GetObjectAsync("bucket", "object")
    ///     .Match(
    ///         onSuccess: objectStat => new ApiResponse&lt;ObjectStat&gt; { 
    ///             Data = objectStat,
    ///             Success = true 
    ///         },
    ///         onFailure: (errorType, message) => new ApiResponse&lt;ObjectStat&gt; { 
    ///             Success = false,
    ///             Error = message,
    ///             ErrorCode = errorType.ToString()
    ///         }
    ///     );
    /// </code>
    /// </example>
    public static TOutput Match<TInput, TOutput>(
        this MinioResult<TInput> result,
        Func<TInput, TOutput> onSuccess,
        Func<MinioErrorType, string, TOutput> onFailure)
    {
        return result.IsSuccess
            ? onSuccess(result.Value!)
            : onFailure(result.ErrorType, result.ErrorMessage ?? "Unknown error");
    }

    /// <summary>
    /// Awaits the operation and executes the appropriate action based on the result state.
    /// </summary>
    /// <param name="result">The pending Minio operation.</param>
    /// <param name="onSuccess">The action to execute if the operation was successful.</param>
    /// <param name="onFailure">The action to execute if the operation failed, receiving the error type and message.</param>
    /// <returns>A task that completes once the matched action has run.</returns>
    /// <remarks>
    /// This overload lets a Minio call be matched directly without an intermediate variable, as in
    /// <c>await client.RemoveObjectAsync(...).Match(...)</c>.
    /// </remarks>
    public static async Task Match(
        this Task<MinioResult> result,
        Action onSuccess,
        Action<MinioErrorType, string> onFailure)
    {
        (await result.ConfigureAwait(false)).Match(onSuccess, onFailure);
    }

    /// <summary>
    /// Awaits the operation and transforms it into a value based on the result state.
    /// </summary>
    /// <typeparam name="T">The type of the value to return.</typeparam>
    /// <param name="result">The pending Minio operation.</param>
    /// <param name="onSuccess">The function to execute if the operation was successful.</param>
    /// <param name="onFailure">The function to execute if the operation failed, receiving the error type and message.</param>
    /// <returns>The value returned by either the success or failure function.</returns>
    public static async Task<T> Match<T>(
        this Task<MinioResult> result,
        Func<T> onSuccess,
        Func<MinioErrorType, string, T> onFailure)
    {
        return (await result.ConfigureAwait(false)).Match(onSuccess, onFailure);
    }

    /// <summary>
    /// Awaits the operation and executes the appropriate action based on the result state.
    /// </summary>
    /// <typeparam name="T">The type of the value contained in a successful result.</typeparam>
    /// <param name="result">The pending Minio operation.</param>
    /// <param name="onSuccess">The action to execute if the operation was successful, receiving the result value.</param>
    /// <param name="onFailure">The action to execute if the operation failed, receiving the error type and message.</param>
    /// <returns>A task that completes once the matched action has run.</returns>
    public static async Task Match<T>(
        this Task<MinioResult<T>> result,
        Action<T> onSuccess,
        Action<MinioErrorType, string> onFailure)
    {
        (await result.ConfigureAwait(false)).Match(onSuccess, onFailure);
    }

    /// <summary>
    /// Awaits the operation and transforms it into a value based on the result state.
    /// </summary>
    /// <typeparam name="TInput">The type of the value contained in the original result.</typeparam>
    /// <typeparam name="TOutput">The type of the value to return.</typeparam>
    /// <param name="result">The pending Minio operation.</param>
    /// <param name="onSuccess">The function to execute if the operation was successful, receiving the result value.</param>
    /// <param name="onFailure">The function to execute if the operation failed, receiving the error type and message.</param>
    /// <returns>The value returned by either the success or failure function.</returns>
    /// <example>
    /// <code>
    /// var summary = await minioClient.StatObjectAsync("bucket", "object")
    ///     .Match(
    ///         onSuccess: stat => $"{stat.Size} bytes",
    ///         onFailure: (errorType, _) => $"unavailable: {errorType}"
    ///     );
    /// </code>
    /// </example>
    public static async Task<TOutput> Match<TInput, TOutput>(
        this Task<MinioResult<TInput>> result,
        Func<TInput, TOutput> onSuccess,
        Func<MinioErrorType, string, TOutput> onFailure)
    {
        return (await result.ConfigureAwait(false)).Match(onSuccess, onFailure);
    }

    /// <summary>
    /// Projects the value of a successful result into a new form, propagating failures unchanged.
    /// </summary>
    /// <typeparam name="TInput">The type of the value contained in the original result.</typeparam>
    /// <typeparam name="TOutput">The type of the projected value.</typeparam>
    /// <param name="result">The result to project.</param>
    /// <param name="map">The projection applied to the value of a successful result.</param>
    /// <returns>
    /// A successful <see cref="MinioResult{T}"/> holding the projected value, or a failed result carrying the
    /// original <see cref="MinioErrorType"/> and message.
    /// </returns>
    /// <remarks>
    /// Use this to reshape a result without unwrapping it, keeping the error information intact for a later
    /// <c>Match</c>.
    /// </remarks>
    public static MinioResult<TOutput> Map<TInput, TOutput>(
        this MinioResult<TInput> result,
        Func<TInput, TOutput> map)
    {
        return result.IsSuccess
            ? MinioResult<TOutput>.Success(map(result.Value!))
            : MinioResult<TOutput>.Failure(result.ErrorType, result.ErrorMessage ?? "Unknown error");
    }

    /// <summary>
    /// Awaits the operation and projects the value of a successful result into a new form.
    /// </summary>
    /// <typeparam name="TInput">The type of the value contained in the original result.</typeparam>
    /// <typeparam name="TOutput">The type of the projected value.</typeparam>
    /// <param name="result">The pending Minio operation.</param>
    /// <param name="map">The projection applied to the value of a successful result.</param>
    /// <returns>A projected successful result, or the original failure.</returns>
    public static async Task<MinioResult<TOutput>> Map<TInput, TOutput>(
        this Task<MinioResult<TInput>> result,
        Func<TInput, TOutput> map)
    {
        return (await result.ConfigureAwait(false)).Map(map);
    }

    /// <summary>
    /// Executes an action when the result represents a failure and returns the result unchanged.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="onFailure">The action to execute if the operation failed, receiving the error type and message.</param>
    /// <returns>The same <paramref name="result"/>, so calls can be chained.</returns>
    /// <remarks>
    /// Useful for side effects such as logging without collapsing the result into another type.
    /// </remarks>
    public static TResult OnFailure<TResult>(
        this TResult result,
        Action<MinioErrorType, string> onFailure)
        where TResult : MinioResult
    {
        if (!result.IsSuccess)
            onFailure(result.ErrorType, result.ErrorMessage ?? "Unknown error");

        return result;
    }
}