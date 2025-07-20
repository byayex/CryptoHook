using System.ComponentModel.DataAnnotations;
using CryptoHook.Api.Models.Consts;
using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.Models.Attributes;

public class ValidCurrencyAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not CurrencyConfig config)
        {
            return new ValidationResult("The configuration must be of type CurrencyConfig.");
        }

        var availableCurrency = AvailableCurrencies.GetCurrencies()
            .FirstOrDefault(c => c.Symbol == config.Symbol && c.Network == config.Network);

        if (availableCurrency == null)
        {
            return new ValidationResult(FormatErrorMessage(validationContext.DisplayName));
        }

        if (availableCurrency.Name != config.Name)
        {
            return new ValidationResult(FormatErrorMessage(validationContext.DisplayName));
        }

        // Validate confirmations
        if (config.Confirmations is null || config.Confirmations.Count == 0)
        {
            return new ValidationResult("Confirmations must not be empty.", [nameof(config.Confirmations)]);
        }

        if (!config.Confirmations.Any(c => c.Amount == 0))
        {
            return new ValidationResult("Confirmations must have a base entry with Amount = 0.", [nameof(config.Confirmations)]);
        }

        var amounts = config.Confirmations.Select(c => c.Amount).ToList();
        var distinctAmounts = amounts.Distinct().ToList();

        if (distinctAmounts.Count != amounts.Count)
        {
            return new ValidationResult("Confirmations must have unique Amount values.", [nameof(config.Confirmations)]);
        }

        return ValidationResult.Success;
    }

    public override string FormatErrorMessage(string name)
    {
        var availablePairs = AvailableCurrencies.GetCurrencies()
            .Select(c => $"'{c.Symbol}' ('{c.Name}') on network '{c.Network}'")
            .ToList();

        return $"The currency must be a valid combination of Name, Symbol and Network. Supported combinations are: {string.Join(", ", availablePairs)}";
    }
}
