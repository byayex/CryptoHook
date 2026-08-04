namespace CryptoHook.Api.Models.Enums;

/// <remarks>
/// The numeric values are persisted, so new members must only ever be appended.
/// </remarks>
public enum PaymentStatus
{
    Pending = 0,

    Paid = 1,

    Underpaid = 2,

    Overpaid = 3,

    /// <summary>
    /// No longer produced. Multiple inbound transactions are now aggregated into a single balance
    /// instead of being rejected. Retained so that rows written by older versions keep
    /// deserialising; the worker re-evaluates them and moves them to a current status.
    /// </summary>
    MultipleTransactions = 4,

    Confirmed = 5,

    /// <summary>
    /// The request was abandoned: it expired without ever receiving the expected amount.
    /// <see cref="Payments.PaymentRequest.AmountPaid"/> may still be non-zero, in which case
    /// partial funds were received and need to be refunded out of band.
    /// </summary>
    Expired = 6,

    OverpaidConfirmed = 7,
}
