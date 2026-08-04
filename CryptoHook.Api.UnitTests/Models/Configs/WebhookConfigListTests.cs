using System.ComponentModel.DataAnnotations;
using CryptoHook.Api.Models.Configs;

namespace CryptoHook.Api.UnitTests.Models.Configs;

public class WebhookConfigListTests
{
    private const string ValidSecret = "a-secret-that-is-long-enough-to-be-accepted";

    private static List<ValidationResult> Validate(WebhookConfigList config)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(config, new ValidationContext(config), results, validateAllProperties: true);
        return results;
    }

    private static WebhookConfigList CreateConfig(params WebhookConfig[] webhooks)
    {
        var config = new WebhookConfigList();
        config.AddRange(webhooks);
        return config;
    }

    [Fact]
    public void Validate_NoSubscribers_Passes()
    {
        // Running without webhooks is a legitimate configuration: the payment endpoints are still
        // pollable.
        Assert.Empty(Validate(CreateConfig()));
    }

    [Fact]
    public void Validate_UrlAndSufficientSecret_Passes()
    {
        var results = Validate(CreateConfig(new WebhookConfig
        {
            Url = new Uri("https://example.com/hooks"),
            Secret = ValidSecret,
        }));

        Assert.Empty(results);
    }

    [Fact]
    public void Validate_ShortSecret_Fails()
    {
        // The whole point of the minimum length: a deployment that kept the placeholder from the
        // sample configuration signs every notification with a value that is public. Validating the
        // list itself is what makes this run at all, since the annotations sit on the elements.
        var results = Validate(CreateConfig(new WebhookConfig
        {
            Url = new Uri("https://example.com/hooks"),
            Secret = "too-short",
        }));

        Assert.Single(results);
        Assert.Contains("at least", results[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_SecretOfExactMinimumLength_Passes()
    {
        var results = Validate(CreateConfig(new WebhookConfig
        {
            Url = new Uri("https://example.com/hooks"),
            Secret = new string('s', WebhookConfig.MinimumSecretLength),
        }));

        Assert.Empty(results);
    }

    [Fact]
    public void Validate_MissingUrl_Fails()
    {
        // Configuration binding does not honour 'required', so a subscriber configured with only a
        // secret arrives here with a null Url and would otherwise take down every payment update
        // when a notification is queued.
        var results = Validate(CreateConfig(new WebhookConfig
        {
            Url = null!,
            Secret = ValidSecret,
        }));

        Assert.Single(results);
        Assert.Contains("Url is required", results[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_MissingSecret_Fails()
    {
        var results = Validate(CreateConfig(new WebhookConfig
        {
            Url = new Uri("https://example.com/hooks"),
            Secret = null!,
        }));

        Assert.Single(results);
        Assert.Contains("Secret is required", results[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RelativeUrl_Fails()
    {
        var results = Validate(CreateConfig(new WebhookConfig
        {
            Url = new Uri("/hooks", UriKind.Relative),
            Secret = ValidSecret,
        }));

        Assert.Single(results);
        Assert.Contains("absolute", results[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ReportsEveryOffendingEntryByIndex()
    {
        var results = Validate(CreateConfig(
            new WebhookConfig { Url = new Uri("https://good.example.com/hooks"), Secret = ValidSecret },
            new WebhookConfig { Url = new Uri("https://bad.example.com/hooks"), Secret = "short" }));

        Assert.Single(results);
        Assert.Contains("Webhooks[1].Secret", results[0].ErrorMessage, StringComparison.Ordinal);
    }
}
