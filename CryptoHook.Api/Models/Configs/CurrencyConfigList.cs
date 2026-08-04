using System.ComponentModel.DataAnnotations;

namespace CryptoHook.Api.Models.Configs;

public class CurrencyConfigList : List<CurrencyConfig>, IValidatableObject
{
    private static readonly string[] SymbolNetworkMemberNames = ["Symbol", "Network"];

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
        }

        ValidateUniqueSymbolNetworkCombinations(results);

        return results;
    }

    private void ValidateUniqueSymbolNetworkCombinations(List<ValidationResult> results)
    {
        // Case insensitively, because every lookup that resolves a currency later on is: two
        // entries differing only in case would both pass here and then resolve ambiguously.
        var duplicateCombinations = this
            .GroupBy(c => (c.Symbol, c.Network), TupleComparer)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var combo in duplicateCombinations)
        {
            results.Add(new ValidationResult(
                $"The combination of Symbol '{combo.Symbol}' and Network '{combo.Network}' must be unique.",
                SymbolNetworkMemberNames));
        }
    }

    private static readonly IEqualityComparer<(string Symbol, string Network)> TupleComparer =
        new SymbolNetworkComparer();

    private sealed class SymbolNetworkComparer : IEqualityComparer<(string Symbol, string Network)>
    {
        public bool Equals((string Symbol, string Network) left, (string Symbol, string Network) right) =>
            string.Equals(left.Symbol, right.Symbol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Network, right.Network, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Symbol, string Network) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Symbol),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Network));
    }
}
