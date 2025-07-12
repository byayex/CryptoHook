using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Payments;

namespace CryptoHook.Api.Managers.CryptoManager;

public interface ICryptoManager
{
    string GetAddressAtIndex(uint index);
    Task<PaymentCheckResult> CheckTransactionStatus(PaymentRequest request);
    string Symbol { get; }
    CurrencyConfig CurrencyConfig { get; }
}