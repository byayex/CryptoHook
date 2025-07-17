using System.ComponentModel.DataAnnotations;
using System.Numerics;

namespace CryptoHook.Api.Models.Payments;

public class PaymentTransaction
{
    /// <summary>
    /// The unique identifier of the blockchain transaction
    /// </summary>
    [Required]
    public required string TransactionId { get; set; }

    /// <summary>
    /// The amount paid in the smallest unit of the cryptocurrency (e.g., satoshis for Bitcoin, wei for Ethereum)
    /// </summary>
    [Required]
    public required BigInteger AmountPaid { get; set; }

    /// <summary>
    /// The number of confirmations the transaction has received on the blockchain
    /// </summary>
    [Required]
    public required uint Confirmations { get; set; }
}