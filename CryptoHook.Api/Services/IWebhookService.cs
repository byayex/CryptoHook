using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Payments;

namespace CryptoHook.Api.Services;

public interface IWebhookService
{
    /// <summary>
    /// Adds the notifications to <paramref name="dbContext"/> without saving, so that the caller can
    /// commit them in the same transaction as the change they describe.
    /// </summary>
    /// <remarks>
    /// Saving the payment first and queueing afterwards leaves a window in which the state is
    /// committed but the notification is not: the next pass sees nothing changed and the merchant is
    /// never told. Call <see cref="SignalDispatch"/> once the transaction has committed.
    /// </remarks>
    void EnqueuePaymentChange(DatabaseContext dbContext, PaymentRequest payload);

    /// <summary>Nudges the dispatcher so a freshly committed notification goes out immediately.</summary>
    void SignalDispatch();

    /// <remarks>
    /// Returns once the notifications are durably recorded, not once they are delivered.
    /// Delivery and retries are the dispatcher's job, so a subscriber that is slow or down
    /// cannot hold up the payment check that produced the event.
    /// </remarks>
    Task NotifyPaymentChange(PaymentRequest payload, CancellationToken cancellationToken = default);
}
