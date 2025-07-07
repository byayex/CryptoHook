using System.ComponentModel.DataAnnotations;
using CryptoHook.Api.Models.Attributes;

namespace CryptoHook.Api.Models.Config;

public abstract class CurrencyConfig
{
    [Required]
    [ValidCurrency]
    public required string Name { get; set; }

    public bool IsEnabled { get; set; }

    [Required]
    public required string PayoutWallet { get; set; }

    /// <summary>
    /// Interval in minutes for payouts.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "PayOutInterval must be greater than 0")]
    public required int PayOutInterval { get; set; }

    [Range(0.01, float.MaxValue, ErrorMessage = "MinPayoutAmount must be greater than 0")]
    public required float MinPayoutAmount { get; set; }
}