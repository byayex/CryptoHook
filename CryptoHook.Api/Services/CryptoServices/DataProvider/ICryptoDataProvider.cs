using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Payments;

namespace CryptoHook.Api.Services.CryptoServices.DataProvider;

public interface ICryptoDataProvider
{
    /// <summary>
    /// Fetches informations about the transactions associated with a specific address on the blockchain.
    /// Limits the number of transactions per default to 2. (more is not needed because we only accept one transaction per payment request/address)
    /// </summary>
    Task<List<PaymentTransaction>> GetTransactionsAsync(string address, uint limit = 2);
}