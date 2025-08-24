using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.Services.CryptoServices.Factory;

public interface ICryptoServiceFactory
{
    ICryptoService GetService(AvailableCurrency currency);
    ICryptoService GetService(string symbol, string network);
}
