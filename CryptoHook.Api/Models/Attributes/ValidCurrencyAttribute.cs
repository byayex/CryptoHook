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

        var availableCurrency = AvailableCurrencies.Currencies
            .FirstOrDefault(c => c.Symbol == config.Symbol && c.Network == config.Network);

        if (availableCurrency == null)
        {
            return new ValidationResult(FormatErrorMessage(validationContext.DisplayName));
        }

        if (availableCurrency.Name != config.Name)
        {
            return new ValidationResult(FormatErrorMessage(validationContext.DisplayName));
        }

        return ValidationResult.Success;
    }

    public override string FormatErrorMessage(string name)
    {
        var availablePairs = AvailableCurrencies.Currencies
            .Select(c => $"'{c.Symbol}' ('{c.Name}') on network '{c.Network}'")
            .ToList();

        return $"The currency must be a valid combination of Name, Symbol and Network. Supported combinations are: {string.Join(", ", availablePairs)}";
    }
}
