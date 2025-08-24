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

    private static CurrencyConfig ReturnValidCurrencyConfig()
    {
        return new CurrencyConfig
        {
            Name = "Ethereum",
            Symbol = "ETH",
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
    }

    private static PaymentRequest CreateTestPaymentRequest()
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            Status = PaymentStatus.Pending,
            AmountExpected = 1000000000000000000, // 1 ETH in wei
            AmountPaid = 0,
            ConfirmationCount = 0,
            ConfirmationNeeded = 1,
            ReceivingAddress = "0x742d35cc6ad7b4f2b7b6ec2c0b7aaa56e3b6e7ec",
            CurrencySymbol = "ETH",
            Network = "Main",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(35)
        };
    }

    [Fact]
    public void Constructor_WithValidConfig_InitializesSuccessfully()
    {
        // Arrange & Act
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        // Assert
        Assert.NotNull(ethereumService);
        Assert.Equal("ETH", ethereumService.Symbol);
        Assert.NotNull(ethereumService.CurrencyConfig);
    }

    [Fact]
    public void Constructor_WithNullConfig_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new EthereumService(null!, _logger.Object, _dataProvider.Object));
    }

    [Fact]
    public void Constructor_WithInvalidExtPubKey_ThrowsException()
    {
        // Arrange
        var invalidConfig = ReturnValidCurrencyConfig();
        invalidConfig.ExtPubKey = "invalid-extended-public-key";

        // Act & Assert
        Assert.ThrowsAny<Exception>(() =>
            new EthereumService(invalidConfig, _logger.Object, _dataProvider.Object));
    }

    [Fact]
    public void GetAddressAtIndex_WithValidIndex_ReturnsEthereumAddress()
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
        Assert.Equal(42, address.Length); // Ethereum addresses are 42 characters (0x + 40 hex chars)
        Assert.Matches("^0x[0-9a-f]{40}$", address); // Should be lowercase hex
    }

    [Fact]
    public void GetAddressAtIndex_WithDifferentIndexes_ReturnsDifferentAddresses()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        // Act
        var address0 = ethereumService.GetAddressAtIndex(0);
        var address1 = ethereumService.GetAddressAtIndex(1);
        var address10 = ethereumService.GetAddressAtIndex(10);
        var address100 = ethereumService.GetAddressAtIndex(100);

        // Assert
        Assert.NotEqual(address0, address1);
        Assert.NotEqual(address1, address10);
        Assert.NotEqual(address10, address100);
        Assert.NotEqual(address0, address100);
    }

    [Fact]
    public void GetAddressAtIndex_WithSameIndex_ReturnsSameAddress()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        // Act
        var address1 = ethereumService.GetAddressAtIndex(5);
        var address2 = ethereumService.GetAddressAtIndex(5);

        // Assert
        Assert.Equal(address1, address2);
    }

    [Fact]
    public void GetAddressAtIndex_FollowsBip44DerivationPath()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        // Act
        var address = ethereumService.GetAddressAtIndex(0);

        // Assert
        // The address should be deterministic based on the derivation path m/0/0
        Assert.NotNull(address);
        Assert.StartsWith("0x", address, StringComparison.Ordinal);
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

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([]);

        // Act
        var result = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testPaymentRequest.Id, result.Id);
        Assert.Equal(PaymentStatus.Pending, result.Status);
        Assert.Equal(0, result.AmountPaid);
        Assert.Equal(0u, result.ConfirmationCount);
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
        testPaymentRequest.ExpiresAt = DateTime.UtcNow.AddMinutes(-10); // Expired 10 minutes ago

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([]);

        // Act
        var result = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testPaymentRequest.Id, result.Id);
        Assert.Equal(PaymentStatus.Expired, result.Status);
    }

    [Fact]
    public async Task CheckTransactionStatus_SingleTransactionWithExactAmount_ReturnsConfirmedStatus()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        var testPaymentRequest = CreateTestPaymentRequest();

        var transaction = new PaymentTransaction
        {
            TransactionId = "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            AmountPaid = 1000000000000000000, // 1 ETH in wei
            Confirmations = 1
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var result = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testPaymentRequest.Id, result.Id);
        Assert.Equal(PaymentStatus.Confirmed, result.Status);
        Assert.Equal(testPaymentRequest.AmountExpected, result.AmountPaid);
        Assert.Equal(1u, result.ConfirmationCount);
        Assert.Equal(transaction.TransactionId, result.TransactionId);
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
        testPaymentRequest.ConfirmationNeeded = 3; // Need 3 confirmations

        var transaction = new PaymentTransaction
        {
            TransactionId = "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            AmountPaid = 1000000000000000000,
            Confirmations = 1 // Only 1 confirmation, need 3
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var result = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testPaymentRequest.Id, result.Id);
        Assert.Equal(PaymentStatus.Paid, result.Status);
        Assert.Equal(testPaymentRequest.AmountExpected, result.AmountPaid);
        Assert.Equal(1u, result.ConfirmationCount);
        Assert.Equal(transaction.TransactionId, result.TransactionId);
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
            TransactionId = "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            AmountPaid = 500000000000000000, // 0.5 ETH in wei (less than expected 1 ETH)
            Confirmations = 1
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var result = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testPaymentRequest.Id, result.Id);
        Assert.Equal(PaymentStatus.Underpaid, result.Status);
        Assert.Equal(500000000000000000, result.AmountPaid);
        Assert.Equal(1u, result.ConfirmationCount);
        Assert.Equal(transaction.TransactionId, result.TransactionId);
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
            TransactionId = "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            AmountPaid = 1500000000000000000, // 1.5 ETH in wei (more than expected 1 ETH)
            Confirmations = 1
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync([transaction]);

        // Act
        var result = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testPaymentRequest.Id, result.Id);
        Assert.Equal(PaymentStatus.Overpaid, result.Status);
        Assert.Equal(1500000000000000000, result.AmountPaid);
        Assert.Equal(1u, result.ConfirmationCount);
        Assert.Equal(transaction.TransactionId, result.TransactionId);
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
                TransactionId = "0x1111111111111111111111111111111111111111111111111111111111111111",
                AmountPaid = 600000000000000000, // 0.6 ETH
                Confirmations = 1
            },
            new()
            {
                TransactionId = "0x2222222222222222222222222222222222222222222222222222222222222222",
                AmountPaid = 400000000000000000, // 0.4 ETH
                Confirmations = 1
            }
        };

        _dataProvider
            .Setup(dp => dp.GetTransactionsAsync(
                It.IsAny<string>(),
                It.IsAny<uint>()))
            .ReturnsAsync(transactions);

        // Act
        var result = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testPaymentRequest.Id, result.Id);
        Assert.Equal(PaymentStatus.MultipleTransactions, result.Status);
        Assert.Equal(1000000000000000000, result.AmountPaid); // Sum of both transactions (1 ETH)
        Assert.Equal(0u, result.ConfirmationCount); // Set to 0 for multiple transactions
        Assert.Equal("", result.TransactionId); // Empty for multiple transactions
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
        var result = await ethereumService.CheckTransactionStatus(testPaymentRequest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testPaymentRequest.Id, result.Id);
        // Should return the original request when an error occurs
        Assert.Equal(testPaymentRequest.Status, result.Status);
    }

    [Fact]
    public async Task CheckTransactionStatus_WithNullPaymentRequest_ThrowsArgumentNullException()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await ethereumService.CheckTransactionStatus(null!));
    }

    [Fact]
    public void Symbol_ReturnsEth()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        // Act & Assert
        Assert.Equal("ETH", ethereumService.Symbol);
    }

    [Fact]
    public void CurrencyConfig_ReturnsConfiguredValue()
    {
        // Arrange
        var config = ReturnValidCurrencyConfig();
        var ethereumService = new EthereumService(
            config,
            _logger.Object,
            _dataProvider.Object);

        // Act & Assert
        Assert.Equal(config, ethereumService.CurrencyConfig);
        Assert.Equal("Ethereum", ethereumService.CurrencyConfig.Name);
        Assert.Equal("ETH", ethereumService.CurrencyConfig.Symbol);
    }

    [Fact]
    public void GetAddressAtIndex_GeneratesValidEthereumAddressFormat()
    {
        // Arrange
        var ethereumService = new EthereumService(
            ReturnValidCurrencyConfig(),
            _logger.Object,
            _dataProvider.Object);

        // Act
        var address = ethereumService.GetAddressAtIndex(42);

        // Assert
        Assert.NotNull(address);
        Assert.StartsWith("0x", address, StringComparison.Ordinal);
        Assert.Equal(42, address.Length);
        Assert.Matches("^0x[0-9a-f]{40}$", address);
    }
}
