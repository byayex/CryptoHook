using System.Text.Json;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Services;

/// <remarks>
/// Records the notifications owed to subscribers; sending them is
/// <see cref="WebhookDispatchWorker"/>'s job. Writing a notification down before attempting it is
/// what makes delivery survive a subscriber outage, a crash and a restart.
/// </remarks>
public class WebhookService(
    IDbContextFactory<DatabaseContext> dbContextFactory,
    IOptions<WebhookConfigList> webhookConfigs,
    WebhookDispatchSignal dispatchSignal,
    ILogger<WebhookService> logger) : IWebhookService
{
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly WebhookConfigList _webhookConfigs = webhookConfigs.Value;
    private readonly WebhookDispatchSignal _dispatchSignal = dispatchSignal;
    private readonly ILogger<WebhookService> _logger = logger;

    public async Task NotifyPaymentChange(PaymentRequest payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (!Enqueue(dbContext, payload))
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        SignalDispatch();
    }

    public void EnqueuePaymentChange(DatabaseContext dbContext, PaymentRequest payload)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(payload);

        Enqueue(dbContext, payload);
    }

    public void SignalDispatch() => _dispatchSignal.Signal();

    private bool Enqueue(DatabaseContext dbContext, PaymentRequest payload)
    {
        if (_webhookConfigs is null || _webhookConfigs.Count == 0)
        {
            return false;
        }

        // Startup validation rejects an entry without a URL, but not defensively enough to be worth
        // relying on here: this runs inside the payment worker's transaction, and a
        // NullReferenceException would discard the status change it was queued alongside, so every
        // payment would stay where it was and the reason would be one line in a log.
        var usable = _webhookConfigs.Where(webhook => webhook?.Url is not null).ToList();

        if (usable.Count != _webhookConfigs.Count)
        {
            _logger.LogError(
                "{Count} of {Total} configured webhook subscriber(s) have no Url and are being skipped. Payments will not be notified to them.",
                _webhookConfigs.Count - usable.Count, _webhookConfigs.Count);
        }

        if (usable.Count == 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var eventId = Guid.NewGuid();
        var eventType = WebhookEnvelope.EventTypeFor(payload.Status);

        // Serialised once and stored per subscriber, so a retry hours later still describes the
        // state that actually triggered the event rather than whatever the payment looks like by
        // the time the subscriber comes back.
        var body = JsonSerializer.Serialize(new WebhookEnvelope
        {
            EventId = eventId,
            EventType = eventType,
            OccurredAt = now,
            Data = payload,
        });

        // One delivery per distinct subscriber: the same URL configured twice is a configuration
        // mistake, and honouring it would post the same event to that subscriber twice.
        var subscribers = usable
            .DistinctBy(webhook => webhook.Url.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var webhook in subscribers)
        {
            dbContext.WebhookDeliveries.Add(new WebhookDelivery
            {
                Id = Guid.NewGuid(),
                PaymentRequestId = payload.Id,
                Url = webhook.Url.ToString(),
                EventType = eventType,
                Payload = body,
                Status = WebhookDeliveryStatus.Pending,
                AttemptCount = 0,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        _logger.LogInformation("Queued {Count} webhook notification(s) for payment {PaymentId} ({EventType}, event {EventId})",
            subscribers.Count, payload.Id, eventType, eventId);

        return true;
    }
}
