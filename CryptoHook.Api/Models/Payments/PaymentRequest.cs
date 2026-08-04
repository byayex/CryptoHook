using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Text.Json.Serialization;
using CryptoHook.Api.Models.Converters;
using CryptoHook.Api.Models.Enums;

namespace CryptoHook.Api.Models.Payments;

public class PaymentRequest
{
    [Key, Required]
    public Guid Id { get; set; }

    [JsonIgnore]
    public ulong DerivationIndex { get; set; }

    [Required]
    public required PaymentStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the expected amount in the currency's smallest unit: satoshis, not BTC, and
    /// wei, not ether.
    /// </summary>
    [Required]
    [JsonConverter(typeof(BigIntegerStringConverter))]
    public required BigInteger AmountExpected { get; set; }

    /// <summary>
    /// Gets or sets the total received so far, in the currency's smallest unit, summed over every
    /// inbound transaction.
    /// </summary>
    [Required]
    [JsonConverter(typeof(BigIntegerStringConverter))]
    public required BigInteger AmountPaid { get; set; }

    [Required]
    public required uint ConfirmationCount { get; set; }

    /// <summary>
    /// Gets or sets the confirmations required to settle, frozen at creation time so that a later
    /// configuration change cannot move the goalposts for a request already in flight.
    /// </summary>
    [Required]
    public required uint ConfirmationNeeded { get; set; }

    [Required]
    public required string ReceivingAddress { get; set; }

    [Required]
    public required string CurrencySymbol { get; set; }

    [Required]
    public required string Network { get; set; }

    [Required]
    public required DateTime CreatedAt { get; set; }

    [Required]
    public required DateTime UpdatedAt { get; set; }

    [Required]
    public required DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the identifiers of the detected inbound transactions, comma separated and
    /// ordered oldest first. A payment is usually a single transaction, but payers and exchanges
    /// do split one across several, so more than one identifier is normal.
    /// </summary>
    public string? TransactionId { get; set; }

    /// <summary>
    /// Gets or sets whether the funds arrived after <see cref="ExpiresAt"/> had already passed.
    /// The merchant may already have cancelled the order, and the amount was quoted against a
    /// rate that is no longer current. Late payments are accepted rather than dropped, but they
    /// need to be recognisable as such.
    /// </summary>
    public bool WasLate { get; set; }

    /// <summary>
    /// Gets or sets when the request first reached a settled status. Anchors the window during
    /// which the worker keeps watching a settled payment for a chain reorganisation.
    /// </summary>
    public DateTime? SettledAt { get; set; }

    public void SetStatus(PaymentStatus newStatus)
    {
        if (Status != newStatus)
        {
            Status = newStatus;
            UpdatedAt = DateTime.UtcNow;
        }
    }
}
