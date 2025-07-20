using System.ComponentModel.DataAnnotations;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;

namespace CryptoHook.Api.Models.Attributes.Tests;

public class ValidCurrencyAttributeTest
{
    // Helper to create a valid CurrencyConfig
    private static CurrencyConfig CreateValidConfig()
    {
        var available = AvailableCurrencies.GetCurrencies()[0];
        return new CurrencyConfig
        {
            Name = available.Name,
            Symbol = available.Symbol,
            Network = available.Network,
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

    [Fact]
    public void IsValid_ReturnsSuccess_ForValidConfig()
    {
        // Arrange
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        var context = new ValidationContext(config);

        // Act
        var result = attribute.GetValidationResult(config, context);

        // Assert
        Assert.Equal(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_ForInvalidType()
    {
        // Arrange
        var attribute = new ValidCurrencyAttribute();
        var context = new ValidationContext(new object());

        // Act
        var result = attribute.GetValidationResult("not a config", context);

        // Assert
        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_ForUnknownCurrency()
    {
        // Arrange
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        config.Symbol = "FAKE";
        var context = new ValidationContext(config);

        // Act
        var result = attribute.GetValidationResult(config, context);

        // Assert
        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_ForWrongName()
    {
        // Arrange
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        config.Name = "WrongName";
        var context = new ValidationContext(config);

        // Act
        var result = attribute.GetValidationResult(config, context);

        // Assert
        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_WhenConfirmationsIsEmpty()
    {
        // Arrange
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        config.Confirmations = [];
        var context = new ValidationContext(config);

        // Act
        var result = attribute.GetValidationResult(config, context);

        // Assert
        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_WhenNoBaseAmountZero()
    {
        // Arrange
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        config.Confirmations =
            [
                new() { Amount = 5000, ConfirmationsNeeded = 2 }
            ];
        var context = new ValidationContext(config);

        // Act
        var result = attribute.GetValidationResult(config, context);

        // Assert
        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_WhenAmountsAreNotUnique()
    {
        // Arrange
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        config.Confirmations =
            [
                new() { Amount = 0, ConfirmationsNeeded = 1 },
                new() { Amount = 500, ConfirmationsNeeded = 3 },
                new() { Amount = 500, ConfirmationsNeeded = 6 }
            ];
        var context = new ValidationContext(config);

        // Act
        var result = attribute.GetValidationResult(config, context);

        // Assert
        Assert.NotEqual(ValidationResult.Success, result);
    }
}