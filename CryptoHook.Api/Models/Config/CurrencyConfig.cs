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
        }

        return results;
    }
}

[ValidCurrency]
public class CurrencyConfig
{
    [Required]
    public required string Name { get; set; }

    [Required]
    public required string Symbol { get; set; }

    [Required]
    public required bool IsEnabled { get; set; }

    [Required]
    public required string PayoutWallet { get; set; }

    /// <summary>
    /// Interval in minutes for payouts.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "PayOutInterval must be greater than 0")]
    public required int PayOutInterval { get; set; }

    [Range(0, float.MaxValue, ErrorMessage = "MinPayoutAmount must be greater than 0")]
    public required float MinPayoutAmount { get; set; }
}