using System.Diagnostics;
using CryptoHook.Api.Services.CryptoServices.DataProvider;

namespace CryptoHook.Api.UnitTests.Services.CryptoServices.DataProvider;

public class ExplorerRateLimiterTests
{
    [Fact]
    public void Constructor_RejectsANonPositiveRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExplorerRateLimiter(0).Dispose());
    }

    [Fact]
    public async Task WaitForTurnAsync_FirstCaller_IsNotDelayed()
    {
        using var limiter = new ExplorerRateLimiter(requestsPerSecond: 2);

        var elapsed = Stopwatch.StartNew();
        await limiter.WaitForTurnAsync();

        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(200), $"waited {elapsed.Elapsed}");
    }

    [Fact]
    public async Task WaitForTurnAsync_SecondCaller_IsPacedBehindTheFirst()
    {
        using var limiter = new ExplorerRateLimiter(requestsPerSecond: 10);

        await limiter.WaitForTurnAsync();

        var elapsed = Stopwatch.StartNew();
        await limiter.WaitForTurnAsync();

        // Being throttled by the explorer is not a harmless slowdown: Etherscan answers a
        // rate-limited query with HTTP 200 and an error body, which reads as "no transactions".
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(50), $"waited only {elapsed.Elapsed}");
    }

    [Fact]
    public async Task WaitForTurnAsync_CallerThatGivesUpWhileQueued_DoesNotHoldItsSlot()
    {
        // A cancelled waiter never sends its request. Keeping its reservation pushes everyone behind
        // it further out, and under the worker's per-check deadline that is cumulative: the same tail
        // of payments is cancelled on every pass and never makes progress.
        using var limiter = new ExplorerRateLimiter(requestsPerSecond: 2);

        await limiter.WaitForTurnAsync();

        using var cancelled = new CancellationTokenSource();
        var abandoned = limiter.WaitForTurnAsync(cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        var elapsed = Stopwatch.StartNew();
        await limiter.WaitForTurnAsync();

        // One interval behind the caller that actually went, not two.
        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(900), $"waited {elapsed.Elapsed}");
    }
}
