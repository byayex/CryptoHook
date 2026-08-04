using System.Numerics;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.Factory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CryptoHook.Api.UnitTests.Services;

public sealed class PaymentCheckWorkerWatchTests : IDisposable
{
    private readonly DbContextOptions<DatabaseContext> _dbContextOptions;
    private readonly Mock<IDbContextFactory<DatabaseContext>> _mockDbContextFactory = new();
    private readonly Mock<ICryptoServiceFactory> _mockCryptoServiceFactory = new();
    private readonly Mock<IWebhookService> _mockWebhookService = new();
    private readonly PaymentCheckConfig _config = TestDefaults.PaymentCheckConfig();
    private readonly PaymentCheckWorker _worker;

    public PaymentCheckWorkerWatchTests()
    {
        _dbContextOptions = new DbContextOptionsBuilder<DatabaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockDbContextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatabaseContext(_dbContextOptions));

        _worker = new PaymentCheckWorker(
            NullLogger<PaymentCheckWorker>.Instance,
            _mockDbContextFactory.Object,
            _mockCryptoServiceFactory.Object,
            _mockWebhookService.Object,
            Options.Create(_config));
    }

    public void Dispose()
    {
        _worker.Dispose();
        GC.SuppressFinalize(this);
    }

    private static PaymentRequest CreatePayment(
        PaymentStatus status,
        DateTime? expiresAt = null,
        DateTime? settledAt = null,
        BigInteger? amountPaid = null,
        string? transactionId = null)
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            Status = status,
            AmountExpected = 100000,
            AmountPaid = amountPaid ?? BigInteger.Zero,
            ConfirmationCount = 0,
            ConfirmationNeeded = 2,
            ReceivingAddress = "bc1qtest123",
            CurrencySymbol = "BTC",
            Network = "Main",
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(30),
            SettledAt = settledAt,
            TransactionId = transactionId,
        };
    }

    private async Task Seed(params PaymentRequest[] payments)
    {
        using var context = new DatabaseContext(_dbContextOptions);
        context.PaymentRequests.AddRange(payments);
        await context.SaveChangesAsync();
    }

    private async Task<List<Guid>> WatchedIds()
    {
        using var context = new DatabaseContext(_dbContextOptions);
        var watched = await _worker
            .WatchedPayments(context.PaymentRequests.AsNoTracking(), DateTime.UtcNow, CancellationToken.None);

        return [.. watched.Select(p => p.Id)];
    }

    [Fact]
    public async Task WatchedPayments_AwaitingConfirmation_IsPolledEvenLongAfterExpiry()
    {
        // Funds are already on chain; confirmations have to be allowed to accumulate however
        // long they take, or a payment that is settling gets abandoned mid-flight.
        var payment = CreatePayment(PaymentStatus.Paid, expiresAt: DateTime.UtcNow.AddDays(-30), amountPaid: 100000);
        await Seed(payment);

        Assert.Contains(payment.Id, await WatchedIds());
    }

    [Fact]
    public async Task WatchedPayments_UnpaidWithinTheGracePeriod_IsStillPolled()
    {
        var payment = CreatePayment(PaymentStatus.Expired, expiresAt: DateTime.UtcNow.AddHours(-1));
        await Seed(payment);

        Assert.Contains(payment.Id, await WatchedIds());
    }

    [Fact]
    public async Task WatchedPayments_UnpaidPastTheGracePeriod_IsDropped()
    {
        var payment = CreatePayment(PaymentStatus.Expired, expiresAt: DateTime.UtcNow.AddHours(-25));
        await Seed(payment);

        Assert.DoesNotContain(payment.Id, await WatchedIds());
    }

    [Fact]
    public async Task WatchedPayments_DustedRequestPastTheGracePeriod_IsDropped()
    {
        // An Underpaid request could never expire, because expiry only ever applied when nothing
        // at all had arrived. A single dust payment to a published address was therefore enough
        // to have that row re-polled on every pass for the lifetime of the deployment.
        var payment = CreatePayment(
            PaymentStatus.Underpaid,
            expiresAt: DateTime.UtcNow.AddHours(-25),
            amountPaid: 1,
            transactionId: "dust");
        await Seed(payment);

        Assert.DoesNotContain(payment.Id, await WatchedIds());
    }

    [Fact]
    public async Task WatchedPayments_RecentlySettled_IsWatchedForAReorg()
    {
        // Settlement is otherwise final, and a reorg deeper than the confirmation threshold can
        // still undo a payment the merchant has already acted on.
        var payment = CreatePayment(
            PaymentStatus.Confirmed,
            settledAt: DateTime.UtcNow.AddMinutes(-10),
            amountPaid: 100000);
        await Seed(payment);

        Assert.Contains(payment.Id, await WatchedIds());
    }

    [Fact]
    public async Task WatchedPayments_SettledLongAgo_IsNoLongerWatched()
    {
        var payment = CreatePayment(
            PaymentStatus.Confirmed,
            settledAt: DateTime.UtcNow.AddHours(-3),
            amountPaid: 100000);
        await Seed(payment);

        Assert.DoesNotContain(payment.Id, await WatchedIds());
    }

    [Fact]
    public async Task WatchedPayments_SettledBeforeTheFieldExisted_IsNotWatched()
    {
        // Rows written by an older version have no settlement time, and re-polling every payment
        // ever confirmed would be a large and pointless amount of explorer traffic.
        var payment = CreatePayment(PaymentStatus.Confirmed, amountPaid: 100000);
        await Seed(payment);

        Assert.DoesNotContain(payment.Id, await WatchedIds());
    }

    [Fact]
    public async Task AbandonStalePayments_UnderpaidRequestThatOutlivedTheWatchSet_IsStillGivenATerminalStatus()
    {
        // The window in which a request is polled and the window in which it counts as abandoned
        // overlap by only an hour, so a worker that was down for longer used to leave the request
        // stuck: it dropped out of the watch set without ever having been abandoned, so it kept a
        // non-terminal status for good and the merchant was never told.
        var payment = CreatePayment(
            PaymentStatus.Underpaid,
            expiresAt: DateTime.UtcNow.AddDays(-9),
            amountPaid: 1,
            transactionId: "dust");
        await Seed(payment);

        Assert.DoesNotContain(payment.Id, await WatchedIds());

        await _worker.AbandonStalePayments(CancellationToken.None);

        using var context = new DatabaseContext(_dbContextOptions);
        Assert.Equal(PaymentStatus.Expired, (await context.PaymentRequests.FindAsync(payment.Id))!.Status);
        _mockWebhookService.Verify(
            w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.Is<PaymentRequest>(p => p.Id == payment.Id)),
            Times.Once);
    }

    [Fact]
    public async Task AbandonStalePayments_RequestWithinTheAbandonWindow_IsLeftAlone()
    {
        // Funds that arrive just after the timeout should still be detected, which is what the
        // window after expiry is for.
        var payment = CreatePayment(PaymentStatus.Pending, expiresAt: DateTime.UtcNow.AddHours(-1));
        await Seed(payment);

        await _worker.AbandonStalePayments(CancellationToken.None);

        using var context = new DatabaseContext(_dbContextOptions);
        Assert.Equal(PaymentStatus.Pending, (await context.PaymentRequests.FindAsync(payment.Id))!.Status);
    }

    [Fact]
    public async Task AbandonStalePayments_PaymentAwaitingConfirmation_IsNeverAbandoned()
    {
        // The funds are on chain. Confirmations have to be allowed to accumulate however long they
        // take, expiry or not.
        var payment = CreatePayment(
            PaymentStatus.Paid,
            expiresAt: DateTime.UtcNow.AddDays(-30),
            amountPaid: 100000);
        await Seed(payment);

        await _worker.AbandonStalePayments(CancellationToken.None);

        using var context = new DatabaseContext(_dbContextOptions);
        Assert.Equal(PaymentStatus.Paid, (await context.PaymentRequests.FindAsync(payment.Id))!.Status);
    }

    [Fact]
    public async Task WatchedPayments_LeastRecentlyUpdatedFirst()
    {
        // The explorer rate limit means the back of the queue may not be reached within a pass at
        // all. In a stable order that would always be the same payments; ordering by wait turns a
        // shortfall of capacity into a delay for everyone rather than starvation for a few.
        var recent = CreatePayment(PaymentStatus.Pending);
        recent.UpdatedAt = DateTime.UtcNow.AddMinutes(-1);
        var stale = CreatePayment(PaymentStatus.Pending);
        stale.UpdatedAt = DateTime.UtcNow.AddHours(-6);
        await Seed(recent, stale);

        var watched = await WatchedIds();

        Assert.Equal(stale.Id, watched[0]);
    }

    [Fact]
    public async Task CheckPayments_ExplorerForgetsAKnownTransaction_DoesNotReportTheFundsAsGone()
    {
        // Walking the payment back to Pending would tell the merchant, over a webhook, that money
        // they were already told about has vanished. A stale or briefly inconsistent explorer is
        // far likelier than that, and if the funds really are gone the request simply stays put
        // and keeps being polled.
        var payment = CreatePayment(PaymentStatus.Paid, amountPaid: 100000, transactionId: "tx-1");
        payment.ConfirmationCount = 1;
        await Seed(payment);

        var empty = CreatePayment(PaymentStatus.Pending);
        empty.Id = payment.Id;

        var mockService = new Mock<ICryptoService>();
        mockService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(empty);
        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main")).Returns(mockService.Object);

        await _worker.CheckPayments(CancellationToken.None);

        using var context = new DatabaseContext(_dbContextOptions);
        var stored = await context.PaymentRequests.FindAsync(payment.Id);
        Assert.Equal(PaymentStatus.Paid, stored!.Status);
        Assert.Equal(new BigInteger(100000), stored.AmountPaid);
        _mockWebhookService.Verify(
            w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.IsAny<PaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_TransactionLosesConfirmations_IsApplied()
    {
        // This is what a real reorganisation looks like: the transaction is still reported, just
        // less deeply buried. Unlike a disappearance, it is trustworthy and must be recorded.
        var payment = CreatePayment(PaymentStatus.Paid, amountPaid: 100000, transactionId: "tx-1");
        payment.ConfirmationCount = 5;
        await Seed(payment);

        var reorged = CreatePayment(PaymentStatus.Paid, amountPaid: 100000, transactionId: "tx-1");
        reorged.Id = payment.Id;
        reorged.ConfirmationCount = 1;

        var mockService = new Mock<ICryptoService>();
        mockService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reorged);
        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main")).Returns(mockService.Object);

        await _worker.CheckPayments(CancellationToken.None);

        using var context = new DatabaseContext(_dbContextOptions);
        Assert.Equal(1u, (await context.PaymentRequests.FindAsync(payment.Id))!.ConfirmationCount);
    }

    [Fact]
    public async Task CheckPayments_LateFlagChange_IsPersistedAndNotified()
    {
        // The merchant was already told the request expired and may have cancelled the order, so
        // this is a change they need to hear about.
        var payment = CreatePayment(PaymentStatus.Expired, expiresAt: DateTime.UtcNow.AddHours(-2));
        await Seed(payment);

        var late = CreatePayment(PaymentStatus.Confirmed, amountPaid: 100000, transactionId: "tx-late");
        late.Id = payment.Id;
        late.WasLate = true;
        late.SettledAt = DateTime.UtcNow;

        var mockService = new Mock<ICryptoService>();
        mockService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(late);
        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main")).Returns(mockService.Object);

        await _worker.CheckPayments(CancellationToken.None);

        using var context = new DatabaseContext(_dbContextOptions);
        var stored = await context.PaymentRequests.FindAsync(payment.Id);
        Assert.True(stored!.WasLate);
        Assert.NotNull(stored.SettledAt);
        _mockWebhookService.Verify(
            w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.Is<PaymentRequest>(p => p.WasLate)), Times.Once);
    }
}
