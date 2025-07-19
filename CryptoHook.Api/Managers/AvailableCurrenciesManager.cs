using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;

namespace CryptoHook.Api.Managers;

public class AvailableCurrenciesManager : IAvailableCurrenciesManager
{
    public IReadOnlyList<AvailableCurrency> GetAvailableCurrencies()
    {
        return AvailableCurrencies.Currencies;
    }
}
