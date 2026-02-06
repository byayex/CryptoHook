using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using Microsoft.Extensions.Logging;
using Moq;

namespace CryptoHook.Api.UnitTests.Services.CryptoServices;

public class EthereumServiceTests
{
    private readonly Mock<ILogger<EthereumService>> _logger;
    private readonly Mock<ICryptoDataProvider> _dataProvider;
    
    public EthereumServiceTests()
    {
        _logger = new Mock<ILogger<EthereumService>>();
        _dataProvider = new Mock<ICryptoDataProvider>();
    }

    private static CurrencyConfig ReturnValidCurrencyConfig() => new()
    {
        Name = "Ethereum",
        Symbol = "ETH",
        IsEnabled = true,
        InitialPaymentTimeout = 30.0,
        // Test mnemonic phrase (12 words) - DO NOT USE IN PRODUCTION
        ExtPubKey = "test test test test test test test test test test test junk",
        Network = "Main",
        Confirmations =
        [
            new() { Amount = 0, ConfirmationsNeeded = 12 },
            new() { Amount = 1000000000000000000, ConfirmationsNeeded = 35 }
        ]
    };

    private static PaymentRequest CreateTestPaymentRequest()
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            Status = PaymentStatus.Pending,
            AmountExpected = 1000000000000000000, // 1 ETH in Wei
            AmountPaid = 0,
            ConfirmationCount = 0,
            ConfirmationNeeded = 12,
            ReceivingAddress = "0x0000000000000000000000000000000000000000",
            CurrencySymbol = "ETH",
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
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        // Act
        var address = ethereumService.GetAddressAtIndex(0);

        // Assert
        Assert.NotNull(address);
        Assert.StartsWith("0x", address, StringComparison.Ordinal);
        Assert.Equal(42, address.Length); // Ethereum addresses are 42 characters (0x + 40 hex)
    }

    [Fact]
    public async Task CheckTransactionStatus_NoTransactionsFound_ReturnsPendingStatus()
    {
        // Arrange
        var ethereumService = new EthereumService(
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

        // Act
        var paymentRequest = await ethereumService.CheckTransactionStatus(testPaymentRequest);
        
        // Assert
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Pending, paymentRequest.Status);
    }

    [Fact]
    public async Task CheckTransactionStatus_ExpiredPayment_ReturnsExpiredStatus()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();
        testPaymentRequest.ExpiresAt = DateTime.UtcNow.AddMinutes(-10);

        // Simulate no transactions found
        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([]);

        // Act
        var paymentRequest = await ethereumService.CheckTransactionStatus(testPaymentRequest);
        
        // Assert
        Assert.NotNull(paymentRequest);
        Assert.Equal(PaymentStatus.Expired, paymentRequest.Status);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
    }

    [Fact]
    public async Task CheckTransactionStatus_SingleTransactionWithSufficientAmount_ReturnsConfirmedStatus()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();

        var transaction = new PaymentTransaction
        {
            TransactionId = "0xtest123",
            AmountPaid = 1000000000000000000, // 1 ETH in Wei
            Confirmations = 12
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var paymentRequest = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Confirmed, paymentRequest.Status);
        Assert.Equal(testPaymentRequest.AmountExpected, paymentRequest.AmountPaid);
        Assert.Equal(12u, paymentRequest.ConfirmationCount);
        Assert.Equal("0xtest123", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_SingleTransactionWithInsufficientConfirmations_ReturnsPaidStatus()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();
        testPaymentRequest.ConfirmationNeeded = 35;

        var transaction = new PaymentTransaction
        {
            TransactionId = "0xtest123",
            AmountPaid = 1000000000000000000,
            Confirmations = 12
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var paymentRequest = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Paid, paymentRequest.Status);
        Assert.Equal(testPaymentRequest.AmountExpected, paymentRequest.AmountPaid);
        Assert.Equal(12u, paymentRequest.ConfirmationCount);
        Assert.Equal("0xtest123", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_UnderpaidTransaction_ReturnsUnderpaidStatus()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();

        var transaction = new PaymentTransaction
        {
            TransactionId = "0xtest123",
            AmountPaid = 500000000000000000, // 0.5 ETH
            Confirmations = 12
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var paymentRequest = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Underpaid, paymentRequest.Status);
        Assert.Equal(500000000000000000, paymentRequest.AmountPaid);
        Assert.Equal(12u, paymentRequest.ConfirmationCount);
        Assert.Equal("0xtest123", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_OverpaidTransaction_ReturnsOverpaidStatus()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();

        var transaction = new PaymentTransaction
        {
            TransactionId = "0xtest123",
            AmountPaid = 1500000000000000000, // 1.5 ETH
            Confirmations = 12
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var paymentRequest = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.Overpaid, paymentRequest.Status);
        Assert.Equal(1500000000000000000, paymentRequest.AmountPaid);
        Assert.Equal(12u, paymentRequest.ConfirmationCount);
        Assert.Equal("0xtest123", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_MultipleTransactions_ReturnsMultipleTransactionsStatus()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();

        var transactions = new List<PaymentTransaction>
        {
            new()
            {
                TransactionId = "0xtest1",
                AmountPaid = 600000000000000000,
                Confirmations = 12
            },
            new()
            {
                TransactionId = "0xtest2",
                AmountPaid = 400000000000000000,
                Confirmations = 12
            }
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync(transactions);

        // Act
        var paymentRequest = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(paymentRequest);
        Assert.Equal(testPaymentRequest.Id, paymentRequest.Id);
        Assert.Equal(PaymentStatus.MultipleTransactions, paymentRequest.Status);
        Assert.Equal(1000000000000000000, paymentRequest.AmountPaid);
        Assert.Equal(0u, paymentRequest.ConfirmationCount);
        Assert.Equal("", paymentRequest.TransactionId);
    }

    [Fact]
    public async Task CheckTransactionStatus_DataProviderThrowsException_ReturnsOriginalRequest()
    {
        // Arrange
        var ethereumService = new EthereumService(
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
        var errorResult = await ethereumService.CheckTransactionStatus(testPaymentRequest);
        
        // Assert
        Assert.NotNull(errorResult);
        Assert.Equal(testPaymentRequest.Id, errorResult.Id);
    }

    [Fact]
    public void GetAddressAtIndex_DifferentIndexes_ShouldReturnDifferentAddresses()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        // Act
        var address0 = ethereumService.GetAddressAtIndex(0);
        var address1 = ethereumService.GetAddressAtIndex(1);
        var address2 = ethereumService.GetAddressAtIndex(2);

        // Assert
        Assert.NotNull(address0);
        Assert.NotNull(address1);
        Assert.NotNull(address2);
        Assert.NotEqual(address0, address1);
        Assert.NotEqual(address1, address2);
        Assert.NotEqual(address0, address2);
    }
}
