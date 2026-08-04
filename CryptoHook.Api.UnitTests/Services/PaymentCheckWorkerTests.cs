using System.Globalization;
using System.Numerics;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.Factory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace CryptoHook.Api.UnitTests.Services;

public sealed class PaymentCheckWorkerTests : IDisposable
{
    private readonly Mock<ILogger<PaymentCheckWorker>> _mockLogger;
    private readonly Mock<IDbContextFactory<DatabaseContext>> _mockDbContextFactory;
    private readonly Mock<ICryptoServiceFactory> _mockCryptoServiceFactory;
    private readonly Mock<IWebhookService> _mockWebhookService;
    private readonly DbContextOptions<DatabaseContext> _dbContextOptions;
    private readonly PaymentCheckWorker _worker;

    public PaymentCheckWorkerTests()
    {
        _mockLogger = new Mock<ILogger<PaymentCheckWorker>>();
        _mockDbContextFactory = new Mock<IDbContextFactory<DatabaseContext>>();
        _mockCryptoServiceFactory = new Mock<ICryptoServiceFactory>();
        _mockWebhookService = new Mock<IWebhookService>();

        _dbContextOptions = new DbContextOptionsBuilder<DatabaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockDbContextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatabaseContext(_dbContextOptions));

        _worker = new PaymentCheckWorker(
            _mockLogger.Object,
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

    private static PaymentRequest CreateTestPaymentRequest(
        PaymentStatus status = PaymentStatus.Pending,
        string currencySymbol = "BTC",
        string network = "Main",
        BigInteger? amountExpected = null,
        BigInteger? amountPaid = null,
        uint confirmationCount = 0,
        uint confirmationNeeded = 1)
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            Status = status,
            AmountExpected = amountExpected ?? 100000,
            AmountPaid = amountPaid ?? BigInteger.Zero,
            ConfirmationCount = confirmationCount,
            ConfirmationNeeded = confirmationNeeded,
            ReceivingAddress = "bc1qtest123",
            CurrencySymbol = currencySymbol,
            Network = network,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };
    }

    private async Task SeedDatabase(params PaymentRequest[] payments)
    {
        using var dbContext = new DatabaseContext(_dbContextOptions);
        dbContext.PaymentRequests.AddRange(payments);
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task CheckPayments_WithNoPayments_DoesNothing()
    {
        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        _mockCryptoServiceFactory.Verify(f => f.GetService(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockWebhookService.Verify(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.IsAny<PaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_WithLongExpiredPayments_DoesNothing()
    {
        var expiredPayment = CreateTestPaymentRequest(PaymentStatus.Expired);
        var config = TestDefaults.PaymentCheckConfig();
        expiredPayment.ExpiresAt = DateTime.UtcNow - config.AbandonAfterExpiry - config.AbandonSweepGrace - TimeSpan.FromHours(1);
        await SeedDatabase(expiredPayment);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        _mockCryptoServiceFactory.Verify(f => f.GetService(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockWebhookService.Verify(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.IsAny<PaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_WithRecentlyExpiredPayment_StillChecksForLateFunds()
    {
        // Funds that land shortly after the timeout must still be detected instead of being
        // silently lost because the request had already been marked as expired.
        var expiredPayment = CreateTestPaymentRequest(PaymentStatus.Expired);
        expiredPayment.ExpiresAt = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        await SeedDatabase(expiredPayment);

        var latePayment = CreateTestPaymentRequest(PaymentStatus.Paid, amountPaid: 100000, confirmationCount: 1);
        latePayment.Id = expiredPayment.Id;

        var mockCryptoService = new Mock<ICryptoService>();
        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(latePayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        using var dbContext = new DatabaseContext(_dbContextOptions);
        var dbPayment = await dbContext.PaymentRequests.FindAsync(expiredPayment.Id);
        Assert.NotNull(dbPayment);
        Assert.Equal(PaymentStatus.Paid, dbPayment.Status);
        _mockWebhookService.Verify(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.IsAny<PaymentRequest>()), Times.Once);
    }

    [Fact]
    public async Task CheckPayments_WithPendingPayments_CallsCryptoService()
    {
        var pendingPayment = CreateTestPaymentRequest(PaymentStatus.Pending);
        await SeedDatabase(pendingPayment);

        var mockCryptoService = new Mock<ICryptoService>();
        var updatedPayment = CreateTestPaymentRequest(PaymentStatus.Paid, amountPaid: 100000);
        updatedPayment.Id = pendingPayment.Id;

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        _mockCryptoServiceFactory.Verify(f => f.GetService("BTC", "Main"), Times.Once);
        mockCryptoService.Verify(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckPayments_WithStatusChange_CallsWebhookService()
    {
        var pendingPayment = CreateTestPaymentRequest(PaymentStatus.Pending);
        await SeedDatabase(pendingPayment);

        var mockCryptoService = new Mock<ICryptoService>();
        var updatedPayment = CreateTestPaymentRequest(PaymentStatus.Paid, amountPaid: BigInteger.Parse("100000", CultureInfo.InvariantCulture));
        updatedPayment.Id = pendingPayment.Id;

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        _mockWebhookService.Verify(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.Is<PaymentRequest>(p => p.Status == PaymentStatus.Paid)), Times.Once);
    }

    [Fact]
    public async Task CheckPayments_WithConfirmationCountChangeOnly_PersistsWithoutNotifying()
    {
        // The event name comes from the status, so notifying on every increment would post another
        // payment.confirmed under a fresh event id each time, which subscribers cannot deduplicate.
        var paidPayment = CreateTestPaymentRequest(PaymentStatus.Paid, confirmationCount: 1);
        await SeedDatabase(paidPayment);

        var mockCryptoService = new Mock<ICryptoService>();
        var updatedPayment = CreateTestPaymentRequest(PaymentStatus.Paid, confirmationCount: 2);
        updatedPayment.Id = paidPayment.Id;

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        using var dbContext = new DatabaseContext(_dbContextOptions);
        Assert.Equal(2u, (await dbContext.PaymentRequests.FindAsync(paidPayment.Id))!.ConfirmationCount);
        _mockWebhookService.Verify(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.IsAny<PaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_WithNoChanges_DoesNotCallWebhookService()
    {
        var paidPayment = CreateTestPaymentRequest(PaymentStatus.Paid, confirmationCount: 1);
        await SeedDatabase(paidPayment);

        var mockCryptoService = new Mock<ICryptoService>();

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(paidPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        _mockWebhookService.Verify(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.IsAny<PaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_WithMultipleCurrencies_GroupsBySymbolAndNetwork()
    {
        var btcPayment = CreateTestPaymentRequest(PaymentStatus.Pending, "BTC", "Main");
        var btcTestnetPayment = CreateTestPaymentRequest(PaymentStatus.Pending, "BTC", "Testnet");
        await SeedDatabase(btcPayment, btcTestnetPayment);

        var mockBtcService = new Mock<ICryptoService>();
        var mockBtcTestnetService = new Mock<ICryptoService>();

        mockBtcService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(btcPayment);

        mockBtcTestnetService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(btcTestnetPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockBtcService.Object);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Testnet"))
            .Returns(mockBtcTestnetService.Object);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        _mockCryptoServiceFactory.Verify(f => f.GetService("BTC", "Main"), Times.Once);
        _mockCryptoServiceFactory.Verify(f => f.GetService("BTC", "Testnet"), Times.Once);
        mockBtcService.Verify(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        mockBtcTestnetService.Verify(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckPayments_WithUnsupportedCurrency_SkipsPayment()
    {
        var unsupportedPayment = CreateTestPaymentRequest(PaymentStatus.Pending, "UNSUPPORTED", "Main");
        await SeedDatabase(unsupportedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("UNSUPPORTED", "Main"))
            .Returns((ICryptoService)null!);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        _mockWebhookService.Verify(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.IsAny<PaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_UpdatesPaymentFields()
    {
        var originalPayment = CreateTestPaymentRequest(
            PaymentStatus.Pending,
            amountPaid: BigInteger.Zero,
            confirmationCount: 0);
        await SeedDatabase(originalPayment);

        var mockCryptoService = new Mock<ICryptoService>();
        var updatedPayment = CreateTestPaymentRequest(
            PaymentStatus.Paid,
            amountPaid: 100000,
            confirmationCount: 1);
        updatedPayment.Id = originalPayment.Id;
        updatedPayment.TransactionId = "test-tx-id";

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        using var dbContext = new DatabaseContext(_dbContextOptions);
        var dbPayment = await dbContext.PaymentRequests.FindAsync(originalPayment.Id);
        Assert.NotNull(dbPayment);
        Assert.Equal(PaymentStatus.Paid, dbPayment.Status);
        Assert.Equal(100000, dbPayment.AmountPaid);
        Assert.Equal((uint)1, dbPayment.ConfirmationCount);
        Assert.Equal("test-tx-id", dbPayment.TransactionId);
        Assert.True(dbPayment.UpdatedAt > originalPayment.UpdatedAt);
    }

    [Fact]
    public async Task CheckPayments_WithMultiplePaymentsForSameCurrency_ProcessesAll()
    {
        var payment1 = CreateTestPaymentRequest(PaymentStatus.Pending, "BTC", "Main");
        var payment2 = CreateTestPaymentRequest(PaymentStatus.Pending, "BTC", "Main");
        await SeedDatabase(payment1, payment2);

        var mockCryptoService = new Mock<ICryptoService>();

        mockCryptoService.SetupSequence(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentRequest
            {
                Id = payment1.Id,
                Status = PaymentStatus.Paid,
                AmountExpected = payment1.AmountExpected,
                AmountPaid = BigInteger.Parse("100000", CultureInfo.InvariantCulture),
                ConfirmationCount = 1,
                ConfirmationNeeded = payment1.ConfirmationNeeded,
                ReceivingAddress = payment1.ReceivingAddress,
                CurrencySymbol = payment1.CurrencySymbol,
                Network = payment1.Network,
                CreatedAt = payment1.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
                ExpiresAt = payment1.ExpiresAt
            })
            .ReturnsAsync(new PaymentRequest
            {
                Id = payment2.Id,
                Status = PaymentStatus.Paid,
                AmountExpected = payment2.AmountExpected,
                AmountPaid = BigInteger.Parse("100000", CultureInfo.InvariantCulture),
                ConfirmationCount = 1,
                ConfirmationNeeded = payment2.ConfirmationNeeded,
                ReceivingAddress = payment2.ReceivingAddress,
                CurrencySymbol = payment2.CurrencySymbol,
                Network = payment2.Network,
                CreatedAt = payment2.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
                ExpiresAt = payment2.ExpiresAt
            });

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        mockCryptoService.Verify(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _mockWebhookService.Verify(w => w.EnqueuePaymentChange(It.IsAny<DatabaseContext>(), It.IsAny<PaymentRequest>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CheckPayments_SavesChangesToDatabase()
    {
        var payment = CreateTestPaymentRequest(PaymentStatus.Pending);
        await SeedDatabase(payment);

        var mockCryptoService = new Mock<ICryptoService>();
        var updatedPayment = CreateTestPaymentRequest(PaymentStatus.Paid);
        updatedPayment.Id = payment.Id;

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        await _worker.CheckPayments(cancellationToken);

        using var dbContext = new DatabaseContext(_dbContextOptions);
        var dbPayment = await dbContext.PaymentRequests.FindAsync(payment.Id);
        Assert.NotNull(dbPayment);
        Assert.Equal(PaymentStatus.Paid, dbPayment.Status);
    }

    [Fact]
    public async Task CheckPayments_UnderpaidRequestPastTheAbandonWindow_IsStillPolledSoItCanExpire()
    {
        // The watch window and the abandon rule used to meet at the same instant with opposite
        // strictness, so a request left the watch set exactly when it became abandonable. An
        // underpaid request therefore stayed Underpaid forever and the merchant was never told.
        var config = TestDefaults.PaymentCheckConfig();
        var underpaid = CreateTestPaymentRequest(PaymentStatus.Underpaid, amountPaid: 40000);
        underpaid.ExpiresAt = DateTime.UtcNow - config.AbandonAfterExpiry - TimeSpan.FromMinutes(5);
        await SeedDatabase(underpaid);

        var abandoned = CreateTestPaymentRequest(PaymentStatus.Expired, amountPaid: 40000);
        abandoned.Id = underpaid.Id;

        var mockCryptoService = new Mock<ICryptoService>();
        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(abandoned);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        await _worker.CheckPayments(CancellationToken.None);

        using var dbContext = new DatabaseContext(_dbContextOptions);
        var dbPayment = await dbContext.PaymentRequests.FindAsync(underpaid.Id);
        Assert.Equal(PaymentStatus.Expired, dbPayment!.Status);
    }

    [Fact]
    public async Task CheckPayments_TransactionMissingLongerThanTheGrace_AppliesTheDisappearance()
    {
        // A dropped or replaced transaction never comes back. Ignoring the disappearance forever
        // left the request at Paid and re-polled it on every pass for the life of the deployment.
        var config = TestDefaults.PaymentCheckConfig();
        var payment = CreateTestPaymentRequest(PaymentStatus.Paid, amountPaid: 100000, confirmationCount: 0);
        payment.TransactionId = "dropped";
        payment.UpdatedAt = DateTime.UtcNow - config.MissingFundsGrace - TimeSpan.FromHours(1);
        await SeedDatabase(payment);

        var nothingSeen = CreateTestPaymentRequest(PaymentStatus.Pending);
        nothingSeen.Id = payment.Id;

        var mockCryptoService = new Mock<ICryptoService>();
        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(nothingSeen);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        await _worker.CheckPayments(CancellationToken.None);

        using var dbContext = new DatabaseContext(_dbContextOptions);
        var dbPayment = await dbContext.PaymentRequests.FindAsync(payment.Id);
        Assert.Equal(PaymentStatus.Pending, dbPayment!.Status);
        Assert.Equal(BigInteger.Zero, dbPayment.AmountPaid);
    }

    [Fact]
    public async Task CheckPayments_TransactionMissingWithinTheGrace_KeepsTheCurrentStatus()
    {
        var payment = CreateTestPaymentRequest(PaymentStatus.Paid, amountPaid: 100000);
        payment.TransactionId = "briefly-missing";
        await SeedDatabase(payment);

        var nothingSeen = CreateTestPaymentRequest(PaymentStatus.Pending);
        nothingSeen.Id = payment.Id;

        var mockCryptoService = new Mock<ICryptoService>();
        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(nothingSeen);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        await _worker.CheckPayments(CancellationToken.None);

        using var dbContext = new DatabaseContext(_dbContextOptions);
        var dbPayment = await dbContext.PaymentRequests.FindAsync(payment.Id);
        Assert.Equal(PaymentStatus.Paid, dbPayment!.Status);
    }
}
