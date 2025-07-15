using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.Models.Consts;

public static class AvailableCurrencies
{
     public static readonly IReadOnlyList<AvailableCurrency> Currencies =
          new List<AvailableCurrency>
          {
            new() { Symbol = "BTC", Name = "Bitcoin", Network = "Main" },
          }.AsReadOnly();
}