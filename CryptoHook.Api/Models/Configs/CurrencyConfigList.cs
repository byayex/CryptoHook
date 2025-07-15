using System.ComponentModel.DataAnnotations;
using System.Numerics;
using CryptoHook.Api.Models.Attributes;
using CryptoHook.Api.Models.Consts;

namespace CryptoHook.Api.Models.Configs;

public class CurrencyConfigList : List<CurrencyConfig>, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        for (int i = 0; i < Count; i++)
        {
            var config = this[i];
            var context = new ValidationContext(config);
            var configResults = new List<ValidationResult>();

            Validator.TryValidateObject(config, context, configResults, true);

            foreach (var result in configResults)
            {
                var memberNames = result.MemberNames.Select(name => $"[{i}].{name}");
                results.Add(new ValidationResult(result.ErrorMessage, memberNames));
            }

            if (!AvailableCurrencies.Currencies.Any(c => c.Symbol == config.Symbol && c.Network == config.Network))
            {
                results.Add(new ValidationResult($"CurrencyConfigs[{i}].Symbol and CurrencyConfigs[{i}].Network combination is not supported.", new[] { $"[{i}].Symbol", $"[{i}].Network" }));
            }



            if (config.Confirmations is null || config.Confirmations.Count == 0)
            {
                results.Add(new ValidationResult($"CurrencyConfigs[{i}].Confirmations must not be empty."));
            }
            else
            {
                if (!config.Confirmations.Any(c => c.Amount == 0))
                {
                    results.Add(new ValidationResult($"CurrencyConfigs[{i}].Confirmations must have a base entry with Amount = 0."));
                }

                var amounts = config.Confirmations.Select(c => c.Amount).ToList();
                var distinctAmounts = amounts.Distinct().ToList();

                if (distinctAmounts.Count != amounts.Count)
                {
                    results.Add(new ValidationResult($"CurrencyConfigs[{i}].Confirmations must have unique Amount values."));
                }
            }
        }

        var duplicateCombinations = this.GroupBy(c => new { c.Symbol, c.Network })
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var combo in duplicateCombinations)
        {
            results.Add(new ValidationResult($"The combination of Symbol '{combo.Symbol}' and Network '{combo.Network}' must be unique.", new[] { "Symbol", "Network" }));
        }

        return results;
    }
}