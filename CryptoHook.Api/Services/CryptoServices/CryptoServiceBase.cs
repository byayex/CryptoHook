using System.Numerics;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services.CryptoServices.DataProvider;

namespace CryptoHook.Api.Services.CryptoServices;

/// <remarks>
/// Shared payment evaluation for all currencies. The rules are currency independent, so they live
/// here once instead of being copied per implementation, where they could drift apart.
/// </remarks>
public abstract class CryptoServiceBase : ICryptoService
{
    private readonly ILogger _logger;
    private readonly ICryptoDataProvider _dataProvider;
    private readonly PaymentCheckConfig _paymentCheckConfig;

    protected CryptoServiceBase(
        CurrencyConfig currencyConfig,
        ILogger logger,
        ICryptoDataProvider dataProvider,
        PaymentCheckConfig paymentCheckConfig)
    {
        CurrencyConfig = currencyConfig ?? throw new ArgumentNullException(nameof(currencyConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Checked here because a null provider otherwise surfaces as a NullReferenceException that
        // the catch in CheckTransactionStatus swallows, leaving the payment unchanged forever.
        _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        _paymentCheckConfig = paymentCheckConfig ?? throw new ArgumentNullException(nameof(paymentCheckConfig));
    }

    public CurrencyConfig CurrencyConfig { get; }

    public abstract string Symbol { get; }

    public abstract string GetAddressAtIndex(uint index);

    /// <summary>
    /// The largest derivation index that can be derived from an extended public key.
    /// </summary>
    /// <remarks>
    /// BIP32 reads an index with the high bit set as hardened, and hardened children cannot be
    /// derived without the private key. Passing one to NBitcoin throws from inside the derivation,
    /// so the real limit is stated once here rather than being discovered at the point of use.
    /// </remarks>
    public const uint MaxDerivationIndex = 0x8000_0000 - 1;

    internal static void EnsureNonHardenedIndex(uint index)
    {
        if (index > MaxDerivationIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index,
                $"The derivation index must not exceed {MaxDerivationIndex}: BIP32 treats larger indices as hardened, which cannot be derived from an extended public key.");
        }
    }

    public async Task<PaymentRequest> CheckTransactionStatus(PaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation("Checking transaction status for {Symbol} at address {Address}", Symbol, request.ReceivingAddress);

        List<PaymentTransaction> transactions;
        try
        {
            transactions = await _dataProvider.GetInboundTransactionsAsync(request.ReceivingAddress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Unavailable chain data is not the same as "nothing was paid", so the request is
            // returned untouched and reconsidered next pass. Keyed off the token rather than the
            // exception type, because an HttpClient timeout arrives as a TaskCanceledException and
            // is a failed read, not a shutdown.
            _logger.LogError(ex, "Failed to retrieve transactions for {Symbol} at address {Address}", Symbol, request.ReceivingAddress);
            return request;
        }

        var now = DateTime.UtcNow;

        // Every inbound transaction counts towards the same balance: payers top up an underpayment
        // with a second transfer, and exchanges routinely split a withdrawal.
        var totalPaid = transactions.Aggregate(BigInteger.Zero, (total, transaction) => total + transaction.AmountPaid);

        var confirmations = SettlementConfirmations(transactions, request.AmountExpected - CurrencyConfig.AmountToleranceValue);

        var transactionIds = transactions
            .Select(transaction => transaction.TransactionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var checkedPayment = new PaymentRequest
        {
            Id = request.Id,
            DerivationIndex = request.DerivationIndex,
            AmountPaid = totalPaid,
            ConfirmationCount = confirmations,
            Status = request.Status,
            AmountExpected = request.AmountExpected,
            ConfirmationNeeded = request.ConfirmationNeeded,
            ReceivingAddress = request.ReceivingAddress,
            CurrencySymbol = request.CurrencySymbol,
            Network = request.Network,
            CreatedAt = request.CreatedAt,
            ExpiresAt = request.ExpiresAt,
            UpdatedAt = request.UpdatedAt,
            TransactionId = transactionIds.Count == 0 ? null : string.Join(",", transactionIds),
            // Sticky: once funds have arrived late they are late for good, even after they confirm
            // normally.
            //
            // Judged against the previous observation rather than against the current clock. There is
            // no arrival time on chain data here, only the moment this poll happened to run, and a
            // payment broadcast comfortably before expiry is not late merely because the poll that
            // first saw it ran afterwards. Requiring that the request was already known to have
            // expired at the previous check makes the flag mean "the funds cannot have arrived in
            // time", which is what the merchant acts on: it under-reports after a gap in polling,
            // and that is the right direction to be wrong in, because the flag says the order may
            // already have been cancelled.
            WasLate = request.WasLate
                      || (totalPaid > request.AmountPaid && request.UpdatedAt > request.ExpiresAt)
                      || (totalPaid > 0 && request.Status == PaymentStatus.Expired),
            SettledAt = request.SettledAt,
        };

        checkedPayment.SetStatus(DetermineStatus(request, checkedPayment, transactions.Count, now));

        if (checkedPayment.Status.IsSettled())
        {
            checkedPayment.SettledAt ??= now;
        }
        else
        {
            // Cleared when a settlement comes undone, so that a later re-settlement is stamped with
            // the time it actually happened. Carrying the original forward means that a payment which
            // went Confirmed, back to Paid on a reorg, and Confirmed again more than ReorgWatchHours
            // later re-settles with a timestamp already outside the reorg window, and so gets no
            // reorg monitoring for the settlement that counts.
            checkedPayment.SettledAt = null;
        }

        if (checkedPayment.WasLate && !request.WasLate)
        {
            _logger.LogWarning(
                "Payment {PaymentId} for {Symbol} at {Address} received {AmountPaid} after it had already expired at {ExpiresAt}. " +
                "The order may already have been cancelled and the amount was quoted against a stale rate.",
                request.Id, Symbol, request.ReceivingAddress, totalPaid, request.ExpiresAt);
        }

        return checkedPayment;
    }

    /// <summary>
    /// How many confirmations the payment as a whole can claim.
    /// </summary>
    /// <remarks>
    /// The weakest transaction decides, because that is the part a reorg can still take away, but
    /// only among the transactions needed to cover the expected amount. Taking the minimum over
    /// every inbound transaction lets anyone un-settle a confirmed payment: receiving addresses are
    /// published, so a single satoshi sent to one afterwards is a zero-confirmation transaction
    /// that drags the request back from Confirmed to Paid and tells the merchant, over a webhook,
    /// that a settled payment has come undone. Best-confirmed first, so that dust cannot hold up a
    /// payment it is not needed for either.
    /// </remarks>
    internal static uint SettlementConfirmations(List<PaymentTransaction> transactions, BigInteger amountNeeded)
    {
        if (transactions.Count == 0)
        {
            return 0;
        }

        var covered = BigInteger.Zero;
        var weakest = uint.MaxValue;

        foreach (var transaction in transactions.OrderByDescending(transaction => transaction.Confirmations))
        {
            covered += transaction.AmountPaid;
            weakest = Math.Min(weakest, transaction.Confirmations);

            if (covered >= amountNeeded)
            {
                break;
            }
        }

        return weakest;
    }

    private PaymentStatus DetermineStatus(PaymentRequest request, PaymentRequest checkedPayment, int transactionCount, DateTime now)
    {
        var expired = request.ExpiresAt < now;
        var abandoned = request.ExpiresAt + _paymentCheckConfig.AbandonAfterExpiry < now;
        var tolerance = CurrencyConfig.AmountToleranceValue;
        var totalPaid = checkedPayment.AmountPaid;

        if (totalPaid <= 0)
        {
            if (expired)
            {
                _logger.LogInformation(
                    "Payment request for {Symbol} at {Address} has expired. | Timeout (in minutes): {Timeout}",
                    Symbol, request.ReceivingAddress, CurrencyConfig.InitialPaymentTimeout);
                return PaymentStatus.Expired;
            }

            _logger.LogInformation("No payment detected for {Symbol} at {Address}", Symbol, request.ReceivingAddress);
            return PaymentStatus.Pending;
        }

        if (totalPaid < request.AmountExpected - tolerance)
        {
            if (abandoned)
            {
                // Watching this forever means re-polling it on every pass for the life of the
                // deployment, which is what a dusting transaction to a published address exploits.
                // The partial funds stay recorded so they can be refunded.
                _logger.LogWarning(
                    "Abandoning underpaid request {PaymentId} for {Symbol} at {Address}: {AmountPaid} of {Expected} received and the grace period after expiry has passed. The received funds need to be refunded out of band.",
                    request.Id, Symbol, request.ReceivingAddress, totalPaid, request.AmountExpected);
                return PaymentStatus.Expired;
            }

            _logger.LogWarning(
                "Payment for {Symbol} at {Address} is below expected amount across {TransactionCount} transaction(s). Expected: {Expected}, Detected: {Detected}",
                Symbol, request.ReceivingAddress, transactionCount, request.AmountExpected, totalPaid);
            return PaymentStatus.Underpaid;
        }

        var overpaid = totalPaid > request.AmountExpected + tolerance;

        if (overpaid)
        {
            _logger.LogWarning(
                "Payment for {Symbol} at {Address} is over expected amount. Expected: {Expected}, Detected: {Detected}",
                Symbol, request.ReceivingAddress, request.AmountExpected, totalPaid);
        }

        // The requirement frozen at creation time, so that a configuration change cannot move the
        // goalposts for a request already in flight.
        var neededConfirmations = request.ConfirmationNeeded;

        if (checkedPayment.ConfirmationCount < neededConfirmations)
        {
            _logger.LogInformation(
                "Transaction for {Symbol} at {Address} has not enough confirmations. Needed: {Needed}, Current: {Current}",
                Symbol, request.ReceivingAddress, neededConfirmations, checkedPayment.ConfirmationCount);
            return overpaid ? PaymentStatus.Overpaid : PaymentStatus.Paid;
        }

        _logger.LogInformation("Transaction for {Symbol} at {Address} is fully paid with sufficient confirmations.",
            Symbol, request.ReceivingAddress);

        // An overpayment settles like any other payment: without this the merchant gets no signal
        // that it is safe to fulfil, and has to reconcile every rounding error by hand.
        return overpaid ? PaymentStatus.OverpaidConfirmed : PaymentStatus.Confirmed;
    }
}
