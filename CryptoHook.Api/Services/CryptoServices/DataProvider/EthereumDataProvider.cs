using System.Globalization;
using System.Numerics;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Payments;

namespace CryptoHook.Api.Services.CryptoServices.DataProvider;

public class EthereumDataProvider(ILogger<EthereumDataProvider> logger, IHttpClientFactory httpClientFactory, CurrencyConfig currencyConfig) : ICryptoDataProvider
{
    private readonly ILogger<EthereumDataProvider> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    public CurrencyConfig CurrencyConfig { get; } = currencyConfig;

    public async Task<List<PaymentTransaction>> GetTransactionsAsync(string address, uint limit = 2)
    {
        _logger.LogDebug("Fetching payment transactions for address {Address} with limit {Limit}", address, limit);

        if (string.IsNullOrWhiteSpace(CurrencyConfig.Network))
        {
            _logger.LogError("Invalid network specified for Symbol {Symbol}", CurrencyConfig.Symbol);
            throw new ArgumentException("Invalid network configuration");
        }

        List<EthereumTransaction> ethereumTransactions;
        try
        {
            ethereumTransactions = await GetTransactionsInternalAsync(address, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching transactions for address {Address}", address);
            throw;
        }

        var paymentTransactions = new List<PaymentTransaction>();
        foreach (var tx in ethereumTransactions)
        {
            if (paymentTransactions.Count >= limit)
            {
                break;
            }

            _logger.LogDebug("Transaction {TxId} for address {Address} has {Confirmations} confirmations and paid {AmountPaid} wei",
                tx.Hash, address, tx.Confirmations, tx.Value);

            if (tx.Value > 0)
            {
                paymentTransactions.Add(new PaymentTransaction
                {
                    TransactionId = tx.Hash,
                    AmountPaid = tx.Value,
                    Confirmations = tx.Confirmations
                });
            }
        }
        return paymentTransactions;
    }

    private async Task<List<EthereumTransaction>> GetTransactionsInternalAsync(string address, uint limit = 2)
    {
        _logger.LogDebug("Fetching transactions for address {Address} in {Symbol}", address, CurrencyConfig.Symbol);

        // Determine API endpoint based on network
        var apiBaseUrl = CurrencyConfig.Network.Equals("Main", StringComparison.OrdinalIgnoreCase) || 
                         CurrencyConfig.Network.Equals("Mainnet", StringComparison.OrdinalIgnoreCase)
            ? "https://api.etherscan.io/api"
            : "https://api-sepolia.etherscan.io/api";

        using var httpClient = _httpClientFactory.CreateClient();
        
        // Get transactions for the address
        var txListUrl = new Uri($"{apiBaseUrl}?module=account&action=txlist&address={address}&startblock=0&endblock=99999999&page=1&offset={limit}&sort=desc");
        
        var response = await httpClient.GetAsync(txListUrl);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Could not fetch transactions for {Address}. Status: {StatusCode}", address, response.StatusCode);
            throw new HttpRequestException($"Could not fetch transactions for {address}. Status: {response.StatusCode}");
        }

        var txsJson = await response.Content.ReadAsStringAsync();
        using var jsonDoc = System.Text.Json.JsonDocument.Parse(txsJson);
        
        var status = jsonDoc.RootElement.GetProperty("status").GetString();
        if (status != "1")
        {
            _logger.LogDebug("No transactions found for address {Address}", address);
            return [];
        }

        var transactions = new List<EthereumTransaction>();
        var currentBlockNumber = await GetCurrentBlockNumberAsync(httpClient, apiBaseUrl);

        foreach (var tx in jsonDoc.RootElement.GetProperty("result").EnumerateArray())
        {
            // Only include transactions where this address is the recipient
            var to = tx.GetProperty("to").GetString();
            if (!string.Equals(to, address, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var txHash = tx.GetProperty("hash").GetString() ?? string.Empty;
            var valueStr = tx.GetProperty("value").GetString() ?? "0";
            var blockNumberStr = tx.GetProperty("blockNumber").GetString() ?? "0";
            
            BigInteger value = BigInteger.Parse(valueStr, CultureInfo.InvariantCulture);
            var blockNumber = ulong.Parse(blockNumberStr, CultureInfo.InvariantCulture);
            
            uint confirmations = 0;
            if (blockNumber > 0 && currentBlockNumber >= blockNumber)
            {
                confirmations = (uint)(currentBlockNumber - blockNumber + 1);
            }

            transactions.Add(new EthereumTransaction
            {
                Hash = txHash,
                Value = value,
                Confirmations = confirmations
            });

            if (transactions.Count >= limit)
            {
                break;
            }
        }

        return transactions;
    }

    private async Task<ulong> GetCurrentBlockNumberAsync(HttpClient httpClient, string apiBaseUrl)
    {
        _logger.LogDebug("Fetching current block number for {Symbol}", CurrencyConfig.Symbol);
        
        var blockNumberUrl = new Uri($"{apiBaseUrl}?module=proxy&action=eth_blockNumber");
        var response = await httpClient.GetAsync(blockNumberUrl);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Could not fetch current block number. Status: {StatusCode}", response.StatusCode);
            throw new HttpRequestException($"Could not fetch current block number. Status: {response.StatusCode}");
        }

        var blockNumberJson = await response.Content.ReadAsStringAsync();
        using var jsonDoc = System.Text.Json.JsonDocument.Parse(blockNumberJson);
        
        var hexBlockNumber = jsonDoc.RootElement.GetProperty("result").GetString() ?? "0x0";
        
        // Remove "0x" prefix and parse hex to decimal
        if (hexBlockNumber.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hexBlockNumber = hexBlockNumber[2..];
        }
        
        return ulong.Parse(hexBlockNumber, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private class EthereumTransaction
    {
        public required string Hash { get; set; }
        public BigInteger Value { get; set; }
        public uint Confirmations { get; set; }
    }
}
