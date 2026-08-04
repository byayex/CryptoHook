using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using CryptoHook.Api.Models.Attributes;

namespace CryptoHook.Api.Models.Configs;

[ValidCurrency]
public class CurrencyConfig
{
    [Required]
    public required string Name { get; set; }

    [Required]
    public required string Symbol { get; set; }

    [Required]
    public required bool IsEnabled { get; set; }

    /// <summary>Gets or sets how long a new payment request stays valid, in minutes.</summary>
    [Required]
    [Range(1, 10_080)]
    public required double InitialPaymentTimeout { get; set; }

    [Required]
    public required string ExtPubKey { get; set; }

    [Required]
    public required string Network { get; set; }

    /// <summary>
    /// Gets or sets the block explorer API key. Optional, but some explorers (Etherscan) throttle
    /// or reject keyless requests.
    /// </summary>
    public string? ExplorerApiKey { get; set; }

    /// <summary>
    /// Gets or sets how many payment requests may be pending at the same time. A load guard only;
    /// the address gap limit is enforced separately by <see cref="AddressGapLimit"/>.
    /// </summary>
    [Range(1, 1_000_000)]
    public int MaxPendingRequests { get; set; } = 100;

    /// <summary>
    /// Gets or sets how many consecutive derivation indices may go unused before new payment
    /// requests are refused.
    /// </summary>
    /// <remarks>
    /// Every payment request burns a derivation index for good, whether or not it is ever paid.
    /// A watch-only wallet stops scanning after it has seen this many consecutive addresses
    /// without any history, so once the run of unpaid indices reaches the limit the merchant's
    /// wallet no longer discovers incoming funds. The common default in wallet software is 20.
    /// </remarks>
    [Range(1, 100_000)]
    public int AddressGapLimit { get; set; } = 20;

    /// <summary>
    /// Gets or sets how far a received total may deviate from the expected amount, in the
    /// currency's smallest unit, and still count as exact.
    /// </summary>
    /// <remarks>
    /// Without a tolerance a payment that is a single satoshi or wei short is underpaid forever.
    /// Kept at zero by default so that the strict behaviour has to be opted out of deliberately.
    /// </remarks>
    public string AmountTolerance { get; set; } = "0";

    /// <summary>
    /// Gets or sets how many requests per second this currency's block explorer may be sent.
    /// Etherscan's free tier allows five; Blockstream is not documented but throttles well below
    /// that under sustained load.
    /// </summary>
    [Range(0.1, 100)]
    public double ExplorerRequestsPerSecond { get; set; } = 4;

    /// <summary>Falls back to zero when <see cref="AmountTolerance"/> is not a valid amount.</summary>
    [JsonIgnore]
    public BigInteger AmountToleranceValue =>
        BigInteger.TryParse(AmountTolerance, CultureInfo.InvariantCulture, out var tolerance) && tolerance >= 0
            ? tolerance
            : BigInteger.Zero;

    [JsonIgnore]
    private IList<Confirmation> _confirmations = [];

    /// <summary>Kept sorted by amount, because the lookup relies on the ordering.</summary>
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
