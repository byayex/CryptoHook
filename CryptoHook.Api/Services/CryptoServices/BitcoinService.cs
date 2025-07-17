using System.Numerics;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using NBitcoin;

namespace CryptoHook.Api.Services.CryptoServices;

public class BitcoinService : ICryptoService
{
    private readonly ExtPubKey _extPubKey;
    private readonly Network _network;
    private readonly ILogger<BitcoinService> _logger;
    private readonly ICryptoDataProvider _dataProvider;
    public CurrencyConfig CurrencyConfig { get; }
    public string Symbol => "BTC";

    // BIP84 derivation path for native SegWit addresses: m/0/index
    private const string DerivationPathFormat = "0/{0}";

    public BitcoinService(CurrencyConfig currencyConfig, ILogger<BitcoinService> logger, ICryptoDataProvider dataProvider)
    {
        _logger = logger;
        _dataProvider = dataProvider;

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

        var transactions = await _dataProvider.GetTransactionsAsync(request.ReceivingAddress, request.Network);

        var paymentResult = new PaymentRequest
        {
            AmountPaid = 0,
            ConfirmationCount = 0,
            Status = request.Status,
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

        var transaction = transactions.First();

        paymentResult.TransactionId = transaction.TransactionId;
        paymentResult.AmountPaid = transaction.AmountPaid;
        paymentResult.ConfirmationCount = transaction.Confirmations;

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
}