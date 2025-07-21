using CryptoHook.Api.Managers;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using CryptoHook.Api.Services.CryptoServices.Factory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CryptoHook.Api.UnitTests.Services.Factory;

public class CryptoServiceFactoryTests
{
    private readonly ConfigManager _configManager;
    private readonly Mock<IHttpClientFactory> _mockHttpClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILogger<CryptoServiceFactory>> _mockServiceLogger;

    public CryptoServiceFactoryTests()
    {
        var testCurrencyConfig = new CurrencyConfigList()
        {
            new CurrencyConfig
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
            },
            new CurrencyConfig
            {
                Name = "Bitcoin Testnet",
                Symbol = "BTC",
                IsEnabled = false,
                InitialPaymentTimeout = 30.0,
                ExtPubKey = "xpub6Cr3shfQvx8bZBrj8g8dsaZyYxLbFB75epS8s2wgmw9561qxsj2bRQ8BhiwLQdZB2gVzhWzCyPjNLn7zbg6nt828ooSmDsgs9aU9BSwQYbk",
                Network = "TestNet",
                Confirmations =
                [
                    new() { Amount = 0, ConfirmationsNeeded = 1 }
                ]
            }
        };

        var mockOptions = new Mock<IOptions<CurrencyConfigList>>();
        mockOptions.Setup(x => x.Value).Returns(testCurrencyConfig);
        var mockLogger = new Mock<ILogger<ConfigManager>>();
        _configManager = new ConfigManager(mockOptions.Object, mockLogger.Object);

        _mockHttpClient = new Mock<IHttpClientFactory>();

        _loggerFactory = NullLoggerFactory.Instance;
        _mockServiceLogger = new Mock<ILogger<CryptoServiceFactory>>();
    }

    [Theory]
    [InlineData("UNKNOWN", "Main")]
    [InlineData("BTC", "UNKNOWN")]
    public void GetService_RequestUnknownData_ThrowsError(string symbol, string network)
    {
        // Arrange
        var factory = new CryptoServiceFactory(_configManager, _mockHttpClient.Object, _loggerFactory);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => factory.GetService(symbol, network));
    }

    [Theory]
    [InlineData("UNKNOWN", "Bitcoin", "Main")]
    [InlineData("BTC", "Bitcoin", "UNKNOWN")]
    [InlineData("BTC", "UNKNOWN", "Main")]
    public void GetService_RequestUnknownAvailableCurrency_ThrowsError(string symbol, string name, string network)
    {
        // Arrange
        var currency = new AvailableCurrency
        {
            Symbol = symbol,
            Name = name,
            Network = network
        };
        var factory = new CryptoServiceFactory(_configManager, _mockHttpClient.Object, _loggerFactory);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => factory.GetService(currency));
    }

    [Fact]
    public void GetService_RequestDisabledCurrency_ThrowsError()
    {
        // Arrange
        var factory = new CryptoServiceFactory(_configManager, _mockHttpClient.Object, _loggerFactory);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => factory.GetService("BTC", "TestNet"));
    }

    [Fact]
    public void GetService_RequestBTCServiceCaseInsensitive_ReturnsCachedService()
    {
        // Arrange
        var factory = new CryptoServiceFactory(_configManager, _mockHttpClient.Object, _loggerFactory);

        // Act
        var firstService = factory.GetService("BTC", "Main");
        var secondService = factory.GetService("btc", "main");

        // Assert
        Assert.Same(firstService, secondService);
        Assert.IsType<BitcoinService>(firstService);
    }

    [Fact]
    public void GetService_RequestBTCServiceTwice_ReturnsSameService()
    {
        // Arrange
        var factory = new CryptoServiceFactory(_configManager, _mockHttpClient.Object, _loggerFactory);

        // Act
        var firstService = factory.GetService("BTC", "Main");
        var secondService = factory.GetService("BTC", "Main");

        // Assert
        Assert.Same(firstService, secondService);
        Assert.IsType<BitcoinService>(firstService);
    }

    [Fact]
    public void GetService_ConcurrentRequests_ReturnsSameInstance()
    {
        // Arrange
        var factory = new CryptoServiceFactory(_configManager, _mockHttpClient.Object, _loggerFactory);
        var services = new ICryptoService[10];

        // Act
        Parallel.For(0, 10, i =>
        {
            services[i] = factory.GetService("BTC", "Main");
        });

        // Assert
        Assert.All(services, service => Assert.Same(services[0], service));
    }
}