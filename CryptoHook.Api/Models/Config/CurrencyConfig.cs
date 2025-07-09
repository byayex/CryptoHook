using System.ComponentModel.DataAnnotations;
using CryptoHook.Api.Models.Attributes;

namespace CryptoHook.Api.Models.Config;

public class CurrencyConfigList : IValidatableObject
{
    public List<CurrencyConfig> CurrencyConfigs { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        for (int i = 0; i < CurrencyConfigs.Count; i++)
        {
            var config = CurrencyConfigs[i];
            var context = new ValidationContext(config);

            var configResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(config, context, configResults, true))
            {
                foreach (var result in configResults)
                {
                    var memberNames = result.MemberNames.Select(name => $"CurrencyConfigs[{i}].{name}");
                    results.Add(new ValidationResult(result.ErrorMessage, memberNames));
                }
            }

            if (config.Confirmations is null || config.Confirmations.Count == 0)
            {
                results.Add(new ValidationResult($"CurrencyConfigs[{i}].Confirmations must not be null or empty."));
            }
            else
            {
                if (!config.Confirmations.Any(c => c.Amount == 0))
                {
                    results.Add(new ValidationResult($"CurrencyConfigs[{i}].Confirmations must contain an entry with Amount = 0."));
                }

                var amounts = config.Confirmations.Select(c => c.Amount).Distinct().ToList();
                if (amounts.Count != config.Confirmations.Count)
                {
                    results.Add(new ValidationResult($"CurrencyConfigs[{i}].Confirmations must have unique Amount values."));
                }
            }
        }

        return results;
    }
}

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
    /// Gets or sets the extended public key (xpub) used for deriving wallet addresses.
    /// </summary>
    [Required]
    public required string ExtPubKey { get; set; }

    /// <summary>
    /// Gets or sets the blockchain network type (e.g., 'Main' for mainnet).
    /// </summary>
    [Required]
    public required string Network { get; set; }

    /// <summary>
    /// Gets or sets the list of confirmation requirements for different transaction amounts.
    /// </summary>
    [Required]
    public required IReadOnlyList<Confirmation> Confirmations { get; set; }

    public uint GetConfirmationsNeeded(string Symbol, ulong paymentAmount)
    {
        if (Confirmations.Count == 0)
        {
            throw new InvalidOperationException("No confirmations configured for this currency.");
        }

        var confirmationRule = Confirmations
            .LastOrDefault(c => paymentAmount >= c.Amount);

        if (confirmationRule == null)
        {
            throw new InvalidOperationException("No confirmation rule found for the specified amount.");
        }

        return confirmationRule.ConfirmationsNeeded;
    }
}

/// <summary>
/// Defines the minimum confirmation requirements for transactions based on amount thresholds.
/// A default MinConfirmation must be established by creating an entry with an amount of 0. This sets the minimum number of confirmations needed for all transactions.
/// </summary>
public class Confirmation
{
    /// <summary>
    /// Gets or sets the minimum number of blockchain confirmations required for a transaction.
    /// </summary>
    public required uint ConfirmationsNeeded { get; set; }

    /// <summary>
    /// Gets or sets the transaction amount threshold (in smallest currency unit, e.g., satoshis) 
    /// for which the corresponding MinConfirmations applies.
    /// </summary>
    public required ulong Amount { get; set; }
}