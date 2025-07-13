namespace CryptoHook.Api.Models.Enums;

/// <summary>
/// Represents the status of a payment request in its lifecycle.
/// </summary>
public enum PaymentStatusEnum
{
    /// <summary>
    /// The payment request has been created and is awaiting payment.
    /// </summary>
    Pending,

    /// <summary>
    /// A payment has been detected but is awaiting blockchain confirmation.
    /// </summary>
    Paid,

    /// <summary>
    /// The payment was received, but the amount was less than expected.
    /// </summary>
    Underpaid,

    /// <summary>
    /// The payment was received, but the amount was higher than expected.
    /// </summary>
    Overpaid,

    /// <summary>
    /// Multiple transactions have been detected for the same payment request.
    /// </summary>
    MultipleTransactions,

    /// <summary>
    /// The payment has been successfully confirmed on the blockchain.
    /// </summary>
    Confirmed,

    /// <summary>
    /// The payment request was not initiated before its expiration time.
    /// </summary>
    Expired
}