using System.ComponentModel.DataAnnotations;

namespace CryptoHook.Api.Models.Configs;

public class PaymentCheckConfig
{
    /// <remarks>
    /// A pass costs a handful of explorer requests per watched payment, so this and
    /// <see cref="MaxConcurrentChecks"/> together decide whether the worker keeps up. Polling
    /// faster than a chain produces blocks buys nothing: Bitcoin averages ten minutes a block.
    /// </remarks>
    [Range(5, 3600)]
    public int CheckIntervalSeconds { get; set; } = 30;

    /// <remarks>
    /// The per-explorer rate limit still applies on top of this, so raising it does not defeat the
    /// throttle.
    /// </remarks>
    [Range(1, 64)]
    public int MaxConcurrentChecks { get; set; } = 8;

    /// <summary>
    /// Gets or sets how long a single payment's check may take before it is abandoned for this pass.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="CheckIntervalSeconds"/>. A check spends most of its time waiting
    /// for its turn at the explorer, and that queue is as long as the watch set: with the default
    /// rate of four requests a second and three requests per payment, a hundred watched payments
    /// take well over a minute to work through. Tying the deadline to the interval cancels
    /// everything past the front of that queue on every single pass, so the same tail of payments
    /// never advances.
    /// </remarks>
    [Range(5, 3600)]
    public int CheckTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets how long after expiry a request that never received the expected amount is
    /// still polled before it is abandoned.
    /// </summary>
    /// <remarks>
    /// Funds that arrive just after the timeout should still be detected rather than silently
    /// lost, but a request cannot be watched forever: anything that received a stray payment,
    /// including dust from an address-poisoning attempt, would otherwise be re-polled on every
    /// pass for the lifetime of the deployment.
    /// </remarks>
    [Range(0, 720)]
    public int AbandonAfterExpiryHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets how long a settled payment keeps being watched for a chain reorganisation.
    /// </summary>
    /// <remarks>
    /// A reorg deeper than the configured confirmation threshold can still undo a payment the
    /// merchant has already acted on, and the merchant can only react to that if something
    /// notices.
    /// </remarks>
    [Range(0, 168)]
    public int ReorgWatchHours { get; set; } = 2;

    /// <summary>
    /// Gets or sets how long a payment whose transaction the explorer has stopped reporting keeps
    /// its current status before the disappearance is believed.
    /// </summary>
    /// <remarks>
    /// A transaction vanishing is far more likely to be a lagging explorer than funds actually
    /// disappearing, so it is not applied immediately. It cannot be ignored forever either: a
    /// replaced or dropped transaction never comes back, and a request stuck at Paid is polled on
    /// every pass for the lifetime of the deployment.
    /// </remarks>
    [Range(0, 168)]
    public int MissingFundsGraceHours { get; set; } = 6;

    public TimeSpan CheckInterval => TimeSpan.FromSeconds(CheckIntervalSeconds);

    public TimeSpan CheckTimeout => TimeSpan.FromSeconds(CheckTimeoutSeconds);

    public TimeSpan AbandonAfterExpiry => TimeSpan.FromHours(AbandonAfterExpiryHours);

    public TimeSpan ReorgWatch => TimeSpan.FromHours(ReorgWatchHours);

    public TimeSpan MissingFundsGrace => TimeSpan.FromHours(MissingFundsGraceHours);

    /// <summary>
    /// How far past the abandonment cutoff a request is still polled.
    /// </summary>
    /// <remarks>
    /// Without this the window in which a request is polled and the window in which it qualifies
    /// as abandoned are disjoint: a request leaves the watch set at the exact instant the abandon
    /// rule becomes true, so an underpaid request never reaches Expired and the merchant is never
    /// told.
    /// </remarks>
    public TimeSpan AbandonSweepGrace => TimeSpan.FromTicks(Math.Max(CheckInterval.Ticks * 4, TimeSpan.FromHours(1).Ticks));
}
