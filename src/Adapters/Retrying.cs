using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Repeats the calls of one named configuration while they fail for a reason that may not repeat, behind one
/// circuit breaker.
/// </summary>
/// <param name="name">The configuration whose settings decide the attempts and the breaker.</param>
/// <param name="options">The named settings, re-read on every operation.</param>
/// <param name="logger">Where retries are reported.</param>
/// <remarks>
/// Every call the adapter makes goes through here, including the ones that are never retried, because this is
/// also where the breaker decides whether the server is worth asking at all. Kept apart from the adapter so the
/// policy can be read, tested and one day replaced on its own.
/// </remarks>
internal sealed class Retrying(string name, IOptionsMonitor<MinioOptions> options, ILogger logger)
{
    private readonly CircuitBreaker _breaker = new();

    /// <summary>The longest this adapter waits between attempts, however the settings are written.</summary>
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Repeats an operation while it fails for a reason that may not repeat.
    /// </summary>
    /// <typeparam name="T">What the operation produces.</typeparam>
    /// <param name="operation">The name to report under.</param>
    /// <param name="attempt">The work to repeat.</param>
    /// <param name="prepare">Puts the world back where the next attempt can start; false stops retrying.</param>
    /// <param name="cancellationToken">Cancels the wait between attempts.</param>
    /// <returns>The first successful outcome, or the last failure.</returns>
    /// <remarks>
    /// Every call the adapter makes goes through here, including the ones that are never retried, because
    /// this is also where the circuit breaker decides whether the server is worth asking at all. Every attempt
    /// tells the breaker how it ended — answered, failed, or abandoned by an exception — because a probe it
    /// never heard back from kept the gate shut for good.
    /// </remarks>
    internal Task<StorageResult<T>> RetryAsync<T>(
        string operation,
        Func<Task<StorageResult<T>>> attempt,
        Func<bool> prepare,
        CancellationToken cancellationToken)
        where T : notnull
        => RetryAsync(
            operation,
            (Attempt: attempt, Prepare: prepare),
            static retry => retry.Attempt(),
            static retry => retry.Prepare(),
            cancellationToken);

    /// <summary>
    /// Repeats an operation on what it was handed while it fails for a reason that may not repeat.
    /// </summary>
    /// <typeparam name="TState">What every attempt is made with.</typeparam>
    /// <typeparam name="T">What the operation produces.</typeparam>
    /// <param name="operation">The name to report under.</param>
    /// <param name="state">What every attempt is made with.</param>
    /// <param name="attempt">The work to repeat, capturing nothing.</param>
    /// <param name="prepare">Puts the world back where the next attempt can start; false stops retrying.</param>
    /// <param name="cancellationToken">Cancels the wait between attempts.</param>
    /// <returns>The first successful outcome, or the last failure.</returns>
    /// <remarks>
    /// The attempt is handed its state rather than capturing it, so the reads, the stat and the upload cost no
    /// closure and no delegate per call.
    /// </remarks>
    internal async Task<StorageResult<T>> RetryAsync<TState, T>(
        string operation,
        TState state,
        Func<TState, Task<StorageResult<T>>> attempt,
        Func<TState, bool> prepare,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var settings = options.Get(name);

        if (!_breaker.TryEnter(settings, out var closedFor))
            return StorageResult<T>.Failure(new StorageError(
                StorageErrorKind.Upstream,
                $"Not attempted: {settings.CircuitBreakFailures} calls in a row failed, "
                + $"so this configuration is not being used for another {closedFor}.")
            {
                RetryAfter = closedFor
            });

        for (var attempted = 0; ; attempted++)
        {
            StorageResult<T> result;

            try
            {
                result = await attempt(state).ConfigureAwait(false);
            }
            catch
            {
                _breaker.Abandoned();

                throw;
            }

            if (result.IsSuccess)
            {
                _breaker.Succeeded();

                return result;
            }

            var worthRetrying = IsWorthRetrying(result.Error);

            if (worthRetrying)
                _breaker.Failed(settings);
            else
                _breaker.Answered();

            if (attempted >= settings.RetryAttempts || !worthRetrying || !prepare(state))
                return result;

            if (!TryWait(settings, attempted, result.Error, out var delay))
                return result;

            Log.Retrying(logger, operation, result.Error.Kind, delay, result.Error.Message);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Works out whether to wait, and for how long.
    /// </summary>
    /// <param name="settings">The settings the wait grows from.</param>
    /// <param name="attempted">How many attempts have already failed.</param>
    /// <param name="error">The failure that would be retried.</param>
    /// <param name="delay">How long to wait before the next attempt.</param>
    /// <returns><see langword="false"/> when the server asked for longer than is worth waiting.</returns>
    /// <remarks>
    /// A server that answers <c>Retry-After: 300</c> is saying it will not be ready inside any request's
    /// lifetime. Waiting that long inside a call nobody can see is worse than answering now and letting the
    /// caller decide.
    /// </remarks>
    private static bool TryWait(MinioOptions settings, int attempted, StorageError error, out TimeSpan delay)
    {
        delay = Backoff(settings, attempted);

        if (error.RetryAfter is not { } asked)
            return true;

        if (asked > MaximumBackoff)
            return false;

        if (asked > delay)
            delay = asked;

        return true;
    }

    /// <summary>
    /// Decides whether a failure is worth another attempt.
    /// </summary>
    /// <param name="error">The failure to judge.</param>
    /// <returns><see langword="true"/> when the same call may yet succeed.</returns>
    /// <remarks>
    /// A lost connection and a timeout, plus the statuses a server sends while it is briefly unable to
    /// answer. A rolling deployment answers 502 and 503 for a few seconds, and a client that gives up on
    /// those turns a deployment into an outage. The S3 codes are there because the SDK reports a 503 by
    /// code and without a status, and the same failure must not depend on which path it arrived by.
    /// </remarks>
    private static bool IsWorthRetrying(StorageError error)
        => error.Kind is StorageErrorKind.Connection or StorageErrorKind.Timeout
            || error.StatusCode is 408 or 429 or 500 or 502 or 503 or 504
            || error.Code is "SlowDown" or "InternalError" or "ServiceUnavailable" or "RequestTimeout" or "Busy";

    /// <summary>
    /// Works out how long to wait before the next attempt.
    /// </summary>
    /// <param name="settings">The settings the delay grows from.</param>
    /// <param name="attempted">How many attempts have already failed.</param>
    /// <returns>The wait, doubling per attempt and capped.</returns>
    /// <remarks>
    /// Capped because the doubling is otherwise unbounded: a generous delay and a generous attempt count
    /// multiply into a wait longer than the timer accepts, which raises out of a call that promised to
    /// answer.
    /// </remarks>
    private static TimeSpan Backoff(MinioOptions settings, int attempted)
    {
        var doublings = Math.Min(attempted, 16);
        var delay = settings.RetryDelay * Math.Pow(2, doublings);

        return delay < MaximumBackoff ? delay : MaximumBackoff;
    }

    /// <summary>
    /// Tells the breaker about a failure met outside the funnel.
    /// </summary>
    /// <param name="failure">The failure met.</param>
    internal void Noticed(StorageError failure)
    {
        if (IsWorthRetrying(failure))
            _breaker.Failed(options.Get(name));
        else
            _breaker.Answered();
    }
}
