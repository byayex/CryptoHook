using System.Numerics;
using CryptoHook.Api.Managers;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace CryptoHook.Api.UnitTests;

public class ConfigManagerTests : IDisposable
{
    private readonly Mock<ILogger<ConfigManager>> _mockLogger;
    private readonly Mock<IOptions<CurrencyConfigList>> _mockOptions;
    private readonly Func<IReadOnlyList<AvailableCurrency>> _originalGetCurrencies;

    public ConfigManagerTests()
    {
        _mockLogger = new Mock<ILogger<ConfigManager>>();
        _mockOptions = new Mock<IOptions<CurrencyConfigList>>();
        _originalGetCurrencies = AvailableCurrencies.GetCurrencies;
    }

    public void Dispose()
    {
        AvailableCurrencies.GetCurrencies = _originalGetCurrencies;
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<AvailableCurrency> CreateTestAvailableCurrencies => new List<AvailableCurrency>
        {
            new() { Symbol = "BTC", Name = "Bitcoin", Network = "Main" },
            new() { Symbol = "ETH", Name = "Ethereum", Network = "Main" }
        }.AsReadOnly();

    private static CurrencyConfigList CreateTestCurrencyConfigList()
    {
        return
        [
            new CurrencyConfig
            {
                Name = "Bitcoin",
                Symbol = "BTC",
                IsEnabled = true,
                InitialPaymentTimeout = 30.0,
                ExtPubKey = "xpub123456789",
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
                Name = "Ethereum",
                Symbol = "ETH",
                IsEnabled = true,
                InitialPaymentTimeout = 15.0,
                ExtPubKey = "xpub987654321",
                Network = "Main",
                Confirmations =
                [
                    new() { Amount = new BigInteger(0), ConfirmationsNeeded = 12 },
                    new() { Amount = new BigInteger(500), ConfirmationsNeeded = 24 }
                ]
            },
            new CurrencyConfig
            {
                Name = "Bitcoin Testnet",
                Symbol = "BTC",
                IsEnabled = false,
                InitialPaymentTimeout = 30.0,
                ExtPubKey = "tpub123456789",
                Network = "Test",
                Confirmations =
                [
                    new() { Amount = new BigInteger(0), ConfirmationsNeeded = 1 }
                ]
            }
        ];
    }

    private ConfigManager CreateConfigManager(CurrencyConfigList? currencyConfigList = null, IReadOnlyList<AvailableCurrency>? availableCurrencies = null)
    {
        currencyConfigList ??= CreateTestCurrencyConfigList();
        availableCurrencies ??= CreateTestAvailableCurrencies;

        _mockOptions.Setup(o => o.Value).Returns(currencyConfigList);
        AvailableCurrencies.GetCurrencies = () => availableCurrencies;

        return new ConfigManager(_mockOptions.Object, _mockLogger.Object);
    }

    [Fact]
    public void Constructor_ValidCurrencyConfigList_InitializesUsableCurrenciesCorrectly()
    {
        // Arrange & Act
        var configManager = CreateConfigManager();
        var usableCurrencies = configManager.GetUsableCurrencies();

        // Assert
        Assert.Equal(2, usableCurrencies.Count);
        Assert.Contains(usableCurrencies, c => c.Symbol == "BTC" && c.Network == "Main");
        Assert.Contains(usableCurrencies, c => c.Symbol == "ETH" && c.Network == "Main");
        Assert.DoesNotContain(usableCurrencies, c => c.Network == "Test");
    }

    [Fact]
    public void Constructor_SortsConfirmationsByAmount()
    {
        // Arrange
        var currencyConfigList = new CurrencyConfigList
        {
            new CurrencyConfig
            {
                Name = "Bitcoin",
                Symbol = "BTC",
                IsEnabled = true,
                InitialPaymentTimeout = 30.0,
                ExtPubKey = "xpub123456789",
                Network = "Main",
                Confirmations =
                [
                    new() { Amount = new BigInteger(10000), ConfirmationsNeeded = 6 },
                    new() { Amount = new BigInteger(0), ConfirmationsNeeded = 1 },
                    new() { Amount = new BigInteger(1000), ConfirmationsNeeded = 3 }
                ]
            }
        };

        // Act
        var configManager = CreateConfigManager(currencyConfigList);

        // Assert
        var btcConfig = currencyConfigList.First(c => c.Symbol == "BTC");
        Assert.Equal(new BigInteger(0), btcConfig.Confirmations[0].Amount);
        Assert.Equal(new BigInteger(1000), btcConfig.Confirmations[1].Amount);
        Assert.Equal(new BigInteger(10000), btcConfig.Confirmations[2].Amount);
    }

    [Fact]
    public void GetCurrencyConfig_ValidSymbolAndNetwork_ReturnsCorrectConfig()
    {
        // Arrange
        var configManager = CreateConfigManager();

        // Act
        var result = configManager.GetCurrencyConfig("BTC", "Main");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Bitcoin", result.Name);
        Assert.Equal("BTC", result.Symbol);
        Assert.Equal("Main", result.Network);
        Assert.True(result.IsEnabled);
    }

    [Fact]
    public void GetCurrencyConfig_CaseInsensitiveSymbol_ReturnsCorrectConfig()
    {
        // Arrange
        var configManager = CreateConfigManager();

        // Act
        var result = configManager.GetCurrencyConfig("btc", "Main");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Bitcoin", result.Name);
        Assert.Equal("BTC", result.Symbol);
        Assert.Equal("Main", result.Network);
        Assert.True(result.IsEnabled);
    }

    [Fact]
    public void GetCurrencyConfig_CaseInsensitiveNetwork_ReturnsCorrectConfig()
    {
        // Arrange
        var configManager = CreateConfigManager();

        // Act
        var result = configManager.GetCurrencyConfig("BTC", "main");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Bitcoin", result.Name);
        Assert.Equal("BTC", result.Symbol);
        Assert.Equal("Main", result.Network);
        Assert.True(result.IsEnabled);
    }

    [Fact]
    public void GetCurrencyConfig_InvalidSymbol_ThrowsInvalidOperationException()
    {
        // Arrange
        var configManager = CreateConfigManager();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            configManager.GetCurrencyConfig("INVALID", "Main"));
    }

