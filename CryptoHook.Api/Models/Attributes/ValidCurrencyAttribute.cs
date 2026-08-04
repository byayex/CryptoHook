using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Numerics;
using CryptoHook.Api.Models.Consts;
using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.Models.Attributes;

public sealed class ValidCurrencyAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        ArgumentNullException.ThrowIfNull(validationContext);

        if (value is not CurrencyConfig config)
        {
            return new ValidationResult("The configuration must be of type CurrencyConfig.");
        }

        // Case insensitively, because every lookup that resolves a currency at runtime is. A
        // configured symbol of "btc" would otherwise fail startup validation with a confusing
        // message even though the rest of the system accepts it.
        var availableCurrency = AvailableCurrencies.GetCurrencies()
            .FirstOrDefault(c =>
                string.Equals(c.Symbol, config.Symbol, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Network, config.Network, StringComparison.OrdinalIgnoreCase));

        if (availableCurrency == null)
        {
            return new ValidationResult(FormatErrorMessage(validationContext.DisplayName));
        }

        if (!string.Equals(availableCurrency.Name, config.Name, StringComparison.OrdinalIgnoreCase))
        {
            return new ValidationResult(FormatErrorMessage(validationContext.DisplayName));
        }

        if (config.Confirmations is null || config.Confirmations.Count == 0)
        {
            return new ValidationResult("Confirmations must not be empty.", [nameof(config.Confirmations)]);
        }

        if (config.Confirmations.Any(c => !c.HasValidAmount))
        {
            return new ValidationResult(
                "Each Confirmations entry must give MinAmount as a non-negative integer in the currency's smallest unit.",
                [nameof(config.Confirmations)]);
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

        // A rule of zero confirmations settles a payment straight out of the mempool, where the
        // payer can still replace or double spend it. Refusing the configuration is the only
        // point at which this is still cheap to catch.
        if (config.Confirmations.Any(c => c.ConfirmationsNeeded < 1))
        {
            return new ValidationResult(
                "ConfirmationsNeeded must be at least 1. Settling a payment at zero confirmations accepts an unconfirmed transaction that can still be replaced.",
                [nameof(config.Confirmations)]);
        }

        // The [Range] on the property is never evaluated, because validation of the list does not
        // recurse into the items of the Confirmations property. An absurd requirement is not merely
        // untidy: every payment for that currency would sit at Paid and never settle.
        if (config.Confirmations.Any(c => c.ConfirmationsNeeded > Confirmation.MaximumConfirmationsNeeded))
        {
            return new ValidationResult(
                $"ConfirmationsNeeded must not exceed {Confirmation.MaximumConfirmationsNeeded}. A payment can never reach a threshold that high, so it would never settle.",
                [nameof(config.Confirmations)]);
        }

        if (!BigInteger.TryParse(config.AmountTolerance, CultureInfo.InvariantCulture, out var tolerance) || tolerance < 0)
        {
            return new ValidationResult(
                "AmountTolerance must be a non-negative integer given in the currency's smallest unit.",
                [nameof(config.AmountTolerance)]);
        }

        // Resolving the chain id here turns an unsupported network into a failed startup rather
        // than a 500 on the first payment check for that currency.
        return config.Symbol.Equals("ETH", StringComparison.OrdinalIgnoreCase)
               && !EthereumNetworks.TryGetChainId(config.Network, out _)
            ? new ValidationResult(
                $"Ethereum network '{config.Network}' has no known chain id. Supported networks are {string.Join(", ", EthereumNetworks.SupportedNetworks)}.",
                [nameof(config.Network)])
            : ValidationResult.Success;
    }

    public override string FormatErrorMessage(string name)
    {
        var availablePairs = AvailableCurrencies.GetCurrencies()
            .Select(c => $"'{c.Symbol}' ('{c.Name}') on network '{c.Network}'")
            .ToList();

        return $"The currency must be a valid combination of Name, Symbol and Network. Supported combinations are: {string.Join(", ", availablePairs)}";
    }
}
