using System.Threading.Tasks;
using Castle.Core.Logging;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using Microsoft.Extensions.Logging;
using Moq;
using NBitcoin;
using System.Collections.Generic;

namespace CryptoHook.Api.UnitTests.Services.CryptoServices;

public class BitcoinServiceTests
{
    private readonly Mock<ILogger<BitcoinService>> _logger;
    private readonly Mock<ICryptoDataProvider> _dataProvider;
    public BitcoinServiceTests()
    {
        _logger = new Mock<ILogger<BitcoinService>>();
        _dataProvider = new Mock<ICryptoDataProvider>();
    }

    private static CurrencyConfig ReturnValidCurrencyConfig() => new()
    {
        Name = "Bitcoin",
        Symbol = "BTC",
        IsEnabled = true,
        InitialPaymentTimeout = 30.0,
        ExtPubKey = "xpub6Cr3shfQvx8bZBrj8g8dsaZyYxLbFB75epS8s2wgmw9561qxsj2bRQ8BhiwLQdZB2gVzhWzCyPjNLn7zbg6nt828ooSmDsgs9aU9BSwQYbk",
        Network = "Main",
        Confirmations =
                [
                    new() { Amount = 0, ConfirmationsNeeded = 1 },
                    new() { Amount = 1000, ConfirmationsNeeded = 3 },
                    new() { Amount = 10000, ConfirmationsNeeded = 6 }
                ]
    };

