using CryptoHook.Api.Middleware;
using CryptoHook.Api.Models.Configs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.UnitTests.Middleware;

public class ApiKeyRegistryTests
{
    private const string ValidKey = "a-sufficiently-long-api-key";

    private static ApiKeyRegistry CreateRegistry(IOptionsMonitor<ApiKeyConfig> monitor) =>
        new(NullLogger<ApiKeyRegistry>.Instance, monitor);

    private static ApiKeyConfig Config(params string[] keys)
    {
        var config = new ApiKeyConfig();
        config.AddRange(keys);
        return config;
    }

    private sealed class StaticMonitor(ApiKeyConfig config) : IOptionsMonitor<ApiKeyConfig>
    {
        public ApiKeyConfig CurrentValue { get; } = config;

        public ApiKeyConfig Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ApiKeyConfig, string?> listener) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>An operator who edits the section into an invalid state while the process runs.</summary>
    private sealed class InvalidMonitor : IOptionsMonitor<ApiKeyConfig>
    {
        public ApiKeyConfig CurrentValue =>
            throw new OptionsValidationException("", typeof(ApiKeyConfig), ["At least one API key must be configured."]);

        public ApiKeyConfig Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ApiKeyConfig, string?> listener) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public void IsValid_ConfiguredKey_IsAccepted()
    {
        using var registry = CreateRegistry(new StaticMonitor(Config(ValidKey)));

        Assert.True(registry.HasKeys);
        Assert.True(registry.IsValid(ValidKey));
    }

    [Theory]
    [InlineData("wrong-key-entirely")]
    [InlineData("a-sufficiently-long-api-ke")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_AnythingElse_IsRejected(string? presented)
    {
        using var registry = CreateRegistry(new StaticMonitor(Config(ValidKey)));

        Assert.False(registry.IsValid(presented));
    }

    [Fact]
    public void IsValid_NoKeysConfigured_RejectsEverything()
    {
        using var registry = CreateRegistry(new StaticMonitor(Config()));

        Assert.False(registry.HasKeys);
        Assert.False(registry.IsValid(ValidKey));
    }

    [Fact]
    public void Construction_InvalidConfiguration_FailsClosedInsteadOfThrowing()
    {
        // Reading the options runs their validation, so an invalid edit at runtime throws from
        // wherever it is read. Read from the request path, that surfaced as an unhandled 500 with
        // nothing pointing at the configuration; here it becomes an empty key set, which denies
        // every request with the reason logged.
        using var registry = CreateRegistry(new InvalidMonitor());

        Assert.False(registry.HasKeys);
        Assert.False(registry.IsValid(ValidKey));
    }
}
