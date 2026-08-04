using System.Globalization;
using System.Text;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using NBitcoin;

namespace CryptoHook.Api.Services.CryptoServices;

public class BitcoinService : CryptoServiceBase
{
    private readonly ExtPubKey _extPubKey;
    private readonly Network _network;
    private readonly ILogger<BitcoinService> _logger;
    public override string Symbol => "BTC";

    // The receive chain of a BIP84 account, relative to the account level key (m/84'/0'/0') that
    // the configured ExtPubKey is expected to be.
    private static readonly CompositeFormat DerivationPathFormat = CompositeFormat.Parse("0/{0}");

    public BitcoinService(
        CurrencyConfig currencyConfig,
        ILogger<BitcoinService> logger,
        ICryptoDataProvider dataProvider,
        PaymentCheckConfig paymentCheckConfig)
        : base(currencyConfig, logger, dataProvider, paymentCheckConfig)
    {
        ArgumentNullException.ThrowIfNull(currencyConfig);

        _logger = logger;

        _logger.LogInformation("Initializing BitcoinManager for {Symbol}", Symbol);

        var network = Network.GetNetwork(currencyConfig.Network);

        if (network is null)
        {
            _logger.LogError("Network for {Symbol} is not configured properly. Network value: {NetworkValue}", Symbol, currencyConfig.Network);
            throw new InvalidOperationException($"Network for {Symbol} is not configured properly.");
        }

        _network = network;

        try
        {
            _extPubKey = ExtPubKey.Parse(currencyConfig.ExtPubKey, _network);
            _logger.LogInformation("Successfully initialized BitcoinManager for {Symbol} on {Network}", Symbol, _network.Name);
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

        EnsureNonHardenedIndex(index);

        try
        {
            var derivationPath = new KeyPath(string.Format(CultureInfo.InvariantCulture, DerivationPathFormat, index));
            var childPubKey = _extPubKey.Derive(derivationPath).PubKey;
            var address = childPubKey.GetAddress(ScriptPubKeyType.Segwit, _network);

            _logger.LogDebug("Successfully generated address {Address} for {Symbol} at index {Index}",
                address.ToString(), Symbol, index);

            return address.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate address for {Symbol} at index {Index}", Symbol, index);
            throw;
        }
    }
}
