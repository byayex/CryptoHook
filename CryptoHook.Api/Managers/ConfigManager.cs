using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Managers;

public class ConfigManager
{
    private readonly ILogger<ConfigManager> _logger;
    private readonly CurrencyConfigList _currencyConfigList;
    public readonly IReadOnlyList<AvailableCurrency> UsableCurrencies = [];

    public ConfigManager(IOptions<CurrencyConfigList> currencyConfigList, ILogger<ConfigManager> logger)
    {
        _logger = logger;
        _currencyConfigList = currencyConfigList.Value;

        foreach (var config in _currencyConfigList)
        {
            config.Confirmations = [.. config.Confirmations.OrderBy(c => c.Amount)];
        }

        UsableCurrencies = AvailableCurrencies.Currencies
            .Where(c => _currencyConfigList.Any(cc => cc.Symbol == c.Symbol && cc.IsEnabled))
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

        _logger.LogDebug("Config for {Symbol} ({Network}) found: {Config}", symbol, network, config);

        if (config is null)
        {
            _logger.LogError("Currency config for {Symbol} with Network {Network} not found.", symbol, network);
            throw new InvalidOperationException($"Currency config for {symbol} in Network {network} not found.");
        }

        return config;
    }
}