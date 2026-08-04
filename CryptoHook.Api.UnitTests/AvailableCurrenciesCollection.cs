namespace CryptoHook.Api.UnitTests;

/// <summary>
/// Groups every test class that reads or replaces <c>AvailableCurrencies.GetCurrencies</c>.
/// </summary>
/// <remarks>
/// It is a mutable static, and xUnit runs test classes in parallel: a class that swaps in its own
/// set of currencies was visible to any class validating a configuration at the same moment, which
/// failed as a currency that is suddenly not supported. Sharing a collection is what serialises
/// them, since the substitution is the point of those tests and cannot be removed.
/// </remarks>
[CollectionDefinition(Name)]
public class AvailableCurrenciesCollection
{
    public const string Name = "AvailableCurrencies";
}
