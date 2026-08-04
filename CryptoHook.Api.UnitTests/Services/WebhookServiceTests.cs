using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CryptoHook.Api.UnitTests.Services;

public sealed class WebhookServiceTests : IDisposable
{
    private readonly DbContextOptions<DatabaseContext> _dbContextOptions;
    private readonly Mock<IDbContextFactory<DatabaseContext>> _mockDbContextFactory = new();
    private readonly WebhookDispatchSignal _dispatchSignal = new();

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    public WebhookServiceTests()
    {
        _dbContextOptions = new DbContextOptionsBuilder<DatabaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockDbContextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatabaseContext(_dbContextOptions));
    }

    public void Dispose() => _dispatchSignal.Dispose();

    private static PaymentRequest CreateTestPaymentRequest()
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            Status = PaymentStatus.Paid,
            AmountExpected = 100000,
            AmountPaid = 100000,
            ConfirmationCount = 1,
            ConfirmationNeeded = 1,
            ReceivingAddress = "bc1qtest123",
            CurrencySymbol = "BTC",
            Network = "Main",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };
    }

    private static WebhookConfigList CreateTestWebhookConfigs()
    {
        return
        [
            new WebhookConfig { Url = new Uri("https://example.com/webhook1"), Secret = "secret1" },
            new WebhookConfig { Url = new Uri("https://example.com/webhook2"), Secret = "secret2" }
        ];
    }

    private WebhookService CreateService(WebhookConfigList? configs = null)
    {
        return new WebhookService(
            _mockDbContextFactory.Object,
            Options.Create(configs ?? CreateTestWebhookConfigs()),
            _dispatchSignal,
            NullLogger<WebhookService>.Instance);
    }

    private async Task<List<WebhookDelivery>> ReadDeliveries()
    {
        using var context = new DatabaseContext(_dbContextOptions);
        return await context.WebhookDeliveries.ToListAsync();
    }

    [Fact]
    public async Task NotifyPaymentChange_WithNoWebhooks_QueuesNothing()
    {
        await CreateService([]).NotifyPaymentChange(CreateTestPaymentRequest());

        Assert.Empty(await ReadDeliveries());
    }

    [Fact]
    public async Task NotifyPaymentChange_WithNullWebhooks_QueuesNothing()
    {
        var options = new Mock<IOptions<WebhookConfigList>>();
        options.Setup(o => o.Value).Returns((WebhookConfigList)null!);

        var service = new WebhookService(
            _mockDbContextFactory.Object,
            options.Object,
            _dispatchSignal,
            NullLogger<WebhookService>.Instance);

        await service.NotifyPaymentChange(CreateTestPaymentRequest());

        Assert.Empty(await ReadDeliveries());
    }

    [Fact]
    public async Task NotifyPaymentChange_SubscriberWithNoUrl_IsSkippedRatherThanThrowing()
    {
        // Startup validation rejects this, but the consequence of relying on that is out of all
        // proportion: queueing runs inside the payment worker's transaction, so a
        // NullReferenceException here discards the status change it was queued alongside and every
        // payment stays where it was.
        WebhookConfigList configs =
        [
            new WebhookConfig { Url = null!, Secret = "secret1" },
            new WebhookConfig { Url = new Uri("https://example.com/webhook2"), Secret = "secret2" },
        ];

        await CreateService(configs).NotifyPaymentChange(CreateTestPaymentRequest());

        var deliveries = await ReadDeliveries();
        Assert.Single(deliveries);
        Assert.Equal("https://example.com/webhook2", deliveries[0].Url);
    }

    [Fact]
    public async Task NotifyPaymentChange_QueuesOneDeliveryPerSubscriber()
    {
        // Persisted before anything is attempted, so a crash or an unreachable subscriber
        // cannot lose the notification the way a fire-and-forget POST did.
        var configs = CreateTestWebhookConfigs();

        await CreateService(configs).NotifyPaymentChange(CreateTestPaymentRequest());

        var deliveries = await ReadDeliveries();
        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, d => d.Url == "https://example.com/webhook1");
        Assert.Contains(deliveries, d => d.Url == "https://example.com/webhook2");
        Assert.All(deliveries, d => Assert.Equal(WebhookDeliveryStatus.Pending, d.Status));
        Assert.All(deliveries, d => Assert.Equal(0, d.AttemptCount));
        Assert.All(deliveries, d => Assert.NotNull(d.NextAttemptAt));
    }

    [Fact]
    public async Task NotifyPaymentChange_RecordsTheEventTypeForTheStatus()
    {
        var payment = CreateTestPaymentRequest();
        payment.Status = PaymentStatus.OverpaidConfirmed;

        await CreateService().NotifyPaymentChange(payment);

        var deliveries = await ReadDeliveries();
        Assert.All(deliveries, d => Assert.Equal("payment.confirmed.overpaid", d.EventType));
    }

    [Fact]
    public async Task NotifyPaymentChange_SharesOneEventIdAcrossSubscribers()
    {
        // The event id is the idempotency key subscribers deduplicate on, so every copy of the
        // same event has to carry the same one.
        await CreateService().NotifyPaymentChange(CreateTestPaymentRequest());

        var eventIds = (await ReadDeliveries())
            .Select(d => JsonSerializer.Deserialize<WebhookEnvelope>(d.Payload)!.EventId)
            .Distinct()
            .ToList();

        Assert.Single(eventIds);
        Assert.NotEqual(Guid.Empty, eventIds[0]);
    }

    [Fact]
    public async Task NotifyPaymentChange_WrapsThePaymentInAVersionedEnvelope()
    {
        var payment = CreateTestPaymentRequest();

        await CreateService().NotifyPaymentChange(payment);

        var envelope = JsonSerializer.Deserialize<WebhookEnvelope>((await ReadDeliveries())[0].Payload);
        Assert.NotNull(envelope);
        Assert.Equal("1", envelope.SpecVersion);
        Assert.Equal("payment.received", envelope.EventType);
        Assert.Equal(payment.Id, envelope.Data.Id);
    }

    [Fact]
    public async Task NotifyPaymentChange_SerializesAmountsAsStrings()
    {
        // Amounts exceed what a JSON number can carry losslessly, so they go over the wire as
        // strings.
        var payment = CreateTestPaymentRequest();
        payment.AmountExpected = BigInteger.Parse("123456789012345678901234567890", CultureInfo.InvariantCulture);

        await CreateService().NotifyPaymentChange(payment);

        Assert.Contains("\"123456789012345678901234567890\"", (await ReadDeliveries())[0].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebhookDelivery_IsSerializableForTheApi()
    {
        // A required property that is also JsonIgnore'd makes System.Text.Json refuse to build a
        // contract for the type at all, which turned every delivery listing into a 500.
        await CreateService().NotifyPaymentChange(CreateTestPaymentRequest());
        var delivery = (await ReadDeliveries())[0];

        var json = JsonSerializer.Serialize(delivery, WebJsonOptions);

        Assert.Contains("\"eventType\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotifyPaymentChange_NullPayload_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateService().NotifyPaymentChange(null!));
    }
}