    [Fact]
    public void GetCurrencyConfig_InvalidNetwork_ThrowsInvalidOperationException()
    {
        // Arrange
        var configManager = CreateConfigManager();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            configManager.GetCurrencyConfig("BTC", "InvalidNetwork"));
    }

    [Fact]
    public void GetCurrencyConfig_DisabledCurrency_ReturnsConfig()
    {
        var currencyConfigList = new CurrencyConfigList
        {
            new CurrencyConfig
            {
                Name = "Bitcoin",
                Symbol = "BTC",
                IsEnabled = false,
                InitialPaymentTimeout = 30.0,
                ExtPubKey = "xpub123456789",
                Network = "Main",
                Confirmations =
                [
                    new() { Amount = new BigInteger(10000), ConfirmationsNeeded = 6 },
                    new() { Amount = new BigInteger(0), ConfirmationsNeeded = 1 },
                    new() { Amount = new BigInteger(1000), ConfirmationsNeeded = 3 }
                ]
            }
        };
        // Arrange
        var configManager = CreateConfigManager(currencyConfigList);

        // Act
        var result = configManager.GetCurrencyConfig("BTC", "Main");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Bitcoin", result.Name);
        Assert.Equal("BTC", result.Symbol);
        Assert.Equal("Main", result.Network);
        Assert.False(result.IsEnabled);
    }

    [Fact]
    public void UsableCurrencies_OnlyIncludesEnabledCurrencies()
    {
        // Arrange
        var currencyConfigList = CreateTestCurrencyConfigList();
        _mockOptions.Setup(o => o.Value).Returns(currencyConfigList);

        // Act
        var configManager = CreateConfigManager();

        // Assert
        Assert.All(configManager.GetUsableCurrencies(), currency =>
        {
            var config = currencyConfigList.FirstOrDefault(c =>
                c.Symbol == currency.Symbol && c.Network == currency.Network);
            Assert.NotNull(config);
            Assert.True(config.IsEnabled);
        });
    }

    [Fact]
    public void UsableCurrencies_OnlyIncludesCurrenciesFromAvailableCurrenciesService()
    {
        // Arrange
        var currencyConfigList = CreateTestCurrencyConfigList();
        // Only include BTC Main in available currencies (exclude ETH and BTC Test)
        var limitedAvailableCurrencies = new List<AvailableCurrency>
        {
            new() { Symbol = "BTC", Name = "Bitcoin", Network = "Main" }
        }.AsReadOnly();

        // Act
        var configManager = CreateConfigManager(currencyConfigList, limitedAvailableCurrencies);
        var usableCurrencies = configManager.GetUsableCurrencies();

        // Assert
        // Only BTC Main should be usable since it's the only one in both lists and enabled
        Assert.Single(usableCurrencies);
        Assert.Contains(usableCurrencies, c => c.Symbol == "BTC" && c.Network == "Main");
        Assert.DoesNotContain(usableCurrencies, c => c.Symbol == "ETH"); // Not in available currencies
    }

    [Fact]
    public void UsableCurrencies_ExcludesDisabledCurrencies()
    {
        // Arrange
        var currencyConfigList = CreateTestCurrencyConfigList();
        // Include BTC Test in available currencies
        var availableCurrencies = new List<AvailableCurrency>
        {
            new() { Symbol = "BTC", Name = "Bitcoin", Network = "Main" },
            new() { Symbol = "ETH", Name = "Ethereum", Network = "Main" },
            new() { Symbol = "BTC", Name = "Bitcoin Testnet", Network = "Test" }
        }.AsReadOnly();

        // Act
        var configManager = CreateConfigManager(currencyConfigList, availableCurrencies);
        var usableCurrencies = configManager.GetUsableCurrencies();

        // Assert
        // Should only include enabled currencies (BTC Main and ETH Main), not BTC Test which does not exist
        Assert.Equal(2, usableCurrencies.Count);
        Assert.Contains(usableCurrencies, c => c.Symbol == "BTC" && c.Network == "Main");
        Assert.Contains(usableCurrencies, c => c.Symbol == "ETH" && c.Network == "Main");
        Assert.DoesNotContain(usableCurrencies, c => c.Network == "Test"); // Disabled
    }

    [Fact]
    public void Constructor_EmptyCurrencyConfigList_InitializesEmptyUsableCurrencies()
    {
        // Arrange
        var emptyCurrencyConfigList = new CurrencyConfigList();

        // Act
        var configManager = CreateConfigManager(emptyCurrencyConfigList);
        var usableCurrencies = configManager.GetUsableCurrencies();

        // Assert
        Assert.Empty(usableCurrencies);
    }

    [Fact]
    public void Constructor_NullConfirmations_HandledGracefully()
    {
        // Arrange
        var currencyConfigList = new CurrencyConfigList
        {
            new CurrencyConfig
            {
                Name = "Bitcoin",
                Symbol = "BTC",
                IsEnabled = true,
                InitialPaymentTimeout = 30.0,
                ExtPubKey = "xpub123456789",
                Network = "Main",
                Confirmations = new List<Confirmation>()
            }
        };

        // Act & Assert
        var configManager = CreateConfigManager(currencyConfigList);
        Assert.NotNull(configManager);
        Assert.Empty(currencyConfigList.First().Confirmations);
    }
}