    private static PaymentRequest CreateTestPaymentRequest()
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            Status = PaymentStatus.Pending,
            AmountExpected = 100000,
            AmountPaid = 0,
            ConfirmationCount = 0,
            ConfirmationNeeded = 1,
            ReceivingAddress = "bc1qtest123",
            CurrencySymbol = "BTC",
            Network = "Main",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(35)
        };
    }

    [Fact]
    public void GetAddressAtIndex_UseValidIndex_ReturnsAddress()
    {
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        var address = bitcoinService.GetAddressAtIndex(5);

        Assert.NotNull(address);
        Assert.Equal("bc1qw8lfd5yp8wqz8pp8wssnyven9gy8l6dvddneh4", address);
    }

    [Fact]
    public async Task CheckTransactionStatus_NoTransactionsFound_ReturnsPendingStatus()
    {
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        var testPaymentRequest = CreateTestPaymentRequest();

        // Simulate no transactions found
        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Pending, paymentRequest.Status);

        // Simulate error thrown by provider
        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Simulated error"));

        var errorResult = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(errorResult);
        Assert.Equal(testPaymentRequest.Id, errorResult.Id);
    }

    [Fact]
    public async Task CheckTransactionStatus_ExpiredPayment_ReturnsExpiredStatus()
    {
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        var testPaymentRequest = CreateTestPaymentRequest();
        testPaymentRequest.ExpiresAt = DateTime.UtcNow.AddMinutes(-10); // Expired 10 minutes ago
        testPaymentRequest.UpdatedAt = DateTime.UtcNow.AddMinutes(-10); // Last update 10 minutes ago

        // Simulate no transactions found
        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(paymentRequest);
        Assert.Equal(PaymentStatus.Expired, paymentRequest.Status);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);

        // Simulate error thrown by provider
        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Simulated error"));

        var errorResult = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(errorResult);
        Assert.Equal(testPaymentRequest.Id, errorResult.Id);
    }

    [Fact]
    public async Task CheckTransactionStatus_SingleTransactionWithSufficientAmount_ReturnsConfirmedStatus()
    {
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        var testPaymentRequest = CreateTestPaymentRequest();

        var transaction = new PaymentTransaction
        {
            TransactionId = "test-tx-id-123",
            AmountPaid = 100000, // Exact amount expected
            Confirmations = 1 // Sufficient confirmations
        };

        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([transaction]);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);

        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Confirmed, paymentRequest.Status);
        Assert.Equal(testPaymentRequest.AmountExpected, paymentRequest.AmountPaid);
        Assert.Equal(1u, paymentRequest.ConfirmationCount);
        Assert.Equal("test-tx-id-123", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_SingleTransactionWithInsufficientConfirmations_ReturnsPaidStatus()
    {
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        var testPaymentRequest = CreateTestPaymentRequest();
        testPaymentRequest.ConfirmationNeeded = 3;

        var transaction = new PaymentTransaction
        {
            TransactionId = "test-tx-id-123",
            AmountPaid = 100000,
            Confirmations = 1
        };

        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([transaction]);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);

        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Paid, paymentRequest.Status);
        Assert.Equal(testPaymentRequest.AmountExpected, paymentRequest.AmountPaid);
        Assert.Equal(1u, paymentRequest.ConfirmationCount);
        Assert.Equal("test-tx-id-123", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_UnderpaidTransaction_ReturnsUnderpaidStatus()
    {
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        var testPaymentRequest = CreateTestPaymentRequest();

        var transaction = new PaymentTransaction
        {
            TransactionId = "test-tx-id-123",
            AmountPaid = 50000,
            Confirmations = 1
        };

        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([transaction]);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);

        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Underpaid, paymentRequest.Status);
        Assert.Equal(50000, paymentRequest.AmountPaid);
        Assert.Equal(1u, paymentRequest.ConfirmationCount);
        Assert.Equal("test-tx-id-123", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_OverpaidTransaction_SettlesOnceConfirmed()
    {
        // An overpayment settles like any other payment: staying in Overpaid with confirmations
        // never evaluated leaves the merchant no signal that it is safe to fulfil.
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        var testPaymentRequest = CreateTestPaymentRequest();

        var transaction = new PaymentTransaction
        {
            TransactionId = "test-tx-id-123",
            AmountPaid = 150000,
            Confirmations = 1
        };

        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([transaction]);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);

        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.OverpaidConfirmed, paymentRequest.Status);
        Assert.Equal(150000, paymentRequest.AmountPaid);
        Assert.Equal(1u, paymentRequest.ConfirmationCount);
        Assert.Equal("test-tx-id-123", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_MultipleTransactions_AreAggregatedIntoOneBalance()
    {
        // Payers top up an underpayment and exchanges split withdrawals, so a second transaction
        // is normal. Rejecting the pair as MultipleTransactions, with the amount recorded but the
        // transaction ids and confirmations thrown away, left the request stuck for good.
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        var testPaymentRequest = CreateTestPaymentRequest();

        var transactions = new List<PaymentTransaction>
        {
            new()
            {
                TransactionId = "test-tx-id-1",
                AmountPaid = 60000,
                Confirmations = 1
            },
            new()
            {
                TransactionId = "test-tx-id-2",
                AmountPaid = 40000,
                Confirmations = 1
            }
        };

        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(transactions);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);

        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Confirmed, paymentRequest.Status);
        Assert.Equal(100000, paymentRequest.AmountPaid); // Sum of both transactions
        Assert.Equal(1u, paymentRequest.ConfirmationCount); // The least confirmed part decides
        Assert.Equal("test-tx-id-1,test-tx-id-2", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_DataProviderThrowsException_ReturnsOriginalRequest()
    {
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        var testPaymentRequest = CreateTestPaymentRequest();

        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        var errorResult = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(errorResult);
        Assert.Equal(testPaymentRequest.Id, errorResult.Id);
    }

    [Fact]
    public void GetAddressAtIndex_DifferentIndexes_ShouldReturnDifferentAddresses()
    {
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        // Act & Assert - Should not throw for reasonable index values
        var address1 = bitcoinService.GetAddressAtIndex(0);
        var address2 = bitcoinService.GetAddressAtIndex(10);
        var address3 = bitcoinService.GetAddressAtIndex(100);

        Assert.NotNull(address1);
        Assert.NotNull(address2);
        Assert.NotNull(address3);
        Assert.NotEqual(address1, address2);
        Assert.NotEqual(address2, address3);
        Assert.NotEqual(address1, address3);
    }

    [Fact]
    public async Task CheckTransactionStatus_UpdatedAtIsSetToCurrentTime()
    {
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object,
            TestDefaults.PaymentCheckConfig());

        var testPaymentRequest = CreateTestPaymentRequest();
        var originalUpdatedAt = testPaymentRequest.UpdatedAt;

        // Simulate no transactions found
        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(paymentRequest);
        Assert.True(paymentRequest.UpdatedAt >= originalUpdatedAt);
        Assert.True(paymentRequest.UpdatedAt <= DateTime.UtcNow);

        // Simulate error thrown by provider
        _dataProvider
            .Setup(dp => dp.GetInboundTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Simulated error"));

        var errorResult = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(errorResult);
        Assert.Equal(testPaymentRequest.Id, errorResult.Id);
    }
}
