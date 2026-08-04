using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using CryptoHook.Api.Managers;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Services.CryptoServices.Factory;

public sealed class CryptoServiceFactory(
    ConfigManager configManager,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    IOptions<PaymentCheckConfig> paymentCheckConfig) : ICryptoServiceFactory, IDisposable
{
    /// <remarks>
    /// Keyed by symbol and network rather than by <see cref="AvailableCurrency"/>, whose properties
    /// are settable and whose hash code is derived from them: mutating an instance already used as
    /// a key makes its entry unreachable and leaks it.
    ///
    /// The values are lazy so that concurrent first-time callers cannot both run the factory.
    /// ConcurrentDictionary does not serialise its value factory, and a second data provider is not
    /// a harmless duplicate: each owns a rate limiter, the two do not coordinate, and the explorer
    /// then sees twice the configured request rate.
    /// </remarks>
    private readonly ConcurrentDictionary<string, Lazy<ICryptoService>> _services = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<ICryptoDataProvider>> _dataProviders = new(StringComparer.Ordinal);

    private readonly ConfigManager _configManager = configManager;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Injected by the container, which owns its lifetime; disposing it here would tear down logging for the whole application.")]
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly PaymentCheckConfig _paymentCheckConfig = paymentCheckConfig.Value;
    private readonly ILogger<CryptoServiceFactory> _logger = loggerFactory.CreateLogger<CryptoServiceFactory>();

    public ICryptoService? GetService(AvailableCurrency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        _logger.LogDebug("Getting service for currency: {Symbol} on network: {Network}", currency.Symbol, currency.Network);
        return GetCryptoService(currency);
    }

    public ICryptoService? GetService(string symbol, string network)
    {
        _logger.LogDebug("Getting service for symbol: {Symbol} on network: {Network}", symbol, network);

        if (symbol is null || network is null)
        {
            return null;
        }

        var currency = _configManager.GetUsableCurrencies()
            .FirstOrDefault(c => c.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                                 c.Network.Equals(network, StringComparison.OrdinalIgnoreCase));

        if (currency == null)
        {
            _logger.LogWarning("No service available for currency: {Symbol} on network: {Network}", symbol, network);
            return null;
        }

        return GetCryptoService(currency);
    }

    /// <summary>
    /// Returns <see langword="null"/> for currencies that are not enabled, so that callers can
    /// answer with a 400 rather than a 500 and the payment worker can skip a single bad row instead
    /// of aborting a whole cycle.
    /// </summary>
    private ICryptoService? GetCryptoService(AvailableCurrency currency)
    {
        if (!_configManager.GetUsableCurrencies().Any(c => c.Equals(currency)))
        {
            _logger.LogWarning("No service available for currency: {Symbol} on network: {Network}", currency.Symbol, currency.Network);
            return null;
        }

        var key = CacheKey(currency);

        return _services.GetOrAdd(key, _ => new Lazy<ICryptoService>(
            () => CreateService(currency),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private ICryptoService CreateService(AvailableCurrency currency)
    {
        _logger.LogInformation("Creating crypto service for {Symbol} on {Network}", currency.Symbol, currency.Network);

        var config = _configManager.GetCurrencyConfig(currency.Symbol, currency.Network);
        var dataProvider = GetDataProvider(currency);

        return currency.Symbol.ToUpperInvariant() switch
        {
            "BTC" => new BitcoinService(
                config,
                _loggerFactory.CreateLogger<BitcoinService>(),
                dataProvider,
                _paymentCheckConfig),

            "ETH" => new EthereumService(
                config,
                _loggerFactory.CreateLogger<EthereumService>(),
                dataProvider,
                _paymentCheckConfig),

            _ => throw new NotSupportedException($"No service implemented for currency: {currency.Symbol}")
        };
    }

    private ICryptoDataProvider GetDataProvider(AvailableCurrency currency)
    {
        return _dataProviders.GetOrAdd(CacheKey(currency), _ => new Lazy<ICryptoDataProvider>(
            () => CreateDataProvider(currency),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private ICryptoDataProvider CreateDataProvider(AvailableCurrency currency)
    {
        _logger.LogInformation("Creating data provider for {Symbol} on {Network}", currency.Symbol, currency.Network);

        var config = _configManager.GetCurrencyConfig(currency.Symbol, currency.Network);

        return currency.Symbol.ToUpperInvariant() switch
        {
            "BTC" => new BitcoinDataProvider(
                _loggerFactory.CreateLogger<BitcoinDataProvider>(),
                _httpClientFactory,
                config),

            "ETH" => new EthereumDataProvider(
                _loggerFactory.CreateLogger<EthereumDataProvider>(),
                _httpClientFactory,
                config),

            _ => throw new NotSupportedException($"No data provider implemented for currency: {currency.Symbol}")
        };
    }

    private static string CacheKey(AvailableCurrency currency) =>
        $"{currency.Symbol.ToUpperInvariant()}|{currency.Network.ToUpperInvariant()}";

    /// <remarks>
    /// The data providers own rate limiters, which own semaphores.
    /// </remarks>
    public void Dispose()
    {
        foreach (var provider in _dataProviders.Values)
        {
            if (provider.IsValueCreated && provider.Value is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _dataProviders.Clear();
        _services.Clear();
    }
}
