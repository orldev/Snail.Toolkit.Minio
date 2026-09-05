namespace Snail.Toolkit.Minio.Domain;

/// <summary>
/// Reads a <see cref="StorageResult"/> without unwrapping it by hand.
/// </summary>
/// <remarks>
/// Every overload exists in a pending form as well, so a call reads as one sentence:
/// <c>await storage.StatAsync(...).MatchAsync(...)</c>.
/// </remarks>
public static class StorageResultExtensions
{
    /// <summary>Runs one of two actions, depending on the outcome.</summary>
    /// <param name="result">The result to read.</param>
    /// <param name="onSuccess">Runs when the operation succeeded.</param>
    /// <param name="onFailure">Runs when the operation failed.</param>
    public static void Match(this StorageResult result, Action onSuccess, Action<StorageError> onFailure)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
            onSuccess();
        else
            onFailure(result.Error);
    }

    /// <summary>Turns either outcome into a value.</summary>
    /// <typeparam name="TOutput">The type to produce.</typeparam>
    /// <param name="result">The result to read.</param>
    /// <param name="onSuccess">Produces the value when the operation succeeded.</param>
    /// <param name="onFailure">Produces the value when the operation failed.</param>
    /// <returns>Whatever the matching function returned.</returns>
    public static TOutput Match<TOutput>(
        this StorageResult result,
        Func<TOutput> onSuccess,
        Func<StorageError, TOutput> onFailure)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess ? onSuccess() : onFailure(result.Error);
    }

    /// <summary>Runs one of two actions, depending on the outcome.</summary>
    /// <typeparam name="T">The type carried by a successful result.</typeparam>
    /// <param name="result">The result to read.</param>
    /// <param name="onSuccess">Runs with the value when the operation succeeded.</param>
    /// <param name="onFailure">Runs when the operation failed.</param>
    public static void Match<T>(
        this StorageResult<T> result,
        Action<T> onSuccess,
        Action<StorageError> onFailure)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.TryGetValue(out var value))
            onSuccess(value);
        else
            onFailure(result.Error!);
    }

    /// <summary>Turns either outcome into a value.</summary>
    /// <typeparam name="T">The type carried by a successful result.</typeparam>
    /// <typeparam name="TOutput">The type to produce.</typeparam>
    /// <param name="result">The result to read.</param>
    /// <param name="onSuccess">Produces the value when the operation succeeded.</param>
    /// <param name="onFailure">Produces the value when the operation failed.</param>
    /// <returns>Whatever the matching function returned.</returns>
    public static TOutput Match<T, TOutput>(
        this StorageResult<T> result,
        Func<T, TOutput> onSuccess,
        Func<StorageError, TOutput> onFailure)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.TryGetValue(out var value) ? onSuccess(value) : onFailure(result.Error!);
    }

    /// <summary>Reshapes the value of a successful result, passing a failure through untouched.</summary>
    /// <typeparam name="T">The type carried by the original result.</typeparam>
    /// <typeparam name="TOutput">The type to project onto.</typeparam>
    /// <param name="result">The result to project.</param>
    /// <param name="map">The projection.</param>
    /// <returns>The projected result, or the original failure.</returns>
    public static StorageResult<TOutput> Map<T, TOutput>(
        this StorageResult<T> result,
        Func<T, TOutput> map)
        where T : notnull
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.TryGetValue(out var value)
            ? StorageResult<TOutput>.Success(map(value))
            : StorageResult<TOutput>.Failure(result.Error!);
    }

    /// <summary>Runs an action when the operation failed, and returns the result unchanged.</summary>
    /// <typeparam name="T">The type carried by a successful result.</typeparam>
    /// <param name="result">The result to inspect.</param>
    /// <param name="onFailure">Runs with the failure.</param>
    /// <returns>The same result, so calls can be chained.</returns>
    public static StorageResult<T> OnFailure<T>(this StorageResult<T> result, Action<StorageError> onFailure)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.IsSuccess)
            onFailure(result.Error);

        return result;
    }

    /// <summary>Awaits the operation and runs one of two actions.</summary>
    /// <param name="result">The pending operation.</param>
    /// <param name="onSuccess">Runs when the operation succeeded.</param>
    /// <param name="onFailure">Runs when the operation failed.</param>
    /// <returns>A task that completes once the matching action has run.</returns>
    public static async Task MatchAsync(
        this Task<StorageResult> result,
        Action onSuccess,
        Action<StorageError> onFailure)
        => (await result.ConfigureAwait(false)).Match(onSuccess, onFailure);

    /// <summary>Awaits the operation and turns either outcome into a value.</summary>
    /// <typeparam name="T">The type carried by a successful result.</typeparam>
    /// <typeparam name="TOutput">The type to produce.</typeparam>
    /// <param name="result">The pending operation.</param>
    /// <param name="onSuccess">Produces the value when the operation succeeded.</param>
    /// <param name="onFailure">Produces the value when the operation failed.</param>
    /// <returns>Whatever the matching function returned.</returns>
    public static async Task<TOutput> MatchAsync<T, TOutput>(
        this Task<StorageResult<T>> result,
        Func<T, TOutput> onSuccess,
        Func<StorageError, TOutput> onFailure)
        where T : notnull
        => (await result.ConfigureAwait(false)).Match(onSuccess, onFailure);

    /// <summary>Awaits the operation and reshapes the value of a successful result.</summary>
    /// <typeparam name="T">The type carried by the original result.</typeparam>
    /// <typeparam name="TOutput">The type to project onto.</typeparam>
    /// <param name="result">The pending operation.</param>
    /// <param name="map">The projection.</param>
    /// <returns>The projected result, or the original failure.</returns>
    public static async Task<StorageResult<TOutput>> MapAsync<T, TOutput>(
        this Task<StorageResult<T>> result,
        Func<T, TOutput> map)
        where T : notnull
        where TOutput : notnull
        => (await result.ConfigureAwait(false)).Map(map);
}
