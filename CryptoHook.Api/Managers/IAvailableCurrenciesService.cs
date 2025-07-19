using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.Managers;

public interface IAvailableCurrenciesService
{
    IReadOnlyList<AvailableCurrency> GetAvailableCurrencies();
}
