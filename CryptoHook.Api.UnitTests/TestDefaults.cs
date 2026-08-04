using CryptoHook.Api.Models.Configs;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.UnitTests;

internal static class TestDefaults
{
    /// <summary>
    /// Checks run one at a time so that tests using <c>SetupSequence</c> stay deterministic.
    /// Tests that care about a specific value build their own.
    /// </summary>
    public static PaymentCheckConfig PaymentCheckConfig() => new()
    {
        CheckIntervalSeconds = 30,
        MaxConcurrentChecks = 1,
        AbandonAfterExpiryHours = 24,
        ReorgWatchHours = 2,
    };

    public static IOptions<PaymentCheckConfig> PaymentCheckOptions() =>
        Options.Create(PaymentCheckConfig());

    public static WebhookDeliveryConfig WebhookDeliveryConfig() => new()
    {
        MaxAttempts = 3,
        InitialBackoffSeconds = 1,
        MaxBackoffSeconds = 4,
        DispatchIntervalSeconds = 1,
        BatchSize = 50,
        RetentionDays = 30,
        SendTimeoutSeconds = 5,
    };

    public static IOptions<WebhookDeliveryConfig> WebhookDeliveryOptions() =>
        Options.Create(WebhookDeliveryConfig());
}
