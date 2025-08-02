
namespace CryptoHook.Api.Models.Configs;

public class AvailableCurrency
{
    public required string Symbol { get; set; }
    public required string Name { get; set; }
    public required string Network { get; set; }

    public override bool Equals(object? obj)
    {
        if (obj is not AvailableCurrency other)
        {

            return false;
        }

        return Symbol.Equals(other.Symbol, StringComparison.OrdinalIgnoreCase) &&
               Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase) &&
               Network.Equals(other.Network, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Symbol.ToUpperInvariant(),
            Name.ToUpperInvariant(),
            Network.ToUpperInvariant());
    }
}
