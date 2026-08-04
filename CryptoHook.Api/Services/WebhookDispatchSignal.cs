namespace CryptoHook.Api.Services;

/// <summary>
/// Lets the enqueueing side nudge the dispatcher awake so a fresh notification goes out
/// immediately instead of waiting for the next poll.
/// </summary>
public sealed class WebhookDispatchSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        // A single pending wake-up is enough: the dispatcher drains everything that is due.
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Raced with another signal; a wake-up is already pending, which is all we need.
            }
        }
    }

    /// <summary>
    /// Waits for a signal, giving up after <paramref name="timeout"/> so the dispatcher still
    /// polls for retries that came due without anything new being enqueued.
    /// </summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _signal.Dispose();
}
