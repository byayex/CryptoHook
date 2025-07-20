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

public class PaymentCheckWorkerTests
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
            _mockWebhookService.Object);
    }

    private static PaymentRequest CreateTestPaymentRequest(
        PaymentStatusEnum status = PaymentStatusEnum.Pending,
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
        // Arrange
        var cancellationToken = CancellationToken.None;

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        _mockCryptoServiceFactory.Verify(f => f.GetService(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockWebhookService.Verify(w => w.NotifyPaymentChange(It.IsAny<PaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_WithOnlyExpiredPayments_DoesNothing()
    {
        // Arrange
        var expiredPayment = CreateTestPaymentRequest(PaymentStatusEnum.Expired);
        await SeedDatabase(expiredPayment);

        var cancellationToken = CancellationToken.None;

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        _mockCryptoServiceFactory.Verify(f => f.GetService(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockWebhookService.Verify(w => w.NotifyPaymentChange(It.IsAny<PaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_WithPendingPayments_CallsCryptoService()
    {
        // Arrange
        var pendingPayment = CreateTestPaymentRequest(PaymentStatusEnum.Pending);
        await SeedDatabase(pendingPayment);

        var mockCryptoService = new Mock<ICryptoService>();
        var updatedPayment = CreateTestPaymentRequest(PaymentStatusEnum.Paid, amountPaid: 100000);
        updatedPayment.Id = pendingPayment.Id;

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()))
            .ReturnsAsync(updatedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        _mockCryptoServiceFactory.Verify(f => f.GetService("BTC", "Main"), Times.Once);
        mockCryptoService.Verify(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()), Times.Once);
    }

    [Fact]
    public async Task CheckPayments_WithStatusChange_CallsWebhookService()
    {
        // Arrange
        var pendingPayment = CreateTestPaymentRequest(PaymentStatusEnum.Pending);
        await SeedDatabase(pendingPayment);

        var mockCryptoService = new Mock<ICryptoService>();
        var updatedPayment = CreateTestPaymentRequest(PaymentStatusEnum.Paid, amountPaid: BigInteger.Parse("100000"));
        updatedPayment.Id = pendingPayment.Id;

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()))
            .ReturnsAsync(updatedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        _mockWebhookService.Verify(w => w.NotifyPaymentChange(It.Is<PaymentRequest>(p => p.Status == PaymentStatusEnum.Paid)), Times.Once);
    }

    [Fact]
    public async Task CheckPayments_WithConfirmationCountChange_CallsWebhookService()
    {
        // Arrange
        var paidPayment = CreateTestPaymentRequest(PaymentStatusEnum.Paid, confirmationCount: 1);
        await SeedDatabase(paidPayment);

        var mockCryptoService = new Mock<ICryptoService>();
        var updatedPayment = CreateTestPaymentRequest(PaymentStatusEnum.Paid, confirmationCount: 2);
        updatedPayment.Id = paidPayment.Id;

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()))
            .ReturnsAsync(updatedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        _mockWebhookService.Verify(w => w.NotifyPaymentChange(It.Is<PaymentRequest>(p => p.ConfirmationCount == 2)), Times.Once);
    }

    [Fact]
    public async Task CheckPayments_WithNoChanges_DoesNotCallWebhookService()
    {
        // Arrange
        var paidPayment = CreateTestPaymentRequest(PaymentStatusEnum.Paid, confirmationCount: 1);
        await SeedDatabase(paidPayment);

        var mockCryptoService = new Mock<ICryptoService>();

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()))
            .ReturnsAsync(paidPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        _mockWebhookService.Verify(w => w.NotifyPaymentChange(It.IsAny<PaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_WithMultipleCurrencies_GroupsBySymbolAndNetwork()
    {
        // Arrange
        var btcPayment = CreateTestPaymentRequest(PaymentStatusEnum.Pending, "BTC", "Main");
        var btcTestnetPayment = CreateTestPaymentRequest(PaymentStatusEnum.Pending, "BTC", "Testnet");
        await SeedDatabase(btcPayment, btcTestnetPayment);

        var mockBtcService = new Mock<ICryptoService>();
        var mockBtcTestnetService = new Mock<ICryptoService>();

        mockBtcService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()))
            .ReturnsAsync(btcPayment);

        mockBtcTestnetService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()))
            .ReturnsAsync(btcTestnetPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockBtcService.Object);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Testnet"))
            .Returns(mockBtcTestnetService.Object);

        var cancellationToken = CancellationToken.None;

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        _mockCryptoServiceFactory.Verify(f => f.GetService("BTC", "Main"), Times.Once);
        _mockCryptoServiceFactory.Verify(f => f.GetService("BTC", "Testnet"), Times.Once);
        mockBtcService.Verify(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()), Times.Once);
        mockBtcTestnetService.Verify(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()), Times.Once);
    }

    [Fact]
    public async Task CheckPayments_WithUnsupportedCurrency_SkipsPayment()
    {
        // Arrange
        var unsupportedPayment = CreateTestPaymentRequest(PaymentStatusEnum.Pending, "UNSUPPORTED", "Main");
        await SeedDatabase(unsupportedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("UNSUPPORTED", "Main"))
            .Returns((ICryptoService)null!);

        var cancellationToken = CancellationToken.None;

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        _mockWebhookService.Verify(w => w.NotifyPaymentChange(It.IsAny<PaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task CheckPayments_UpdatesPaymentFields()
    {
        // Arrange
        var originalPayment = CreateTestPaymentRequest(
            PaymentStatusEnum.Pending,
            amountPaid: BigInteger.Zero,
            confirmationCount: 0);
        await SeedDatabase(originalPayment);

        var mockCryptoService = new Mock<ICryptoService>();
        var updatedPayment = CreateTestPaymentRequest(
            PaymentStatusEnum.Paid,
            amountPaid: 100000,
            confirmationCount: 1);
        updatedPayment.Id = originalPayment.Id;
        updatedPayment.TransactionId = "test-tx-id";

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()))
            .ReturnsAsync(updatedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        using var dbContext = new DatabaseContext(_dbContextOptions);
        var dbPayment = await dbContext.PaymentRequests.FindAsync(originalPayment.Id);
        Assert.NotNull(dbPayment);
        Assert.Equal(PaymentStatusEnum.Paid, dbPayment.Status);
        Assert.Equal(100000, dbPayment.AmountPaid);
        Assert.Equal((uint)1, dbPayment.ConfirmationCount);
        Assert.Equal("test-tx-id", dbPayment.TransactionId);
        Assert.True(dbPayment.UpdatedAt > originalPayment.UpdatedAt);
    }

    [Fact]
    public async Task CheckPayments_WithMultiplePaymentsForSameCurrency_ProcessesAll()
    {
        // Arrange
        var payment1 = CreateTestPaymentRequest(PaymentStatusEnum.Pending, "BTC", "Main");
        var payment2 = CreateTestPaymentRequest(PaymentStatusEnum.Pending, "BTC", "Main");
        await SeedDatabase(payment1, payment2);

        var mockCryptoService = new Mock<ICryptoService>();

        mockCryptoService.SetupSequence(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()))
            .ReturnsAsync(new PaymentRequest
            {
                Id = payment1.Id,
                Status = PaymentStatusEnum.Paid,
                AmountExpected = payment1.AmountExpected,
                AmountPaid = BigInteger.Parse("100000"),
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
                Status = PaymentStatusEnum.Paid,
                AmountExpected = payment2.AmountExpected,
                AmountPaid = BigInteger.Parse("100000"),
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

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        mockCryptoService.Verify(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()), Times.Exactly(2));
        _mockWebhookService.Verify(w => w.NotifyPaymentChange(It.IsAny<PaymentRequest>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CheckPayments_SavesChangesToDatabase()
    {
        // Arrange
        var payment = CreateTestPaymentRequest(PaymentStatusEnum.Pending);
        await SeedDatabase(payment);

        var mockCryptoService = new Mock<ICryptoService>();
        var updatedPayment = CreateTestPaymentRequest(PaymentStatusEnum.Paid);
        updatedPayment.Id = payment.Id;

        mockCryptoService.Setup(s => s.CheckTransactionStatus(It.IsAny<PaymentRequest>()))
            .ReturnsAsync(updatedPayment);

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main"))
            .Returns(mockCryptoService.Object);

        var cancellationToken = CancellationToken.None;

        // Act
        await _worker.CheckPayments(cancellationToken);

        // Assert
        using var dbContext = new DatabaseContext(_dbContextOptions);
        var dbPayment = await dbContext.PaymentRequests.FindAsync(payment.Id);
        Assert.NotNull(dbPayment);
        Assert.Equal(PaymentStatusEnum.Paid, dbPayment.Status);
    }
}
