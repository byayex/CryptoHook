using System.Diagnostics;

namespace CryptoHook.Api.Services.CryptoServices.DataProvider;

/// <remarks>
/// Spaces out requests to a block explorer so that the worker cannot exceed the explorer's
/// published rate limit when it checks many payments at once.
///
/// Being throttled is not a harmless slowdown here: Etherscan answers a rate-limited query with
/// HTTP 200 and an error body, and a provider that treats that as "no transactions" would report
/// a paid request as unpaid. Pacing the calls is what keeps that from happening under load.
/// </remarks>
public sealed class ExplorerRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _minimumIntervalTicks;
    private long _nextAllowedTimestamp;

    public ExplorerRateLimiter(double requestsPerSecond)
    {
        if (requestsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestsPerSecond), requestsPerSecond,
                "The explorer request rate must be greater than zero.");
        }

        _minimumIntervalTicks = (long)(Stopwatch.Frequency / requestsPerSecond);
        _nextAllowedTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Waits for the caller's turn, then reserves the slot after it.</summary>
    public async Task WaitForTurnAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        TimeSpan delay;
        try
        {
            var now = Stopwatch.GetTimestamp();

            if (_nextAllowedTimestamp <= now)
            {
                _nextAllowedTimestamp = now + _minimumIntervalTicks;
                return;
            }

            delay = Stopwatch.GetElapsedTime(now, _nextAllowedTimestamp);
            _nextAllowedTimestamp += _minimumIntervalTicks;
        }
        finally
        {
            // Released before the wait so that queued callers can reserve their own slots
            // immediately instead of waiting one at a time for the whole queue ahead of them.
            _gate.Release();
        }

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Hand the reservation back. A caller that gives up while queued never sends its
            // request, and keeping its slot reserved pushes everyone behind it further out: under a
            // per-check deadline the same tail of the queue then times out on every pass and never
            // makes progress.
            ReturnReservation();
            throw;
        }
    }

    private void ReturnReservation()
    {
        _gate.Wait();

        try
        {
            // Only if the slot is still in the future. Once it has passed, the reservation has
            // already been consumed by the pacing of whoever queued behind it.
            if (_nextAllowedTimestamp > Stopwatch.GetTimestamp())
            {
                _nextAllowedTimestamp -= _minimumIntervalTicks;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
