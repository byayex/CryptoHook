using CryptoHook.Api.Models.Configs;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Managers;

public class ConfigManager
{
    private readonly ILogger<ConfigManager> _logger;
    private readonly CurrencyConfigList _currencyConfigList;
    private readonly IAvailableCurrenciesService _availableCurrenciesService;
    private readonly IReadOnlyList<AvailableCurrency> UsableCurrencies = [];

    public ConfigManager(IOptions<CurrencyConfigList> currencyConfigList, ILogger<ConfigManager> logger, IAvailableCurrenciesService availableCurrenciesService)
    {
        _logger = logger;
        _currencyConfigList = currencyConfigList.Value;
        _availableCurrenciesService = availableCurrenciesService;

        foreach (var config in _currencyConfigList)
        {
            config.Confirmations = [.. config.Confirmations.OrderBy(c => c.Amount)];
        }

        UsableCurrencies = _availableCurrenciesService.GetAvailableCurrencies()
            .Where(c => _currencyConfigList.Any(cc =>
                cc.Symbol.Equals(c.Symbol, StringComparison.OrdinalIgnoreCase)
                && cc.Network.Equals(c.Network, StringComparison.OrdinalIgnoreCase)
                && cc.IsEnabled))
            .Select(c => new AvailableCurrency
            {
                Symbol = c.Symbol,
                Name = c.Name,
                Network = c.Network
            }).ToList();
    }

    public CurrencyConfig GetCurrencyConfig(string symbol, string network)
    {
        _logger.LogDebug("Retrieving config for currency: {Symbol} and network: {Network}", symbol, network);

        var config = _currencyConfigList.FirstOrDefault(c =>
            c.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
            && c.Network.Equals(network, StringComparison.OrdinalIgnoreCase));

        if (config is null)
        {
            _logger.LogError("Currency config for {Symbol} with Network {Network} not found.", symbol, network);
            throw new InvalidOperationException($"Currency config for {symbol} in Network {network} not found.");
        }

        return config;
    }

    public IReadOnlyList<AvailableCurrency> GetUsableCurrencies()
    {
        _logger.LogDebug("Returning {Count} usable currencies", UsableCurrencies.Count);
        return UsableCurrencies;
    }
}