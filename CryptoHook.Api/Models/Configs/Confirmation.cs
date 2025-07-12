using System.Numerics;
using System.Text.Json.Serialization;

namespace CryptoHook.Api.Models.Configs;

/// <summary>
/// Defines the minimum confirmation requirements for transactions based on amount thresholds.
/// A default MinConfirmation must be established by creating an entry with an amount of 0. This sets the minimum number of confirmations needed for all transactions.
/// </summary>
public class Confirmation
{
    public required uint ConfirmationsNeeded { get; set; }

    private string? MinAmount { get; set; }

    /// <summary>
    /// Gets or sets the transaction amount threshold.
    /// </summary>
    public required BigInteger Amount
    {
        get => BigInteger.Parse(MinAmount ?? "0");
        set => MinAmount = value.ToString();
    }
}