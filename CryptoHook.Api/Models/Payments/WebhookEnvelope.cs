using CryptoHook.Api.Models.Enums;

namespace CryptoHook.Api.Models.Payments;

/// <remarks>
/// The body posted to subscribers. The envelope is what tells them what happened without diffing
/// against their own state, and what leaves room to change the payload's shape later without
/// breaking every subscriber at once.
/// </remarks>
public class WebhookEnvelope
{
    /// <summary>Subscribers should reject versions they do not know rather than guess.</summary>
    public string SpecVersion { get; init; } = "1";

    /// <summary>
    /// Stable across retries and across subscribers, so it doubles as the idempotency key a
    /// subscriber should deduplicate on.
    /// </summary>
    public required Guid EventId { get; init; }

    public required string EventType { get; init; }

    public required DateTime OccurredAt { get; init; }

    public required PaymentRequest Data { get; init; }

    public static string EventTypeFor(PaymentStatus status) => status switch
    {
        PaymentStatus.Pending => "payment.pending",
        PaymentStatus.Paid => "payment.received",
        PaymentStatus.Underpaid => "payment.underpaid",
        PaymentStatus.Overpaid => "payment.overpaid",
        PaymentStatus.MultipleTransactions => "payment.multiple_transactions",
        PaymentStatus.Confirmed => "payment.confirmed",
        PaymentStatus.OverpaidConfirmed => "payment.confirmed.overpaid",
        PaymentStatus.Expired => "payment.expired",
        _ => "payment.updated",
    };
}
