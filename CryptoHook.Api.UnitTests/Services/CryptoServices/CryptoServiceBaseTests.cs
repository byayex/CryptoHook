using System.Numerics;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CryptoHook.Api.UnitTests.Services.CryptoServices;

/// <summary>
/// The payment evaluation rules are currency independent and shared by all services, so
/// they are asserted once through both implementations.
/// </summary>
public class CryptoServiceBaseTests
{
    private const string BitcoinExtPubKey =
        "xpub6Cr3shfQvx8bZBrj8g8dsaZyYxLbFB75epS8s2wgmw9561qxsj2bRQ8BhiwLQdZB2gVzhWzCyPjNLn7zbg6nt828ooSmDsgs9aU9BSwQYbk";

    private readonly Mock<ICryptoDataProvider> _mockDataProvider = new();

    private static CurrencyConfig CreateConfig(string name, string symbol, string network, string amountTolerance = "0")
    {
        return new CurrencyConfig
        {
            Name = name,
            Symbol = symbol,
            IsEnabled = true,
            InitialPaymentTimeout = 30.0,
            ExtPubKey = BitcoinExtPubKey,
            Network = network,
            AmountTolerance = amountTolerance,
            Confirmations = [new() { Amount = 0, ConfirmationsNeeded = 2 }]
        };
    }

    public static TheoryData<string> ServiceNames => ["BTC", "ETH"];

    private ICryptoService CreateService(string symbol, string amountTolerance = "0", PaymentCheckConfig? checkConfig = null)
    {
        checkConfig ??= TestDefaults.PaymentCheckConfig();

        return symbol switch
        {
            "BTC" => new BitcoinService(
                CreateConfig("Bitcoin", "BTC", "Main", amountTolerance),
                NullLogger<BitcoinService>.Instance,
                _mockDataProvider.Object,
                checkConfig),
            "ETH" => new EthereumService(
                CreateConfig("Ethereum", "ETH", "Main", amountTolerance),
                NullLogger<EthereumService>.Instance,
                _mockDataProvider.Object,
                checkConfig),
            _ => throw new ArgumentOutOfRangeException(nameof(symbol)),
        };
    }

