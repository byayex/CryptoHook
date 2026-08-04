using System.ComponentModel.DataAnnotations;

namespace CryptoHook.Api.Models.Configs;

public class WebhookConfig
{
    /// <summary>
    /// The shortest secret that is accepted. A deployment that never replaced the placeholder from
    /// the sample configuration signs its webhooks with a value that is public, so a subscriber
    /// verifying the signature learns nothing. Refusing to start is the only way that gets noticed.
    /// </summary>
    public const int MinimumSecretLength = 32;

    [Required]
    public required Uri Url { get; set; }

    [Required]
    [MinLength(MinimumSecretLength)]
    public required string Secret { get; set; }
}

/// <remarks>
/// Validates its own elements. <c>ValidateDataAnnotations</c> only inspects the members of the type
/// it is given, and this one is a collection, so without this the per-entry <see cref="WebhookConfig"/>
/// rules never run: a placeholder secret would start cleanly and an entry without a URL would bind
/// <see cref="WebhookConfig.Url"/> to null and take down every payment update with it.
/// </remarks>
public class WebhookConfigList : List<WebhookConfig>, IValidatableObject
{
    private static readonly string[] WebhooksMemberName = ["Webhooks"];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        for (int i = 0; i < Count; i++)
        {
            var webhook = this[i];

            if (webhook is null)
            {
                results.Add(new ValidationResult($"Webhooks[{i}] must not be null.", WebhooksMemberName));
                continue;
            }

            // Configuration binding does not honour 'required', so a missing Url arrives as null
            // rather than as a binding failure.
            if (webhook.Url is null)
            {
                results.Add(new ValidationResult($"Webhooks[{i}].Url is required.", WebhooksMemberName));
            }
            else if (!webhook.Url.IsAbsoluteUri)
            {
                results.Add(new ValidationResult(
                    $"Webhooks[{i}].Url must be an absolute URI, for example 'https://example.com/hooks/cryptohook'.",
                    WebhooksMemberName));
            }

            if (string.IsNullOrWhiteSpace(webhook.Secret))
            {
                results.Add(new ValidationResult($"Webhooks[{i}].Secret is required.", WebhooksMemberName));
            }
            else if (webhook.Secret.Length < WebhookConfig.MinimumSecretLength)
            {
                results.Add(new ValidationResult(
                    $"Webhooks[{i}].Secret must be at least {WebhookConfig.MinimumSecretLength} characters long.",
                    WebhooksMemberName));
            }
        }

        return results;
    }
}

/// <remarks>
/// Every notification is persisted before it is attempted and retried on this schedule until it is
/// accepted or the budget is exhausted, so that a brief outage at a subscriber cannot lose a
/// settlement event permanently.
/// </remarks>
public class WebhookDeliveryConfig
{
    [Range(1, 50)]
    public int MaxAttempts { get; set; } = 8;

    /// <summary>
    /// Gets or sets the delay before the second attempt. Each further attempt doubles it, up to
    /// <see cref="MaxBackoffSeconds"/>. The default schedule (eight attempts from fifteen seconds:
    /// 15, 30, 60, 120, 240, 480, 960) spans roughly thirty minutes; raise
    /// <see cref="MaxAttempts"/> to cover a longer subscriber outage.
    /// </summary>
    [Range(1, 3600)]
    public int InitialBackoffSeconds { get; set; } = 15;

    [Range(1, 86400)]
    public int MaxBackoffSeconds { get; set; } = 3600;

    [Range(1, 600)]
    public int DispatchIntervalSeconds { get; set; } = 10;

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Gets or sets how long a delivered or exhausted record is kept before it is pruned. The
    /// history is what lets an operator answer "did the merchant ever get told".
    /// </summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 30;

    [Range(1, 300)]
    public int SendTimeoutSeconds { get; set; } = 15;

    public TimeSpan DispatchInterval => TimeSpan.FromSeconds(DispatchIntervalSeconds);

    public TimeSpan SendTimeout => TimeSpan.FromSeconds(SendTimeoutSeconds);

    public TimeSpan Retention => TimeSpan.FromDays(RetentionDays);

    /// <summary>Attempt numbers are 1-based, so the first attempt is immediate.</summary>
    public TimeSpan BackoffFor(int attemptCount)
    {
        if (attemptCount <= 0)
        {
            return TimeSpan.Zero;
        }

        // Doubling in the exponent space rather than in seconds keeps a large MaxAttempts from
        // overflowing before it is clamped.
        var exponent = Math.Min(attemptCount - 1, 30);
        var seconds = Math.Min((double)InitialBackoffSeconds * Math.Pow(2, exponent), MaxBackoffSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
