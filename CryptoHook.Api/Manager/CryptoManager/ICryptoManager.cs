using CryptoHook.Api.Models.Config;
using NBitcoin;

namespace CryptoHook.Api.Manager.CryptoManager;

public interface ICryptoManager
{
    BitcoinAddress GetAddressAtIndex(uint index);
    Task<bool> IsPaymentConfirmed(string address, ulong paymentAmount);
    string Symbol { get; }
    CurrencyConfig CurrencyConfig { get; }
}