using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;

namespace CryptoHook.Api.Services.CryptoServices.Factory;

public interface ICryptoServiceFactory
{
    ICryptoService GetService(AvailableCurrency currency);
    ICryptoService GetService(string symbol, string network);
}
