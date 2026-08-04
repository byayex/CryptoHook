using System.ComponentModel.DataAnnotations;
using System.Numerics;

namespace CryptoHook.Api.Models.Payments;

public class PaymentTransaction
{
    [Required]
    public required string TransactionId { get; set; }

    /// <summary>Satoshis for Bitcoin, wei for Ethereum.</summary>
    [Required]
    public required BigInteger AmountPaid { get; set; }

    [Required]
    public required uint Confirmations { get; set; }
}