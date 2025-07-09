using CryptoHook.Api.Models.Config;

namespace CryptoHook.Api.Manager;

public class ConfigManager(CurrencyConfigList currencyConfigList, ILogger<ConfigManager> logger)
{
    private readonly ILogger<ConfigManager> _logger = logger;
    private readonly CurrencyConfigList _currencyConfigList = currencyConfigList;

    public CurrencyConfig GetCurrencyConfig(string Symbol)
    {
        _logger.LogDebug("Retrieving config for currency: {Symbol}", Symbol);

        var config = _currencyConfigList.CurrencyConfigs
            .FirstOrDefault(c => c.Symbol.Equals(Symbol, StringComparison.OrdinalIgnoreCase));

        if (config is null)
        {
            _logger.LogError("Currency config for {Symbol} not found.", Symbol);
            throw new InvalidOperationException("CurrencyConfigs section is not configured properly.");
        }

        config.Confirmations = config.Confirmations.OrderBy(c => c.Amount).ToList();

        return config;
    }
}