using System.Net;
using System.Security.Cryptography;
using System.Text;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace CryptoHook.Api.UnitTests.Services;

public sealed class WebhookDispatchWorkerTests : IDisposable
{
    private const string Url = "https://example.com/webhook";
    private const string Secret = "webhook-secret";

    private readonly DbContextOptions<DatabaseContext> _dbContextOptions;
    private readonly Mock<IDbContextFactory<DatabaseContext>> _mockDbContextFactory = new();
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory = new();
    private readonly Mock<HttpMessageHandler> _mockHandler = new();
    private readonly WebhookDispatchSignal _dispatchSignal = new();
    private readonly WebhookDeliveryConfig _config = TestDefaults.WebhookDeliveryConfig();
    private readonly List<WebhookDispatchWorker> _workers = [];

    public WebhookDispatchWorkerTests()
    {
        _dbContextOptions = new DbContextOptionsBuilder<DatabaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockDbContextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatabaseContext(_dbContextOptions));

        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_mockHandler.Object));
    }

    public void Dispose()
    {
        _workers.ForEach(worker => worker.Dispose());
        _dispatchSignal.Dispose();
        _mockHandler.Object.Dispose();
    }

    private WebhookDispatchWorker CreateWorker(WebhookConfigList? configs = null)
    {
        configs ??= [new WebhookConfig { Url = new Uri(Url), Secret = Secret }];

        var worker = new WebhookDispatchWorker(
            NullLogger<WebhookDispatchWorker>.Instance,
            _mockDbContextFactory.Object,
            _mockHttpClientFactory.Object,
            Options.Create(configs),
            Options.Create(_config),
            _dispatchSignal);

        _workers.Add(worker);
        return worker;
    }

    private async Task<WebhookDelivery> SeedDelivery(string payload = "{\"eventId\":\"x\"}", string url = Url)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            PaymentRequestId = Guid.NewGuid(),
            Url = url,
            EventType = "payment.confirmed",
            Payload = payload,
            Status = WebhookDeliveryStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = DateTime.UtcNow.AddSeconds(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        using var context = new DatabaseContext(_dbContextOptions);
        context.WebhookDeliveries.Add(delivery);
        await context.SaveChangesAsync();
        return delivery;
    }

    private async Task<WebhookDelivery> ReadDelivery(Guid id)
    {
        using var context = new DatabaseContext(_dbContextOptions);
        return await context.WebhookDeliveries.SingleAsync(d => d.Id == id);
    }

    private void SetupResponse(HttpStatusCode statusCode)
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(statusCode));
    }

    [Fact]
    public async Task DispatchDueDeliveries_SubscriberAccepts_MarksDelivered()
    {
        SetupResponse(HttpStatusCode.OK);
        var delivery = await SeedDelivery();

        await CreateWorker().DispatchDueDeliveries(CancellationToken.None);

        var stored = await ReadDelivery(delivery.Id);
        Assert.Equal(WebhookDeliveryStatus.Delivered, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(stored.DeliveredAt);
        Assert.Null(stored.NextAttemptAt);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public async Task DispatchDueDeliveries_SubscriberFails_SchedulesARetryInsteadOfDropping()
    {
        // A notification that is logged and forgotten means a briefly unreachable subscriber never
        // learns that a payment settled.
        SetupResponse(HttpStatusCode.InternalServerError);
        var delivery = await SeedDelivery();

        await CreateWorker().DispatchDueDeliveries(CancellationToken.None);

        var stored = await ReadDelivery(delivery.Id);
        Assert.Equal(WebhookDeliveryStatus.Pending, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(stored.NextAttemptAt);
        Assert.True(stored.NextAttemptAt > DateTime.UtcNow);
        Assert.Contains("500", stored.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchDueDeliveries_RetryNotYetDue_IsLeftAlone()
    {
        SetupResponse(HttpStatusCode.OK);
        var delivery = await SeedDelivery();

        using (var context = new DatabaseContext(_dbContextOptions))
        {
            var stored = await context.WebhookDeliveries.SingleAsync(d => d.Id == delivery.Id);
            stored.NextAttemptAt = DateTime.UtcNow.AddMinutes(5);
            await context.SaveChangesAsync();
        }

        await CreateWorker().DispatchDueDeliveries(CancellationToken.None);

        Assert.Equal(WebhookDeliveryStatus.Pending, (await ReadDelivery(delivery.Id)).Status);
        _mockHandler.Protected().Verify(
            "SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task DispatchDueDeliveries_AttemptBudgetExhausted_BecomesADeadLetter()
    {
        // Kept rather than deleted: an undelivered settlement notification that nobody can see
        // is how a merchant ends up not knowing a payment happened.
        SetupResponse(HttpStatusCode.InternalServerError);
        var delivery = await SeedDelivery();
        var worker = CreateWorker();

        for (var attempt = 0; attempt < _config.MaxAttempts; attempt++)
        {
            using (var context = new DatabaseContext(_dbContextOptions))
            {
                var due = await context.WebhookDeliveries.SingleAsync(d => d.Id == delivery.Id);
                due.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
                await context.SaveChangesAsync();
            }

            await worker.DispatchDueDeliveries(CancellationToken.None);
        }

        var stored = await ReadDelivery(delivery.Id);
        Assert.Equal(WebhookDeliveryStatus.Failed, stored.Status);
        Assert.Equal(_config.MaxAttempts, stored.AttemptCount);
        Assert.Null(stored.NextAttemptAt);
        Assert.NotNull(stored.LastError);
    }

    [Fact]
    public async Task DispatchDueDeliveries_SubscriberTimesOut_IsRetriedRatherThanCrashing()
    {
        // A send timeout surfaces as TaskCanceledException, which must not escape and abort the
        // rest of the queue.
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("timed out"));

        var delivery = await SeedDelivery();

        await CreateWorker().DispatchDueDeliveries(CancellationToken.None);

        var stored = await ReadDelivery(delivery.Id);
        Assert.Equal(WebhookDeliveryStatus.Pending, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Fact]
    public async Task DispatchDueDeliveries_OneFailingSubscriber_DoesNotStopTheOthers()
    {
        var configs = new WebhookConfigList
        {
            new WebhookConfig { Url = new Uri("https://example.com/bad"), Secret = "a" },
            new WebhookConfig { Url = new Uri("https://example.com/good"), Secret = "b" },
        };

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
                request.RequestUri!.ToString().Contains("bad", StringComparison.Ordinal)
                    ? throw new HttpRequestException("unreachable")
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var bad = await SeedDelivery(url: "https://example.com/bad");
        var good = await SeedDelivery(url: "https://example.com/good");

        await CreateWorker(configs).DispatchDueDeliveries(CancellationToken.None);

        Assert.Equal(WebhookDeliveryStatus.Pending, (await ReadDelivery(bad.Id)).Status);
        Assert.Equal(WebhookDeliveryStatus.Delivered, (await ReadDelivery(good.Id)).Status);
    }

    [Fact]
    public async Task DispatchDueDeliveries_TargetNoLongerConfigured_FailsRatherThanSendingUnsigned()
    {
        // Signing with the wrong secret, or with none, would be worse than admitting the
        // notification cannot be delivered.
        SetupResponse(HttpStatusCode.OK);
        var delivery = await SeedDelivery(url: "https://example.com/removed");

        await CreateWorker().DispatchDueDeliveries(CancellationToken.None);

        Assert.Equal(WebhookDeliveryStatus.Failed, (await ReadDelivery(delivery.Id)).Status);
        _mockHandler.Protected().Verify(
            "SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task DispatchDueDeliveries_SignatureIsLowerCaseHexAndRecomputable()
    {
        // Lower-case hex is what the common verification helpers, and hmac hex digests generally,
        // produce.
        string? body = null;
        string? signature = null;
        string? timestamp = null;
        string? requestId = null;

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, cancellationToken) =>
            {
                body = await request.Content!.ReadAsStringAsync(cancellationToken);
                signature = request.Headers.GetValues("X-Signature").Single();
                timestamp = request.Headers.GetValues("X-Timestamp").Single();
                requestId = request.Headers.GetValues("X-Request-ID").Single();
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        await SeedDelivery();

        await CreateWorker().DispatchDueDeliveries(CancellationToken.None);

        Assert.NotNull(signature);
        Assert.StartsWith("sha256=", signature, StringComparison.Ordinal);

        var digest = signature["sha256=".Length..];
        Assert.DoesNotContain(digest, char.IsAsciiLetterUpper);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var expected = Convert.ToHexStringLower(
            hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{requestId}.{body}")));

        Assert.Equal(expected, digest, StringComparer.Ordinal);
    }

    [Fact]
    public async Task DispatchDueDeliveries_EachAttemptCarriesAFreshTimestamp()
    {
        // Signed at send time rather than at enqueue time: the timestamp is what protects the
        // subscriber against replay, and a retry hours later carrying the original timestamp
        // would be rejected by any subscriber that checks its freshness.
        var timestamps = new List<string>();

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                timestamps.Add(request.Headers.GetValues("X-Timestamp").Single());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            });

        var delivery = await SeedDelivery();
        var worker = CreateWorker();

        await worker.DispatchDueDeliveries(CancellationToken.None);

        using (var context = new DatabaseContext(_dbContextOptions))
        {
            var due = await context.WebhookDeliveries.SingleAsync(d => d.Id == delivery.Id);
            due.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            await context.SaveChangesAsync();
        }

        await worker.DispatchDueDeliveries(CancellationToken.None);

        Assert.Equal(2, timestamps.Count);
        Assert.All(timestamps, stamp => Assert.True(long.TryParse(stamp, out _)));
    }

    [Fact]
    public async Task DispatchDueDeliveries_SendsTheStoredPayloadVerbatim()
    {
        // Frozen at enqueue time so a retry describes the state that triggered the event rather
        // than whatever the payment looks like by the time the subscriber comes back.
        const string payload = "{\"eventId\":\"abc\",\"data\":{\"status\":\"Confirmed\"}}";
        string? sent = null;

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, cancellationToken) =>
            {
                sent = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        await SeedDelivery(payload);

        await CreateWorker().DispatchDueDeliveries(CancellationToken.None);

        Assert.Equal(payload, sent);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(10, 4)]
    public void BackoffFor_DoublesUpToTheCeiling(int attemptCount, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), _config.BackoffFor(attemptCount));
    }
}
