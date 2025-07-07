using System.ComponentModel.DataAnnotations;
using CryptoHook.Api.Models.Consts;

namespace CryptoHook.Api.Models.Attributes;

public class ValidCurrencyAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is string currency)
        {
            return AvailableCurrencies.Currencies.Contains(currency);
        }
        return false;
    }

    public override string FormatErrorMessage(string name)
    {
        return $"The {name} field must be one of: {string.Join(", ", AvailableCurrencies.Currencies)}";
    }
}
