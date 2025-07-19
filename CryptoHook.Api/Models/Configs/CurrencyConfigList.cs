using System.ComponentModel.DataAnnotations;
using System.Numerics;

namespace CryptoHook.Api.Models.Configs;

/// <summary>
/// A collection of currency configurations with validation for list-level constraints.
/// Individual currency validation is handled by the ValidCurrencyAttribute on CurrencyConfig.
/// </summary>
public class CurrencyConfigList : List<CurrencyConfig>, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        // Validate each individual currency config (including ValidCurrencyAttribute and IValidatableObject)
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
        }

        // Validate list-level constraints: no duplicate symbol/network combinations
        ValidateUniqueSymbolNetworkCombinations(results);

        return results;
    }

    private void ValidateUniqueSymbolNetworkCombinations(List<ValidationResult> results)
    {
        var duplicateCombinations = this.GroupBy(c => new { c.Symbol, c.Network })
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var combo in duplicateCombinations)
        {
            results.Add(new ValidationResult($"The combination of Symbol '{combo.Symbol}' and Network '{combo.Network}' must be unique.", new[] { "Symbol", "Network" }));
        }
    }
}