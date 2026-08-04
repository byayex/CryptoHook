using System.Numerics;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.Factory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CryptoHook.Api.UnitTests.Services;

public sealed class PaymentCheckWorkerResilienceTests : IDisposable
{
    private readonly Mock<IDbContextFactory<DatabaseContext>> _mockDbContextFactory = new();
    private readonly Mock<ICryptoServiceFactory> _mockCryptoServiceFactory = new();
    private readonly Mock<IWebhookService> _mockWebhookService = new();
    private readonly DbContextOptions<DatabaseContext> _dbContextOptions;
    private readonly PaymentCheckWorker _worker;

    public PaymentCheckWorkerResilienceTests()
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
            TestDefaults.PaymentCheckOptions());
    }

    public void Dispose()
    {
        _worker.Dispose();
        GC.SuppressFinalize(this);
    }

    private static PaymentRequest CreatePayment(
        PaymentStatus status = PaymentStatus.Pending,
        string currencySymbol = "BTC",
        string network = "Main",
        ulong derivationIndex = 0)
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            DerivationIndex = derivationIndex,
            Status = status,
            AmountExpected = 100000,
            AmountPaid = BigInteger.Zero,
            ConfirmationCount = 0,
            ConfirmationNeeded = 2,
            ReceivingAddress = $"address-{derivationIndex}",
            CurrencySymbol = currencySymbol,
            Network = network,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
    }

    private async Task Seed(params PaymentRequest[] payments)
    {
        using var context = new DatabaseContext(_dbContextOptions);
        context.PaymentRequests.AddRange(payments);
        await context.SaveChangesAsync();
    }

    private static PaymentRequest Paid(PaymentRequest source)
    {
        return new PaymentRequest
        {
            Id = source.Id,
            DerivationIndex = source.DerivationIndex,
            Status = PaymentStatus.Paid,
            AmountExpected = source.AmountExpected,
            AmountPaid = source.AmountExpected,
            ConfirmationCount = 1,
            ConfirmationNeeded = source.ConfirmationNeeded,
            ReceivingAddress = source.ReceivingAddress,
            CurrencySymbol = source.CurrencySymbol,
            Network = source.Network,
            CreatedAt = source.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = source.ExpiresAt,
            TransactionId = "tx-1",
        };
    }

    [Fact]
    public async Task CheckPayments_WhenOnePaymentThrows_StillPersistsTheOthers()
    {
        // One failing payment must not cost the whole cycle its updates.
        var failing = CreatePayment(derivationIndex: 0);
        var healthy = CreatePayment(derivationIndex: 1);
        await Seed(failing, healthy);

        var mockService = new Mock<ICryptoService>();
        mockService.Setup(s => s.CheckTransactionStatus(It.Is<PaymentRequest>(p => p.Id == failing.Id), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("explorer down"));
        mockService.Setup(s => s.CheckTransactionStatus(It.Is<PaymentRequest>(p => p.Id == healthy.Id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Paid(healthy));

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main")).Returns(mockService.Object);

        await _worker.CheckPayments(CancellationToken.None);

        using var context = new DatabaseContext(_dbContextOptions);
        Assert.Equal(PaymentStatus.Paid, (await context.PaymentRequests.FindAsync(healthy.Id))!.Status);
        Assert.Equal(PaymentStatus.Pending, (await context.PaymentRequests.FindAsync(failing.Id))!.Status);
    }

    [Fact]
    public async Task CheckPayments_WhenQueueingTheNotificationFails_TheChangeIsNotCommittedEither()
    {
        // Committing the status without its notification is the worse outcome: the next pass sees
        // nothing changed, so the merchant is never told at all. Leaving both uncommitted means the
        // next pass detects the change again and retries.
        var payment = CreatePayment();
        await Seed(payment);

        var mockService = new Mock<ICryptoService>();
        mockService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Paid(payment));

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main")).Returns(mockService.Object);
        _mockWebhookService.Setup(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.IsAny<PaymentRequest>()))
            .Throws(new InvalidOperationException("could not queue"));

        await _worker.CheckPayments(CancellationToken.None);

        using var context = new DatabaseContext(_dbContextOptions);
        Assert.Equal(PaymentStatus.Pending, (await context.PaymentRequests.FindAsync(payment.Id))!.Status);
    }

    [Fact]
    public async Task CheckPayments_WhenOneCurrencyHasNoService_OtherCurrenciesAreStillProcessed()
    {
        var unsupported = CreatePayment(currencySymbol: "DOGE");
        var supported = CreatePayment(derivationIndex: 1);
        await Seed(unsupported, supported);

        var mockService = new Mock<ICryptoService>();
        mockService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Paid(supported));

        _mockCryptoServiceFactory.Setup(f => f.GetService("DOGE", "Main")).Returns((ICryptoService?)null);
        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main")).Returns(mockService.Object);

        await _worker.CheckPayments(CancellationToken.None);

        using var context = new DatabaseContext(_dbContextOptions);
        Assert.Equal(PaymentStatus.Paid, (await context.PaymentRequests.FindAsync(supported.Id))!.Status);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Paid)]
    [InlineData(PaymentStatus.Underpaid)]
    [InlineData(PaymentStatus.Overpaid)]
    [InlineData(PaymentStatus.MultipleTransactions)]
    public async Task CheckPayments_NonTerminalStatus_IsRechecked(PaymentStatus status)
    {
        // None of these are dead ends: a top-up still has to be noticed, and a zero-confirmation
        // overpayment can still be replaced.
        var payment = CreatePayment(status);
        await Seed(payment);

        var mockService = new Mock<ICryptoService>();
        mockService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Paid(payment));

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main")).Returns(mockService.Object);

        await _worker.CheckPayments(CancellationToken.None);

        mockService.Verify(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckPayments_ConfirmedStatus_IsNotRechecked()
    {
        var payment = CreatePayment(PaymentStatus.Confirmed);
        await Seed(payment);

        await _worker.CheckPayments(CancellationToken.None);

        _mockCryptoServiceFactory.Verify(f => f.GetService(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_WhenOnlyTheAmountChanges_StillNotifies()
    {
        // A top-up that keeps status and confirmation count identical is still a change the
        // subscriber has to learn about.
        var payment = CreatePayment(PaymentStatus.Underpaid);
        payment.AmountPaid = 500;
        await Seed(payment);

        var updated = Paid(payment);
        updated.Status = PaymentStatus.Underpaid;
        updated.ConfirmationCount = payment.ConfirmationCount;
        updated.AmountPaid = 900;

        var mockService = new Mock<ICryptoService>();
        mockService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(updated);
        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main")).Returns(mockService.Object);

        await _worker.CheckPayments(CancellationToken.None);

        _mockWebhookService.Verify(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.Is<PaymentRequest>(p => p.AmountPaid == 900)), Times.Once);
    }

    [Fact]
    public async Task CheckPayments_QueuesTheNotificationInTheSameTransactionAsTheChange()
    {
        var payment = CreatePayment();
        await Seed(payment);

        var mockService = new Mock<ICryptoService>();
        mockService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Paid(payment));
        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main")).Returns(mockService.Object);

        PaymentStatus? statusAtQueueTime = null;
        _mockWebhookService.Setup(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.IsAny<PaymentRequest>()))
            .Callback(() =>
            {
                using var context = new DatabaseContext(_dbContextOptions);
                statusAtQueueTime = context.PaymentRequests.Single(p => p.Id == payment.Id).Status;
            });

        await _worker.CheckPayments(CancellationToken.None);

        // Still uncommitted while the notification is being queued, and committed together with it.
        Assert.Equal(PaymentStatus.Pending, statusAtQueueTime);

        using var context = new DatabaseContext(_dbContextOptions);
        Assert.Equal(PaymentStatus.Paid, (await context.PaymentRequests.FindAsync(payment.Id))!.Status);
    }
}
