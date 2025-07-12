using System.ComponentModel.DataAnnotations;
using CryptoHook.Api.Models.Consts;
using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.Models.Attributes;

public class ValidCurrencyAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is not CurrencyConfig config)
            return false;

        if (!AvailableCurrencies.Currencies.ContainsKey(config.Symbol))
            return false;

        AvailableCurrencies.Currencies.TryGetValue(config.Symbol, out var currencyName);
        if (string.IsNullOrWhiteSpace(currencyName))
            return false;

        return AvailableCurrencies.Currencies[config.Symbol] == config.Name;
    }

    public override string FormatErrorMessage(string name)
    {
        var availablePairs = AvailableCurrencies.Currencies
            .Select(kvp => $"{kvp.Key} ({kvp.Value})")
            .ToList();

        return $"The currency Name and Symbol must be a valid combination. Available pairs: {string.Join(", ", availablePairs)}";
    }
}
