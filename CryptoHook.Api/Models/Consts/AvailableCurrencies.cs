using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.Models.Consts;

public static class AvailableCurrencies
{
    private static readonly IReadOnlyList<AvailableCurrency> _defaultCurrencies = [
      new() { Name = "Bitcoin", Symbol = "BTC", Network = "Main" },
    new() { Name = "Ethereum", Symbol = "ETH", Network = "MAIN" },
    new() { Name = "Ethereum", Symbol = "ETH", Network = "SEPOLIA" },
    new() { Name = "Ethereum", Symbol = "ETH", Network = "GOERLI" },
  ];

    /// <summary>
    /// Gets the list of available currencies. 
    /// SHOULD ONLY BE USED TO VALIDATE SETTINGS.
    /// Use ConfigManager.GetUsableCurrencies() to retrieve currencies that are enabled in the configuration
    /// </summary>
    /// <returns>A read-only list of available currencies.</returns>
    public static Func<IReadOnlyList<AvailableCurrency>> GetCurrencies { get; set; } = () => _defaultCurrencies;
}