    private void SetupTransactions(params PaymentTransaction[] transactions)
    {
        _mockDataProvider
            .Setup(p => p.GetInboundTransactionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. transactions]);
    }

    private static PaymentRequest CreateRequest(
        PaymentStatus status = PaymentStatus.Pending,
        ulong derivationIndex = 7,
        BigInteger? amountExpected = null)
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            DerivationIndex = derivationIndex,
            Status = status,
            AmountExpected = amountExpected ?? 100000,
            AmountPaid = BigInteger.Zero,
            ConfirmationCount = 0,
            ConfirmationNeeded = 2,
            ReceivingAddress = "0x1234567890abcdef1234567890abcdef12345678",
            CurrencySymbol = "BTC",
            Network = "Main",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_PreservesTheDerivationIndex(string symbol)
    {
        // The derivation index was dropped from the returned copy, so anything consuming the
        // result (webhooks, callers) saw index 0 for every payment.
        SetupTransactions();

        var request = CreateRequest(derivationIndex: 42);

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.Equal(42ul, result.DerivationIndex);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_DataProviderFails_LeavesTheRequestUntouched(string symbol)
    {
        // Unavailable chain data must not be mistaken for "nothing was paid", which would
        // eventually expire a request that had in fact been paid.
        _mockDataProvider
            .Setup(p => p.GetInboundTransactionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("explorer unavailable"));

        var request = CreateRequest();
        request.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.Same(request, result);
        Assert.Equal(PaymentStatus.Pending, result.Status);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_ZeroConfirmationOverpayment_IsNotTreatedAsSettled(string symbol)
    {
        // An unconfirmed overpayment can still be replaced, so it must not settle yet.
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 200000, Confirmations = 0 });

        var result = await CreateService(symbol).CheckTransactionStatus(CreateRequest());

        Assert.Equal(PaymentStatus.Overpaid, result.Status);
        Assert.False(result.Status.IsSettled());
        Assert.Null(result.SettledAt);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_OverpaymentWithEnoughConfirmations_Settles(string symbol)
    {
        // An overpayment settles like any other payment: staying in Overpaid with confirmations
        // never evaluated leaves the merchant no signal that it is safe to fulfil.
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 200000, Confirmations = 5 });

        var result = await CreateService(symbol).CheckTransactionStatus(CreateRequest());

        Assert.Equal(PaymentStatus.OverpaidConfirmed, result.Status);
        Assert.True(result.Status.IsSettled());
        Assert.NotNull(result.SettledAt);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_UnderpaidRequest_StaysUnderpaid(string symbol)
    {
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 50000, Confirmations = 3 });

        var result = await CreateService(symbol).CheckTransactionStatus(CreateRequest());

        Assert.Equal(PaymentStatus.Underpaid, result.Status);
        Assert.False(result.Status.IsSettled());
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_ExactAmountWithEnoughConfirmations_IsConfirmed(string symbol)
    {
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 100000, Confirmations = 2 });

        var result = await CreateService(symbol).CheckTransactionStatus(CreateRequest());

        Assert.Equal(PaymentStatus.Confirmed, result.Status);
        Assert.True(result.Status.IsSettled());
        Assert.NotNull(result.SettledAt);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_SplitPayment_IsAggregatedAndSettles(string symbol)
    {
        // Payers top up an underpayment and exchanges split withdrawals, so a second transaction
        // is normal. Rejecting it as MultipleTransactions left both cases permanently stuck.
        SetupTransactions(
            new PaymentTransaction { TransactionId = "tx1", AmountPaid = 40000, Confirmations = 4 },
            new PaymentTransaction { TransactionId = "tx2", AmountPaid = 60000, Confirmations = 2 });

        var result = await CreateService(symbol).CheckTransactionStatus(CreateRequest());

        Assert.Equal(PaymentStatus.Confirmed, result.Status);
        Assert.Equal(new BigInteger(100000), result.AmountPaid);
        Assert.Equal("tx1,tx2", result.TransactionId);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_SplitPayment_ReportsTheLeastConfirmedPart(string symbol)
    {
        // The payment is only as settled as its weakest transaction, because that is the part a
        // reorganisation can still take away.
        SetupTransactions(
            new PaymentTransaction { TransactionId = "tx1", AmountPaid = 60000, Confirmations = 9 },
            new PaymentTransaction { TransactionId = "tx2", AmountPaid = 40000, Confirmations = 1 });

        var result = await CreateService(symbol).CheckTransactionStatus(CreateRequest());

        Assert.Equal(1u, result.ConfirmationCount);
        Assert.Equal(PaymentStatus.Paid, result.Status);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_ShortfallWithinTolerance_CountsAsExact(string symbol)
    {
        // Without a tolerance a payment a single unit short is underpaid forever.
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 99999, Confirmations = 2 });

        var result = await CreateService(symbol, amountTolerance: "10").CheckTransactionStatus(CreateRequest());

        Assert.Equal(PaymentStatus.Confirmed, result.Status);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_ShortfallOutsideTolerance_IsUnderpaid(string symbol)
    {
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 99000, Confirmations = 2 });

        var result = await CreateService(symbol, amountTolerance: "10").CheckTransactionStatus(CreateRequest());

        Assert.Equal(PaymentStatus.Underpaid, result.Status);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_UnderpaidPastTheGracePeriod_IsAbandoned(string symbol)
    {
        // A request that received a stray payment could never expire, because expiry only ever
        // applied when nothing at all had arrived. Every such row was then re-polled on every
        // pass forever, which is exactly what dusting a published address would exploit.
        SetupTransactions(new PaymentTransaction { TransactionId = "dust", AmountPaid = 1, Confirmations = 6 });

        var request = CreateRequest(PaymentStatus.Underpaid);
        request.ExpiresAt = DateTime.UtcNow.AddHours(-25);

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.Equal(PaymentStatus.Expired, result.Status);
        Assert.Equal(BigInteger.One, result.AmountPaid);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_UnderpaidWithinTheGracePeriod_IsStillUnderpaid(string symbol)
    {
        SetupTransactions(new PaymentTransaction { TransactionId = "part", AmountPaid = 50000, Confirmations = 6 });

        var request = CreateRequest(PaymentStatus.Underpaid);
        request.ExpiresAt = DateTime.UtcNow.AddHours(-1);

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.Equal(PaymentStatus.Underpaid, result.Status);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_NothingReceivedAfterExpiry_Expires(string symbol)
    {
        SetupTransactions();

        var request = CreateRequest();
        request.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.Equal(PaymentStatus.Expired, result.Status);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_FundsArrivingAfterExpiry_AreFlaggedAsLate(string symbol)
    {
        // The merchant was already told the request expired and may have cancelled the order, so
        // a payment that turns up afterwards has to stay distinguishable from a normal one.
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 100000, Confirmations = 6 });

        var request = CreateRequest(PaymentStatus.Expired);
        request.ExpiresAt = DateTime.UtcNow.AddHours(-2);

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.Equal(PaymentStatus.Confirmed, result.Status);
        Assert.True(result.WasLate);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_LateFlag_SurvivesLaterChecks(string symbol)
    {
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 100000, Confirmations = 6 });

        var request = CreateRequest(PaymentStatus.Paid);
        request.WasLate = true;

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.True(result.WasLate);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_PaymentOnTime_IsNotFlaggedAsLate(string symbol)
    {
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 100000, Confirmations = 6 });

        var result = await CreateService(symbol).CheckTransactionStatus(CreateRequest());

        Assert.False(result.WasLate);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_AlreadySettled_KeepsTheOriginalSettlementTime(string symbol)
    {
        // The reorg watch window is anchored on this, so re-confirming a payment must not keep
        // pushing it forward.
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 100000, Confirmations = 9 });

        var settledAt = DateTime.UtcNow.AddHours(-3);
        var request = CreateRequest(PaymentStatus.Confirmed);
        request.SettledAt = settledAt;

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.Equal(settledAt, result.SettledAt);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_DustArrivingAfterSettlement_DoesNotUndoIt(string symbol)
    {
        // Receiving addresses are published, so anyone can send a single satoshi to one. Taking
        // the minimum confirmation count over every inbound transaction let that drag a settled
        // payment back to Paid and told the merchant a confirmed payment had come undone.
        SetupTransactions(
            new PaymentTransaction { TransactionId = "payment", AmountPaid = 100000, Confirmations = 9 },
            new PaymentTransaction { TransactionId = "dust", AmountPaid = 1, Confirmations = 0 });

        var request = CreateRequest(PaymentStatus.Confirmed);
        request.SettledAt = DateTime.UtcNow.AddHours(-1);

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.Equal(PaymentStatus.OverpaidConfirmed, result.Status);
        Assert.Equal(9u, result.ConfirmationCount);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_TransactionNeededToCoverTheAmount_StillDecidesConfirmations(string symbol)
    {
        // The weakest transaction must still hold the payment back when it is part of what
        // actually covers the expected amount.
        SetupTransactions(
            new PaymentTransaction { TransactionId = "first", AmountPaid = 60000, Confirmations = 9 },
            new PaymentTransaction { TransactionId = "topup", AmountPaid = 40000, Confirmations = 0 });

        var result = await CreateService(symbol).CheckTransactionStatus(CreateRequest());

        Assert.Equal(PaymentStatus.Paid, result.Status);
        Assert.Equal(0u, result.ConfirmationCount);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_FundsArrivingAfterExpiry_AreMarkedLate(string symbol)
    {
        // The pass that first sees the money is usually also the pass that would have written
        // Expired, so keying this off the stored status missed the case entirely. The request was
        // already known to have expired when it was last checked, so anything arriving since then
        // cannot have arrived in time.
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 100000, Confirmations = 5 });

        var request = CreateRequest();
        request.ExpiresAt = DateTime.UtcNow.AddMinutes(-30);
        request.UpdatedAt = DateTime.UtcNow.AddMinutes(-5);

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.True(result.WasLate);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_FundsThatCouldHaveArrivedBeforeExpiry_AreNotMarkedLate(string symbol)
    {
        // Chain data carries no arrival time, only the moment this poll ran. The request had not
        // expired at the previous check, so the funds may well have been sent in time and simply
        // observed a pass later. Flagging that as late tells the merchant an order they may have
        // cancelled was paid too late, on no evidence at all.
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 100000, Confirmations = 5 });

        var request = CreateRequest();
        request.UpdatedAt = DateTime.UtcNow.AddMinutes(-10);
        request.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.False(result.WasLate);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task CheckTransactionStatus_SettlementUndoneByAReorg_ClearsTheSettlementTime(string symbol)
    {
        // The reorg watch selects on SettledAt, so carrying the original forward would leave a
        // later re-settlement stamped outside the window and unwatched.
        SetupTransactions(new PaymentTransaction { TransactionId = "tx", AmountPaid = 100000, Confirmations = 1 });

        var request = CreateRequest(PaymentStatus.Confirmed);
        request.SettledAt = DateTime.UtcNow.AddHours(-3);

        var result = await CreateService(symbol).CheckTransactionStatus(request);

        Assert.Equal(PaymentStatus.Paid, result.Status);
        Assert.Null(result.SettledAt);
    }

    [Fact]
    public async Task CheckTransactionStatus_NullRequest_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateService("BTC").CheckTransactionStatus(null!));
    }
}
