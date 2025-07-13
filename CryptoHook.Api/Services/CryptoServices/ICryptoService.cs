using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Payments;

namespace CryptoHook.Api.Services.CryptoServices;

public interface ICryptoService
{
    string GetAddressAtIndex(uint index);
    Task<PaymentCheckResult> CheckTransactionStatus(PaymentRequest request);
    string Symbol { get; }
    CurrencyConfig CurrencyConfig { get; }
}