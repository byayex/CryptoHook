using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Text.Json.Serialization;
using CryptoHook.Api.Models.Converters;
using CryptoHook.Api.Models.Enums;

namespace CryptoHook.Api.Models.Payments;

/// <summary>
/// Represents the result of checking the status of a payment on the blockchain.
/// </summary>
public class PaymentCheckResult
{
    /// <summary>
    /// The status determined by the check.
    /// </summary>
    [Required]
    public required PaymentStatusEnum Status { get; set; }

    /// <summary>
    /// The total amount detected on the receiving address, in its smallest unit.
    /// </summary>
    [Required]
    [JsonConverter(typeof(BigIntegerStringConverter))]
    public required BigInteger AmountDetected { get; set; }

    /// <summary>
    /// The number of confirmations for the detected transaction.
    /// </summary>
    [Required]
    public required uint Confirmations { get; set; }

    /// <summary>
    /// Gets or sets the transaction ID of the detected transaction.
    /// </summary>
    public string? TransactionId { get; set; }
}