using System.ComponentModel.DataAnnotations;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;

namespace CryptoHook.Api.UnitTests.Models.Configs;

public class CurrencyConfigListTests : IDisposable
{
    private readonly Func<IReadOnlyList<AvailableCurrency>> _originalGetCurrencies;

    public CurrencyConfigListTests()
    {
        _originalGetCurrencies = AvailableCurrencies.GetCurrencies;
    }

    private static CurrencyConfig CreateValidConfig(string name = "Bitcoin", string symbol = "BTC", string network = "Main")
    {
        return new CurrencyConfig
        {
            Name = name,
            Symbol = symbol,
            Network = network,
            IsEnabled = true,
            InitialPaymentTimeout = 30,
            ExtPubKey = "xpub-test",
            Confirmations =
                [
                    new() { Amount = 0, ConfirmationsNeeded = 1 },
                    new() { Amount = 100000, ConfirmationsNeeded = 3 }
                ]
        };
    }

    public void Dispose()
    {
        AvailableCurrencies.GetCurrencies = _originalGetCurrencies;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Validate_EmptyList_ReturnsNoErrors()
    {
        // Arrange
        var configList = new CurrencyConfigList();
        var context = new ValidationContext(configList);

        // Act
        var results = configList.Validate(context).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void Validate_SingleValidConfig_ReturnsNoErrors()
    {
        // Arrange
        var configList = new CurrencyConfigList
        {
            CreateValidConfig()
        };
        var context = new ValidationContext(configList);

        // Act
        var results = configList.Validate(context).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void Validate_UniqueSymbolNetworkCombinations_ReturnsNoErrors()
    {
        // Arrange
        var configList = new CurrencyConfigList
        {
            CreateValidConfig("Bitcoin", "BTC", "Main"),
            CreateValidConfig("Bitcoin Testnet", "BTC", "TestNet"),
            CreateValidConfig("Ethereum", "ETH", "Main"),
            CreateValidConfig("Litecoin", "LTC", "Main")
        };
        var context = new ValidationContext(configList);
        AvailableCurrencies.GetCurrencies = () =>
        [
            new() { Symbol = "BTC", Name = "Bitcoin", Network = "Main" },
            new() { Symbol = "BTC", Name = "Bitcoin Testnet", Network = "TestNet" },
            new() { Symbol = "ETH", Name = "Ethereum", Network = "Main" },
            new() { Symbol = "LTC", Name = "Litecoin", Network = "Main" }
        ];

        // Act
        var results = configList.Validate(context).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ValidateUniqueSymbolNetworkCombinations_DuplicateSymbolAndNetwork_ReturnsError()
    {
        // Arrange
        var configList = new CurrencyConfigList
        {
            CreateValidConfig("Bitcoin", "BTC", "Main"),
            CreateValidConfig("Bitcoin", "BTC", "Main")
        };
        var context = new ValidationContext(configList);

        // Act
        var results = configList.Validate(context).ToList();

        // Assert
        Assert.NotEmpty(results);
    }

    [Fact]
    public void ValidateUniqueSymbolNetworkCombinations_SameSymbolDifferentNetwork_ReturnsNoError()
    {
        // Arrange
        var configList = new CurrencyConfigList
        {
            CreateValidConfig("Bitcoin", "BTC", "Main"),
            CreateValidConfig("Bitcoin Testnet", "BTC", "TestNet"),
        };
        var context = new ValidationContext(configList);
        AvailableCurrencies.GetCurrencies = () =>
        [
            new() { Symbol = "BTC", Name = "Bitcoin", Network = "Main" },
            new() { Symbol = "BTC", Name = "Bitcoin Testnet", Network = "TestNet" },
        ];

        // Act
        var results = configList.Validate(context).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ValidateUniqueSymbolNetworkCombinations_DifferentSymbolSameNetwork_ReturnsNoError()
    {
        // Arrange
        var configList = new CurrencyConfigList
        {
            CreateValidConfig("Bitcoin", "BTC", "Main"),
            CreateValidConfig("Ethereum", "ETH", "Main"),
            CreateValidConfig("Litecoin", "LTC", "Main")
        };
        var context = new ValidationContext(configList);
        AvailableCurrencies.GetCurrencies = () =>
        [
            new() { Symbol = "BTC", Name = "Bitcoin", Network = "Main" },
            new() { Symbol = "ETH", Name = "Ethereum", Network = "Main" },
            new() { Symbol = "LTC", Name = "Litecoin", Network = "Main" },
        ];

        // Act
        var results = configList.Validate(context).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ValidateUniqueSymbolNetworkCombinations_CaseSensitiveComparison_TreatsNotUnique()
    {
        // Arrange
        var configList = new CurrencyConfigList
        {
            CreateValidConfig("Bitcoin", "BTC", "Main"),
            CreateValidConfig("Bitcoin", "btc", "Main"),
            CreateValidConfig("Bitcoin", "BTC", "main")
        };
        var context = new ValidationContext(configList);

        // Act
        var results = configList.Validate(context).ToList();

        // Assert
        Assert.NotEmpty(results);
    }

    [Fact]
    public void ValidateUniqueSymbolNetworkCombinations_OneConfigIsInvalid_ReturnsError()
    {
        // Arrange
        var invalidConfig = new CurrencyConfig
        {
            Name = "",
            Symbol = "BTC",
            Network = "Main",
            IsEnabled = true,
            InitialPaymentTimeout = 30,
            ExtPubKey = "xpub-test",
            Confirmations =
                [
                    new() { Amount = 0, ConfirmationsNeeded = 1 }
                ]
        };
        var configList = new CurrencyConfigList
        {
            invalidConfig,
            CreateValidConfig("Bitcoin", "BTC", "Main")
        };
        var context = new ValidationContext(configList);

        // Act
        var results = configList.Validate(context).ToList();

        // Assert
        Assert.True(results.Count >= 2);
    }
}
