using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.Factory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Services;

public class PaymentCheckWorker(
    ILogger<PaymentCheckWorker> logger,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    ICryptoServiceFactory cryptoServiceFactory,
    IWebhookService webhookService,
    IOptions<PaymentCheckConfig> paymentCheckConfig) : BackgroundService
{
    private readonly ILogger<PaymentCheckWorker> _logger = logger;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly ICryptoServiceFactory _cryptoServiceFactory = cryptoServiceFactory;
    private readonly IWebhookService _webhookService = webhookService;
    private readonly PaymentCheckConfig _config = paymentCheckConfig.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PaymentCheckWorker running every {Interval} with up to {Concurrency} concurrent checks.",
            _config.CheckInterval, _config.MaxConcurrentChecks);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckPayments(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while checking payments.");
            }

            _logger.LogDebug("Next check in {Delay}.", _config.CheckInterval);

            try
            {
                await Task.Delay(_config.CheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task CheckPayments(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Checking for payments that are not in a final state.");

        await AbandonStalePayments(stoppingToken).ConfigureAwait(false);

        List<PaymentRequest> paymentsToCheck;

        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false))
        {
            // Read untracked, and reload each payment on its own context when updating it: a
            // DbContext is not thread safe, so checks cannot run concurrently while sharing one.
            paymentsToCheck = await WatchedPayments(dbContext.PaymentRequests.AsNoTracking(), DateTime.UtcNow, stoppingToken)
                .ConfigureAwait(false);
        }

        if (paymentsToCheck.Count == 0)
        {
            _logger.LogInformation("No payments to check.");
            return;
        }

        var paymentsByCurrencyAndNetwork = paymentsToCheck.GroupBy(p => new { p.CurrencySymbol, p.Network });

        using var concurrencyGate = new SemaphoreSlim(_config.MaxConcurrentChecks, _config.MaxConcurrentChecks);
        var checks = new List<Task>();

        foreach (var group in paymentsByCurrencyAndNetwork)
        {
            var cryptoService = _cryptoServiceFactory.GetService(group.Key.CurrencySymbol, group.Key.Network);

            if (cryptoService is null)
            {
                _logger.LogError("No crypto service found for currency {CurrencySymbol} on network {Network}", group.Key.CurrencySymbol, group.Key.Network);
                continue;
            }

            _logger.LogInformation("Checking {Count} payments for {CurrencySymbol} on {Network}", group.Count(), group.Key.CurrencySymbol, group.Key.Network);

            foreach (var request in group)
            {
                checks.Add(CheckWithConcurrencyLimit(concurrencyGate, cryptoService, request, stoppingToken));
            }
        }

        await Task.WhenAll(checks).ConfigureAwait(false);
    }

    /// <summary>
    /// Narrows the set of payments the worker polls.
    /// </summary>
    /// <remarks>
    /// Three different lifetimes are at play, and collapsing them into one rule either drops
    /// payments that are still in flight or polls dead ones forever.
    /// <list type="bullet">
    /// <item>Funds on chain but not yet confirmed: always polled. Confirmations must be allowed to
    /// accumulate however long they take, expiry or not.</item>
    /// <item>Nothing received, or not enough: polled until the grace period after expiry runs out,
    /// which is what keeps a dusted address from being re-polled forever.</item>
    /// <item>Settled: polled for a short window afterwards, so that a chain reorganisation undoing
    /// the payment does not go unnoticed.</item>
    /// </list>
    /// Each lifetime is its own query rather than one chain of ORs, because SQLite will not satisfy
    /// an OR across different columns from the indexes on Status, (Status, ExpiresAt) and
    /// (Status, SettledAt): a single predicate turns every pass into a full table scan that grows
    /// with all historical payments.
    /// </remarks>
    internal async Task<List<PaymentRequest>> WatchedPayments(
        IQueryable<PaymentRequest> payments,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var abandonCutoff = now - _config.AbandonAfterExpiry - _config.AbandonSweepGrace;
        var reorgCutoff = now - _config.ReorgWatch;

        var awaitingConfirmation = await payments
            .Where(p => p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Overpaid)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var awaitingFunds = await payments
            .Where(p => (p.Status == PaymentStatus.Pending
                         || p.Status == PaymentStatus.Underpaid
                         || p.Status == PaymentStatus.MultipleTransactions
                         || p.Status == PaymentStatus.Expired)
                        && p.ExpiresAt > abandonCutoff)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var withinReorgWatch = await payments
            .Where(p => (p.Status == PaymentStatus.Confirmed || p.Status == PaymentStatus.OverpaidConfirmed)
                        && p.SettledAt != null
                        && p.SettledAt > reorgCutoff)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Least recently updated first. The explorer rate limit means the back of the queue may not
        // be reached within a pass at all, and in a stable order that would always be the same
        // payments: whoever waited longest goes first instead, so a shortfall of capacity delays
        // every payment a little rather than starving a few of them completely.
        List<PaymentRequest> watched = [.. awaitingConfirmation, .. awaitingFunds, .. withinReorgWatch];

        return [.. watched.OrderBy(p => p.UpdatedAt)];
    }

    /// <summary>
    /// Gives a terminal status to requests that are past the point of being watched, without
    /// spending an explorer request on them.
    /// </summary>
    /// <remarks>
    /// The window in which a request is polled and the window in which it qualifies as abandoned
    /// overlap by only <see cref="PaymentCheckConfig.AbandonSweepGrace"/>, so a worker that is down,
    /// or a database that is unreachable, for longer than that leaves an underpaid request stuck:
    /// it drops out of the watch set without ever having been abandoned, so it keeps a non-terminal
    /// status for good and the merchant is never told. Sweeping from the database rather than from
    /// the poll makes the two windows impossible to miss regardless of how long the gap was, and
    /// costs nothing per request, which is what the bounded watch set was protecting.
    /// </remarks>
    internal async Task AbandonStalePayments(CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;
        var abandonBefore = now - _config.AbandonAfterExpiry;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);

        // Paid and Overpaid are excluded on purpose: funds are on chain and confirmations must be
        // allowed to accumulate for as long as they take.
        var stale = await dbContext.PaymentRequests
            .Where(p => (p.Status == PaymentStatus.Pending
                         || p.Status == PaymentStatus.Underpaid
                         || p.Status == PaymentStatus.MultipleTransactions)
                        && p.ExpiresAt < abandonBefore)
            .ToListAsync(stoppingToken)
            .ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var payment in stale)
        {
            _logger.LogWarning(
                "Abandoning payment {PaymentId} for {CurrencySymbol} on {Network}: it expired at {ExpiresAt} and was last seen as {Status} with {AmountPaid} of {Expected} received. Any received funds need to be refunded out of band.",
                payment.Id, payment.CurrencySymbol, payment.Network, payment.ExpiresAt, payment.Status,
                payment.AmountPaid, payment.AmountExpected);

            payment.Status = PaymentStatus.Expired;
            payment.UpdatedAt = now;

            _webhookService.EnqueuePaymentChange(dbContext, payment);
        }

        await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

        _webhookService.SignalDispatch();
    }

    private async Task CheckWithConcurrencyLimit(
        SemaphoreSlim concurrencyGate,
        ICryptoService cryptoService,
        PaymentRequest request,
        CancellationToken stoppingToken)
    {
        await concurrencyGate.WaitAsync(stoppingToken).ConfigureAwait(false);

        // A check that hangs holds a concurrency slot, and enough of them stall the pass
        // indefinitely. HttpClient's default timeout of 100 seconds applies per request, not to the
        // check as a whole, so the deadline is imposed here rather than left to the transport.
        using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        checkTimeout.CancelAfter(_config.CheckTimeout);

        // One failing payment must not cost us the updates of all the others, so
        // every payment is checked, persisted and notified on its own. The filter keys off the
        // token rather than the exception type on purpose: a subscriber timeout also surfaces as
        // a TaskCanceledException, and letting that through would abort the whole cycle over one
        // slow webhook.
        try
        {
            await CheckSinglePayment(cryptoService, request, checkTimeout.Token, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Checking payment {PaymentId} for {CurrencySymbol} on {Network} exceeded {Deadline} and was abandoned for this pass.",
                request.Id, request.CurrencySymbol, request.Network, _config.CheckTimeout);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to check payment {PaymentId} for {CurrencySymbol} on {Network}",
                request.Id, request.CurrencySymbol, request.Network);
        }
        finally
        {
            concurrencyGate.Release();
        }
    }

    /// <param name="readToken">
    /// Bounds the chain read only. It carries the per-check deadline, which has nothing to say about
    /// how long the write may take.
    /// </param>
    /// <param name="stoppingToken">
    /// Bounds the write. Persisting what the read established must not be cancelled just because the
    /// read used up most of its budget getting there: the work would be thrown away, the queued
    /// notification with it, and the next pass would see nothing changed.
    /// </param>
    private async Task CheckSinglePayment(
        ICryptoService cryptoService,
        PaymentRequest request,
        CancellationToken readToken,
        CancellationToken stoppingToken)
    {
        var result = await cryptoService.CheckTransactionStatus(request, readToken).ConfigureAwait(false);

        if (ShouldIgnoreAsTransient(request, result))
        {
            return;
        }

        // A confirmation count that ticked up is worth persisting but not worth telling the merchant
        // about: the event name is derived from the status, so every increment would post another
        // payment.confirmed under a fresh event id, which subscriber-side deduplication cannot
        // suppress. On a twelve-second chain that is hundreds of identical notifications.
        var worthNotifying = result.Status != request.Status ||
                             result.AmountPaid != request.AmountPaid ||
                             result.WasLate != request.WasLate ||
                             !string.Equals(result.TransactionId, request.TransactionId, StringComparison.Ordinal);

        var changed = worthNotifying || result.ConfirmationCount != request.ConfirmationCount;

        if (!changed)
        {
            return;
        }

        WarnIfSettlementUndone(request, result);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);

        var tracked = await dbContext.PaymentRequests.FindAsync([request.Id], stoppingToken).ConfigureAwait(false);

        if (tracked is null)
        {
            _logger.LogWarning("Payment {PaymentId} disappeared while it was being checked.", request.Id);
            return;
        }

        tracked.Status = result.Status;
        tracked.AmountPaid = result.AmountPaid;
        tracked.ConfirmationCount = result.ConfirmationCount;
        tracked.TransactionId = result.TransactionId;
        tracked.WasLate = result.WasLate;
        tracked.SettledAt = result.SettledAt;
        tracked.UpdatedAt = DateTime.UtcNow;

        // The notification is queued into the same transaction as the change it describes. Saving
        // first and queueing afterwards leaves a window in which the state is committed but the
        // notification is not, and because the next pass then sees nothing changed, the merchant is
        // never told at all.
        if (worthNotifying)
        {
            _webhookService.EnqueuePaymentChange(dbContext, tracked);
        }

        await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("Payment {PaymentId} updated - Status: {Status}, Confirmations: {Confirmations}, Paid: {AmountPaid}",
            tracked.Id, tracked.Status, tracked.ConfirmationCount, tracked.AmountPaid);

        if (worthNotifying)
        {
            _webhookService.SignalDispatch();
        }
    }

    /// <summary>
    /// Recognises the case where the explorer stopped reporting a transaction that had already been
    /// seen.
    /// </summary>
    /// <remarks>
    /// Applying it immediately would walk a payment back to Pending and tell the merchant, over a
    /// webhook, that money they were already told about is gone. A stale or briefly inconsistent
    /// explorer is far likelier than funds actually vanishing. A transaction that is still reported
    /// but with fewer confirmations, which is what a real reorganisation looks like, is not caught
    /// here and is applied normally.
    /// </remarks>
    private bool ShouldIgnoreAsTransient(PaymentRequest request, PaymentRequest result)
    {
        var hadFunds = request.AmountPaid > 0 || !string.IsNullOrEmpty(request.TransactionId);
        var reportsNothing = result.AmountPaid == 0 && string.IsNullOrEmpty(result.TransactionId);

        if (!hadFunds || !reportsNothing)
        {
            return false;
        }

        // A dropped or replaced transaction never comes back, so the benefit of the doubt has to
        // run out. Otherwise the request sits at Paid and is re-polled on every pass forever,
        // which is the cost this whole watch set exists to bound.
        if (DateTime.UtcNow - request.UpdatedAt > _config.MissingFundsGrace)
        {
            _logger.LogWarning(
                "The explorer has not reported the transaction(s) for payment {PaymentId} at {Address} since {UpdatedAt}, which is longer than the {Grace} grace period. Applying the disappearance.",
                request.Id, request.ReceivingAddress, request.UpdatedAt, _config.MissingFundsGrace);

            return false;
        }

        _logger.LogWarning(
            "The explorer no longer reports the transaction(s) for payment {PaymentId} at {Address}, which previously showed {AmountPaid} paid. " +
            "Keeping status {Status} and retrying, rather than reporting the funds as gone.",
            request.Id, request.ReceivingAddress, request.AmountPaid, request.Status);

        return true;
    }

    private void WarnIfSettlementUndone(PaymentRequest request, PaymentRequest result)
    {
        if (!request.Status.IsSettled() || result.Status.IsSettled())
        {
            return;
        }

        // Settlement is supposed to be final, so the chain reorganised deeper than the configured
        // confirmation threshold and undid a payment the merchant has very likely already acted on.
        // Nothing here can fix that; the point is that somebody finds out.
        _logger.LogCritical(
            "Payment {PaymentId} for {CurrencySymbol} on {Network} was settled as {PreviousStatus} and is now {NewStatus}. " +
            "A chain reorganisation has undone a confirmed payment. Expected {Expected}, now seeing {AmountPaid} with {Confirmations} confirmation(s). " +
            "Consider raising the confirmation requirement for this currency.",
            request.Id, request.CurrencySymbol, request.Network, request.Status, result.Status,
            request.AmountExpected, result.AmountPaid, result.ConfirmationCount);
    }
}
