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
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        // Act
        var address = bitcoinService.GetAddressAtIndex(5);

        // Assert
        Assert.NotNull(address);
        Assert.Equal("bc1qw8lfd5yp8wqz8pp8wssnyven9gy8l6dvddneh4", address);
    }

    [Fact]
    public async Task CheckTransactionStatus_NoTransactionsFound_ReturnsPendingStatus()
    {
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();

        // Simulate no transactions found
        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([]);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Pending, paymentRequest.Status);

        // Simulate error thrown by provider
        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ThrowsAsync(new HttpRequestException("Simulated error"));

        var errorResult = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(errorResult);
        Assert.Equal(testPaymentRequest.Id, errorResult.Id);
    }

    [Fact]
    public async Task CheckTransactionStatus_ExpiredPayment_ReturnsExpiredStatus()
    {
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();
        testPaymentRequest.ExpiresAt = DateTime.UtcNow.AddMinutes(-10); // Expired 10 minutes ago
        testPaymentRequest.UpdatedAt = DateTime.UtcNow.AddMinutes(-10); // Last update 10 minutes ago

        // Simulate no transactions found
        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([]);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(paymentRequest);
        Assert.Equal(PaymentStatus.Expired, paymentRequest.Status);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);

        // Simulate error thrown by provider
        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ThrowsAsync(new HttpRequestException("Simulated error"));

        var errorResult = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(errorResult);
        Assert.Equal(testPaymentRequest.Id, errorResult.Id);
    }

    [Fact]
    public async Task CheckTransactionStatus_SingleTransactionWithSufficientAmount_ReturnsConfirmedStatus()
    {
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();

        var transaction = new PaymentTransaction
        {
            TransactionId = "test-tx-id-123",
            AmountPaid = 100000, // Exact amount expected
            Confirmations = 1 // Sufficient confirmations
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);

        // Assert
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
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();
        testPaymentRequest.ConfirmationNeeded = 3;

        var transaction = new PaymentTransaction
        {
            TransactionId = "test-tx-id-123",
            AmountPaid = 100000,
            Confirmations = 1
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);

        // Assert
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
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();

        var transaction = new PaymentTransaction
        {
            TransactionId = "test-tx-id-123",
            AmountPaid = 50000,
            Confirmations = 1
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Underpaid, paymentRequest.Status);
        Assert.Equal(50000, paymentRequest.AmountPaid);
        Assert.Equal(1u, paymentRequest.ConfirmationCount);
        Assert.Equal("test-tx-id-123", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_OverpaidTransaction_ReturnsOverpaidStatus()
    {
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();

        var transaction = new PaymentTransaction
        {
            TransactionId = "test-tx-id-123",
            AmountPaid = 150000,
            Confirmations = 1
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Overpaid, paymentRequest.Status);
        Assert.Equal(150000, paymentRequest.AmountPaid);
        Assert.Equal(1u, paymentRequest.ConfirmationCount);
        Assert.Equal("test-tx-id-123", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_MultipleTransactions_ReturnsMultipleTransactionsStatus()
    {
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

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
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync(transactions);

        // Act
        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.MultipleTransactions, paymentRequest.Status);
        Assert.Equal(100000, paymentRequest.AmountPaid); // Sum of both transactions
        Assert.Equal(0u, paymentRequest.ConfirmationCount); // Set to 0 for multiple transactions
        Assert.Equal("", paymentRequest.TransactionId); // Empty for multiple transactions
    }

    [Fact]
    public async Task CheckTransactionStatus_DataProviderThrowsException_ReturnsOriginalRequest()
    {
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var errorResult = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(errorResult);
        Assert.Equal(testPaymentRequest.Id, errorResult.Id);
    }

    [Fact]
    public void GetAddressAtIndex_DifferentIndexes_ShouldReturnDifferentAddresses()
    {
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

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
        // Arrange
        var bitcoinService = new BitcoinService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();
        var originalUpdatedAt = testPaymentRequest.UpdatedAt;

        // Simulate no transactions found
        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([]);

        var paymentRequest = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(paymentRequest);
        Assert.True(paymentRequest.UpdatedAt >= originalUpdatedAt);
        Assert.True(paymentRequest.UpdatedAt <= DateTime.UtcNow);

        // Simulate error thrown by provider
        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ThrowsAsync(new HttpRequestException("Simulated error"));

        var errorResult = await bitcoinService.CheckTransactionStatus(testPaymentRequest);
        Assert.NotNull(errorResult);
        Assert.Equal(testPaymentRequest.Id, errorResult.Id);
    }
}
