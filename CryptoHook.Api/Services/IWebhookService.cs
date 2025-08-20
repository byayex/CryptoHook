using CryptoHook.Api.Models.Payments;

namespace CryptoHook.Api.Services;

public interface IWebhookService
{
    Task NotifyPaymentChange(PaymentRequest payload);
}