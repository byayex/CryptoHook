using CryptoHook.Api.UnitTests;
using System.ComponentModel.DataAnnotations;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;

namespace CryptoHook.Api.Models.Attributes.Tests;

[Collection(AvailableCurrenciesCollection.Name)]
public class ValidCurrencyAttributeTest
{
    // Helper to create a valid CurrencyConfig
    private static CurrencyConfig CreateValidConfig(List<Confirmation>? confirmations = null)
    {
        var available = AvailableCurrencies.GetCurrencies()[0];
        var defaultConfirmations = new List<Confirmation>
        {
            new() { Amount = 0, ConfirmationsNeeded = 1 },
            new() { Amount = 100000, ConfirmationsNeeded = 3 }
        };

        return new CurrencyConfig
        {
            Name = available.Name,
            Symbol = available.Symbol,
            Network = available.Network,
            IsEnabled = true,
            InitialPaymentTimeout = 30,
            ExtPubKey = "xpub-test",
            Confirmations = confirmations ?? defaultConfirmations
        };
    }

    [Fact]
    public void IsValid_ReturnsError_ForZeroConfirmations()
    {
        // A rule of zero confirmations settles a payment straight out of the mempool, where the
        // payer can still replace or double spend it. Refusing the configuration is the only
        // point at which this is still cheap to catch.
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig([new() { Amount = 0, ConfirmationsNeeded = 0 }]);
        var context = new ValidationContext(config);

        var result = attribute.GetValidationResult(config, context);

        Assert.NotEqual(ValidationResult.Success, result);
        Assert.Contains("at least 1", result!.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void IsValid_ReturnsError_ForAnUnreachableConfirmationCount()
    {
        // The [Range] on Confirmation.ConfirmationsNeeded is never evaluated, because validation of
        // the currency does not recurse into the items of its Confirmations property. Without a check
        // here, a requirement no chain will ever reach binds cleanly and every payment for that
        // currency then sits at Paid for good.
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig(
            [new() { Amount = 0, ConfirmationsNeeded = Confirmation.MaximumConfirmationsNeeded + 1 }]);
        var context = new ValidationContext(config);

        var result = attribute.GetValidationResult(config, context);

        Assert.NotEqual(ValidationResult.Success, result);
        Assert.Contains("must not exceed", result!.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void IsValid_ReturnsSuccess_ForTheHighestAllowedConfirmationCount()
    {
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig(
            [new() { Amount = 0, ConfirmationsNeeded = Confirmation.MaximumConfirmationsNeeded }]);
        var context = new ValidationContext(config);

        Assert.Equal(ValidationResult.Success, attribute.GetValidationResult(config, context));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void IsValid_ReturnsError_ForAnUnusableAmountTolerance(string tolerance)
    {
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        config.AmountTolerance = tolerance;
        var context = new ValidationContext(config);

        var result = attribute.GetValidationResult(config, context);

        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_ForAnEthereumNetworkWithNoChainId()
    {
        // Resolving the chain id here turns an unsupported network into a failed startup rather
        // than a 500 on the first payment check for that currency.
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        config.Name = "Ethereum";
        config.Symbol = "ETH";
        config.Network = "Goerli";
        var context = new ValidationContext(config);

        var result = attribute.GetValidationResult(config, context);

        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsSuccess_ForValidConfig()
    {
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        var context = new ValidationContext(config);

        var result = attribute.GetValidationResult(config, context);

        Assert.Equal(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_ForInvalidType()
    {
        var attribute = new ValidCurrencyAttribute();
        var context = new ValidationContext(new object());

        var result = attribute.GetValidationResult("not a config", context);

        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_ForUnknownCurrency()
    {
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        config.Symbol = "FAKE";
        var context = new ValidationContext(config);

        var result = attribute.GetValidationResult(config, context);

        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_ForWrongName()
    {
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig();
        config.Name = "WrongName";
        var context = new ValidationContext(config);

        var result = attribute.GetValidationResult(config, context);

        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_WhenConfirmationsIsEmpty()
    {
        var attribute = new ValidCurrencyAttribute();
        var config = CreateValidConfig([]);
        var context = new ValidationContext(config);

        var result = attribute.GetValidationResult(config, context);

        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_WhenNoBaseAmountZero()
    {
        var attribute = new ValidCurrencyAttribute();
        var confirmations = new List<Confirmation>
        {
            new() { Amount = 5000, ConfirmationsNeeded = 2 }
        };
        var config = CreateValidConfig(confirmations);
        var context = new ValidationContext(config);

        var result = attribute.GetValidationResult(config, context);

        Assert.NotEqual(ValidationResult.Success, result);
    }

    [Fact]
    public void IsValid_ReturnsError_WhenAmountsAreNotUnique()
    {
        var attribute = new ValidCurrencyAttribute();
        var confirmations = new List<Confirmation>
            {
                new() { Amount = 0, ConfirmationsNeeded = 1 },
                new() { Amount = 500, ConfirmationsNeeded = 3 },
                new() { Amount = 500, ConfirmationsNeeded = 6 }
            };
        var config = CreateValidConfig(confirmations);
        var context = new ValidationContext(config);

        var result = attribute.GetValidationResult(config, context);

        Assert.NotEqual(ValidationResult.Success, result);
    }
}