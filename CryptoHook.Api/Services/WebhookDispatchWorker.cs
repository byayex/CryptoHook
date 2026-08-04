using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Services;

/// <remarks>
/// Delivers queued webhook notifications, retrying with exponential backoff until a subscriber
/// accepts them or the attempt budget runs out.
///
/// At-least-once is the only guarantee a retrying sender can offer, so subscribers are expected to
/// deduplicate. The keys that are stable across attempts are the envelope's <c>EventId</c> and the
/// <c>X-Delivery-ID</c> header; <c>X-Request-ID</c> is per attempt, because it is part of what the
/// signature covers.
/// </remarks>
public class WebhookDispatchWorker(
    ILogger<WebhookDispatchWorker> logger,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<WebhookConfigList> webhookConfigs,
    IOptions<WebhookDeliveryConfig> deliveryConfig,
    WebhookDispatchSignal dispatchSignal) : BackgroundService
{
    /// <summary>
    /// How much of a failure description is kept. Enough to identify the problem, bounded so a
    /// verbose subscriber cannot bloat the table.
    /// </summary>
    internal const int MaxStoredErrorLength = 500;

    private readonly ILogger<WebhookDispatchWorker> _logger = logger;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly WebhookConfigList _webhookConfigs = webhookConfigs.Value;
    private readonly WebhookDeliveryConfig _config = deliveryConfig.Value;

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The signal is a container-owned singleton shared with WebhookService; its lifetime is the application's, not this worker's.")]
    private readonly WebhookDispatchSignal _dispatchSignal = dispatchSignal;

    private DateTime _lastPruneUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookDispatchWorker running.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchDueDeliveries(stoppingToken).ConfigureAwait(false);
                await PruneIfDue(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while dispatching webhook notifications.");
            }

            try
            {
                // Woken early by a freshly queued notification; the timeout still covers retries
                // that simply came due.
                await _dispatchSignal.WaitAsync(_config.DispatchInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task DispatchDueDeliveries(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = await ClaimDueDeliveries(stoppingToken).ConfigureAwait(false);

            if (claimed.Count == 0)
            {
                return;
            }

            foreach (var deliveryId in claimed)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                await DeliverClaimed(deliveryId, stoppingToken).ConfigureAwait(false);
            }

            // A full batch means there is probably more waiting; keep going rather than sleeping.
            if (claimed.Count < _config.BatchSize)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Takes a lease on the next batch of due deliveries by pushing their next attempt out.
    /// </summary>
    /// <remarks>
    /// Selecting rows and only writing them back after the POST means an overlapping pass, or a
    /// second instance, picks up the same rows and posts them again. Leasing first narrows that to
    /// the case where the lease actually expires mid-send, and a crash still resolves itself
    /// because the lease is a timestamp rather than a flag that needs cleaning up.
    /// </remarks>
    private async Task<List<Guid>> ClaimDueDeliveries(CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;
        var leaseUntil = now + _config.SendTimeout + TimeSpan.FromMinutes(1);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);

        var due = await dbContext.WebhookDeliveries
            .Where(delivery => delivery.Status == WebhookDeliveryStatus.Pending
                               && delivery.NextAttemptAt != null
                               && delivery.NextAttemptAt <= now)
            .OrderBy(delivery => delivery.NextAttemptAt)
            .Take(_config.BatchSize)
            .ToListAsync(stoppingToken)
            .ConfigureAwait(false);

        if (due.Count == 0)
        {
            return [];
        }

        foreach (var delivery in due)
        {
            delivery.NextAttemptAt = leaseUntil;
        }

        await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

        return [.. due.Select(delivery => delivery.Id)];
    }

    /// <remarks>
    /// One short-lived context per delivery, and the result is written down as soon as the attempt
    /// finishes. Holding one context across a whole batch of sends keeps a connection and a set of
    /// unflushed changes open for as long as the slowest subscriber takes, and loses the bookkeeping
    /// for every already-posted delivery in the batch if the process stops before the save.
    /// </remarks>
    private async Task DeliverClaimed(Guid deliveryId, CancellationToken stoppingToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);

        var delivery = await dbContext.WebhookDeliveries.FindAsync([deliveryId], stoppingToken).ConfigureAwait(false);

        if (delivery is null)
        {
            return;
        }

        // One bad subscriber must not stop the rest of the queue from draining.
        try
        {
            await AttemptDelivery(delivery, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The attempt may already have reached the subscriber, so what was recorded about it
            // still has to be saved below.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure while delivering webhook {DeliveryId} to {Url}", delivery.Id, delivery.Url);
            RecordFailure(delivery, ex.Message);
        }

        // Saved without the stopping token on purpose: an attempt that has already been made must
        // be recorded even while the application is shutting down, or it is made again on restart.
        await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task AttemptDelivery(WebhookDelivery delivery, CancellationToken stoppingToken)
    {
        var secret = ResolveSecret(delivery.Url);

        if (secret is null)
        {
            // Signing with the wrong secret, or with none, would be worse than admitting the
            // notification is undeliverable.
            _logger.LogWarning(
                "Webhook {DeliveryId} targets {Url}, which is no longer configured. Marking it as failed.",
                delivery.Id, delivery.Url);
            delivery.Status = WebhookDeliveryStatus.Failed;
            delivery.NextAttemptAt = null;
            delivery.LastError = "The target URL is no longer present in the webhook configuration.";
            delivery.UpdatedAt = DateTime.UtcNow;
            return;
        }

        delivery.AttemptCount++;

        // Signed at send time, not at enqueue time: the timestamp is what protects the subscriber
        // against replay, and a retry hours later carrying the original timestamp would be rejected
        // by any subscriber that checks its freshness.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var requestId = Guid.NewGuid().ToString();
        var signature = GenerateHmacSignature(delivery.Payload, timestamp, requestId, secret);

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = _config.SendTimeout;

        using var content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(delivery.Url)) { Content = content };
        request.Headers.Add("X-Signature", $"sha256={signature}");
        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Request-ID", requestId);
        request.Headers.Add("X-Event-Type", delivery.EventType);
        request.Headers.Add("X-Delivery-ID", delivery.Id.ToString());
        request.Headers.Add("X-Delivery-Attempt", delivery.AttemptCount.ToString(CultureInfo.InvariantCulture));

        try
        {
            using var response = await httpClient.SendAsync(request, stoppingToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                delivery.Status = WebhookDeliveryStatus.Delivered;
                delivery.DeliveredAt = DateTime.UtcNow;
                delivery.NextAttemptAt = null;
                delivery.LastError = null;
                delivery.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation("Delivered webhook {DeliveryId} ({EventType}) to {Url} on attempt {Attempt}",
                    delivery.Id, delivery.EventType, delivery.Url, delivery.AttemptCount);
                return;
            }

            RecordFailure(delivery, $"The subscriber answered {(int)response.StatusCode} {response.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            RecordFailure(delivery, ex.Message);
        }
        catch (TaskCanceledException ex) when (!stoppingToken.IsCancellationRequested)
        {
            // A send timeout surfaces as TaskCanceledException rather than a timeout exception.
            RecordFailure(delivery, $"The request timed out after {_config.SendTimeout.TotalSeconds:0} seconds. {ex.Message}");
        }
    }

    private void RecordFailure(WebhookDelivery delivery, string error)
    {
        delivery.LastError = error.Length > MaxStoredErrorLength ? error[..MaxStoredErrorLength] : error;
        delivery.UpdatedAt = DateTime.UtcNow;

        if (delivery.AttemptCount >= _config.MaxAttempts)
        {
            delivery.Status = WebhookDeliveryStatus.Failed;
            delivery.NextAttemptAt = null;

            // Left in the table on purpose. A dropped notification nobody can see is how a merchant
            // ends up not knowing a payment settled; a dead letter can be found and replayed.
            _logger.LogError(
                "Giving up on webhook {DeliveryId} ({EventType}) to {Url} after {Attempts} attempts. Last error: {Error}. " +
                "It is kept as a dead letter and can be replayed once the subscriber is healthy.",
                delivery.Id, delivery.EventType, delivery.Url, delivery.AttemptCount, delivery.LastError);
            return;
        }

        var backoff = _config.BackoffFor(delivery.AttemptCount);
        delivery.NextAttemptAt = DateTime.UtcNow + backoff;

        _logger.LogWarning(
            "Webhook {DeliveryId} to {Url} failed on attempt {Attempt}/{MaxAttempts}: {Error}. Retrying in {Backoff}.",
            delivery.Id, delivery.Url, delivery.AttemptCount, _config.MaxAttempts, delivery.LastError, backoff);
    }

    /// <remarks>
    /// Scheme and host are compared case insensitively, the path and query exactly, because that is
    /// how URLs actually compare. Matching the whole string case insensitively lets two subscribers
    /// that differ only in the case of their path resolve to each other's secret.
    /// </remarks>
    private string? ResolveSecret(string url)
    {
        if (_webhookConfigs is null || !Uri.TryCreate(url, UriKind.Absolute, out var target))
        {
            return null;
        }

        return _webhookConfigs.FirstOrDefault(webhook =>
            string.Equals(webhook.Url.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(webhook.Url.Authority, target.Authority, StringComparison.OrdinalIgnoreCase)
            && string.Equals(webhook.Url.PathAndQuery, target.PathAndQuery, StringComparison.Ordinal))?.Secret;
    }

    private async Task PruneIfDue(CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;

        if (now - _lastPruneUtc < TimeSpan.FromHours(1))
        {
            return;
        }

        var cutoff = now - _config.Retention;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);

        var removed = await dbContext.WebhookDeliveries
            .Where(delivery => delivery.Status != WebhookDeliveryStatus.Pending && delivery.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(stoppingToken)
            .ConfigureAwait(false);

        // Stamped only once the prune has actually run, so that a failure is retried next pass
        // rather than suppressed for an hour.
        _lastPruneUtc = now;

        if (removed > 0)
        {
            _logger.LogInformation("Pruned {Count} webhook delivery record(s) older than {Retention}.", removed, _config.Retention);
        }
    }

    /// <param name="timestamp">Unix seconds, which is what makes a captured request unreplayable.</param>
    /// <returns>
    /// Lower-case hex, matching the convention used by common webhook verification helpers.
    /// </returns>
    internal static string GenerateHmacSignature(string payload, string timestamp, string requestId, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);

        var signaturePayload = $"{timestamp}.{requestId}.{payload}";
        var payloadBytes = Encoding.UTF8.GetBytes(signaturePayload);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}
