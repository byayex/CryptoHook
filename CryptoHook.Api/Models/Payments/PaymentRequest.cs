using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Text.Json.Serialization;
using CryptoHook.Api.Models.Converters;
using CryptoHook.Api.Models.Enums;

namespace CryptoHook.Api.Models.Payments;

/// <summary>
/// Models a request for a specific amount of cryptocurrency to be paid.
/// </summary>
public class PaymentRequest
{
    /// <summary>
    /// Gets or sets the public identifier for the payment request (used in API responses).
    /// </summary>
    [Key, Required]
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the internal sequential ID used for deriving crypto addresses.
    /// </summary>
    [JsonIgnore]
    public ulong DerivationIndex { get; set; }

    /// <summary>
    /// Gets or sets the current status of the payment request (e.g., Pending, Paid, Expired).
    /// </summary>
    [Required]
    public required PaymentStatusEnum Status { get; set; }

    /// <summary>
    /// Gets or sets the amount of currency expected for this payment, in its smallest unit (e.g., satoshis).
    /// </summary>
    [Required]
    [JsonConverter(typeof(BigIntegerStringConverter))]
    public required BigInteger ExpectedAmount { get; set; }

    /// <summary>
    /// Gets or sets the total amount of currency that has been paid towards this request so far.
    /// </summary>
    [Required]
    [JsonConverter(typeof(BigIntegerStringConverter))]
    public required BigInteger AmountPaid { get; set; }

    /// <summary>
    /// Gets or sets the unique cryptocurrency address generated for this specific payment request.
    /// </summary>
    [Required]
    public required string ReceivingAddress { get; set; }

    /// <summary>
    /// Gets or sets the symbol that represents the currency for this payment request (e.g., BTC, ETH).
    /// </summary>
    [Required]
    public required string CurrencySymbol { get; set; }

    /// <summary>
    /// Gets or sets the timestamp for when the payment request was created.
    /// </summary>
    [Required]
    public required DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp for when this payment request becomes invalid.
    /// </summary>
    [Required]
    public required DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the transaction ID of the detected transaction.
    /// </summary>
    public string? TransactionId { get; set; }
}

