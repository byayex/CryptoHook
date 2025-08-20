using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json.Serialization;
using CryptoHook.Api.Models.Attributes;

namespace CryptoHook.Api.Models.Configs;

/// <summary>
/// Configuration settings for a cryptocurrency, including network details and confirmation requirements.
/// </summary>
[ValidCurrency]
public class CurrencyConfig
{
    /// <summary>
    /// Gets or sets the display name of the cryptocurrency (e.g., 'Bitcoin').
    /// </summary>
    [Required]
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the ticker symbol representing the cryptocurrency (e.g., 'BTC').
    /// </summary>
    [Required]
    public required string Symbol { get; set; }

    /// <summary>
    /// Gets or sets a boolean flag indicating if this currency is enabled for use.
    /// </summary>
    [Required]
    public required bool IsEnabled { get; set; }
    /// <summary>
    /// Gets or sets the initial timeout in minutes for payment processing.
    /// </summary>
    [Required]
    public required double InitialPaymentTimeout { get; set; }

    /// <summary>
    /// Gets or sets the extended public key (xpub) used for deriving wallet addresses.
    /// </summary>
    [Required]
    public required string ExtPubKey { get; set; }

    /// <summary>
    /// Gets or sets the blockchain network type (e.g., 'Main' for mainnet).
    /// </summary>
    [Required]
    public required string Network { get; set; }

    [JsonIgnore]
    private IList<Confirmation> _confirmations = [];

    /// <summary>
    /// Gets or sets the list of confirmation requirements for different transaction amounts.
    /// </summary>
    [Required]
    public required IList<Confirmation> Confirmations
    {
        get => _confirmations; init => _confirmations = [.. value.OrderBy(c => c.Amount)];
    }

    public uint GetConfirmationsNeeded(BigInteger paymentAmount)
    {
        if (Confirmations.Count == 0)
        {
            throw new InvalidOperationException("No confirmations configured for this currency.");
        }

        var confirmationRule = Confirmations
            .LastOrDefault(c => paymentAmount >= c.Amount);

        return confirmationRule == null
            ? throw new InvalidOperationException("No confirmation rule found for the specified amount.")
            : confirmationRule.ConfirmationsNeeded;
    }
}

