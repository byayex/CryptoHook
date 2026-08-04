using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.Models.Consts;

public static class AvailableCurrencies
{
    private static readonly IReadOnlyList<AvailableCurrency> _defaultCurrencies = [
        new() { Name = "Bitcoin", Symbol = "BTC", Network = "Main" },
        new() { Name = "Ethereum", Symbol = "ETH", Network = "Main" },
        new() { Name = "Ethereum", Symbol = "ETH", Network = "Sepolia" },
    ];

    /// <summary>
    /// The allowlist the configuration is validated against, not the set of currencies that are
    /// usable: for those, ask <c>ConfigManager.GetUsableCurrencies()</c>, which also honours
    /// whether a currency is enabled. The setter is internal because it is a test seam only.
    /// </summary>
    public static Func<IReadOnlyList<AvailableCurrency>> GetCurrencies { get; internal set; } = () => _defaultCurrencies;
}
