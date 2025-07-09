using System.Collections.Frozen;
using CryptoHook.Api.Models.Config;
using NBitcoin;

namespace CryptoHook.Api.Manager.CryptoManager;

public class BitcoinManager : ICryptoManager
{
    private readonly ExtPubKey _extPubKey;
    private readonly Network _network;
    private readonly ILogger<BitcoinManager> _logger;
    public CurrencyConfig CurrencyConfig { get; }
    public string Symbol => "BTC";

    // BIP84 derivation path for native SegWit addresses: m/0/index
    private const string DerivationPathFormat = "0/{0}";

    public BitcoinManager(ConfigManager configManager, ILogger<BitcoinManager> logger)
    {
        _logger = logger;

        _logger.LogInformation("Initializing BitcoinManager for {Symbol}", Symbol);

        var config = configManager.GetCurrencyConfig(Symbol);
        _logger.LogDebug("Retrieved config for {Symbol}: Network={Network}", Symbol, config.Network);

        var network = Network.GetNetwork(config.Network);

        if (network is null)
        {
            _logger.LogError("Network for {Symbol} is not configured properly. Network value: {NetworkValue}", Symbol, config.Network);
            throw new InvalidOperationException($"Network for {Symbol} is not configured properly.");
        }

        _network = network;
        CurrencyConfig = config;

        try
        {
            _extPubKey = ExtPubKey.Parse(config.ExtPubKey, _network);
            _logger.LogInformation("Successfully initialized BitcoinManager for {Symbol} on {Network}", Symbol, _network.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse ExtPubKey for {Symbol}", Symbol);
            throw;
        }
    }

    public BitcoinAddress GetAddressAtIndex(uint index)
    {
        _logger.LogDebug("Generating address for {Symbol} at index {Index}", Symbol, index);

        try
        {
            // Derives receiving addresses using BIP84 path (m/0/index)
            var derivationPath = new KeyPath(string.Format(DerivationPathFormat, index));
            var childPubKey = _extPubKey.Derive(derivationPath).PubKey;
            var address = childPubKey.GetAddress(ScriptPubKeyType.Segwit, _network);

            _logger.LogDebug("Successfully generated address {Address} for {Symbol} at index {Index}",
                address.ToString(), Symbol, index);

            return address;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate address for {Symbol} at index {Index}", Symbol, index);
            throw;
        }
    }

    public async Task<bool> IsPaymentConfirmed(string address, ulong paymentAmount)
    {
        var neededConfirmations = CurrencyConfig.GetConfirmationsNeeded(Symbol, paymentAmount);
        return true;
    }
}