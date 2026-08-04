using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Numerics;

namespace CryptoHook.Api.Models.Configs;

/// <summary>
/// A confirmation requirement that applies from <see cref="Amount"/> upwards. An entry with an
/// amount of zero is required, because it is what every payment below the other thresholds falls
/// back to.
/// </summary>
public class Confirmation
{
    /// <summary>
    /// The largest confirmation requirement that is accepted. Enforced by
    /// <see cref="Attributes.ValidCurrencyAttribute"/> rather than by the annotation below, which
    /// validation never reaches: the list is validated per <see cref="CurrencyConfig"/>, and
    /// <c>Validator.TryValidateObject</c> does not recurse into the items of a collection property.
    /// </summary>
    public const int MaximumConfirmationsNeeded = 1000;

    /// <remarks>
    /// Must be at least one. Accepting a payment at zero confirmations means accepting an
    /// unconfirmed mempool transaction, which the payer can still replace or double spend.
    /// </remarks>
    [Range(1, MaximumConfirmationsNeeded)]
    public required uint ConfirmationsNeeded { get; set; }

    private string? MinAmount { get; set; }

    /// <remarks>
    /// Backed by a string so that an amount in the currency's smallest unit survives
    /// configuration binding, which has no BigInteger. An unparseable value reads as zero rather
    /// than throwing: this getter runs during validation and inside sorting, where an exception
    /// surfaces as an unhandled startup crash instead of the validation message that explains it.
    /// </remarks>
    public required BigInteger Amount
    {
        get => BigInteger.TryParse(MinAmount, CultureInfo.InvariantCulture, out var amount) ? amount : BigInteger.Zero;
        set => MinAmount = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Whether <see cref="Amount"/> holds a usable value, for validation to report on.</summary>
    internal bool HasValidAmount =>
        string.IsNullOrEmpty(MinAmount)
        || (BigInteger.TryParse(MinAmount, CultureInfo.InvariantCulture, out var amount) && amount >= 0);
}
