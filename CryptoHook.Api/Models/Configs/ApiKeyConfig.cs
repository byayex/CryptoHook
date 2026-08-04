using System.ComponentModel.DataAnnotations;

namespace CryptoHook.Api.Models.Configs;

public class ApiKeyConfig : List<string>, IValidatableObject
{
    /// <summary>
    /// Short keys are brute-forceable, so they are rejected at startup rather than silently
    /// accepted.
    /// </summary>
    public const int MinimumKeyLength = 16;

    private static readonly string[] ApiKeysMemberName = ["ApiKeys"];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Count == 0)
        {
            yield return new ValidationResult(
                "At least one API key must be configured in the 'ApiKeys' section.",
                ApiKeysMemberName);
            yield break;
        }

        for (int i = 0; i < Count; i++)
        {
            var key = this[i];

            if (string.IsNullOrWhiteSpace(key))
            {
                yield return new ValidationResult($"ApiKeys[{i}] must not be empty or whitespace.", ApiKeysMemberName);
                continue;
            }

            if (key.Length < MinimumKeyLength)
            {
                yield return new ValidationResult(
                    $"ApiKeys[{i}] must be at least {MinimumKeyLength} characters long.",
                    ApiKeysMemberName);
            }
        }

        var duplicates = this.Where(k => !string.IsNullOrWhiteSpace(k))
            .GroupBy(k => k, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            yield return new ValidationResult("ApiKeys must not contain duplicate entries.", ApiKeysMemberName);
        }
    }
}
