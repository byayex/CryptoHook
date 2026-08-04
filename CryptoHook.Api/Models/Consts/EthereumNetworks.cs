using System.Collections.Frozen;

namespace CryptoHook.Api.Models.Consts;

/// <remarks>
/// The one place a network name maps to an EIP-155 chain id. The same name also selects currency
/// configuration and, in <see cref="Services.CryptoServices.EthereumService"/>, extended-key
/// prefixes, so keeping one map makes adding a network a single edit that startup validation
/// checks, rather than a runtime failure on the first payment request.
/// </remarks>
public static class EthereumNetworks
{
    private static readonly FrozenDictionary<string, int> ChainIdsByNetwork =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Main"] = 1,
            ["Mainnet"] = 1,
            ["Sepolia"] = 11155111,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> SupportedNetworks => ChainIdsByNetwork.Keys;

    public static bool TryGetChainId(string? network, out int chainId)
    {
        if (string.IsNullOrWhiteSpace(network))
        {
            chainId = 0;
            return false;
        }

        return ChainIdsByNetwork.TryGetValue(network, out chainId);
    }

    public static int GetChainId(string? network)
    {
        return TryGetChainId(network, out var chainId)
            ? chainId
            : throw new InvalidOperationException(
                $"Unsupported Ethereum network '{network}'. Supported networks are {string.Join(", ", SupportedNetworks)}.");
    }
}
