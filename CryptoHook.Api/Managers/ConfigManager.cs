using CryptoHook.Api.Models.Configs;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Managers;

public class ConfigManager
{
    private readonly ILogger<ConfigManager> _logger;
    private readonly CurrencyConfigList _currencyConfigList;

    public ConfigManager(IOptions<CurrencyConfigList> currencyConfigList, ILogger<ConfigManager> logger)
    {
        _logger = logger;
        _currencyConfigList = currencyConfigList.Value;

        foreach (var config in _currencyConfigList)
        {
            config.Confirmations = [.. config.Confirmations.OrderBy(c => c.Amount)];
        }
    }

    public CurrencyConfig GetCurrencyConfig(string Symbol)
    {
        _logger.LogDebug("Retrieving config for currency: {Symbol}", Symbol);

        var config = _currencyConfigList.FirstOrDefault(c => c.Symbol.Equals(Symbol, StringComparison.OrdinalIgnoreCase));

        _logger.LogDebug("Config for {Symbol} found: {Config}", Symbol, config);

        if (config is null)
        {
            _logger.LogError("Currency config for {Symbol} not found.", Symbol);
            throw new InvalidOperationException($"Currency config for {Symbol} not found.");
        }

        return config;
    }
}