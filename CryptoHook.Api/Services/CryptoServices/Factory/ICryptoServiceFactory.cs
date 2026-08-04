using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.Services.CryptoServices.Factory;

public interface ICryptoServiceFactory
{
    /// <returns><see langword="null"/> when the currency is not enabled in the configuration.</returns>
    ICryptoService? GetService(AvailableCurrency currency);

    /// <returns><see langword="null"/> when that combination is not enabled in the configuration.</returns>
    ICryptoService? GetService(string symbol, string network);
}
