using System.Numerics;
using CryptoHook.Api.Managers;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using NBitcoin;

namespace CryptoHook.Api.Services.CryptoServices;

public class BitcoinService : ICryptoService
{
    private readonly ExtPubKey _extPubKey;
    private readonly Network _network;
    private readonly ILogger<BitcoinService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    public CurrencyConfig CurrencyConfig { get; }
    public string Symbol => "BTC";

    // BIP84 derivation path for native SegWit addresses: m/0/index
    private const string DerivationPathFormat = "0/{0}";

    public BitcoinService(CurrencyConfig currencyConfig, ILogger<BitcoinService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        _logger.LogInformation("Initializing BitcoinManager for {Symbol}", Symbol);

        var network = Network.GetNetwork(currencyConfig.Network);

        if (network is null)
        {
            _logger.LogError("Network for {Symbol} is not configured properly. Network value: {NetworkValue}", Symbol, currencyConfig.Network);
            throw new InvalidOperationException($"Network for {Symbol} is not configured properly.");
        }

        _network = network;
        CurrencyConfig = currencyConfig;

        try
        {
            _extPubKey = ExtPubKey.Parse(currencyConfig.ExtPubKey, _network);
            _logger.LogInformation("Successfully initialized BitcoinManager for {Symbol} on {Network}", Symbol, _network.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse ExtPubKey for {Symbol}", Symbol);
            throw;
        }
    }

    public string GetAddressAtIndex(uint index)
    {
        _logger.LogDebug("Generating address for {Symbol} at index {Index}", Symbol, index);

        try
        {
            // Derives receiving addresses using BIP84 path (m/0/index)
            var derivationPath = new KeyPath(string.Format(DerivationPathFormat, index));
            var childPubKey = _extPubKey.Derive(derivationPath).PubKey;
            var address = childPubKey.GetAddress(ScriptPubKeyType.Segwit, _network);

            _logger.LogDebug("Successfully generated address {Address} for {Symbol} at index {Index}",
                address.ToString(), Symbol, index);

            return address.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate address for {Symbol} at index {Index}", Symbol, index);
            throw;
        }
    }

    public async Task<PaymentRequest> CheckTransactionStatus(PaymentRequest request)
    {
        _logger.LogInformation("Checking transaction status for {Symbol} at address {Address}", Symbol, request.ReceivingAddress);

        var transactions = await GetTransactionsAsync(request.ReceivingAddress);

        var paymentResult = new PaymentRequest
        {
            AmountPaid = 0,
            ConfirmationCount = 0,
            Status = PaymentStatusEnum.Pending,
            AmountExpected = request.AmountExpected,
            ConfirmationNeeded = request.ConfirmationNeeded,
            ReceivingAddress = request.ReceivingAddress,
            CurrencySymbol = request.CurrencySymbol,
            Network = request.Network,
            CreatedAt = request.CreatedAt,
            ExpiresAt = request.ExpiresAt,
            UpdatedAt = DateTime.UtcNow,
        };

        if (transactions.Count > 1)
        {
            _logger.LogWarning("Multiple transactions found for {Symbol} at {Address}.", Symbol, request.ReceivingAddress);
            paymentResult.Status = PaymentStatusEnum.MultipleTransactions;
            paymentResult.TransactionId = "Multiple";
            return paymentResult;
        }

        if (transactions.Count <= 0 && request.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogInformation("Payment request for {Symbol} at {Address} has expired. | Timeout (in minutes): {Timeout}", Symbol, request.ReceivingAddress, CurrencyConfig.InitialPaymentTimeout);
            paymentResult.Status = PaymentStatusEnum.Expired;
            return paymentResult;
        }

        if (transactions.Count <= 0)
        {
            _logger.LogInformation("No payment detected for {Symbol} at {Address}", Symbol, request.ReceivingAddress);
            paymentResult.Status = PaymentStatusEnum.Pending;
            return paymentResult;
        }

        var transaction = transactions[0];

        paymentResult.TransactionId = transaction.GetHash().ToString();
        paymentResult.AmountPaid = (BigInteger)transaction.Outputs
            .Where(o =>
            {
                if (o.ScriptPubKey is null)
                {
                    return false;
                }

                var destinationAddress = o.ScriptPubKey.GetDestinationAddress(_network);

                if (destinationAddress is null)
                {
                    return false;
                }

                return destinationAddress.ToString() == request.ReceivingAddress;
            })
            .Sum(o => o.Value.ToUnit(MoneyUnit.Satoshi));

        paymentResult.ConfirmationCount = await GetConfirmationsAsync(paymentResult.TransactionId);

        if (paymentResult.AmountPaid < request.AmountExpected)
        {
            _logger.LogWarning("Payment for {Symbol} at {Address} is below expected amount. " +
                                   "Expected: {Expected}, Detected: {Detected}",
                Symbol, request.ReceivingAddress, request.AmountExpected, paymentResult.AmountPaid);
            paymentResult.Status = PaymentStatusEnum.Underpaid;
            return paymentResult;
        }

        if (paymentResult.AmountPaid > request.AmountExpected)
        {
            _logger.LogWarning("Payment for {Symbol} at {Address} is over expected amount. " +
                                   "Expected: {Expected}, Detected: {Detected}",
                Symbol, request.ReceivingAddress, request.AmountExpected, paymentResult.AmountPaid);
            paymentResult.Status = PaymentStatusEnum.Overpaid;
            return paymentResult;
        }

        // Using the confirmation count which got set during the payment request creation 
        // (the config that was used at the point of creation counts)
        var neededConfirmations = request.ConfirmationNeeded;

        if (paymentResult.ConfirmationCount < neededConfirmations)
        {
            _logger.LogInformation("Transaction for {Symbol} at {Address} has not enough confirmations. " +
                                   "Needed: {Needed}, Current: {Current}",
                Symbol, request.ReceivingAddress, neededConfirmations, paymentResult.ConfirmationCount);
            paymentResult.Status = PaymentStatusEnum.Paid;
            return paymentResult;
        }

        _logger.LogInformation("Transaction for {Symbol} at {Address} is fully paid with sufficient confirmations.",
            Symbol, request.ReceivingAddress);
        paymentResult.Status = PaymentStatusEnum.Confirmed;
        return paymentResult;
    }

    private async Task<List<Transaction>> GetTransactionsAsync(string address)
    {
        _logger.LogDebug("Fetching transactions for address {Address} in {Symbol}", address, Symbol);

        var apiBaseUrl = _network == Network.Main ? "https://blockstream.info/api" : "https://blockstream.info/testnet/api";
        var txsUrl = $"{apiBaseUrl}/address/{address}/txs";

        try
        {
            var response = await _httpClientFactory.CreateClient().GetAsync(txsUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Could not fetch transactions for {Address}. Status: {StatusCode}", address, response.StatusCode);
                return new List<Transaction>();
            }

            var txsJson = await response.Content.ReadAsStringAsync();
            using var jsonDoc = System.Text.Json.JsonDocument.Parse(txsJson);
            var txIds = jsonDoc.RootElement.EnumerateArray()
                .Select(tx => tx.GetProperty("txid").GetString())
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();

            if (txIds.Count == 0)
            {
                return [];
            }

            var transactions = new List<Transaction>();
            foreach (var txId in txIds)
            {
                try
                {
                    var txHex = await _httpClientFactory.CreateClient().GetStringAsync($"{apiBaseUrl}/tx/{txId}/hex");
                    transactions.Add(Transaction.Parse(txHex, _network));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process transaction {TxId} for address {Address}", txId, address);
                }
            }

            return transactions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while fetching transactions for address {Address}", address);
            return [];
        }
    }

    private async Task<uint> GetConfirmationsAsync(string txId)
    {
        _logger.LogDebug("Fetching confirmations for transaction {TxId} in {Symbol}", txId, Symbol);
        var apiBaseUrl = _network == Network.Main ? "https://blockstream.info/api" : "https://blockstream.info/testnet/api";

        try
        {
            var txStatusUrl = $"{apiBaseUrl}/tx/{txId}/status";
            var response = await _httpClientFactory.CreateClient().GetAsync(txStatusUrl);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Could not fetch transaction status for {TxId}. Status: {StatusCode}", txId, response.StatusCode);
                return 0;
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while fetching confirmations for transaction {TxId}", txId);
            return 0;
        }
    }
}