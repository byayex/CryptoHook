using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Payments;

namespace CryptoHook.Api.Services.CryptoServices;

public interface ICryptoService
{
    string GetAddressAtIndex(uint index);

    /// <summary>
    /// Evaluates the request against the chain and returns what it should look like now. The
    /// returned instance is a copy; the caller decides whether to persist it. If the chain data
    /// cannot be read, the request is returned unchanged rather than reported as unpaid.
    /// </summary>
    Task<PaymentRequest> CheckTransactionStatus(PaymentRequest request, CancellationToken cancellationToken = default);

    string Symbol { get; }

    CurrencyConfig CurrencyConfig { get; }
}
