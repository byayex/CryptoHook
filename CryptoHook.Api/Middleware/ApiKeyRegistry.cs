using System.Security.Cryptography;
using System.Text;
using CryptoHook.Api.Models.Configs;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Middleware;

/// <summary>
/// The configured API keys, as hashes, and the fixed-time check against them.
/// </summary>
/// <remarks>
/// Shared rather than living in <see cref="ApiKeyMiddleware"/>, because the rate limiter has to
/// decide whether a presented key is real before the middleware ever runs: it partitions on the key
/// and runs earlier in the pipeline, so keying on an unvalidated header would let a caller pick a
/// fresh limit for every guess.
///
/// Hashed once per configuration change rather than per request, and the digests are what get
/// compared, so neither the value nor the length of a configured key can leak through the timing of
/// the comparison.
/// </remarks>
public sealed class ApiKeyRegistry : IDisposable
{
    private readonly ILogger<ApiKeyRegistry> _logger;
    private readonly IOptionsMonitor<ApiKeyConfig> _apiKeyConfig;
    private readonly IDisposable? _changeSubscription;

    private volatile byte[][] _hashes = [];

    public ApiKeyRegistry(ILogger<ApiKeyRegistry> logger, IOptionsMonitor<ApiKeyConfig> apiKeyConfig)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiKeyConfig = apiKeyConfig ?? throw new ArgumentNullException(nameof(apiKeyConfig));

        Reload();

        // Rotating a key has to take effect without a restart; this is a singleton, so a snapshot
        // taken here would otherwise live as long as the process.
        _changeSubscription = _apiKeyConfig.OnChange(_ => Reload());
    }

    /// <summary>Whether any usable key is configured. False means every protected request is denied.</summary>
    public bool HasKeys => _hashes.Length > 0;

    public bool IsValid(string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return false;
        }

        var hashes = _hashes;

        if (hashes.Length == 0)
        {
            return false;
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));

        bool isValid = false;
        foreach (var configuredHash in hashes)
        {
            if (CryptographicOperations.FixedTimeEquals(presentedHash, configuredHash))
            {
                // Deliberately no break: the loop has to take the same time whichever key matched.
                isValid = true;
            }
        }

        return isValid;
    }

    private void Reload()
    {
        try
        {
            // Reading CurrentValue runs the options validation, so an operator who edits the
            // 'ApiKeys' section into an invalid state while the process is running gets an
            // exception from here. Swallowed into an empty set on purpose: a reload must fail
            // closed with the reason logged, not surface as an unhandled 500 from whichever
            // request happened to arrive next.
            _hashes = [.. _apiKeyConfig.CurrentValue.Select(key => SHA256.HashData(Encoding.UTF8.GetBytes(key)))];
        }
        catch (OptionsValidationException ex)
        {
            _hashes = [];

            _logger.LogCritical(ex,
                "The 'ApiKeys' configuration is invalid, so no API key will be accepted until it is corrected: {Failures}",
                string.Join("; ", ex.Failures));
        }
    }

    public void Dispose() => _changeSubscription?.Dispose();
}
