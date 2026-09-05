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
    extension(StorageResult result)
    {
        /// <summary>Runs one of two actions, depending on the outcome.</summary>
        /// <param name="onSuccess">Runs when the operation succeeded.</param>
        /// <param name="onFailure">Runs when the operation failed.</param>
        public void Match(Action onSuccess, Action<StorageError> onFailure)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (result.IsSuccess)
                onSuccess();
            else
                onFailure(result.Error);
        }

        /// <summary>Turns either outcome into a value.</summary>
        /// <typeparam name="TOutput">The type to produce.</typeparam>
        /// <param name="onSuccess">Produces the value when the operation succeeded.</param>
        /// <param name="onFailure">Produces the value when the operation failed.</param>
        /// <returns>Whatever the matching function returned.</returns>
        public TOutput Match<TOutput>(Func<TOutput> onSuccess, Func<StorageError, TOutput> onFailure)
        {
            ArgumentNullException.ThrowIfNull(result);

            return result.IsSuccess ? onSuccess() : onFailure(result.Error);
        }
    }

    extension<T>(StorageResult<T> result) where T : notnull
    {
        /// <summary>Runs one of two actions, depending on the outcome.</summary>
        /// <param name="onSuccess">Runs with the value when the operation succeeded.</param>
        /// <param name="onFailure">Runs when the operation failed.</param>
        public void Match(Action<T> onSuccess, Action<StorageError> onFailure)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (result.TryGetValue(out var value))
                onSuccess(value);
            else
                onFailure(result.Error);
        }

        /// <summary>Turns either outcome into a value.</summary>
        /// <typeparam name="TOutput">The type to produce.</typeparam>
        /// <param name="onSuccess">Produces the value when the operation succeeded.</param>
        /// <param name="onFailure">Produces the value when the operation failed.</param>
        /// <returns>Whatever the matching function returned.</returns>
        public TOutput Match<TOutput>(Func<T, TOutput> onSuccess, Func<StorageError, TOutput> onFailure)
        {
            ArgumentNullException.ThrowIfNull(result);

            return result.TryGetValue(out var value) ? onSuccess(value) : onFailure(result.Error);
        }

        /// <summary>Reshapes the value of a successful result, passing a failure through untouched.</summary>
        /// <typeparam name="TOutput">The type to project onto.</typeparam>
        /// <param name="map">The projection.</param>
        /// <returns>The projected result, or the original failure.</returns>
        public StorageResult<TOutput> Map<TOutput>(Func<T, TOutput> map)
            where TOutput : notnull
        {
            ArgumentNullException.ThrowIfNull(result);

            return result.TryGetValue(out var value)
                ? StorageResult<TOutput>.Success(map(value))
                : StorageResult<TOutput>.Failure(result.Error);
        }

        /// <summary>Runs an action when the operation failed, and returns the result unchanged.</summary>
        /// <param name="onFailure">Runs with the failure.</param>
        /// <returns>The same result, so calls can be chained.</returns>
        public StorageResult<T> OnFailure(Action<StorageError> onFailure)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (!result.IsSuccess)
                onFailure(result.Error);

            return result;
        }
    }

    extension(Task<StorageResult> result)
    {
        /// <summary>Awaits the operation and runs one of two actions.</summary>
        /// <param name="onSuccess">Runs when the operation succeeded.</param>
        /// <param name="onFailure">Runs when the operation failed.</param>
        /// <returns>A task that completes once the matching action has run.</returns>
        public async Task MatchAsync(Action onSuccess, Action<StorageError> onFailure)
            => (await result.ConfigureAwait(false)).Match(onSuccess, onFailure);
    }

    extension<T>(Task<StorageResult<T>> result) where T : notnull
    {
        /// <summary>Awaits the operation and turns either outcome into a value.</summary>
        /// <typeparam name="TOutput">The type to produce.</typeparam>
        /// <param name="onSuccess">Produces the value when the operation succeeded.</param>
        /// <param name="onFailure">Produces the value when the operation failed.</param>
        /// <returns>Whatever the matching function returned.</returns>
        public async Task<TOutput> MatchAsync<TOutput>(
            Func<T, TOutput> onSuccess,
            Func<StorageError, TOutput> onFailure)
            => (await result.ConfigureAwait(false)).Match(onSuccess, onFailure);

        /// <summary>Awaits the operation and reshapes the value of a successful result.</summary>
        /// <typeparam name="TOutput">The type to project onto.</typeparam>
        /// <param name="map">The projection.</param>
        /// <returns>The projected result, or the original failure.</returns>
        public async Task<StorageResult<TOutput>> MapAsync<TOutput>(Func<T, TOutput> map)
            where TOutput : notnull
            => (await result.ConfigureAwait(false)).Map(map);
    }
}
