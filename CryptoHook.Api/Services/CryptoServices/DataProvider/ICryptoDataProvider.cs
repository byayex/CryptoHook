using CryptoHook.Api.Models.Payments;

namespace CryptoHook.Api.Services.CryptoServices.DataProvider;

public interface ICryptoDataProvider
{
    /// <summary>
    /// Returns every inbound transaction the explorer reports for the address, oldest first.
    /// </summary>
    /// <remarks>
    /// All of them, not a sample. A payer or an exchange can split a payment across several
    /// transactions, and the caller sums them to decide whether the request is covered, so a
    /// truncated list produces a wrong balance rather than a slightly stale one. Implementations
    /// page through the explorer's listings and only stop at a cap far above what a single-use
    /// receiving address can plausibly accumulate, which they log when they hit it.
    /// </remarks>
    Task<List<PaymentTransaction>> GetInboundTransactionsAsync(string address, CancellationToken cancellationToken = default);
}
