namespace CryptoHook.Api.Models.Enums;

/// <remarks>
/// The numeric values are persisted, so new members must only ever be appended.
/// </remarks>
public enum WebhookDeliveryStatus
{
    Pending = 0,

    Delivered = 1,

    /// <summary>
    /// Every attempt failed. The record is kept as a dead letter so that an operator can see what
    /// was never delivered and replay it once the subscriber is healthy again.
    /// </summary>
    Failed = 2,
}
