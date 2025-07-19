using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;

namespace CryptoHook.Api.Managers;

public class AvailableCurrenciesService : IAvailableCurrenciesService
{
    public IReadOnlyList<AvailableCurrency> GetAvailableCurrencies()
    {
        return AvailableCurrencies.Currencies;
    }
}
