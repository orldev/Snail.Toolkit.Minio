namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Stops a client from spending its retries on a server that is refusing everything.
/// </summary>
/// <remarks>
/// <para>
/// Retries help when a failure is one server's bad second. They stop helping the moment the failure is the
/// server itself, because then every caller is retrying, and the retries are a share of the load keeping it
/// down. After enough consecutive transient failures this answers immediately, waits, and lets exactly one
/// call through to find out whether anything changed.
/// </para>
/// <para>
/// Only failures worth retrying count. A missing object is not the server's health.
/// </para>
/// </remarks>
internal sealed class CircuitBreaker
{
    private readonly object _gate = new();

    private int _failures;
    private DateTimeOffset _openedAt;
    private bool _probing;

    /// <summary>
    /// Asks whether a call may go out.
    /// </summary>
    /// <param name="settings">The thresholds to judge by.</param>
    /// <param name="wait">How long is left before the next call is let through.</param>
    /// <returns><see langword="true"/> when the call may be made.</returns>
    internal bool TryEnter(MinioOptions settings, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;

        if (settings.CircuitBreakFailures <= 0)
            return true;

        lock (_gate)
        {
            if (_failures < settings.CircuitBreakFailures)
                return true;

            var elapsed = DateTimeOffset.UtcNow - _openedAt;

            if (elapsed >= settings.CircuitBreakDuration && !_probing)
            {
                _probing = true;

                return true;
            }

            var remaining = settings.CircuitBreakDuration - elapsed;
            wait = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;

            return false;
        }
    }

    /// <summary>Reports that the server answered.</summary>
    internal void Succeeded()
    {
        lock (_gate)
        {
            _failures = 0;
            _probing = false;
        }
    }

    /// <summary>
    /// Reports a failure worth counting.
    /// </summary>
    /// <param name="settings">The thresholds to judge by.</param>
    internal void Failed(MinioOptions settings)
    {
        if (settings.CircuitBreakFailures <= 0)
            return;

        lock (_gate)
        {
            _probing = false;

            if (_failures < settings.CircuitBreakFailures)
                _failures++;

            if (_failures >= settings.CircuitBreakFailures)
                _openedAt = DateTimeOffset.UtcNow;
        }
    }
}
