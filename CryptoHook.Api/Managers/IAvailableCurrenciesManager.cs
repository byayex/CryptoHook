using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.Managers;

public interface IAvailableCurrenciesManager
{
    IReadOnlyList<AvailableCurrency> GetAvailableCurrencies();
}
