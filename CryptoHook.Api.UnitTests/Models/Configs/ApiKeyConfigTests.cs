using System.ComponentModel.DataAnnotations;
using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.UnitTests.Models.Configs;

public class ApiKeyConfigTests
{
    private static List<ValidationResult> Validate(ApiKeyConfig config)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(config, new ValidationContext(config), results, validateAllProperties: true);
        return results;
    }

    private static ApiKeyConfig CreateConfig(params string[] keys)
    {
        var config = new ApiKeyConfig();
        config.AddRange(keys);
        return config;
    }

    [Fact]
    public void Validate_EmptyKeyList_FailsStartupValidation()
    {
        // An empty list must not be accepted: it would leave every protected endpoint
        // unreachable, and used to disable authentication altogether.
        var results = Validate(CreateConfig());

        Assert.Single(results);
        Assert.Contains("At least one API key", results[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_SufficientlyLongKey_Passes()
    {
        var results = Validate(CreateConfig("a-sufficiently-long-api-key"));

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("123456789012345")]
    public void Validate_TooShortKey_Fails(string key)
    {
        var results = Validate(CreateConfig(key));

        Assert.Single(results);
        Assert.Contains("at least", results[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_KeyOfExactMinimumLength_Passes()
    {
        var key = new string('k', ApiKeyConfig.MinimumKeyLength);

        var results = Validate(CreateConfig(key));

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankKey_Fails(string key)
    {
        var results = Validate(CreateConfig(key));

        Assert.Single(results);
        Assert.Contains("must not be empty", results[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DuplicateKeys_Fails()
    {
        var results = Validate(CreateConfig("a-sufficiently-long-api-key", "a-sufficiently-long-api-key"));

        Assert.Single(results);
        Assert.Contains("duplicate", results[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_MultipleValidKeys_Passes()
    {
        var results = Validate(CreateConfig("a-sufficiently-long-api-key", "another-sufficiently-long-key"));

        Assert.Empty(results);
    }
}
