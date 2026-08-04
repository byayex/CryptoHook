using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Attributes;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoHook.Api.Controllers;

/// <remarks>
/// Delivery is at-least-once but not guaranteed: a subscriber that stays down past the retry budget
/// leaves a dead letter behind. This is how an operator finds those and pushes them again.
/// </remarks>
[ApiController]
[Route("api/v{version:apiVersion}/webhook-deliveries")]
[ApiVersion("1.0")]
[RequireApiKey]
public class WebhookDeliveriesController(
    ILogger<WebhookDeliveriesController> logger,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    WebhookDispatchSignal dispatchSignal) : ControllerBase
{
    internal const int MaxPageSize = 200;

    private readonly ILogger<WebhookDeliveriesController> _logger = logger;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly WebhookDispatchSignal _dispatchSignal = dispatchSignal;

    /// <summary>Most recent first.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WebhookDelivery>>> ListDeliveries(
        [FromQuery] Guid? paymentRequestId,
        [FromQuery] WebhookDeliveryStatus? status,
        [FromQuery] int limit = 50)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad Request",
                Detail = $"'limit' must be between 1 and {MaxPageSize}.",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1"
            });
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var query = dbContext.WebhookDeliveries.AsNoTracking().AsQueryable();

        if (paymentRequestId is { } id)
        {
            query = query.Where(delivery => delivery.PaymentRequestId == id);
        }

        if (status is { } deliveryStatus)
        {
            query = query.Where(delivery => delivery.Status == deliveryStatus);
        }

        var deliveries = await query
            .OrderByDescending(delivery => delivery.CreatedAt)
            .Take(limit)
            .ToListAsync();

        return Ok(deliveries);
    }

    /// <summary>Requeues a delivery that was given up on, resetting its attempt budget.</summary>
    [HttpPost]
    [Route("{id}/replay")]
    public async Task<ActionResult<WebhookDelivery>> ReplayDelivery([FromRoute] Guid id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var delivery = await dbContext.WebhookDeliveries.FindAsync(id);

        if (delivery is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Webhook Delivery Not Found",
                Detail = $"Webhook delivery '{id}' not found",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.4"
            });
        }

        if (delivery.Status == WebhookDeliveryStatus.Pending)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = "This delivery is already queued and will be attempted again on its own.",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.8"
            });
        }

        // The stored payload is replayed verbatim, so the subscriber sees the event as it was
        // when it happened. The envelope's event id is unchanged too, which is what lets a
        // subscriber that did receive it recognise the duplicate.
        delivery.Status = WebhookDeliveryStatus.Pending;
        delivery.AttemptCount = 0;
        delivery.NextAttemptAt = DateTime.UtcNow;
        delivery.DeliveredAt = null;
        delivery.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();

        _logger.LogInformation("Webhook delivery {DeliveryId} to {Url} was requeued for replay.", delivery.Id, delivery.Url);

        _dispatchSignal.Signal();

        return Ok(delivery);
    }
}
