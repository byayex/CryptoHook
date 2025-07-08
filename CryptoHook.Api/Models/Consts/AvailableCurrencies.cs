namespace CryptoHook.Api.Models.Consts;

public static class AvailableCurrencies
{
    public static readonly IReadOnlyDictionary<string, string> Currencies =
       new Dictionary<string, string>
       {
            { "BTC", "Bitcoin" },
            { "XMR", "Monero" },
       }.AsReadOnly();
}