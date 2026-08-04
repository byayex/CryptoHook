using CryptoHook.Api.Managers;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.Factory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CryptoHook.Api.UnitTests.Services.CryptoServices.Factory;

public sealed class CryptoServiceFactoryTests : IDisposable
{
    private readonly List<CryptoServiceFactory> _factories = [];
    private readonly ConfigManager _configManager;
    private readonly Mock<IHttpClientFactory> _mockHttpClient;
    private readonly ILoggerFactory _loggerFactory;

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
    }

    public void Dispose() => _factories.ForEach(factory => factory.Dispose());

    private CryptoServiceFactory CreateFactory()
    {
        var factory = new CryptoServiceFactory(
            _configManager, _mockHttpClient.Object, _loggerFactory, TestDefaults.PaymentCheckOptions());

        _factories.Add(factory);
        return factory;
    }

    [Theory]
    [InlineData("UNKNOWN", "Main")]
    [InlineData("BTC", "UNKNOWN")]
    public void GetService_RequestUnknownData_ReturnsNull(string symbol, string network)
    {
        var factory = CreateFactory();

        // Unknown currencies are an expected caller mistake, not a server fault: returning
        // null lets the controller answer 400 and the worker skip a single row.
        Assert.Null(factory.GetService(symbol, network));
    }

    [Theory]
    [InlineData("UNKNOWN", "Bitcoin", "Main")]
    [InlineData("BTC", "Bitcoin", "UNKNOWN")]
    [InlineData("BTC", "UNKNOWN", "Main")]
    public void GetService_RequestUnknownAvailableCurrency_ReturnsNull(string symbol, string name, string network)
    {
        var currency = new AvailableCurrency
        {
            Symbol = symbol,
            Name = name,
            Network = network
        };
        var factory = CreateFactory();

        Assert.Null(factory.GetService(currency));
    }

    [Fact]
    public void GetService_RequestDisabledCurrency_ReturnsNull()
    {
        var factory = CreateFactory();

        Assert.Null(factory.GetService("BTC", "TestNet"));
    }

    [Fact]
    public void GetService_RequestBTCServiceCaseInsensitive_ReturnsCachedService()
    {
        var factory = CreateFactory();

        var firstService = factory.GetService("BTC", "Main");
        var secondService = factory.GetService("btc", "main");

        Assert.Same(firstService, secondService);
        Assert.IsType<BitcoinService>(firstService);
    }

    [Fact]
    public void GetService_RequestBTCServiceTwice_ReturnsSameService()
    {
        var factory = CreateFactory();

        var firstService = factory.GetService("BTC", "Main");
        var secondService = factory.GetService("BTC", "Main");

        Assert.Same(firstService, secondService);
        Assert.IsType<BitcoinService>(firstService);
    }

    [Fact]
    public void GetService_ConcurrentRequests_ReturnsSameInstance()
    {
        var factory = CreateFactory();
        var services = new ICryptoService?[10];

        Parallel.For(0, 10, i =>
        {
            services[i] = factory.GetService("BTC", "Main");
        });

        Assert.All(services, service => Assert.Same(services[0], service));
    }
}