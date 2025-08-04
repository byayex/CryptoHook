using CryptoHook.Api.Models.Configs;
using Newtonsoft.Json.Bson;

namespace CryptoHook.Api.UnitTests.Models.Configs;

public class CurrencyConfigTests : IDisposable
{

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
                    new() { Amount = 100000, ConfirmationsNeeded = 3 },
                    new() { Amount = 500000, ConfirmationsNeeded = 9 }
                ]
        };
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetConfirmationsNeeded_ValidConfig_ReturnsExpectedValue()
    {
        // Arrange
        var config = CreateValidConfig();

        // Act
        var result = config.GetConfirmationsNeeded(5000);

        // Assert
        Assert.Equal(1u, result);
    }

    [Fact]
    public void GetConfirmationsNeeded_NegativNumber_ThrowsException()
    {
        // Arrange
        var config = CreateValidConfig();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => config.GetConfirmationsNeeded(-1));
    }

    [Fact]
    public void GetConfirmationsNeeded_MiddleNumber_ReturnsExpectedValue()
    {
        // Arrange
        var config = CreateValidConfig();

        // Act
        var result = config.GetConfirmationsNeeded(250000);

        // Assert
        Assert.Equal(3u, result);
    }

    [Fact]
    public void GetConfirmationsNeeded_HighNumber_ReturnsExpectedValue()
    {
        // Arrange
        var config = CreateValidConfig();

        // Act
        var result = config.GetConfirmationsNeeded(600000);

        // Assert
        Assert.Equal(9u, result);
    }

    [Fact]
    public void GetConfirmationsNeeded_EmptyList_ThrowsException()
    {
        // Arrange
        var config = CreateValidConfig();
        config.Confirmations = [];

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => config.GetConfirmationsNeeded(5000));
    }
}
