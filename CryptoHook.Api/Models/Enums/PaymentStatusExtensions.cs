namespace CryptoHook.Api.Models.Enums;

public static class PaymentStatusExtensions
{
    public static bool IsSettled(this PaymentStatus status) =>
        status is PaymentStatus.Confirmed or PaymentStatus.OverpaidConfirmed;

    public static bool IsAwaitingConfirmation(this PaymentStatus status) =>
        status is PaymentStatus.Paid or PaymentStatus.Overpaid;

    /// <summary>
    /// How far a status has progressed towards settlement, so that a backwards transition can be
    /// recognised as such.
    /// </summary>
    public static int SettlementRank(this PaymentStatus status) => status switch
    {
        PaymentStatus.Pending => 0,
        PaymentStatus.Expired => 0,
        PaymentStatus.Underpaid => 1,
        PaymentStatus.MultipleTransactions => 1,
        PaymentStatus.Paid => 2,
        PaymentStatus.Overpaid => 2,
        PaymentStatus.Confirmed => 3,
        PaymentStatus.OverpaidConfirmed => 3,
        _ => 0,
    };
}
