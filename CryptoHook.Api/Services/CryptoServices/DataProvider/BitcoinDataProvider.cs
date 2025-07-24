using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Payments;
using NBitcoin;
using System.Numerics;

namespace CryptoHook.Api.Services.CryptoServices.DataProvider;

public class BitcoinDataProvider(ILogger<BitcoinDataProvider> logger, IHttpClientFactory httpClientFactory, CurrencyConfig currencyConfig) : ICryptoDataProvider
{
    private readonly ILogger<BitcoinDataProvider> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    public CurrencyConfig CurrencyConfig { get; } = currencyConfig;

    public async Task<List<PaymentTransaction>> GetTransactionsAsync(string address, uint limit = 2)
    {
        _logger.LogDebug("Fetching payment transactions for address {Address} with limit {Limit}", address, limit);

        var _network = Network.GetNetwork(CurrencyConfig.Network);

        if (_network == null)
        {
            _logger.LogError("Invalid network specified: {Network} for Symbol {Symbol}", CurrencyConfig.Network, CurrencyConfig.Symbol);
            throw new ArgumentException($"Invalid network: {CurrencyConfig.Network}", nameof(CurrencyConfig.Network));
        }

        List<Transaction> bitcoinTransactions;
        try
        {
            bitcoinTransactions = await GetTransactionsInternalAsync(address, _network, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching transactions for address {Address}", address);
            throw;
        }

        var paymentTransactions = new List<PaymentTransaction>();
        foreach (var tx in bitcoinTransactions)
        {
            if (paymentTransactions.Count >= limit)
                break;

            var txId = tx.GetHash().ToString();
            uint confirmations;
            try
            {
                confirmations = await GetConfirmationsAsync(txId, _network);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching confirmations for transaction {TxId}", txId);
                throw;
            }

            var amountPaid = (BigInteger)tx.Outputs
                .Where(o =>
                {
                    if (o.ScriptPubKey is null)
                        return false;
                    var destinationAddress = o.ScriptPubKey.GetDestinationAddress(Network.GetNetwork(CurrencyConfig.Network) ?? Network.Main);
                    if (destinationAddress is null)
                        return false;
                    return destinationAddress.ToString() == address;
                })
                .Sum(o => o.Value.ToUnit(MoneyUnit.Satoshi));

            _logger.LogDebug("Transaction {TxId} for address {Address} has {Confirmations} confirmations and paid {AmountPaid} satoshis",
                txId, address, confirmations, amountPaid);

            if (amountPaid > 0)
            {
                paymentTransactions.Add(new PaymentTransaction
                {
                    TransactionId = txId,
                    AmountPaid = amountPaid,
                    Confirmations = confirmations
                });
            }
        }
        return paymentTransactions;
    }

    private async Task<List<Transaction>> GetTransactionsInternalAsync(string address, Network network, uint limit = 2)
    {
        _logger.LogDebug("Fetching transactions for address {Address} in {Symbol}", address, CurrencyConfig.Symbol);

        var apiBaseUrl = network == Network.Main ? "https://blockstream.info/api" : "https://blockstream.info/testnet/api";
        var txsUrl = $"{apiBaseUrl}/address/{address}/txs";

        var response = await _httpClientFactory.CreateClient().GetAsync(txsUrl);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Could not fetch transactions for {Address}. Status: {StatusCode}", address, response.StatusCode);
            throw new HttpRequestException($"Could not fetch transactions for {address}. Status: {response.StatusCode}");
        }

        var txsJson = await response.Content.ReadAsStringAsync();
        using var jsonDoc = System.Text.Json.JsonDocument.Parse(txsJson);
        var txIds = jsonDoc.RootElement.EnumerateArray()
            .Select(tx => tx.GetProperty("txid").GetString())
            .Where(id => !string.IsNullOrEmpty(id))
            .Take((int)limit)
            .ToList();

        if (txIds.Count == 0)
        {
            return [];
        }

        var transactions = new List<Transaction>();
        foreach (var txId in txIds)
        {
            var txHex = await _httpClientFactory.CreateClient().GetStringAsync($"{apiBaseUrl}/tx/{txId}/hex");
            transactions.Add(Transaction.Parse(txHex, network));
        }
        return transactions;
    }

    private async Task<uint> GetConfirmationsAsync(string txId, Network network)
    {
        _logger.LogDebug("Fetching confirmations for transaction {TxId} in {Symbol}", txId, CurrencyConfig.Symbol);
        var apiBaseUrl = network == Network.Main ? "https://blockstream.info/api" : "https://blockstream.info/testnet/api";

        var txStatusUrl = $"{apiBaseUrl}/tx/{txId}/status";
        var response = await _httpClientFactory.CreateClient().GetAsync(txStatusUrl);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Could not fetch transaction status for {TxId}. Status: {StatusCode}", txId, response.StatusCode);
            throw new HttpRequestException($"Could not fetch transaction status for {txId}. Status: {response.StatusCode}");
        }

        var txStatusJson = await response.Content.ReadAsStringAsync();
        using var statusDoc = System.Text.Json.JsonDocument.Parse(txStatusJson);

        if (!statusDoc.RootElement.GetProperty("confirmed").GetBoolean())
        {
            return 0;
        }

        var txBlockHeight = statusDoc.RootElement.GetProperty("block_height").GetUInt32();

        var tipHeightUrl = $"{apiBaseUrl}/blocks/tip/height";
        var currentHeightStr = await _httpClientFactory.CreateClient().GetStringAsync(tipHeightUrl);
        var currentHeight = uint.Parse(currentHeightStr);

        return currentHeight - txBlockHeight + 1;
    }
}