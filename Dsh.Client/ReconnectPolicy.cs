namespace Dsh.Client;

/// <summary>
/// Backoff policy for the two downstream WebSocket streams, mirroring
/// <c>packages/client/connection</c> (see <c>os/07</c> §4.3). Pure and unit-testable: it
/// only decides the delay before the next reconnect attempt — it owns no sockets and no
/// UI state, so the reconnect <em>loop</em> (advance generation, tear down stale state,
/// reopen both streams) lives in the caller.
/// </summary>
public sealed class ReconnectPolicy
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _backoffMultiplier;
    private int _attempt;

    /// <param name="initialDelay">Delay after the first drop (default 1s).</param>
    /// <param name="maxDelay">Never wait longer than this (default 15s).</param>
    /// <param name="backoffMultiplier">Per-attempt growth factor (default 2).</param>
    public ReconnectPolicy(
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        double backoffMultiplier = 2.0)
    {
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(15);
        _backoffMultiplier = backoffMultiplier;
        if (_backoffMultiplier <= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(backoffMultiplier), "must be > 1");
        }
    }

    /// <summary>Number of reconnect attempts made so far.</summary>
    public int Attempts => _attempt;

    /// <summary>
    /// The delay to wait before the next reconnect, then records the attempt. A successful
    /// (re)connect should call <see cref="Reset"/> so a later drop starts back at the initial delay.
    /// </summary>
    public TimeSpan NextDelay()
    {
        double ms = _initialDelay.TotalMilliseconds;
        for (int i = 0; i < _attempt; i++)
        {
            ms *= _backoffMultiplier;
            if (ms >= _maxDelay.TotalMilliseconds)
            {
                ms = _maxDelay.TotalMilliseconds;
                break;
            }
        }
        _attempt++;
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>Clear the attempt counter after a successful reconnect.</summary>
    public void Reset() => _attempt = 0;
}
