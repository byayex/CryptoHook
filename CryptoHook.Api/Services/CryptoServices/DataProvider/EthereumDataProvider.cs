using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Payments;
using System.Numerics;
using System.Text.Json;

namespace CryptoHook.Api.Services.CryptoServices.DataProvider;

public class EthereumDataProvider(ILogger<EthereumDataProvider> logger, IHttpClientFactory httpClientFactory, CurrencyConfig currencyConfig) : ICryptoDataProvider
{
    private readonly ILogger<EthereumDataProvider> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    public CurrencyConfig CurrencyConfig { get; } = currencyConfig;

    public async Task<List<PaymentTransaction>> GetTransactionsAsync(string address, uint limit = 2)
    {
        _logger.LogDebug("Fetching payment transactions for address {Address} with limit {Limit}", address, limit);

        // Validate Ethereum address format
        if (!IsValidEthereumAddress(address))
        {
            _logger.LogError("Invalid Ethereum address format: {Address}", address);
            throw new ArgumentException($"Invalid Ethereum address format: {address}", nameof(address));
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

            uint confirmations;
            try
            {
                confirmations = await GetConfirmationsAsync(tx.Hash, tx.BlockNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching confirmations for transaction {TxId}", tx.Hash);
                throw;
            }

            // For Ethereum, we need to check if the transaction is TO our address and has a value > 0
            if (string.Equals(tx.To, address, StringComparison.OrdinalIgnoreCase) && tx.Value > 0)
            {
                _logger.LogDebug("Transaction {TxId} for address {Address} has {Confirmations} confirmations and paid {AmountPaid} wei",
                    tx.Hash, address, confirmations, tx.Value);

                paymentTransactions.Add(new PaymentTransaction
                {
                    TransactionId = tx.Hash,
                    AmountPaid = tx.Value,
                    Confirmations = confirmations
                });
            }
        }
        return paymentTransactions;
    }

    private static bool IsValidEthereumAddress(string address)
    {
        // Basic Ethereum address validation (starts with 0x and is 42 characters long)
        return !string.IsNullOrEmpty(address) &&
               address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
               address.Length == 42 &&
               address[2..].All(char.IsAsciiHexDigit);
    }

    private async Task<List<EthereumTransaction>> GetTransactionsInternalAsync(string address, uint limit = 2)
    {
        _logger.LogDebug("Fetching transactions for address {Address} in {Symbol}", address, CurrencyConfig.Symbol);

        // Determine API base URL based on network configuration
        var apiBaseUrl = GetEthereumApiBaseUrl();
        var txsUrl = new Uri($"{apiBaseUrl}/api?module=account&action=txlist&address={address}&startblock=0&endblock=9999999999&sort=desc&page=1&offset={limit}");

        using var httpClient = _httpClientFactory.CreateClient();

        var response = await httpClient.GetAsync(txsUrl);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Could not fetch transactions for {Address}. Status: {StatusCode}", address, response.StatusCode);
            throw new HttpRequestException($"Could not fetch transactions for {address}. Status: {response.StatusCode}");
        }

        var txsJson = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(txsJson);

        var result = jsonDoc.RootElement.GetProperty("result");
        if (result.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("No transactions found for address {Address}", address);
            return [];
        }

        var transactions = new List<EthereumTransaction>();
        foreach (var tx in result.EnumerateArray())
        {
            if (transactions.Count >= limit)
            {
                break;
            }

            var hash = tx.GetProperty("hash").GetString();
            var to = tx.GetProperty("to").GetString();
            var valueString = tx.GetProperty("value").GetString();
            var blockNumberString = tx.GetProperty("blockNumber").GetString();

            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(to) ||
                string.IsNullOrEmpty(valueString) || string.IsNullOrEmpty(blockNumberString))
            {
                continue;
            }

            if (BigInteger.TryParse(valueString, out var value) &&
                uint.TryParse(blockNumberString, out var blockNumber))
            {
                transactions.Add(new EthereumTransaction
                {
                    Hash = hash,
                    To = to,
                    Value = value,
                    BlockNumber = blockNumber
                });
            }
        }

        return transactions;
    }

    private async Task<uint> GetConfirmationsAsync(string txHash, uint blockNumber)
    {
        _logger.LogDebug("Fetching confirmations for transaction {TxHash} in {Symbol}", txHash, CurrencyConfig.Symbol);

        var apiBaseUrl = GetEthereumApiBaseUrl();
        var blockHeightUrl = new Uri($"{apiBaseUrl}/api?module=proxy&action=eth_blockNumber");

        using var httpClient = _httpClientFactory.CreateClient();
        var response = await httpClient.GetAsync(blockHeightUrl);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Could not fetch current block number. Status: {StatusCode}", response.StatusCode);
            throw new HttpRequestException($"Could not fetch current block number. Status: {response.StatusCode}");
        }

        var blockHeightJson = await response.Content.ReadAsStringAsync();
        using var blockDoc = JsonDocument.Parse(blockHeightJson);

        var currentBlockHex = blockDoc.RootElement.GetProperty("result").GetString();
        if (string.IsNullOrEmpty(currentBlockHex))
        {
            _logger.LogError("Invalid block number response for transaction {TxHash}", txHash);
            throw new InvalidOperationException($"Invalid block number response for transaction {txHash}");
        }

        // Convert hex block number to decimal
        var currentBlockNumber = Convert.ToUInt32(currentBlockHex, 16);

        // Calculate confirmations
        return currentBlockNumber > blockNumber ? currentBlockNumber - blockNumber + 1 : 0;
    }

    private string GetEthereumApiBaseUrl()
    {
        // Use different API endpoints based on network configuration
        return CurrencyConfig.Network?.ToUpperInvariant() switch
        {
            "MAIN" or "MAINNET" => "https://api.etherscan.io",
            "SEPOLIA" => "https://api-sepolia.etherscan.io",
            "GOERLI" => "https://api-goerli.etherscan.io",
            _ => "https://api.etherscan.io" // Default to mainnet
        };
    }

    private class EthereumTransaction
    {
        public required string Hash { get; set; }
        public required string To { get; set; }
        public required BigInteger Value { get; set; }
        public required uint BlockNumber { get; set; }
    }
}