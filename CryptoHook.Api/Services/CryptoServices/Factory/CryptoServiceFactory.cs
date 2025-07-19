using System.Collections.Concurrent;
using CryptoHook.Api.Managers;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Services.CryptoServices.DataProvider;

namespace CryptoHook.Api.Services.CryptoServices.Factory;

public class CryptoServiceFactory(
    ConfigManager configManager,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : ICryptoServiceFactory
{
    private readonly ConcurrentDictionary<AvailableCurrency, ICryptoService> _services = new();
    private readonly ConcurrentDictionary<AvailableCurrency, ICryptoDataProvider> _dataProviders = new();
    private readonly ConfigManager _configManager = configManager;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger<CryptoServiceFactory> _logger = loggerFactory.CreateLogger<CryptoServiceFactory>();

    public ICryptoService GetService(AvailableCurrency currency)
    {
        _logger.LogDebug("Getting service for currency: {Symbol} on network: {Network}", currency.Symbol, currency.Network);
        return GetCryptoService(currency);
    }

    public ICryptoService GetService(string symbol, string network)
    {
        _logger.LogDebug("Getting service for symbol: {Symbol} on network: {Network}", symbol, network);

        var currency = _configManager.GetUsableCurrencies()
            .FirstOrDefault(c => c.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                                 c.Network.Equals(network, StringComparison.OrdinalIgnoreCase));

        if (currency == null)
        {
            _logger.LogError("No service available for currency: {Symbol} on network: {Network}", symbol, network);
            throw new InvalidOperationException($"No service available for currency: {symbol} on network: {network}");
        }

        _logger.LogDebug("Found currency configuration for {Symbol} on {Network}", symbol, network);
        return GetCryptoService(currency);
    }

    private ICryptoService GetCryptoService(AvailableCurrency currency)
    {
        if (!_configManager.GetUsableCurrencies().Any(c => c.Equals(currency)))
        {
            _logger.LogError("No service available for currency: {Symbol} on network: {Network}", currency.Symbol, currency.Network);
            throw new InvalidOperationException($"No service available for currency: {currency}");
        }

        _logger.LogDebug("Checking cache for existing service: {Symbol} on {Network}. Currency hash: {Hash}",
            currency.Symbol, currency.Network, currency.GetHashCode());

        var service = _services.GetOrAdd(currency, c =>
        {
            _logger.LogInformation("Creating new crypto service for currency: {Symbol} on {Network}. Service will be cached with hash: {Hash}",
                c.Symbol, c.Network, c.GetHashCode());

            var config = _configManager.GetCurrencyConfig(c.Symbol, c.Network);
            var dataProvider = GetDataProvider(c);

            var createdService = c.Symbol switch
            {
                "BTC" => new BitcoinService(
                    config,
                    _loggerFactory.CreateLogger<BitcoinService>(),
                    dataProvider),

                _ => throw new NotSupportedException($"No service implemented for currency: {c.Symbol}")
            };

            _logger.LogInformation("Successfully created {ServiceType} for {Symbol} on {Network}",
                createdService.GetType().Name, c.Symbol, c.Network);

            return createdService;
        });

        if (_services.ContainsKey(currency))
        {
            _logger.LogDebug("Reusing existing {ServiceType} for currency: {Symbol} on {Network}",
                service.GetType().Name, currency.Symbol, currency.Network);
        }

        _logger.LogDebug("Returning {ServiceType} for currency: {Symbol} on {Network}",
            service.GetType().Name, currency.Symbol, currency.Network);

        return service;
    }

    public ICryptoDataProvider GetDataProvider(AvailableCurrency currency)
    {
        return _dataProviders.GetOrAdd(currency, _ =>
        {
            _logger.LogInformation("Creating ew data provider for {Symbol} on {Network}", currency.Symbol, currency.Network);

            return currency.Symbol switch
            {
                "BTC" => new BitcoinDataProvider(
                    _loggerFactory.CreateLogger<BitcoinDataProvider>(),
                    _httpClientFactory,
                    _configManager.GetCurrencyConfig(currency.Symbol, currency.Network)),

                _ => throw new NotSupportedException($"No data provider implemented for currency: {currency.Symbol}")
            };
        });
    }
}