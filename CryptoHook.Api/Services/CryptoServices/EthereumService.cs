using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using NBitcoin;
using Nethereum.Util;

namespace CryptoHook.Api.Services.CryptoServices;

public class EthereumService : CryptoServiceBase
{
    private readonly ExtPubKey _extPubKey;
    private readonly ILogger<EthereumService> _logger;
    public override string Symbol => "ETH";

    public EthereumService(
        CurrencyConfig currencyConfig,
        ILogger<EthereumService> logger,
        ICryptoDataProvider dataProvider,
        PaymentCheckConfig paymentCheckConfig)
        : base(currencyConfig, logger, dataProvider, paymentCheckConfig)
    {
        ArgumentNullException.ThrowIfNull(currencyConfig);

        _logger = logger;

        _logger.LogInformation("Initializing EthereumService for {Symbol}", Symbol);

        // Ethereum addresses do not depend on a network, but NBitcoin needs one to decode the
        // extended public key. Networks NBitcoin does not know (Sepolia, ...) fall back to mainnet,
        // so the configured key must carry an xpub prefix.
        var network = Network.GetNetwork(currencyConfig.Network);

        if (network is null)
        {
            _logger.LogInformation(
                "NBitcoin does not know network {NetworkValue}; parsing the ExtPubKey for {Symbol} with mainnet key prefixes.",
                currencyConfig.Network, Symbol);
            network = Network.Main;
        }

        try
        {
            _extPubKey = ExtPubKey.Parse(currencyConfig.ExtPubKey, network);
            _logger.LogInformation("Successfully initialized EthereumService for {Symbol} on {Network}", Symbol, currencyConfig.Network);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to parse ExtPubKey for {Symbol}", Symbol);
            throw;
        }
    }

    public override string GetAddressAtIndex(uint index)
    {
        _logger.LogDebug("Generating address for {Symbol} at index {Index}", Symbol, index);

        CryptoServiceBase.EnsureNonHardenedIndex(index);

        try
        {
            // m/0/index. Hardened derivation (44'/60'/0'/0) needs the master private key, which a
            // watch-only extended public key does not have.
            var derivationPath = new KeyPath($"0/{index}");
            var derivedPubKey = _extPubKey.Derive(derivationPath).PubKey;

            var ethereumAddress = GenerateEthereumAddress(derivedPubKey);

            _logger.LogDebug("Successfully generated address {Address} for {Symbol} at index {Index}",
                ethereumAddress, Symbol, index);

            return ethereumAddress;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate address for {Symbol} at index {Index}", Symbol, index);
            throw;
        }
    }

    /// <summary>
    /// An Ethereum address is the last 20 bytes of the Keccak-256 hash of the uncompressed public
    /// key with its 0x04 prefix stripped.
    /// </summary>
    private static string GenerateEthereumAddress(PubKey publicKey)
    {
        var publicKeyBytes = publicKey.ToBytes(false)[1..];

        var hashBytes = new Sha3Keccack().CalculateHash(publicKeyBytes);

        return "0x" + Convert.ToHexStringLower(hashBytes[^20..]);
    }
}
