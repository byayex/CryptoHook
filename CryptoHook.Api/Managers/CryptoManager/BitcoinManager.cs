using System.Numerics;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using NBitcoin;

namespace CryptoHook.Api.Managers.CryptoManager;

public class BitcoinManager : ICryptoManager
{
    private readonly ExtPubKey _extPubKey;
    private readonly Network _network;
    private readonly ILogger<BitcoinManager> _logger;
    private readonly HttpClient _httpClient;
    public CurrencyConfig CurrencyConfig { get; }
    public string Symbol => "BTC";

    // BIP84 derivation path for native SegWit addresses: m/0/index
    private const string DerivationPathFormat = "0/{0}";

    public BitcoinManager(ConfigManager configManager, ILogger<BitcoinManager> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();

        _logger.LogInformation("Initializing BitcoinManager for {Symbol}", Symbol);

        var config = configManager.GetCurrencyConfig(Symbol);
        _logger.LogDebug("Retrieved config for {Symbol}: Network={Network}", Symbol, config.Network);

        var network = Network.GetNetwork(config.Network);

        if (network is null)
        {
            _logger.LogError("Network for {Symbol} is not configured properly. Network value: {NetworkValue}", Symbol, config.Network);
            throw new InvalidOperationException($"Network for {Symbol} is not configured properly.");
        }

        _network = network;
        CurrencyConfig = config;

        try
        {
            _extPubKey = ExtPubKey.Parse(config.ExtPubKey, _network);
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

    public async Task<PaymentCheckResult> CheckTransactionStatus(PaymentRequest request)
    {
        _logger.LogInformation("Checking transaction status for {Symbol} at address {Address}", Symbol, request.ReceivingAddress);

        var neededConfirmations = CurrencyConfig.GetConfirmationsNeeded(request.ExpectedAmount);
        var transactions = await GetTransactionsAsync(request.ReceivingAddress);

        var paymentCheckResult = new PaymentCheckResult
        {
            AmountDetected = 0,
            Confirmations = 0,
            Status = PaymentStatusEnum.Pending
        };

        if (transactions.Count > 1)
        {
            _logger.LogWarning("Multiple transactions found for {Symbol} at {Address}.", Symbol, request.ReceivingAddress);
            paymentCheckResult.Status = PaymentStatusEnum.MultipleTransactions;
            return paymentCheckResult;
        }

        if (transactions.Count <= 0 && request.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogInformation("Payment request for {Symbol} at {Address} has expired. | Timeout (in minutes): {Timeout}", Symbol, request.ReceivingAddress, CurrencyConfig.InitialPaymentTimeout);
            paymentCheckResult.Status = PaymentStatusEnum.Expired;
            return paymentCheckResult;
        }

        if (transactions.Count <= 0)
        {
            _logger.LogInformation("No payment detected for {Symbol} at {Address}", Symbol, request.ReceivingAddress);
            paymentCheckResult.Status = PaymentStatusEnum.Pending;
            return paymentCheckResult;
        }

        var transaction = transactions[0];

        paymentCheckResult.TransactionId = transaction.GetHash().ToString();
        paymentCheckResult.AmountDetected = (BigInteger)transaction.Outputs
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

        paymentCheckResult.Confirmations = await GetConfirmationsAsync(paymentCheckResult.TransactionId);

        if (paymentCheckResult.AmountDetected < request.ExpectedAmount)
        {
            _logger.LogWarning("Payment for {Symbol} at {Address} is below expected amount. " +
                                   "Expected: {Expected}, Detected: {Detected}",
                Symbol, request.ReceivingAddress, request.ExpectedAmount, paymentCheckResult.AmountDetected);
            paymentCheckResult.Status = PaymentStatusEnum.Underpaid;
            return paymentCheckResult;
        }

        if (paymentCheckResult.AmountDetected > request.ExpectedAmount)
        {
            _logger.LogWarning("Payment for {Symbol} at {Address} is over expected amount. " +
                                   "Expected: {Expected}, Detected: {Detected}",
                Symbol, request.ReceivingAddress, request.ExpectedAmount, paymentCheckResult.AmountDetected);
            paymentCheckResult.Status = PaymentStatusEnum.Overpaid;
            return paymentCheckResult;
        }

        if (paymentCheckResult.Confirmations < neededConfirmations)
        {
            _logger.LogInformation("Transaction for {Symbol} at {Address} has not enough confirmations. " +
                                   "Needed: {Needed}, Current: {Current}",
                Symbol, request.ReceivingAddress, neededConfirmations, paymentCheckResult.Confirmations);
            paymentCheckResult.Status = PaymentStatusEnum.Paid;
            return paymentCheckResult;
        }

        _logger.LogInformation("Transaction for {Symbol} at {Address} is fully paid with sufficient confirmations.",
            Symbol, request.ReceivingAddress);
        paymentCheckResult.Status = PaymentStatusEnum.Confirmed;
        return paymentCheckResult;
    }

    private async Task<List<Transaction>> GetTransactionsAsync(string address)
    {
        // Placeholder for actual implementation to fetch transactions from a Bitcoin node or API
        // This should return a list of transactions associated with the given address
        _logger.LogDebug("Fetching transactions for address {Address} in {Symbol}", address, Symbol);
        return await Task.FromResult(new List<Transaction>());
    }

    private async Task<uint> GetConfirmationsAsync(string txId)
    {
        // Placeholder for actual implementation to get the number of confirmations for a transaction
        // This should return the number of confirmations for the given transaction ID
        _logger.LogDebug("Fetching confirmations for transaction {TxId} in {Symbol}", txId, Symbol);
        return await Task.FromResult(0u);
    }
}